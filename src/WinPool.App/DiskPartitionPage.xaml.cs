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
    private bool _renameInProgress;

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
            var targetPartition = ViewModel.EffectiveActiveSnapshot.Partitions.FirstOrDefault(item =>
                item.StableId.Equals(parameter.TargetStableId, StringComparison.OrdinalIgnoreCase));
            _selectedPartitionId = targetPartition?.StableId;
            _selectedDiskId = targetPartition?.OsDiskStableId ?? ResolveOsDiskId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.EffectiveActiveSnapshot;
        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected, false);
        LocalizeChrome();
        RefreshAll();
    }

    private void LocalizeChrome()
    {
        OnlineButtonLabel.Text = Text("联机", "Online");
        OfflineButtonLabel.Text = Text("脱机", "Offline");
        InitializeButtonLabel.Text = ViewModel.Localization["InitializeDisk"];
        ConvertGptButtonLabel.Text = Text("转换为 GPT", "Convert to GPT");
        NewPartitionButtonLabel.Text = ViewModel.Localization["NewPartition"];
        DeletePartitionButtonLabel.Text = Text("删除分区", "Delete partition");
        ExtendButtonLabel.Text = Text("扩展分区", "Extend partition");
        ShrinkButtonLabel.Text = Text("压缩分区", "Shrink partition");
        OpenExplorerButtonLabel.Text = Text("打开资源管理器", "Open in File Explorer");
        DiskLocationLabel.Text = Text("所在磁盘", "Disk");
        PartitionNumberLabel.Text = Text("分区编号", "Partition number");
        StartOffsetLabel.Text = Text("起始位置", "Start");
        EndOffsetLabel.Text = Text("结束位置", "End");
        PartitionTypeLabel.Text = Text("分区类型", "Partition type");
        DriveLetterLabel.Text = Text("盘符", "Drive letter");
        VolumeLabelCaption.Text = Text("卷标", "Volume label");
        SizeLabel.Text = Text("容量（GiB）", "Size (GiB)");
        FileSystemLabel.Text = Text("文件系统", "File system");
        ClusterLabel.Text = Text("分配单元", "Allocation unit");
        QuickFormatLabel.Text = Text("快速格式化", "Quick format");
        FormatButtonLabel.Text = ViewModel.Localization["Format"];
        foreach (var button in PropertyResetButtons())
        {
            ToolTipService.SetToolTip(button, ViewModel.Localization["ResetRecommended"]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                ViewModel.Localization["ResetRecommended"]);
        }
        FillFileSystemBox();
        FillClusterBox();
        FillPartitionTypeBox();
    }

    private IEnumerable<Button> PropertyResetButtons()
    {
        yield return ResetSizeButton;
        yield return ResetFileSystemButton;
        yield return ResetClusterButton;
        yield return ResetQuickFormatButton;
    }

    private void FillFileSystemBox()
    {
        FileSystemBox.Items.Clear();
        FileSystemBox.Items.Add("NTFS");
        FileSystemBox.Items.Add("ReFS");
        FileSystemBox.Items.Add("exFAT");
        FileSystemBox.SelectedIndex = 0;
    }

    private void FillPartitionTypeBox()
    {
        PartitionTypeBox.Items.Clear();
        PartitionTypeBox.Items.Add(Text("基本数据分区", "Basic data partition"));
        PartitionTypeBox.Items.Add(Text("EFI 系统分区", "EFI system partition"));
        PartitionTypeBox.Items.Add(Text("Microsoft 保留分区（MSR）", "Microsoft Reserved Partition (MSR)"));
        PartitionTypeBox.Items.Add(Text("Windows 恢复分区", "Windows recovery partition"));
        PartitionTypeBox.SelectedIndex = 0;
    }

    private PartitionKind SelectedPartitionKind() => PartitionTypeBox.SelectedIndex switch
    {
        1 => PartitionKind.EfiSystem,
        2 => PartitionKind.MicrosoftReserved,
        3 => PartitionKind.WindowsRecovery,
        _ => PartitionKind.BasicData
    };

    private void FillClusterBox()
    {
        ClusterBox.Items.Clear();
        foreach (var size in new[] { "4 KiB", "8 KiB", "16 KiB", "32 KiB", "64 KiB" })
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

        var snapshot = ViewModel.EffectiveActiveSnapshot;
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
            var start = partition?.Offset ?? (gap ? _selectedUnallocatedOffset : null);
            var length = partition?.Size ?? (gap ? _selectedUnallocatedSize : null);
            if (start is long startBytes && length is long sizeBytes)
            {
                StartOffsetValue.Text = TopologyProjector.FormatBytes(startBytes);
                EndOffsetValue.Text = TopologyProjector.FormatBytes(startBytes + sizeBytes);
            }
            else if (disk is not null && !gap && partition is null)
            {
                StartOffsetValue.Text = TopologyProjector.FormatBytes(0);
                EndOffsetValue.Text = TopologyProjector.FormatBytes(disk.Size);
            }
            else
            {
                StartOffsetValue.Text = "—";
                EndOffsetValue.Text = "—";
            }

            FillFileSystemChoices(partition);
            FillDriveLetters(partition, volume, autoAssign: gap);
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
                PartitionTypeBox.SelectedIndex = 0;
            }
            else if (partition is not null)
            {
                SizeBox.Maximum = 1_000_000;
                SizeBox.Value = Math.Round(partition.Size / 1024d / 1024d / 1024d, 2);
                SelectFileSystem(volume?.FileSystem ?? partition.FileSystem);
                SelectCluster(volume?.AllocationUnitSize ?? partition.AllocationUnitSize);
                QuickFormatSwitch.IsOn = true;
                PartitionTypeBox.SelectedIndex = partition.Type switch
                {
                    "EfiSystem" => 1,
                    "MicrosoftReserved" => 2,
                    "WindowsRecovery" => 3,
                    _ => 0
                };
            }
            else
            {
                SizeBox.Value = 0;
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                QuickFormatSwitch.IsOn = true;
                PartitionTypeBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private void FillDriveLetters(PartitionInfo? partition, VolumeInfo? volume, bool autoAssign)
    {
        DriveLetterBox.Items.Clear();
        var none = Text("无", "None");
        DriveLetterBox.Items.Add(none);
        var current = volume?.DriveLetter ?? (partition is null ? string.Empty : _working.DriveLetterOf(partition));
        var used = UsedDriveLetters(exceptPartitionId: partition?.StableId);
        var nextFree = NextFreeDriveLetter(used);
        if (autoAssign && current.Length != 1)
        {
            current = nextFree;
        }

        for (var letter = 'C'; letter <= 'Z'; letter++)
        {
            var token = letter.ToString();
            if (used.Contains(token) && !token.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DriveLetterBox.Items.Add(token);
        }

        if (current.Length == 1
            && current[0] is >= 'A' and <= 'B'
            && !DriveLetterBox.Items.Cast<string>().Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            DriveLetterBox.Items.Insert(1, current);
        }

        DriveLetterBox.SelectedItem = current.Length == 1
            ? DriveLetterBox.Items.Cast<string>().FirstOrDefault(item =>
                item.Equals(current, StringComparison.OrdinalIgnoreCase))
            : none;
    }

    private void FillFileSystemChoices(PartitionInfo? partition)
    {
        switch (partition?.Type)
        {
            case "EfiSystem":
                FillFileSystemBoxFor("FAT32");
                break;
            case "MicrosoftReserved":
                FillFileSystemBoxFor(string.Empty);
                break;
            case "WindowsRecovery":
                FillFileSystemBoxFor("NTFS");
                break;
            default:
                FillFileSystemBox();
                break;
        }
    }

    private static string NextFreeDriveLetter(IReadOnlySet<string> used)
    {
        foreach (var candidate in "CDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(candidate.ToString()))
            {
                return candidate.ToString();
            }
        }

        return string.Empty;
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
        partition is { Type: "Primary" or "BasicData", IsBoot: false, IsSystem: false }
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
        ParseSize(ClusterBox.SelectedItem as string ?? "64 KiB");

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
        var alreadyGpt = disk is not null
            && string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase);
        var raw = disk?.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase) == true;
        var mbr = disk?.PartitionStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase) == true;
        var hasGap = disk is not null && EditWorkspace.UnallocatedGaps(
            disk,
            _working.Partitions.Where(item => item.OsDiskStableId == disk.StableId).ToArray()).Any(item => item.Size > 0);
        var letter = volume?.DriveLetter ?? (partition is null ? string.Empty : _working.DriveLetterOf(partition));
        var explorerPath = letter.Length == 1 ? $"{letter}:\\" : string.Empty;
        var diskOffline = disk?.IsOffline == true;
        var createMode = isGapSelection && alreadyGpt;

        OnlineButton.IsEnabled = simulated && isDiskSelection && disk is { IsOffline: true };
        OfflineButton.IsEnabled = simulated
            && isDiskSelection
            && disk is { IsOffline: false, IsBoot: false, IsSystem: false };
        InitializeButton.IsEnabled = simulated && isDiskSelection && !diskOffline
            && disk is { IsBoot: false, IsSystem: false } && raw;
        ConvertGptButton.IsEnabled = simulated && isDiskSelection && !diskOffline
            && disk is { IsBoot: false, IsSystem: false } && mbr;
        NewPartitionButton.IsEnabled = simulated && !diskOffline && alreadyGpt && hasGap
            && (isDiskSelection || isGapSelection);
        DeletePartitionButton.IsEnabled = simulated && !diskOffline && userPartition;
        ExtendButton.IsEnabled = false;
        ShrinkButton.IsEnabled = false;
        OpenExplorerButton.IsEnabled = !simulated
            && isPartitionSelection
            && !diskOffline
            && letter.Length == 1
            && Directory.Exists(explorerPath);

        var propertyEnabled = simulated && !diskOffline && (isPartitionSelection || isGapSelection);
        var kind = SelectedPartitionKind();
        PartitionTypeBox.IsEnabled = propertyEnabled && createMode;
        DriveLetterBox.IsEnabled = propertyEnabled && (hasVolume || createMode);
        VolumeLabelBox.IsEnabled = propertyEnabled && (hasVolume || createMode);
        SizeBox.IsEnabled = propertyEnabled && isGapSelection;
        FileSystemBox.IsEnabled = propertyEnabled && kind != PartitionKind.MicrosoftReserved;
        ClusterBox.IsEnabled = propertyEnabled && kind != PartitionKind.MicrosoftReserved;
        QuickFormatSwitch.IsEnabled = propertyEnabled && kind != PartitionKind.MicrosoftReserved;
        if (createMode && kind is PartitionKind.EfiSystem or PartitionKind.MicrosoftReserved or PartitionKind.WindowsRecovery)
        {
            DriveLetterBox.IsEnabled = false;
        }
        if (createMode && kind == PartitionKind.MicrosoftReserved)
        {
            VolumeLabelBox.IsEnabled = false;
        }
        FormatButtonLabel.Text = createMode
            ? kind == PartitionKind.MicrosoftReserved
                ? Text("新建分区", "Create partition")
                : Text("新建分区并格式化", "Create partition and format")
            : ViewModel.Localization["Format"];
        FormatButtonIcon.Glyph = createMode ? "\uE710" : "\uE9CE";
        FormatButton.IsEnabled = propertyEnabled && (createMode || userPartition);
        UpdatePropertyResetState();
    }

    private void FileSystemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        UpdateButtonState();
    }

    private void PartitionTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _selectedUnallocatedOffset is null)
        {
            return;
        }

        _filling = true;
        try
        {
            var gapGiB = (_selectedUnallocatedSize ?? 0) / 1024d / 1024d / 1024d;
            switch (SelectedPartitionKind())
            {
                case PartitionKind.EfiSystem:
                    FillFileSystemBoxFor("FAT32");
                    ClusterBox.SelectedIndex = 0;
                    SizeBox.Value = Math.Min(gapGiB, 300d / 1024d);
                    DriveLetterBox.SelectedIndex = 0;
                    break;
                case PartitionKind.MicrosoftReserved:
                    FillFileSystemBoxFor(string.Empty);
                    SizeBox.Value = Math.Min(gapGiB, 16d / 1024d);
                    DriveLetterBox.SelectedIndex = 0;
                    VolumeLabelBox.Text = string.Empty;
                    QuickFormatSwitch.IsOn = false;
                    break;
                case PartitionKind.WindowsRecovery:
                    FillFileSystemBoxFor("NTFS");
                    ClusterBox.SelectedIndex = 0;
                    SizeBox.Value = Math.Min(gapGiB, 990d / 1024d);
                    DriveLetterBox.SelectedIndex = 0;
                    QuickFormatSwitch.IsOn = true;
                    break;
                default:
                    FillFileSystemBox();
                    ClusterBox.SelectedIndex = 4;
                    SizeBox.Value = gapGiB;
                    FillDriveLetters(null, null, autoAssign: true);
                    QuickFormatSwitch.IsOn = true;
                    break;
            }
        }
        finally
        {
            _filling = false;
        }
        UpdateButtonState();
    }

    private void FillFileSystemBoxFor(string fileSystem)
    {
        FileSystemBox.Items.Clear();
        FileSystemBox.Items.Add(fileSystem);
        FileSystemBox.SelectedIndex = 0;
    }

    private void SizeBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_filling)
        {
            UpdatePropertyResetState();
        }
    }

    private void ClusterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            UpdatePropertyResetState();
        }
    }

    private void QuickFormatSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_filling)
        {
            UpdatePropertyResetState();
        }
    }

    private void UpdatePropertyResetState()
    {
        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        var gap = _selectedUnallocatedOffset is not null;
        var parameterEnabled = ViewModel.IsUsingSimulatedInventory && (partition is not null || gap);
        var recommendedSize = Math.Round(RecommendedCreateSizeGiB(), 2);
        var baselineFileSystem = volume?.FileSystem ?? partition?.FileSystem;
        if (string.IsNullOrWhiteSpace(baselineFileSystem))
        {
            baselineFileSystem = SelectedPartitionKind() switch
            {
                PartitionKind.EfiSystem => "FAT32",
                PartitionKind.MicrosoftReserved => string.Empty,
                _ => "NTFS"
            };
        }

        var baselineCluster = volume?.AllocationUnitSize ?? partition?.AllocationUnitSize;
        var sizeChanged = gap
            && SizeBox.IsEnabled
            && (double.IsNaN(SizeBox.Value) || Math.Abs(SizeBox.Value - recommendedSize) > 0.005);
        var fileSystemChanged = parameterEnabled
            && FileSystemBox.IsEnabled
            && !string.Equals(
                SelectedFileSystemToken(),
                baselineFileSystem,
                StringComparison.OrdinalIgnoreCase);
        var clusterChanged = parameterEnabled
            && ClusterBox.IsEnabled
            && SelectedClusterBytes() != (baselineCluster ??
                (SelectedPartitionKind() == PartitionKind.BasicData ? 65536 : 4096));
        var quickFormatChanged = parameterEnabled
            && QuickFormatSwitch.IsEnabled
            && !QuickFormatSwitch.IsOn;

        SetPropertyResetState(ResetSizeButton, SizeChangedIndicator, sizeChanged);
        SetPropertyResetState(ResetFileSystemButton, FileSystemChangedIndicator, fileSystemChanged);
        SetPropertyResetState(ResetClusterButton, ClusterChangedIndicator, clusterChanged);
        SetPropertyResetState(ResetQuickFormatButton, QuickFormatChangedIndicator, quickFormatChanged);
    }

    private static void SetPropertyResetState(
        Button button,
        FrameworkElement indicator,
        bool changed)
    {
        var visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        button.Visibility = visibility;
        indicator.Visibility = visibility;
    }

    private void ResetSize_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUnallocatedOffset is not null)
        {
            SizeBox.Value = Math.Round(RecommendedCreateSizeGiB(), 2);
        }

        UpdatePropertyResetState();
    }

    private void ResetFileSystem_Click(object sender, RoutedEventArgs e)
    {
        FileSystemBox.SelectedItem = SelectedPartitionKind() switch
        {
            PartitionKind.EfiSystem => "FAT32",
            PartitionKind.MicrosoftReserved => string.Empty,
            _ => "NTFS"
        };
        UpdatePropertyResetState();
    }

    private void ResetCluster_Click(object sender, RoutedEventArgs e)
    {
        ClusterBox.SelectedItem = SelectedPartitionKind() == PartitionKind.BasicData
            ? "64 KiB"
            : "4 KiB";
        UpdatePropertyResetState();
    }

    private void ResetQuickFormat_Click(object sender, RoutedEventArgs e)
    {
        QuickFormatSwitch.IsOn = SelectedPartitionKind() != PartitionKind.MicrosoftReserved;
        UpdatePropertyResetState();
    }

    private double RecommendedCreateSizeGiB()
    {
        var gapGiB = (_selectedUnallocatedSize ?? 0) / 1024d / 1024d / 1024d;
        return SelectedPartitionKind() switch
        {
            PartitionKind.EfiSystem => Math.Min(gapGiB, 300d / 1024d),
            PartitionKind.MicrosoftReserved => Math.Min(gapGiB, 16d / 1024d),
            PartitionKind.WindowsRecovery => Math.Min(gapGiB, 990d / 1024d),
            _ => gapGiB
        };
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
        if (_filling || _renameInProgress || !ViewModel.IsUsingSimulatedInventory)
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

        _renameInProgress = true;
        try
        {
            await SubmitAsync(
                new SimulationOperationRequest(
                    SimulationOperationKind.Rename,
                    volume.StableId,
                    Name: next),
                Text("卷标已更新", "Volume label updated"),
                Text("卷标已写入模拟文档。", "The volume label was saved to the simulation."));
        }
        finally
        {
            _renameInProgress = false;
        }
    }

    private async void Online_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskOffline,
                disk.StableId,
                Offline: false),
            Text("已联机", "Disk online"),
            Text("磁盘联机状态已写入模拟文档。", "The disk online state was saved to the simulation."));
    }

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskOffline,
                disk.StableId,
                Offline: true),
            Text("已脱机", "Disk offline"),
            Text("磁盘脱机状态已写入模拟文档，所有修改入口已锁定。", "The disk offline state was saved to the simulation and all edit actions are locked."));
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        if (DiskHoldsStoredData(disk)
            && !await ConfirmAsync(
                Text("初始化含数据的磁盘", "Initialize disk with data"),
                Text("初始化将清除该磁盘上的分区和已用数据。确定继续？",
                    "Initialization clears the partitions and used data on this disk. Continue?")))
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

        if (DiskHoldsStoredData(disk)
            && !await ConfirmAsync(
                Text("转换含数据的磁盘", "Convert disk with data"),
                Text("转换为 GPT 将清除该 MBR 磁盘上的分区和已用数据。确定继续？",
                    "Converting to GPT clears the partitions and used data on this MBR disk. Continue?")))
        {
            return;
        }

        await SubmitAsync(
            new SimulationOperationRequest(SimulationOperationKind.ConvertDisk, disk.StableId, Name: "GPT"),
            Text("转换成功", "Conversion succeeded"),
            Text("磁盘已转换为 GPT。", "The disk was converted to GPT."));
    }

    private void NewPartition_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        var largest = EditWorkspace.UnallocatedGaps(
                disk,
                _working.Partitions.Where(item => item.OsDiskStableId == disk.StableId).ToArray())
            .Where(item => item.Size > 0)
            .OrderByDescending(item => item.Size)
            .ThenBy(item => item.Offset)
            .FirstOrDefault();
        if (largest.Size <= 0)
        {
            return;
        }

        _selectedPartitionId = null;
        _selectedUnallocatedOffset = largest.Offset;
        _selectedUnallocatedSize = largest.Size;
        RefreshAll();
    }

    private bool DiskHoldsStoredData(OsDiskInfo disk) =>
        _working.Partitions.Any(item =>
            item.OsDiskStableId == disk.StableId
            && EditWorkspace.PartitionHoldsStoredData(item));

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

        var partitionKind = SelectedPartitionKind();
        var fileSystem = partitionKind == PartitionKind.MicrosoftReserved
            ? string.Empty
            : SelectedFileSystemToken();
        if (fileSystem == "ReFS")
        {
            PublishRefsNotice();
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
                    OffsetBytes: offset,
                    PartitionKind: partitionKind),
                Text("新建分区成功", "Partition created"),
                partitionKind == PartitionKind.MicrosoftReserved
                    ? Text("已在空隙中创建 Microsoft 保留分区。", "A Microsoft Reserved Partition was created in the gap.")
                    : Text("已在空隙中创建分区并格式化。", "A partition was created in the gap and formatted.")))
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

        if (EditWorkspace.PartitionHoldsStoredData(partition)
            && !await ConfirmAsync(
                Text("删除含数据的分区", "Delete partition with data"),
                Text("该分区含有已使用的数据。删除后这些数据将不可用。确定继续？",
                    "This partition holds used data. Deleting it makes that data unavailable. Continue?")))
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

        var holdsData = EditWorkspace.PartitionHoldsStoredData(partition);
        var usesRefs = fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        if (holdsData
            && !await ConfirmAsync(
                ViewModel.Localization["Format"],
                usesRefs
                    ? Text(
                        "格式化将清除该分区上的已用数据。ReFS 尚无与 64 KiB NTFS 同等的长期测试证据。确定继续？",
                        "Formatting clears used data on this partition. ReFS has no long-run evidence equivalent to 64 KiB NTFS. Continue?")
                    : Text(
                        "格式化将清除该分区上的已用数据。确定继续？",
                        "Formatting clears used data on this partition. Continue?")))
        {
            return;
        }

        if (!holdsData && usesRefs)
        {
            PublishRefsNotice();
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

    private void PublishRefsNotice() =>
        ViewModel.NotificationService.PublishWarning(
            Text("ReFS 提示", "ReFS notice"),
            Text(
                "ReFS 尚无与 64 KiB NTFS 同等的长期测试证据。操作将继续。",
                "ReFS has no long-run evidence equivalent to 64 KiB NTFS. The operation will continue."),
            "disk-partition-editor");

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
            _working = ViewModel.EffectiveActiveSnapshot;
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

        _working = ViewModel.EffectiveActiveSnapshot;
        ViewModel.NotificationService.PublishInfo(
            successTitle,
            successMessage,
            "disk-partition-editor");
        RefreshAll();
        return true;
    }
}
