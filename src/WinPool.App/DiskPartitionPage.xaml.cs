using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using SimulationEditKind = WinPool.Application.SimulationEditKind;
using SimulationEditRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Disk partition editor: topology on the left, two-row actions under it,
/// and a right-hand property card that follows the selection.
/// Simulation writes go through Agent one operation at a time.
/// </summary>
public sealed partial class DiskPartitionPage : EditorPageBase
{
    private const string NoneLetterValue = "";
    private const long BytesPerGiB = 1024L * 1024 * 1024;

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
                item.StableId.Equals(ViewModel.EffectiveActiveSnapshot.ResolvePartitionUnion(parameter.TargetStableId)?.Id ?? parameter.TargetStableId, StringComparison.OrdinalIgnoreCase));
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
        ContextHelp.Set(OnlineButton,
            Text("仅将模拟磁盘联机。", "Bring a simulated disk online only."));
        ContextHelp.Set(OfflineButton,
            Text("仅将非系统模拟磁盘脱机。", "Take a non-system simulated disk offline only."));
        ContextHelp.Set(InitializeButton,
            Text("初始化空白模拟磁盘；不修改本机磁盘。", "Initialize a blank simulated disk; no local disk is changed."));
        ContextHelp.Set(ConvertGptButton,
            Text("将符合条件的模拟 MBR 磁盘转换为 GPT。", "Convert an eligible simulated MBR disk to GPT."));
        ContextHelp.Set(NewPartitionButton,
            Text("在 GPT 模拟磁盘的未分配空间创建分区。", "Create a partition in unallocated space on a simulated GPT disk."));
        ContextHelp.Set(DeletePartitionButton,
            Text("删除选中的非系统、非启动模拟分区。", "Delete the selected simulated partition when it is neither a system nor a boot partition."));
        ContextHelp.Set(ExtendButton,
            Text(
                "选择“扩展分区”后，输入更大的目标总容量（MiB 整数，按 1 MiB 对齐）；只验证建模几何，不是 Windows 支持容量实测。",
                "Select Extend partition, then enter a larger total target capacity as a whole number of MiB (1 MiB-aligned); this validates modeled geometry, not a Windows supported-size result."));
        ContextHelp.Set(ShrinkButton,
            Text(
                "选择“压缩分区”后，输入更小的目标总容量（MiB 整数，按 1 MiB 对齐）；目标是总容量而非增量，只验证建模几何。",
                "Select Shrink partition, then enter a smaller total target capacity as a whole number of MiB (1 MiB-aligned); the target is a total size, not an increment, and only modeled geometry is validated."));
        ContextHelp.Set(OpenExplorerButton,
            Text("仅打开有本机盘符的现有本机卷。", "Open only an existing local volume with a local drive letter."));
        ContextHelp.Set(PartitionTypeBox,
            Text("仅在 GPT 模拟未分配空间中选择固定分区类型。", "Choose a fixed partition type only in simulated GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择模拟卷盘符；保留分区不能分配盘符。", "Choose a simulated volume drive letter; reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后按 Enter 保存；离开焦点不会提交。", "Enter a volume label and press Enter to save; losing focus does not submit it."));
        ContextHelp.Set(SizeBox,
            Text(
                "创建时输入新分区大小；选中已有分区时这里只显示四舍五入后的容量。扩展或压缩请使用相应按钮输入精确的 MiB 整数目标。",
                "Enter a new partition size while creating; for an existing partition, this only shows its rounded capacity. Use Extend or Shrink to enter an exact whole-MiB target."));
        ContextHelp.Set(FileSystemBox,
            Text("选择模拟格式化的文件系统。", "Choose the file system for simulated formatting."));
        ContextHelp.Set(ClusterBox,
            Text("选择分配单元；64 KiB NTFS 是当前已测试建议，不是容量保证。",
                "Choose the allocation unit; 64 KiB NTFS is the current tested recommendation, not a capacity guarantee."));
        ContextHelp.Set(QuickFormatSwitch,
            Text("控制模拟格式化是否为快速格式化。", "Choose whether simulated formatting is quick."));
        ContextHelp.Set(FormatButton,
            Text("提交当前模拟分区创建或格式化设置。", "Submit the current simulated partition creation or formatting settings."));
        foreach (var button in PropertyResetButtons())
        {
            ContextHelp.Set(button, ViewModel.Localization["ResetRecommended"]);
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
                SizeLabel.Text = Text("容量（GiB）", "Size (GiB)");
                var gb = Math.Round((_selectedUnallocatedSize ?? 0) / 1024d / 1024d / 1024d, 2);
                SizeBox.Minimum = 0;
                SizeBox.Value = gb;
                SizeBox.Maximum = gb;
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                QuickFormatSwitch.IsOn = true;
                PartitionTypeBox.SelectedIndex = 0;
            }
            else if (partition is not null)
            {
                SizeLabel.Text = Text("当前容量（GiB）", "Current size (GiB)");
                var displayedSize = Math.Round(partition.Size / (double)BytesPerGiB, 2);
                SizeBox.Minimum = 0;
                SizeBox.Maximum = Math.Max(1_000_000, displayedSize);
                SizeBox.Value = displayedSize;
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
                SizeLabel.Text = Text("容量（GiB）", "Size (GiB)");
                SizeBox.Minimum = 0;
                SizeBox.Maximum = 1_000_000;
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

    /// <summary>
    /// Projects the application deletion policy only. The page keeps its
    /// separate simulation-mode and online-disk gates around this result.
    /// </summary>
    private static bool IsDestructivePartitionTarget(PartitionInfo? partition) =>
        StorageEditRules.CanDeleteSimulatedPartition(partition);

    /// <summary>
    /// Formatting remains narrower than deletion: this page formats normal
    /// data partitions only, while the shared deletion policy is flags-only.
    /// </summary>
    private static bool IsFormatPartitionTarget(PartitionInfo? partition) =>
        partition is { Type: "Primary" or "BasicData", IsBoot: false, IsSystem: false };

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
        var destructivePartition = IsDestructivePartitionTarget(partition);
        var formattablePartition = IsFormatPartitionTarget(partition);
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
        var propertyEnabled = simulated && !diskOffline && (isPartitionSelection || isGapSelection);
        var extendCapability = partition is null
            ? null
            : StorageEditRules.GetPartitionResizeCapability(
                _working,
                partition.StableId,
                SimulationEditKind.ExtendPartition);
        var shrinkCapability = partition is null
            ? null
            : StorageEditRules.GetPartitionResizeCapability(
                _working,
                partition.StableId,
                SimulationEditKind.ShrinkPartition);
        var canExtend = propertyEnabled
            && extendCapability?.Decision.Verdict == StorageRuleVerdict.Allow;
        var canShrink = propertyEnabled
            && shrinkCapability?.Decision.Verdict == StorageRuleVerdict.Allow;

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
        DeletePartitionButton.IsEnabled = simulated && !diskOffline && destructivePartition;
        ExtendButton.IsEnabled = canExtend;
        ShrinkButton.IsEnabled = canShrink;
        OpenExplorerButton.IsEnabled = !simulated
            && isPartitionSelection
            && !diskOffline
            && letter.Length == 1
            && Directory.Exists(explorerPath);

        var canFormatSelection = createMode || formattablePartition;
        var kind = SelectedPartitionKind();
        PartitionTypeBox.IsEnabled = propertyEnabled && createMode;
        DriveLetterBox.IsEnabled = propertyEnabled && (hasVolume || createMode);
        VolumeLabelBox.IsEnabled = propertyEnabled && (hasVolume || createMode);
        SizeBox.IsEnabled = propertyEnabled && isGapSelection;
        FileSystemBox.IsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved;
        ClusterBox.IsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved;
        QuickFormatSwitch.IsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved;
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
        FormatButton.IsEnabled = propertyEnabled && canFormatSelection;
        RestoreFieldHelp();
        var contextReason = ResolveContextDisabledReason(simulated, disk, diskOffline);
        var protectedPartitionReason = DescribeFormatPartitionReason(partition);
        var destructiveReason = contextReason
            ?? (partition is null
                ? Text("请选择一个模拟分区。", "Select a simulated partition.")
                : partition.IsBoot || partition.IsSystem
                    ? Text("系统或启动分区不能删除。", "A system or boot partition cannot be deleted.")
                    : Text("当前选择不具备模拟删除条件。", "The current selection cannot be deleted in the simulation."));
        var selectionReason = contextReason;
        var createReason = contextReason
            ?? (!alreadyGpt
                ? Text("新建分区需要已初始化的 GPT 模拟磁盘。", "Creating a partition requires an initialized simulated GPT disk.")
                : !hasGap
                    ? Text("该模拟磁盘没有可用的未分配空间。", "This simulated disk has no usable unallocated space.")
                    : Text("请选择未分配空间以创建分区。", "Select unallocated space to create a partition."));
        var extendEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通模拟数据分区以扩展。", "Select a normal simulated data partition to extend.")
                : ResizeCapabilityReason(extendCapability!, extend: true));
        var shrinkEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通模拟数据分区以压缩。", "Select a normal simulated data partition to shrink.")
                : ResizeCapabilityReason(shrinkCapability!, extend: false));
        var extendReason = canExtend ? null : extendEligibilityReason;
        var shrinkReason = canShrink ? null : shrinkEligibilityReason;

        SetDisabledReason(OnlineButton,
            !simulated
                ? LocalReadOnlyReason()
                : disk is null
                    ? Text("请选择一个模拟磁盘。", "Select a simulated disk.")
                    : !isDiskSelection
                        ? Text("请先选择磁盘本身，而不是分区或未分配空间。", "Select the disk itself, not a partition or unallocated space.")
                        : !disk.IsOffline
                            ? Text("该模拟磁盘已经联机；无需再次联机。", "This simulated disk is already online.")
                            : Text("只有脱机的模拟磁盘可以联机。", "Only an offline simulated disk can be brought online."));
        SetDisabledReason(OfflineButton,
            !simulated
                ? LocalReadOnlyReason()
                : disk is null || !isDiskSelection
                    ? Text("请选择一个联机的模拟磁盘。", "Select an online simulated disk.")
                    : disk.IsBoot || disk.IsSystem
                        ? Text("系统或启动磁盘不能脱机。", "A system or boot disk cannot be taken offline.")
                        : disk.IsOffline
                            ? Text("该模拟磁盘已经脱机。", "This simulated disk is already offline.")
                            : Text("只有联机的模拟磁盘可以脱机。", "Only an online simulated disk can be taken offline."));
        SetDisabledReason(InitializeButton,
            !simulated
                ? LocalReadOnlyReason()
                : disk is null || !isDiskSelection
                    ? Text("请选择一个未初始化的模拟磁盘。", "Select an uninitialized simulated disk.")
                    : disk.IsBoot || disk.IsSystem
                        ? Text("系统或启动磁盘不能初始化。", "A system or boot disk cannot be initialized.")
                        : diskOffline
                            ? Text("请先将模拟磁盘联机。", "Bring the simulated disk online first.")
                            : raw
                                ? Text("当前磁盘已满足初始化条件。", "The current disk already meets the initialization conditions.")
                                : Text("只有 RAW 模拟磁盘可以初始化。", "Only a RAW simulated disk can be initialized."));
        SetDisabledReason(ConvertGptButton,
            !simulated
                ? LocalReadOnlyReason()
                : disk is null || !isDiskSelection
                    ? Text("请选择一个 MBR 模拟磁盘。", "Select an MBR simulated disk.")
                    : disk.IsBoot || disk.IsSystem
                        ? Text("系统或启动磁盘不能转换分区表。", "A system or boot disk cannot have its partition table converted.")
                        : diskOffline
                            ? Text("请先将模拟磁盘联机。", "Bring the simulated disk online first.")
                            : mbr
                                ? Text("当前磁盘已满足转换条件。", "The current disk already meets the conversion conditions.")
                                : Text("只有 MBR 模拟磁盘可以转换为 GPT。", "Only an MBR simulated disk can be converted to GPT."));
        SetDisabledReason(NewPartitionButton, createReason);
        SetDisabledReason(DeletePartitionButton, destructiveReason);
        SetDisabledReason(ExtendButton, extendReason);
        SetDisabledReason(ShrinkButton, shrinkReason);
        SetDisabledReason(OpenExplorerButton,
            simulated
                ? Text("资源管理器只打开本机卷；模拟系统没有本机路径。", "File Explorer opens local volumes only; simulated systems have no local path.")
                : !isPartitionSelection
                    ? Text("请选择一个本机分区。", "Select a local partition.")
                    : diskOffline
                        ? Text("该磁盘当前脱机。", "The disk is currently offline.")
                        : letter.Length != 1
                            ? Text("当前卷没有可打开的盘符。", "The current volume has no drive letter to open.")
                            : Text("当前本机路径不可用。", "The current local path is unavailable."));

        SetDisabledReason(PartitionTypeBox, createReason);
        SetDisabledReason(DriveLetterBox,
            selectionReason
            ?? (createMode && kind is PartitionKind.EfiSystem or PartitionKind.MicrosoftReserved or PartitionKind.WindowsRecovery
                ? Text("EFI、MSR 和恢复分区不能分配盘符。", "EFI, MSR, and recovery partitions cannot receive a drive letter.")
                : Text("当前选择没有可改写盘符的模拟卷。", "The current selection has no simulated volume whose drive letter can be changed.")));
        SetDisabledReason(VolumeLabelBox,
            selectionReason
            ?? (createMode && kind == PartitionKind.MicrosoftReserved
                ? Text("Microsoft 保留分区没有卷标。", "A Microsoft Reserved Partition has no volume label.")
                : Text("当前选择没有可改写卷标的模拟卷。", "The current selection has no simulated volume whose label can be changed.")));
        SetDisabledReason(SizeBox,
            selectionReason
            ?? (isGapSelection
                ? Text("请选择 GPT 模拟磁盘上的未分配空间以设置新分区容量。", "Select unallocated space on a simulated GPT disk to set a new partition capacity.")
                : partition is not null
                    ? Text(
                        "这里仅显示四舍五入后的当前容量。请使用扩展或压缩按钮输入精确的 MiB 整数目标容量。",
                        "This only shows the rounded current capacity. Use Extend or Shrink to enter an exact whole-MiB target capacity.")
                    : Text("请选择未分配空间以设置新分区容量。", "Select unallocated space to set a new partition capacity.")));
        var formatReason = contextReason
            ?? (createMode
                ? kind == PartitionKind.MicrosoftReserved
                    ? Text("Microsoft 保留分区不能格式化。", "A Microsoft Reserved Partition cannot be formatted.")
                    : Text("请选择可创建的未分配空间。", "Select unallocated space that can be used to create a partition.")
                : protectedPartitionReason
                    ?? Text("只有普通模拟数据分区可以格式化。", "Only a normal simulated data partition can be formatted."));
        SetDisabledReason(FileSystemBox, formatReason);
        SetDisabledReason(ClusterBox, formatReason);
        SetDisabledReason(QuickFormatSwitch, formatReason);
        SetDisabledReason(FormatButton, formatReason);
        UpdatePropertyResetState();
    }

    private void RestoreFieldHelp()
    {
        ContextHelp.Set(PartitionTypeBox,
            Text("仅在 GPT 模拟未分配空间中选择固定分区类型。", "Choose a fixed partition type only in simulated GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择模拟卷盘符；保留分区不能分配盘符。", "Choose a simulated volume drive letter; reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后按 Enter 保存；离开焦点不会提交。", "Enter a volume label and press Enter to save; losing focus does not submit it."));
        ContextHelp.Set(SizeBox,
            SelectedPartition() is null
                ? Text("以 GiB 输入新分区大小；提交时按现有规则转换为字节。", "Enter the new partition size in GiB; submission converts it to bytes using the existing rules.")
                : Text(
                    "这里只显示四舍五入后的当前容量。扩展或压缩请点击相应按钮，在对话框中输入精确的 MiB 整数目标总容量；扩缩只检查保存的几何、空闲空间和方向支持的模拟文件系统，不是 Windows 支持容量实测。",
                    "This only shows the rounded current capacity. Click Extend or Shrink and enter an exact whole-MiB total target in the dialog; resize checks persisted geometry, free space, and direction-supported simulated file systems, not a Windows supported-size result."));
        ContextHelp.Set(FileSystemBox,
            Text("选择模拟格式化的文件系统。", "Choose the file system for simulated formatting."));
        ContextHelp.Set(ClusterBox,
            Text("选择分配单元；64 KiB NTFS 是当前已测试建议，不是容量保证。", "Choose the allocation unit; 64 KiB NTFS is the current tested recommendation, not a capacity guarantee."));
        ContextHelp.Set(QuickFormatSwitch,
            Text("控制模拟格式化是否为快速格式化。", "Choose whether simulated formatting is quick."));
        ContextHelp.Set(FormatButton,
            Text("提交当前模拟分区创建或格式化设置。", "Submit the current simulated partition creation or formatting settings."));
    }

    private static bool TryGetResizeTargetRange(
        PartitionInfo partition,
        PartitionResizeCapability capability,
        bool extend,
        out long minimumMib,
        out long maximumMib,
        out long suggestedMib)
    {
        minimumMib = 0;
        maximumMib = 0;
        suggestedMib = 0;
        if (capability.Decision.Verdict != StorageRuleVerdict.Allow
            || capability.MinimumTargetSizeBytes is not long minimumBytes
            || capability.MaximumTargetSizeBytes is not long maximumBytes
            || minimumBytes <= 0
            || maximumBytes < minimumBytes)
        {
            return false;
        }

        var alignment = StorageEditRules.PartitionResizeAlignmentBytes;
        var minimumAllowedMib = minimumBytes / alignment
            + (minimumBytes % alignment == 0 ? 0 : 1);
        var maximumAllowedMib = maximumBytes / alignment;
        if (extend)
        {
            minimumMib = Math.Max(minimumAllowedMib, partition.Size / alignment + 1);
            maximumMib = maximumAllowedMib;
            suggestedMib = minimumMib;
        }
        else
        {
            if (partition.Size <= 1)
            {
                return false;
            }

            minimumMib = minimumAllowedMib;
            maximumMib = Math.Min(maximumAllowedMib, (partition.Size - 1) / alignment);
            suggestedMib = maximumMib;
        }

        return minimumMib > 0 && minimumMib <= maximumMib;
    }

    private static bool TryParseResizeTargetMib(string? value, out long targetMib) =>
        long.TryParse(
            value?.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out targetMib)
        && targetMib > 0;

    private static bool TryConvertMibToBytes(long targetMib, out long targetSize)
    {
        targetSize = 0;
        var alignment = StorageEditRules.PartitionResizeAlignmentBytes;
        if (targetMib <= 0 || targetMib > long.MaxValue / alignment)
        {
            return false;
        }

        targetSize = targetMib * alignment;
        return true;
    }

    private string? ValidateResizeTarget(
        PartitionInfo partition,
        SimulationEditKind action,
        long minimumMib,
        long maximumMib,
        string? input,
        out long targetSize)
    {
        targetSize = 0;
        if (!TryParseResizeTargetMib(input, out var targetMib))
        {
            return Text(
                "请输入正整数 MiB；目标是总容量，不是增量。",
                "Enter a positive whole number of MiB; the target is a total capacity, not an increment.");
        }

        if (targetMib < minimumMib || targetMib > maximumMib)
        {
            return Text(
                $"目标容量必须在 {minimumMib} 到 {maximumMib} MiB 之间。",
                $"The target capacity must be between {minimumMib} and {maximumMib} MiB.");
        }

        if (!TryConvertMibToBytes(targetMib, out targetSize))
        {
            return Text(
                "目标容量超出可安全表示的范围。",
                "The target capacity is outside the safely representable range.");
        }

        var decision = StorageEditRules.Evaluate(
            _working,
            new SimulationEditRequest(action, partition.StableId, SizeBytes: targetSize));
        return decision.Verdict == StorageRuleVerdict.Allow
            ? null
            : ResizeDecisionReason(
                decision,
                action == SimulationEditKind.ExtendPartition);
    }

    private string ResizeCapabilityReason(PartitionResizeCapability capability, bool? extend) =>
        capability.Decision.Code switch
        {
            "storage.rule.source-field-unavailable" => Text(
                "相关容量、偏移、空闲空间、文件系统或联机状态的来源不完整或冲突，无法安全模拟扩缩。",
                "The related capacity, offset, free-space, file-system, or online-state source is unavailable or conflicting, so resize cannot be simulated safely."),
            "storage.rule.source-value-out-of-range" => Text(
                "相关来源容量超出 WinPool 可安全编辑的数值范围；请在来源详情查看原始值。",
                "A related source capacity is outside WinPool's safe editing range; inspect its original value in source details."),
            "storage.rule.resize.missing-partition" => Text(
                "所选分区已不在当前模拟系统中。", "The selected partition is no longer in the current simulation."),
            "storage.rule.resize.reserved-partition" => Text(
                "EFI、Microsoft 保留和恢复分区不在模拟扩缩范围内。", "EFI, Microsoft Reserved, and recovery partitions are outside the simulated resize range."),
            "storage.rule.resize.partition-type" => Text(
                "只有普通 Primary 或 BasicData 模拟分区可以扩缩。", "Only normal Primary or BasicData simulated partitions can be resized."),
            "storage.rule.resize.missing-disk" => Text(
                "该分区没有可验证的模拟磁盘归属，无法确定容量边界。", "The partition has no verifiable simulated disk owner, so its capacity boundary is unknown."),
            "storage.rule.resize.offline" => Text(
                "模拟磁盘已脱机；请先联机后扩缩分区。", "The simulated disk is offline; bring it online before resizing the partition."),
            "storage.rule.resize.ambiguous-volume" => Text(
                "该分区关联了多个卷，无法确定要保留的文件系统容量。", "The partition is linked to multiple volumes, so the file-system capacity to preserve is ambiguous."),
            "storage.rule.resize.extend-filesystem" when extend is null => Text(
                "当前模拟文件系统不支持扩缩：NTFS 支持两种方向，ReFS 仅支持扩展，RAW／未格式化按建模规则可用，exFAT 两种方向都不支持。",
                "The current simulated file system does not support resize: NTFS supports both directions, ReFS only extend, RAW/unformatted is modeled as supported, and exFAT supports neither direction."),
            "storage.rule.resize.extend-filesystem" => Text(
                "仅 NTFS、ReFS 或 RAW／未格式化的普通模拟数据分区可以扩展；exFAT 不支持扩展。",
                "Only NTFS, ReFS, or RAW/unformatted normal simulated data partitions can be extended; exFAT cannot be extended."),
            "storage.rule.resize.shrink-filesystem" when extend is null => Text(
                "当前模拟文件系统不支持扩缩：NTFS 支持两种方向，ReFS 仅支持扩展，RAW／未格式化按建模规则可用，exFAT 两种方向都不支持。",
                "The current simulated file system does not support resize: NTFS supports both directions, ReFS only extend, RAW/unformatted is modeled as supported, and exFAT supports neither direction."),
            "storage.rule.resize.shrink-filesystem" => Text(
                "仅 NTFS 或 RAW／未格式化的普通模拟数据分区可以压缩；ReFS 和 exFAT 不支持压缩。",
                "Only NTFS or RAW/unformatted normal simulated data partitions can be shrunk; ReFS and exFAT cannot be shrunk."),
            "storage.rule.resize.extend-no-aligned-target" => Text(
                "分区右侧没有足够的连续未分配空间，无法形成更大的 1 MiB 对齐目标容量。",
                "There is not enough contiguous unallocated space immediately after the partition for a larger 1 MiB-aligned target capacity."),
            "storage.rule.resize.shrink-no-aligned-target" => Text(
                "当前已用数据和几何没有更小的 1 MiB 对齐目标容量可用。", "No smaller 1 MiB-aligned target capacity preserves the current data and geometry."),
            "storage.rule.resize.partition-geometry" or "storage.rule.resize.sibling-geometry"
                or "storage.rule.resize.overlapping-layout" or "storage.rule.resize.volume-geometry"
                or "storage.rule.resize.geometry-range" or "storage.rule.resize.numeric-overflow" => Text(
                    "当前保存的容量、偏移、相邻分区或卷数据不能安全确定模拟扩缩边界。",
                "The persisted capacity, offset, neighboring-partition, or volume data cannot safely determine simulated resize bounds."),
            _ => Text(
                extend is true
                    ? "当前选择不具备可验证的模拟扩展条件。"
                    : extend is false
                        ? "当前选择不具备可验证的模拟压缩条件。"
                        : "当前选择不具备可验证的模拟分区扩缩条件。",
                extend is true
                    ? "The current selection does not meet verifiable simulated extend conditions."
                    : extend is false
                        ? "The current selection does not meet verifiable simulated shrink conditions."
                        : "The current selection does not meet verifiable simulated partition-resize conditions.")
        };

    private string ResizeDecisionReason(StorageRuleDecision? decision, bool extend)
    {
        if (decision is null)
        {
            return Text("请输入目标容量后再执行分区扩缩。", "Enter a target capacity before resizing the partition.");
        }

        return decision.Code switch
        {
            "storage.rule.resize.extend-target" => Text(
                "扩展的目标容量必须大于当前分区容量。", "The extend target capacity must be larger than the current partition capacity."),
            "storage.rule.resize.shrink-target" => Text(
                "压缩的目标容量必须小于当前分区容量。", "The shrink target capacity must be smaller than the current partition capacity."),
            "storage.rule.resize.used-space" or "storage.rule.resize.volume-free-space" => Text(
                "目标容量小于模拟已用数据或无法保留所需的空闲空间。", "The target capacity is smaller than modeled used data or cannot preserve required free space."),
            "storage.rule.resize.geometry-boundary" => Text(
                "目标容量会越过模拟磁盘边界或与下一分区重叠。", "The target capacity would cross the simulated disk boundary or overlap the next partition."),
            "storage.rule.resize.target-alignment" => Text(
                "目标容量必须按 1 MiB 对齐。", "The target capacity must be 1 MiB-aligned."),
            _ when decision.Verdict != StorageRuleVerdict.Allow => ResizeCapabilityReason(
                new PartitionResizeCapability(decision, null, null, null), extend),
            _ => extend
                ? Text("输入更大的目标容量以扩展分区。", "Enter a larger target capacity to extend the partition.")
                : Text("输入更小的目标容量以压缩分区。", "Enter a smaller target capacity to shrink the partition.")
        };
    }

    private string? ResolveContextDisabledReason(
        bool simulated,
        OsDiskInfo? disk,
        bool diskOffline)
    {
        if (!simulated)
        {
            return LocalReadOnlyReason();
        }

        if (disk is null)
        {
            return Text("请选择一个模拟磁盘、分区或未分配空间。",
                "Select a simulated disk, partition, or unallocated space.");
        }

        if (diskOffline)
        {
            return Text("模拟磁盘已脱机；请先联机后编辑分区。",
                "The simulated disk is offline; bring it online before editing partitions.");
        }

        return null;
    }

    private string LocalReadOnlyReason() =>
        Text("本机存储在此页只读；请选择或创建模拟系统后编辑。",
            "Local storage is read-only on this page; select or create a simulated system to edit.");

    private string? DescribeFormatPartitionReason(PartitionInfo? partition)
    {
        if (partition is null)
        {
            return null;
        }

        if (partition.IsBoot || partition.IsSystem)
        {
            return Text(
                "系统或启动分区不能格式化；卷标、盘符和符合条件的模拟扩缩仍可编辑。",
                "A system or boot partition cannot be formatted; label, drive letter, and eligible simulated resize remain editable.");
        }

        return partition.Type switch
        {
            "EfiSystem" => Text("EFI 系统分区不能在此页格式化。", "An EFI system partition cannot be formatted on this page."),
            "MicrosoftReserved" => Text("Microsoft 保留分区不能在此页格式化。", "A Microsoft Reserved Partition cannot be formatted on this page."),
            "WindowsRecovery" => Text("Windows 恢复分区不能在此页格式化。", "A Windows recovery partition cannot be formatted on this page."),
            _ => null
        };
    }

    private static void SetDisabledReason(FrameworkElement control, string? reason) =>
        ContextHelp.SetDisabledReason(
            control,
            control is Control { IsEnabled: false } ? reason : null);

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
            UpdateButtonState();
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

        UpdateButtonState();
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
            new SimulationEditRequest(
                SimulationEditKind.ChangeDriveLetter,
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
                new SimulationEditRequest(
                    SimulationEditKind.Rename,
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
            new SimulationEditRequest(
                SimulationEditKind.SetDiskOffline,
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
            new SimulationEditRequest(
                SimulationEditKind.SetDiskOffline,
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
            new SimulationEditRequest(
                SimulationEditKind.InitializeDisk,
                disk.StableId,
                PartitionStyle: "GPT",
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
            new SimulationEditRequest(SimulationEditKind.ConvertDisk, disk.StableId, PartitionStyle: "GPT"),
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
                new SimulationEditRequest(
                    SimulationEditKind.CreatePartition,
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

    private async void Extend_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: true);

    private async void Shrink_Click(object sender, RoutedEventArgs e) => await ResizeAsync(extend: false);

    private async Task<long?> PromptResizeTargetAsync(
        PartitionInfo partition,
        PartitionResizeCapability capability,
        bool extend)
    {
        if (!TryGetResizeTargetRange(
                partition,
                capability,
                extend,
                out var minimumMib,
                out var maximumMib,
                out var suggestedMib))
        {
            await ShowMessageAsync(
                extend
                    ? Text("无法扩展分区", "Cannot extend partition")
                    : Text("无法压缩分区", "Cannot shrink partition"),
                extend
                    ? Text(
                        "分区右侧没有足够的连续未分配空间，无法形成更大的 1 MiB 对齐目标容量。",
                        "There is not enough contiguous unallocated space immediately after the partition for a larger 1 MiB-aligned target capacity.")
                    : Text(
                        "当前已用数据和几何没有可用的更小 1 MiB 对齐目标容量。",
                        "The modeled used data and geometry have no smaller 1 MiB-aligned target capacity."));
            return null;
        }

        var action = extend ? SimulationEditKind.ExtendPartition : SimulationEditKind.ShrinkPartition;
        var input = new TextBox
        {
            Header = Text("目标容量（MiB，整数）", "Target capacity (MiB, whole number)"),
            Text = suggestedMib.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinWidth = 320
        };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Text(
                "请输入目标总容量，而不是要增加或减少的容量。只有选择确认后才会写入模拟修改。",
                "Enter the total target capacity, not the amount to add or remove. The simulation changes only after you confirm."),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = extend
                ? Text(
                    $"可用目标范围：{minimumMib} 到 {maximumMib} MiB。已预填最小可扩展目标 {suggestedMib} MiB。",
                    $"Available target range: {minimumMib} to {maximumMib} MiB. The smallest expandable target, {suggestedMib} MiB, is prefilled.")
                : Text(
                    $"可用目标范围：{minimumMib} 到 {maximumMib} MiB。已预填最接近当前容量的可压缩目标 {suggestedMib} MiB。",
                    $"Available target range: {minimumMib} to {maximumMib} MiB. The shrink target closest to the current capacity, {suggestedMib} MiB, is prefilled."),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(input);
        content.Children.Add(validation);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = extend
                ? Text("扩展分区", "Extend partition")
                : Text("压缩分区", "Shrink partition"),
            Content = content,
            PrimaryButtonText = extend
                ? Text("确认扩展", "Confirm extend")
                : Text("确认压缩", "Confirm shrink"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        long? targetSize = null;
        void Validate()
        {
            var error = ValidateResizeTarget(
                partition,
                action,
                minimumMib,
                maximumMib,
                input.Text,
                out var candidateSize);
            targetSize = error is null ? candidateSize : null;
            validation.Text = error ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        input.TextChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            Validate();
            args.Cancel = targetSize is null;
        };
        Validate();
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary
            ? targetSize
            : null;
    }

    private async Task ResizeAsync(bool extend)
    {
        var partition = SelectedPartition();
        if (partition is null)
        {
            return;
        }

        var action = extend ? SimulationEditKind.ExtendPartition : SimulationEditKind.ShrinkPartition;
        var capability = StorageEditRules.GetPartitionResizeCapability(
            _working,
            partition.StableId,
            action);
        if (capability.Decision.Verdict != StorageRuleVerdict.Allow)
        {
            await ShowMessageAsync(
                extend
                    ? Text("无法扩展分区", "Cannot extend partition")
                    : Text("无法压缩分区", "Cannot shrink partition"),
                ResizeCapabilityReason(capability, extend));
            UpdateButtonState();
            return;
        }

        var targetSize = await PromptResizeTargetAsync(partition, capability, extend);
        if (targetSize is null)
        {
            return;
        }

        var request = new SimulationEditRequest(action, partition.StableId, SizeBytes: targetSize);
        var decision = StorageEditRules.Evaluate(_working, request);
        if (decision.Verdict != StorageRuleVerdict.Allow)
        {
            await ShowMessageAsync(
                extend
                    ? Text("扩展分区失败", "Partition extend failed")
                    : Text("压缩分区失败", "Partition shrink failed"),
                ResizeDecisionReason(decision, extend));
            UpdateButtonState();
            return;
        }

        var successTitle = extend
            ? Text("扩展分区成功", "Partition extended")
            : Text("压缩分区成功", "Partition shrunk");
        var successMessage = Text(
            "已将模拟分区调整为目标容量；如有关联卷，已同步其容量与剩余空间。这是建模结果，不是 Windows 支持容量实测。",
            "The simulated partition was adjusted to the target capacity; when a linked volume exists, its capacity/free space was synchronized. This is a modeled result, not a Windows supported-size measurement.");
        _ = await SubmitAsync(
            request,
            successTitle,
            successMessage,
            failTitle: extend
                ? Text("扩展分区失败", "Partition extend failed")
                : Text("压缩分区失败", "Partition shrink failed"));
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
                new SimulationEditRequest(SimulationEditKind.DeletePartition, id),
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
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
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
        PublishOperationFeedback(
            GlobalNotificationSeverity.Warning,
            Text("ReFS 提示", "ReFS notice"),
            Text(
                "ReFS 尚无与 64 KiB NTFS 同等的长期测试证据。操作将继续。",
                "ReFS has no long-run evidence equivalent to 64 KiB NTFS. The operation will continue."),
            "disk-partition-editor",
            "partition.refs-evidence",
            showNotification: true);

    private async Task<bool> SubmitAsync(
        SimulationEditRequest request,
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
            PublishOperationException(
                failTitle ?? Text("操作失败", "Operation failed"),
                "disk-partition-editor",
                exception,
                "partition.submit.exception",
                showNotification: true);
            return false;
        }

        if (result.Status == WinPool.Application.ApplicationStatus.OutcomeUnknown)
        {
            PublishOperationResult(
                result.Status,
                result.Messages,
                result.CorrelationId,
                Text("提交结果未知", "Commit outcome unknown"),
                "disk-partition-editor",
                showNotification: true);
            _working = ViewModel.EffectiveActiveSnapshot;
            RefreshAll();
            return false;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            PublishOperationResult(
                result.Status,
                result.Messages,
                result.CorrelationId,
                failTitle ?? Text("操作失败", "Operation failed"),
                "disk-partition-editor",
                showNotification: true);
            return false;
        }

        _working = ViewModel.EffectiveActiveSnapshot;
        PublishOperationFeedback(
            GlobalNotificationSeverity.Info,
            successTitle,
            successMessage,
            "disk-partition-editor",
            "partition.submit.completed");
        RefreshAll();
        return true;
    }
}
