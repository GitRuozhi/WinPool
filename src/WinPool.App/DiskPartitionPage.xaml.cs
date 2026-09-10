using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Disk partition editor: topology on the left, two-row actions under it,
/// and a right-hand property card that follows the selection.
/// Simulation writes go through Agent one operation at a time.
/// </summary>
public sealed partial class DiskPartitionPage : EditorPageBase
{
    private const string NoneLetterValue = "";

    private string? _selectedDiskId;
    private string? _selectedPartitionId;
    private long? _selectedUnallocatedOffset;
    private long? _selectedUnallocatedSize;
    private TopologyEditInteraction _interaction = null!;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;
    private bool _filling;

    public DiskPartitionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            _selectedDiskId = ResolveOsDiskId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected, false);
        LocalizeChrome();
        RefreshAll();
    }

    private void LocalizeChrome()
    {
        OnlineButton.Content = Text("联机", "Online");
        OfflineButton.Content = Text("脱机", "Offline");
        InitializeButton.Content = ViewModel.Localization["InitializeDisk"];
        ConvertGptButton.Content = Text("转换为 GPT", "Convert to GPT");
        NewPartitionButton.Content = ViewModel.Localization["NewPartition"];
        DeletePartitionButton.Content = ViewModel.Localization["DeleteVolume"];
        ExtendButton.Content = ViewModel.Localization["ExtendVolume"];
        ShrinkButton.Content = ViewModel.Localization["ShrinkVolume"];
        OpenExplorerButton.Content = Text("打开资源管理器", "Open in File Explorer");
        DiskLocationLabel.Text = Text("所在磁盘", "Disk");
        PartitionNumberLabel.Text = Text("分区编号", "Partition number");
        DriveLetterLabel.Text = Text("盘符", "Drive letter");
        VolumeLabelCaption.Text = Text("卷标", "Volume label");
        SizeLabel.Text = Text("容量（GB）", "Size (GB)");
        FileSystemLabel.Text = Text("文件系统", "File system");
        ClusterLabel.Text = Text("分配单元", "Allocation unit");
        QuickFormatLabel.Text = Text("快速格式化", "Quick format");
        FormatButton.Content = ViewModel.Localization["Format"];
        FillFileSystemBox();
        FillClusterBox();
    }

    private void FillFileSystemBox()
    {
        FileSystemBox.Items.Clear();
        FileSystemBox.Items.Add("NTFS");
        FileSystemBox.Items.Add("ReFS");
        FileSystemBox.Items.Add("exFAT");
        FileSystemBox.SelectedIndex = 0;
    }

    private void FillClusterBox()
    {
        ClusterBox.Items.Clear();
        foreach (var size in new[] { "4K", "8K", "16K", "32K", "64K" })
        {
            ClusterBox.Items.Add(size);
        }

        ClusterBox.SelectedIndex = 4;
    }

    private string? ResolveOsDiskId(string? stableId)
    {
        if (stableId is null)
        {
            return null;
        }

        var snapshot = ViewModel.ActiveSnapshot;
        if (snapshot.OsDisks.Any(x => x.StableId == stableId))
        {
            return stableId;
        }

        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == stableId);
        return partition?.OsDiskStableId
            ?? snapshot.OsDisks.FirstOrDefault(x =>
                x.PhysicalDiskStableId == stableId || x.VirtualDiskStableId == stableId)?.StableId;
    }

    private void RefreshAll()
    {
        RefreshTopology();
        FillForm();
        UpdateButtonState();
    }

    private bool IsTopologyNodeSelected(TopologyNodeViewModel node)
    {
        if (_selectedPartitionId is not null)
        {
            return node.Unit.Kind == StorageUnitKind.Partition
                && node.Unit.StableId == _selectedPartitionId;
        }

        if (_selectedUnallocatedOffset is not null)
        {
            return EditWorkspace.IsUnallocated(node.Unit.StableId)
                && EditWorkspace.TryParseUnallocated(node.Unit.StableId, out var disk, out var offset, out _)
                && disk == _selectedDiskId
                && offset == _selectedUnallocatedOffset;
        }

        return node.Unit.Kind == StorageUnitKind.OsDisk
            && node.Unit.StableId == _selectedDiskId;
    }

    private void OnTopologySelected(TopologyNodeViewModel node)
    {
        if (node.Unit.Kind == StorageUnitKind.OsDisk)
        {
            _selectedDiskId = node.Unit.StableId;
            _selectedPartitionId = null;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
        }
        else if (EditWorkspace.IsUnallocated(node.Unit.StableId)
                 && EditWorkspace.TryParseUnallocated(node.Unit.StableId, out var disk, out var offset, out var size))
        {
            _selectedDiskId = disk;
            _selectedPartitionId = null;
            _selectedUnallocatedOffset = offset;
            _selectedUnallocatedSize = size;
        }
        else if (node.Unit.Kind == StorageUnitKind.Partition)
        {
            _selectedPartitionId = node.Unit.StableId;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
            var partition = _working.Partitions.FirstOrDefault(item => item.StableId == node.Unit.StableId);
            _selectedDiskId = partition?.OsDiskStableId ?? node.Unit.ParentStableId;
        }

        RefreshAll();
    }

    private void TopologyScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(MinTopologyWidth, e.NewSize.Width - TopologyWidthMargin);
        TopologyControl.Width = width;
        _viewportWidth = width;
        if (TopologyControl.ItemsSource is IReadOnlyList<TopologyNodeViewModel> roots
            && roots.Count > 0)
        {
            roots[0].SetSurfaceViewportWidth(width);
        }
    }

    private void RefreshTopology()
    {
        var root = EditWorkspace.ProjectPartitionWorkspaceRoot(_working, UnallocatedIgnoreBytes);
        var rootViewModel = new TopologyNodeViewModel(
            EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, EditWorkspace.PartitionRowStableId),
            ViewModel,
            _working,
            _interaction,
            isLayoutRoot: true);
        rootViewModel.SetSurfaceViewportWidth(_viewportWidth);
        TopologyControl.ItemsSource = new[] { rootViewModel };
    }

    private void FillForm()
    {
        _filling = true;
        try
        {
            var partition = SelectedPartition();
            var disk = SelectedDisk();
            var gap = _selectedUnallocatedOffset is not null;
            var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
            DiskLocationValue.Text = disk is null
                ? string.Empty
                : Text($"磁盘 {disk.Number}  {disk.FriendlyName}", $"Disk {disk.Number}  {disk.FriendlyName}");
            PartitionNumberValue.Text = partition is not null
                ? partition.PartitionNumber.ToString()
                : gap
                    ? Text("未分配", "Unallocated")
                    : "—";
            FillDriveLetters(partition, volume);
            VolumeLabelBox.Text = volume?.FileSystemLabel
                ?? (partition is null ? string.Empty : partition.FileSystemLabel);
            if (gap)
            {
                var gb = Math.Round((_selectedUnallocatedSize ?? 0) / 1024d / 1024d / 1024d, 2);
                SizeBox.Value = gb;
                SizeBox.Maximum = gb;
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                QuickFormatSwitch.IsOn = true;
            }
            else if (partition is not null)
            {
                SizeBox.Maximum = 1_000_000;
                SizeBox.Value = Math.Round(partition.Size / 1024d / 1024d / 1024d, 2);
                SelectFileSystem(volume?.FileSystem ?? partition.FileSystem);
                SelectCluster(volume?.AllocationUnitSize ?? partition.AllocationUnitSize);
                QuickFormatSwitch.IsOn = true;
            }
            else
            {
                SizeBox.Value = 0;
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                QuickFormatSwitch.IsOn = true;
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private void FillDriveLetters(PartitionInfo? partition, VolumeInfo? volume)
    {
        DriveLetterBox.Items.Clear();
        DriveLetterBox.Items.Add(Text("无", "None"));
        var current = volume?.DriveLetter ?? (partition is null ? string.Empty : _working.DriveLetterOf(partition));
        var used = UsedDriveLetters(exceptPartitionId: partition?.StableId);
        for (var letter = 'D'; letter <= 'Z'; letter++)
        {
            var token = letter.ToString();
            if (used.Contains(token) && !token.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DriveLetterBox.Items.Add(token);
        }

        if (current.Length == 1
            && current[0] is >= 'A' and <= 'C'
            && !DriveLetterBox.Items.Cast<string>().Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            DriveLetterBox.Items.Insert(1, current);
        }

        DriveLetterBox.SelectedItem = current.Length == 1
            ? DriveLetterBox.Items.Cast<string>().FirstOrDefault(item =>
                item.Equals(current, StringComparison.OrdinalIgnoreCase))
            : DriveLetterBox.Items[0];
    }

    private HashSet<string> UsedDriveLetters(string? exceptPartitionId)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in _working.Volumes)
        {
            if (exceptPartitionId is not null
                && string.Equals(volume.PartitionStableId, exceptPartitionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (volume.DriveLetter.Length == 1)
            {
                used.Add(volume.DriveLetter);
            }
        }

        foreach (var disk in _working.NetworkDisks)
        {
            var letter = TopologyProjector.NormalizeDriveLetter(disk.DriveLetter);
            if (letter.Length == 1)
            {
                used.Add(letter);
            }
        }

        return used;
    }

    private void SelectFileSystem(string? fileSystem)
    {
        var normalized = (fileSystem ?? string.Empty).Trim();
        if (normalized.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            FileSystemBox.SelectedIndex = 0;
        }
        else if (normalized.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("REFS", StringComparison.OrdinalIgnoreCase))
        {
            FileSystemBox.SelectedIndex = 1;
        }
        else if (normalized.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            FileSystemBox.SelectedIndex = 2;
        }
        else
        {
            FileSystemBox.SelectedIndex = 0;
        }
    }

    private void SelectCluster(long? bytes)
    {
        ClusterBox.SelectedIndex = bytes switch
        {
            4096 => 0,
            8192 => 1,
            16384 => 2,
            32768 => 3,
            _ => 4
        };
    }

    private PartitionInfo? SelectedPartition() =>
        _working.Partitions.FirstOrDefault(item => item.StableId == _selectedPartitionId);

    private OsDiskInfo? SelectedDisk() =>
        _working.OsDisks.FirstOrDefault(item => item.StableId == _selectedDiskId);

    private bool IsUserPartition(PartitionInfo? partition) =>
        partition is { Type: "Primary", IsBoot: false, IsSystem: false }
        && partition.Type is not "EfiSystem" and not "MicrosoftReserved" and not "WindowsRecovery";

    private bool IsProtected(PartitionInfo partition) =>
        partition.IsBoot
        || partition.IsSystem
        || partition.Type is "EfiSystem" or "MicrosoftReserved" or "WindowsRecovery";

    private string SelectedFileSystemToken() =>
        FileSystemBox.SelectedItem as string ?? "NTFS";

    private bool IsInitialized(OsDiskInfo? disk) =>
        disk is not null && EditWorkspace.IsPartitionTableInitialized(disk);

    private long SelectedClusterBytes() =>
        ParseSize(ClusterBox.SelectedItem as string ?? "64K");

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var partition = SelectedPartition();
        var disk = SelectedDisk();
        var isPartitionSelection = partition is not null;
        var isGapSelection = _selectedUnallocatedOffset is not null;
        var isDiskSelection = disk is not null && !isPartitionSelection && !isGapSelection;
        var userPartition = IsUserPartition(partition);
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        var hasVolume = volume is not null;
        var initialized = IsInitialized(disk);
        var alreadyGpt = disk is not null
            && string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase);
        var hasPartitions = disk is not null
            && _working.Partitions.Any(item => item.OsDiskStableId == disk.StableId);
        var letter = volume?.DriveLetter ?? (partition is null ? string.Empty : _working.DriveLetterOf(partition));
        var explorerPath = letter.Length == 1 ? $"{letter}:\\" : string.Empty;
        var diskOffline = disk?.IsOffline == true;
        var createMode = isGapSelection && initialized;

        OnlineButton.IsEnabled = simulated && isDiskSelection && disk is { IsOffline: true };
        OfflineButton.IsEnabled = simulated
            && isDiskSelection
            && disk is { IsOffline: false, IsBoot: false, IsSystem: false };
        InitializeButton.IsEnabled = simulated && isDiskSelection && disk is { IsBoot: false, IsSystem: false } && !initialized;
        ConvertGptButton.IsEnabled = simulated && isDiskSelection && !alreadyGpt && !hasPartitions;
        NewPartitionButton.IsEnabled = simulated
            && disk is { IsOffline: false }
            && (createMode || (isDiskSelection && !initialized));
        DeletePartitionButton.IsEnabled = simulated && isPartitionSelection;
        ExtendButton.IsEnabled = simulated && userPartition;
        ShrinkButton.IsEnabled = simulated && userPartition;
        OpenExplorerButton.IsEnabled = !simulated
            && isPartitionSelection
            && !diskOffline
            && letter.Length == 1
            && Directory.Exists(explorerPath);

        var propertyEnabled = simulated && (isPartitionSelection || isGapSelection);
        DriveLetterBox.IsEnabled = simulated && (hasVolume || createMode);
        VolumeLabelBox.IsEnabled = simulated && (hasVolume || createMode);
        SizeBox.IsEnabled = simulated && isGapSelection;
        FileSystemBox.IsEnabled = propertyEnabled;
        ClusterBox.IsEnabled = propertyEnabled;
        QuickFormatSwitch.IsEnabled = propertyEnabled;
        FormatButton.Content = createMode
            ? ViewModel.Localization["NewPartition"]
            : ViewModel.Localization["Format"];
        FormatButton.IsEnabled = simulated && (createMode || userPartition);
    }

    private void FileSystemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        UpdateButtonState();
    }

    private async void DriveLetterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        if (partition is null || volume is null)
        {
            return;
        }

        var selected = DriveLetterBox.SelectedItem as string ?? string.Empty;
        var next = selected == Text("无", "None") ? NoneLetterValue : selected;
        if (string.Equals(next, volume.DriveLetter, StringComparison.OrdinalIgnoreCase)
            || (next.Length == 0 && volume.DriveLetter.Length == 0))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.ChangeDriveLetter,
                partition.StableId,
                DriveLetter: next),
            Text("盘符已更新", "Drive letter updated"),
            next.Length == 0
                ? Text("已清除盘符。", "The drive letter was cleared.")
                : Text($"盘符已设为 {next}:。", $"The drive letter is now {next}:."));
    }

    private async void VolumeLabelBox_LostFocus(object sender, RoutedEventArgs e) =>
        await CommitVolumeLabelAsync();

    private async void VolumeLabelBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await CommitVolumeLabelAsync();
    }

    private async Task CommitVolumeLabelAsync()
    {
        if (_filling || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        if (partition is null || volume is null)
        {
            return;
        }

        var next = VolumeLabelBox.Text ?? string.Empty;
        if (string.Equals(next, volume.FileSystemLabel, StringComparison.Ordinal))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.Rename,
                partition.StableId,
                Name: next),
            Text("卷标已更新", "Volume label updated"),
            Text("卷标已写入模拟文档。", "The volume label was saved to the simulation."));
    }

    private async void Online_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(SimulationOperationKind.SetDiskOffline, disk.StableId, Offline: false),
            Text("已联机", "Disk online"),
            Text("磁盘已联机。", "The disk is online."));
    }

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        var physical = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.PhysicalDiskStableId);
        if (physical?.IsPageFile == true
            && !await ConfirmAsync(
                Text("移除页面文件", "Remove page file"),
                Text("脱机前将移除该磁盘上的页面文件。确定继续？", "The page file on this disk will be removed before it goes offline. Continue?")))
        {
            return;
        }

        if (physical?.IsCrashDump == true
            && !await ConfirmAsync(
                Text("移除崩溃转储", "Remove crash dump"),
                Text("脱机前将移除该磁盘上的崩溃转储。确定继续？", "The crash dump on this disk will be removed before it goes offline. Continue?")))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(SimulationOperationKind.SetDiskOffline, disk.StableId, Offline: true),
            Text("已脱机", "Disk offline"),
            Text("磁盘已脱机。", "The disk is offline."));
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        if (!await ConfirmAsync(
                ViewModel.Localization["InitializeDisk"],
                Text("将该磁盘初始化为 GPT？", "Initialize this disk as GPT?")))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.InitializeDisk,
                disk.StableId,
                Name: "GPT",
                CreateMsr: ViewModel.CurrentPreferences.CreateMsrOnInitialize),
            Text("初始化成功", "Initialization succeeded"),
            Text("磁盘已初始化为 GPT。", "The disk was initialized as GPT."));
    }

    private async void ConvertGpt_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        if (!await ConfirmAsync(
                Text("转换为 GPT", "Convert to GPT"),
                Text("将空磁盘转换为 GPT？不能转换为 MBR。", "Convert this empty disk to GPT? Conversion to MBR is not offered.")))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(SimulationOperationKind.ConvertDisk, disk.StableId, Name: "GPT"),
            Text("转换成功", "Conversion succeeded"),
            Text("磁盘已转换为 GPT。", "The disk was converted to GPT."));
    }

    private async void NewPartition_Click(object sender, RoutedEventArgs e) =>
        await CreatePartitionAsync();

    private async Task CreatePartitionAsync()
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        if (!IsInitialized(disk))
        {
            await ShowMessageAsync(
                ViewModel.Localization["NewPartition"],
                Text("请先初始化磁盘。未初始化的磁盘不能创建分区。",
                    "Initialize the disk first. An uninitialized disk cannot hold partitions."));
            return;
        }

        if (_selectedUnallocatedOffset is null)
        {
            return;
        }

        var fileSystem = SelectedFileSystemToken();
        if (fileSystem == "ReFS" && !await ConfirmRefsAsync())
        {
            return;
        }

        long? bytes = null;
        if (!double.IsNaN(SizeBox.Value) && SizeBox.Value > 0)
        {
            bytes = (long)(SizeBox.Value * 1024d * 1024d * 1024d);
        }

        var letter = DriveLetterBox.SelectedItem as string;
        if (letter == Text("无", "None"))
        {
            letter = NoneLetterValue;
        }

        var diskId = _selectedDiskId;
        var offset = _selectedUnallocatedOffset;
        if (!await SubmitAsync(
                new SimulationOperationRequest(
                    SimulationOperationKind.CreatePartition,
                    diskId!,
                    Name: VolumeLabelBox.Text,
                    DriveLetter: letter,
                    FileSystem: fileSystem,
                    AllocationUnitSize: SelectedClusterBytes(),
                    SizeBytes: bytes ?? _selectedUnallocatedSize,
                    OffsetBytes: offset),
                Text("新建分区成功", "Partition created"),
                Text("已在空隙中创建分区并格式化。", "A partition was created in the gap and formatted.")))
        {
            return;
        }

        var created = _working.Partitions.FirstOrDefault(item =>
            item.OsDiskStableId == diskId && item.Offset == offset);
        if (created is not null)
        {
            _selectedPartitionId = created.StableId;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
            RefreshAll();
        }
    }

    private async void DeletePartition_Click(object sender, RoutedEventArgs e)
    {
        var partition = SelectedPartition();
        if (partition is null)
        {
            return;
        }

        if (!await ConfirmAsync(
                Text("删除分区", "Delete partition"),
                Text("确定从模拟系统中删除这个分区？分区上的数据将不可用。",
                    "Remove this partition from the simulation? Data on it will no longer be available.")))
        {
            return;
        }

        var id = partition.StableId;
        if (await SubmitAsync(
                new SimulationOperationRequest(SimulationOperationKind.DeletePartition, id),
                Text("删除成功", "Partition deleted"),
                Text("分区已从模拟文档中删除。", "The partition was removed from the simulation.")))
        {
            _selectedPartitionId = null;
            RefreshAll();
        }
    }

    private async void Extend_Click(object sender, RoutedEventArgs e) => await ShowResizeUnsupportedAsync();

    private async void Shrink_Click(object sender, RoutedEventArgs e) => await ShowResizeUnsupportedAsync();

    private async Task ShowResizeUnsupportedAsync() =>
        await ShowMessageAsync(
            Text("尚不支持", "Not supported yet"),
            Text(
                "扩展和收缩需要 Windows supported-size 依据，本阶段不实现，不会改变分区大小。",
                "Extend and shrink need a Windows supported-size result. They are not implemented in this stage and do not change partition size."));

    private async void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var partition = SelectedPartition();
        if (partition is null)
        {
            return;
        }

        var letter = _working.DriveLetterOf(partition);
        var path = letter.Length == 1 ? $"{letter}:\\" : string.Empty;
        if (path.Length == 0 || !Directory.Exists(path))
        {
            await ShowMessageAsync(
                Text("无法打开", "Cannot open"),
                Text("当前卷没有可打开的本机路径。", "The selected volume has no local path that can be opened."));
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUnallocatedOffset is not null)
        {
            await CreatePartitionAsync();
            return;
        }

        var partition = SelectedPartition();
        if (partition is null || IsProtected(partition))
        {
            return;
        }

        var fileSystem = SelectedFileSystemToken();

        var current = _working.FileSystemOf(partition);
        if (!string.IsNullOrWhiteSpace(current)
            && !await ConfirmAsync(
                ViewModel.Localization["Format"],
                Text("格式化将清除该分区上的数据。确定继续？", "Formatting clears data on this partition. Continue?")))
        {
            return;
        }

        if (fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase) && !await ConfirmRefsAsync())
        {
            return;
        }

        var quick = QuickFormatSwitch.IsOn;
        var ok = await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.FormatPartition,
                partition.StableId,
                Name: VolumeLabelBox.Text,
                FileSystem: fileSystem,
                AllocationUnitSize: SelectedClusterBytes()),
            quick ? Text("快速格式化成功", "Quick format succeeded") : Text("格式化成功", "Format succeeded"),
            quick
                ? Text("模拟快速格式化已写入文档。", "The simulated quick format was saved.")
                : Text("模拟格式化已写入文档。", "The simulated format was saved."),
            failTitle: Text("格式化失败", "Format failed"));
        _ = ok;
    }

    private async Task<bool> ConfirmRefsAsync() =>
        await ConfirmAsync(
            Text("ReFS 提示", "ReFS notice"),
            Text(
                "ReFS 没有与 NTFS 64K 同等的长期测试证据。确定继续？",
                "ReFS has no long-run evidence equivalent to NTFS 64K. Continue anyway?"));

    private async Task<bool> SubmitAsync(
        SimulationOperationRequest request,
        string successTitle,
        string successMessage,
        string? failTitle = null)
    {
        WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt> result;
        try
        {
            result = await ViewModel.ApplySimulationOperationAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            await ShowMessageAsync(failTitle ?? Text("操作失败", "Operation failed"), exception.Message);
            return false;
        }

        if (result.Status == WinPool.Application.ApplicationStatus.OutcomeUnknown)
        {
            await ShowMessageAsync(
                Text("提交结果未知", "Commit outcome unknown"),
                result.Messages.FirstOrDefault()?.UserTextKey
                    ?? Text(
                        "请求已发送，但结果未知。请重新加载后再决定是否重试。",
                        "The request was sent and the outcome is unknown. Reload before retrying."));
            _working = ViewModel.ActiveSnapshot;
            RefreshAll();
            return false;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            await ShowMessageAsync(
                failTitle ?? Text("操作失败", "Operation failed"),
                result.Messages.FirstOrDefault()?.UserTextKey
                    ?? Text("模拟操作未完成。", "The simulation operation did not complete."));
            return false;
        }

        _working = ViewModel.ActiveSnapshot;
        await ShowMessageAsync(successTitle, successMessage);
        RefreshAll();
        return true;
    }
}
