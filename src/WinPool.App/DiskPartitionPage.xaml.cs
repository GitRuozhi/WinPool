using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
using WinPool.App.ViewModels;
using WinPool.App.Services;
using WinPool_App.Controls;
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
    private GridLength _widePropertiesWidth = new(EditorFormMetrics.SidebarWidth);
    private ContentDialog? _resizeDialog;
    private CancellationTokenSource? _resizeCancellation;
    private bool _resizePromptOpen;
    private int _resizePromptVersion;

    public DiskPartitionPage()
    {
        InitializeComponent();
        ApplyPropertyEditorMetrics();
        Unloaded += (_, _) => CancelResizePrompt();
        _formatModeControlsReady = true;
        SetFormatMode(quickFormat: true);
        IntegerTextInput.Attach(SizeBox,
            () => _working is null ? null : SelectedCreateGeometry()?.MaximumSizeBytes / BytesPerMiB,
            fillMaximumOnLostFocus: true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        CancelResizePrompt();
        base.OnNavigatedTo(e);
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            var targetPartition = ViewModel.ActiveSnapshot.Partitions.FirstOrDefault(item =>
                item.StableId.Equals(ViewModel.ActiveSnapshot.ResolvePartitionUnion(parameter.TargetStableId)?.Id ?? parameter.TargetStableId, StringComparison.OrdinalIgnoreCase));
            _selectedPartitionId = targetPartition?.StableId;
            _selectedDiskId = targetPartition?.OsDiskStableId ?? ResolveOsDiskId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected, false, ContextMenuFactory: CreateTopologyContextMenu);
        LocalizeChrome();
        RefreshAll();
    }

    internal void RefreshExecutionMode()
    {
        CancelResizePrompt();
        UpdateButtonState();
    }

    protected override void OnRealOperationActivityChanged(bool isBusy, bool showOverlay, string phase)
    {
        RealTopologyOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
        RealTopologyProgress.IsActive = showOverlay;
        RealTopologyPhase.Text = phase;
        UpdateButtonState();
    }

    protected override void OnRealInventoryRefreshed()
    {
        if (ViewModel.IsUsingSimulatedInventory)
            return;

        _working = ViewModel.ActiveSnapshot;
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
        DiskOnlineStateButtonLabel.Text = Text("联机/脱机", "Online/offline");
        DiskPartitionStyleButtonLabel.Text = Text("分区表状态", "Partition style");
        ClearDiskButtonLabel.Text = Text("清空至 RAW", "Clear to RAW");
        DeletePartitionButtonLabel.Text = Text("删除分区", "Delete partition");
        ExtendButtonLabel.Text = Text("扩展分区", "Extend partition");
        ShrinkButtonLabel.Text = Text("压缩分区", "Shrink partition");
        OpenExplorerButtonLabel.Text = Text("打开资源管理器", "Open in File Explorer");
        DiskLocationLabel.Text = Text("所在磁盘", "Disk");
        StartOffsetLabel.Text = Text("起点-终点", "Start–end");
        EndOffsetLabel.Text = StartOffsetLabel.Text;
        PartitionTypeLabel.Text = Text("分区类型", "Partition type");
        DriveLetterLabel.Text = Text("盘符", "Drive letter");
        VolumeLabelCaption.Text = Text("卷标", "Volume label");
        SizeLabel.Text = Text("容量", "Capacity");
        SizeAdaptiveLabel.Text = SizeLabel.Text;
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
        ToolTipService.SetToolTip(MaximumSizeButton, Text("使用最大容量", "Use maximum capacity"));
        foreach (var button in new[] { UndoDriveLetterButton, UndoVolumeLabelButton })
        {
            AutomationProperties.SetName(button, Text("撤销此项修改", "Undo this property change"));
            ToolTipService.SetToolTip(button, Text("恢复原值", "Restore the original value"));
        }
        foreach (var button in new[] { CommitDriveLetterButton, CommitVolumeLabelButton })
        {
            AutomationProperties.SetName(button, Text("应用此项修改", "Apply this property change"));
            ToolTipService.SetToolTip(button, Text("应用此项修改", "Apply this property change"));
        }
        ContextHelp.Set(DiskOnlineStateButton,
            Text("根据磁盘当前采集到的联机状态提供相反动作；只有真实操作完成且结果核对成功后才更新显示。",
                "Offers the opposite of the collected disk online state; the display changes only after a real operation completes and its result is verified."));
        ContextHelp.Set(DiskPartitionStyleButton,
            Text("RAW 可初始化为 GPT；模拟 MBR 可转换为 GPT；GPT 已初始化后此按钮禁用。真实 MBR 转换尚未支持。",
                "Initialize RAW as GPT, or convert a simulated MBR disk to GPT. GPT is already initialized and disables this button. Real MBR conversion is not supported."));
        ContextHelp.Set(ClearDiskButton,
            Text("独立清除所选磁盘的分区和卷结构至 RAW；不全盘写零、不初始化、不格式化，也不创建池。",
                "Independently clear the selected disk's partition and volume structure to RAW. This does not zero the whole disk, initialize, format, or create a pool."));
        ContextHelp.Set(DeletePartitionButton,
            Text("删除符合条件的非系统、非启动分区；真实删除会使该分区的数据丢失。", "Delete an eligible non-system, non-boot partition; real deletion loses its data."));
        ContextHelp.Set(ExtendButton,
            Text(
                "公式为 A MiB + B MiB = C MiB。A 只读；B 扩展量与 C 目标总容量可双向编辑。第二行显示同一容量的自适应单位；真实范围由 Agent 实时查询。",
                "The formula is A MiB + B MiB = C MiB. A is read-only; B (the amount added) and C (the total target) update each other. The second row shows adaptive units; the Agent reads the live real-operation range."));
        ContextHelp.Set(ShrinkButton,
            Text(
                "公式为 A MiB − B MiB = C MiB。A 只读；B 压缩量与 C 目标总容量可双向编辑。第二行显示同一容量的自适应单位；真实范围由 Agent 实时查询。",
                "The formula is A MiB − B MiB = C MiB. A is read-only; B (the amount removed) and C (the total target) update each other. The second row shows adaptive units; the Agent reads the live real-operation range."));
        ContextHelp.Set(OpenExplorerButton,
            Text("仅打开有本机盘符的现有本机卷。", "Open only an existing local volume with a local drive letter."));
        ContextHelp.Set(PartitionTypeBox,
            Text("在 GPT 未分配空间中选择固定分区类型。", "Choose a fixed partition type in GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择盘符后点击右侧勾号应用；左侧撤销恢复原值。保留分区不能分配盘符。", "Choose a drive letter, then apply it with the check button; undo restores the original value. Reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后点击右侧勾号或按 Enter 应用；左侧撤销恢复原值。新分区使用此名称创建。", "Enter a volume label and apply with the check button or Enter; undo restores the original value. A new partition is created with this name."));
        ContextHelp.Set(SizeBox,
            Text(
                "创建时输入 MiB 正整数；第二行显示同一容量的自适应单位。最大值按选中空隙的 1 MiB 对齐范围计算。",
                "Enter a whole number of MiB while creating; the second line shows the same capacity in an adaptive unit. The maximum uses the selected gap's 1 MiB-aligned range."));
        ContextHelp.Set(MaximumSizeButton,
            Text("将容量填为选中空隙中的最大 1 MiB 对齐容量。", "Fill the largest 1 MiB-aligned capacity available in the selected gap."));
        ContextHelp.Set(FileSystemBox,
            Text("选择格式化文件系统；真实模式仅开放本阶段已验证的组合。", "Choose a format file system; real mode allows only combinations verified for this stage."));
        ContextHelp.Set(ClusterBox,
            Text("选择文件系统的分配单元大小。", "Choose the file-system allocation unit size."));
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
            var hint = Text("恢复此项原值（新建时恢复默认值）", "Restore this property's original value (default for a new partition)");
            ContextHelp.Set(button, hint);
            AutomationProperties.SetName(button, hint);
            ToolTipService.SetToolTip(button, hint);
        }
        ContextHelp.Set(ResetSizeButton,
            Text("恢复新分区的默认容量。", "Restore the new partition’s default capacity."));
        AutomationProperties.SetName(
            ResetSizeButton,
            Text("恢复默认容量", "Restore default capacity"));
        FillFileSystemBox();
        FillClusterBox();
        FillPartitionTypeBox();
    }

    private void ApplyPropertyEditorMetrics()
    {
        PageRootGrid.Margin = new Thickness(
            EditorFormMetrics.PageMargin, 0, EditorFormMetrics.PageMargin, EditorFormMetrics.PageMargin);
        _widePropertiesWidth = new GridLength(EditorFormMetrics.SidebarWidth);
        EditorFormMetrics.ConfigureColumns(PartitionFormGrid, PartitionPropertiesPanel);
        EditorFormMetrics.ConfigureColumns(PartitionSettingsFormGrid, PartitionPropertiesPanel);
        EditorFormMetrics.ConfigurePropertyPane(PropertiesPane, PartitionPropertiesPanel, PropertiesColumn);

        foreach (var label in new[]
        {
            DiskLocationLabel, StartOffsetLabel, EndOffsetLabel, DriveLetterLabel,
            VolumeLabelCaption, PartitionTypeLabel, SizeLabel, SizeAdaptiveLabel,
            FileSystemLabel, ClusterLabel, QuickFormatLabel, FullFormatLabel
        })
        {
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxLines = 2;
        }

        UpdateEditorLayout();
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
        if (!_narrowLayout && PropertiesColumn.Width.IsAbsolute && PropertiesColumn.Width.Value > 0)
            _widePropertiesWidth = PropertiesColumn.Width;
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
        FillClusterChoices("NTFS", 65536, 65536);
    }

    private void FillClusterChoices(
        string? fileSystem, long? preferredBytes, long? defaultBytes, bool preserveUnlistedObservedValue = false)
    {
        var wasFilling = _filling;
        _filling = true;
        try
        {
            ClusterBox.Items.Clear();
            var sizes = new List<int>();
            if (!string.IsNullOrWhiteSpace(fileSystem))
                sizes.AddRange(FormatAllocationUnitPolicy.GetSupportedClusterSizes(fileSystem, _working.Computer.OsBuild));
            if (preserveUnlistedObservedValue && !string.IsNullOrWhiteSpace(fileSystem)
                && preferredBytes is > 0 and <= int.MaxValue
                && !sizes.Contains((int)preferredBytes.Value))
                sizes.Add((int)preferredBytes.Value); // Keep the observed value visible; the real planner still verifies support.
            foreach (var bytes in sizes.OrderBy(value => value))
                ClusterBox.Items.Add(ClusterToken(bytes));

            var selected = preferredBytes is > 0 and <= int.MaxValue
                ? ClusterToken((int)preferredBytes.Value)
                : defaultBytes is > 0 and <= int.MaxValue ? ClusterToken((int)defaultBytes.Value) : null;
            if (selected is not null && ClusterBox.Items.Contains(selected))
                ClusterBox.SelectedItem = selected;
            else if (ClusterBox.Items.Count > 0)
                ClusterBox.SelectedIndex = 0;
        }
        finally
        {
            _filling = wasFilling;
        }
    }

    private static string ClusterToken(int bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes % (1024 * 1024) == 0
            ? $"{bytes / (1024 * 1024)} MiB"
            : $"{bytes / 1024} KiB";

    private static int? ParseClusterToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var value = token.Trim();
        var factor = value.EndsWith(" MiB", StringComparison.OrdinalIgnoreCase) ? 1024 * 1024
            : value.EndsWith(" KiB", StringComparison.OrdinalIgnoreCase) ? 1024
            : value.EndsWith(" B", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (factor == 0) return null;
        var number = value[..^3].Trim();
        if (factor == 1) number = value[..^2].Trim();
        return int.TryParse(number, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var size)
            && size > 0 && (long)size * factor <= int.MaxValue
            ? (int)(size * factor) : null;
    }

    private long DefaultClusterForCurrentRole(string? fileSystem = null)
    {
        var kind = SelectedPartitionKind();
        if (kind is PartitionKind.EfiSystem or PartitionKind.WindowsRecovery) return 4096;
        return FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes(
            string.IsNullOrWhiteSpace(fileSystem) ? "NTFS" : fileSystem);
    }

    private static string RecommendedFileSystem(PartitionKind kind) => kind switch
    {
        PartitionKind.EfiSystem => "FAT32",
        PartitionKind.MicrosoftReserved => string.Empty,
        _ => "NTFS"
    };

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
        CancelResizePrompt();
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

    private MenuFlyout? CreateTopologyContextMenu(TopologyNodeViewModel node)
    {
        var menu = new MenuFlyout();
        void Add(Button source, string label, RoutedEventHandler action, string glyph)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = source.IsEnabled, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => { if (source.IsEnabled) action(source, new RoutedEventArgs()); };
            menu.Items.Add(item);
        }
        if (node.Unit.Kind == StorageUnitKind.OsDisk)
        {
            Add(DiskOnlineStateButton, DiskOnlineStateButtonLabel.Text, DiskOnlineState_Click, DiskOnlineStateButtonIcon.Glyph);
            Add(DiskPartitionStyleButton, DiskPartitionStyleButtonLabel.Text, DiskPartitionStyleButton_Click, DiskPartitionStyleButtonIcon.Glyph);
            Add(ClearDiskButton, ClearDiskButtonLabel.Text, ClearDisk_Click, "\uE74D");
        }
        else if (EditWorkspace.IsUnallocated(node.Unit.StableId))
            Add(PartitionActionButton, PartitionActionButtonLabel.Text, PartitionActionButton_Click, PartitionActionButtonIcon.Glyph);
        else if (node.Unit.Kind == StorageUnitKind.Partition)
        {
            Add(PartitionActionButton, PartitionActionButtonLabel.Text, PartitionActionButton_Click, PartitionActionButtonIcon.Glyph);
            Add(ExtendButton, ExtendButtonLabel.Text, Extend_Click, "\uE8A5");
            Add(ShrinkButton, ShrinkButtonLabel.Text, Shrink_Click, "\uE73F");
            Add(DeletePartitionButton, DeletePartitionButtonLabel.Text, DeletePartition_Click, "\uE74D");
            Add(OpenExplorerButton, OpenExplorerButtonLabel.Text, OpenExplorer_Click, "\uE838");
        }
        return menu.Items.Count > 0 ? menu : null;
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
            IntegerTextInput.Attach(SizeBox,
                () => SelectedCreateGeometry()?.MaximumSizeBytes / BytesPerMiB,
                minimum: gap ? 1 : 0, fillMaximumOnLostFocus: true);
            var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
            DiskLocationValue.Text = disk is null
                ? string.Empty
                : Text($"磁盘 {disk.Number}  {disk.FriendlyName}", $"Disk {disk.Number}  {disk.FriendlyName}");
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
            SetOffsetValues(startOffsetBytes, endOffsetBytes);

            FillFileSystemChoices(partition);
            FillDriveLetters(partition, volume, autoAssign: gap);
            VolumeLabelBox.Text = volume?.FileSystemLabel
                ?? (partition is null ? gap ? DefaultVolumeLabel() : string.Empty : partition.FileSystemLabel);
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
                    SetSizeAdaptiveValue(null);
                }
                FileSystemBox.SelectedIndex = 0;
                FillClusterChoices("NTFS", null, 65536);
                SetFormatMode(quickFormat: true);
                PartitionTypeBox.SelectedIndex = 0;
            }
            else if (partition is not null)
            {
                SizeBox.Text = (partition.Size / BytesPerMiB).ToString(System.Globalization.CultureInfo.InvariantCulture);
                SetSizeAdaptiveValue(partition.Size);
                var currentFileSystem = volume?.FileSystem ?? partition.FileSystem;
                if (string.IsNullOrWhiteSpace(currentFileSystem)
                    || currentFileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase))
                    SelectFileSystem("NTFS");
                else
                    SelectFileSystem(currentFileSystem);
                FillClusterChoices(SelectedFileSystemToken(),
                    volume?.AllocationUnitSize ?? partition.AllocationUnitSize,
                    DefaultClusterForCurrentRole(SelectedFileSystemToken()), preserveUnlistedObservedValue: true);
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
                SetSizeAdaptiveValue(null);
                FileSystemBox.SelectedIndex = 0;
                FillClusterChoices("NTFS", null, 65536);
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
            FileSystemBox.SelectedIndex = -1;
        }
    }

    private PartitionInfo? SelectedPartition() =>
        _working.Partitions.FirstOrDefault(item => item.StableId == _selectedPartitionId);

    private OsDiskInfo? SelectedDisk() =>
        _working.OsDisks.FirstOrDefault(item => item.StableId == _selectedDiskId);

    private bool TryGetDiskOfflineState(OsDiskInfo disk, out bool offline)
    {
        offline = false;
        var facts = ViewModel.ActiveDocument.SourceFacts;
        if (facts is null || facts.SystemId != ViewModel.ActiveDocument.SystemId)
            return false;

        var sources = facts.Objects.Where(item => item.ObjectType == FactObjectType.Disk
            && string.Equals(item.Id, disk.StableId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length != 1)
            return false;

        if (sources[0].Field("IsOffline") is not
                { ReadState: FieldReadState.Returned, Value: { } value }
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        offline = value.GetBoolean();
        return true;
    }

    private bool IsProtectedDiskTarget(OsDiskInfo disk)
    {
        var physical = _working.PhysicalDisks.FirstOrDefault(item =>
            string.Equals(item.StableId, disk.PhysicalDiskStableId, StringComparison.OrdinalIgnoreCase));
        return disk.IsBoot || disk.IsSystem || physical?.IsPageFile == true || physical?.IsCrashDump == true;
    }

    private string? ResolveDiskActionDisabledReason(
        bool simulated,
        bool real,
        OsDiskInfo? disk,
        bool isDiskSelection,
        bool? offlineState,
        bool protectedDisk,
        bool operationBusy,
        bool requireOnline)
    {
        if (operationBusy)
            return Text("另一项磁盘操作仍在执行或等待核对。", "Another disk operation is running or awaiting reconciliation.");
        if (!simulated && !real)
            return LocalReadOnlyReason();
        if (disk is null)
            return Text("请选择一个准确的磁盘。", "Select an exact disk.");
        if (!isDiskSelection)
            return Text("请先选择磁盘本身，而不是分区或未分配空间。",
                "Select the disk itself, not a partition or unallocated space.");
        if (offlineState is null)
            return Text("无法从该磁盘的准确来源读取联机状态。", "The online state is unavailable from this disk's exact source.");
        if (protectedDisk)
            return Text("系统、启动、分页文件或转储磁盘不能在此入口修改联机状态或结构。",
                "System, boot, page-file, and crash-dump disks cannot change online state or structure here.");
        if (requireOnline && offlineState.Value)
            return Text("该磁盘当前脱机；请先联机后执行结构操作。", "The disk is offline; bring it online before changing its structure.");
        return null;
    }

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
        var selected = FileSystemBox.SelectedItem as string ?? string.Empty;
        return selected == Text("不格式化", "Do not format") ? string.Empty : selected;
    }

    private bool IsInitialized(OsDiskInfo? disk) =>
        disk is not null && EditWorkspace.IsPartitionTableInitialized(disk);

    private long SelectedClusterBytes() => ParseClusterToken(ClusterBox.SelectedItem as string)
        ?? DefaultClusterForCurrentRole(SelectedFileSystemToken());

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var real = ViewModel.CanSubmitRealOperation;
        var operationBusy = ViewModel.IsRealOperationBusy
            || ViewModel.RealOperationSubmission.IsBlocked;
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
        var offlineState = disk is not null && TryGetDiskOfflineState(disk, out var collectedOffline)
            ? collectedOffline
            : (bool?)null;
        var diskOffline = offlineState != false;
        var protectedDisk = disk is not null && IsProtectedDiskTarget(disk);
        var createMode = isGapSelection && alreadyGpt;
        var realEditablePartition = ViewModel.CanSubmitRealOperation
            && partition is { IsBoot: false, IsSystem: false, Type: "Primary" or "BasicData" };
        var realDeletablePartition = ViewModel.CanSubmitRealOperation
            && partition is { IsBoot: false, IsSystem: false,
                Type: "Primary" or "BasicData" or "EfiSystem" or "MicrosoftReserved" or "WindowsRecovery" };
        var propertyEnabled = (simulated || (ViewModel.CanSubmitRealOperation && createMode)
            || realEditablePartition)
            && !operationBusy && !diskOffline && (isPartitionSelection || isGapSelection);
        var createGeometry = SelectedCreateGeometry();
        var hasIntegerSize = TryGetSizeBytes(out var requestedSizeBytes);
        var maximumCreateSizeBytes = createGeometry?.MaximumSizeBytes;
        var sizeFitsSelectedGap = hasIntegerSize
            && createGeometry is { CanCreate: true }
            && maximumCreateSizeBytes is long maximumSizeBytes
            && requestedSizeBytes <= maximumSizeBytes;
        var contextReason = ResolveContextDisabledReason(simulated, disk, offlineState, operationBusy);
        var createReason = contextReason
            ?? (!alreadyGpt
                ? Text("新建分区需要已初始化的 GPT 磁盘。", "Creating a partition requires an initialized GPT disk.")
                : !isGapSelection
                    ? Text("请选择 GPT 磁盘上的未分配空间。", "Select unallocated space on a GPT disk.")
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
            disk is { IsBoot: false, IsSystem: false } && !diskOffline && !operationBusy &&
            RealPartitionResizeUiRange.IsSupportedFileSystem(
                volume?.FileSystem ?? partition?.FileSystem);
        var realRefsResize = realResizeCandidate && string.Equals(
            volume?.FileSystem ?? partition?.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
        var canExtend = realResizeCandidate || (simulated && propertyEnabled
            && extendCapability?.Decision.Verdict == StorageRuleVerdict.Allow);
        var canShrink = (realResizeCandidate && !realRefsResize) || (simulated && propertyEnabled
            && shrinkCapability?.Decision.Verdict == StorageRuleVerdict.Allow);

        DiskOnlineStateButtonLabel.Text = disk is null
            ? Text("联机/脱机", "Online/offline")
            : offlineState switch
            {
                true => Text("联机", "Online"),
                false => Text("脱机", "Offline"),
                _ => Text("状态未知", "State unknown")
            };
        DiskOnlineStateButtonIcon.Glyph = offlineState == true ? "\uE8FB" : "\uE8AE";
        DiskPartitionStyleButtonLabel.Text = raw
            ? ViewModel.Localization["InitializeDisk"]
            : mbr
                ? Text("转换为 GPT", "Convert to GPT")
                : alreadyGpt
                    ? Text("已初始化", "Already initialized")
                    : Text("分区表未知", "Partition style unknown");
        DiskPartitionStyleButtonIcon.Glyph = raw ? "\uE9CE" : "\uE8AB";
        ClearDiskButtonLabel.Text = Text("清空至 RAW", "Clear to RAW");

        var stateReason = ResolveDiskActionDisabledReason(
            simulated, real, disk, isDiskSelection, offlineState, protectedDisk, operationBusy,
            requireOnline: false);
        DiskOnlineStateButton.IsEnabled = stateReason is null;
        SetDisabledReason(DiskOnlineStateButton, stateReason);

        var onlineDiskReason = ResolveDiskActionDisabledReason(
            simulated, real, disk, isDiskSelection, offlineState, protectedDisk, operationBusy,
            requireOnline: true);
        var styleActionReason = onlineDiskReason
            ?? (alreadyGpt
                ? Text("该磁盘已经初始化为 GPT；此入口只负责初始化 RAW 或转换模拟 MBR。",
                    "This disk is already initialized as GPT. This button only initializes RAW or converts a simulated MBR disk.")
                : !raw && !mbr
                    ? Text("磁盘分区表状态未知或不受支持。", "The disk partition style is unknown or unsupported.")
                    : mbr && !simulated
                        ? Text("真实 MBR→GPT 转换 C03 尚未支持；不会通过先清盘绕过。",
                            "Real MBR-to-GPT conversion C03 is not supported; clearing the disk is not used as a workaround.")
                        : null);
        DiskPartitionStyleButton.IsEnabled = styleActionReason is null && (raw || (mbr && simulated));
        SetDisabledReason(DiskPartitionStyleButton, styleActionReason);

        var clearDiskReason = onlineDiskReason
            ?? (raw
                ? Text("该磁盘已经是 RAW，无需重复清空。", "This disk is already RAW and does not need clearing.")
                : mbr && real && !simulated
                    ? Text(
                        "真实清空当前仅支持 GPT 普通数据/MSR 分区，或来源事实完整证明分区数为零的 GPT；真实 MBR 清空尚未支持。",
                        "Real clear currently supports GPT disks with ordinary data/MSR partitions, or GPT disks whose complete source facts prove zero partitions; clearing a real MBR disk is not supported.")
                : !alreadyGpt && !mbr
                    ? Text("磁盘分区表状态未知或不受支持。", "The disk partition style is unknown or unsupported.")
                    : null);
        ClearDiskButton.IsEnabled = clearDiskReason is null;
        SetDisabledReason(ClearDiskButton, clearDiskReason);
        DeletePartitionButton.IsEnabled = (simulated || realDeletablePartition)
            && !operationBusy && !diskOffline && destructivePartition;
        ExtendButton.IsEnabled = canExtend;
        ShrinkButton.IsEnabled = canShrink;
        OpenExplorerButton.IsEnabled = !simulated
            && !operationBusy
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
        var realRefsFormat = real
            && SelectedFileSystemToken().Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        var realQuickOnlyRoleCreation = real && createMode
            && (kind == PartitionKind.EfiSystem || kind == PartitionKind.WindowsRecovery);
        QuickFormatSwitch.IsEnabled = formatOptionsEnabled && !realQuickOnlyRoleCreation;
        FullFormatSwitch.IsEnabled = formatOptionsEnabled
            && !realRefsFormat && !realQuickOnlyRoleCreation;
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
                ? Text("请选择一个分区。", "Select a partition.")
                : partition.IsBoot || partition.IsSystem
                    ? Text("系统或启动分区不能删除。", "A system or boot partition cannot be deleted.")
                    : Text("当前选择不满足删除条件。", "The current selection does not meet the deletion requirements."));
        var selectionReason = contextReason;
        var extendEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通数据分区以扩展。", "Select a normal data partition to extend.")
                : ResizeCapabilityReason(extendCapability!, extend: true));
        var shrinkEligibilityReason = contextReason
            ?? (partition is null
                ? Text("请选择普通数据分区以压缩。", "Select a normal data partition to shrink.")
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

        SetDisabledReason(DeletePartitionButton, destructiveReason);
        SetDisabledReason(ExtendButton, extendReason);
        SetDisabledReason(ShrinkButton, shrinkReason);
        SetDisabledReason(OpenExplorerButton,
            contextReason
                ?? (simulated
                ? Text("资源管理器只打开本机卷；模拟系统没有本机路径。", "File Explorer opens local volumes only; simulated systems have no local path.")
                : !isPartitionSelection
                    ? Text("请选择一个本机分区。", "Select a local partition.")
                    : diskOffline
                        ? Text("该磁盘当前脱机。", "The disk is currently offline.")
                        : letter.Length != 1
                            ? Text("当前卷没有可打开的盘符。", "The current volume has no drive letter to open.")
                            : Text("当前本机路径不可用。", "The current local path is unavailable.")));

        SetDisabledReason(PartitionTypeBox, createReason);
        SetDisabledReason(DriveLetterBox,
            selectionReason
            ?? (createMode && kind is PartitionKind.EfiSystem or PartitionKind.MicrosoftReserved or PartitionKind.WindowsRecovery
                ? Text("EFI、MSR 和恢复分区不能分配盘符。", "EFI, MSR, and recovery partitions cannot receive a drive letter.")
                : Text("当前选择没有可改写盘符的卷。", "The current selection has no volume whose drive letter can be changed.")));
        SetDisabledReason(VolumeLabelBox,
            selectionReason
            ?? (createMode && kind == PartitionKind.MicrosoftReserved
                ? Text("Microsoft 保留分区没有卷标。", "A Microsoft Reserved Partition has no volume label.")
                : Text("当前选择没有可改写卷标的卷。", "The current selection has no volume whose label can be changed.")));
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
                        "这里仅供属性查看。扩展/压缩公式保留当前原始 bytes；当前容量不是整 MiB 时不会使用舍入值。",
                        "This field is for property display. Resize formulas preserve the original current bytes and never use a rounded value when it is not a whole MiB.")
                    : Text("请选择未分配空间以设置新分区容量。", "Select unallocated space to set a new partition capacity.")));
        SetDisabledReason(MaximumSizeButton, createReason);
        var formatOptionsReason = contextReason
            ?? (createMode
                ? kind == PartitionKind.MicrosoftReserved
                    ? Text("Microsoft 保留分区不能格式化。", "A Microsoft Reserved Partition cannot be formatted.")
                    : null
                : protectedPartitionReason
                    ?? (partition is null
                        ? Text("请选择可格式化的普通数据分区。", "Select a data partition that can be formatted.")
                        : null));
        var formatReason = contextReason
            ?? (partition is null
                ? Text("请选择现有的普通数据分区以格式化。", "Select an existing data partition to format.")
                : protectedPartitionReason);
        var createActionSelected = partition is null;
        var selectedRealFileSystem = SelectedFileSystemToken();
        var realExistingFormatSupported = !realEditablePartition
            || (selectedRealFileSystem is "NTFS" or "exFAT" or "ReFS"
                && SelectedClusterBytes() <= int.MaxValue
                && FormatAllocationUnitPolicy.IsSupportedClusterSize(
                    selectedRealFileSystem, (int)SelectedClusterBytes(), _working.Computer.OsBuild)
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
            ? canCreatePartition
            : propertyEnabled && isPartitionSelection && formattablePartition
                && realExistingFormatSupported;
        ContextHelp.Set(
            PartitionActionButton,
            createActionSelected
                ? Text("在选中的 GPT 未分配空间中创建分区；真实操作会显示目标空间和分区参数，确认后才提交。", "Create a partition in the selected GPT gap; a real operation shows the target space and partition settings for confirmation before submission.")
                : Text("提交当前分区格式化设置；真实操作会清除目标分区的数据。", "Submit the partition format settings; a real operation erases data on the target partition."));
        SetDisabledReason(PartitionActionButton, createActionSelected ? createReason
            : realExistingFormatSupported ? formatReason
            : Text("当前文件系统与分配单元不在本机已知支持范围内，或 ReFS 未选择快速格式化。",
                "The selected file system and allocation unit are outside the known Windows support range, or ReFS is not using quick format."));
        SetDisabledReason(FileSystemBox, formatOptionsReason);
        SetDisabledReason(ClusterBox, formatOptionsReason);
        SetDisabledReason(QuickFormatSwitch, realQuickOnlyRoleCreation
            ? Text("真实 EFI 和恢复分区仅支持快速格式化。",
                "Real EFI and Recovery partition creation supports quick format only.")
            : formatOptionsReason);
        SetDisabledReason(FullFormatSwitch, realRefsFormat
            ? Text("ReFS 仅支持快速格式化。",
                "ReFS formatting is limited to quick format.")
            : realQuickOnlyRoleCreation
                ? Text("真实 EFI 和恢复分区仅支持快速格式化。",
                    "Real EFI and Recovery partition creation supports quick format only.")
                : formatOptionsReason);
        UpdatePropertyResetState();
        UpdatePropertyCommandState();
    }

    private void UpdatePropertyCommandState()
    {
        if (ViewModel is null) return;
        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        var isNew = _selectedUnallocatedOffset is not null;
        var baselineLabel = volume?.FileSystemLabel ?? (isNew ? DefaultVolumeLabel() : partition?.FileSystemLabel ?? string.Empty);
        var labelChanged = !string.Equals(VolumeLabelBox.Text, baselineLabel, StringComparison.Ordinal);
        var baselineLetter = volume?.DriveLetter ?? (partition is null ? DefaultCreateDriveLetter() : _working.DriveLetterOf(partition));
        var selected = SelectedDriveLetter();
        var letterChanged = !string.Equals(selected, baselineLetter, StringComparison.OrdinalIgnoreCase);
        SetRestoreButtonState(UndoVolumeLabelButton, VolumeLabelBox.IsEnabled && labelChanged);
        CommitVolumeLabelButton.IsEnabled = VolumeLabelBox.IsEnabled && volume is not null && labelChanged;
        SetRestoreButtonState(UndoDriveLetterButton, DriveLetterBox.IsEnabled && letterChanged);
        CommitDriveLetterButton.IsEnabled = DriveLetterBox.IsEnabled && volume is not null && letterChanged;
    }

    private string DefaultCreateDriveLetter()
    {
        var used = UsedDriveLetters(null);
        return ViewModel.CanSubmitRealOperation
            ? "DEFGHIJKLMNOPQRSTUVWXYZ".Select(letter => letter.ToString()).FirstOrDefault(letter => !used.Contains(letter)) ?? string.Empty
            : NextFreeDriveLetter(used);
    }

    private string SelectedDriveLetter() => DriveLetterBox.SelectedItem as string is { } selected
        && selected != Text("无", "None") ? selected : NoneLetterValue;

    private void UndoDriveLetter_Click(object sender, RoutedEventArgs e)
    {
        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        var wasFilling = _filling;
        _filling = true;
        try { FillDriveLetters(partition, volume, autoAssign: _selectedUnallocatedOffset is not null); }
        finally { _filling = wasFilling; }
        UpdatePropertyCommandState();
    }

    private void UndoVolumeLabel_Click(object sender, RoutedEventArgs e)
    {
        var partition = SelectedPartition();
        var volume = partition is null ? null : _working.VolumeForPartition(partition.StableId);
        VolumeLabelBox.Text = volume?.FileSystemLabel
            ?? (_selectedUnallocatedOffset is not null ? DefaultVolumeLabel() : partition?.FileSystemLabel ?? string.Empty);
        UpdatePropertyCommandState();
    }

    private async void CommitDriveLetter_Click(object sender, RoutedEventArgs e) => await CommitDriveLetterAsync();

    private async void CommitVolumeLabel_Click(object sender, RoutedEventArgs e) => await CommitVolumeLabelAsync();

    private void VolumeLabelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling) UpdatePropertyCommandState();
    }

    private void RestoreFieldHelp()
    {
        ContextHelp.Set(PartitionTypeBox,
            Text("在 GPT 未分配空间中选择固定分区类型。", "Choose a fixed partition type in GPT unallocated space."));
        ContextHelp.Set(DriveLetterBox,
            Text("选择盘符后点击右侧勾号应用；左侧撤销恢复原值。保留分区不能分配盘符。", "Choose a drive letter, then apply it with the check button; undo restores the original value. Reserved partitions cannot receive one."));
        ContextHelp.Set(VolumeLabelBox,
            Text("输入卷标后点击右侧勾号或按 Enter 应用；左侧撤销恢复原值。新分区使用此名称创建。", "Enter a volume label and apply with the check button or Enter; undo restores the original value. A new partition is created with this name."));
        ContextHelp.Set(SizeBox,
            SelectedPartition() is null
                ? Text("以 MiB 正整数输入新分区大小；下一行显示自适应单位。", "Enter the new partition size as a whole number of MiB; the next line shows an adaptive unit.")
                : Text(
                    "这里仅供属性查看；扩展/压缩对话框会保留当前原始 bytes，展示 A±B=C 两行公式并校验精确目标。当前容量不是整 MiB 时会停止公式操作，不用舍入值代替。",
                    "This field is for property display. The resize dialog preserves the original current bytes and validates the exact A±B=C formula; a current size that is not a whole MiB stops the formula operation instead of using a rounded value."));
        ContextHelp.Set(MaximumSizeButton,
            Text("将容量填为选中空隙中的最大 1 MiB 对齐容量。", "Fill the largest 1 MiB-aligned capacity available in the selected gap."));
        ContextHelp.Set(FileSystemBox,
            Text("选择格式化文件系统；真实模式仅开放本阶段已验证的组合。", "Choose a format file system; real mode allows only combinations verified for this stage."));
        ContextHelp.Set(ClusterBox,
            Text("选择文件系统的分配单元大小。", "Choose the file-system allocation unit size."));
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

        if (!RealPartitionResizeUiRange.TryGetWholeMibTargets(
                partition.Size,
                minimumBytes,
                maximumBytes,
                extend,
                out minimumMib,
                out maximumMib))
            return false;

        suggestedMib = extend ? maximumMib : minimumMib;
        return true;
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
        bool? offlineState,
        bool operationBusy)
    {
        if (operationBusy)
        {
            return Text("另一项磁盘操作仍在执行或等待核对。", "Another disk operation is running or awaiting reconciliation.");
        }

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

        if (offlineState is null)
        {
            return Text("无法从该磁盘的准确来源读取联机状态。", "The online state is unavailable from this disk's exact source.");
        }

        if (offlineState.Value)
        {
            return Text("磁盘已脱机；请先联机后编辑分区。",
                "The disk is offline; bring it online before editing partitions.");
        }

        return null;
    }

    private string LocalReadOnlyReason()
    {
        if (ViewModel.RealOperationSubmission.IsBlocked)
        {
            return Text("先前真实操作仍在等待最终结果和刷新核对；核对完成前暂不能修改磁盘。",
                "A previous real operation is still awaiting its final result and refreshed inventory. Disk changes are paused until reconciliation completes.");
        }

        if (ViewModel.IsRealOperationBusy)
        {
            return Text("真实存储操作正在执行或刷新核对；请等待完成后再修改磁盘。",
                "A real storage operation is running or refreshing its result. Wait for it to finish before changing the disk.");
        }

        return ViewModel.CanSubmitRealOperation
            ? Text("所选真实操作或目标尚未通过本阶段安全校验，入口保持禁用。",
                "This real operation or target has not passed this stage's safety checks and remains disabled.")
            : Text("本机存储在此页只读；请先取得 Agent 真实模式回执或选择模拟系统。",
                "Local storage is read-only here until the Agent arms real mode, or select a simulation.");
    }

    private string? DescribeFormatPartitionReason(PartitionInfo? partition)
    {
        if (partition is null)
        {
            return null;
        }

        if (partition.IsBoot || partition.IsSystem)
        {
            return ViewModel.IsUsingSimulatedInventory
                ? Text(
                    "系统或启动分区不能格式化；卷标、盘符和符合条件的模拟扩缩仍可编辑。",
                    "A system or boot partition cannot be formatted; label, drive letter, and eligible simulated resize remain editable.")
                : Text("系统或启动分区不能格式化。", "A system or boot partition cannot be formatted.");
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

        var fileSystem = SelectedFileSystemToken();
        var selectedCluster = ParseClusterToken(ClusterBox.SelectedItem as string);
        var preserveCluster = selectedCluster is int bytes && !string.IsNullOrEmpty(fileSystem)
            && FormatAllocationUnitPolicy.IsSupportedClusterSize(fileSystem, bytes, _working.Computer.OsBuild)
            ? selectedCluster : null;
        FillClusterChoices(fileSystem, preserveCluster,
            string.IsNullOrEmpty(fileSystem) ? null : DefaultClusterForCurrentRole(fileSystem));

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
                        FillClusterChoices("FAT32", null, 4096);
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        SetFormatMode(quickFormat: true);
                        break;
                    case PartitionKind.MicrosoftReserved:
                        FillFileSystemBoxFor(string.Empty);
                        FillClusterChoices(null, null, null);
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        VolumeLabelBox.Text = string.Empty;
                        SetFormatMode(quickFormat: true);
                        break;
                    case PartitionKind.WindowsRecovery:
                        FillFileSystemBoxFor("NTFS");
                        FillClusterChoices("NTFS", null, 4096);
                        SetSizeMib(defaultMib);
                        DriveLetterBox.SelectedIndex = 0;
                        SetFormatMode(quickFormat: true);
                        break;
                    default:
                        FillFileSystemBox();
                        FillClusterChoices("NTFS", null, 65536);
                        SetSizeMib(defaultMib);
                        FillDriveLetters(null, null, autoAssign: true);
                        SetFormatMode(quickFormat: true);
                        break;
                }
            }
            if (SelectedPartitionKind() != PartitionKind.MicrosoftReserved
                && string.IsNullOrEmpty(VolumeLabelBox.Text))
                VolumeLabelBox.Text = DefaultVolumeLabel();
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

    private string DefaultVolumeLabel() => Text("新卷", "New Volume");

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
        var parameterEnabled = partition is not null || gap;
        var recommendedSizeMib = RecommendedCreateSizeMib();
        var recommendedFileSystem = RecommendedFileSystem(SelectedPartitionKind());
        var baselineFileSystem = BaselineFileSystem();
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
        var resetFileSystem = fileSystemChanged;
        var recommendedCluster = string.IsNullOrEmpty(SelectedFileSystemToken())
            ? DefaultClusterForCurrentRole(recommendedFileSystem)
            : DefaultClusterForCurrentRole(SelectedFileSystemToken());
        var resetCluster = parameterEnabled && ClusterBox.IsEnabled
            && SelectedClusterBytes() != (gap ? recommendedCluster : baselineCluster ?? recommendedCluster);
        var comparisonCluster = baselineCluster ?? DefaultClusterForCurrentRole(baselineFileSystem);
        var clusterChanged = parameterEnabled
            && ClusterBox.IsEnabled
            && SelectedClusterBytes() != comparisonCluster;
        var quickFormatChanged = parameterEnabled
            && QuickFormatSwitch.IsEnabled
            && !QuickFormatSwitch.IsOn;

        SetPropertyResetState(ResetSizeButton, SizeChangedIndicator, sizeChanged, sizeChanged);
        SetPropertyResetState(ResetFileSystemButton, FileSystemChangedIndicator, resetFileSystem, fileSystemChanged);
        SetPropertyResetState(ResetClusterButton, ClusterChangedIndicator, resetCluster, clusterChanged);
        SetPropertyResetState(ResetQuickFormatButton, QuickFormatChangedIndicator, quickFormatChanged, quickFormatChanged);
    }

    private static void SetPropertyResetState(
        Button button,
        FrameworkElement indicator,
        bool canReset,
        bool changed)
    {
        SetRestoreButtonState(button, canReset);
        indicator.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetRestoreButtonState(Button button, bool changed)
    {
        // Opacity keeps the dedicated column and hit area stable while removing the action.
        button.Opacity = changed ? 1 : 0;
        button.IsEnabled = changed;
        button.IsHitTestVisible = changed;
        button.IsTabStop = changed;
        button.Visibility = Visibility.Visible;
        AutomationProperties.SetAccessibilityView(button, changed
            ? AccessibilityView.Content : AccessibilityView.Raw);
    }

    private string BaselineFileSystem()
    {
        var partition = SelectedPartition();
        var actual = partition is null ? null : _working.VolumeForPartition(partition.StableId)?.FileSystem ?? partition.FileSystem;
        return string.IsNullOrWhiteSpace(actual) || actual.Equals("RAW", StringComparison.OrdinalIgnoreCase)
            ? RecommendedFileSystem(SelectedPartitionKind()) : actual;
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
        var recommended = BaselineFileSystem();
        if (string.IsNullOrEmpty(recommended)) return;
        var currentCluster = ParseClusterToken(ClusterBox.SelectedItem as string);
        var wasFilling = _filling;
        _filling = true;
        try
        {
            FileSystemBox.SelectedItem = recommended;
            var preserveCluster = currentCluster is int bytes
                && FormatAllocationUnitPolicy.IsSupportedClusterSize(recommended, bytes, _working.Computer.OsBuild)
                ? currentCluster : null;
            FillClusterChoices(recommended, preserveCluster,
                DefaultClusterForCurrentRole(recommended));
        }
        finally
        {
            _filling = wasFilling;
        }
        UpdatePropertyResetState();
        UpdateButtonState();
    }

    private void ResetCluster_Click(object sender, RoutedEventArgs e)
    {
        var fileSystem = SelectedFileSystemToken();
        if (string.IsNullOrEmpty(fileSystem)) return;
        var partition = SelectedPartition();
        var actual = partition is null ? null : _working.VolumeForPartition(partition.StableId)?.AllocationUnitSize ?? partition.AllocationUnitSize;
        var baseline = actual ?? DefaultClusterForCurrentRole(fileSystem);
        FillClusterChoices(fileSystem, baseline, DefaultClusterForCurrentRole(fileSystem), preserveUnlistedObservedValue: true);
        UpdatePropertyResetState();
        UpdateButtonState();
    }

    private void ResetQuickFormat_Click(object sender, RoutedEventArgs e)
    {
        SetFormatMode(quickFormat: true);
        UpdatePropertyResetState();
        UpdateButtonState();
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
        SetSizeAdaptiveValue(TryGetSizeBytes(out var sizeBytes) ? sizeBytes : null);
    }

    private void SetSizeAdaptiveValue(long? bytes)
    {
        var text = bytes is long size ? TopologyProjector.FormatBytes(size) : string.Empty;
        var separator = text.LastIndexOf(' ');
        SizeAdaptiveValue.Text = separator < 0 ? text : text[..separator];
        SizeAdaptiveUnit.Text = separator < 0 ? string.Empty : text[(separator + 1)..];
    }

    private void SetOffsetValues(long? startBytes, long? endBytes)
    {
        var culture = OffsetDisplayCulture();
        SetOffsetRangeValue(startBytes, endBytes, OffsetMibRangeValue, culture, FormatOffsetMib);
        SetOffsetRangeValue(startBytes, endBytes, OffsetAdaptiveRangeValue, culture, FormatAdaptiveBytes);
    }

    private static void SetOffsetRangeValue(
        long? startBytes,
        long? endBytes,
        TextBlock target,
        System.Globalization.CultureInfo culture,
        Func<long, System.Globalization.CultureInfo, string> format)
    {
        var hasValue = startBytes.HasValue || endBytes.HasValue;
        target.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!hasValue)
        {
            target.Text = string.Empty;
            return;
        }

        var start = startBytes is long startValue ? format(startValue, culture) : string.Empty;
        var end = endBytes is long endValue ? format(endValue, culture) : string.Empty;
        target.Text = startBytes.HasValue && endBytes.HasValue
            ? $"{start} – {end}"
            : startBytes.HasValue ? start : end;
    }

    private static string FormatOffsetMib(long bytes, System.Globalization.CultureInfo culture) =>
        $"{(bytes / BytesPerMiB).ToString("N0", culture)} MiB";

    private System.Globalization.CultureInfo OffsetDisplayCulture() =>
        System.Globalization.CultureInfo.GetCultureInfo(
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "zh-CN" : "en-US");

    private static string FormatExactMib(long bytes, System.Globalization.CultureInfo culture)
    {
        var whole = Math.Abs(bytes / BytesPerMiB);
        var remainder = Math.Abs(bytes % BytesPerMiB);
        var sign = bytes < 0 ? culture.NumberFormat.NegativeSign : string.Empty;
        var value = whole.ToString("N0", culture);
        if (remainder == 0)
            return sign + value;

        // MiB has a denominator of 2^20, so every byte value has an exact
        // finite decimal representation with at most 20 fractional digits.
        var fraction = ((decimal)remainder * 95_367_431_640_625m)
            .ToString("00000000000000000000", System.Globalization.CultureInfo.InvariantCulture)
            .TrimEnd('0');
        return sign + value + culture.NumberFormat.NumberDecimalSeparator + fraction;
    }

    private static string FormatAdaptiveBytes(long bytes, System.Globalization.CultureInfo culture)
    {
        if (bytes <= 0)
            return "0 B";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value.ToString("0.##", culture)} {units[index]}";
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

    private void DriveLetterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling) UpdatePropertyCommandState();
    }

    private async Task CommitDriveLetterAsync()
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

        var next = SelectedDriveLetter();
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
            var target = new RealDriveLetterSelectionTarget(ViewModel.ActiveDocument.SystemId,
                partition.StableId, volume.StableId,
                RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(_working, partition.StableId));
            var previousResponse = LastRealOperationResponse;
            var succeeded = await SubmitRealPropertyAsync(RealPartitionIntent(OperationIntent.SetDriveLetter,
                partition, new SetDriveLetterCommand(PartitionReference(partition),
                    previous, requested),
                $"Drive letter changes from {previous?.ToString() ?? "none"} to {requested?.ToString() ?? "none"}",
                "No partition data loss expected"));
            var currentPartition = SelectedPartition();
            var currentVolume = currentPartition is null
                ? null : _working.VolumeForPartition(currentPartition.StableId);
            if (ViewModel.CanSubmitRealOperation && currentPartition is not null && currentVolume is not null
                && RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
                    LastRealOperationResponse, succeeded, LastRealInventoryRefreshSucceeded,
                    ViewModel.RealOperationSubmission.IsBlocked, _filling, target,
                    new RealDriveLetterSelectionTarget(ViewModel.ActiveDocument.SystemId,
                        currentPartition.StableId, currentVolume.StableId,
                        RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(_working,
                            currentPartition.StableId)), previousResponse))
            {
                var wasFilling = _filling;
                _filling = true;
                try
                {
                    FillDriveLetters(currentPartition, currentVolume, autoAssign: false);
                }
                finally
                {
                    _filling = wasFilling;
                }
                UpdateButtonState();
            }
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
                await SubmitRealPropertyAsync(RealOneTargetIntent(OperationIntent.SetVolumeLabel,
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

    private async void DiskOnlineState_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null
            || _selectedPartitionId is not null
            || _selectedUnallocatedOffset is not null
            || ViewModel.IsRealOperationBusy
            || ViewModel.RealOperationSubmission.IsBlocked
            || IsProtectedDiskTarget(disk)
            || !TryGetDiskOfflineState(disk, out var offline))
        {
            return;
        }

        var online = offline;
        if (ViewModel.CanSubmitRealOperation)
        {
            await SubmitRealAsync(RealDiskIntent(OperationIntent.SetDiskOnlineState,
                disk, new SetDiskOnlineCommand(DiskReference(disk), online),
                online ? "Disk online" : "Disk offline",
                online ? "No partition data loss expected" : "Running access to this disk stops"));
            return;
        }

        if (!ViewModel.IsUsingSimulatedInventory)
            return;

        await SubmitAsync(
            new SimulationEditRequest(
                SimulationEditKind.SetDiskOffline,
                disk.StableId,
                Offline: !online),
            online ? Text("已联机", "Disk online") : Text("已脱机", "Disk offline"),
            online
                ? Text("磁盘联机状态已写入模拟文档。", "The disk online state was saved to the simulation.")
                : Text("磁盘脱机状态已写入模拟文档，所有修改入口已锁定。", "The disk offline state was saved to the simulation and all edit actions are locked."));
    }

    private async void DiskPartitionStyleButton_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null
            || _selectedPartitionId is not null
            || _selectedUnallocatedOffset is not null
            || ViewModel.IsRealOperationBusy
            || ViewModel.RealOperationSubmission.IsBlocked
            || IsProtectedDiskTarget(disk)
            || !TryGetDiskOfflineState(disk, out var offline)
            || offline)
            return;

        var system = ViewModel.ActiveDocument.SystemId;
        if (string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            if (ViewModel.CanSubmitRealOperation)
            {
                await SubmitInitializeDiskAndLayoutAsync(disk,
                    ViewModel.CurrentPreferences.CreateMsrOnInitialize);
                return;
            }

            if (!ViewModel.IsUsingSimulatedInventory)
                return;

            await SubmitAsync(
                new SimulationEditRequest(
                    SimulationEditKind.InitializeDisk,
                    disk.StableId,
                    PartitionStyle: "GPT",
                    CreateMsr: ViewModel.CurrentPreferences.CreateMsrOnInitialize),
                Text("初始化成功", "Initialization succeeded"),
                Text("磁盘已初始化为 GPT。", "The disk was initialized as GPT."));
            return;
        }

        if (!string.Equals(disk.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase))
            return;

        if (ViewModel.CanSubmitRealOperation)
        {
            await ShowMessageAsync(Text("真实 MBR 转换尚未支持", "Real MBR conversion is not supported"),
                Text("真实 MBR→GPT 转换 C03 尚未开放；不会通过先清空磁盘绕过此限制。",
                    "Real MBR-to-GPT conversion C03 is not supported. Clearing the disk is not used to bypass this restriction."));
            return;
        }

        if (ViewModel.IsUsingSimulatedInventory)
            await ConvertMbrToGptSimulationAsync(disk);
    }

    private async Task ConvertMbrToGptSimulationAsync(OsDiskInfo disk)
    {
        var partitions = _working.Partitions.Where(item =>
            string.Equals(item.OsDiskStableId, disk.StableId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if ((partitions.Length > 0 || DiskHoldsStoredData(disk))
            && !await ConfirmAsync(
                Text("转换含分区的模拟磁盘", "Convert simulated disk with partitions"),
                SimulatedDiskDataLossDetails(disk)
                    + Environment.NewLine
                    + Text("转换会清除分区和已用数据。", "Conversion removes the partitions and stored data.")))
            return;

        await SubmitAsync(
            new SimulationEditRequest(SimulationEditKind.ConvertDisk, disk.StableId, PartitionStyle: "GPT"),
            Text("转换成功", "Conversion succeeded"),
            Text("模拟磁盘已转换为 GPT。", "The simulated disk was converted to GPT."));
    }

    private async void ClearDisk_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk is null
            || _selectedPartitionId is not null
            || _selectedUnallocatedOffset is not null
            || ViewModel.IsRealOperationBusy
            || ViewModel.RealOperationSubmission.IsBlocked
            || IsProtectedDiskTarget(disk)
            || !TryGetDiskOfflineState(disk, out var offline)
            || offline
            || string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase)
            || (ViewModel.CanSubmitRealOperation
                && !ViewModel.IsUsingSimulatedInventory
                && string.Equals(disk.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase))
            || (!string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(disk.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase)))
            return;

        if (ViewModel.CanSubmitRealOperation)
        {
            var target = new StorageObjectId(ViewModel.ActiveDocument.SystemId,
                StorageObjectKind.OsDisk, disk.StableId);
            await SubmitRealAsync(RealOperationProposalFactory.ClearToRaw(
                ViewModel.ActiveDocument.SystemId, target));
            return;
        }

        if (!ViewModel.IsUsingSimulatedInventory)
            return;

        var details = SimulatedDiskDataLossDetails(disk)
            + Environment.NewLine
            + Text("此操作只更新模拟事实，清除该模拟磁盘的分区和关联卷并将分区表置为 RAW；不会修改真实磁盘。",
                "This updates simulated facts only. It removes this simulated disk's partitions and related volumes and sets its style to RAW; no real disk is changed.");
        if (!await ConfirmAsync(Text("清空模拟磁盘至 RAW", "Clear simulated disk to RAW"), details))
            return;

        await SubmitAsync(
            new SimulationEditRequest(SimulationEditKind.ClearDisk, disk.StableId),
            Text("模拟磁盘已清空", "Simulated disk cleared"),
            Text("模拟磁盘已变为 RAW；未执行初始化、格式化或建池。",
                "The simulated disk is now RAW. No initialization, formatting, or pool creation was performed."));
    }

    private string SimulatedDiskDataLossDetails(OsDiskInfo disk)
    {
        var partitions = _working.Partitions.Where(item =>
            string.Equals(item.OsDiskStableId, disk.StableId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Offset)
            .ToArray();
        var partitionIds = partitions.Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var volumes = _working.Volumes.Where(item =>
            item.PartitionStableId is not null && partitionIds.Contains(item.PartitionStableId))
            .OrderBy(item => item.StableId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var partitionText = partitions.Length == 0
            ? Text("(none reported)", "（未发现分区）")
            : string.Join(Environment.NewLine, partitions.Select(item =>
                $"#{item.PartitionNumber} {item.Type} offset={item.Offset} size={item.Size} " +
                $"letter={(string.IsNullOrWhiteSpace(item.DriveLetter) ? "none" : item.DriveLetter + ":")} " +
                $"fileSystem={item.FileSystem} label={item.FileSystemLabel} id={item.StableId}"));
        var volumeText = volumes.Length == 0
            ? Text("(none reported)", "（未发现关联卷）")
            : string.Join(Environment.NewLine, volumes.Select(item =>
                $"id={item.StableId} partition={item.PartitionStableId} fileSystem={item.FileSystem} " +
                $"label={item.FileSystemLabel} size={item.Size} free={item.SizeRemaining} " +
                $"allocationUnit={item.AllocationUnitSize?.ToString() ?? "unknown"} " +
                $"accessPaths={string.Join(", ", item.AccessPaths)}"));
        return string.Join(Environment.NewLine,
            $"OS disk: id={disk.StableId}; number={disk.Number}; style={disk.PartitionStyle}; size={disk.Size} bytes",
            $"Physical disk: {disk.PhysicalDiskStableId ?? "unresolved"}",
            Text("分区及盘符：", "Partitions and drive letters:"),
            partitionText,
            Text("关联卷及访问路径：", "Related volumes and access paths:"),
            volumeText,
            Text("上述分区、卷、文件和盘符将从模拟结构中移除。", "The listed partitions, volumes, files, and drive letters will be removed from the simulated structure."));
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

    private async Task<(long SizeBytes, bool Extend)?> PromptResizeTargetAsync(
        PartitionInfo partition, bool extend)
    {
        (long Minimum, long Maximum)? GetBounds(bool direction)
        {
            var capability = StorageEditRules.GetPartitionResizeCapability(_working,
                partition.StableId, direction ? SimulationEditKind.ExtendPartition : SimulationEditKind.ShrinkPartition);
            return TryGetResizeTargetRange(partition, capability, direction,
                out var minimum, out var maximum, out _) ? (minimum, maximum) : null;
        }
        var extendBounds = GetBounds(true);
        var shrinkBounds = GetBounds(false);
        return await PromptResizeFormulaAsync(partition.Size, extendBounds, shrinkBounds,
            extend, Text("模拟分区扩缩", "Resize simulated partition"),
            Text("模拟范围按当前几何与已用数据估算。", "The simulated range models current geometry and used data."));
    }

    private sealed record ResizePromptRange(
        long CurrentSizeBytes,
        (long Minimum, long Maximum)? ExtendBounds,
        (long Minimum, long Maximum)? ShrinkBounds,
        string Details);

    private void CancelResizePrompt()
    {
        _resizePromptVersion++;
        _resizeCancellation?.Cancel();
        _resizeDialog?.Hide();
    }

    private async Task<(long SizeBytes, bool Extend)?> PromptResizeFormulaAsync(
        long currentSizeBytes,
        (long Minimum, long Maximum)? extendBounds,
        (long Minimum, long Maximum)? shrinkBounds,
        bool initialExtend, string title, string rangeDetails,
        Func<CancellationToken, Task<ResizePromptRange?>>? loadRange = null)
    {
        var cancellation = new CancellationTokenSource();
        var promptVersion = _resizePromptVersion;
        _resizeCancellation = cancellation;
        var loading = loadRange is not null;
        var extend = loading ? initialExtend : initialExtend ? extendBounds is not null : shrinkBounds is null;
        var currentMib = currentSizeBytes / BytesPerMiB;
        var direction = new ToggleSwitch
        {
            Header = Text("扩展 / 压缩", "Extend / shrink"),
            OnContent = Text("扩展", "Extend"), OffContent = Text("压缩", "Shrink"),
            IsOn = extend, IsEnabled = extendBounds is not null && shrinkBounds is not null
        };
        AutomationProperties.SetName(direction, Text("扩缩方向", "Resize direction"));
        var currentInput = new TextBox { IsReadOnly = true, Text = currentMib.ToString(System.Globalization.CultureInfo.InvariantCulture), MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        var deltaInput = new TextBox { MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        var targetInput = new TextBox { MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        AutomationProperties.SetName(currentInput, Text("当前容量（MiB）", "Current size (MiB)"));
        AutomationProperties.SetName(deltaInput, Text("扩缩量（MiB）", "Resize amount (MiB)"));
        AutomationProperties.SetName(targetInput, Text("结果容量（MiB）", "Result size (MiB)"));
        var deltaMaximum = new Button { Content = "MAX", MinWidth = 0, Height = 32, Padding = new Thickness(2, 0, 2, 0) };
        var targetExtreme = new Button { Content = "MAX", MinWidth = 0, Height = 32, Padding = new Thickness(2, 0, 2, 0) };
        var currentAdaptive = new TextBox { IsReadOnly = true, MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        var deltaAdaptive = new TextBox { IsReadOnly = true, MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        var targetAdaptive = new TextBox { IsReadOnly = true, MinWidth = 0, Height = 32, TextAlignment = TextAlignment.Right };
        var currentUnit = new TextBlock();
        var deltaUnit = new TextBlock();
        var targetUnit = new TextBlock();
        var firstOperator = CreateFormulaOperator(extend ? "+" : "−");
        var secondOperator = CreateFormulaOperator(extend ? "+" : "−");
        var formula = new Grid { Width = 750, ColumnSpacing = 6, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var width in new[] { 226d, 24d, 226d, 24d, 226d })
            formula.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        formula.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        formula.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        AddResizeFormulaCell(formula, CreateResizeInputGroup(currentInput, new TextBlock { Text = "MiB" }), 0, 0);
        AddResizeFormulaCell(formula, firstOperator, 1, 0);
        AddResizeFormulaCell(formula, CreateResizeInputGroup(deltaInput, new TextBlock { Text = "MiB" }, deltaMaximum), 2, 0);
        AddResizeFormulaCell(formula, CreateFormulaOperator("="), 3, 0);
        AddResizeFormulaCell(formula, CreateResizeInputGroup(targetInput, new TextBlock { Text = "MiB" }, targetExtreme), 4, 0);
        AddResizeFormulaCell(formula, CreateResizeInputGroup(currentAdaptive, currentUnit), 0, 1);
        AddResizeFormulaCell(formula, secondOperator, 1, 1);
        AddResizeFormulaCell(formula, CreateResizeInputGroup(deltaAdaptive, deltaUnit), 2, 1);
        AddResizeFormulaCell(formula, CreateFormulaOperator("="), 3, 1);
        AddResizeFormulaCell(formula, CreateResizeInputGroup(targetAdaptive, targetUnit), 4, 1);
        var formulaHost = new ContentControl { Content = formula, IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var maximumDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 24 };
        var content = new StackPanel { Spacing = 8, Width = 750 };
        var loadingRing = new ProgressRing { Width = 32, Height = 32, IsActive = loading, Visibility = loading ? Visibility.Visible : Visibility.Collapsed };
        content.Children.Add(loadingRing);
        content.Children.Add(direction);
        content.Children.Add(maximumDetails);
        content.Children.Add(formulaHost);
        content.Children.Add(validation);
        var rangeDescription = new TextBlock { Text = rangeDetails, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(rangeDescription);
        var roundingNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(roundingNote);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title,
            Content = new ScrollViewer { MaxHeight = 560, Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = Text("扩展", "Extend"), CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 850d;
        _resizeDialog = dialog;
        long? authoritativeTargetBytes = null;
        var updatingInputs = false;
        var lastEditedIsDelta = true;
        (long Minimum, long Maximum) Bounds() => (extend ? extendBounds : shrinkBounds) ?? (1, 1);
        long MaximumDeltaMib() => Math.Max(0, extend ? Bounds().Maximum - currentMib : currentMib - Bounds().Minimum);
        IntegerTextInput.Attach(deltaInput, () => MaximumDeltaMib(), minimum: 0, fillMaximumOnLostFocus: true);
        IntegerTextInput.Attach(targetInput, () => Bounds().Maximum, fillMaximumOnLostFocus: true);

        void SetAdaptive(TextBox input, TextBlock unit, long? bytes)
        {
            var value = bytes is long amount ? TopologyProjector.FormatBytes(amount) : string.Empty;
            var separator = value.LastIndexOf(' ');
            input.Text = separator < 0 ? value : value[..separator];
            unit.Text = separator < 0 ? string.Empty : value[(separator + 1)..];
        }
        SetAdaptive(currentAdaptive, currentUnit, currentSizeBytes);
        void Validate(bool editedDelta)
        {
            if (updatingInputs || loading) return;
            lastEditedIsDelta = editedDelta;
            authoritativeTargetBytes = null;
            var input = editedDelta ? deltaInput : targetInput;
            if (long.TryParse(input.Text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var inputMib)
                && RealPartitionResizeUiRange.TryClampResizeFormula(currentSizeBytes, inputMib, editedDelta,
                    extend, Bounds().Minimum, Bounds().Maximum, out var targetBytes, out var deltaBytes))
            {
                authoritativeTargetBytes = targetBytes;
                updatingInputs = true;
                deltaInput.Text = Math.Abs(targetBytes / BytesPerMiB - currentMib).ToString(System.Globalization.CultureInfo.InvariantCulture);
                targetInput.Text = (targetBytes / BytesPerMiB).ToString(System.Globalization.CultureInfo.InvariantCulture);
                updatingInputs = false;
                SetAdaptive(deltaAdaptive, deltaUnit, deltaBytes);
                SetAdaptive(targetAdaptive, targetUnit, targetBytes);
            }
            else
            {
                SetAdaptive(deltaAdaptive, deltaUnit, null);
                SetAdaptive(targetAdaptive, targetUnit, null);
            }
            validation.Text = authoritativeTargetBytes is null
                ? Text("请输入 MiB 正整数。", "Enter a positive whole number of MiB.") : string.Empty;
            dialog.IsPrimaryButtonEnabled = authoritativeTargetBytes is not null;
        }
        void UseExtreme()
        {
            updatingInputs = true;
            targetInput.Text = (extend ? Bounds().Maximum : Bounds().Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture);
            updatingInputs = false;
            Validate(false);
        }
        void RefreshDirection()
        {
            direction.IsEnabled = !loading && extendBounds is not null && shrinkBounds is not null;
            formulaHost.IsEnabled = !loading && (extend ? extendBounds : shrinkBounds) is not null;
            roundingNote.Text = RealPartitionResizeUiRange.IsWholeMib(currentSizeBytes) ? string.Empty
                : Text("首行当前容量向下显示为整数 MiB；第二行保留实际容量。实际变化量与首行显示值可能相差不足 1 MiB；提交使用结果容量的准确整数 MiB。",
                    "The first row rounds the current size down to whole MiB; the second row preserves the actual capacity. The actual change may differ by less than 1 MiB; the exact whole-MiB result size is submitted.");
            if (loading)
            {
                maximumDetails.Text = Text("正在读取 Windows 支持范围…", "Reading the Windows supported range…");
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            if (extendBounds is null && shrinkBounds is null)
            {
                maximumDetails.Text = Text("当前没有可用的整 MiB 扩展或压缩目标。", "There is no supported whole-MiB extension or shrink target.");
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            extend = direction.IsOn;
            formulaHost.IsEnabled = (extend ? extendBounds : shrinkBounds) is not null;
            firstOperator.Text = secondOperator.Text = extend ? "+" : "−";
            dialog.PrimaryButtonText = extend ? Text("扩展", "Extend") : Text("压缩", "Shrink");
            targetExtreme.Content = extend ? "MAX" : "MIN";
            var targetHint = extend ? Text("使用最大结果容量", "Use maximum result size") : Text("使用最小结果容量", "Use minimum result size");
            AutomationProperties.SetName(targetExtreme, targetHint);
            ToolTipService.SetToolTip(targetExtreme, targetHint);
            var deltaHint = extend ? Text("使用最大扩展量", "Use maximum extension amount") : Text("使用最大压缩量", "Use maximum shrink amount");
            AutomationProperties.SetName(deltaMaximum, deltaHint);
            ToolTipService.SetToolTip(deltaMaximum, deltaHint);
            string MaximumText((long Minimum, long Maximum)? bounds, bool growing) => bounds is { } available
                ? $"{(growing ? available.Maximum - currentMib : currentMib - available.Minimum):N0} MiB"
                : Text("不可用", "Unavailable");
            maximumDetails.Text = Text($"最大扩展量：{MaximumText(extendBounds, true)}；最大压缩量：{MaximumText(shrinkBounds, false)}。",
                $"Maximum extension: {MaximumText(extendBounds, true)}; maximum shrink: {MaximumText(shrinkBounds, false)}.")
                + (direction.IsEnabled ? string.Empty : Text(" 当前仅支持所示方向。", " Only the displayed direction is currently supported."));
            UseExtreme();
            IntegerTextInput.RefreshBounds(deltaInput);
            IntegerTextInput.RefreshBounds(targetInput);
        }
        deltaInput.TextChanged += (_, _) => Validate(true);
        targetInput.TextChanged += (_, _) => Validate(false);
        deltaMaximum.Click += (_, _) => UseExtreme();
        targetExtreme.Click += (_, _) => UseExtreme();
        direction.Toggled += (_, _) => RefreshDirection();
        dialog.PrimaryButtonClick += (_, args) => { Validate(lastEditedIsDelta); args.Cancel = authoritativeTargetBytes is null; };
        dialog.Opened += async (_, _) =>
        {
            if (cancellation.IsCancellationRequested || promptVersion != _resizePromptVersion || !IsLoaded)
            {
                dialog.Hide();
                return;
            }
            if (loadRange is null) return;
            try
            {
                var loaded = await loadRange(cancellation.Token);
                if (cancellation.IsCancellationRequested || promptVersion != _resizePromptVersion || !IsLoaded) return;
                if (loaded is not null)
                {
                    currentSizeBytes = loaded.CurrentSizeBytes;
                    currentMib = currentSizeBytes / BytesPerMiB;
                    currentInput.Text = currentMib.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    SetAdaptive(currentAdaptive, currentUnit, currentSizeBytes);
                    extendBounds = loaded.ExtendBounds;
                    shrinkBounds = loaded.ShrinkBounds;
                    rangeDescription.Text = loaded.Details;
                }
                else rangeDescription.Text = Text("实时支持范围读取失败；未提交扩缩操作。", "The live range could not be read; no resize was submitted.");
                loading = false;
                loadingRing.IsActive = false;
                loadingRing.Visibility = Visibility.Collapsed;
                direction.IsOn = initialExtend ? extendBounds is not null : shrinkBounds is null;
                RefreshDirection();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (cancellation.IsCancellationRequested || promptVersion != _resizePromptVersion || !IsLoaded) return;
                loading = false;
                loadingRing.IsActive = false;
                loadingRing.Visibility = Visibility.Collapsed;
                rangeDescription.Text = Text("实时支持范围读取失败；未提交扩缩操作。", "The live range could not be read; no resize was submitted.");
                RefreshDirection();
                PublishOperationException(Text("实时扩缩范围读取失败", "Live resize range failed"),
                    "real", exception, "real.resize.range_query_failed");
            }
        };
        dialog.Closed += (_, _) => cancellation.Cancel();
        RefreshDirection();
        try
        {
            var result = await DialogCoordinator.ShowAsync(dialog);
            // Closed cancels pending range reads, so selection/page ownership is checked separately.
            return result == ContentDialogResult.Primary && promptVersion == _resizePromptVersion
                && IsLoaded && authoritativeTargetBytes is long bytes
                ? (bytes, extend) : null;
        }
        finally
        {
            cancellation.Cancel();
            if (ReferenceEquals(_resizeDialog, dialog)) _resizeDialog = null;
            if (ReferenceEquals(_resizeCancellation, cancellation)) _resizeCancellation = null;
            cancellation.Dispose();
        }
    }

    private static FrameworkElement CreateResizeInputGroup(TextBox input, TextBlock unit, Button? extreme = null)
    {
        var group = new Grid { ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        group.Children.Add(input);
        unit.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(unit, 1);
        group.Children.Add(unit);
        if (extreme is not null)
        {
            Grid.SetColumn(extreme, 2);
            group.Children.Add(extreme);
        }
        return group;
    }

    private static TextBlock CreateFormulaOperator(string value) => new()
    {
        Text = value, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static void AddResizeFormulaCell(Grid grid, FrameworkElement element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private async Task<ResizePromptRange?> ReadRealResizeRangeAsync(
        PartitionInfo partition, StorageObjectId target, CancellationToken cancellationToken)
    {
        ApplicationResult<AgentResponse> queried;
        try
        {
            queried = await ViewModel.AgentConnection!.SendAsync(
                new QueryAgentRealPartitionResizeRangeRequest(target,
                    ViewModel.RealProductSessionId, CorrelationId.New()), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
                PublishOperationException(Text("实时扩缩范围读取失败", "Live resize range failed"),
                    "real", exception, "real.resize.range_query_failed");
            return null;
        }
        if (cancellationToken.IsCancellationRequested) return null;
        if (!queried.IsSuccess)
        {
            PublishOperationResult(queried.Status, queried.Messages, queried.CorrelationId,
                Text("实时扩缩范围不可用", "Live resize range unavailable"), "real");
            return null;
        }
        if (queried.Value is not AgentRealPartitionResizeRangeResponse response || response.Range.Partition != target)
            return null;
        var range = response.Range;
        (long Minimum, long Maximum)? GetBounds(bool growing) =>
            RealPartitionResizeUiRange.TryGetWholeMibTargets(range, growing, out var minimum, out var maximum)
                ? (minimum, maximum) : null;
        var culture = OffsetDisplayCulture();
        var targetName = RealOperationConfirmationFormatter.FormatTarget(target,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn,
            ViewModel.ActiveDocument.SourceFacts);
        var details =
            $"{Text("目标分区", "Target partition")}: {targetName}\n" +
            $"{Text("当前容量", "Current size")}: {FormatExactMib(range.CurrentSizeBytes, culture)} MiB ({range.CurrentSizeBytes.ToString("N0", culture)} bytes)\n" +
            $"{Text("Windows 支持范围", "Windows supported range")}: " +
            $"{FormatExactMib(range.ProviderMinBytes, culture)}–{FormatExactMib(range.ProviderMaxBytes, culture)} MiB\n" +
            $"{Text("几何交集", "Geometry intersection")}: " +
            $"{FormatExactMib(range.AllowedMinBytes, culture)}–{FormatExactMib(range.AllowedMaxBytes, culture)} MiB";
        return new ResizePromptRange(range.CurrentSizeBytes, GetBounds(true), GetBounds(false), details);
    }

    private async Task ResizeRealAsync(PartitionInfo partition, bool extend)
    {
        if (ViewModel.AgentConnection is null) return;
        var system = ViewModel.ActiveDocument.SystemId;
        var target = new StorageObjectId(system, StorageObjectKind.Partition, partition.StableId);
        var targetSize = await PromptResizeFormulaAsync(partition.Size, null, null, extend,
            Text("分区扩缩", "Resize partition"), string.Empty,
            cancellationToken => ReadRealResizeRangeAsync(partition, target, cancellationToken));
        if (targetSize is null || ViewModel.ActiveDocument.SystemId != system
            || SelectedPartition()?.StableId != partition.StableId || !ViewModel.CanSubmitRealOperation)
            return;
        await SubmitRealAsync(RealPartitionIntent(OperationIntent.ResizePartition,
            partition, new ResizePartitionCommand(RealTargetReference.ForExisting(target), targetSize.Value.SizeBytes),
            $"Partition total size becomes {targetSize.Value.SizeBytes} bytes",
            targetSize.Value.Extend ? "No file data loss expected; capacity changes"
                : "Shrinking may make data beyond the new boundary inaccessible"));
    }

    private async Task ResizeAsync(bool extend)
    {
        if (_resizePromptOpen) return;
        _resizePromptOpen = true;
        try { await ResizeCoreAsync(extend); }
        finally { _resizePromptOpen = false; }
    }

    private async Task ResizeCoreAsync(bool extend)
    {
        if (ViewModel.IsRealOperationBusy || ViewModel.RealOperationSubmission.IsBlocked)
            return;

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

        var system = ViewModel.ActiveDocument.SystemId;
        var targetSize = await PromptResizeTargetAsync(partition, extend);
        if (targetSize is null || ViewModel.ActiveDocument.SystemId != system
            || SelectedPartition()?.StableId != partition.StableId || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        extend = targetSize.Value.Extend;
        action = extend ? SimulationEditKind.ExtendPartition : SimulationEditKind.ShrinkPartition;
        var request = new SimulationEditRequest(action, partition.StableId, SizeBytes: targetSize.Value.SizeBytes);
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
        if (ViewModel.IsRealOperationBusy || ViewModel.RealOperationSubmission.IsBlocked)
            return;

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
                    Text("当前文件系统与分配单元组合不受支持；请选择 Windows 支持的组合。ReFS 仅支持快速格式化。",
                        "This file-system and allocation-unit combination is unsupported; select a Windows-supported combination. ReFS is limited to quick format."));
                return;
            }
            await SubmitRealAsync(proposal);
            return;
        }

        var holdsData = EditWorkspace.PartitionHoldsStoredData(partition);
        var quick = QuickFormatSwitch.IsOn;
        if (holdsData
            && !await ConfirmAsync(
                ViewModel.Localization["Format"],
                FormatConfirmationText(quick)))
        {
            return;
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
                : Text("模拟完整格式化已完成；没有格式化真实磁盘。", "Simulated full formatting completed; no real disk was formatted."),
            failTitle: Text("格式化失败", "Format failed"));
        _ = ok;
    }

    private string FormatConfirmationText(bool quickFormat)
    {
        var modeWarning = quickFormat
            ? Text(
                "模拟快速格式化会清除该分区上的已用数据；不会格式化真实磁盘。",
                "Simulated quick formatting clears used data on this partition; no real disk will be formatted.")
            : Text(
                "模拟完整格式化会清除该分区上的已用数据；不会格式化真实磁盘。",
                "Simulated full formatting clears used data on this partition; it does not format a real disk.");
        return Text($"{modeWarning} 确定继续？", $"{modeWarning} Continue?");
    }

    private async Task<bool> SubmitAsync(
        SimulationEditRequest request,
        string successTitle,
        string successMessage,
        string? failTitle = null)
    {
        if (ViewModel.IsRealOperationBusy || ViewModel.RealOperationSubmission.IsBlocked)
            return false;

        using var activity = BeginPageActivity(Text("正在应用分区修改", "Applying partition changes"));
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
            _working = ViewModel.ActiveSnapshot;
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

        _working = ViewModel.ActiveSnapshot;
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
