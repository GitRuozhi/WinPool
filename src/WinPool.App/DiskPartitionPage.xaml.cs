using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
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
    private sealed record InitializedDiskLayoutOptions(
        bool CreateMsr, long? DiskSizeBytes, bool FormatNtfs, string? Label, char? Letter);

    private string? _selectedDiskId;
    private string? _selectedPartitionId;
    private long? _selectedUnallocatedOffset;
    private long? _selectedUnallocatedSize;
    private TopologyEditInteraction _interaction = null!;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;
    private bool _filling;
    private bool _renameInProgress;
    private bool _formatModeControlsReady;
    private bool _narrowLayout;
    private bool _showPropertiesInNarrowLayout;
    private GridLength _widePropertiesWidth = new(320);

    public DiskPartitionPage()
    {
        InitializeComponent();
        _formatModeControlsReady = true;
        SetFormatMode(quickFormat: true);
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

    internal void RefreshExecutionMode() => UpdateButtonState();

    protected override void OnRealInventoryRefreshed()
    {
        if (ViewModel.IsUsingSimulatedInventory)
            return;

        _working = ViewModel.EffectiveActiveSnapshot;
        var selectedPartition = _selectedPartitionId is null
            ? null
            : _working.Partitions.FirstOrDefault(item =>
                item.StableId.Equals(_selectedPartitionId, StringComparison.OrdinalIgnoreCase));
        if (_selectedPartitionId is not null && selectedPartition is null)
            _selectedPartitionId = null;
        if (selectedPartition is not null)
        {
            _selectedDiskId = selectedPartition.OsDiskStableId ?? _selectedDiskId;
            _selectedUnallocatedOffset = null;
            _selectedUnallocatedSize = null;
        }

        if (_selectedDiskId is not null
            && !_working.OsDisks.Any(item =>
                item.StableId.Equals(_selectedDiskId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedDiskId = null;
        }

        if (_selectedUnallocatedOffset is long selectedOffset)
        {
            var selectedDisk = _selectedDiskId is null
                ? null
                : _working.OsDisks.FirstOrDefault(item =>
                    item.StableId.Equals(_selectedDiskId, StringComparison.OrdinalIgnoreCase));
            var gapStillExists = selectedDisk is not null && _selectedUnallocatedSize is long size
                && EditWorkspace.UnallocatedGaps(selectedDisk,
                    _working.Partitions.Where(item => item.OsDiskStableId == selectedDisk.StableId).ToArray())
                    .Any(gap => gap.Offset == selectedOffset && gap.Size == size
                        && gap.Size >= UnallocatedIgnoreBytes);
            if (!gapStillExists)
            {
                _selectedUnallocatedOffset = null;
                _selectedUnallocatedSize = null;
            }
        }

        RefreshAll();
    }

    private void LocalizeChrome()
    {
        ShowTargetsButton.Content = Text("目标与操作", "Targets and actions");
        ShowPropertiesButton.Content = Text("属性", "Properties");
        QueryRealOperationButton.Content = Text("按 ID 查询真实操作", "Query real operation by ID");
        QueryRealOperationButton.IsEnabled = ViewModel.AgentConnection is not null;
        StopRealOperationButton.Content = Text("按 ID 停止后续真实步骤", "Stop following real steps by ID");
        StopRealOperationButton.IsEnabled = ViewModel.AgentConnection is not null;
        OnlineButtonLabel.Text = Text("联机", "Online");
        OfflineButtonLabel.Text = Text("脱机", "Offline");
        InitializeButtonLabel.Text = ViewModel.Localization["InitializeDisk"];
        ConvertGptButtonLabel.Text = Text("转换为 GPT", "Convert to GPT");
        ClearDiskForPoolButtonLabel.Text = Text("清空至 RAW（独立操作）", "Clear to RAW (separate operation)");
        DeletePartitionButtonLabel.Text = Text("删除分区", "Delete partition");
        ExtendButtonLabel.Text = Text("扩展分区", "Extend partition");
        ShrinkButtonLabel.Text = Text("压缩分区", "Shrink partition");
        OpenExplorerButtonLabel.Text = Text("打开资源管理器", "Open in File Explorer");
        DiskLocationLabel.Text = Text("所在磁盘", "Disk");
        PartitionNumberLabel.Text = Text("分区编号", "Partition number");
        StartOffsetLabel.Text = Text("起点", "Start");
        EndOffsetLabel.Text = Text("终点", "End");
        PartitionTypeLabel.Text = Text("分区类型", "Partition type");
        DriveLetterLabel.Text = Text("盘符", "Drive letter");
        VolumeLabelCaption.Text = Text("卷标", "Volume label");
        SizeLabel.Text = Text("容量", "Capacity");
        FileSystemLabel.Text = Text("文件系统", "File system");
        ClusterLabel.Text = Text("分配单元", "Allocation unit");
        QuickFormatLabel.Text = Text("快速格式化", "Quick format");
        FullFormatLabel.Text = Text("完整格式化", "Full format");
        AutomationProperties.SetName(QuickFormatSwitch, QuickFormatLabel.Text);
        AutomationProperties.SetName(FullFormatSwitch, FullFormatLabel.Text);
        PartitionActionButtonLabel.Text = Text("新建分区", "Create partition");
        PartitionActionButtonIcon.Glyph = "\uE710";
        AutomationProperties.SetName(PartitionActionButton, Text("新建分区", "Create partition"));
        AutomationProperties.SetName(MaximumSizeButton, Text("使用最大容量", "Use maximum capacity"));
        AutomationProperties.SetName(SizeBox, Text("容量", "Capacity"));
        AutomationProperties.SetName(SizeAdaptiveValue, Text("自适应容量单位", "Adaptive capacity unit"));
        ContextHelp.Set(OnlineButton,
            Text("将符合条件的磁盘联机；本机真实操作需单独预览和确认。", "Bring an eligible disk online; a real local operation requires its own preview and confirmation."));
        ContextHelp.Set(OfflineButton,
            Text("将符合条件的非系统磁盘脱机；本机真实操作需单独预览和确认。", "Take an eligible non-system disk offline; a real local operation requires its own preview and confirmation."));
        ContextHelp.Set(InitializeButton,
            Text("初始化符合条件的 RAW 磁盘；本机真实操作会修改磁盘结构。", "Initialize an eligible RAW disk; a real local operation changes its disk structure."));
        ContextHelp.Set(ConvertGptButton,
            Text("将符合条件的模拟 MBR 磁盘转换为 GPT。", "Convert an eligible simulated MBR disk to GPT."));
        ContextHelp.Set(DeletePartitionButton,
            Text("删除符合条件的非系统、非启动分区；真实删除会使该分区的数据丢失。", "Delete an eligible non-system, non-boot partition; real deletion loses its data."));
        ContextHelp.Set(ExtendButton,
            Text(
                "选择“扩展分区”后，输入更大的目标总容量（MiB 整数，按 1 MiB 对齐）；真实操作由 Agent 查询 Windows 实时支持范围。",
                "Select Extend partition, then enter a larger total target capacity as a whole number of MiB (1 MiB-aligned); for real operations, the Agent queries the live Windows supported range."));
        ContextHelp.Set(ShrinkButton,
            Text(
                "选择“压缩分区”后，输入更小的目标总容量（MiB 整数，按 1 MiB 对齐）；真实操作由 Agent 查询 Windows 实时支持范围。",
                "Select Shrink partition, then enter a smaller total target capacity as a whole number of MiB (1 MiB-aligned); for real operations, the Agent queries the live Windows supported range."));
        ContextHelp.Set(OpenExplorerButton,
            Text("仅打开有本机盘符的现有本机卷。", "Open only an existing local volume with a local drive letter."));
        ContextHelp.Set(PartitionTypeBox,
            Text("在 GPT 未分配空间中选择固定分区类型。", "Choose a fixed partition type in GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择卷盘符；保留分区不能分配盘符。", "Choose a volume drive letter; reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后按 Enter 保存；离开焦点不会提交。", "Enter a volume label and press Enter to save; losing focus does not submit it."));
        ContextHelp.Set(SizeBox,
            Text(
                "创建时输入 MiB 正整数；第二行显示同一容量的自适应单位。最大值按选中空隙的 1 MiB 对齐范围计算。",
                "Enter a whole number of MiB while creating; the second line shows the same capacity in an adaptive unit. The maximum uses the selected gap's 1 MiB-aligned range."));
        ContextHelp.Set(MaximumSizeButton,
            Text("将容量填为选中空隙中的最大 1 MiB 对齐容量。", "Fill the largest 1 MiB-aligned capacity available in the selected gap."));
        ContextHelp.Set(FileSystemBox,
            Text("选择格式化文件系统；真实模式仅开放本阶段已验证的组合。", "Choose a format file system; real mode allows only combinations verified for this stage."));
        ContextHelp.Set(ClusterBox,
            Text("选择分配单元；64 KiB NTFS 是当前已测试建议，不是容量保证。",
                "Choose the allocation unit; 64 KiB NTFS is the current tested recommendation, not a capacity guarantee."));
        ContextHelp.Set(QuickFormatSwitch,
            Text(
                "选择快速格式化；真实操作将在单独确认后格式化目标分区。",
                "Choose quick format; a real operation formats the target partition after its own confirmation."));
        ContextHelp.Set(FullFormatSwitch,
            Text(
                "选择完整格式化；真实操作将在单独确认后扫描并格式化目标分区。",
                "Choose full format; a real operation scans and formats the target partition after its own confirmation."));
        foreach (var button in PropertyResetButtons())
        {
            ContextHelp.Set(button, ViewModel.Localization["ResetRecommended"]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                ViewModel.Localization["ResetRecommended"]);
        }
        ContextHelp.Set(ResetSizeButton,
            Text("恢复此分区类型的建议 MiB 容量。", "Restore the recommended MiB capacity for this partition type."));
        AutomationProperties.SetName(
            ResetSizeButton,
            Text("恢复建议容量", "Restore recommended capacity"));
        FillFileSystemBox();
        FillClusterBox();
        FillPartitionTypeBox();
    }

    private void EditorLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        if (narrow && !_narrowLayout)
            _widePropertiesWidth = PropertiesColumn.Width;
        _narrowLayout = narrow;
        UpdateEditorLayout();
    }

    private void ShowTargets_Click(object sender, RoutedEventArgs e)
    {
        _showPropertiesInNarrowLayout = false;
        UpdateEditorLayout();
    }

    private void ShowProperties_Click(object sender, RoutedEventArgs e)
    {
        _showPropertiesInNarrowLayout = true;
        UpdateEditorLayout();
    }

    private void UpdateEditorLayout()
    {
        NarrowPageNavigation.Visibility = _narrowLayout
            ? Visibility.Visible : Visibility.Collapsed;
        ShowTargetsButton.IsEnabled = _showPropertiesInNarrowLayout;
        ShowPropertiesButton.IsEnabled = !_showPropertiesInNarrowLayout;
        var showTargets = !_narrowLayout || !_showPropertiesInNarrowLayout;
        var showProperties = !_narrowLayout || _showPropertiesInNarrowLayout;
        TopologyBorder.Visibility = showTargets ? Visibility.Visible : Visibility.Collapsed;
        PartitionChromeBorder.Visibility = showTargets ? Visibility.Visible : Visibility.Collapsed;
        PropertiesPane.Visibility = showProperties ? Visibility.Visible : Visibility.Collapsed;
        EditorSplitter.Visibility = _narrowLayout ? Visibility.Collapsed : Visibility.Visible;
        TargetsColumn.MinWidth = _narrowLayout ? 0 : 120;
        PropertiesColumn.MinWidth = _narrowLayout ? 0 : 240;
        TargetsColumn.Width = showTargets
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitterColumn.Width = new GridLength(_narrowLayout ? 0 : 8);
        PropertiesColumn.Width = _narrowLayout
            ? showProperties ? new GridLength(1, GridUnitType.Star) : new GridLength(0)
            : _widePropertiesWidth;
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
        if (_selectedUnallocatedOffset is not null)
            FileSystemBox.Items.Add(Text("不格式化", "Do not format"));
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

    private void PartitionActionsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PartitionActionsPanel.Width = Math.Max(PartitionActionsPanel.ItemWidth, e.NewSize.Width);
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
            var geometry = gap ? SelectedCreateGeometry() : null;
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
            long? startOffsetBytes = null;
            long? endOffsetBytes = null;
            if (start is long startBytes && length is long sizeBytes)
            {
                if (geometry is { CanCreate: true, StartOffsetBytes: long createStart, MaximumEndOffsetExclusiveBytes: long usableEnd })
                {
                    startOffsetBytes = createStart;
                    endOffsetBytes = usableEnd;
                }
                else
                {
                    if (TryGetExclusiveRangeEnd(startBytes, sizeBytes, out var endBytes))
                    {
                        startOffsetBytes = startBytes;
                        endOffsetBytes = endBytes;
                    }
                }
            }
            else if (disk is not null && !gap && partition is null)
            {
                startOffsetBytes = 0;
                endOffsetBytes = disk.Size;
            }
            SetOffsetValues(startOffsetBytes, endOffsetBytes);

            FillFileSystemChoices(partition);
            FillDriveLetters(partition, volume, autoAssign: gap);
            VolumeLabelBox.Text = volume?.FileSystemLabel
                ?? (partition is null ? string.Empty : partition.FileSystemLabel);
            if (gap)
            {
                SetSizeMib(geometry?.DefaultSizeBytes is long defaultSize
                    ? defaultSize / BytesPerMiB
                    : null);
                if (geometry is { CanCreate: true })
                {
                    UpdateSizeAdaptiveValue();
                }
                else
                {
                    SizeAdaptiveValue.Text = "—";
                }
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                SetFormatMode(quickFormat: true);
                PartitionTypeBox.SelectedIndex = 0;
            }
            else if (partition is not null)
            {
                SizeBox.Text = (partition.Size / (double)BytesPerMiB).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                SizeAdaptiveValue.Text = TopologyProjector.FormatBytes(partition.Size);
                SelectFileSystem(volume?.FileSystem ?? partition.FileSystem);
                SelectCluster(volume?.AllocationUnitSize ?? partition.AllocationUnitSize);
                SetFormatMode(quickFormat: true);
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
                SizeBox.Text = string.Empty;
                SizeAdaptiveValue.Text = "—";
                FileSystemBox.SelectedIndex = 0;
                ClusterBox.SelectedIndex = 4;
                SetFormatMode(quickFormat: true);
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
        var nextFree = ViewModel.CanSubmitRealOperation
            ? "DEFGHIJKLMNOPQRSTUVWXYZ".Select(letter => letter.ToString())
                .FirstOrDefault(letter => !used.Contains(letter)) ?? string.Empty
            : NextFreeDriveLetter(used);
        if (autoAssign && current.Length != 1)
        {
            current = nextFree;
        }

        for (var letter = 'C'; letter <= 'Z'; letter++)
        {
            if (ViewModel.CanSubmitRealOperation && letter == 'C'
                && !string.Equals(current, "C", StringComparison.OrdinalIgnoreCase))
                continue;
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
        StorageEditRules.CanFormatSimulatedPartition(partition);

    private bool IsProtected(PartitionInfo partition) =>
        partition.IsBoot
        || partition.IsSystem
        || partition.Type is "EfiSystem" or "MicrosoftReserved" or "WindowsRecovery";

    private string SelectedFileSystemToken()
    {
        var selected = FileSystemBox.SelectedItem as string ?? "NTFS";
        return selected == Text("不格式化", "Do not format") ? string.Empty : selected;
    }

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
        var letter = volume?.DriveLetter ?? (partition is null ? string.Empty : _working.DriveLetterOf(partition));
        var explorerPath = letter.Length == 1 ? $"{letter}:\\" : string.Empty;
        var diskOffline = disk?.IsOffline == true;
        var createMode = isGapSelection && alreadyGpt;
        var realEditablePartition = ViewModel.CanSubmitRealOperation
            && partition is { IsBoot: false, IsSystem: false, Type: "Primary" or "BasicData" };
        var realDeletablePartition = ViewModel.CanSubmitRealOperation
            && partition is { IsBoot: false, IsSystem: false,
                Type: "Primary" or "BasicData" or "EfiSystem" or "MicrosoftReserved" or "WindowsRecovery" };
        var propertyEnabled = (simulated || (ViewModel.CanSubmitRealOperation && createMode)
            || realEditablePartition)
            && !diskOffline && (isPartitionSelection || isGapSelection);
        var createGeometry = SelectedCreateGeometry();
        var hasIntegerSize = TryGetSizeBytes(out var requestedSizeBytes);
        var maximumCreateSizeBytes = createGeometry?.MaximumSizeBytes;
        var sizeFitsSelectedGap = hasIntegerSize
            && createGeometry is { CanCreate: true }
            && maximumCreateSizeBytes is long maximumSizeBytes
            && requestedSizeBytes <= maximumSizeBytes;
        var contextReason = ResolveContextDisabledReason(simulated, disk, diskOffline);
        var createReason = contextReason
            ?? (!alreadyGpt
                ? Text("新建分区需要已初始化的 GPT 模拟磁盘。", "Creating a partition requires an initialized simulated GPT disk.")
                : !isGapSelection
                    ? Text("请选择 GPT 模拟磁盘上的未分配空间。", "Select unallocated space on a simulated GPT disk.")
                    : createGeometry is { CanCreate: false }
                        ? GeometryUnavailableReason(createGeometry)
                        : !hasIntegerSize
                            ? Text("请输入大于零的 MiB 正整数。", "Enter a positive whole number of MiB.")
                            : maximumCreateSizeBytes is long maximum && requestedSizeBytes > maximum
                                ? Text($"容量不能超过对齐后的上限 {maximum / BytesPerMiB} MiB。", $"Capacity cannot exceed the aligned maximum of {maximum / BytesPerMiB} MiB.")
                                : null);
        var canCreatePartition = propertyEnabled && createMode && sizeFitsSelectedGap;
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
        var realResizeCandidate = realEditablePartition && alreadyGpt &&
            disk is { IsBoot: false, IsSystem: false } && !diskOffline &&
            RealPartitionResizeUiRange.IsSupportedFileSystem(
                volume?.FileSystem ?? partition?.FileSystem);
        var realRefsResize = realResizeCandidate && string.Equals(
            volume?.FileSystem ?? partition?.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
        var canExtend = realResizeCandidate || (simulated && propertyEnabled
            && extendCapability?.Decision.Verdict == StorageRuleVerdict.Allow);
        var canShrink = (realResizeCandidate && !realRefsResize) || (simulated && propertyEnabled
            && shrinkCapability?.Decision.Verdict == StorageRuleVerdict.Allow);

        var real = ViewModel.CanSubmitRealOperation;
        var diskPartitions = disk is null ? Array.Empty<PartitionInfo>()
            : _working.Partitions.Where(item =>
                string.Equals(item.OsDiskStableId, disk.StableId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        var canResumeRealLayout = real && isDiskSelection && alreadyGpt
            && disk is { IsBoot: false, IsSystem: false, IsOffline: false }
            && diskPartitions.Length <= 1
            && diskPartitions.All(IsExactProviderMsr);
        var realRefsUnsupported = real && createMode &&
            SelectedFileSystemToken().Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        OnlineButton.IsEnabled = (simulated || real) && isDiskSelection && disk is { IsOffline: true };
        OfflineButton.IsEnabled = (simulated || real)
            && isDiskSelection
            && disk is { IsOffline: false, IsBoot: false, IsSystem: false };
        InitializeButtonLabel.Text = real && alreadyGpt
            ? Text("完成布局", "Complete layout")
            : ViewModel.Localization["InitializeDisk"];
        InitializeButton.IsEnabled = (simulated || real) && isDiskSelection && !diskOffline
            && disk is { IsBoot: false, IsSystem: false } && raw
            || canResumeRealLayout;
        ConvertGptButton.IsEnabled = simulated && isDiskSelection && !diskOffline
            && disk is { IsBoot: false, IsSystem: false } && mbr;
        ClearDiskForPoolButton.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
        ClearDiskForPoolButton.IsEnabled = real && isDiskSelection && !diskOffline
            && disk is { IsBoot: false, IsSystem: false } && !raw;
        DeletePartitionButton.IsEnabled = (simulated || realDeletablePartition)
            && !diskOffline && destructivePartition;
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
        SizeBox.IsEnabled = propertyEnabled && createMode && createGeometry is { CanCreate: true };
        MaximumSizeButton.IsEnabled = propertyEnabled && createMode && createGeometry is { CanCreate: true, MaximumSizeBytes: not null };
        FileSystemBox.IsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved;
        ClusterBox.IsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved
            && !string.IsNullOrEmpty(SelectedFileSystemToken());
        var formatOptionsEnabled = propertyEnabled && canFormatSelection && kind != PartitionKind.MicrosoftReserved
            && !string.IsNullOrEmpty(SelectedFileSystemToken());
        var realRefsExistingFormat = real && !createMode
            && SelectedFileSystemToken().Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        var realQuickOnlyRoleCreation = real && createMode
            && (kind == PartitionKind.EfiSystem || kind == PartitionKind.WindowsRecovery);
        QuickFormatSwitch.IsEnabled = formatOptionsEnabled && !realQuickOnlyRoleCreation;
        FullFormatSwitch.IsEnabled = formatOptionsEnabled
            && !realRefsExistingFormat && !realQuickOnlyRoleCreation;
        if (createMode && kind is PartitionKind.EfiSystem or PartitionKind.MicrosoftReserved or PartitionKind.WindowsRecovery)
        {
            DriveLetterBox.IsEnabled = false;
        }
        if (createMode && kind == PartitionKind.MicrosoftReserved)
        {
            VolumeLabelBox.IsEnabled = false;
        }
        RestoreFieldHelp();
        var protectedPartitionReason = DescribeFormatPartitionReason(partition);
        var destructiveReason = contextReason
            ?? (partition is null
                ? Text("请选择一个模拟分区。", "Select a simulated partition.")
                : partition.IsBoot || partition.IsSystem
                    ? Text("系统或启动分区不能删除。", "A system or boot partition cannot be deleted.")
                    : Text("当前选择不具备模拟删除条件。", "The current selection cannot be deleted in the simulation."));
        var selectionReason = contextReason;
        var extendEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通模拟数据分区以扩展。", "Select a normal simulated data partition to extend.")
                : ResizeCapabilityReason(extendCapability!, extend: true));
        var shrinkEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通模拟数据分区以压缩。", "Select a normal simulated data partition to shrink.")
                : ResizeCapabilityReason(shrinkCapability!, extend: false));
        var realRangeReason = realRefsResize
            ? Text("ReFS 真实分区仅开放扩展；压缩保持禁用，扩展范围由 Agent 实时核验。",
                "Real ReFS partitions allow extension only; shrinking stays disabled and the Agent checks the live extension range.")
            : Text(
                "真实扩缩仅允许普通 GPT NTFS、ReFS 扩展或 RAW 数据分区；点击后由 Agent 读取实时支持范围。",
                "Real resize is limited to ordinary GPT NTFS, ReFS extension, or RAW data partitions; the Agent reads the live supported range when clicked.");
        var extendReason = canExtend ? null : ViewModel.CanSubmitRealOperation
            ? realRangeReason : extendEligibilityReason;
        var shrinkReason = canShrink ? null : ViewModel.CanSubmitRealOperation
            ? realRangeReason : shrinkEligibilityReason;

        SetDisabledReason(OnlineButton,
            !simulated && !real
                ? LocalReadOnlyReason()
                : disk is null
                    ? Text("请选择一个模拟磁盘。", "Select a simulated disk.")
                    : !isDiskSelection
                        ? Text("请先选择磁盘本身，而不是分区或未分配空间。", "Select the disk itself, not a partition or unallocated space.")
                        : !disk.IsOffline
                            ? Text("该模拟磁盘已经联机；无需再次联机。", "This simulated disk is already online.")
                            : Text("只有脱机的模拟磁盘可以联机。", "Only an offline simulated disk can be brought online."));
        SetDisabledReason(OfflineButton,
            !simulated && !real
                ? LocalReadOnlyReason()
                : disk is null || !isDiskSelection
                    ? Text("请选择一个联机的模拟磁盘。", "Select an online simulated disk.")
                    : disk.IsBoot || disk.IsSystem
                        ? Text("系统或启动磁盘不能脱机。", "A system or boot disk cannot be taken offline.")
                        : disk.IsOffline
                            ? Text("该模拟磁盘已经脱机。", "This simulated disk is already offline.")
                            : Text("只有联机的模拟磁盘可以脱机。", "Only an online simulated disk can be taken offline."));
        SetDisabledReason(InitializeButton,
            !simulated && !real
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
        if (real && alreadyGpt)
        {
            SetDisabledReason(InitializeButton,
                canResumeRealLayout
                    ? Text("新扫描显示 GPT 且仅有零个或一个精确 provider MSR；可显式重新准备 MSR 和数据布局。",
                        "The fresh scan shows GPT and zero or one exact provider MSR. You can explicitly prepare the MSR and data layout.")
                    : Text("只有未启动且仅含精确 provider MSR 的 GPT 磁盘可继续布局。",
                        "Only a non-system GPT disk containing no partition other than the exact provider MSR can continue layout."));
        }
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
        SetDisabledReason(ClearDiskForPoolButton,
            !real ? LocalReadOnlyReason()
                : disk is null || !isDiskSelection
                    ? Text("请选择精确的本机磁盘。", "Select the exact local disk.")
                    : disk.IsBoot || disk.IsSystem
                        ? Text("系统或启动磁盘禁止清盘。", "A system or boot disk cannot be cleared.")
                        : raw ? Text("RAW 磁盘无需清盘。", "A RAW disk does not need clearing.")
                            : Text("该目标的实时安全预检尚未完成。", "Live safety preflight is required for this target."));
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
                ? createMode
                    ? createGeometry is { CanCreate: false }
                        ? GeometryUnavailableReason(createGeometry)
                        : Text("请使用 MiB 正整数设置新分区容量。", "Enter the new partition capacity as a whole number of MiB.")
                    : createReason
                : partition is not null
                    ? Text(
                        "这里仅显示四舍五入后的当前容量。请使用扩展或压缩按钮输入精确的 MiB 整数目标容量。",
                        "This only shows the rounded current capacity. Use Extend or Shrink to enter an exact whole-MiB target capacity.")
                    : Text("请选择未分配空间以设置新分区容量。", "Select unallocated space to set a new partition capacity.")));
        SetDisabledReason(MaximumSizeButton, createReason);
        var formatOptionsReason = contextReason
            ?? (createMode
                ? kind == PartitionKind.MicrosoftReserved
                    ? Text("Microsoft 保留分区不能格式化。", "A Microsoft Reserved Partition cannot be formatted.")
                    : null
                : protectedPartitionReason
                    ?? (partition is null
                        ? Text("请选择可格式化的普通模拟数据分区。", "Select a formatable simulated data partition.")
                        : null));
        var formatReason = contextReason
            ?? (partition is null
                ? Text("请选择现有的普通模拟数据分区以格式化。", "Select an existing simulated data partition to format.")
                : protectedPartitionReason);
        var createActionSelected = partition is null;
        var selectedRealFileSystem = SelectedFileSystemToken();
        var realExistingFormatSupported = !realEditablePartition
            || (selectedRealFileSystem is "NTFS" or "exFAT" or "ReFS"
                && SelectedClusterBytes() == 65536
                && (!selectedRealFileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
                    || QuickFormatSwitch.IsOn));
        PartitionActionButtonLabel.Text = createActionSelected
            ? Text("新建分区", "Create partition")
            : Text("格式化分区", "Format partition");
        PartitionActionButtonIcon.Glyph = createActionSelected ? "\uE710" : "\uE9CE";
        AutomationProperties.SetName(
            PartitionActionButton,
            createActionSelected ? Text("新建分区", "Create partition") : Text("格式化分区", "Format partition"));
        PartitionActionButton.IsEnabled = createActionSelected
            ? canCreatePartition && !realRefsUnsupported
            : propertyEnabled && isPartitionSelection && formattablePartition
                && realExistingFormatSupported;
        ContextHelp.Set(
            PartitionActionButton,
            createActionSelected
                ? Text("在选中的 GPT 未分配空间中创建分区；真实操作需单独预览和确认。", "Create a partition in the selected GPT gap; a real operation requires its own preview and confirmation.")
                : Text("提交当前分区格式化设置；真实操作会清除目标分区的数据。", "Submit the partition format settings; a real operation erases data on the target partition."));
        SetDisabledReason(PartitionActionButton, realRefsUnsupported
            ? Text("ReFS 真实创建仍待 C01 现场能力证据，本阶段禁用。",
                "Real ReFS creation requires C01 provider evidence and is disabled in this stage.")
            : createActionSelected ? createReason
            : realExistingFormatSupported ? formatReason
            : Text("现有真实数据分区仅支持 64 KiB NTFS/exFAT；ReFS 限快速格式化，且必须为 64 KiB。",
                "Existing real data partitions support 64 KiB NTFS/exFAT; ReFS is limited to quick format with 64 KiB."));
        SetDisabledReason(FileSystemBox, formatOptionsReason);
        SetDisabledReason(ClusterBox, formatOptionsReason);
        SetDisabledReason(QuickFormatSwitch, realQuickOnlyRoleCreation
            ? Text("真实 EFI 和恢复分区仅支持快速格式化。",
                "Real EFI and Recovery partition creation supports quick format only.")
            : formatOptionsReason);
        SetDisabledReason(FullFormatSwitch, realRefsExistingFormat
            ? Text("C01 本阶段仅开放 64 KiB 快速 ReFS 格式化。",
                "C01 currently enables only quick ReFS format with 64 KiB allocation units.")
            : realQuickOnlyRoleCreation
                ? Text("真实 EFI 和恢复分区仅支持快速格式化。",
                    "Real EFI and Recovery partition creation supports quick format only.")
                : formatOptionsReason);
        UpdatePropertyResetState();
    }

    private void RestoreFieldHelp()
    {
        ContextHelp.Set(PartitionTypeBox,
            Text("在 GPT 未分配空间中选择固定分区类型。", "Choose a fixed partition type in GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择卷盘符；保留分区不能分配盘符。", "Choose a volume drive letter; reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后按 Enter 保存；离开焦点不会提交。", "Enter a volume label and press Enter to save; losing focus does not submit it."));
        ContextHelp.Set(SizeBox,
            SelectedPartition() is null
                ? Text("以 MiB 正整数输入新分区大小；下一行显示自适应单位。", "Enter the new partition size as a whole number of MiB; the next line shows an adaptive unit.")
                : Text(
                    "这里只显示四舍五入后的当前容量。扩展或压缩请点击相应按钮，在对话框中输入精确的 MiB 整数目标总容量；真实操作由 Agent 查询实时支持范围。",
                    "This only shows the rounded current capacity. Click Extend or Shrink and enter an exact whole-MiB total target in the dialog; for real operations, the Agent queries the live supported range."));
        ContextHelp.Set(MaximumSizeButton,
            Text("将容量填为选中空隙中的最大 1 MiB 对齐容量。", "Fill the largest 1 MiB-aligned capacity available in the selected gap."));
        ContextHelp.Set(FileSystemBox,
            Text("选择格式化文件系统；真实模式仅开放本阶段已验证的组合。", "Choose a format file system; real mode allows only combinations verified for this stage."));
        ContextHelp.Set(ClusterBox,
            Text("选择分配单元；64 KiB NTFS 是当前已测试建议，不是容量保证。", "Choose the allocation unit; 64 KiB NTFS is the current tested recommendation, not a capacity guarantee."));
        ContextHelp.Set(QuickFormatSwitch,
            Text(
                "选择快速格式化；真实操作将在单独确认后格式化目标分区。",
                "Choose quick format; a real operation formats the target partition after its own confirmation."));
        ContextHelp.Set(FullFormatSwitch,
            Text(
                "选择完整格式化；真实操作将在单独确认后扫描并格式化目标分区。",
                "Choose full format; a real operation scans and formats the target partition after its own confirmation."));
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
        if (!simulated && !(ViewModel.CanSubmitRealOperation
            && (_selectedUnallocatedOffset is not null
                || SelectedPartition() is { IsBoot: false, IsSystem: false,
                    Type: "Primary" or "BasicData" })))
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

    private string LocalReadOnlyReason() => ViewModel.CanSubmitRealOperation
        ? Text("所选真实操作或目标尚未通过本阶段安全校验，入口保持禁用。",
            "This real operation or target has not passed this stage's safety checks and remains disabled.")
        : Text("本机存储在此页只读；请先取得 Agent 真实模式回执或选择模拟系统。",
            "Local storage is read-only here until the Agent arms real mode, or select a simulation.");

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
            "Primary" or "BasicData" => null,
            _ => Text("只有普通数据分区可以在此页格式化。", "Only a normal data partition can be formatted on this page.")
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
            var recommendedSizeMib = RecommendedCreateSizeMib();
            if (recommendedSizeMib is long defaultMib)
            {
                switch (SelectedPartitionKind())
                {
                    case PartitionKind.EfiSystem:
                        FillFileSystemBoxFor("FAT32");
                        ClusterBox.SelectedIndex = 0;
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        SetFormatMode(quickFormat: true);
                        break;
                    case PartitionKind.MicrosoftReserved:
                        FillFileSystemBoxFor(string.Empty);
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        VolumeLabelBox.Text = string.Empty;
                        SetFormatMode(quickFormat: true);
                        break;
                    case PartitionKind.WindowsRecovery:
                        FillFileSystemBoxFor("NTFS");
                        ClusterBox.SelectedIndex = 0;
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        SetFormatMode(quickFormat: true);
                        break;
                    default:
                        FillFileSystemBox();
                        ClusterBox.SelectedIndex = 4;
                        SetSizeMib(defaultMib);
                        FillDriveLetters(null, null, autoAssign: true);
                        SetFormatMode(quickFormat: true);
                        break;
                }
            }
            UpdateSizeAdaptiveValue();
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

    private void SizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling)
        {
            UpdateSizeAdaptiveValue();
            UpdatePropertyResetState();
            UpdateButtonState();
        }
    }

    private void MaximumSize_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCreateGeometry()?.MaximumSizeBytes is long maximumBytes)
        {
            SetSizeMib(maximumBytes / BytesPerMiB);
            UpdateSizeAdaptiveValue();
            UpdatePropertyResetState();
            UpdateButtonState();
        }
    }

    private void ClusterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            UpdatePropertyResetState();
            UpdateButtonState();
        }
    }

    private void FormatModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_formatModeControlsReady || _filling || sender is not ToggleSwitch changedSwitch)
        {
            return;
        }

        _filling = true;
        try
        {
            if (changedSwitch == QuickFormatSwitch)
            {
                FullFormatSwitch.IsOn = !QuickFormatSwitch.IsOn;
            }
            else if (changedSwitch == FullFormatSwitch)
            {
                QuickFormatSwitch.IsOn = !FullFormatSwitch.IsOn;
            }
        }
        finally
        {
            _filling = false;
        }

        UpdatePropertyResetState();
        UpdateButtonState();
    }

    private void SetFormatMode(bool quickFormat)
    {
        var wasFilling = _filling;
        _filling = true;
        try
        {
            QuickFormatSwitch.IsOn = quickFormat;
            FullFormatSwitch.IsOn = !quickFormat;
        }
        finally
        {
            _filling = wasFilling;
        }
    }

    private void UpdatePropertyResetState()
    {
        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        var gap = _selectedUnallocatedOffset is not null;
        var parameterEnabled = ViewModel.IsUsingSimulatedInventory && (partition is not null || gap);
        var recommendedSizeMib = RecommendedCreateSizeMib();
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
            && (!TryGetSizeMib(out var currentSizeMib)
                || recommendedSizeMib is not long recommendedMib
                || currentSizeMib != recommendedMib);
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
            SetSizeMib(RecommendedCreateSizeMib());
            UpdateSizeAdaptiveValue();
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
        SetFormatMode(quickFormat: true);
        UpdatePropertyResetState();
    }

    private long? RecommendedCreateSizeMib()
    {
        var maximumMib = SelectedCreateGeometry()?.DefaultSizeBytes is long defaultBytes
            ? defaultBytes / BytesPerMiB
            : 0;
        return maximumMib > 0 ? maximumMib : null;
    }

    private PartitionCreateGeometry? SelectedCreateGeometry()
    {
        if (_selectedUnallocatedOffset is not long offset || _selectedUnallocatedSize is not long size)
            return null;
        if (ViewModel.IsUsingSimulatedInventory)
            return EditWorkspace.GetPartitionCreateGeometry(offset, size);
        return SelectedDisk() is { } disk
            ? EditWorkspace.GetRealPartitionCreateGeometry(disk.Size, offset, size)
            : null;
    }

    private void SetSizeMib(long? sizeMib) =>
        SizeBox.Text = sizeMib?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private bool TryGetSizeMib(out long sizeMib) =>
        long.TryParse(
            SizeBox.Text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out sizeMib)
        && sizeMib > 0
        && sizeMib <= long.MaxValue / BytesPerMiB;

    private bool TryGetSizeBytes(out long sizeBytes)
    {
        sizeBytes = 0;
        if (!TryGetSizeMib(out var sizeMib))
        {
            return false;
        }

        sizeBytes = sizeMib * BytesPerMiB;
        return true;
    }

    private void UpdateSizeAdaptiveValue()
    {
        SizeAdaptiveValue.Text = TryGetSizeBytes(out var sizeBytes)
            ? TopologyProjector.FormatBytes(sizeBytes)
            : Text("请输入 MiB 正整数", "Enter a whole number of MiB");
    }

    private void SetOffsetValues(long? startBytes, long? endBytes)
    {
        SetOffsetValue(
            startBytes,
            StartOffsetValue,
            StartOffsetMibPrefixValue,
            StartOffsetAdaptiveValue,
            StartOffsetUnitValue,
            StartOffsetUnavailableValue);
        SetOffsetValue(
            endBytes,
            EndOffsetValue,
            EndOffsetMibPrefixValue,
            EndOffsetAdaptiveValue,
            EndOffsetUnitValue,
            EndOffsetUnavailableValue);
    }

    private void SetOffsetValue(
        long? bytes,
        TextBlock mibValue,
        TextBlock mibPrefix,
        TextBlock adaptiveValue,
        TextBlock adaptiveUnit,
        TextBlock unavailableValue)
    {
        var hasValue = bytes is long;
        mibValue.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        mibPrefix.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        adaptiveValue.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        adaptiveUnit.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        unavailableValue.Visibility = hasValue ? Visibility.Collapsed : Visibility.Visible;
        if (bytes is not long value)
        {
            return;
        }

        var cultureName = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
            ? "zh-CN"
            : "en-US";
        mibValue.Text = ((decimal)value / BytesPerMiB).ToString(
            "#,0.########",
            System.Globalization.CultureInfo.GetCultureInfo(cultureName));

        var adaptiveText = TopologyProjector.FormatBytes(value);
        var unitSeparator = adaptiveText.LastIndexOf(' ');
        adaptiveValue.Text = unitSeparator > 0
            ? adaptiveText[..unitSeparator]
            : adaptiveText;
        adaptiveUnit.Text = unitSeparator > 0
            ? $" {adaptiveText[(unitSeparator + 1)..]})"
            : ")";
    }

    private static bool TryGetExclusiveRangeEnd(long startBytes, long sizeBytes, out long endBytes)
    {
        endBytes = 0;
        if (startBytes < 0 || sizeBytes <= 0 || startBytes > long.MaxValue - sizeBytes)
        {
            return false;
        }

        endBytes = startBytes + sizeBytes;
        return true;
    }

    private string GeometryUnavailableReason(PartitionCreateGeometry geometry) => geometry.UnavailableReason switch
    {
        "The selected unallocated region has no usable space." =>
            Text("所选未分配区域没有可用空间。", "The selected unallocated region has no usable space."),
        "The selected unallocated region exceeds the supported byte range." =>
            Text("所选未分配区域超出支持的容量范围。", "The selected unallocated region exceeds the supported byte range."),
        "This unallocated region has less than 1 MiB remaining after aligning its start." =>
            Text("起点按 1 MiB 对齐后，剩余空间不足 1 MiB。", "Less than 1 MiB remains after aligning the start."),
        "No unallocated region can hold a 1 MiB-aligned partition." =>
            Text("没有可容纳 1 MiB 对齐分区的未分配区域。", "No unallocated region can hold a 1 MiB-aligned partition."),
        _ => geometry.UnavailableReason ?? Text("该未分配区域不可创建分区。", "A partition cannot be created in this unallocated region.")
    };

    private async void DriveLetterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || (!ViewModel.IsUsingSimulatedInventory && !ViewModel.CanSubmitRealOperation))
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

        if (ViewModel.CanSubmitRealOperation)
        {
            var previous = volume.DriveLetter.Length == 1
                ? char.ToUpperInvariant(volume.DriveLetter[0]) : (char?)null;
            var requested = next.Length == 1 ? char.ToUpperInvariant(next[0]) : (char?)null;
            await SubmitRealAsync(RealPartitionIntent(OperationIntent.SetDriveLetter,
                partition, new SetDriveLetterCommand(PartitionReference(partition),
                    previous, requested),
                $"Drive letter changes from {previous?.ToString() ?? "none"} to {requested?.ToString() ?? "none"}",
                "No partition data loss expected"));
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
        if (_filling || _renameInProgress ||
            (!ViewModel.IsUsingSimulatedInventory && !ViewModel.CanSubmitRealOperation))
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
            if (ViewModel.CanSubmitRealOperation)
            {
                var target = new StorageObjectId(ViewModel.ActiveDocument.SystemId,
                    StorageObjectKind.Volume, volume.StableId);
                await SubmitRealAsync(RealOneTargetIntent(OperationIntent.SetVolumeLabel,
                    target, new RenameVolumeCommand(RealTargetReference.ForExisting(target), next),
                    $"Volume label becomes {next}", "No file data loss expected"));
                return;
            }
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

        if (ViewModel.CanSubmitRealOperation)
        {
            await SubmitRealAsync(RealDiskIntent(OperationIntent.SetDiskOnlineState,
                disk, new SetDiskOnlineCommand(DiskReference(disk), true),
                "Disk online", "No partition data loss expected"));
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

    private async void QueryRealOperation_Click(object sender, RoutedEventArgs e) =>
        await QueryRealOperationByIdAsync();

    private async void StopRealOperation_Click(object sender, RoutedEventArgs e) =>
        await StopRealOperationFollowingStepsByIdAsync();

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null)
        {
            return;
        }

        if (ViewModel.CanSubmitRealOperation)
        {
            await SubmitRealAsync(RealDiskIntent(OperationIntent.SetDiskOnlineState,
                disk, new SetDiskOnlineCommand(DiskReference(disk), false),
                "Disk offline", "Running access to this disk stops"));
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

        if (ViewModel.CanSubmitRealOperation)
        {
            var system = ViewModel.ActiveDocument.SystemId;
            if (string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase))
            {
                var options = await PromptInitializedDiskLayoutAsync(disk);
                if (options is not null)
                    await SubmitInitializedDiskLayoutAsync(disk, options.CreateMsr,
                        options.DiskSizeBytes, options.FormatNtfs, options.Label, options.Letter);
                return;
            }

            if (!string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
                return;
            var target = new StorageObjectId(system, StorageObjectKind.OsDisk, disk.StableId);
            if (!await SubmitRealAsync(RealOperationProposalFactory.InitializeGpt(system, target))
                || !LastRealInventoryRefreshSucceeded)
                return;

            var refreshedDisk = _working.OsDisks.FirstOrDefault(item =>
                item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
            if (refreshedDisk is null)
            {
                await ShowMessageAsync(Text("磁盘初始化后无法继续", "Cannot continue after disk initialization"),
                    Text("新扫描中找不到原目标磁盘；未提交后续布局计划。",
                        "The fresh scan no longer contains the target disk. No follow-up layout plan was submitted."));
                return;
            }

            await SubmitInitializedDiskLayoutAsync(refreshedDisk,
                ViewModel.CurrentPreferences.CreateMsrOnInitialize);
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

    private async Task<InitializedDiskLayoutOptions?> PromptInitializedDiskLayoutAsync(
        OsDiskInfo disk)
    {
        var partitions = _working.Partitions.Where(partition =>
            string.Equals(partition.OsDiskStableId, disk.StableId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partitions.Length > 1 || partitions.Any(partition => !IsExactProviderMsr(partition)))
        {
            await ShowMessageAsync(Text("布局计划被阻止", "Layout plan blocked"),
                Text("新扫描中的分区不符合 GPT 空盘或唯一精确 provider MSR 条件。",
                    "The fresh scan does not show an empty GPT disk or one exact provider MSR."));
            return null;
        }

        var msr = partitions.SingleOrDefault();
        var targetSummary = Text(
            $"OS Disk: {disk.StableId}; size={disk.Size}; partition style={disk.PartitionStyle}\n" +
            (msr is null
                ? "Provider MSR: none"
                : $"Provider MSR: {msr.StableId}; type={msr.PartitionTypeId}; offset={msr.Offset}; size={msr.Size}"),
            $"OS disk: {disk.StableId}; size={disk.Size}; partition style={disk.PartitionStyle}\n" +
            (msr is null
                ? "Provider MSR: none"
                : $"Provider MSR: {msr.StableId}; type={msr.PartitionTypeId}; offset={msr.Offset}; size={msr.Size}"));
        var msrBox = new CheckBox
        {
            Content = Text("保留一个 16 MiB MSR（位于 1 MiB）", "Create one 16 MiB MSR at 1 MiB"),
            IsChecked = ViewModel.CurrentPreferences.CreateMsrOnInitialize
        };
        var dataBox = new CheckBox
        {
            Content = Text("使用剩余空间建立 BasicData 分区", "Create a BasicData partition from the remaining space"),
            IsChecked = false
        };
        var formatBox = new CheckBox
        {
            Content = Text("快速格式化为 NTFS / 64 KiB", "Quick-format as NTFS / 64 KiB"),
            IsChecked = true
        };
        var labelBox = new TextBox
        {
            Header = Text("卷标（可选）", "Volume label (optional)"),
            Text = "WinPool_Test"
        };
        var letterBox = new TextBox
        {
            Header = Text("盘符 D–Z（可留空）", "Drive letter D–Z (optional)"),
            MaxLength = 1
        };
        var layoutPreview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void RefreshPreview()
        {
            var total = disk.Size / BytesPerMiB * BytesPerMiB;
            var dataOffset = msrBox.IsChecked == true ? 17 * BytesPerMiB : BytesPerMiB;
            var dataSize = total - dataOffset - BytesPerMiB;
            layoutPreview.Text = dataBox.IsChecked == true
                ? Text($"新数据分区起点：{dataOffset}；大小：{dataSize} bytes（已保留末尾 1 MiB）。",
                    $"New BasicData offset: {dataOffset}; size: {dataSize} bytes (1 MiB tail reserve).")
                : Text("不会创建 BasicData 分区。", "No BasicData partition will be created.");
            formatBox.IsEnabled = dataBox.IsChecked == true;
            labelBox.IsEnabled = dataBox.IsChecked == true && formatBox.IsChecked == true;
            letterBox.IsEnabled = dataBox.IsChecked == true;
        }
        msrBox.Checked += (_, _) => RefreshPreview();
        msrBox.Unchecked += (_, _) => RefreshPreview();
        dataBox.Checked += (_, _) => RefreshPreview();
        dataBox.Unchecked += (_, _) => RefreshPreview();
        formatBox.Checked += (_, _) => RefreshPreview();
        formatBox.Unchecked += (_, _) => RefreshPreview();
        RefreshPreview();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = targetSummary,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(msrBox);
        panel.Children.Add(dataBox);
        panel.Children.Add(formatBox);
        panel.Children.Add(labelBox);
        panel.Children.Add(letterBox);
        panel.Children.Add(layoutPreview);
        panel.Children.Add(validation);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Text("完成 GPT 磁盘布局", "Complete GPT disk layout"),
            Content = new ScrollViewer { MaxHeight = 520, Content = panel },
            PrimaryButtonText = Text("准备第二阶段计划", "Prepare second-phase plan"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (dataBox.IsChecked == true && disk.Size / BytesPerMiB * BytesPerMiB
                    - (msrBox.IsChecked == true ? 17 * BytesPerMiB : BytesPerMiB)
                    - BytesPerMiB <= 0)
            {
                validation.Text = Text("磁盘容量不足以建立所选布局。", "The disk is too small for the selected layout.");
                args.Cancel = true;
                return;
            }
            var letter = letterBox.Text.Trim().ToUpperInvariant();
            if (dataBox.IsChecked == true && letter.Length > 0
                && (letter.Length != 1 || letter[0] is < 'D' or > 'Z'))
            {
                validation.Text = Text("盘符只能为 D–Z，或留空。", "The drive letter must be D–Z or blank.");
                args.Cancel = true;
                return;
            }
        };
        if (await DialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;

        var createData = dataBox.IsChecked == true;
        var diskSizeBytes = createData
            ? disk.Size / BytesPerMiB * BytesPerMiB
            : (long?)null;
        var selectedLetter = letterBox.Text.Trim().ToUpperInvariant();
        return new InitializedDiskLayoutOptions(
            msrBox.IsChecked == true,
            diskSizeBytes,
            createData && formatBox.IsChecked == true,
            createData && formatBox.IsChecked == true ? labelBox.Text.Trim() : null,
            createData && selectedLetter.Length == 1 ? selectedLetter[0] : null);
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

    private async void ClearDiskForPool_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (!ViewModel.CanSubmitRealOperation || disk is null ||
            disk.IsBoot || disk.IsSystem || disk.IsOffline ||
            _selectedPartitionId is not null || _selectedUnallocatedOffset is not null)
            return;

        var physical = _working.PhysicalDisks.SingleOrDefault(item =>
            item.StableId == disk.PhysicalDiskStableId);
        var partitions = _working.Partitions.Where(item =>
            item.OsDiskStableId == disk.StableId).OrderBy(item => item.Offset).ToArray();
        var partitionList = partitions.Length == 0
            ? Text("（未发现分区）", "(no partitions reported)")
            : string.Join(Environment.NewLine, partitions.Select(item =>
                $"#{item.PartitionNumber} {item.Type} " +
                $"offset={item.Offset} size={item.Size} " +
                $"{item.DriveLetter}: {item.FileSystem} {item.FileSystemLabel} " +
                $"id={item.StableId}"));
        var preliminary = string.Join(Environment.NewLine,
            $"OS disk: {disk.StableId}",
            $"Physical member: {physical?.StableId ?? "unresolved"}",
            $"Model: {physical?.Model ?? "unresolved"}",
            $"Serial: {physical?.SerialNumber ?? "unresolved"}",
            $"Size: {disk.Size} bytes",
            Text("所有下列分区、卷、文件和盘符将丢失：",
                "All partitions, volumes, files and drive letters below will be lost:"),
            partitionList,
            Text("此操作只清空至 RAW。建池或初始化 GPT 需要另建计划并再次确认。",
                "This operation only clears to RAW. Pool creation or GPT initialization needs a separate plan and confirmation."));
        if (!await ConfirmAsync(Text("独立清盘确认", "Separate disk-clear confirmation"),
                preliminary))
            return;

        var target = new StorageObjectId(ViewModel.ActiveDocument.SystemId,
            StorageObjectKind.OsDisk, disk.StableId);
        await SubmitRealAsync(RealOperationProposalFactory.ClearToRaw(
            ViewModel.ActiveDocument.SystemId, target));
    }

    private bool DiskHoldsStoredData(OsDiskInfo disk) =>
        _working.Partitions.Any(item =>
            item.OsDiskStableId == disk.StableId
            && EditWorkspace.PartitionHoldsStoredData(item));

    private RealTargetReference DiskReference(OsDiskInfo disk) =>
        RealTargetReference.ForExisting(new StorageObjectId(
            ViewModel.ActiveDocument.SystemId, StorageObjectKind.OsDisk, disk.StableId));

    private RealTargetReference PartitionReference(PartitionInfo partition) =>
        RealTargetReference.ForExisting(new StorageObjectId(
            ViewModel.ActiveDocument.SystemId, StorageObjectKind.Partition,
            partition.StableId));

    private RealOperationIntentRequest RealPartitionIntent(
        OperationIntent intent, PartitionInfo partition, RealStorageCommand command,
        string expectedState, string dataLoss) =>
        RealOneTargetIntent(intent,
            new StorageObjectId(ViewModel.ActiveDocument.SystemId,
                StorageObjectKind.Partition, partition.StableId),
            command, expectedState, dataLoss);

    private RealOperationIntentRequest RealOneTargetIntent(
        OperationIntent intent, StorageObjectId target, RealStorageCommand command,
        string expectedState, string dataLoss) =>
        RealOperationProposalFactory.OneStep(ViewModel.ActiveDocument.SystemId,
            intent, target, command, expectedState, dataLoss);

    private RealOperationIntentRequest RealDiskIntent(
        OperationIntent intent, OsDiskInfo disk, RealStorageCommand command,
        string expectedState, string dataLoss)
    {
        var target = new StorageObjectId(
            ViewModel.ActiveDocument.SystemId, StorageObjectKind.OsDisk, disk.StableId);
        return RealOperationProposalFactory.OneStep(ViewModel.ActiveDocument.SystemId,
            intent, target, command, expectedState, dataLoss);
    }

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

        var geometry = SelectedCreateGeometry();
        if (geometry is not { CanCreate: true, StartOffsetBytes: long createOffset, MaximumSizeBytes: long maximumSize }
            || _selectedUnallocatedOffset is null)
        {
            return;
        }

        if (!TryGetSizeBytes(out var createSizeBytes) || createSizeBytes > maximumSize)
        {
            return;
        }

        var partitionKind = SelectedPartitionKind();
        var fileSystem = partitionKind == PartitionKind.MicrosoftReserved
            ? string.Empty
            : SelectedFileSystemToken();
        if (fileSystem == "ReFS" && ViewModel.CanSubmitRealOperation)
        {
            await ShowMessageAsync(Text("真实 ReFS 未开放", "Real ReFS is unavailable"),
                Text("C01 现场能力尚未证实，真实 ReFS 创建保持禁用。",
                    "C01 provider capability is unverified, so real ReFS creation remains disabled."));
            return;
        }
        if (fileSystem == "ReFS")
        {
            PublishRefsNotice();
        }

        var letter = DriveLetterBox.SelectedItem as string;
        if (letter == Text("无", "None"))
        {
            letter = NoneLetterValue;
        }

        var diskId = _selectedDiskId;
        var offset = createOffset;
        var quickFormat = QuickFormatSwitch.IsOn;
        if (ViewModel.CanSubmitRealOperation)
        {
            var diskTarget = new StorageObjectId(ViewModel.ActiveDocument.SystemId,
                StorageObjectKind.OsDisk, disk.StableId);
            var realFileSystem = fileSystem.ToUpperInvariant() switch
            {
                "" => (RealFileSystem?)null,
                "NTFS" => RealFileSystem.Ntfs,
                "EXFAT" => RealFileSystem.ExFat,
                "REFS" => RealFileSystem.ReFs,
                "FAT32" => RealFileSystem.Fat32,
                _ => throw new InvalidOperationException("Unsupported real file system selection.")
            };
            try
            {
                await SubmitRealAsync(RealOperationProposalFactory.CreatePartition(
                    ViewModel.ActiveDocument.SystemId, diskTarget,
                    partitionKind switch
                    {
                        PartitionKind.EfiSystem => RealPartitionRole.Efi,
                        PartitionKind.MicrosoftReserved => RealPartitionRole.Msr,
                        PartitionKind.WindowsRecovery => RealPartitionRole.Recovery,
                        _ => RealPartitionRole.BasicData
                    }, offset, createSizeBytes, realFileSystem,
                    checked((int)SelectedClusterBytes()), !quickFormat,
                    VolumeLabelBox.Text,
                    partitionKind == PartitionKind.BasicData && letter is { Length: 1 }
                        ? char.ToUpperInvariant(letter[0]) : null));
            }
            catch (ArgumentException exception)
            {
                await ShowMessageAsync(
                    Text("真实分区参数不受支持", "Real partition parameters are unsupported"),
                    exception.Message);
            }
            return;
        }
        if (!await SubmitAsync(
                new SimulationEditRequest(
                    SimulationEditKind.CreatePartition,
                    diskId!,
                    Name: VolumeLabelBox.Text,
                    DriveLetter: letter,
                    FileSystem: fileSystem,
                    AllocationUnitSize: SelectedClusterBytes(),
                    SizeBytes: createSizeBytes,
                    OffsetBytes: offset,
                    PartitionKind: partitionKind,
                    QuickFormat: string.IsNullOrWhiteSpace(fileSystem) ? null : quickFormat),
                partitionKind == PartitionKind.MicrosoftReserved
                    ? Text("新建分区成功", "Partition created")
                    : quickFormat
                        ? Text("新建分区并快速格式化成功", "Partition created with quick format")
                        : Text("新建分区并完整格式化成功", "Partition created with full format"),
                partitionKind == PartitionKind.MicrosoftReserved
                    ? Text("已在空隙中创建 Microsoft 保留分区。", "A Microsoft Reserved Partition was created in the gap.")
                    : quickFormat
                        ? Text("已在空隙中创建分区并模拟快速格式化；没有格式化真实磁盘。", "A partition was created and simulated quick formatting was recorded; no real disk was formatted.")
                        : Text("已在空隙中创建分区并记录完整格式化模式；没有扫描真实介质。", "A partition was created and simulated full-format mode was recorded; no real media was scanned.")))
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

    private async Task ResizeRealAsync(PartitionInfo partition, bool extend)
    {
        if (ViewModel.AgentConnection is null)
            return;
        var system = ViewModel.ActiveDocument.SystemId;
        var target = new StorageObjectId(system, StorageObjectKind.Partition,
            partition.StableId);
        ApplicationResult<AgentResponse> queried;
        try
        {
            queried = await ViewModel.AgentConnection.SendAsync(
                new QueryAgentRealPartitionResizeRangeRequest(target,
                    ViewModel.RealProductSessionId, CorrelationId.New()),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishOperationException(Text("实时扩缩范围读取失败", "Live resize range failed"),
                "real", exception, "real.resize.range_query_failed");
            return;
        }
        if (!queried.IsSuccess)
        {
            PublishOperationResult(queried.Status, queried.Messages, queried.CorrelationId,
                Text("实时扩缩范围不可用", "Live resize range unavailable"), "real");
            return;
        }
        if (queried.Value is not AgentRealPartitionResizeRangeResponse response ||
            response.Range.Partition != target)
        {
            await ShowMessageAsync(Text("实时扩缩范围无效", "Invalid live resize range"),
                Text("Agent 回执未匹配所选分区；未准备写入计划。",
                    "The Agent response did not match the selected partition; no write plan was prepared."));
            return;
        }
        var range = response.Range;
        if (!RealPartitionResizeUiRange.TryGetWholeMibTargets(
                range, extend, out var minimumMib, out var maximumMib))
        {
            await ShowMessageAsync(Text("当前没有可用目标", "No supported target"),
                Text("Agent 已读取 Windows 支持范围与当前几何交集，但所选方向没有 1 MiB 整数目标。",
                    "The Agent read the Windows supported range and current geometry intersection, but there is no whole-MiB target in this direction."));
            return;
        }
        var targetSize = await PromptRealResizeTargetAsync(
            range, extend, minimumMib, maximumMib);
        if (targetSize is null)
            return;
        await SubmitRealAsync(RealPartitionIntent(OperationIntent.ResizePartition,
            partition, new ResizePartitionCommand(RealTargetReference.ForExisting(target),
                targetSize.Value),
            $"Partition total size becomes {targetSize.Value} bytes",
            extend ? "No file data loss expected; capacity changes"
                : "Shrinking may make data beyond the new boundary inaccessible"));
    }

    private async Task<long?> PromptRealResizeTargetAsync(
        RealPartitionResizeRange range, bool extend,
        long minimumMib, long maximumMib)
    {
        var input = new TextBox
        {
            Header = Text("目标总容量（MiB，整数）", "Target total capacity (whole MiB)"),
            Text = (extend ? minimumMib : maximumMib).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            MinWidth = 320
        };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"{Text("准确目标", "Exact target")}: {range.Partition.ProviderKey}\n" +
                $"{Text("当前容量", "Current size")}: {range.CurrentSizeBytes} bytes\n" +
                $"{Text("Windows 支持范围", "Windows provider range")}: " +
                $"{range.ProviderMinBytes}–{range.ProviderMaxBytes} bytes\n" +
                $"{Text("几何交集", "Geometry intersection")}: " +
                $"{range.AllowedMinBytes}–{range.AllowedMaxBytes} bytes\n" +
                $"{Text("本方向 1 MiB 目标范围", "Whole-MiB targets in this direction")}: " +
                $"{minimumMib}–{maximumMib} MiB\n" +
                $"{Text("采集时间", "Captured")}: {range.CapturedAtUtc.LocalDateTime:G}\n" +
                $"Target fingerprint: {range.TargetFingerprint}",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(input);
        content.Children.Add(validation);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = extend ? Text("真实扩展分区", "Extend real partition")
                : Text("真实压缩分区", "Shrink real partition"),
            Content = new ScrollViewer { MaxHeight = 500, Content = content },
            PrimaryButtonText = Text("准备 Agent 计划", "Prepare Agent plan"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        long? targetSize = null;
        void Validate()
        {
            if (!long.TryParse(input.Text.Trim(), out var mib) ||
                mib < minimumMib || mib > maximumMib ||
                mib > long.MaxValue / BytesPerMiB)
            {
                targetSize = null;
                validation.Text = Text("目标须在实时 1 MiB 范围内。",
                    "The target must be within the live whole-MiB range.");
            }
            else
            {
                targetSize = mib * BytesPerMiB;
                validation.Text = string.Empty;
            }
            dialog.IsPrimaryButtonEnabled = targetSize is not null;
        }
        input.TextChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            Validate();
            args.Cancel = targetSize is null;
        };
        Validate();
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary
            ? targetSize : null;
    }

    private async Task ResizeAsync(bool extend)
    {
        var partition = SelectedPartition();
        if (partition is null)
        {
            return;
        }

        if (ViewModel.CanSubmitRealOperation)
        {
            await ResizeRealAsync(partition, extend);
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

        if (ViewModel.CanSubmitRealOperation)
        {
            await SubmitRealAsync(RealPartitionIntent(OperationIntent.DeletePartition,
                partition, new DeletePartitionCommand(PartitionReference(partition)),
                "The selected partition is absent",
                "All files and volume data on the selected partition become inaccessible"));
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

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is
            System.ComponentModel.Win32Exception or InvalidOperationException
                or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(
                Text("无法打开", "Cannot open"),
                Text("当前卷的本机路径无法打开。", "The selected volume's local path could not be opened."));
        }
    }

    private async void PartitionActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPartition() is null)
        {
            await CreatePartitionAsync();
            return;
        }

        await FormatPartitionAsync();
    }

    private async Task FormatPartitionAsync()
    {
        var partition = SelectedPartition();
        if (partition is null || IsProtected(partition))
        {
            return;
        }

        var fileSystem = SelectedFileSystemToken();

        if (ViewModel.CanSubmitRealOperation)
        {
            var system = ViewModel.ActiveDocument.SystemId;
            var target = new StorageObjectId(system, StorageObjectKind.Partition,
                partition.StableId);
            RealOperationIntentRequest? proposal;
            try
            {
                proposal = RealOperationProposalFactory.TryFormatExistingData(
                    system, target, fileSystem,
                    checked((int)SelectedClusterBytes()), !QuickFormatSwitch.IsOn,
                    VolumeLabelBox.Text);
            }
            catch (ArgumentException exception)
            {
                await ShowMessageAsync(Text("真实格式化参数不受支持", "Real format parameters are unsupported"),
                    exception.Message);
                return;
            }
            if (proposal is null)
            {
                await ShowMessageAsync(Text("真实文件系统未开放", "Real file system is unavailable"),
                    Text("现有真实数据分区仅开放 64 KiB NTFS/exFAT，或 C01 条件下的 64 KiB 快速 ReFS；未知文件系统保持禁用，不会改用其它格式。",
                        "Existing real data partitions allow 64 KiB NTFS/exFAT or C01's conditional quick ReFS with 64 KiB. Unknown file systems stay disabled and are never changed to another format."));
                return;
            }
            await SubmitRealAsync(proposal);
            return;
        }

        var holdsData = EditWorkspace.PartitionHoldsStoredData(partition);
        var usesRefs = fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        var quick = QuickFormatSwitch.IsOn;
        if (holdsData
            && !await ConfirmAsync(
                ViewModel.Localization["Format"],
                FormatConfirmationText(quick, usesRefs)))
        {
            return;
        }

        if (!holdsData && usesRefs)
        {
            PublishRefsNotice();
        }

        var ok = await SubmitAsync(
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                partition.StableId,
                Name: VolumeLabelBox.Text,
                FileSystem: fileSystem,
                AllocationUnitSize: SelectedClusterBytes(),
                QuickFormat: quick),
            quick ? Text("快速格式化成功", "Quick format succeeded") : Text("完整格式化成功", "Full format succeeded"),
            quick
                ? Text("模拟快速格式化已写入文档；没有格式化真实磁盘。", "The simulated quick format was saved; no real disk was formatted.")
                : Text("模拟完整格式化模式已写入操作计划；没有扫描真实介质。", "The simulated full-format mode was recorded in the operation plan; no real media was scanned."),
            failTitle: Text("格式化失败", "Format failed"));
        _ = ok;
    }

    private string FormatConfirmationText(bool quickFormat, bool usesRefs)
    {
        var modeWarning = quickFormat
            ? Text(
                "模拟快速格式化会清除该分区上的已用数据；不会格式化真实磁盘。",
                "Simulated quick formatting clears used data on this partition; no real disk will be formatted.")
            : Text(
                "模拟完整格式化会清除该分区上的已用数据，并将完整模式写入操作计划；不会扫描真实介质或格式化真实磁盘。",
                "Simulated full formatting clears used data on this partition and records full mode in the operation plan; it does not scan real media or format a real disk.");
        if (!usesRefs)
        {
            return Text($"{modeWarning} 确定继续？", $"{modeWarning} Continue?");
        }

        var refsWarning = Text(
            "ReFS 尚无与 64 KiB NTFS 同等的长期测试证据。",
            "ReFS has no long-run evidence equivalent to 64 KiB NTFS.");
        return Text(
            $"{modeWarning} {refsWarning} 确定继续？",
            $"{modeWarning} {refsWarning} Continue?");
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
