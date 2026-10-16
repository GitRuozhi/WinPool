using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool_App.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using SimulationEditKind = WinPool.Application.SimulationEditKind;
using SimulationEditRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Storage structure editor: left pool topology, bottom-left wrapping
/// structure operations, and a right-hand property card that sizes to its
/// grouped, independently collapsible pool / tier / disk-and-partition cards.
/// Simulation editing and controlled real structure editing.
/// </summary>
public sealed partial class StorageStructurePage : EditorPageBase
{
    private string? _selectedPoolId;
    private string? _selectedPoolDiskId;
    private string? _selectedPoolVdiskId;
    private string? _selectedPoolTierId;
    private TopologyEditInteraction _interaction = null!;
    private bool _formBuilt;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;
    private bool _filling;
    private bool _applyingRealDraft;
    private RealStructureDraftPreview? _realDraftPreview;
    private bool CanEditDraft => EditingSession.SystemId == ViewModel.SelectedSystem.SystemId
        && (ViewModel.IsUsingSimulatedInventory || ViewModel.CanSubmitRealOperation)
        && !_applyingRealDraft && !IsRealStructureApplyInProgress && !_renameInProgress
        && !_outcomeUnknown && !EditingSession.RealResultSynchronizationPending && !EditingSession.HasBaselineConflict
        && !ViewModel.IsRealOperationBusy;

    private bool _formDirty;
    private string _lastPoolName = string.Empty;
    private string _lastVirtualDiskName = string.Empty;
    private readonly HashSet<string> _capacityDefaultModesInitialized = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TextBox, string> _programmaticCapacityText = [];
    private readonly HashSet<TextBox> _userCapacityEdits = [];
    private readonly Dictionary<string, StructureColumnSuggestion> _columnSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToggleSwitch ShowHotSpareSwitch = new() { MinWidth = 0, OffContent = "", OnContent = "" };
    private readonly ToggleSwitch ShowRetiredSwitch = new() { MinWidth = 0, OffContent = "", OnContent = "" };
    private bool _virtualDiskActionDeletes;
    private bool _narrowLayout;
    private bool _showPropertiesInNarrowLayout;
    private GridLength _widePropertiesWidth = new(380);

    private Stack<EditorDraftState> _undoStack => EditingSession.UndoStack;
    private Stack<EditorDraftState> _redoStack => EditingSession.RedoStack;
    private HashSet<string> _maximumSizeFields => EditingSession.MaximumSizeFields;
    private Dictionary<string, PoolEditIntent> _poolIntents => EditingSession.PoolIntents;
    private SimulationDraftPlan? _currentPlan { get => EditingSession.CurrentPlan; set => EditingSession.CurrentPlan = value; }
    private string _planBuildError { get => EditingSession.PlanBuildError; set => EditingSession.PlanBuildError = value; }
    private bool _outcomeUnknown { get => EditingSession.OutcomeUnknown; set => EditingSession.OutcomeUnknown = value; }
    private bool _renameInProgress { get => EditingSession.RenameInProgress; set => EditingSession.RenameInProgress = value; }

    private sealed record TierFields(
        string Media,
        string TitleKey,
        ComboBox ResiliencyBox,
        ComboBox InterleaveBox,
        TextBox SizeBox,
        Button MaximumButton,
        NumberBox CopiesBox,
        NumberBox FailuresBox,
        NumberBox ColumnsBox,
        TextBox DiskCountBox,
        ComboBox ProvisioningBox,
        List<FrameworkElement> Rows,
        List<int> RowIndices)
    {
        public TextBox FullStripeDisplay { get; } = new()
        {
            IsReadOnly = true, IsTabStop = false, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        public Border Card { get; set; } = null!;
        public Grid FormGrid { get; set; } = null!;
        public TextBox MaximumDisplay { get; } = new()
        {
            IsReadOnly = true,
            IsTabStop = false,
            TextAlignment = TextAlignment.Right,
            Height = 32,
            MinWidth = 0,
            Visibility = Visibility.Collapsed
        };
    }

    /// <summary>Reset affordances shown only while a field differs from the
    /// committed state.</summary>
    private sealed record FieldReset(
        Button ResetButton,
        FrameworkElement ChangeIndicator,
        Func<bool> IsChanged);

    private sealed record NameActionFields(TextBox Input, Button Undo, Button Check);

    private readonly List<FieldReset> _fieldResets = [];
    private readonly List<NameActionFields> _nameActions = [];
    private readonly TextBox _poolNameBox = new();
    private readonly TextBox _virtualDiskNameBox = new();
    private readonly TextBox _volumeNameBox = new();
    private readonly ComboBox _partitionStyleBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _fileSystemBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _clusterBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _multiVdiskWarning = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
    };

    private TierFields Performance { get; set; } = null!;
    private TierFields Capacity { get; set; } = null!;
    private TierFields Dedicated { get; set; } = null!;

    private static NumberBox CreateNumberField(double minimum = double.NaN, double maximum = double.NaN)
    {
        var box = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            SmallChange = 1,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
        };
        box.Loaded += (_, _) => AlignNumericText(box);
        // A NaN bound is "unset", never an actual Minimum/Maximum value:
        // NumberBox coerces Value into [Minimum, Maximum], and a NaN
        // maximum corrupts that range and clamps every value to Minimum
        // (the capacity field showed 0 no matter what was set).
        if (!double.IsNaN(minimum))
        {
            box.Minimum = minimum;
        }

        if (!double.IsNaN(maximum))
        {
            box.Maximum = maximum;
        }

        return box;
    }

    private static void AlignNumericText(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox text) text.TextAlignment = TextAlignment.Right;
            else AlignNumericText(child);
        }
    }

    private static TierFields CreateTierFields(string media, string titleKey) =>
        new(
            media,
            titleKey,
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Right },
            new Button
            {
                Style = (Style)Application.Current.Resources["WinPoolCompactIconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE74E", FontSize = 14 }
            },
            CreateNumberField(minimum: 1, maximum: 16),
            CreateNumberField(minimum: 0, maximum: 16),
            CreateNumberField(minimum: 1, maximum: 64),
            new TextBox { IsReadOnly = true },
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            [],
            []);

    public StorageStructurePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Performance = CreateTierFields("SSD", "PerformanceTier");
        Capacity = CreateTierFields("HDD", "CapacityTier");
        Dedicated = CreateTierFields("SCM", "DedicatedTier");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        string? targetStableId = null;
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            targetStableId = parameter.TargetStableId;
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        var restoredForm = RestoreNavigationDraft(targetStableId);
        _interaction = new TopologyEditInteraction(
            IsTopologyNodeSelected,
            OnTopologySelected,
            CanEditDraft,
            OnDiskDropped,
            () => CanEditDraft,
            CanDropDisk,
            ContextMenuFactory: CreateTopologyContextMenu, CanCreatePoolNow: CanCreatePoolNow);
        LocalizeChrome();
        EnsureForm();
        RefreshAll();
        if (restoredForm is not null) RestoreFormInputs(restoredForm);
        UpdateButtonState();
    }

    internal void RefreshExecutionMode()
    {
        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected,
            CanEditDraft, OnDiskDropped, () => CanEditDraft, CanDropDisk,
            ContextMenuFactory: CreateTopologyContextMenu, CanCreatePoolNow: CanCreatePoolNow);
        RefreshTopology();
        UpdateButtonState();
    }

    protected override void OnRealOperationActivityChanged(bool isBusy, bool showOverlay, string phase)
    {
        RealTopologyOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
        RealTopologyProgress.IsActive = showOverlay;
        RealTopologyPhase.Text = phase;
        if (isBusy) { RefreshTopology(); UpdateButtonState(); }
        else RefreshExecutionMode();
    }

    protected override void OnRealInventoryRefreshed()
    {
        if (ViewModel.IsUsingSimulatedInventory || EditingSession.SystemId != ViewModel.SelectedSystem.SystemId)
            return;

        if (_applyingRealDraft || IsRealStructureApplyInProgress || _renameInProgress
            || EditingSession.RealResultSynchronizationPending)
            return;
        if (HasUncommittedChanges())
        {
            EditingSession.MarkBaselineConflict(ViewModel.SelectedSystem);
            UpdateButtonState();
            return;
        }

        var priorPool = SelectedPool();
        var priorMemberIds = priorPool?.MemberPhysicalDiskIds.ToArray();
        var priorSelectedDiskId = _selectedPoolDiskId;
        var fresh = ViewModel.EffectiveActiveSnapshot;
        EditingSession.Bind(ViewModel.SelectedSystem, fresh);
        NormalizeSelection();

        if (_selectedPoolId is null && priorMemberIds is { Length: > 0 })
        {
            var replacementPools = _working.StoragePools.Where(pool =>
                !pool.IsPrimordial && SamePhysicalMemberSet(pool.MemberPhysicalDiskIds, priorMemberIds)).ToArray();
            if (replacementPools.Length == 1)
            {
                _selectedPoolId = replacementPools[0].StableId;
                _selectedPoolDiskId = priorSelectedDiskId is not null
                    && replacementPools[0].MemberPhysicalDiskIds.Contains(priorSelectedDiskId, StringComparer.OrdinalIgnoreCase)
                    ? priorSelectedDiskId : null;
            }
            else if (priorSelectedDiskId is not null && _working.PhysicalDisks.SingleOrDefault(disk =>
                         disk.StableId.Equals(priorSelectedDiskId, StringComparison.OrdinalIgnoreCase)) is { } disk)
            {
                _selectedPoolDiskId = disk.StableId;
                _selectedPoolId = disk.PoolStableId
                    ?? _working.StoragePools.FirstOrDefault(item => item.IsPrimordial)?.StableId;
            }
        }

        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    protected override bool OnRecoveredRealOperation(AgentRealOperationResponse response, bool inventoryRefreshed)
    {
        // The workspace maps exact owned stages before it notifies any view.
        // A terminal with a pending stage is not safe to release yet.
        if (ViewModel.RealStructureResults.Owns(response))
        {
            UpdateButtonState();
            return false;
        }
        var selectedDraftVd = EditWorkspace.IsDraftVirtualDisk(_selectedPoolVdiskId ?? string.Empty);
        var selectedPoolWasRemoved = _selectedPoolId is not null
            && !_working.StoragePools.Any(pool => pool.StableId == _selectedPoolId);
        NormalizeSelection();
        var physicalTargets = response.Plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (selectedPoolWasRemoved && physicalTargets.Length == 1)
            RestoreRealSelection(physicalTargets[0].ProviderKey);
        if (selectedDraftVd && _selectedPoolId is { } poolId
            && _poolIntents.TryGetValue(poolId, out var intent))
            _selectedPoolVdiskId = intent.VerifiedVirtualDiskId;
        RefreshAll();
        return true;
    }

    private bool HasRoleDisks(string poolId, string usage) =>
        _working.PhysicalDisks.Any(disk =>
            string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && (usage == "Retired" ? disk.IsRetired : disk.IsHotSpare));

    private void LocalizeChrome()
    {
        ShowTargetsButton.Content = Text("目标与操作", "Targets and actions");
        ShowPropertiesButton.Content = Text("属性", "Properties");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UndoButton, ViewModel.Localization["Undo"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RedoButton, ViewModel.Localization["Redo"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiscardAllButton, ViewModel.Localization["DiscardAll"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ApplyAllButton, ViewModel.Localization["ApplyAll"]);
        DissolveButtonLabel.Text = Text("解散选中池", "Dissolve selected pool");
        RetireButtonLabel.Text = Text("退役", "Retire");
        HotSpareButtonLabel.Text = Text("热备", "Hot spare");
        CreateVdiskButtonLabel.Text = Text("创建虚拟磁盘", "Create virtual disk");
        UndoButtonLabel.Text = ViewModel.Localization["Undo"];
        RedoButtonLabel.Text = ViewModel.Localization["Redo"];
        DiscardButtonLabel.Text = ViewModel.Localization["DiscardAll"];
        ApplyButtonLabel.Text = ViewModel.Localization["ApplyAll"];
        foreach (var localize in _localizeForm) localize();
        ApplyFormMetrics();
        _multiVdiskWarning.Text = ViewModel.Localization["MultipleVirtualDiskWarning"];
        _volumeNameBox.PlaceholderText = ViewModel.Localization["VolumeName"];

        ContextHelp.Set(UndoButton, Text("撤销最近一项未应用的修改。", "Undo the most recent unapplied change."));
        ContextHelp.Set(RedoButton, Text("恢复最近撤销的修改。", "Redo the most recently undone change."));
        ContextHelp.Set(DiscardAllButton, Text("放弃所有未应用的修改。", "Discard all unapplied changes."));
        ContextHelp.Set(ApplyAllButton, ViewModel.IsUsingSimulatedInventory
            ? Text("确认风险后将待处理结构修改写入模拟系统。", "Write pending structural changes to the simulated system after confirming risks.")
            : Text("确认本次准确目标与全部结构变更后应用同一真实草稿。", "Confirm the exact target and all structural changes, then apply the same real draft."));
        ContextHelp.Set(DissolveButton, Text("解散选中的存储池；真实删除会使其关联数据丢失。", "Dissolve the selected storage pool; real deletion loses its associated data."));
        ContextHelp.Set(RetireButton, Text("将选中的模拟池成员标为已退役。", "Mark the selected simulated pool member as retired."));
        ContextHelp.Set(HotSpareButton, Text("将选中的模拟池成员标为热备。", "Mark the selected simulated pool member as a hot spare."));
        ContextHelp.Set(CreateVdiskButton, Text("为符合条件的存储池创建待应用的虚拟磁盘草稿；点击应用时核对并确认全部变更。", "Create a pending virtual disk draft for an eligible pool; review and confirm all changes when applying."));
        ContextHelp.Set(ShowHotSpareSwitch, Text("显示或隐藏热备层。", "Show or hide the hot-spare layer."));
        ContextHelp.Set(ShowRetiredSwitch, Text("显示或隐藏已退役层。", "Show or hide the retired layer."));

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
        StructureChromeBorder.Visibility = showTargets ? Visibility.Visible : Visibility.Collapsed;
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

    private void EnsureForm()
    {
        if (_formBuilt)
        {
            return;
        }

        _formBuilt = true;
        foreach (var (control, id) in new (DependencyObject Control, string Id)[]
        {
            (_poolNameBox, "PoolNameBox"),
            (_virtualDiskNameBox, "VirtualDiskNameBox"),
            (_volumeNameBox, "StructureVolumeNameBox"),
            (_partitionStyleBox, "StructurePartitionStyleBox"),
            (_fileSystemBox, "StructureFileSystemBox"),
            (_clusterBox, "StructureClusterSizeBox")
        })
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(control, id);
        foreach (var group in TierGroups())
        {
            foreach (var (control, suffix) in new (DependencyObject Control, string Suffix)[]
            {
                (group.ResiliencyBox, "ResiliencyBox"),
                (group.InterleaveBox, "InterleaveBox"),
                (group.SizeBox, "SizeBox"),
                (group.MaximumButton, "MaximumButton"),
                (group.CopiesBox, "CopiesBox"),
                (group.FailuresBox, "FailuresBox"),
                (group.ColumnsBox, "ColumnsBox"),
                (group.DiskCountBox, "DiskCountBox"),
                (group.ProvisioningBox, "ProvisioningBox")
            })
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(control, $"Structure{group.Media}{suffix}");
        }
        ConfigureFormGrid(PoolFormGrid);
        ConfigureFormGrid(DiskAndPartitionFormGrid);
        FillCombo(_partitionStyleBox, ["GPT", "MBR"], 0);
        FillCombo(_fileSystemBox, ["NTFS", "ReFS"], 0);
        RefreshClusterChoices();
        foreach (var group in TierGroups())
        {
            FillCombo(group.ResiliencyBox, ["Simple", "Mirror", "Parity"], 1);
            FillCombo(group.InterleaveBox, ["16 KiB", "32 KiB", "64 KiB", "128 KiB", "256 KiB"], 2);
            FillCombo(group.ProvisioningBox, ["Fixed"], 0);
            ContextHelp.Set(
                group.MaximumButton,
                Text("设置整数 GiB 最大值，并保留内部 MAX 搜索意图。",
                    "Set the whole-GiB maximum and preserve the internal MAX search intent."));
            group.MaximumButton.Width = EditorFormMetrics.MaximumButtonWidth;
            group.MaximumButton.MinWidth = EditorFormMetrics.MaximumButtonWidth;
            group.MaximumButton.Padding = new Thickness(0, 4, 0, 4);
            group.MaximumButton.Content = new TextBlock { Text = "MAX" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                group.MaximumButton,
                ViewModel.Localization["UseMaximumSize"]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(group.MaximumDisplay,
                $"Structure{group.Media}MaximumDisplay");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(group.MaximumDisplay, "MAX");
            group.MaximumButton.Click += (_, _) => SetMaximumSize(group);
            group.MaximumDisplay.AddHandler(UIElement.PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
                {
                    if (!group.SizeBox.IsEnabled) return;
                    BeginExplicitCapacityEdit(group);
                    group.SizeBox.Focus(FocusState.Programmatic);
                    group.SizeBox.SelectAll();
                    args.Handled = true;
                }), true);
            group.SizeBox.GotFocus += (_, _) =>
            {
                if (!_filling && group.SizeBox.IsEnabled
                    && _maximumSizeFields.Contains(MaximumKey(group)))
                    BeginExplicitCapacityEdit(group);
            };
        }

        foreach (var group in TierGroups())
        {
            HookFormField(group.ResiliencyBox, isCombo: true);
            HookFormField(group.InterleaveBox, isCombo: true);
            HookFormField(group.ProvisioningBox, isCombo: true);
            HookFormField(group.CopiesBox);
            HookFormField(group.FailuresBox);
            HookFormField(group.ColumnsBox);
            IntegerTextInput.Attach(group.SizeBox,
                () =>
                {
                    var maximum = CapacityMaximumIntegerGiB(group);
                    return maximum > 0 ? maximum : null;
                },
                minimum: 1,
                fillMaximumOnLostFocus: true);
            IntegerTextInput.Attach(group.CopiesBox);
            IntegerTextInput.Attach(group.FailuresBox);
            IntegerTextInput.Attach(group.ColumnsBox);
            group.SizeBox.BeforeTextChanging += (_, args) =>
            {
                if (_programmaticCapacityText.TryGetValue(group.SizeBox, out var expected)
                    && args.NewText.Equals(expected, StringComparison.Ordinal)
                    && !args.NewText.Equals(group.SizeBox.Text, StringComparison.Ordinal))
                    return;
                _programmaticCapacityText.Remove(group.SizeBox);
                if (IntegerInputPolicy.ContainsOnlyDigits(args.NewText))
                {
                    if (args.NewText.Equals(group.SizeBox.Text, StringComparison.Ordinal))
                    {
                        SwitchCapacityToExplicit(group);
                        _formDirty = true;
                        UpdateButtonState();
                    }
                    else
                    {
                        _userCapacityEdits.Add(group.SizeBox);
                    }
                }
            };
            HookFormField(group.SizeBox);
            HookEnterCommit(group.SizeBox);
            HookEnterCommit(group.CopiesBox);
            HookEnterCommit(group.FailuresBox);
            HookEnterCommit(group.ColumnsBox);
        }

        HookFormField(_partitionStyleBox, isCombo: true);
        HookFormField(_fileSystemBox, isCombo: true);
        HookFormField(_clusterBox, isCombo: true);
        HookNameField(_poolNameBox);
        HookNameField(_virtualDiskNameBox);
        HookNameField(_volumeNameBox);

        ContextHelp.Set(_poolNameBox, Text("新对象名称即时更新草稿；现有对象按 Enter 提交改名。", "Names for new objects update the draft immediately; press Enter to rename an existing object."));
        ContextHelp.Set(_virtualDiskNameBox, Text("输入名称后点击“检查”直接提交此名称；Enter 也可提交。", "Click Check to submit this name directly; Enter also submits it."));
        ContextHelp.Set(_volumeNameBox, Text("输入卷标后点击“检查”直接提交此名称；Enter 也可提交。", "Click Check to submit this label directly; Enter also submits it."));
        ContextHelp.Set(_partitionStyleBox, Text("选择新分区表样式；真实模式仅开放本阶段已验证的组合。", "Choose the new partition-table style; real mode allows only combinations verified for this stage."));
        ContextHelp.Set(_fileSystemBox, Text("选择卷文件系统。", "Choose the volume file system."));
        ContextHelp.Set(_clusterBox, Text("选择文件系统分配单元大小。", "Choose the file-system allocation unit size."));
        foreach (var group in TierGroups())
        {
            ContextHelp.Set(group.SizeBox, Text("以 GiB 输入层大小；合法输入即时更新草稿，按 Enter 规范化。", "Enter the tier size in GiB; valid input updates the draft immediately. Press Enter to normalize."));
            ContextHelp.Set(group.ResiliencyBox, Text("选择支持的复原类型。", "Choose a supported resiliency type."));
            ContextHelp.Set(group.InterleaveBox, Text("选择条带单元大小。", "Choose the stripe unit size."));
            ContextHelp.Set(group.CopiesBox, Text("镜像层的数据副本数；按 Enter 规范化。", "Data-copy count for a mirror tier; press Enter to normalize."));
            ContextHelp.Set(group.FailuresBox, Text("奇偶校验层可容忍的物理磁盘故障数；按 Enter 规范化。", "Physical-disk failures tolerated by a parity tier; press Enter to normalize."));
            ContextHelp.Set(group.ColumnsBox, Text("列数须符合数据盘数和副本数；按 Enter 规范化。", "Columns must fit the data disk and copy counts; press Enter to normalize."));
        }

        var row = AddSectionHeader(PoolFormGrid, 0, "PoolPropertiesSection");
        row = AddNameFormRow(PoolFormGrid, row, "PoolName", _poolNameBox);
        row = AddNameFormRow(PoolFormGrid, row, "VirtualDiskName", _virtualDiskNameBox);
        row = AddNameFormRow(PoolFormGrid, row, "VolumeName", _volumeNameBox);
        row = AddFormRow(PoolFormGrid, row, "ShowHotSpareLayer", ShowHotSpareSwitch);
        row = AddFormRow(PoolFormGrid, row, "ShowRetiredLayer", ShowRetiredSwitch);
        ShowHotSpareSwitch.Toggled += ShowHotSpareSwitch_Toggled;
        ShowRetiredSwitch.Toggled += ShowRetiredSwitch_Toggled;

        var diskCardIndex = PoolSectionsPanel.Children.IndexOf(DiskAndPartitionCard);
        foreach (var group in TierGroups())
        {
            group.FormGrid = CreateFormGrid();
            group.Card = new Border
            {
                Style = (Style)Application.Current.Resources["WinPoolCardBorderStyle"],
                Padding = new Thickness(12),
                Child = group.FormGrid
            };
            PoolSectionsPanel.Children.Insert(diskCardIndex++, group.Card);
            AddTierGroup(group);
        }

        row = AddSectionHeader(DiskAndPartitionFormGrid, 0, "DiskAndPartitionSection");
        row = AddPartitionRow(DiskAndPartitionFormGrid, row, "PartitionTableStyle", _partitionStyleBox, PartitionStyleChanged, () => ResetPartitionField("PartitionStyle"));
        row = AddPartitionRow(DiskAndPartitionFormGrid, row, "FileSystem", _fileSystemBox, FileSystemChanged, () => ResetPartitionField("FileSystem"));
        row = AddPartitionRow(DiskAndPartitionFormGrid, row, "AllocationUnit", _clusterBox, AllocationUnitChanged, () => ResetPartitionField("AllocationUnit"));

        DiskAndPartitionFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _multiVdiskWarning.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(_multiVdiskWarning, row);
        Grid.SetColumn(_multiVdiskWarning, 0);
        Grid.SetColumnSpan(_multiVdiskWarning, 5);
        DiskAndPartitionFormGrid.Children.Add(_multiVdiskWarning);
        ApplyFormMetrics();
    }

    private void UpdateAutomaticCreationChrome()
    {
        if (_virtualDiskActionDeletes) return;
        ContextHelp.Set(CreateVdiskButton, Text("为符合条件的存储池创建虚拟磁盘和分区。",
            "Create a virtual disk and partition for an eligible pool."));
    }

    private void HookNameField(TextBox box)
    {
        box.TextChanged += (_, _) =>
        {
            if (_filling)
            {
                return;
            }

            var pool = SelectedPool();
            if (ReferenceEquals(box, _poolNameBox) && pool is not null && EditWorkspace.IsDraftPool(pool.StableId))
            {
                var previous = _lastPoolName;
                if (_virtualDiskNameBox.Text == previous || _virtualDiskNameBox.Text == pool.FriendlyName)
                {
                    _filling = true;
                    _virtualDiskNameBox.Text = _poolNameBox.Text;
                    _lastVirtualDiskName = _virtualDiskNameBox.Text;
                    if (_volumeNameBox.Text == previous || _volumeNameBox.Text == pool.FriendlyName)
                        _volumeNameBox.Text = _poolNameBox.Text;
                    _filling = false;
                }
                _lastPoolName = _poolNameBox.Text;
            }
            var selectedVdisk = pool is null ? null : _working.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            var draftTarget = pool is not null
                && (EditWorkspace.IsDraftPool(pool.StableId)
                    || ReferenceEquals(box, _virtualDiskNameBox)
                        && selectedVdisk is not null
                        && EditWorkspace.IsDraftVirtualDisk(selectedVdisk.StableId)
                    || ReferenceEquals(box, _volumeNameBox)
                        && (EditWorkspace.IsDraftPool(pool.StableId)
                            || selectedVdisk is not null
                                && EditWorkspace.IsDraftVirtualDisk(selectedVdisk.StableId)));
            if (draftTarget)
            {
                if (ReferenceEquals(box, _virtualDiskNameBox))
                {
                    if (_volumeNameBox.Text == _lastVirtualDiskName)
                    {
                        _filling = true;
                        _volumeNameBox.Text = _virtualDiskNameBox.Text;
                        _filling = false;
                    }
                    _lastVirtualDiskName = _virtualDiskNameBox.Text;
                }
                _formDirty = true;
            }

            UpdateButtonState();
        };
        box.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            await CommitNameAsync(box);
        };
    }

    /// <summary>Order of tier groups on the property form: dedicated (SCM /
    /// Optane) first, then performance, then capacity.</summary>
    private IEnumerable<TierFields> TierGroups()
    {
        yield return Dedicated;
        yield return Performance;
        yield return Capacity;
    }

    /// <summary>Marks the form dirty and refreshes buttons and field dots.</summary>
    private void HookFormField(Control control, bool isCombo = false)
    {
        switch (control)
        {
            case TextBox box:
                box.TextChanged += (_, _) =>
                {
                    if (!_filling && control.IsEnabled && SelectedPool() is { IsPrimordial: false })
                    {
                        var sizeGroup = TierGroups().FirstOrDefault(group => ReferenceEquals(group.SizeBox, box));
                        if (sizeGroup is not null)
                        {
                            if (_programmaticCapacityText.TryGetValue(box, out var expected)
                                && box.Text.Equals(expected, StringComparison.Ordinal))
                            {
                                _programmaticCapacityText.Remove(box);
                                return;
                            }

                            // The integer input normalizer can raise a second
                            // TextChanged after clamping. Only the user's
                            // original text edit changes MAX into explicit size.
                            if (!_userCapacityEdits.Remove(box))
                                return;

                            SwitchCapacityToExplicit(sizeGroup);
                        }
                        _formDirty = true;
                        UpdateButtonState();
                    }
                };
                break;
            case NumberBox number:
                HookLiveNumberInput(number);
                number.ValueChanged += (_, _) =>
                {
                    if (!_filling && control.IsEnabled && SelectedPool() is { IsPrimordial: false })
                    {
                        MarkExplicitColumns(number);
                        var tierGroup = TierGroups().FirstOrDefault(group =>
                            ReferenceEquals(group.CopiesBox, number)
                            || ReferenceEquals(group.FailuresBox, number));
                        if (tierGroup is not null)
                        {
                            var choice = ColumnChoice(tierGroup);
                            var resiliency = tierGroup.ResiliencyBox.SelectedItem as string ?? "Simple";
                            if (NumValue(number) is { } chosen)
                                _columnSuggestions[ColumnSuggestionKey(tierGroup)] = ReferenceEquals(tierGroup.CopiesBox, number)
                                    ? choice.ChooseCopies(resiliency, (int)chosen)
                                    : choice.ChooseFailures(resiliency, (int)chosen);
                            _filling = true;
                            try { LinkResiliency(tierGroup); }
                            finally { _filling = false; }
                        }
                        _formDirty = true;
                        UpdateButtonState();
                    }
                };
                break;
            case ComboBox combo when isCombo:
                combo.SelectionChanged += (_, args) =>
                {
                    if (!_filling && control.IsEnabled && SelectedPool() is { IsPrimordial: false })
                    {
                        if (TierGroups().FirstOrDefault(group => ReferenceEquals(group.ResiliencyBox, combo)) is { } tierGroup
                            && combo.SelectedItem is string resiliency)
                            _columnSuggestions[ColumnSuggestionKey(tierGroup)] = ColumnChoice(tierGroup).ChooseResiliency(resiliency);
                        _formDirty = true;
                        UpdateLinkedFields();
                        UpdateButtonState();
                    }
                };
                break;
        }
    }

    /// <summary>Enter commits the field and moves focus away from it.</summary>
    private void HookEnterCommit(FrameworkElement field)
    {
        field.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            if (field is NumberBox number)
            {
                // Marking Enter handled suppresses the NumberBox's own
                // commit, so fold the typed text into Value explicitly.
                if (double.TryParse(
                        number.Text,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture,
                        out var committed)
                    && double.IsFinite(committed))
                {
                    number.Value = committed;
                }

                MarkExplicitColumns(number);
                NormalizeTierNumber(number);
            }

            Focus(FocusState.Programmatic);
            UpdateButtonState();
        };
    }

    private async Task CommitNameAsync(TextBox field)
    {
        if (_filling || !CanEditDraft)
        {
            return;
        }

        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return;
        }

        string? targetId = null;
        string currentName = string.Empty;
        var draftTarget = false;
        if (ReferenceEquals(field, _poolNameBox))
        {
            targetId = pool.StableId;
            currentName = pool.FriendlyName;
            draftTarget = EditWorkspace.IsDraftPool(pool.StableId);
        }
        else if (ReferenceEquals(field, _virtualDiskNameBox))
        {
            var vdisk = _working.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            targetId = vdisk?.StableId;
            currentName = vdisk?.FriendlyName ?? string.Empty;
            draftTarget = vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId);
        }
        else if (ReferenceEquals(field, _volumeNameBox))
        {
            var partition = PrimaryPartition(pool.StableId);
            var volume = partition is null ? null : EditingSession.Baseline.VolumeForPartition(partition.StableId);
            targetId = volume?.StableId;
            currentName = volume?.FileSystemLabel ?? string.Empty;
            draftTarget = volume is null;
        }

        if (draftTarget || string.IsNullOrWhiteSpace(targetId))
        {
            if (EditWorkspace.IsDraftPool(pool.StableId)
                || _working.VirtualDisks.Any(item => item.PoolStableId == pool.StableId
                    && EditWorkspace.IsDraftVirtualDisk(item.StableId)))
            {
                _formDirty = true;
            }
            Focus(FocusState.Programmatic);
            UpdateButtonState();
            return;
        }

        var requestedName = field.Text.Trim();
        if (string.Equals(requestedName, currentName, StringComparison.Ordinal))
        {
            Focus(FocusState.Programmatic);
            return;
        }

        var namingSession = EditingSession;
        var namingSystemId = namingSession.SystemId;
        _renameInProgress = true;
        try
        {
            if (ViewModel.CanSubmitRealOperation)
            {
                if (!CanUseRealPool(pool))
                    return;
                var system = ViewModel.ActiveDocument.SystemId;
                StorageObjectKind kind;
                RealStorageCommand command;
                if (ReferenceEquals(field, _poolNameBox))
                {
                    kind = StorageObjectKind.StoragePool;
                    var id = new StorageObjectId(system, kind, targetId);
                    command = new RenamePoolCommand(RealTargetReference.ForExisting(id), requestedName);
                }
                else if (ReferenceEquals(field, _virtualDiskNameBox))
                {
                    kind = StorageObjectKind.VirtualDisk;
                    var id = new StorageObjectId(system, kind, targetId);
                    command = new RenameVirtualDiskCommand(RealTargetReference.ForExisting(id), requestedName);
                }
                else
                {
                    kind = StorageObjectKind.Volume;
                    var id = new StorageObjectId(system, kind, targetId);
                    command = new RenameVolumeCommand(RealTargetReference.ForExisting(id), requestedName);
                }
                var target = new StorageObjectId(system, kind, targetId);
                var renamed = await SubmitRealPropertyAsync(RealOperationProposalFactory.OneStep(
                    system, kind == StorageObjectKind.Volume
                        ? OperationIntent.SetVolumeLabel
                        : OperationIntent.RenameStorageObject,
                    target, command, $"Name becomes {requestedName}",
                    "No file data loss expected"));
                if (renamed)
                {
                    if (ViewModel.SystemCatalog.Systems.SingleOrDefault(document => document.SystemId == namingSystemId) is { } renamedSystem)
                        namingSession.AcceptRename(renamedSystem, targetId);
                    if (!ReferenceEquals(EditingSession, namingSession) || ViewModel.SelectedSystem.SystemId != namingSystemId) return;
                    RefreshTopology();
                    UpdateButtonState();
                }
                return;
            }
            using var activity = BeginPageActivity(Text("正在提交名称修改", "Saving name change"));
            ApplicationResult<SimulationEditReceipt> result;
            try
            {
                result = await ViewModel.ApplySimulationOperationAsync(new SimulationEditRequest(
                    SimulationEditKind.Rename,
                    targetId,
                    Name: requestedName));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                PublishOperationException(
                    Text("改名失败", "Rename failed"),
                    "storage-structure-editor",
                    exception,
                    "structure.rename.exception");
                return;
            }

            if (!result.IsSuccess || result.Value is null)
            {
                PublishOperationResult(
                    result.Status,
                    result.Messages,
                    result.CorrelationId,
                    result.Status == ApplicationStatus.OutcomeUnknown
                        ? Text("提交结果未知", "Commit outcome unknown")
                        : Text("改名失败", "Rename failed"),
                    "storage-structure-editor");
                if (result.Status == ApplicationStatus.OutcomeUnknown)
                {
                    namingSession.OutcomeUnknown = true;
                }
                return;
            }

            if (ViewModel.SystemCatalog.Systems.SingleOrDefault(document => document.SystemId == namingSystemId) is { } savedSystem)
                namingSession.AcceptRename(savedSystem, targetId);
            if (!ReferenceEquals(EditingSession, namingSession) || ViewModel.SelectedSystem.SystemId != namingSystemId) return;
            _currentPlan = null;
            RefreshTopology();
            UpdateButtonState();
        }
        finally
        {
            namingSession.RenameInProgress = false;
            RefreshTopology();
            UpdateButtonState();
        }
    }

    /// <summary>
    /// Commit-time normalization of tier numbers (Enter / focus loss): empty
    /// values restore the committed tier value; out-of-range values clamp to
    /// the allowed bounds (capacity to the tier's member capacity).
    /// </summary>
    private void NormalizeTierNumber(NumberBox number)
    {
        foreach (var group in TierGroups())
        {
            var tier = SelectedTier(group.Media);
            if (tier is null)
            {
                continue;
            }

            if (ReferenceEquals(group.CopiesBox, number))
            {
                var value = NumValue(number) ?? (tier.NumberOfDataCopies ?? 1);
                SetNum(number, Math.Clamp(value, 1, 16));
                return;
            }

            if (ReferenceEquals(group.FailuresBox, number))
            {
                var value = NumValue(number) ?? (tier.PhysicalDiskRedundancy ?? 0);
                SetNum(number, Math.Clamp(value, 0, 16));
                return;
            }

            if (ReferenceEquals(group.ColumnsBox, number))
            {
                var value = NumValue(number) ?? (tier.NumberOfColumns ?? 1);
                var setting = group.ResiliencyBox.SelectedItem as string ?? tier.ResiliencySettingName;
                var count = TierDataDisks(group.Media).Count;
                var copies = Math.Max(1, (int)(NumValue(group.CopiesBox) ?? tier.NumberOfDataCopies ?? 1));
                var minimum = setting.Equals("Parity", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1, (int)(NumValue(group.FailuresBox) ?? 1)) + 2 : 1;
                var maximum = double.IsFinite(group.ColumnsBox.Maximum)
                    ? (int)Math.Max(minimum, group.ColumnsBox.Maximum)
                    : Math.Max(minimum, setting.Equals("Mirror", StringComparison.OrdinalIgnoreCase) ? count / copies : count);
                SetNum(number, Math.Clamp((int)value, minimum, maximum));
                return;
            }
        }
    }

    private StorageTierInfo? SelectedTier(string media)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return null;
        }

        return TierMap(pool.StableId).GetValueOrDefault(media);
    }

    private void ConfigureFormGrid(Grid grid)
    {
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorFormMetrics.LabelWidth(IsChinese)) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorFormMetrics.CompactActionButtonWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = EditorFormMetrics.MinimumValueWidth });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorFormMetrics.UnitColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorFormMetrics.MaximumButtonWidth) });
        grid.ColumnSpacing = 4;
        grid.RowSpacing = 4;
    }

    private Grid CreateFormGrid()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0),
            ColumnSpacing = 4,
            RowSpacing = 4
        };
        ConfigureFormGrid(grid);
        return grid;
    }

    private void AddTierGroup(TierFields group)
    {
        var row = AddSectionHeader(group.FormGrid, 0, group.TitleKey, group.Rows);
        var sizePanel = new Grid();
        var hostedSizeBox = ContextHelp.Wrap(group.SizeBox);
        sizePanel.Children.Add(hostedSizeBox);
        sizePanel.Children.Add(group.MaximumDisplay);
        row = AddTierRow(row, group, "TierSize", sizePanel, () => ResetTierField(group, "Size"));
        var unit = new TextBlock { Text = "GiB", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(unit, row - 1);
        Grid.SetColumn(unit, 3);
        group.FormGrid.Children.Add(unit);
        group.Rows.Add(unit);
        var hostedMaximumButton = ContextHelp.Wrap(group.MaximumButton);
        Grid.SetRow(hostedMaximumButton, row - 1);
        Grid.SetColumn(hostedMaximumButton, 4);
        group.FormGrid.Children.Add(hostedMaximumButton);
        group.Rows.Add(hostedMaximumButton);
        row = AddTierRow(row, group, "TierProvisioning", group.ProvisioningBox, null);
        row = AddTierRow(row, group, "TierResiliency", group.ResiliencyBox, () => ResetTierField(group, "Resiliency"));
        row = AddTierRow(row, group, "TierDataCopies", group.CopiesBox, () => ResetTierField(group, "Copies"));
        row = AddTierRow(row, group, "TierToleratedFailures", group.FailuresBox, () => ResetTierField(group, "Failures"));
        row = AddTierRow(row, group, "TierDiskCount", group.DiskCountBox, null);
        row = AddTierRow(row, group, "TierColumns", group.ColumnsBox, () => ResetTierField(group, "Columns"));
        row = AddTierRow(row, group, "TierStripeSize", group.InterleaveBox, () => ResetTierField(group, "Stripe"));
        group.RowIndices.Add(row);
        AddFormRow(group.FormGrid, row, "FullStripe", group.FullStripeDisplay, visibilityGroup: group.Rows,
            labelText: Text("完整条带", "Full stripe"));
    }

    private int AddTierRow(
        int row,
        TierFields group,
        string key,
        FrameworkElement value,
        Action? reset)
    {
        group.RowIndices.Add(row);
        return AddFormRow(group.FormGrid, row, key, value, reset, TierRowChanged(group, key), group.Rows,
            valueColumnSpan: key == "TierSize" ? 1 : 3);
    }

    private int AddPartitionRow(
        Grid formGrid,
        int row,
        string key,
        FrameworkElement value,
        Func<bool> changed,
        Action reset) =>
        AddFormRow(formGrid, row, key, value, reset, changed, null);

    /// <summary>Live "changed vs committed" check for a tier parameter row.</summary>
    private Func<bool> TierRowChanged(TierFields group, string key)
    {
        var (field, media) = key switch
        {
            "TierSize" => (TierFieldKind.Size, group.Media),
            "TierProvisioning" => (TierFieldKind.Provisioning, group.Media),
            "TierResiliency" => (TierFieldKind.Resiliency, group.Media),
            "TierDataCopies" => (TierFieldKind.Copies, group.Media),
            "TierToleratedFailures" => (TierFieldKind.Failures, group.Media),
            "TierDiskCount" => (TierFieldKind.DiskCount, group.Media),
            "TierColumns" => (TierFieldKind.Columns, group.Media),
            _ => (TierFieldKind.Stripe, group.Media)
        };
        return () => TierFieldChanged(field, media);
    }

    private Button CreateResetButton(Action reset)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["WinPoolCompactIconButtonStyle"],
            Content = new FontIcon
            {
                Glyph = "\uE777",
                FontSize = 12
            },
            Width = EditorFormMetrics.CompactActionButtonWidth,
            MinWidth = EditorFormMetrics.CompactActionButtonWidth,
            Height = 32,
            Padding = new Thickness(0)
        };
        ContextHelp.Set(button, Text("恢复编辑前值；新对象恢复默认值。", "Restore the value before editing, or the default for a new object."));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Text("恢复此项", "Restore this field"));
        button.Click += (_, _) =>
        {
            if (_filling)
            {
                return;
            }

            _filling = true;
            try { reset(); }
            finally { _filling = false; }
            _formDirty = true;
            UpdateLinkedFields();
            UpdateButtonState();
        };
        return button;
    }

    /// <summary>
    /// Adds a card title row. Each card owns its own grid, so sections no
    /// longer need separator lines inside one shared form.
    /// </summary>
    private int AddSectionHeader(
        Grid formGrid,
        int row,
        string key,
        List<FrameworkElement>? visibilityGroup = null)
    {
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            Text = ViewModel.Localization[key]
        };
        var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(label);
        Grid.SetColumn(arrow, 1);
        header.Children.Add(arrow);
        var button = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = header
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, $"Structure{key}Header");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label.Text);
        _localizeForm.Add(() =>
        {
            label.Text = ViewModel.Localization[key];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label.Text);
        });
        _sectionHeaders[formGrid] = (button, arrow, false);
        button.Click += (_, _) =>
        {
            var state = _sectionHeaders[formGrid];
            _sectionHeaders[formGrid] = (state.Button, state.Arrow, !state.Collapsed);
            ApplySectionCollapse(formGrid);
        };
        Grid.SetRow(button, row);
        Grid.SetColumnSpan(button, 5);
        formGrid.Children.Add(button);
        visibilityGroup?.Add(button);
        return row + 1;
    }

    /// <summary>
    /// One uniform-height row: label (col 0), a fixed reset-button lane
    /// (col 1, blank on rows without one), value (col 2). Parameter rows
    /// show their reset button only while the field differs from the
    /// committed state.
    /// </summary>
    private int AddFormRow(
        Grid formGrid,
        int row,
        string key,
        FrameworkElement value,
        Action? reset = null,
        Func<bool>? changed = null,
        List<FrameworkElement>? visibilityGroup = null,
        string? labelText = null,
        int valueColumnSpan = 3)
    {
        formGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto,
            MinHeight = 40
        });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = labelText ?? ViewModel.Localization[key]
        };
        _localizeForm.Add(() =>
        {
            label.Text = FormLabel(key, labelText);
            label.MaxLines = IsChinese ? 1 : 2;
        });
        label.Text = FormLabel(key, labelText);
        label.MaxLines = IsChinese ? 1 : 2;
        var indicator = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed
        };
        var labelPanel = new Grid
        {
            ColumnSpacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(indicator, 1);
        labelPanel.Children.Add(label);
        labelPanel.Children.Add(indicator);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.MinHeight = 32;
        value.MinWidth = 0;
        if (value is Control and not ToggleSwitch) value.Height = 32;
        if (value is TextBox numeric && numeric != _poolNameBox && numeric != _virtualDiskNameBox && numeric != _volumeNameBox)
            numeric.TextAlignment = TextAlignment.Right;

        Grid.SetRow(labelPanel, row);
        Grid.SetColumn(labelPanel, 0);
        // A disabled WinUI control cannot reliably receive pointer input, so
        // host actual input controls instead of putting their explanation in
        // a separate gray text line. Composite panels retain their own layout
        // and host their child inputs individually (see AddTierGroup).
        var hostedValue = value is Control ? ContextHelp.Wrap(value) : value;
        Grid.SetRow(hostedValue, row);
        Grid.SetColumn(hostedValue, 2);
        Grid.SetColumnSpan(hostedValue, valueColumnSpan);
        formGrid.Children.Add(labelPanel);
        formGrid.Children.Add(hostedValue);
        visibilityGroup?.Add(labelPanel);
        visibilityGroup?.Add(hostedValue);
        if (reset is null)
        {
            return row + 1;
        }

        var button = CreateResetButton(reset);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Visibility = Visibility.Collapsed;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, 1);
        formGrid.Children.Add(button);
        visibilityGroup?.Add(button);
        _fieldResets.Add(new FieldReset(button, indicator, changed ?? (() => false)));
        return row + 1;
    }

    private int AddNameFormRow(Grid formGrid, int row, string key, TextBox input)
    {
        var check = new Button
        {
            Style = (Style)Application.Current.Resources["WinPoolCompactIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE73E", FontSize = 12 },
            Width = EditorFormMetrics.CompactActionButtonWidth,
            MinWidth = EditorFormMetrics.CompactActionButtonWidth,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var applyName = key switch
        {
            "PoolName" => Text("应用池名", "Apply pool name"),
            "VirtualDiskName" => Text("应用虚拟磁盘名", "Apply virtual disk name"),
            _ => Text("应用卷名", "Apply volume name")
        };
        var undoName = key switch
        {
            "PoolName" => Text("撤销池名", "Undo pool name"),
            "VirtualDiskName" => Text("撤销虚拟磁盘名", "Undo virtual-disk name"),
            _ => Text("撤销卷名", "Undo volume name")
        };
        ContextHelp.Set(check, Text($"直接提交此字段：{applyName}。", $"Submit only this field: {applyName}."));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(check, $"Structure{key}Check");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, applyName);
        check.Click += async (_, _) => await CommitNameAsync(input);
        var hostedCheck = ContextHelp.Wrap(check);
        Grid.SetColumn(hostedCheck, 4);
        Grid.SetRow(hostedCheck, row);
        formGrid.Children.Add(hostedCheck);

        var nextRow = AddFormRow(
            formGrid,
            row,
            key,
            input,
            () => ResetNameField(input),
            () => NameFieldChanged(input),
            valueColumnSpan: 2);
        var undo = _fieldResets[^1].ResetButton;
        undo.Style = (Style)Application.Current.Resources["WinPoolCompactIconButtonStyle"];
        undo.Content = new FontIcon { Glyph = "\uE777", FontSize = 12 };
        undo.Width = EditorFormMetrics.CompactActionButtonWidth;
        undo.MinWidth = EditorFormMetrics.CompactActionButtonWidth;
        undo.Height = 32;
        undo.Padding = new Thickness(0);
        ContextHelp.Set(undo, Text("恢复到已提交名称或新对象默认名称。", "Restore the committed name or the new object's default name."));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(undo, $"Structure{key}Undo");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(undo, undoName);
        input.Height = 32;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, $"Structure{key}Input");
        _nameActions.Add(new NameActionFields(input, undo, check));
        return nextRow;
    }

    private void ResetNameField(TextBox input)
    {
        var defaultName = NameUndoValue(input);
        if (!input.Text.Equals(defaultName, StringComparison.Ordinal))
            input.Text = defaultName;
        UpdateButtonState();
    }

    private bool NameFieldChanged(TextBox input) =>
        SelectedPool() is { IsPrimordial: false }
        && !string.Equals(input.Text.Trim(), NameUndoValue(input), StringComparison.Ordinal);

    private void UpdateNameActionStates()
    {
        foreach (var action in _nameActions)
        {
            var changed = NameFieldChanged(action.Input);
            var valueIsValid = NameValueIsValid(action.Input);
            action.Undo.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
            action.Undo.IsEnabled = CanEditDraft && action.Input.IsEnabled && changed;
            action.Check.IsEnabled = CanEditDraft && action.Input.IsEnabled && changed && valueIsValid;
        }
    }

    private bool NameValueIsValid(TextBox input)
    {
        if (!ReferenceEquals(input, _volumeNameBox))
            return !string.IsNullOrWhiteSpace(input.Text);

        var pool = SelectedPool();
        var partition = pool is null ? null : PrimaryPartition(pool.StableId);
        var committedVolume = partition is null
            ? null
            : EditingSession.Baseline.VolumeForPartition(partition.StableId);
        return committedVolume is not null || !string.IsNullOrWhiteSpace(input.Text);
    }

    private string NameUndoValue(TextBox input)
    {
        var pool = SelectedPool();
        if (pool is null)
            return string.Empty;

        var slot = NameSlot(input);
        var key = NameDefaultKey(pool.StableId, slot);
        if (ReferenceEquals(input, _poolNameBox))
        {
            return EditingSession.Baseline.StoragePools.FirstOrDefault(item =>
                    item.StableId.Equals(pool.StableId, StringComparison.OrdinalIgnoreCase))?.FriendlyName
                ?? _nameDefaults.GetValueOrDefault(key, pool.FriendlyName);
        }

        var vdisk = _working?.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(input, _virtualDiskNameBox))
        {
            return vdisk is null
                ? _nameDefaults.GetValueOrDefault(key, pool.FriendlyName)
                : EditingSession.Baseline.VirtualDisks.FirstOrDefault(item =>
                    item.StableId.Equals(vdisk.StableId, StringComparison.OrdinalIgnoreCase))?.FriendlyName
                    ?? _nameDefaults.GetValueOrDefault(key, vdisk.FriendlyName);
        }

        var partition = CommittedPrimaryPartition(pool.StableId);
        var committedVolume = partition is null
            ? null
            : EditingSession.Baseline.VolumeForPartition(partition.StableId);
        return committedVolume?.FileSystemLabel
            ?? _nameDefaults.GetValueOrDefault(
                key,
                partition?.FileSystemLabel ?? _poolIntents.GetValueOrDefault(pool.StableId)?.VolumeName
                    ?? vdisk?.FriendlyName ?? pool.FriendlyName);
    }

    private string NameSlot(TextBox input) =>
        ReferenceEquals(input, _poolNameBox) ? "Pool"
        : ReferenceEquals(input, _virtualDiskNameBox) ? "VirtualDisk"
        : "Volume";

    private static string NameDefaultKey(string poolId, string slot) => $"{poolId}|Name|{slot}";

    private void RememberNameDefault(string poolId, string slot, string value) =>
        _nameDefaults.TryAdd(NameDefaultKey(poolId, slot), value);

    private static void FillCombo(ComboBox box, IReadOnlyList<string> items, int selected)
    {
        box.Items.Clear();
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        box.SelectedIndex = selected;
    }


    private string? ResolvePoolId(string? stableId)
    {
        if (stableId is null)
        {
            return null;
        }

        var snapshot = EditingSession.Baseline;
        if (snapshot.StoragePools.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.StableId, stableId)))
        {
            return stableId;
        }

        return snapshot.StorageTiers.FirstOrDefault(item =>
                StringComparer.OrdinalIgnoreCase.Equals(item.StableId, stableId))?.PoolStableId
            ?? snapshot.PhysicalDisks.FirstOrDefault(item =>
                StringComparer.OrdinalIgnoreCase.Equals(item.StableId, stableId))?.PoolStableId
            ?? snapshot.VirtualDisks.FirstOrDefault(item =>
                StringComparer.OrdinalIgnoreCase.Equals(item.StableId, stableId))?.PoolStableId;
    }

    private void RefreshAll()
    {
        FillPoolForm();
        RefreshTopology();
        UpdateButtonState();
    }

    private void RefreshTopology()
    {
        var visible = new List<string>();
        if (ShowHotSpareSwitch.IsOn)
        {
            visible.Add("HotSpare");
        }

        if (ShowRetiredSwitch.IsOn)
        {
            visible.Add("Retired");
        }

        _interaction = new TopologyEditInteraction(IsTopologyNodeSelected, OnTopologySelected, CanEditDraft, OnDiskDropped, () => CanEditDraft, CanDropDisk,
            ContextMenuFactory: CreateTopologyContextMenu, CanCreatePoolNow: CanCreatePoolNow);
        var display = _working;
        var root = EditWorkspace.ProjectPoolWorkspaceRoot(
            display,
            UnallocatedIgnoreBytes,
            EditingSession.Baseline,
            visible,
            showSourceTierTemplates: !ViewModel.IsUsingSimulatedInventory,
            sourceFacts: EditingSession.BaselineSourceFacts);
        var rootViewModel = new TopologyNodeViewModel(
            EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, "edit-pool-row"),
            ViewModel,
            display,
            _interaction,
            isLayoutRoot: true);
        rootViewModel.SetSurfaceViewportWidth(_viewportWidth);
        ApplyPendingModificationStates(rootViewModel);
        TopologyControl.ItemsSource = new[] { rootViewModel };
    }

    private void ApplyPendingModificationStates(TopologyNodeViewModel root)
    {
        var committed = EditingSession.Baseline;
        var pending = _formDirty && !HasInvalidSizeInput() ? ApplyFormToWorking(_working) : _working;
        var queue = new Queue<TopologyNodeViewModel>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.ShowsEditStatus)
            {
                node.SetPendingModifications(
                    HasPendingStructureChanges(pending, committed, node));
            }

            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    private bool HasPendingStructureChanges(StorageSnapshot pending, StorageSnapshot committed, TopologyNodeViewModel node)
    {
        var id = node.Unit.StableId;
        if (EditWorkspace.HasPendingModifications(pending, committed, id)) return true;
        switch (node.Unit.Kind)
        {
            case StorageUnitKind.StoragePool:
                return EditWorkspace.HasPoolPropertyChanges(pending, committed, id)
                    || HasPendingPartitionFormatChanges(pending, committed, id);
            case StorageUnitKind.StorageTier:
                var tier = pending.StorageTiers.FirstOrDefault(item => item.StableId == id);
                var priorTier = committed.StorageTiers.FirstOrDefault(item => item.StableId == id);
                return tier is not null && (priorTier is null || !TierSpecsEqual(tier, priorTier)
                    || tier.Size != priorTier.Size
                    || !tier.MemberPhysicalDiskIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(priorTier.MemberPhysicalDiskIds));
            case StorageUnitKind.VirtualDisk:
                var vdisk = pending.VirtualDisks.FirstOrDefault(item => item.StableId == id);
                var priorVdisk = committed.VirtualDisks.FirstOrDefault(item => item.StableId == id);
                return vdisk is not null && (priorVdisk is null || vdisk.Size != priorVdisk.Size
                    || vdisk.ResiliencySettingName != priorVdisk.ResiliencySettingName
                    || vdisk.Interleave != priorVdisk.Interleave
                    || vdisk.NumberOfColumns != priorVdisk.NumberOfColumns
                    || vdisk.NumberOfDataCopies != priorVdisk.NumberOfDataCopies
                    || vdisk.PhysicalDiskRedundancy != priorVdisk.PhysicalDiskRedundancy);
            case StorageUnitKind.PhysicalDisk:
                var disk = pending.PhysicalDisks.FirstOrDefault(item => item.StableId == id);
                var priorDisk = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == id);
                return disk is not null && (priorDisk is null || disk.Usage != priorDisk.Usage
                    || !pending.StorageTiers.Where(item => item.MemberPhysicalDiskIds.Contains(id))
                        .Select(item => item.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(committed.StorageTiers.Where(item => item.MemberPhysicalDiskIds.Contains(id))
                            .Select(item => item.StableId)));
            default: return false;
        }
    }

    private bool HasPendingPartitionFormatChanges(StorageSnapshot pending, StorageSnapshot committed, string poolId)
    {
        var partition = StructurePartitionBaseline.ForPool(committed, poolId);
        // Deleted/replaced partitions are represented by the structural diff,
        // rather than a stale formatting intent for the previous partition.
        if (partition is null || !pending.Partitions.Any(item => item.StableId == partition.StableId))
            return false;

        string fileSystem;
        long cluster;
        if (string.Equals(poolId, _selectedPoolId, StringComparison.OrdinalIgnoreCase))
        {
            fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
            cluster = SelectedClusterSize();
        }
        else if (_poolIntents.TryGetValue(poolId, out var intent))
        {
            fileSystem = intent.FileSystem;
            cluster = intent.AllocationUnitSize;
        }
        else return false;

        return !string.Equals(fileSystem, partition.FileSystem, StringComparison.OrdinalIgnoreCase)
            || cluster != partition.AllocationUnitSize;
    }

    private StoragePoolInfo? SelectedPool() =>
        _working.StoragePools.FirstOrDefault(item => item.StableId == _selectedPoolId);

    private bool IsTopologyNodeSelected(TopologyNodeViewModel node) =>
        node.Unit.Kind switch
        {
            StorageUnitKind.PhysicalDisk => node.Unit.StableId == _selectedPoolDiskId,
            StorageUnitKind.VirtualDisk => node.Unit.StableId == _selectedPoolVdiskId,
            StorageUnitKind.StorageTier => node.Unit.StableId == _selectedPoolTierId,
            StorageUnitKind.StoragePool => node.Unit.StableId == _selectedPoolId
                && string.IsNullOrEmpty(_selectedPoolDiskId)
                && string.IsNullOrEmpty(_selectedPoolVdiskId)
                && string.IsNullOrEmpty(_selectedPoolTierId),
            _ => false
        };

    private void OnTopologySelected(TopologyNodeViewModel node)
    {
        if (EditWorkspace.IsPlus(node.Unit.StableId))
        {
            CreateDraftPoolAndSelect();
            return;
        }

        var priorState = _formDirty ? CaptureDraftState() : null;
        CaptureSelectedIntent();
        if (_formDirty && HasInvalidSizeInput())
        {
            UpdateButtonState();
            return;
        }

        if (_formDirty)
        {
            _working = ApplyFormToWorking(_working);
            _formDirty = false;
            _formHistoryCaptured = false;
            _undoStack.Push(priorState!);
            _redoStack.Clear();
        }

        switch (node.Unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
                _selectedPoolDiskId = node.Unit.StableId;
                _selectedPoolVdiskId = null;
                _selectedPoolTierId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.PhysicalDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            case StorageUnitKind.VirtualDisk:
                _selectedPoolVdiskId = node.Unit.StableId;
                _selectedPoolDiskId = null;
                _selectedPoolTierId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.VirtualDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            case StorageUnitKind.StorageTier:
                _selectedPoolTierId = node.Unit.StableId;
                _selectedPoolDiskId = null;
                _selectedPoolVdiskId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.StorageTiers.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            default:
                _selectedPoolVdiskId = null;
                _selectedPoolDiskId = null;
                _selectedPoolTierId = null;
                _selectedPoolId = node.Unit.Kind == StorageUnitKind.StoragePool
                    ? node.Unit.StableId
                    : node.Unit.ParentStableId;
                break;
        }

        ResetLayerSwitchesForSelection();
        FillPoolForm();
        RefreshTopology();
        UpdateButtonState();
    }

    private bool CanCreatePoolNow() => CanEditDraft
        && !_working.StoragePools.Any(pool => EditWorkspace.IsDraftPool(pool.StableId))
        && _working.PhysicalDisks.Any(disk => CanDropDisk(disk.StableId, EditWorkspace.PlusStableId));

    private bool CanDropDisk(string diskId, string targetId)
    {
        if (!CanEditDraft || IsPhysicalDiskOffline(diskId)) return false;
        var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == diskId);
        if (disk is not { IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }) return false;
        var sourcePoolId = disk.PoolStableId;
        var originalRealMember = IsOriginalConcreteRealMember(diskId, sourcePoolId);
        var samePoolRoleTarget = EditWorkspace.IsSimulatedLayer(targetId)
            && sourcePoolId is not null
            && targetId.EndsWith(sourcePoolId, StringComparison.OrdinalIgnoreCase);
        if (originalRealMember && !samePoolRoleTarget && (EditWorkspace.IsPlus(targetId)
            || !string.Equals(ResolvePoolDropTarget(targetId), sourcePoolId, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (EditWorkspace.IsPlus(targetId))
            return !_working.StoragePools.Any(pool => EditWorkspace.IsDraftPool(pool.StableId))
                && !EditWorkspace.DiskCannotLeave(_working, EditingSession.Baseline, disk);
        if (EditWorkspace.IsPoolRow(targetId)) return false;
        if (EditWorkspace.IsSimulatedLayer(targetId))
            return CanStageDiskSpecialRole(disk)
                && disk.PoolStableId == _selectedPoolId
                && targetId.EndsWith(disk.PoolStableId ?? "", StringComparison.OrdinalIgnoreCase);
        var poolId = ResolvePoolDropTarget(targetId);
        var pool = _working.StoragePools.FirstOrDefault(item => item.StableId == poolId);
        if (pool is null || StorageEditRules.TouchesOfflineDisk(_working, [poolId])) return false;
        var samePool = string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase);
        if (!ViewModel.IsUsingSimulatedInventory && !pool.IsPrimordial
            && !EditWorkspace.IsDraftPool(poolId) && !samePool)
            return false;
        var hasSpecialRole = disk.IsHotSpare || disk.IsRetired;
        if (!ViewModel.IsUsingSimulatedInventory && hasSpecialRole && !samePool
            && !EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)) return false;
        if (targetId.StartsWith("group:direct:", StringComparison.OrdinalIgnoreCase))
            return samePool
                && (ViewModel.IsUsingSimulatedInventory || !EditWorkspace.IsDraftPool(poolId)
                    || CanStageRealMemberRelationship(diskId, poolId))
                && (!hasSpecialRole || CanStageDiskSpecialRole(disk))
                && (EditWorkspace.DiskIsAssignedToTier(_working, diskId) || EditWorkspace.DiskUsage(disk).Length > 0);
        var tier = _working.StorageTiers.FirstOrDefault(item => item.StableId == targetId);
        if (tier is not null && EditWorkspace.NormalizeMedia(tier.MediaType) != EditWorkspace.NormalizeMedia(disk.MediaType))
            return false;
        if (tier?.MemberPhysicalDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase) == true) return false;
        if (tier is null && disk.PoolStableId == poolId && EditWorkspace.DiskIsAssignedToTier(_working, diskId)) return false;
        if (disk.PoolStableId != poolId && EditWorkspace.DiskCannotLeave(_working, EditingSession.Baseline, disk)) return false;
        return (!hasSpecialRole || CanStageDiskSpecialRole(disk))
            && (ViewModel.IsUsingSimulatedInventory || CanStageRealMemberRelationship(diskId, poolId));
    }

    private void OnDiskDropped(string diskId, string targetId)
    {
        if (!CanEditDraft)
        {
            return;
        }

        if (IsPhysicalDiskOffline(diskId))
        {
            _ = ShowMessageAsync(
                Text("磁盘已脱机", "Disk offline"),
                Text("脱机磁盘只能查看，请先在磁盘分区编辑页联机。", "An offline disk is read-only. Bring it online in the disk partition editor first."));
            return;
        }

        if (!CanDropDisk(diskId, targetId)) return;

        if (EditWorkspace.IsPlus(targetId))
        {
            CreateDraftPoolAndSelect();
            if (_selectedPoolId is not null && EditWorkspace.IsDraftPool(_selectedPoolId))
            {
                _ = DropDiskIntoPoolAsync(diskId, _selectedPoolId);
            }
            return;
        }

        if (EditWorkspace.IsRetiredLayer(targetId) || EditWorkspace.IsHotSpareLayer(targetId))
        {
            _ = DropDiskIntoSimulatedLayerAsync(
                diskId,
                EditWorkspace.IsRetiredLayer(targetId) ? "Retired" : "HotSpare",
                targetId);
            return;
        }

        if (targetId.StartsWith("group:direct:", StringComparison.OrdinalIgnoreCase))
        {
            // Dropping onto the Unallocated group header evicts the disk
            // from every real tier while it stays in the pool.
            _ = EvictDroppedDiskToUnallocatedAsync(diskId, targetId);
            return;
        }

        if (EditWorkspace.IsPoolRow(targetId))
        {
            return;
        }

        var targetTier = _working.StorageTiers.FirstOrDefault(tier => tier.StableId == targetId);
        if (targetTier is not null)
        {
            var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == diskId);
            if (disk is null || EditWorkspace.NormalizeMedia(disk.MediaType) != EditWorkspace.NormalizeMedia(targetTier.MediaType))
                return;
        }
        _ = DropDiskIntoPoolAsync(diskId, ResolvePoolDropTarget(targetId), targetTier?.StableId);
    }

    private string ResolvePoolDropTarget(string targetId)
    {
        if (_working.StoragePools.Any(pool => pool.StableId == targetId)) return targetId;
        var tierPool = _working.StorageTiers.FirstOrDefault(tier => tier.StableId == targetId)?.PoolStableId;
        if (tierPool is not null) return tierPool;
        foreach (var pool in _working.StoragePools)
            if (targetId.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
                && targetId.EndsWith(pool.StableId, StringComparison.OrdinalIgnoreCase)) return pool.StableId;
        return targetId;
    }

    private bool CanStageRealMemberRelationship(string diskId, string poolId) =>
        _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId) is
            { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }
        && _working.StoragePools.Any(pool => pool.StableId == poolId);

    private bool IsOriginalConcreteRealMember(string diskId, string? poolId) =>
        !ViewModel.IsUsingSimulatedInventory && poolId is not null
        && !EditWorkspace.IsDraftPool(poolId)
        && EditingSession.Baseline.StoragePools.Any(pool => pool.StableId == poolId && !pool.IsPrimordial)
        && EditingSession.Baseline.PhysicalDisks.Any(disk => disk.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase));

    private bool CanStageDiskSpecialRole(PhysicalDiskInfo disk) =>
        CanEditDraft
        && disk is { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }
        && !IsPhysicalDiskOffline(disk.StableId)
        && _working.StoragePools.FirstOrDefault(pool => pool.StableId == disk.PoolStableId && !pool.IsPrimordial) is { } pool
        && !StorageEditRules.TouchesOfflineDisk(_working, [disk.PoolStableId!])
        && (ViewModel.IsUsingSimulatedInventory
            || EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)
                && CanStageRealMemberRelationship(disk.StableId, disk.PoolStableId!)
            || CanUseRealPool(pool)
                && !EditWorkspace.PoolHoldsStoredData(_working, pool.StableId)
                && !_working.VirtualDisks.Any(vdisk => vdisk.PoolStableId == pool.StableId));

    private async Task DropDiskIntoSimulatedLayerAsync(string diskId, string usage, string targetLayerId)
    {
        MergeFormIntoWorking();
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        if (workingDisk is null || !CanStageDiskSpecialRole(workingDisk))
        {
            return;
        }

        var layerPoolId = EditWorkspace.IsRetiredLayer(targetLayerId)
            ? targetLayerId[EditWorkspace.RetiredLayerPrefix.Length..]
            : targetLayerId[EditWorkspace.HotSpareLayerPrefix.Length..];
        var selected = SelectedPool();
        if (selected is null || !selected.StableId.Equals(layerPoolId, StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowMessageAsync(
                ViewModel.Localization["SimulatedLayerRestrictedTitle"],
                ViewModel.Localization["SimulatedLayerRestrictedMessage"]);
            return;
        }

        if (usage == "Retired" ? workingDisk.IsRetired : workingDisk.IsHotSpare)
        {
            return;
        }

        var prepared = await PrepareDiskSpecialRoleDropAsync(workingDisk);
        if (prepared is null)
        {
            return;
        }

        try
        {
            var next = EditWorkspace.SetDiskUsage(prepared, diskId, usage);
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private async Task EvictDroppedDiskToUnallocatedAsync(string diskId, string targetGroupId)
    {
        MergeFormIntoWorking();
        var groupPoolId = targetGroupId["group:direct:".Length..];
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        if (workingDisk is null
            || workingDisk.PoolStableId != groupPoolId)
        {
            return;
        }

        _selectedPoolId = groupPoolId;
        _selectedPoolDiskId = diskId;
        _selectedPoolTierId = null;
        _selectedPoolVdiskId = null;
        if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(groupPoolId))
        {
            if ((workingDisk.IsHotSpare || workingDisk.IsRetired) && CanStageDiskSpecialRole(workingDisk))
            {
                try
                {
                    CommitWorkingStep(EditWorkspace.SetDiskUsage(_working, diskId, string.Empty));
                }
                catch (InvalidOperationException exception)
                {
                    PublishOperationException(
                        ViewModel.Localization["OperationFailed"],
                        "storage-structure-editor",
                        exception,
                        "structure.edit.exception");
                }
                return;
            }
            if (EditWorkspace.DiskIsAssignedToTier(_working, diskId))
                CommitWorkingStep(EditWorkspace.EvictDiskToUnallocated(_working, diskId));
            return;
        }

        var role = EditWorkspace.DiskUsage(workingDisk);
        if (role.Length > 0)
        {
            // Dropping a retired / hot-spare disk onto the Unallocated group
            // leaves the simulated layer and stays unallocated in the pool.
            try
            {
                CommitWorkingStep(EditWorkspace.SetDiskUsage(_working, diskId, string.Empty));
            }
            catch (InvalidOperationException exception)
            {
                PublishOperationException(
                    ViewModel.Localization["OperationFailed"],
                    "storage-structure-editor",
                    exception,
                    "structure.edit.exception");
            }

            return;
        }

        if (!EditWorkspace.DiskIsAssignedToTier(_working, diskId))
        {
            return;
        }

        var prepared = await PrepareDiskSpecialRoleDropAsync(workingDisk);
        if (prepared is null)
        {
            return;
        }

        try
        {
            CommitWorkingStep(EditWorkspace.EvictDiskToUnallocated(prepared, diskId));
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private async Task DropDiskIntoPoolAsync(string diskId, string poolId, string? targetTierId = null)
    {
        if (!CanEditDraft)
        {
            return;
        }

        if (!ViewModel.IsUsingSimulatedInventory && !CanStageRealMemberRelationship(diskId, poolId))
        {
            await ShowMessageAsync(Text("成员关系不受支持", "Member relationship unsupported"),
                Text("该磁盘身份、运行角色或目标池不允许建立此草稿关系。", "The disk identity, runtime roles or target pool do not permit this draft relationship."));
            return;
        }
        MergeFormIntoWorking();
        // Drag-out rule: a real pool whose structure modification is
        // unsupported freezes its ORIGINAL committed members. A disk moved
        // in during this session can always be dragged back out.
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        var sourcePoolId = workingDisk?.PoolStableId;
        if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(poolId)
            && _working.StoragePools.Any(pool => pool.StableId == poolId && !pool.IsPrimordial)
            && !string.Equals(sourcePoolId, poolId, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(
                ViewModel.Localization["StructureChangeBlockedTitle"],
                Text("现有池成员关系不能在此更改；请先解散原池后再重建。", "Existing pool membership cannot change here; dissolve the pool before rebuilding it."));
            return;
        }
        var originalRealMember = IsOriginalConcreteRealMember(diskId, sourcePoolId);
        if (originalRealMember && !string.Equals(sourcePoolId, poolId, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(
                ViewModel.Localization["StructureChangeBlockedTitle"],
                Text("现有真实池成员需先解散原池后才能加入其他池。", "An existing real pool member can join another pool after its current pool is dissolved."));
            return;
        }
        if (!ViewModel.IsUsingSimulatedInventory && (workingDisk is { IsHotSpare: true } or { IsRetired: true })
            && !string.Equals(sourcePoolId, poolId, StringComparison.OrdinalIgnoreCase)
            && sourcePoolId is not null && !EditWorkspace.IsDraftPool(sourcePoolId))
        {
            await ShowMessageAsync(
                Text("成员关系不受支持", "Member relationship unsupported"),
                Text("现有池成员只能在原池内恢复用途；不能在此迁移到其他池。", "An existing pool member can have its usage restored in its current pool, but cannot migrate to another pool here."));
            return;
        }
        if (!string.IsNullOrEmpty(sourcePoolId)
            && !EditWorkspace.IsDraftPool(sourcePoolId)
            && _working.StoragePools.FirstOrDefault(candidate =>
                candidate.StableId == sourcePoolId) is { IsPrimordial: false } sourcePoolInfo
            && !EditWorkspace.PoolSupportsStructureModification(_working, sourcePoolInfo.StableId))
        {
            var committedDisk = EditingSession.Baseline.PhysicalDisks.FirstOrDefault(
                item => item.StableId == diskId);
            var wasOriginalMember = committedDisk is not null
                && string.Equals(
                    committedDisk.PoolStableId,
                    sourcePoolId,
                    StringComparison.OrdinalIgnoreCase);
            if (wasOriginalMember && sourcePoolId != poolId && EditWorkspace.DiskIsAssignedToTier(_working, diskId))
            {
                await ShowMessageAsync(
                    ViewModel.Localization["StructureChangeBlockedTitle"],
                    ViewModel.Localization["DataBearingMemberMoveBlockedMessage"]);
                return;
            }
        }

        var priorState = CaptureDraftState();
        try
        {
            var next = _working;
            if (EditWorkspace.IsPlus(poolId))
            {
                next = CreateDraftPoolStep(next);
            }
            else if (!EditWorkspace.IsPoolRow(poolId))
            {
                if (workingDisk!.IsBoot || workingDisk.IsSystem)
                {
                    await ShowMessageAsync(
                        ViewModel.Localization["SystemDiskCannotEvictTitle"],
                        ViewModel.Localization["SystemDiskCannotEvictMessage"]);
                    return;
                }
                if (workingDisk.IsPageFile || workingDisk.IsCrashDump)
                {
                    await ShowMessageAsync(
                        ViewModel.Localization["StructureChangeBlockedTitle"],
                        Text(
                            "该磁盘承担页面文件或崩溃转储角色，不能在池之间移动。请先移除这些角色。",
                            "This disk has a page-file or crash-dump role and cannot move between pools. Remove those roles first."));
                    return;
                }

                var restoresExistingRealMember = !ViewModel.IsUsingSimulatedInventory
                    && !EditWorkspace.IsDraftPool(poolId)
                    && string.Equals(sourcePoolId, poolId, StringComparison.OrdinalIgnoreCase);
                // Restoring usage in the same real pool does not move a member
                // or allocate tier extents. Preserve the provider's pool size
                // and unused template facts until the role write is verified.
                next = restoresExistingRealMember
                    ? workingDisk.IsHotSpare || workingDisk.IsRetired
                        ? EditWorkspace.SetDiskUsage(next, diskId, string.Empty) : next
                    : EditWorkspace.MoveDiskToPool(next, diskId, poolId);
                if (targetTierId is not null && !restoresExistingRealMember)
                    next = next with { StorageTiers = next.StorageTiers.Select(tier => tier.PoolStableId != poolId
                        ? tier : tier with { MemberPhysicalDiskIds = tier.MemberPhysicalDiskIds
                            .Where(id => id != diskId).Concat(tier.StableId == targetTierId ? new[] { diskId } : Array.Empty<string>()).ToArray() }).ToArray() };
                if (!ViewModel.IsUsingSimulatedInventory)
                {
                    // Do not invent a RAW OS disk when a draft relationship
                    // returns a member to the unassigned group. Windows facts
                    // are obtained only after the controlled operation.
                    next = next with { OsDisks = next.OsDisks.Where(item =>
                        item.PhysicalDiskStableId is null || _working.OsDisks.Any(prior =>
                            prior.StableId == item.StableId)).ToArray() };
                }
            }

            if (EditWorkspace.IsDraftPool(poolId))
            {
                var draftPool = next.StoragePools.First(item => item.StableId == poolId);
                RememberNameDefault(poolId, "Pool", draftPool.FriendlyName);
                var intent = _poolIntents.GetValueOrDefault(poolId) ?? CreateAutomaticIntent(draftPool.FriendlyName);
                _poolIntents[poolId] = intent with { Layout = CreationLayout(next, poolId) };
            }

            if (EditWorkspace.IsDraftPool(poolId)
                && (_poolIntents.GetValueOrDefault(poolId)?.AutoCreateVirtualDisk ?? true)
                && next.VirtualDisks.All(item => !string.Equals(
                    item.PoolStableId,
                    poolId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                var draftPool = next.StoragePools.First(item => item.StableId == poolId);
                var virtualDiskName = _poolIntents.GetValueOrDefault(poolId)?.VirtualDiskName ?? draftPool.FriendlyName;
                next = InsertVirtualDiskDraft(
                    next,
                    poolId,
                    virtualDiskName,
                    "Simple",
                    65536);
                RememberNameDefault(poolId, "VirtualDisk", virtualDiskName);
                RememberNameDefault(poolId, "Volume", _poolIntents.GetValueOrDefault(poolId)?.VolumeName ?? virtualDiskName);
                _poolIntents[poolId] = _poolIntents.TryGetValue(poolId, out var existingIntent)
                    ? existingIntent with
                    {
                        AutoCreateVirtualDisk = true,
                        AutoCreatePartition = true
                    }
                    : CreateAutomaticIntent(draftPool.FriendlyName);
            }

            if (!ReferenceEquals(next, _working))
            {
                _selectedPoolId = poolId;
                _selectedPoolDiskId = diskId;
                _selectedPoolTierId = null;
                _selectedPoolVdiskId = null;
                _undoStack.Push(priorState);
                _redoStack.Clear();
                _working = next;
                _formDirty = false;
                _formHistoryCaptured = false;
                ResetLayerSwitchesForSelection();
                RefreshAll();
            }
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    /// <summary>
    /// Shared gate for retiring, hot-sparing, and evicting a pooled disk.
    /// Boot and system disks are refused immediately. Evictable special
    /// roles are staged in the same undoable draft step and summarized by
    /// the single Apply confirmation.
    /// </summary>
    private async Task<StorageSnapshot?> PrepareDiskSpecialRoleDropAsync(PhysicalDiskInfo disk)
    {
        if (IsPhysicalDiskOffline(disk.StableId))
        {
            await ShowMessageAsync(
                Text("磁盘已脱机", "Disk offline"),
                Text("脱机磁盘只能查看，请先联机。", "An offline disk is read-only. Bring it online first."));
            return null;
        }

        if (EditWorkspace.ClassifyDiskEvict(disk) == EditWorkspace.DiskEvictCheck.DeniedSystem)
        {
            await ShowMessageAsync(
                ViewModel.Localization["SystemDiskCannotEvictTitle"],
                ViewModel.Localization["SystemDiskCannotEvictMessage"]);
            return null;
        }

        return disk.IsPageFile || disk.IsCrashDump
            ? EditWorkspace.ClearEvictableSpecialRoles(_working, disk.StableId)
            : _working;
    }

    private bool IsPhysicalDiskOffline(string physicalDiskId) =>
        _working.OsDisks.Any(item =>
            item.PhysicalDiskStableId == physicalDiskId && item.IsOffline);

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

    private void FillPoolForm()
    {
        _filling = true;
        try
        {
            UpdateAutomaticCreationChrome();
            var pool = SelectedPool();
            var multi = pool is not null && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId);
            _multiVdiskWarning.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

            foreach (var group in TierGroups())
            {
                var visible = pool is not null
                    && !pool.IsPrimordial
                    && TierVisible(pool.StableId, group.Media);
                group.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                foreach (var element in group.Rows)
                {
                    element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }

                // Collapse hidden tier rows while keeping visible rows at the
                // shared 40 DIP minimum and allowing wrapped content to grow.
                foreach (var index in group.RowIndices)
                {
                    if (index < group.FormGrid.RowDefinitions.Count)
                    {
                        group.FormGrid.RowDefinitions[index].MinHeight = visible ? 40 : 0;
                    }
                }
            }

            if (pool is null || pool.IsPrimordial)
            {
                FillRecommendedDefaults();
                return;
            }

            var tierCache = TierMap(pool.StableId);
            _poolNameBox.Text = pool.FriendlyName;
            _lastPoolName = pool.FriendlyName;
            var vdisk = _working.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            var virtualDiskName = vdisk?.FriendlyName ?? pool.FriendlyName;
            _virtualDiskNameBox.Text = virtualDiskName;
            var partition = PrimaryPartition(pool.StableId);
            if (partition is not null) partition = StructurePartitionBaseline.Resolve(_working, partition);
            var volumeName = partition is not null
                ? partition.FileSystemLabel
                : vdisk?.FriendlyName ?? pool.FriendlyName;
            _volumeNameBox.Text = volumeName;
            if (EditWorkspace.IsDraftPool(pool.StableId))
                RememberNameDefault(pool.StableId, "Pool", pool.FriendlyName);
            if (vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId))
                RememberNameDefault(pool.StableId, "VirtualDisk", virtualDiskName);
            if (partition is null || CommittedPrimaryPartition(pool.StableId) is null)
                RememberNameDefault(pool.StableId, "Volume", volumeName);
            if ((ViewModel.IsUsingSimulatedInventory || EditWorkspace.IsDraftPool(pool.StableId))
                && (EditWorkspace.IsDraftPool(pool.StableId)
                    || vdisk is null
                    || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId)))
            {
                foreach (var group in TierGroups().Where(item => TierVisible(pool.StableId, item.Media)))
                {
                    var key = MaximumKey(group);
                    if (_capacityDefaultModesInitialized.Add(key))
                        _maximumSizeFields.Add(key);
                }
            }
            foreach (var group in TierGroups())
            {
                var tier = tierCache.GetValueOrDefault(group.Media);
                FillTierForm(group, tier);
            }

            _partitionStyleBox.SelectedItem = "GPT";
            _fileSystemBox.SelectedItem = "NTFS";
            RefreshClusterChoices();
            _clusterBox.SelectedItem = "64 KiB";
            if (partition is not null)
            {
                if (vdisk is not null)
                {
                    var osDisk = _working.OsDisks.FirstOrDefault(item =>
                        item.VirtualDiskStableId == vdisk.StableId);
                    if (osDisk is not null)
                    {
                        var style = osDisk.PartitionStyle.Trim().ToUpperInvariant();
                        _partitionStyleBox.SelectedItem = style is "MBR" ? "MBR" : "GPT";
                    }
                }

                if (!string.IsNullOrWhiteSpace(partition.FileSystem)
                    && _fileSystemBox.Items.Contains(partition.FileSystem))
                {
                    _fileSystemBox.SelectedItem = partition.FileSystem;
                }

                RefreshClusterChoices();
                if (partition.AllocationUnitSize is long cluster)
                {
                    SelectClusterSize(cluster);
                }
            }

            if (_poolIntents.TryGetValue(pool.StableId, out var intent))
            {
                _partitionStyleBox.SelectedItem = intent.PartitionStyle;
                if (vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId))
                    _virtualDiskNameBox.Text = intent.VirtualDiskName ?? vdisk?.FriendlyName ?? pool.FriendlyName;
                if (_fileSystemBox.Items.Contains(StructurePartitionBaseline.CanonicalFileSystem(intent.FileSystem)))
                {
                    _fileSystemBox.SelectedItem = StructurePartitionBaseline.CanonicalFileSystem(intent.FileSystem);
                }
                RefreshClusterChoices();
                SelectClusterSize(intent.AllocationUnitSize);
                if (intent.PendingAutomaticLayout || CommittedPrimaryPartition(pool.StableId) is null)
                {
                    _volumeNameBox.Text = intent.VolumeName;
                }
            }

            if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(pool.StableId)
                && (vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId)))
            {
                RestoreRealCreationTierGoals(pool, intent);
            }

            _lastVirtualDiskName = _virtualDiskNameBox.Text;
            foreach (var group in TierGroups()) RefreshSuggestedColumns(group, updateDraft: true);
            RefreshClusterChoices();
        }
        finally
        {
            _filling = false;
            _formDirty = false;
            _formHistoryCaptured = false;
        }
    }

    private void RefreshTierVisibility()
    {
        var pool = SelectedPool();
        foreach (var group in TierGroups())
        {
            var visible = pool is { IsPrimordial: false } && TierVisible(pool.StableId, group.Media);
            group.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var element in group.Rows) element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var index in group.RowIndices)
                if (index < group.FormGrid.RowDefinitions.Count)
                    group.FormGrid.RowDefinitions[index].MinHeight = visible ? 40 : 0;
        }
    }

    private void FillRecommendedDefaults()
    {
        var nextName = NextPoolName();
        _poolNameBox.Text = nextName;
        _lastPoolName = nextName;
        _virtualDiskNameBox.Text = nextName;
        _lastVirtualDiskName = nextName;
        _volumeNameBox.Text = nextName;
        foreach (var group in TierGroups())
        {
            SetResiliency(group.ResiliencyBox, "Mirror");
            SetInterleave(group.InterleaveBox, 65536);
            SetCapacityText(group, string.Empty);
            SetNum(group.ColumnsBox, null);
            SetNum(group.CopiesBox, 2);
            SetNum(group.FailuresBox, 1);
            group.DiskCountBox.Text = string.Empty;
        }

        SetResiliency(Capacity.ResiliencyBox, "Parity");
        SetNum(Capacity.CopiesBox, 1);
        _partitionStyleBox.SelectedItem = "GPT";
        _fileSystemBox.SelectedItem = "NTFS";
        RefreshClusterChoices();
        _clusterBox.SelectedItem = "64 KiB";
        UpdateLinkedFields();
    }

    private void FillTierForm(TierFields group, StorageTierInfo? tier)
    {
        if (tier is null)
        {
            SetResiliency(group.ResiliencyBox, group.Media == "HDD" ? "Parity" : "Mirror");
            SetInterleave(group.InterleaveBox, 65536);
            SetCapacityText(group, string.Empty);
            SetNum(group.ColumnsBox, null);
            SetNum(group.CopiesBox, 2);
            SetNum(group.FailuresBox, 1);
            group.DiskCountBox.Text = "0";
            group.ProvisioningBox.SelectedItem = "Fixed";
            UpdateMaximumSizeText(group);
            return;
        }

        if (!EditingSession.Baseline.StorageTiers.Any(actual => actual.StableId == tier.StableId))
        {
            tier = ColumnChoice(group).ApplyLayout(tier, TierDataDisks(group.Media));
            _working = _working with { StorageTiers = _working.StorageTiers.Select(item => item.StableId == tier.StableId
                ? tier : item).ToArray() };
        }
        SetResiliency(group.ResiliencyBox, tier.ResiliencySettingName);
        var isDraft = EditWorkspace.IsDraftPool(tier.PoolStableId);
        if (tier.Interleave is { } knownUnit) SetInterleave(group.InterleaveBox, knownUnit);
        else if (isDraft || ViewModel.IsUsingSimulatedInventory) SetInterleave(group.InterleaveBox, 65536);
        else group.InterleaveBox.SelectedItem = null;
        var capacityUnavailable = _working.FieldIssues.Any(issue =>
            issue.ObjectId.Equals(tier.StableId, StringComparison.OrdinalIgnoreCase)
            && issue.FieldName.Equals(nameof(StorageTierInfo.Size), StringComparison.OrdinalIgnoreCase)
            && issue.State != FieldReadState.Returned);
        var tierBytes = tier.Size > 0
            ? tier.Size
            : isDraft ? TierCapacityMaxBytes(group.Media) : 0;
        // Only a new draft has a planner default. A real tier with a missing
        // source Size stays blank; a returned zero is intentionally shown.
        if (!ViewModel.IsUsingSimulatedInventory)
            SetCapacityText(group, !capacityUnavailable ? CreationGigabytes(tier.Size) : string.Empty);
        else
            SetCapacityText(
                group,
                isDraft || !capacityUnavailable
                    ? Math.Round(tierBytes / 1024d / 1024d / 1024d, 2).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
                    : string.Empty);
        SetNum(group.ColumnsBox, tier.NumberOfColumns ?? (ViewModel.IsUsingSimulatedInventory || isDraft
            ? EditWorkspace.RecommendedTierColumns(tier.ResiliencySettingName, TierDataDisks(group.Media))
            : (int?)null));
        SetNum(group.CopiesBox, tier.NumberOfDataCopies ?? (isDraft || ViewModel.IsUsingSimulatedInventory ? 1 : (int?)null));
        SetNum(group.FailuresBox, tier.PhysicalDiskRedundancy ?? (isDraft || ViewModel.IsUsingSimulatedInventory ? 1 : (int?)null));
        group.DiskCountBox.Text = tier.MemberPhysicalDiskIds.Count.ToString();
        group.ProvisioningBox.SelectedItem = "Fixed";
        UpdateMaximumSizeText(group);
    }

    private void RestoreRealCreationTierGoals(StoragePoolInfo pool, PoolEditIntent? intent)
    {
        var savedGoals = intent?.Tiers;
        foreach (var group in TierGroups().Where(item => TierVisible(pool.StableId, item.Media)))
        {
            var goal = savedGoals?.FirstOrDefault(item =>
                EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
            if (goal is not null)
            {
                // A verified source template is immutable. Keep its actual
                // parameters from FillTierForm while restoring the saved size.
                var tier = TierMap(pool.StableId).GetValueOrDefault(group.Media);
                var immutableRealTemplate = tier is not null && IsBaselineRealTemplate(pool, tier);
                if (goal.VerifiedTemplateId is null && !immutableRealTemplate)
                {
                    SetResiliency(group.ResiliencyBox, goal.Layout.ResiliencySettingName);
                    SetInterleave(group.InterleaveBox, goal.Layout.InterleaveBytes);
                    SetNum(group.ColumnsBox, goal.Layout.NumberOfColumns);
                    SetNum(group.CopiesBox, goal.Layout.NumberOfDataCopies);
                    SetNum(group.FailuresBox, goal.Layout.PhysicalDiskRedundancy);
                }

                if (goal.UseMaximumSize)
                {
                    _maximumSizeFields.Add(MaximumKey(group));
                    UpdateMaximumSizeText(group);
                }
                else
                {
                    _maximumSizeFields.Remove(MaximumKey(group));
                    SetCapacityText(group, CreationGigabytes(goal.SizeBytes));
                }

                continue;
            }

            // An explicit empty tier list represents a role-only pool or a
            // deliberately unfinished tier goal. Do not invent capacities.
            if (savedGoals is not null)
            {
                continue;
            }

            // Old saved intents had one aggregate size field. Keep that value
            // on HDD for compatibility and use per-media MAX for other tiers.
            if (group.Media == "HDD" && intent?.VirtualDiskUseMaximum == false)
            {
                _maximumSizeFields.Remove(MaximumKey(group));
                SetCapacityText(group, CreationGigabytes(intent.VirtualDiskSizeBytes));
            }
            else
            {
                _maximumSizeFields.Add(MaximumKey(group));
                UpdateMaximumSizeText(group);
            }
        }
    }

    private Dictionary<string, StorageTierInfo> TierMap(string poolId) =>
        EditWorkspace.SelectPoolTiersForForm(
            _working, poolId, _selectedPoolTierId, _selectedPoolVdiskId,
            ViewModel.IsUsingSimulatedInventory);

    private bool IsBaselineRealTemplate(StoragePoolInfo pool, StorageTierInfo tier) =>
        !EditWorkspace.IsDraftPool(pool.StableId)
        && EditingSession.Baseline.StorageTiers.Any(item =>
            item.StableId.Equals(tier.StableId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && item.VirtualDiskStableId is null);

    private int DataDiskCount(StoragePoolInfo pool, string media) =>
        _working.PhysicalDisks.Count(disk =>
            string.Equals(disk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage)
            && EditWorkspace.NormalizeMedia(disk.MediaType) == media);

    /// <summary>Source templates remain visible before any space is allocated.</summary>
    private bool TierVisible(string poolId, string media) =>
        TierMap(poolId).GetValueOrDefault(media) is { } tier
            && (!EditWorkspace.IsDraftPool(poolId) && !ViewModel.IsUsingSimulatedInventory
                || tier.MemberPhysicalDiskIds.Count > 0);

    /// <summary>Member disk count of the tier (0 when the tier does not exist).</summary>
    private int TierMemberCount(string poolId, string media) =>
        TierDataDisks(media).Count;

    private PartitionInfo? PrimaryPartition(string poolId)
    {
        var vdisk = _working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase));
        if (vdisk is null)
        {
            return null;
        }

        var osDisk = _working.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        return _working.Partitions
            .Where(item => item.OsDiskStableId == osDisk?.StableId
                && item.Type is "Primary" or "BasicData")
            .OrderBy(item => item.Offset)
            .FirstOrDefault();
    }

    private IReadOnlyList<PartitionInfo> UserPartitions(string poolId)
    {
        var vdisk = _working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase));
        if (vdisk is null)
        {
            return [];
        }

        var osDisk = _working.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        return _working.Partitions
            .Where(item => item.OsDiskStableId == osDisk?.StableId
                && item.Type is "Primary" or "BasicData")
            .OrderBy(item => item.Offset)
            .ToArray();
    }

    private PartitionInfo? CommittedPrimaryPartition(string poolId) =>
        StructurePartitionBaseline.ForPool(EditingSession.Baseline, poolId);

    private static void SetResiliency(ComboBox box, string value)
    {
        var match = box.Items.OfType<string>().FirstOrDefault(item =>
            item.Equals(value, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<string>().First();
    }

    private static void SetInterleave(ComboBox box, long bytes)
    {
        var token = InterleaveToken(bytes);
        if (!box.Items.OfType<string>().Contains(token))
        {
            box.Items.Add(token);
        }
        box.SelectedItem = token;
    }

    private static string ToGigabytes(long? bytes) =>
        bytes is > 0 ? Math.Round(bytes.Value / 1024d / 1024d / 1024d, 2).ToString("0.##") : string.Empty;

    private void UpdateLinkedFields()
    {
        if (_filling)
        {
            return;
        }

        _filling = true;
        try
        {
            LinkResiliency(Performance);
            LinkResiliency(Capacity);
            LinkResiliency(Dedicated);
            RefreshClusterChoices();
        }
        finally { _filling = false; }
    }

    /// <summary>
    /// One linking rule for every tier group: Mirror keeps the data-copies
    /// number (min 2) and derives tolerated failures from it; Parity fixes
    /// one data copy and keeps the editable tolerated-failure number; Simple
    /// fixes both at 1 / 0. Media type does not change these rules.
    /// </summary>
    private void LinkResiliency(TierFields group)
    {
        var resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
        if (resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            SetNum(group.CopiesBox, 1);
            SetNum(group.FailuresBox, 0);
        }
        else if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            var copies = NumValue(group.CopiesBox) is { } existing && existing >= 2
                ? (int)existing
                : 2;
            SetNum(group.CopiesBox, copies);
            SetNum(group.FailuresBox, Math.Max(0, copies - 1));
        }
        else
        {
            SetNum(group.CopiesBox, 1);
            var tolerated = NumValue(group.FailuresBox) is { } failures && failures >= 1
                ? (int)failures
                : 1;
            SetNum(group.FailuresBox, tolerated);
        }

        RefreshColumnInputBounds(group);
        RefreshSuggestedColumns(group);
    }

    private string ColumnSuggestionKey(TierFields group) => $"{EditingSession.SystemId}|{EditingSession.BindingGeneration}|{_selectedPoolId}|{group.Media}";

    private StructureColumnSuggestion ColumnChoice(TierFields group) =>
        _columnSuggestions.GetValueOrDefault(ColumnSuggestionKey(group)) ?? new();

    private void MarkExplicitColumns(NumberBox number)
    {
        var group = TierGroups().FirstOrDefault(group => ReferenceEquals(group.ColumnsBox, number));
        if (group is null || NumValue(number) is not { } value) return;
        _columnSuggestions[ColumnSuggestionKey(group)] = ColumnChoice(group).Choose((int)value);
    }

    private void RefreshSuggestedColumns(TierFields group, bool updateDraft = false)
    {
        var tier = SelectedTier(group.Media);
        if (tier is null || EditingSession.Baseline.StorageTiers.Any(actual => actual.StableId == tier.StableId)) return;
        // A member refill runs with SelectionChanged suppressed. Update the
        // NumberBox range before assigning its new recommendation, otherwise
        // the previous member count silently clamps it to the old maximum.
        RefreshColumnInputBounds(group);
        var key = ColumnSuggestionKey(group);
        if (!_columnSuggestions.TryGetValue(key, out var suggestion))
            _columnSuggestions[key] = suggestion = new();
        var columns = suggestion.Resolve(group.ResiliencyBox.SelectedItem as string ?? "Simple",
            TierDataDisks(group.Media), (int)(NumValue(group.CopiesBox) ?? 1));
        SetNum(group.ColumnsBox, columns);
        if (updateDraft && tier.NumberOfColumns != columns)
            _working = _working with { StorageTiers = _working.StorageTiers.Select(item => item.StableId == tier.StableId
                ? item with { NumberOfColumns = columns } : item).ToArray() };
    }

    private void RefreshColumnInputBounds(TierFields group)
    {
        var resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
        var diskCount = TierDataDisks(group.Media).Count;
        var copies = Math.Max(1, (int)(NumValue(group.CopiesBox) ?? 1));
        var failures = Math.Max(0, (int)(NumValue(group.FailuresBox) ?? 0));
        var minimum = resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, failures) + 2
            : 1;
        var maximum = resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase)
            ? diskCount / copies
            : diskCount;
        maximum = Math.Min(64, Math.Max(minimum, maximum));
        group.ColumnsBox.Minimum = minimum;
        group.ColumnsBox.Maximum = maximum;

        if (NumValue(group.ColumnsBox) is { } columns
            && (!_columnSuggestions.TryGetValue(ColumnSuggestionKey(group), out var suggestion)
                || suggestion.ExplicitColumns is null))
            SetNum(group.ColumnsBox, Math.Clamp(columns, minimum, maximum));
    }

    private (IReadOnlyList<VirtualDiskInfo> All, VirtualDiskInfo? DeleteTarget) PoolVirtualDiskState(
        StoragePoolInfo? pool)
    {
        if (pool is null || pool.IsPrimordial)
        {
            return ([], null);
        }

        var all = _working.VirtualDisks
            .Where(item => string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        VirtualDiskInfo? target = null;
        if (all.Length > 0)
        {
            target = _selectedPoolVdiskId is not null
                ? all.FirstOrDefault(item =>
                    item.StableId.Equals(_selectedPoolVdiskId, StringComparison.OrdinalIgnoreCase))
                : all.Length == 1 ? all[0] : null;
        }

        return (all, target);
    }

    /// <summary>
    /// Any uncommitted draft content: structural draft steps, object-backed
    /// property edits, form-level partition edits, or plain dirty fields.
    /// </summary>
    private bool HasUncommittedChanges() =>
        (_formDirty && SelectedPool() is { IsPrimordial: false })
        || StructureDraftReentry.HasDraftContent(EditingSession);

    private void RefreshPendingActions()
    {
        PendingActionsPanel.Children.Clear();
        _planBuildError = string.Empty;
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            _currentPlan = null;
            _realDraftPreview = null;
            try
            {
                var previewSnapshot = _formDirty ? ApplyFormToWorking(_working) : _working;
                var state = CaptureDraftState();
                var intents = new Dictionary<string, PoolEditIntent>(state.PoolIntents, StringComparer.OrdinalIgnoreCase);
                if (SelectedPool() is { IsPrimordial: false } selected)
                    intents[selected.StableId] = SelectedCreationIntent();
                var candidate = EditingSession.ForkPreview(state with { Snapshot = previewSnapshot, PoolIntents = intents });
                _realDraftPreview = BuildRealStructureDraftPreview(candidate);
                if (!string.IsNullOrWhiteSpace(EditingSession.RealApplyMessage))
                    PendingActionsPanel.Children.Add(new TextBlock
                    {
                        Text = EditingSession.RealApplyMessage, TextWrapping = TextWrapping.Wrap
                    });
                foreach (var action in _realDraftPreview.Presentation)
                    PendingActionsPanel.Children.Add(RenderPendingAction(action));
                foreach (var reason in _realDraftPreview.BlockingReasons)
                    PendingActionsPanel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });
                if (EditingSession.HasBaselineConflict)
                    PendingActionsPanel.Children.Add(new TextBlock
                    {
                        Text = Text("实时基线冲突，已保留未应用草稿；放弃后可按新事实编辑。", EditingSession.ConflictReason),
                        TextWrapping = TextWrapping.Wrap
                    });
                if (_realDraftPreview.Actions.Count == 0 && _realDraftPreview.BlockingReasons.Count == 0)
                    PendingActionsPanel.Children.Add(new TextBlock { Text = ViewModel.Localization["NoPendingActions"], TextWrapping = TextWrapping.Wrap });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                _planBuildError = exception.Message;
                PendingActionsPanel.Children.Add(new TextBlock { Text = _planBuildError, TextWrapping = TextWrapping.Wrap });
            }
            return;
        }
        try
        {
            var projected = _formDirty ? ApplyFormToWorking(_working) : _working;
            var built = SimulationDraftPlanner.Build(EditingSession.Baseline, projected);
            var enriched = built.Steps.Select(EnrichPlanIntent).ToList();
            AppendPartitionIntents(enriched);
            _currentPlan = SimulationDraftPlanner.Precheck(EditingSession.Baseline, enriched);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            _ = exception;
            _currentPlan = null;
            _planBuildError = Text(
                "当前草稿无法生成可应用的操作；请检查字段值和待处理操作。",
                "The current draft cannot produce applicable operations; check field values and pending operations.");
        }

        if (_currentPlan is null || _currentPlan.IsEmpty)
        {
            var empty = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_planBuildError)
                    ? HasInvalidSizeInput()
                        ? Text("容量必须是有效的非负 GiB 数值。", "Capacity must be a valid non-negative GiB value.")
                        : ViewModel.Localization["NoPendingActions"]
                    : _planBuildError,
                TextWrapping = TextWrapping.Wrap
            };
            PendingActionsPanel.Children.Add(empty);
            return;
        }

        if (HasInvalidSizeInput())
        {
            PendingActionsPanel.Children.Add(new TextBlock
            {
                Text = Text("容量必须是有效的非负 GiB 数值。", "Capacity must be a valid non-negative GiB value."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            });
        }

        foreach (var item in _currentPlan.DisplayItems)
        {
            var title = DescribePlanStep(item.Request);
            PendingActionsPanel.Children.Add(RenderPendingAction(PendingStructureAction.FromSimulation(item, title)));
        }
    }

    private SimulationEditRequest EnrichPlanIntent(SimulationEditRequest step)
    {
        if (step.Kind is not (SimulationEditKind.CreateTieredPool
                or SimulationEditKind.CreateVirtualDisk
                or SimulationEditKind.UpdateStoragePool))
        {
            return step;
        }

        var intentKey = step.Kind == SimulationEditKind.CreateTieredPool
            ? step.DraftSourceId
            : step.TargetProviderKey;
        _poolIntents.TryGetValue(intentKey ?? string.Empty, out var intent);
        var selected = intentKey is not null
            && intentKey.Equals(_selectedPoolId, StringComparison.OrdinalIgnoreCase);
        bool UsesMaximum(TierFields group, string media) =>
            intentKey is not null && _maximumSizeFields.Contains($"{intentKey}|{media}")
            || selected && _maximumSizeFields.Contains(MaximumKey(group));

        return step with
        {
            FileSystem = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.FileSystem ?? (selected ? _fileSystemBox.SelectedItem as string : null) ?? step.FileSystem
                : step.FileSystem,
            AllocationUnitSize = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.AllocationUnitSize
                    ?? (selected ? SelectedClusterSize() : (long?)null)
                    ?? step.AllocationUnitSize
                : step.AllocationUnitSize,
            VolumeName = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.VolumeName ?? (selected ? _volumeNameBox.Text.Trim() : null) ?? step.VolumeName
                : step.VolumeName,
            CreateVirtualDisk = step.Kind == SimulationEditKind.CreateTieredPool
                ? false
                : step.CreateVirtualDisk,
            CreatePartition = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? false
                : step.CreatePartition,
            PerformanceUseMaximum = UsesMaximum(Performance, "SSD"),
            CapacityUseMaximum = UsesMaximum(Capacity, "HDD"),
            ScmUseMaximum = UsesMaximum(Dedicated, "SCM"),
            ProvisioningType = "Fixed"
        };
    }

    private static string StableIntentObjectId(string prefix, string? intentKey)
    {
        var source = string.IsNullOrWhiteSpace(intentKey) ? "pending" : intentKey;
        var suffix = source[(source.LastIndexOf(':') + 1)..];
        return $"{prefix}:{suffix}";
    }

    /// <summary>
    /// A new pool targets a VD and its partition; explicit deletion changes that goal.
    /// </summary>
    private PoolEditIntent CreateAutomaticIntent(string volumeName) =>
        StructureCreationPolicy.NewPool(volumeName, ViewModel.CurrentPreferences.CreateMsrOnInitialize);

    private static string CreationGigabytes(long? bytes) =>
        RealVirtualDiskCreationCapacityResolver.FormatGigabytes(bytes);

    private bool TryReadCreationSize(out long? bytes, out bool maximum)
    {
        var pool = SelectedPool();
        var creatingRealVirtualDisk = !ViewModel.IsUsingSimulatedInventory
            && pool is { IsPrimordial: false }
            && !EditWorkspace.IsDraftPool(pool.StableId)
            && !_working.VirtualDisks.Any(item => item.PoolStableId == pool.StableId
                && !EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var groups = creatingRealVirtualDisk
            ? TierGroups().Where(group => TierVisible(pool!.StableId, group.Media)).ToArray()
            : _selectedPoolId is not null && !TierVisible(_selectedPoolId, "HDD")
                ? Array.Empty<TierFields>()
                : new[] { Capacity };
        if (groups.Length == 0)
        {
            bytes = null;
            maximum = true;
            return true;
        }

        long totalBytes = 0;
        maximum = false;
        foreach (var group in groups)
        {
            if (_maximumSizeFields.Contains(MaximumKey(group)))
            {
                maximum = true;
                continue;
            }

            var text = group.SizeBox.Text.Trim();
            if (!IntegerInputPolicy.ContainsOnlyDigits(text)
                || !long.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var gigabytes)
                || gigabytes <= 0
                || gigabytes > CapacityMaximumIntegerGiB(group))
            {
                bytes = null;
                return false;
            }

            try
            {
                totalBytes = checked(totalBytes + checked(gigabytes * MaximumCapacityAlgorithm.GiB));
            }
            catch (OverflowException)
            {
                bytes = null;
                return false;
            }
        }

        bytes = maximum ? null : totalBytes;
        return true;
    }

    private PoolVirtualDiskLayout CreationLayout(string? poolId) => CreationLayout(_working, poolId);

    private PoolVirtualDiskLayout CreationLayout(StorageSnapshot snapshot, string? poolId)
    {
        if (poolId is null) return PoolVirtualDiskLayout.Ordinary;
        if (snapshot.StorageTiers.Any(tier => tier.PoolStableId == poolId
            && (tier.MemberPhysicalDiskIds.Count > 0 || !EditWorkspace.IsDraftPool(poolId))))
            return PoolVirtualDiskLayout.HddTiered;
        // Preserve the remaining automatic tier goal after a verified pool creation.
        return _poolIntents.TryGetValue(poolId, out var saved) ? saved.Layout : PoolVirtualDiskLayout.Ordinary;
    }

    private PoolEditIntent SelectedCreationIntent()
    {
        var savedPartition = _selectedPoolId is null ? null : CommittedPrimaryPartition(_selectedPoolId);
        // A replacement VD has its own layout inputs. The deleted VD's volume
        // remains in the baseline, but must not override the new label field.
        if (savedPartition is not null && _working.Partitions.All(item => item.StableId != savedPartition.StableId))
            savedPartition = null;
        _poolIntents.TryGetValue(_selectedPoolId ?? string.Empty, out var prior);
        // A partially verified workflow keeps the frozen original layout goal.
        // Provider-created unformatted partitions and later preference changes
        // must not replace its label, format or MSR/letter choices.
        if (prior is { PendingAutomaticLayout: true }) return prior;
        var layout = CreationLayout(_selectedPoolId);
        TryReadCreationSize(out var bytes, out var maximum);
        IReadOnlyList<PoolTierEditIntent>? tierGoals = null;
        IReadOnlyList<PoolMemberEditIntent>? memberGoals = null;
        if (!ViewModel.IsUsingSimulatedInventory && SelectedPool() is { } pool)
        {
            var poolMembers = _working.PhysicalDisks.Where(disk => pool.MemberPhysicalDiskIds.Contains(
                disk.StableId, StringComparer.OrdinalIgnoreCase)).ToArray();
            memberGoals = poolMembers
                .Select(disk => new PoolMemberEditIntent(disk.StableId, PhysicalDiskUsage.Normalize(disk.Usage))).ToArray();
            if (layout == PoolVirtualDiskLayout.HddTiered)
            {
                var dataMedia = poolMembers.Where(disk => PhysicalDiskUsage.ContributesDataCapacity(disk.Usage))
                    .Select(disk => EditWorkspace.NormalizeMedia(disk.MediaType))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var capturedGoals = new List<PoolTierEditIntent>();
                var tierMap = TierMap(pool.StableId);
                foreach (var group in TierGroups().Where(group => dataMedia.Contains(group.Media)
                    && TierVisible(pool.StableId, group.Media)))
                {
                    if (!tierMap.TryGetValue(group.Media, out var tier)) continue;
                    var savedGoal = prior?.Tiers?.FirstOrDefault(item =>
                        EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
                    var useActualLayout = IsBaselineRealTemplate(pool, tier);
                    var resiliency = useActualLayout
                        ? tier.ResiliencySettingName
                        : group.ResiliencyBox.SelectedItem as string ?? tier.ResiliencySettingName;
                    int copies;
                    int redundancy;
                    if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
                    {
                        copies = useActualLayout
                            ? tier.NumberOfDataCopies ?? savedGoal?.Layout.NumberOfDataCopies ?? 2
                            : (int)(NumValue(group.CopiesBox) ?? tier.NumberOfDataCopies ?? 2);
                        redundancy = copies - 1;
                    }
                    else if (resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase))
                    {
                        copies = 1;
                        redundancy = useActualLayout
                            ? tier.PhysicalDiskRedundancy ?? savedGoal?.Layout.PhysicalDiskRedundancy ?? 1
                            : (int)(NumValue(group.FailuresBox) ?? tier.PhysicalDiskRedundancy ?? 1);
                    }
                    else
                    {
                        copies = 1;
                        redundancy = 0;
                    }

                    var columns = useActualLayout
                        ? tier.NumberOfColumns ?? savedGoal?.Layout.NumberOfColumns
                        : (int?)(NumValue(group.ColumnsBox) ?? tier.NumberOfColumns);
                    var interleave = useActualLayout
                        ? tier.Interleave ?? savedGoal?.Layout.InterleaveBytes ?? 65536
                        : ParseSize(group.InterleaveBox.SelectedItem as string ?? "64 KiB");
                    if (columns is null)
                    {
                        if (savedGoal is not null) capturedGoals.Add(savedGoal);
                        continue;
                    }

                    var spec = new StorageLayoutParameters(resiliency, columns.Value,
                        checked((int)interleave), copies, redundancy);
                    var isMaximum = _maximumSizeFields.Contains(MaximumKey(group));
                    var requestedBytes = isMaximum ? 0 : long.TryParse(group.SizeBox.Text.Trim(), out var integerGiB)
                        ? checked(integerGiB * MaximumCapacityAlgorithm.GiB) : 0;
                    var verifiedTemplateId = useActualLayout ? tier.StableId : null;
                    capturedGoals.Add(new PoolTierEditIntent(group.Media, spec, requestedBytes, isMaximum,
                        verifiedTemplateId));
                }

                // Keep a saved per-media goal if the refreshed provider view
                // temporarily omitted its source template after pool remapping.
                capturedGoals.AddRange((prior?.Tiers ?? []).Where(goal => dataMedia.Contains(
                        EditWorkspace.NormalizeMedia(goal.MediaType))
                    && capturedGoals.All(current => !current.MediaType.Equals(
                        EditWorkspace.NormalizeMedia(goal.MediaType), StringComparison.OrdinalIgnoreCase))));
                tierGoals = capturedGoals;
                if (dataMedia.Count == 0 && !(_poolIntents.GetValueOrDefault(pool.StableId)?.AutoCreateVirtualDisk ?? false))
                    tierGoals = [];
                maximum = tierGoals.Any(tier => tier.UseMaximumSize);
                bytes = maximum ? null : tierGoals.Sum(tier => tier.SizeBytes);
            }
        }
        return new PoolEditIntent(
            StructureCreationPolicy.WantsVirtualDisk(_working, _selectedPoolId, prior),
            true,
            _fileSystemBox.SelectedItem as string ?? "NTFS",
            SelectedClusterSize(),
            savedPartition is null ? _volumeNameBox.Text.Trim() : savedPartition.FileSystemLabel,
            layout,
            _virtualDiskNameBox.Text.Trim(), bytes, maximum,
            ViewModel.CurrentPreferences.CreateMsrOnInitialize,
            prior?.DriveLetter,
            _partitionStyleBox.SelectedItem as string ?? "GPT",
            prior?.QuickFormat ?? true,
            prior?.VerifiedVirtualDiskId,
            prior?.PendingAutomaticLayout ?? false, tierGoals, memberGoals);
    }

    private void CaptureSelectedIntent()
    {
        if (_filling || EditingSession.RealResultSynchronizationPending || string.IsNullOrWhiteSpace(_selectedPoolId))
        {
            return;
        }

        var current = SelectedCreationIntent();
        if (!_poolIntents.TryGetValue(_selectedPoolId, out var previous) || !SamePoolIntent(previous, current))
            EditingSession.MarkDraftContentChanged();
        _poolIntents[_selectedPoolId] = current;
    }

    private EditorDraftState CaptureDraftState() => EditingSession.Capture();

    private void RestoreDraftState(EditorDraftState state)
    {
        EditingSession.Restore(state);
        foreach (var group in TierGroups())
            if (_columnSuggestions.TryGetValue(ColumnSuggestionKey(group), out var suggestion)
                && SelectedTier(group.Media) is { } tier)
                _columnSuggestions[ColumnSuggestionKey(group)] = suggestion with
                {
                    ExplicitColumns = suggestion.ExplicitColumns is null ? null : tier.NumberOfColumns,
                    ExplicitResiliency = suggestion.ExplicitResiliency is null ? null : tier.ResiliencySettingName,
                    ExplicitCopies = suggestion.ExplicitCopies is null ? null : tier.NumberOfDataCopies,
                    ExplicitFailures = suggestion.ExplicitFailures is null ? null : tier.PhysicalDiskRedundancy
                };
        _formDirty = false;
        _formHistoryCaptured = false;
    }

    private void AppendPartitionIntents(List<SimulationEditRequest> steps)
    {
        var intents = new Dictionary<string, PoolEditIntent>(_poolIntents, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_selectedPoolId))
        {
            intents[_selectedPoolId] = SelectedCreationIntent();
        }

        foreach (var pair in intents)
        {
            AppendPartitionIntent(steps, pair.Key, pair.Value);
        }
    }

    private void AppendPartitionIntent(
        List<SimulationEditRequest> steps,
        string poolId,
        PoolEditIntent intent)
    {
        var draftPool = EditWorkspace.IsDraftPool(poolId);
        var appliedPoolId = poolId;
        if (draftPool)
        {
            appliedPoolId = steps.FirstOrDefault(item =>
                item.Kind == SimulationEditKind.CreateTieredPool
                && item.DraftSourceId?.Equals(poolId, StringComparison.OrdinalIgnoreCase) == true)
                ?.AllocatedPoolId ?? string.Empty;
        }

        var createVdisk = steps.FirstOrDefault(item =>
            item.Kind == SimulationEditKind.CreateVirtualDisk
            && item.TargetProviderKey.Equals(appliedPoolId, StringComparison.OrdinalIgnoreCase));
        if (createVdisk?.AllocatedOsDiskId is not null)
        {
            SimulationDraftPlanner.ConfigureAutomaticPartitionIntent(steps, createVdisk, intent,
                StableIntentObjectId("sim:partition", poolId),
                StableIntentObjectId("sim:volume", poolId),
                StableIntentObjectId("sim:msr", poolId));
        }

        if (EditWorkspace.IsDraftPool(poolId))
        {
            return;
        }

        var partition = CommittedPrimaryPartition(poolId);
        var finalPartition = PrimaryPartition(poolId);
        if (partition is null
            || finalPartition is null
            || finalPartition.StableId != partition.StableId
            || steps.Any(item => item.Kind == SimulationEditKind.DeletePartition
                && item.TargetProviderKey.Equals(partition.StableId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var fileSystem = intent.FileSystem;
        var cluster = intent.AllocationUnitSize;
        var label = partition.FileSystemLabel;
        var formatChanged = !string.Equals(fileSystem, partition.FileSystem, StringComparison.OrdinalIgnoreCase)
            || cluster != partition.AllocationUnitSize;
        if (formatChanged)
        {
            steps.Add(new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                partition.StableId,
                Name: label,
                FileSystem: fileSystem,
                AllocationUnitSize: cluster));
            return;
        }

    }

    private FrameworkElement RenderPendingAction(PendingStructureAction action)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(action.IsChild ? 12 : 0, 0, 0, 6) };
        panel.Children.Add(new TextBlock { Text = action.Title, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var parameter in action.Parameters)
            panel.Children.Add(new TextBlock { Text = $"{PendingParameterLabel(parameter.Key)}: {parameter.Value}",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (action.DataLoss)
            panel.Children.Add(new TextBlock { Text = Text("会删除已使用的数据。", "Deletes used data."),
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });
        if (!string.IsNullOrWhiteSpace(action.Reason))
            panel.Children.Add(new TextBlock { Text = action.Reason, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });
        return panel;
    }

    private string PendingParameterLabel(string key) => key switch
    {
        "Target" => Text("目标", "Target"), "Name" => Text("名称", "Name"),
        "Members" => Text("成员", "Members"), "MemberUsage" => Text("成员角色", "Member usage"),
        "Usage" => Text("角色", "Usage"), "Media" => Text("介质", "Media"),
        "VirtualDiskName" => Text("虚拟磁盘名", "Virtual disk name"), "VolumeName" => Text("卷标", "Volume label"),
        "SizeBytes" => Text("容量（字节）", "Size (bytes)"), "OffsetBytes" => Text("起点（字节）", "Offset (bytes)"),
        "PartitionStyle" => Text("分区表", "Partition table"), "CreateMsr" => Text("创建 MSR", "Create MSR"),
        "FileSystem" => Text("文件系统", "File system"), "AllocationUnitSize" => Text("簇（字节）", "Cluster (bytes)"),
        "DriveLetter" => Text("盘符", "Drive letter"), "QuickFormat" => Text("快速格式化", "Quick format"),
        "Resiliency" => Text("复原类型", "Resiliency"), "Interleave" => Text("条带单元（字节）", "Stripe unit (bytes)"),
        "Columns" => Text("列数", "Columns"), "Copies" => Text("数据副本", "Data copies"),
        "Failures" => Text("容忍故障数", "Tolerated failures"), "Maximum" => "MAX",
        "Provisioning" => Text("预配", "Provisioning"), "Layout" => Text("布局", "Layout"),
        "Capacity" => Text("容量", "Capacity"), "CreatePartition" => Text("创建分区", "Create partition"),
        "CreateVirtualDisk" => Text("创建虚拟磁盘", "Create virtual disk"), "Offline" => Text("脱机", "Offline"),
        "PartitionKind" => Text("分区类型", "Partition type"), "Destination" => Text("目标组", "Destination group"),
        "AccessPaths" => Text("访问路径", "Access paths"), _ => key
    };

    private bool HasInvalidSizeInput()
    {
        var pool = SelectedPool();
        return pool is not null && (HasIncompleteNumberInput() || TierGroups().Any(group =>
            TierVisible(pool.StableId, group.Media)
            && group.SizeBox.IsEnabled
            && !_maximumSizeFields.Contains(MaximumKey(group))
            && (NumValue(group.SizeBox) is not { } size
                || size > CapacityMaximumIntegerGiB(group))
            && !CapacityTextMatchesCurrentTier(group)));
    }

    private bool CapacityTextMatchesCurrentTier(TierFields group)
    {
        var pool = SelectedPool();
        var tier = pool is null || pool.IsPrimordial
            ? null
            : TierMap(pool.StableId).GetValueOrDefault(group.Media);
        if (tier is null)
            return false;

        var expected = ViewModel.IsUsingSimulatedInventory
            ? Math.Round(tier.Size / 1024d / 1024d / 1024d, 2)
                .ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
            : CreationGigabytes(tier.Size);
        return string.Equals(group.SizeBox.Text, expected, StringComparison.Ordinal);
    }

    private void UpdateButtonState()
    {
        CommitLiveFormEdit();
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var pool = SelectedPool();
        var poolOffline = pool is not null
            && StorageEditRules.TouchesOfflineDisk(_working, [pool.StableId]);
        var (poolVdisks, deleteTarget) = PoolVirtualDiskState(pool);
        var realVdisk = poolVdisks.FirstOrDefault(item => !EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var formEnabled = CanEditDraft
            && pool is { IsPrimordial: false }
            && !poolOffline
            && poolVdisks.Count(item => !EditWorkspace.IsDraftVirtualDisk(item.StableId)) <= 1;

        // Capacity validation reads field availability. Refresh it for the
        // current selection before evaluating the plan and action buttons.
        UpdateFormStates(formEnabled, pool, realVdisk, realVdisk is not null);
        RefreshTierVisibility();
        RefreshPendingActions();
        UpdatePendingActionCount();
        RefreshFullStripes();
        var hasUnapplied = HasUncommittedChanges();

        UndoButton.IsEnabled = CanEditDraft && _undoStack.Count > 0;
        RedoButton.IsEnabled = CanEditDraft && _redoStack.Count > 0;
        DiscardAllButton.IsEnabled = hasUnapplied && !_applyingRealDraft && !IsRealStructureApplyInProgress && !_renameInProgress
            && (simulated || !ViewModel.IsRealOperationBusy);
        var planBlocked = _currentPlan?.DisplayItems.Any(item =>
            item.Decision?.Verdict != StorageRuleVerdict.Allow) == true;
        ApplyAllButton.IsEnabled = CanEditDraft
            && hasUnapplied
            && (simulated ? _currentPlan is { IsEmpty: false } : _realDraftPreview?.CanApply == true)
            && string.IsNullOrWhiteSpace(_planBuildError)
            && !planBlocked
            && !HasInvalidSizeInput()
            && !_outcomeUnknown;
        DissolveButton.IsEnabled = CanEditDraft
            && (simulated || EditWorkspace.IsDraftPool(pool?.StableId ?? "") || CanDissolveRealPool(pool))
            && pool is { IsPrimordial: false } && !poolOffline;
        var selectedDisk = _working!.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        var canLayerDisk = selectedDisk is not null
            && CanStageDiskSpecialRole(selectedDisk)
            && string.Equals(selectedDisk.PoolStableId, pool?.StableId, StringComparison.OrdinalIgnoreCase);
        RetireButton.IsEnabled = canLayerDisk && !selectedDisk!.IsRetired;
        HotSpareButton.IsEnabled = canLayerDisk && !selectedDisk!.IsHotSpare;

        var canEditPool = simulated
            && pool is { IsPrimordial: false }
            && !EditWorkspace.IsDraftPool(pool.StableId)
            && !poolOffline;
        var canCreateVirtualDisk = CanEditDraft && (canEditPool || CanCreateRealVirtualDisk(pool)
                || pool is { IsPrimordial: false } && EditWorkspace.IsDraftPool(pool.StableId))
            && poolVdisks.Count == 0 && !HasInvalidSizeInput()
            && (CanCreateRealVirtualDisk(pool)
                || pool!.MemberPhysicalDiskIds.Count > 0
                || DataDiskCount(pool!, "SSD") + DataDiskCount(pool!, "HDD")
                    + DataDiskCount(pool!, "SCM") > 0);
        var virtualDiskOffline = deleteTarget is not null && _working.OsDisks.Any(item =>
            item.VirtualDiskStableId == deleteTarget.StableId && item.IsOffline);
        var canDeleteVirtualDisk = CanEditDraft && (canEditPool || CanDeleteRealVirtualDisk(pool)
            || pool is { IsPrimordial: false } && EditWorkspace.IsDraftPool(pool.StableId))
            && deleteTarget is not null && !virtualDiskOffline;
        _virtualDiskActionDeletes = poolVdisks.Count > 0;
        CreateVdiskButton.IsEnabled = _virtualDiskActionDeletes ? canDeleteVirtualDisk : canCreateVirtualDisk;
        CreateVdiskButtonLabel.Text = _virtualDiskActionDeletes
            ? Text("删除虚拟磁盘", "Delete virtual disk") : Text("创建虚拟磁盘", "Create virtual disk");
        CreateVdiskButtonIcon.Glyph = _virtualDiskActionDeletes ? "\uE74D" : "\uE710";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CreateVdiskButton, CreateVdiskButtonLabel.Text);
        ContextHelp.Set(CreateVdiskButton, _virtualDiskActionDeletes
            ? Text("删除虚拟磁盘及其分区；真实删除会使其数据丢失。", "Delete the virtual disk and its partitions; real deletion loses their data.")
            : Text("创建待应用的虚拟磁盘草稿。", "Create a pending virtual-disk draft."));
        var structureReason = ResolveStructureDisabledReason(
            simulated,
            pool,
            poolOffline,
            poolVdisks.Count(item => !EditWorkspace.IsDraftVirtualDisk(item.StableId)));
        RestoreStructureActionHelp();
        var canPreviewSpecialRoles = simulated || pool is not null
            && (EditWorkspace.IsDraftPool(pool.StableId) || CanUseRealPool(pool));
        ShowHotSpareSwitch.IsEnabled = canPreviewSpecialRoles;
        ShowRetiredSwitch.IsEnabled = canPreviewSpecialRoles;
        SetDisabledReason(
            UndoButton,
            Text("没有可撤销的未应用修改。", "There is no unapplied change to undo."));
        SetDisabledReason(
            RedoButton,
            Text("没有可恢复的撤销修改。", "There is no undone change to redo."));
        SetDisabledReason(
            DiscardAllButton,
            Text("没有可放弃的未应用修改。", "There is no unapplied change to discard."));
        SetDisabledReason(
            ApplyAllButton,
            !simulated
                ? structureReason ?? _realDraftPreview?.BlockingReasons.FirstOrDefault()
                    ?? Text("没有可应用的真实草稿。", "There is no applicable real draft.")
                : !hasUnapplied
                    ? Text("没有待应用的模拟修改。", "There is no simulated change to apply.")
                    : !string.IsNullOrWhiteSpace(_planBuildError)
                        ? _planBuildError
                        : planBlocked
                            ? Text("待处理操作包含被规则阻止的步骤；请在待处理操作中查看原因。", "A pending operation is blocked by rules; see Pending actions for the reason.")
                            : HasInvalidSizeInput()
                                ? Text("容量必须是有效的非负 GiB 数值。", "Capacity must be a valid non-negative GiB value.")
                                : _outcomeUnknown
                                    ? structureReason
                                    : Text("需要可应用且未被规则阻止的修改。", "Applicable changes that pass the rules are required."));
        SetDisabledReason(
            DissolveButton,
            structureReason
                ?? (!simulated && pool is not null && !CanDissolveRealPool(pool)
                    ? Text("该池包含数据或无法核实的成员、布局或关联；当前不能解散。",
                        "This pool contains data or has members, layouts, or associations that cannot be verified for dissolution.")
                    : Text("请选择可解散的普通存储池。", "Select a normal storage pool to dissolve.")));
        var diskRoleReason = structureReason
            ?? (selectedDisk is null
                ? Text("请选择该池中的一块数据磁盘。", "Select a data disk in this pool.")
                : selectedDisk.IsBoot || selectedDisk.IsSystem
                    ? Text("启动盘或系统盘不能在池中切换为特殊角色。", "A boot or system disk cannot be assigned a special role in a pool.")
                    : !simulated && (selectedDisk.IsPageFile || selectedDisk.IsCrashDump || !selectedDisk.IsStable)
                        ? Text("该磁盘身份尚未核实或承担运行角色，不能更改用途。", "The disk identity is unverified or it has a runtime role, so its usage cannot change.")
                    : IsPhysicalDiskOffline(selectedDisk.StableId)
                        ? Text("该磁盘已脱机；请先在磁盘分区页联机。", "This disk is offline; bring it online in the disk partition page first.")
                        : !string.Equals(selectedDisk.PoolStableId, pool?.StableId, StringComparison.OrdinalIgnoreCase)
                            ? Text("请选择当前池中的成员磁盘。", "Select a member disk in the current pool.")
                            : !simulated && !EditWorkspace.IsDraftPool(pool?.StableId ?? string.Empty)
                                && (!CanUseRealPool(pool) || _working.VirtualDisks.Any(item => item.PoolStableId == pool?.StableId))
                                ? Text("现有空池中的用途可更改；含虚拟磁盘的池保持不变。", "Usage can change in an existing empty pool; pools with a virtual disk stay unchanged.")
                                : null);
        SetDisabledReason(
            RetireButton,
            diskRoleReason ?? Text("该磁盘已经标为已退役。", "This disk is already marked as retired."));
        SetDisabledReason(
            HotSpareButton,
            diskRoleReason ?? Text("该磁盘已经标为热备。", "This disk is already marked as a hot spare."));
        SetDisabledReason(
            CreateVdiskButton,
            structureReason ?? (_virtualDiskActionDeletes
                ? virtualDiskOffline
                    ? Text("该虚拟磁盘已脱机；请先在磁盘分区页联机。", "This virtual disk is offline; bring it online in Disk and partitions first.")
                    : Text("当前虚拟磁盘不能删除。", "The current virtual disk cannot be deleted.")
                : Text("该池需要至少一块数据磁盘后才能创建虚拟磁盘。", "This pool needs at least one data disk before a virtual disk can be created.")));
        SetDisabledReason(
            ShowHotSpareSwitch,
            Text("热备图层可在模拟池、新建草稿池或可编辑的现有空池中预览。", "Preview the hot-spare layer in a simulated pool, a new draft pool, or an eligible existing empty pool."));
        SetDisabledReason(
            ShowRetiredSwitch,
            Text("退役图层可在模拟池、新建草稿池或可编辑的现有空池中预览。", "Preview the retired layer in a simulated pool, a new draft pool, or an eligible existing empty pool."));
        UpdateFieldResets();
        UpdateNameActionStates();
        if (TopologyControl.ItemsSource is IEnumerable<TopologyNodeViewModel> roots)
            foreach (var root in roots) ApplyPendingModificationStates(root);
        foreach (var grid in _sectionHeaders.Keys) ApplySectionCollapse(grid);
        UpdateApplyAnimation(hasUnapplied && PendingActionCount > 0);
    }

    private string? ResolveStructureDisabledReason(
        bool simulated,
        StoragePoolInfo? pool,
        bool poolOffline,
        int realVirtualDiskCount)
    {
        if (_outcomeUnknown)
        {
            return Text("上次提交结果尚未核实，暂时不能继续编辑或应用。相关结果见消息，详细原因见开发页消息详情。",
                "The previous submission outcome remains unresolved, so editing and applying are blocked. Review the outcome in messages and the diagnostic details on the developer page.");
        }

        if (!simulated && ViewModel.RealOperationSubmission.IsBlocked)
        {
            return Text("先前真实操作仍在等待最终结果和刷新核对；核对完成前暂不能编辑。",
                "A previous real operation is still awaiting its final result and refreshed inventory. Editing is paused until reconciliation completes.");
        }

        if (!simulated && ViewModel.IsRealOperationBusy)
        {
            return Text("真实存储操作正在执行或刷新核对；请等待完成后再编辑。",
                "A real storage operation is running or refreshing its result. Wait for it to finish before editing.");
        }

        if (_applyingRealDraft || IsRealStructureApplyInProgress)
            return Text("正在执行并核对当前草稿。", "The current draft is executing and being verified.");

        if (EditingSession.HasBaselineConflict)
            return Text("实时基线已改变；草稿已保留。请放弃草稿后按新事实编辑。", EditingSession.ConflictReason);
        if (!simulated && !ViewModel.CanSubmitRealOperation)
            return Text("本机存储当前只读；请开启受控真实编辑。", "Local storage is read-only; enable controlled real editing.");

        if (pool is null)
        {
            return Text("请选择一个非原始存储池以编辑属性。",
                "Select a non-primordial storage pool to edit properties.");
        }

        if (pool.IsPrimordial)
        {
            return Text("原始池不可在此编辑；请选择或创建普通存储池。",
                "The primordial pool cannot be edited here; select or create a normal storage pool.");
        }

        if (poolOffline)
        {
            return Text("该池包含脱机磁盘；请先在磁盘分区页联机。",
                "This pool contains an offline disk; bring it online in the disk partition page first.");
        }

        if (realVirtualDiskCount > 1)
        {
            return Text("该池有多个现有虚拟磁盘；此表单不更改它们的共享属性。",
                "This pool has multiple existing virtual disks; this form does not change their shared properties.");
        }

        return null;
    }

    private void RestoreStructureActionHelp()
    {
        ContextHelp.Set(ApplyAllButton, ViewModel.IsUsingSimulatedInventory
            ? Text("确认风险后将待处理结构修改写入模拟系统。", "Write pending structural changes to the simulated system after confirming risks.")
            : Text("确认本次准确目标与全部结构变更后应用同一真实草稿。", "Confirm the exact target and all structural changes, then apply the same real draft."));

    }

    private static void SetDisabledReason(FrameworkElement element, string? reason) =>
        ContextHelp.SetDisabledReason(
            element,
            element is Control { IsEnabled: false } ? reason : null);

    private void UpdateFormStates(
        bool formEnabled,
        StoragePoolInfo? pool,
        VirtualDiskInfo? vdisk,
        bool hasVdisk)
    {
        if (!_formBuilt)
        {
            return;
        }

        // State refresh rewrites linked read-only values; those program
        // writes must never mark the form dirty.
        var wasFilling = _filling;
        _filling = true;
        try
        {
            UpdateFormStatesCore(formEnabled, pool, vdisk, hasVdisk);
        }
        finally
        {
            _filling = wasFilling;
        }
    }

    private void UpdateFormStatesCore(
        bool formEnabled,
        StoragePoolInfo? pool,
        VirtualDiskInfo? vdisk,
        bool hasVdisk)
    {
        var isDraft = pool is not null && EditWorkspace.IsDraftPool(pool.StableId);
        var real = !ViewModel.IsUsingSimulatedInventory;
        var pendingAutomaticLayout = pool is not null
            && _poolIntents.TryGetValue(pool.StableId, out var savedIntent) && savedIntent.PendingAutomaticLayout;
        var canConfigureCreation = real && formEnabled && !hasVdisk && !pendingAutomaticLayout;
        ContextHelp.Set(
            _poolNameBox,
            isDraft
                ? Text("名称将在创建时生效。", "The name takes effect when the object is created.")
                : Text("点击名称旁的勾选按钮直接提交池名；Enter 也可提交。", "Click the checkmark beside the name to submit the pool name; Enter also submits it."));
        var realPool = CanEditDraft && CanUseRealPool(pool);
        _poolNameBox.IsEnabled = formEnabled || realPool;
        _virtualDiskNameBox.IsEnabled = formEnabled || (realPool && vdisk is not null);
        var formDisabledReason = ResolveStructureDisabledReason(
            ViewModel.IsUsingSimulatedInventory,
            pool,
            pool is not null && StorageEditRules.TouchesOfflineDisk(_working, [pool.StableId]),
            pool is null
                ? 0
                : _working.VirtualDisks.Count(item => item.PoolStableId == pool.StableId
                    && !EditWorkspace.IsDraftVirtualDisk(item.StableId)))
            ?? Text("请选择可编辑的存储池。", "Select an editable storage pool.");
        SetDisabledReason(_poolNameBox, formDisabledReason);
        var volumePartition = pool is not null && vdisk is not null
            ? PrimaryPartition(pool.StableId)
            : null;
        var virtualDiskIsDraft = vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId);
        ContextHelp.Set(
            _virtualDiskNameBox,
            virtualDiskIsDraft
                ? Text("名称将在创建时生效。", "The name takes effect when the object is created.")
                : Text("点击名称旁的勾选按钮直接提交虚拟磁盘名；Enter 也可提交。", "Click the checkmark beside the name to submit the virtual-disk name; Enter also submits it."));
        ContextHelp.Set(
            _volumeNameBox,
            volumePartition is null
                ? Text("卷标将在创建时生效。", "The volume label takes effect when the volume is created.")
                : Text("点击名称旁的勾选按钮直接提交卷标；Enter 也可提交。", "Click the checkmark beside the name to submit the volume label; Enter also submits it."));
        _volumeNameBox.IsEnabled = (formEnabled
            && (vdisk is null || volumePartition is not null || canConfigureCreation))
            || (realPool && volumePartition is not null);
        SetDisabledReason(_virtualDiskNameBox, formDisabledReason);
        SetDisabledReason(
            _volumeNameBox,
            formEnabled && vdisk is not null && volumePartition is null
                ? Text("该虚拟磁盘尚未创建分区；请先在磁盘与分区页完成初始化。", "This virtual disk has no partition yet; initialize it in Disk and partitions first.")
                : formDisabledReason);
        if (pool is null)
        {
            foreach (var group in TierGroups())
            {
                RestoreTierHelp(group);
                group.SizeBox.IsEnabled = false;
                group.MaximumButton.IsEnabled = false;
                group.ProvisioningBox.IsEnabled = false;
                group.ResiliencyBox.IsEnabled = false;
                group.InterleaveBox.IsEnabled = false;
                group.CopiesBox.IsEnabled = false;
                group.FailuresBox.IsEnabled = false;
                group.ColumnsBox.IsEnabled = false;
                SetTierDisabledReasons(group, formDisabledReason, supportedInterleave: true);
            }
            _partitionStyleBox.IsEnabled = false;
            _fileSystemBox.IsEnabled = false;
            _clusterBox.IsEnabled = false;
            SetDisabledReason(_partitionStyleBox, formDisabledReason);
            SetDisabledReason(_fileSystemBox, formDisabledReason);
            SetDisabledReason(_clusterBox, formDisabledReason);
            return;
        }

        var holdsData = EditWorkspace.PoolHoldsStoredData(_working, pool.StableId);
        var parameterTiers = EditWorkspace.SelectPoolTiersForParameterUpdate(_working, pool.StableId);
        foreach (var group in TierGroups())
        {
            RestoreTierHelp(group);
            var tier = TierMap(pool.StableId).GetValueOrDefault(group.Media);
            var tierVisible = TierVisible(pool.StableId, group.Media);
            var realTierCreation = canConfigureCreation && !isDraft && tierVisible && tier is not null;
            var draftTierEditable = formEnabled && isDraft && tierVisible && tier is not null && !holdsData;
            var immutableSourceTemplate = tier is not null && IsBaselineRealTemplate(pool, tier);
            var exactParameterTier = tier is not null
                && parameterTiers.GetValueOrDefault(group.Media)?.StableId == tier.StableId;
            var sizeEditable = exactParameterTier
                && (realTierCreation || draftTierEditable || formEnabled && tierVisible && tier is not null && !holdsData && !real);
            var specEditable = sizeEditable && (!real || isDraft || !immutableSourceTemplate);
            var maximumBytes = tierVisible && tier is not null ? TierCapacityMaxBytes(group.Media) : 0;
            var maximumIntegerGiB = CapacityMaximumIntegerGiB(group);
            var maximumSelected = _maximumSizeFields.Contains(MaximumKey(group));
            group.SizeBox.IsEnabled = sizeEditable && maximumIntegerGiB > 0;
            group.MaximumButton.IsEnabled = exactParameterTier && maximumIntegerGiB > 0
                && (realTierCreation || draftTierEditable || formEnabled && !real
                    && tierVisible
                    && tier is not null
                    && maximumBytes > 0
                    && (!holdsData || maximumSelected || maximumBytes > tier.Size));
            var provisioning = string.IsNullOrWhiteSpace(vdisk?.ProvisioningType)
                ? "Fixed"
                : vdisk.ProvisioningType;
            if (!group.ProvisioningBox.Items.Contains(provisioning))
            {
                group.ProvisioningBox.Items.Add(provisioning);
            }
            group.ProvisioningBox.SelectedItem = provisioning;
            group.ProvisioningBox.IsEnabled = specEditable
                && provisioning.Equals("Fixed", StringComparison.OrdinalIgnoreCase);
            group.ResiliencyBox.IsEnabled = specEditable;
            var supportedInterleave = tier?.Interleave is null
                || tier.Interleave is 16384 or 32768 or 65536 or 131072 or 262144;
            group.InterleaveBox.IsEnabled = specEditable && supportedInterleave;
            ContextHelp.Set(
                group.InterleaveBox,
                supportedInterleave
                    ? Text("选择条带单元大小。",
                        "Choose the stripe unit size.")
                    : Text("当前 Interleave 值超出编辑范围，已按原值保留。", "The current interleave is outside the editable range and is preserved."));
            group.DiskCountBox.IsReadOnly = true;
            if (!tierVisible)
            {
                continue;
            }

            var resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
            var isMirror = resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase);
            var isSimple = resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase);
            // Linking happens once in the user edit transaction. A state refresh
            // must preserve an empty input and unknown provider facts.
            group.DiskCountBox.Text = TierMemberCount(pool.StableId, group.Media).ToString();
            group.CopiesBox.IsEnabled = specEditable && isMirror;
            group.FailuresBox.IsEnabled = specEditable && !isMirror && !isSimple;
            group.ColumnsBox.IsEnabled = specEditable;
            UpdateMaximumSizeText(group);
            IntegerTextInput.RefreshBounds(group.SizeBox);
            var disabledReason = ResolveStructureDisabledReason(
                ViewModel.IsUsingSimulatedInventory,
                pool,
                StorageEditRules.TouchesOfflineDisk(_working, [pool.StableId]),
                _working.VirtualDisks.Count(item => item.PoolStableId == pool.StableId
                    && !EditWorkspace.IsDraftVirtualDisk(item.StableId)))
                ?? (immutableSourceTemplate
                    ? Text("现有源层的实际布局保持不变；可为每个介质层指定容量。",
                        "Existing source-tier layouts stay as reported; set a capacity for each media tier.")
                    : holdsData
                    ? Text("池含有已存储数据；会改变结构的字段已锁定。",
                        "The pool holds stored data, so structure-changing fields are locked.")
                    : Text("此层字段受当前层和复原类型限制。",
                        "This tier field is limited by the current tier and resiliency type."));
            SetTierDisabledReasons(group, disabledReason, supportedInterleave);
            if (!exactParameterTier)
                SetTierDisabledReasons(group, Text(
                    "该介质包含多个实际层或多个未使用模板；当前参数编辑不能准确表达该目标。",
                    "This media has multiple allocated tiers or unused templates; this parameter editor cannot represent an exact target."), supportedInterleave);
            if (real && formEnabled && !canConfigureCreation && !isDraft)
            {
                var sizeReason = Text("已有真实虚拟磁盘和层的扩缩容当前不受支持。", "Resizing existing real virtual disks and tiers is currently unsupported.");
                SetDisabledReason(group.SizeBox, sizeReason);
                SetDisabledReason(group.MaximumButton, sizeReason);
            }
        }

        // Disk and partition group.
        var partition = PrimaryPartition(pool.StableId);
        var userPartitions = UserPartitions(pool.StableId);
        var canEditPartition = !hasVdisk && !holdsData;
        var partitionHasData = hasVdisk && EditWorkspace.DiskHoldsStoredData(
            _working,
            vdisk!.StableId,
            isVirtualDisk: true);
        var initialized = hasVdisk && partition is not null;
        _partitionStyleBox.IsEnabled = (!real || isDraft) && formEnabled
            && (canEditPartition || (hasVdisk && !initialized && !holdsData));
        var fsEditable = hasVdisk
            ? initialized && !partitionHasData && userPartitions.Count <= 1
            : canEditPartition;
        _fileSystemBox.IsEnabled = formEnabled && fsEditable && (!real || canConfigureCreation);
        _clusterBox.IsEnabled = formEnabled && fsEditable && (!real || canConfigureCreation);
        var partitionReason = formDisabledReason;
        if (formEnabled && hasVdisk && partitionHasData)
        {
            partitionReason = Text("该分区含有数据；格式相关属性已锁定。", "This partition contains data, so format-related properties are locked.");
        }
        SetDisabledReason(_partitionStyleBox, partitionReason);
        SetDisabledReason(_fileSystemBox, partitionReason);
        SetDisabledReason(_clusterBox, partitionReason);
    }

    private void RestoreTierHelp(TierFields group)
    {
        ContextHelp.Set(group.SizeBox, Text("以 GiB 输入层大小；合法输入即时更新草稿，按 Enter 规范化。", "Enter the tier size in GiB; valid input updates the draft immediately. Press Enter to normalize."));
        ContextHelp.Set(group.MaximumButton, Text("使用 WinPool 内置算法计算整数 GiB 最大容量。", "Calculate a whole GiB maximum using WinPool's built-in algorithm."));
        ContextHelp.Set(group.ProvisioningBox, Text("当前仅支持固定预配。", "Fixed provisioning is currently supported."));
        ContextHelp.Set(group.ResiliencyBox, Text("选择支持的复原类型。", "Choose a supported resiliency type."));
        ContextHelp.Set(group.InterleaveBox, Text("选择条带单元大小。", "Choose the stripe unit size."));
        ContextHelp.Set(group.CopiesBox, Text("镜像层的数据副本数；按 Enter 规范化。", "Data-copy count for a mirror tier; press Enter to normalize."));
        ContextHelp.Set(group.FailuresBox, Text("奇偶校验层可容忍的物理磁盘故障数；按 Enter 规范化。", "Physical-disk failures tolerated by a parity tier; press Enter to normalize."));
        ContextHelp.Set(group.ColumnsBox, Text("列数须符合数据盘数和副本数；按 Enter 规范化。", "Columns must fit the data disk and copy counts; press Enter to normalize."));
        ContextHelp.Set(group.DiskCountBox, Text("显示当前层的成员磁盘数量，只读。", "Shows the number of member disks in this tier; read-only."));
    }

    private void SetTierDisabledReasons(
        TierFields group,
        string? disabledReason,
        bool supportedInterleave)
    {
        SetDisabledReason(group.SizeBox, disabledReason);
        SetDisabledReason(group.MaximumButton, disabledReason);
        SetDisabledReason(group.ProvisioningBox, disabledReason);
        SetDisabledReason(group.ResiliencyBox, disabledReason);
        SetDisabledReason(
            group.InterleaveBox,
            supportedInterleave
                ? disabledReason
                : Text("当前 Interleave 值超出编辑范围，已按原值保留。", "The current interleave is outside the editable range and is preserved."));
        SetDisabledReason(group.CopiesBox, disabledReason);
        SetDisabledReason(group.FailuresBox, disabledReason);
        SetDisabledReason(group.ColumnsBox, disabledReason);
    }

    private void ShowHotSpareSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // The switch only previews an empty hot-spare layer for dragging;
        // a layer that holds disks is drawn regardless.
        RefreshTopology();
    }

    private void ShowRetiredSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        RefreshTopology();
    }

    private string NextPoolName()
    {
        var index = _working.StoragePools.Count(item => !item.IsPrimordial) + 1;
        return $"Pool{index:00}";
    }

    // ---- Structural draft step helpers ---------------------------------

    /// <summary>
    /// Folds pending property-form edits into the working copy without
    /// creating a step. Called at the start of every structural action so a
    /// form edit is never silently dropped by a later topology refresh.
    /// </summary>
    private void MergeFormIntoWorking()
    {
        if (!_formDirty)
        {
            return;
        }

        CaptureSelectedIntent();
        var merged = ApplyFormToWorking(_working);
        _working = merged;
        _formDirty = false;
        _formHistoryCaptured = false;
    }

    private void CommitWorkingStep(StorageSnapshot next)
    {
        if (ReferenceEquals(next, _working))
        {
            // An intent-only update keeps the current form text and working objects.
            _formDirty = false;
            _formHistoryCaptured = false;
            UpdateButtonState();
            return;
        }

        _undoStack.Push(CaptureDraftState());
        _redoStack.Clear();
        _working = next;
        _formDirty = false;
        _formHistoryCaptured = false;
        RefreshAll();
    }

    private void CreateDraftPoolAndSelect()
    {
        if (!CanEditDraft
            || _working.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            return;
        }

        MergeFormIntoWorking();
        try
        {
            var next = CreateDraftPoolStep(_working);
            var draftId = next.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
            var draftName = next.StoragePools.First(item => item.StableId == draftId).FriendlyName;
            RememberNameDefault(draftId, "Pool", draftName);
            RememberNameDefault(draftId, "VirtualDisk", draftName);
            RememberNameDefault(draftId, "Volume", draftName);
            _poolIntents[draftId] = CreateAutomaticIntent(draftName);
            if (next.StoragePools.First(item => item.StableId == draftId).MemberPhysicalDiskIds.Count > 0)
            {
                next = InsertVirtualDiskDraft(
                    next,
                    draftId,
                    draftName,
                    "Simple",
                    65536);
            }

            _selectedPoolId = draftId;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            ResetLayerSwitchesForSelection();
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private static StorageSnapshot CreateDraftPoolStep(StorageSnapshot snapshot)
    {
        var existing = snapshot.StoragePools.LastOrDefault(item => EditWorkspace.IsDraftPool(item.StableId));
        if (existing is not null)
        {
            return snapshot;
        }

        var index = snapshot.StoragePools.Count(item => !item.IsPrimordial) + 1;
        return EditWorkspace.InsertDraftPool(snapshot, $"Pool{index:00}");
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditDraft) return;
        if (_undoStack.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["UndoStepTitle"],
                ViewModel.Localization["UndoStepMessage"]);
            return;
        }

        CaptureSelectedIntent();
        if (_formDirty)
        {
            _working = ApplyFormToWorking(_working);
        }
        EditingSession.Undo();
        _formDirty = false;
        _formHistoryCaptured = false;
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditDraft) return;
        if (_redoStack.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["RedoStepTitle"],
                ViewModel.Localization["RedoStepMessage"]);
            return;
        }

        EditingSession.Redo();
        _formDirty = false;
        _formHistoryCaptured = false;
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    private async void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingRealDraft || IsRealStructureApplyInProgress || _renameInProgress
            || !ViewModel.IsUsingSimulatedInventory && ViewModel.IsRealOperationBusy) return;
        if (!HasUncommittedChanges())
        {
            return;
        }

        var discardSession = EditingSession;
        var discardSystemId = discardSession.SystemId;
        if (!await ConfirmAsync(
                Text("放弃所有修改", "Discard all changes"),
                Text(
                    "确定放弃所有未应用的修改？草稿和撤销记录都将清除。",
                    "Discard all unapplied changes? The draft and its undo history will be cleared.")))
        {
            return;
        }

        if (!ReferenceEquals(EditingSession, discardSession)
            || ViewModel.SelectedSystem.SystemId != discardSystemId) return;

        _undoStack.Clear();
        _redoStack.Clear();
        _maximumSizeFields.Clear();
        _capacityDefaultModesInitialized.Clear();
        _poolIntents.Clear();
        _nameDefaults.Clear();
        _outcomeUnknown = false;
        EditingSession.Bind(ViewModel.SelectedSystem, ViewModel.EffectiveActiveSnapshot);
        _formDirty = false;
        _formHistoryCaptured = false;
        _selectedPoolId = null;
        _selectedPoolDiskId = null;
        _selectedPoolVdiskId = null;
        _selectedPoolTierId = null;
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    /// <summary>Keeps only selection ids that still exist in the working copy.</summary>
    private void RestoreRealSelection(string? memberId) => RestoreRealSelection(null, memberId);

    private void RestoreRealSelection(IReadOnlyCollection<string>? memberIds, string? memberId)
    {
        NormalizeSelection();
        var selectedPool = _selectedPoolId is null ? null : _working.StoragePools.FirstOrDefault(pool =>
            pool.StableId.Equals(_selectedPoolId, StringComparison.OrdinalIgnoreCase));
        if (selectedPool is not null)
        {
            if (_selectedPoolDiskId is not null
                && !selectedPool.MemberPhysicalDiskIds.Contains(_selectedPoolDiskId, StringComparer.OrdinalIgnoreCase))
                _selectedPoolDiskId = null;
            return;
        }

        if (memberIds is { Count: > 0 })
        {
            var matches = _working.StoragePools.Where(pool => !pool.IsPrimordial
                && SamePhysicalMemberSet(pool.MemberPhysicalDiskIds, memberIds)).ToArray();
            if (matches.Length == 1)
            {
                _selectedPoolId = matches[0].StableId;
                _selectedPoolDiskId = memberId is not null
                    && matches[0].MemberPhysicalDiskIds.Contains(memberId, StringComparer.OrdinalIgnoreCase)
                    ? memberId : null;
                return;
            }
        }

        if (memberId is null) return;
        var pools = _working.StoragePools.Where(pool => !pool.IsPrimordial
            && pool.MemberPhysicalDiskIds.Contains(memberId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pools.Length == 1)
        {
            _selectedPoolId = pools[0].StableId;
            _selectedPoolDiskId = memberId;
        }
    }

    private static bool SamePhysicalMemberSet(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count
        && left.Distinct(StringComparer.OrdinalIgnoreCase).Count() == left.Count
        && right.Distinct(StringComparer.OrdinalIgnoreCase).Count() == right.Count
        && left.All(id => right.Contains(id, StringComparer.OrdinalIgnoreCase));

    private void NormalizeSelection()
    {
        if (_selectedPoolId is not null
            && !_working.StoragePools.Any(item =>
                item.StableId.Equals(_selectedPoolId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedPoolId = null;
        }

        if (_selectedPoolDiskId is not null
            && !_working.PhysicalDisks.Any(item =>
                item.StableId.Equals(_selectedPoolDiskId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedPoolDiskId = null;
        }

        if (_selectedPoolVdiskId is not null
            && !_working.VirtualDisks.Any(item =>
                item.StableId.Equals(_selectedPoolVdiskId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedPoolVdiskId = null;
        }

        if (_selectedPoolTierId is not null
            && !_working.StorageTiers.Any(item =>
                item.StableId.Equals(_selectedPoolTierId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedPoolTierId = null;
        }
    }

    private void CreatePool_Click(object sender, RoutedEventArgs e) => CreateDraftPoolAndSelect();

    private bool CanUseRealPool(StoragePoolInfo? pool)
    {
        if (!ViewModel.CanSubmitRealOperation || pool is not { IsStable: true, IsPrimordial: false }
            || EditWorkspace.IsDraftPool(pool.StableId)
            || pool.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.MemberPhysicalDiskIds.Count)
            return false;

        var baselinePool = EditingSession.Baseline.StoragePools.FirstOrDefault(item =>
            item.StableId.Equals(pool.StableId, StringComparison.OrdinalIgnoreCase));
        if (baselinePool is not { IsStable: true, IsPrimordial: false }
            || !SamePhysicalMemberSet(pool.MemberPhysicalDiskIds, baselinePool.MemberPhysicalDiskIds)
            || StorageEditRules.TouchesOfflineDisk(_working, [pool.StableId]))
            return false;

        foreach (var memberId in pool.MemberPhysicalDiskIds)
        {
            var members = _working.PhysicalDisks.Where(disk =>
                disk.StableId.Equals(memberId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var baselineMembers = EditingSession.Baseline.PhysicalDisks.Where(disk =>
                disk.StableId.Equals(memberId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (members.Length != 1 || baselineMembers.Length != 1
                || members[0] is not { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }
                || baselineMembers[0].IsBoot || baselineMembers[0].IsSystem
                || baselineMembers[0].IsPageFile || baselineMembers[0].IsCrashDump
                || EditWorkspace.NormalizeMedia(members[0].MediaType) == "Unknown"
                || PhysicalDiskUsage.IsUnknown(members[0].Usage))
                return false;
        }

        return true;
    }

    private StorageTierInfo[] RealPoolTiers(StoragePoolInfo pool) =>
        _working.StorageTiers.Where(tier =>
            string.Equals(tier.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)).ToArray();

    private bool CanDissolveRealPool(StoragePoolInfo? pool)
    {
        if (!CanUseRealPool(pool)) return false;
        var targetPool = pool!;
        var vdisks = _working.VirtualDisks.Where(item =>
            string.Equals(item.PoolStableId, targetPool.StableId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (vdisks.Length > 1) return false;

        var vdiskIds = vdisks.Select(item => item.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tiers = RealPoolTiers(targetPool);
        if (tiers.Select(tier => tier.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tiers.Length)
            return false;
        foreach (var tier in tiers)
        {
            if (!tier.IsStable || !string.Equals(tier.PoolStableId, targetPool.StableId, StringComparison.OrdinalIgnoreCase)
                || EditWorkspace.NormalizeMedia(tier.MediaType) == "Unknown"
                || tier.VirtualDiskStableId is not null && !vdiskIds.Contains(tier.VirtualDiskStableId))
                return false;

            var resiliency = tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase) ? "Simple"
                : tier.ResiliencySettingName.Equals("Mirror", StringComparison.OrdinalIgnoreCase) ? "Mirror"
                : tier.ResiliencySettingName.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? "Parity"
                : string.Empty;
            if (resiliency.Length == 0 || tier.NumberOfColumns is not { } columns
                || tier.Interleave is not { } interleave || interleave is < 16384 or > 262144
                || interleave > int.MaxValue)
                return false;
            var copies = tier.NumberOfDataCopies ?? (resiliency == "Simple" ? 1 : resiliency == "Mirror" ? 2 : 1);
            var redundancy = tier.PhysicalDiskRedundancy ?? (resiliency == "Simple" ? 0 : resiliency == "Mirror" ? copies - 1 : 0);
            try
            {
                var layout = new StorageLayoutParameters(resiliency, columns, checked((int)interleave), copies, redundancy);
                layout.Validate();
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return true;
    }

    private bool CanCreateRealVirtualDisk(StoragePoolInfo? pool) =>
        CanDissolveRealPool(pool)
        && !_working.VirtualDisks.Any(item => string.Equals(item.PoolStableId, pool!.StableId, StringComparison.OrdinalIgnoreCase))
        && pool!.MemberPhysicalDiskIds.Any(memberId => _working.PhysicalDisks.Any(disk =>
            disk.StableId.Equals(memberId, StringComparison.OrdinalIgnoreCase)
            && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage)));

    private bool CanDeleteRealVirtualDisk(StoragePoolInfo? pool) =>
        CanDissolveRealPool(pool)
        && _working.VirtualDisks.Count(item => string.Equals(item.PoolStableId, pool!.StableId, StringComparison.OrdinalIgnoreCase)) == 1;

    private void Dissolve_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (!CanEditDraft || pool is null || pool.IsPrimordial
            || (!ViewModel.IsUsingSimulatedInventory
                && !EditWorkspace.IsDraftPool(pool.StableId)
                && !CanDissolveRealPool(pool))) return;

        MergeFormIntoWorking();
        try
        {
            var next = _working;
            next = !ViewModel.IsUsingSimulatedInventory
                ? SimulationEditingSession.RemoveRealPoolDraft(next, pool.StableId)
                : EditWorkspace.IsDraftPool(pool.StableId)
                    ? EditWorkspace.DiscardDraftPool(next, pool.StableId)
                    : EditWorkspace.DissolvePoolInWorking(next, pool.StableId);

            _selectedPoolId = null;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            _selectedPoolTierId = null;
            ResetLayerSwitchesForSelection();
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private async void Retire_Click(object sender, RoutedEventArgs e) =>
        await ChangeDiskUsageAsync("Retired");

    private async void HotSpare_Click(object sender, RoutedEventArgs e) =>
        await ChangeDiskUsageAsync("HotSpare");

    private async Task ChangeDiskUsageAsync(string usage)
    {
        MergeFormIntoWorking();
        var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        var pool = SelectedPool();
        if (disk is null
            || pool is null
            || pool.IsPrimordial
            || !CanStageDiskSpecialRole(disk)
            || !string.Equals(disk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prepared = await PrepareDiskSpecialRoleDropAsync(disk);
        if (prepared is null)
        {
            return;
        }

        try
        {
            var next = EditWorkspace.SetDiskUsage(prepared, disk.StableId, usage);
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private StorageSnapshot InsertVirtualDiskDraft(StorageSnapshot snapshot, string poolId,
        string name, string resiliency, long interleave) => ViewModel.IsUsingSimulatedInventory
        ? EditWorkspace.InsertDraftVirtualDisk(snapshot, poolId, name, resiliency, interleave)
        : SimulationEditingSession.InsertRealDraftVirtualDisk(snapshot, poolId, name);

    private void CreateVdisk_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (!CanEditDraft || pool is null || pool.IsPrimordial
            || !ViewModel.IsUsingSimulatedInventory && HasInvalidSizeInput()
            || (!ViewModel.IsUsingSimulatedInventory
                && !EditWorkspace.IsDraftPool(pool.StableId)
                && !CanCreateRealVirtualDisk(pool))) return;

        var priorState = CaptureDraftState();
        CaptureSelectedIntent();
        MergeFormIntoWorking();
        var existing = _working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return;
        }

        var name = _virtualDiskNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = pool.FriendlyName;
        }

        // The virtual disk inherits the leading real tier's specification.
        var (resiliency, interleave) = PrimaryVdiskSpec(pool);
        try
        {
            var next = InsertVirtualDiskDraft(_working, pool.StableId, name, resiliency, interleave);
            RememberNameDefault(pool.StableId, "VirtualDisk", name);
            RememberNameDefault(pool.StableId, "Volume", _poolIntents.GetValueOrDefault(pool.StableId)?.VolumeName ?? name);
            _selectedPoolVdiskId = next.VirtualDisks.Last(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)).StableId;
            _selectedPoolDiskId = null;
            _undoStack.Push(priorState);
            _redoStack.Clear();
            _working = next;
            _poolIntents[pool.StableId] = StructureCreationPolicy.ChooseVirtualDisk(
                _poolIntents.GetValueOrDefault(pool.StableId) ?? CreateAutomaticIntent(name), true);
            _formDirty = false;
            _formHistoryCaptured = false;
            RefreshAll();
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    private (string Resiliency, long Interleave) PrimaryVdiskSpec(StoragePoolInfo pool)
    {
        foreach (var media in new[] { "SSD", "HDD", "SCM" })
        {
            var tier = TierMap(pool.StableId).GetValueOrDefault(media);
            if (tier is not null && tier.MemberPhysicalDiskIds.Count > 0)
            {
                return (tier.ResiliencySettingName, tier.Interleave ?? 65536);
            }
        }

        return ("Simple", 65536);
    }

    private void DeleteVdisk_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (!CanEditDraft || pool is null || pool.IsPrimordial
            || (!ViewModel.IsUsingSimulatedInventory
                && !EditWorkspace.IsDraftPool(pool.StableId)
                && !CanDeleteRealVirtualDisk(pool))) return;

        MergeFormIntoWorking();
        var (_, deleteTarget) = PoolVirtualDiskState(pool);
        if (deleteTarget is null)
        {
            return;
        }

        var vdisk = deleteTarget;

        try
        {
            var next = ViewModel.IsUsingSimulatedInventory
                ? EditWorkspace.DeleteVirtualDiskFromWorking(_working, vdisk.StableId)
                : SimulationEditingSession.RemoveRealVirtualDiskDraft(_working, vdisk.StableId);
            _undoStack.Push(CaptureDraftState());
            _redoStack.Clear();
            _selectedPoolVdiskId = null;
            _poolIntents[pool.StableId] = StructureCreationPolicy.ChooseVirtualDisk(
                _poolIntents.GetValueOrDefault(pool.StableId) ?? CreateAutomaticIntent(pool.FriendlyName), false);
            _working = next;
            _formDirty = false;
            _formHistoryCaptured = false;
            RefreshAll();
        }
        catch (InvalidOperationException exception)
        {
            PublishOperationException(
                ViewModel.Localization["OperationFailed"],
                "storage-structure-editor",
                exception,
                "structure.edit.exception");
        }
    }

    // ---- Apply-all -----------------------------------------------------

    private async void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditDraft) return;
        var submittedSession = EditingSession;
        var submittedSystemId = submittedSession.SystemId;
        bool IsCurrentSubmission() => ReferenceEquals(EditingSession, submittedSession)
            && ViewModel.SelectedSystem.SystemId == submittedSystemId;

        if (!ViewModel.IsUsingSimulatedInventory)
        {
            if (!CanEditDraft || !HasUncommittedChanges() || HasInvalidSizeInput()) return;
            var selectedPoolMembers = SelectedPool()?.MemberPhysicalDiskIds.ToArray();
            var selectedMember = _selectedPoolDiskId;
            MergeFormIntoWorking();
            CaptureSelectedIntent();
            _applyingRealDraft = true;
            UpdateButtonState();
            try
            {
                var realApplied = await ApplyRealStructureDraftAsync(submittedSession);
                if (!IsCurrentSubmission()) return;
                if (realApplied)
                {
                    EditingSession.Bind(ViewModel.SelectedSystem, ViewModel.EffectiveActiveSnapshot);
                    _formDirty = false;
                    _formHistoryCaptured = false;
                    RestoreRealSelection(selectedPoolMembers, selectedMember);
                    RefreshAll();
                }
                else
                {
                    // Verified stages may have advanced the session. Keep the
                    // remaining input and surface the new baseline and actions.
                    RestoreRealSelection(selectedPoolMembers, selectedMember);
                    RefreshTopology();
                }
            }
            finally
            {
                _applyingRealDraft = false;
                RefreshExecutionMode();
            }
            return;
        }

        if (!HasUncommittedChanges())
        {
            await ShowMessageAsync(
                ViewModel.Localization["NoApplyChangesTitle"],
                ViewModel.Localization["NoApplyChangesMessage"]);
            return;
        }

        var pending = _working;
        if (_formDirty)
        {
            pending = ApplyFormToWorking(pending);
        }

        // Reject property rebuilds on data-bearing pools before anything is
        // executed, so a mid-sequence rejection cannot leave the draft half
        // applied and then duplicated by a second Apply.
        foreach (var pool in pending.StoragePools.Where(item =>
                     !item.IsPrimordial && !EditWorkspace.IsDraftPool(item.StableId)))
        {
            if (!EditWorkspace.HasPoolPropertyChanges(pending, EditingSession.Baseline, pool.StableId)
                || !EditWorkspace.PoolHoldsStoredData(EditingSession.Baseline, pool.StableId)
                || !RebuildsTierParameters(pending, EditingSession.Baseline, pool.StableId))
            {
                continue;
            }

            await ShowMessageAsync(
                ViewModel.Localization["RebuildBlockedTitle"],
                ViewModel.Localization["RebuildBlockedMessage"]);
            return;
        }

        var planned = _currentPlan ?? SimulationDraftPlanner.Build(EditingSession.Baseline, pending);
        var blockedItem = planned.DisplayItems.FirstOrDefault(item =>
            item.Decision?.Verdict != StorageRuleVerdict.Allow);
        if (blockedItem is not null)
        {
            await ShowMessageAsync(
                Text("修改被规则阻止", "Changes blocked by rules"),
                blockedItem.Decision?.Message ?? blockedItem.Title);
            return;
        }
        var preview = planned.Steps.Select(step => DescribePlanStep(step)).ToList();
        if (preview.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["NoApplyChangesTitle"],
                ViewModel.Localization["NoApplyChangesMessage"]);
            return;
        }

        var joiningWithData = pending.PhysicalDisks
            .Where(disk =>
            {
                var committedDisk = EditingSession.Baseline.PhysicalDisks.FirstOrDefault(item =>
                    item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
                return committedDisk is not null
                    && !string.Equals(committedDisk.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(disk.PoolStableId)
                    && EditWorkspace.DiskHoldsStoredData(EditingSession.Baseline, disk.StableId);
            })
            .ToArray();
        var warnings = new List<string>();
        if (planned.DisplayItems.Any(item => item.CausesDataLoss))
        {
            warnings.Add(Text(
                "这些修改会删除含有已用数据的分区或虚拟磁盘。",
                "These changes delete a partition or virtual disk that holds used data."));
        }
        if (joiningWithData.Length > 0)
        {
            var names = string.Join(", ", joiningWithData.Select(disk => disk.FriendlyName));
            warnings.Add(Text(
                $"加入池会使以下磁盘上的已用数据不可用：{names}。",
                $"Joining the pool makes used data on these disks unavailable: {names}."));
        }
        var specialRoleDisks = pending.PhysicalDisks
            .Where(disk =>
            {
                var committed = EditingSession.Baseline.PhysicalDisks.FirstOrDefault(item =>
                    item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
                return committed is not null
                    && ((committed.IsPageFile && !disk.IsPageFile)
                        || (committed.IsCrashDump && !disk.IsCrashDump));
            })
            .Select(disk => disk.FriendlyName)
            .ToArray();
        if (specialRoleDisks.Length > 0)
        {
            warnings.Add(Text(
                $"将移除以下磁盘上的页面文件或崩溃转储角色：{string.Join(", ", specialRoleDisks)}。",
                $"Page-file or crash-dump roles will be removed from: {string.Join(", ", specialRoleDisks)}."));
        }

        var confirmation = new List<string>();
        if (warnings.Count > 0)
        {
            confirmation.Add(Text("注意：", "Warnings:"));
            confirmation.AddRange(warnings.Select(item => "• " + item));
            confirmation.Add(string.Empty);
        }
        confirmation.Add(Text("将执行：", "Operations:"));
        confirmation.AddRange(preview.Select(step => "• " + step));
        var lines = string.Join("\n", confirmation);
        if (!await ConfirmAsync(ViewModel.Localization["ApplyPreviewTitle"], lines))
        {
            return;
        }

        if (!CanEditDraft || !IsCurrentSubmission()) return;

        var plan = EditingSession.Prepare(planned);
        if (plan.IsEmpty)
        {
            await ShowMessageAsync(
                ViewModel.Localization["NoApplyChangesTitle"],
                ViewModel.Localization["NoApplyChangesMessage"]);
            return;
        }

        using var activity = BeginPageActivity(Text("正在应用结构修改", "Applying structural changes"));
        var applied = await ViewModel.ApplySimulationPlanAsync(plan);
        if (!IsCurrentSubmission())
        {
            if (applied.Status == ApplicationStatus.OutcomeUnknown) submittedSession.OutcomeUnknown = true;
            if (applied.IsSuccess && ViewModel.SystemCatalog.Systems.SingleOrDefault(system => system.SystemId == submittedSystemId) is { } completedSystem)
                submittedSession.Bind(completedSystem, EditWorkspace.NormalizeTierCapacities(completedSystem.Snapshot));
            return;
        }
        if (!applied.IsSuccess)
        {
            await HandleFailedApplyAsync(applied, pending);
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        _maximumSizeFields.Clear();
        _poolIntents.Clear();
        _outcomeUnknown = false;
        EditingSession.Bind(ViewModel.SelectedSystem, ViewModel.EffectiveActiveSnapshot);
        _selectedPoolId = EditWorkspace.IsDraftPool(_selectedPoolId ?? string.Empty)
            ? _working.StoragePools.LastOrDefault(item => !item.IsPrimordial)?.StableId
            : _selectedPoolId;
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        _formDirty = false;
        _formHistoryCaptured = false;
        RefreshAll();
        PublishOperationFeedback(
            GlobalNotificationSeverity.Info,
            Text("修改已应用", "Changes applied"),
            Text("结构修改已写入模拟系统。", "The structural changes were saved to the simulation."),
            "storage-structure-editor",
            "structure.apply.completed");
    }

    private Task HandleFailedApplyAsync(
        ApplicationResult<SimulationEditReceipt> applied,
        StorageSnapshot pending)
    {
        if (applied.Status == ApplicationStatus.OutcomeUnknown)
        {
            PublishOperationResult(
                applied.Status,
                applied.Messages,
                applied.CorrelationId,
                Text("提交结果未知", "Commit outcome unknown"),
                "storage-structure-editor");
            _outcomeUnknown = true;
            _working = pending;
            _formDirty = false;
            _formHistoryCaptured = false;
            RefreshAll();
            return Task.CompletedTask;
        }

        PublishOperationResult(
            applied.Status,
            applied.Messages,
            applied.CorrelationId,
            Text("操作未完成", "Operation did not complete"),
            "storage-structure-editor");
        if (!ReferenceEquals(pending, _working))
        {
            _working = pending;
            _formDirty = false;
            _formHistoryCaptured = false;
        }

        RefreshAll();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Projects property-form fields into the local working snapshot. The
    /// caller controls the undo transaction; partition create/format values
    /// are captured separately in the same draft intent.
    /// </summary>
    private StorageSnapshot ApplyFormToWorking(StorageSnapshot snapshot)
    {
        var result = snapshot;
        var pool = result.StoragePools.FirstOrDefault(item => item.StableId == _selectedPoolId);
        if (pool is null || pool.IsPrimordial)
        {
            return result;
        }

        var isDraft = EditWorkspace.IsDraftPool(pool.StableId);
        var poolName = _poolNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(poolName))
        {
            poolName = pool.FriendlyName;
        }

        if (isDraft && !pool.FriendlyName.Equals(poolName, StringComparison.OrdinalIgnoreCase))
        {
            result = result with
            {
                StoragePools = result.StoragePools
                    .Select(item => item.StableId == pool.StableId ? item with { FriendlyName = poolName } : item)
                    .ToArray()
            };
        }

        var vdiskName = _virtualDiskNameBox.Text.Trim();
        var vdisk = result.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
        if (vdisk is not null
            && (isDraft || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId))
            && !string.IsNullOrWhiteSpace(vdiskName)
            && !vdisk.FriendlyName.Equals(vdiskName, StringComparison.OrdinalIgnoreCase))
        {
            result = result with
            {
                VirtualDisks = result.VirtualDisks
                    .Select(item => item.StableId == vdisk.StableId
                        ? item with { FriendlyName = vdiskName }
                        : item)
                    .ToArray()
            };
        }

        var holdsData = EditWorkspace.PoolHoldsStoredData(result, pool.StableId);
        var selectedTiers = EditWorkspace.SelectPoolTiersForForm(result, pool.StableId,
            _selectedPoolTierId, _selectedPoolVdiskId, ViewModel.IsUsingSimulatedInventory);
        var parameterTiers = EditWorkspace.SelectPoolTiersForParameterUpdate(result, pool.StableId);
        foreach (var group in TierGroups())
        {
            if (!ViewModel.IsUsingSimulatedInventory && !isDraft) continue;
            var tier = selectedTiers.GetValueOrDefault(group.Media);
            if (tier is null || tier.MemberPhysicalDiskIds.Count == 0
                || parameterTiers.GetValueOrDefault(group.Media)?.StableId != tier.StableId)
            {
                continue;
            }

            var maximumSelected = _maximumSizeFields.Contains(MaximumKey(group));
            var resiliency = holdsData
                ? tier.ResiliencySettingName
                : group.ResiliencyBox.SelectedItem as string ?? tier.ResiliencySettingName;
            long? interleave = holdsData
                ? tier.Interleave
                : ParseSize(group.InterleaveBox.SelectedItem as string ?? "64 KiB");
            int? copies = holdsData
                ? tier.NumberOfDataCopies
                : NumValue(group.CopiesBox) is { } copyValue
                    ? Math.Max(1, (int)copyValue)
                    : tier.NumberOfDataCopies ?? 1;
            int? failures = holdsData
                ? tier.PhysicalDiskRedundancy
                : NumValue(group.FailuresBox) is { } failureValue
                    ? Math.Max(0, (int)failureValue)
                    : tier.PhysicalDiskRedundancy ?? 1;
            var columns = holdsData
                ? tier.NumberOfColumns
                : NumValue(group.ColumnsBox) is { } columnValue
                    ? (int)columnValue
                    : tier.NumberOfColumns;
            var size = holdsData && !maximumSelected
                ? tier.Size
                : FormTierSizeBytes(group, tier);
            var changed = !string.Equals(resiliency, tier.ResiliencySettingName, StringComparison.OrdinalIgnoreCase)
                || interleave != tier.Interleave
                || copies != tier.NumberOfDataCopies
                || failures != tier.PhysicalDiskRedundancy
                || columns != tier.NumberOfColumns
                || (size is not null && size != tier.Size);
            if (changed)
            {
                var parity = resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1, failures ?? 1)
                    : 0;
                var footprint = size is null
                    ? tier.FootprintOnPool
                    : ConservativeCapacity.PhysicalFootprintForLogical(
                        size.Value, resiliency, copies ?? 1, columns, parity);
                result = result with
                {
                    StorageTiers = result.StorageTiers
                        .Select(item => item.StableId == tier.StableId
                            ? item with
                            {
                                ResiliencySettingName = resiliency,
                                Interleave = interleave,
                                NumberOfDataCopies = copies,
                                PhysicalDiskRedundancy = failures,
                                NumberOfColumns = columns,
                                Size = size ?? tier.Size,
                                FootprintOnPool = footprint,
                                SizeSource = CapacitySourceKind.SimulatedEstimate
                            }
                            : item)
                        .ToArray()
                };
            }
        }

        var draftVdisk = result.VirtualDisks.FirstOrDefault(item =>
            item.PoolStableId == pool.StableId && EditWorkspace.IsDraftVirtualDisk(item.StableId));
        if (draftVdisk is not null)
        {
            var primaryTier = result.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                && tier.MemberPhysicalDiskIds.Count > 0).OrderBy(tier => EditWorkspace.NormalizeMedia(tier.MediaType) switch
                { "SCM" => 0, "SSD" => 1, _ => 2 }).FirstOrDefault();
            var finalSize = ViewModel.IsUsingSimulatedInventory || isDraft
                ? result.StorageTiers.Where(item => item.PoolStableId == pool.StableId).Sum(item => item.Size)
                : TryReadCreationSize(out var requested, out var maximum) && !maximum ? requested ?? 0 : 0;
            var finalFootprint = ViewModel.IsUsingSimulatedInventory || isDraft
                ? result.StorageTiers.Where(item => item.PoolStableId == pool.StableId).Sum(item => item.FootprintOnPool) : 0;
            result = result with
            {
                VirtualDisks = result.VirtualDisks.Select(item => item.StableId == draftVdisk.StableId
                    ? item with { Size = finalSize, FootprintOnPool = finalFootprint,
                        ResiliencySettingName = primaryTier?.ResiliencySettingName ?? item.ResiliencySettingName,
                        Interleave = primaryTier?.Interleave ?? item.Interleave,
                        NumberOfColumns = primaryTier?.NumberOfColumns ?? item.NumberOfColumns,
                        NumberOfDataCopies = primaryTier?.NumberOfDataCopies ?? item.NumberOfDataCopies,
                        PhysicalDiskRedundancy = primaryTier?.PhysicalDiskRedundancy ?? item.PhysicalDiskRedundancy }
                    : item).ToArray(),
                OsDisks = result.OsDisks.Select(item => item.VirtualDiskStableId == draftVdisk.StableId
                    ? item with { Size = finalSize }
                    : item).ToArray()
            };
        }

        return result;
    }

    private string DescribePlanStep(SimulationEditRequest step)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        return step.Kind switch
        {
            SimulationEditKind.CreateTieredPool => zh
                ? $"创建存储池“{step.Name}”。"
                : $"Create storage pool \"{step.Name}\".",
            SimulationEditKind.CreateVirtualDisk => zh
                ? $"创建虚拟磁盘“{step.Name}”。"
                : $"Create virtual disk \"{step.Name}\".",
            SimulationEditKind.DeleteVirtualDisk => zh
                ? "删除虚拟磁盘。"
                : "Delete virtual disk.",
            SimulationEditKind.DissolveStoragePool => zh
                ? "解散存储池。"
                : "Dissolve storage pool.",
            SimulationEditKind.DeleteEmptyStoragePool => zh
                ? "移除空存储池。"
                : "Remove empty storage pool.",
            SimulationEditKind.CreatePartition => zh
                ? "创建分区。"
                : "Create partition.",
            SimulationEditKind.DeletePartition => zh
                ? "删除分区。"
                : "Delete partition.",
            SimulationEditKind.FormatPartition => zh
                ? $"格式化为 {step.FileSystem ?? "NTFS"}。"
                : $"Format as {step.FileSystem ?? "NTFS"}.",
            SimulationEditKind.ChangeDriveLetter => zh
                ? $"更改盘符为 {step.DriveLetter}。"
                : $"Change drive letter to {step.DriveLetter}.",
            SimulationEditKind.SetDiskUsage => zh
                ? $"设置磁盘用途为 {step.DiskUsage}。"
                : $"Set disk usage to {step.DiskUsage}.",
            SimulationEditKind.MovePhysicalDisk => zh
                ? "移动物理磁盘。"
                : "Move physical disk.",
            SimulationEditKind.UpdateStoragePool => zh
                ? $"更新存储池“{step.Name}”。"
                : $"Update storage pool \"{step.Name}\".",
            _ => step.Kind.ToString()
        };
    }

    private bool RebuildsTierParameters(
        StorageSnapshot working,
        StorageSnapshot committed,
        string poolId) =>
        TierGroups().Any(group =>
        {
            var workingTier = working.StorageTiers.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
                && EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
            var committedTier = committed.StorageTiers.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
                && EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
            if (workingTier is null || committedTier is null)
            {
                return false;
            }

            return !string.Equals(
                    workingTier.ResiliencySettingName,
                    committedTier.ResiliencySettingName,
                    StringComparison.OrdinalIgnoreCase)
                || workingTier.Interleave != committedTier.Interleave
                || workingTier.NumberOfDataCopies != committedTier.NumberOfDataCopies
                || workingTier.PhysicalDiskRedundancy != committedTier.PhysicalDiskRedundancy
                || workingTier.NumberOfColumns != committedTier.NumberOfColumns;
        });

    private static bool TierSpecsEqual(StorageTierInfo left, StorageTierInfo right) =>
        string.Equals(left.ResiliencySettingName, right.ResiliencySettingName, StringComparison.OrdinalIgnoreCase)
        && left.Interleave == right.Interleave
        && left.NumberOfDataCopies == right.NumberOfDataCopies
        && left.PhysicalDiskRedundancy == right.PhysicalDiskRedundancy
        && left.NumberOfColumns == right.NumberOfColumns;

    private static bool SameToken(ComboBox box, string value) =>
        string.Equals(box.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);

    private static string InterleaveToken(long? bytes) =>
        $"{Math.Max(1, (bytes ?? 65536) / 1024)} KiB";

    private static string ClusterToken(long? bytes) => bytes is > 0 and < 1024
        ? $"{bytes} B" : $"{(bytes ?? 65536) / 1024} KiB";

    private long SelectedClusterSize() => _clusterBox.SelectedItem is string token
        && token.EndsWith(" B", StringComparison.Ordinal)
        && long.TryParse(token[..^2], out var bytes) ? bytes
            : ParseSize(_clusterBox.SelectedItem as string ?? "64 KiB");

    private void SelectClusterSize(long bytes)
    {
        var token = ClusterToken(bytes);
        // Preserve an existing source value even when it is outside the
        // current creation choices; provider validation still governs writes.
        if (!_clusterBox.Items.Contains(token)) _clusterBox.Items.Add(token);
        _clusterBox.SelectedItem = token;
    }

    private void RefreshClusterChoices()
    {
        var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        int? build = int.TryParse(_working.Computer.OsBuild, out var parsedBuild) ? parsedBuild : null;
        var choices = FormatAllocationUnitPolicy.GetSupportedClusterSizes(fileSystem, build)
            .Select(bytes => ClusterToken(bytes)).ToArray();
        var selected = _clusterBox.SelectedItem as string;
        var wasFilling = _filling;
        _filling = true;
        try
        {
            if (!_clusterBox.Items.Cast<string>().SequenceEqual(choices))
            {
                _clusterBox.Items.Clear();
                foreach (var token in choices) _clusterBox.Items.Add(token);
            }
            _clusterBox.SelectedItem = selected is not null && choices.Contains(selected)
                ? selected : ClusterToken(FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes(fileSystem));
        }
        finally { _filling = wasFilling; }
    }
    // ---- Field dirty dots, recommended resets, and change checks ------

    private enum TierFieldKind
    {
        Size,
        Provisioning,
        Resiliency,
        Copies,
        Failures,
        DiskCount,
        Columns,
        Stripe
    }

    private void ResetLayerSwitchesForSelection()
    {
        // Simulated-layer switches are session preview tools only; they are
        // never reset by selection, and layers with disks show themselves.
    }

    /// <summary>
    /// NumberBox stores its empty state as NaN, not null. The visible text
    /// is the user's truth when the Value has not been committed yet, so a
    /// typed number is never read back as empty.
    /// </summary>
    private static double? NumValue(NumberBox box)
    {
        if (double.IsNaN(box.Value)
            && double.TryParse(
                box.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out var typed)
            && double.IsFinite(typed))
        {
            return typed;
        }

        return double.IsNaN(box.Value) ? null : box.Value;
    }

    private double? NumValue(TextBox box)
    {
        var group = TierGroups().FirstOrDefault(item => ReferenceEquals(item.SizeBox, box));
        if (group is not null && _maximumSizeFields.Contains(MaximumKey(group)))
        {
            return CapacityInitialCandidateGiB(group);
        }

        return IntegerInputPolicy.ContainsOnlyDigits(box.Text)
            && long.TryParse(box.Text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static void SetNum(NumberBox box, double? value) => box.Value = value ?? double.NaN;

    private static void SetNum(TextBox box, double? value) =>
        box.Text = value?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    private void SetCapacityText(TierFields group, string text)
    {
        if (group.SizeBox.Text.Equals(text, StringComparison.Ordinal))
        {
            _programmaticCapacityText.Remove(group.SizeBox);
            return;
        }

        _programmaticCapacityText[group.SizeBox] = text;
        IntegerTextInput.SetDisplayText(group.SizeBox, text);
    }

    private string MaximumKey(TierFields group) => $"{_selectedPoolId}|{group.Media}";

    private void SetMaximumSize(TierFields group)
    {
        if (!group.MaximumButton.IsEnabled) return;
        var key = MaximumKey(group);
        if (_maximumSizeFields.Contains(key))
        {
            UpdateMaximumSizeText(group);
            return;
        }

        CaptureSelectedIntent();
        _undoStack.Push(CaptureDraftState());
        _redoStack.Clear();
        _maximumSizeFields.Add(key);
        _formHistoryCaptured = true;
        UpdateMaximumSizeText(group);
        CaptureSelectedIntent();
        _formDirty = true;
        UpdateButtonState();
    }

    private void UpdateMaximumSizeText(TierFields group)
    {
        if (!_maximumSizeFields.Contains(MaximumKey(group)))
        {
            return;
        }

        if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(_selectedPoolId ?? string.Empty)
            && (SelectedPool() is not { } pool
                || _working.VirtualDisks.Any(item => item.PoolStableId == pool.StableId
                    && !EditWorkspace.IsDraftVirtualDisk(item.StableId)))) return;
        var wasFilling = _filling;
        _filling = true;
        try
        {
            var maximum = CapacityInitialCandidateGiB(group);
            SetCapacityText(group, maximum > 0
                ? maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty);
            group.MaximumDisplay.Text = $"MAX({maximum})";
        }
        finally { _filling = wasFilling; }
    }

    private void SwitchCapacityToExplicit(TierFields group)
    {
        var key = MaximumKey(group);
        if (!_maximumSizeFields.Contains(key))
            return;

        // Preserve the previous maximum-search intent as one Undo step.
        CaptureSelectedIntent();
        _undoStack.Push(CaptureDraftState());
        _redoStack.Clear();
        _maximumSizeFields.Remove(key);
        _formHistoryCaptured = true;
        CaptureSelectedIntent();
    }

    private void BeginExplicitCapacityEdit(TierFields group)
    {
        SwitchCapacityToExplicit(group);
        group.MaximumDisplay.Visibility = Visibility.Collapsed;
        _formDirty = true;
        UpdateButtonState();
    }

    private static bool NumberEquals(double? actual, int? expected) =>
        expected is not null
        && actual is not null
        && Math.Abs(actual.Value - expected.Value) < 0.001;

    private TierFields? GroupFor(string media) =>
        TierGroups().FirstOrDefault(group => group.Media == media);

    private void UpdateFieldResets()
    {
        foreach (var group in TierGroups())
        {
            group.MaximumDisplay.Visibility = _maximumSizeFields.Contains(MaximumKey(group))
                ? Visibility.Visible : Visibility.Collapsed;
            group.SizeBox.Opacity = group.MaximumDisplay.Visibility == Visibility.Visible ? 0 : 1;
            group.MaximumDisplay.IsEnabled = group.SizeBox.IsEnabled;
            ContextHelp.Set(group.MaximumDisplay, Text(
                "MAX 括号内为当前上界的初始整数 GiB 候选；点击输入框改为指定容量。",
                "MAX shows the initial whole-GiB candidate from the current upper bound; click the field to enter an explicit capacity."));
        }
        foreach (var field in _fieldResets)
        {
            var changed = false;
            try
            {
                changed = field.IsChanged();
            }
            catch
            {
                changed = false;
            }

            field.ResetButton.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
            field.ChangeIndicator.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private long TierCapacityMaxBytes(string media)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return 0;
        }

        var tier = TierMap(pool.StableId).GetValueOrDefault(media);
        var group = GroupFor(media);
        if (tier is null || group is null)
        {
            return 0;
        }

        var creatingRealVirtualDisk = !ViewModel.IsUsingSimulatedInventory
            && !EditWorkspace.IsDraftPool(pool.StableId)
            && !_working.VirtualDisks.Any(item => item.PoolStableId == pool.StableId
                && !EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var memberIds = creatingRealVirtualDisk
            ? pool.MemberPhysicalDiskIds
            : tier.MemberPhysicalDiskIds;
        var members = _working.PhysicalDisks
            .Where(disk => memberIds.Contains(
                    disk.StableId, StringComparer.OrdinalIgnoreCase)
                && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage))
            .Where(disk => EditWorkspace.NormalizeMedia(disk.MediaType) == media)
            .ToArray();
        var resiliency = group.ResiliencyBox.SelectedItem as string ?? tier.ResiliencySettingName;
        var copies = NumValue(group.CopiesBox) is { } copyValue
            ? Math.Max(1, (int)copyValue)
            : tier.NumberOfDataCopies ?? 1;
        var columns = NumValue(group.ColumnsBox) is { } columnValue
            ? (int)columnValue
            : tier.NumberOfColumns;
        var parity = resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase)
            ? NumValue(group.FailuresBox) is { } failureValue ? Math.Max(1, (int)failureValue) : 1
            : 0;
        try
        {
            return ConservativeCapacity.PlanLogicalUpperBound(
                members.Select(item => item.Size).ToArray(),
                resiliency,
                copies,
                columns,
                parity,
                ParseSize(group.InterleaveBox.SelectedItem as string ?? "64 KiB")).AlignedLogicalBytes;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private long CapacityMaximumIntegerGiB(TierFields group) =>
        Math.Max(0, TierCapacityMaxBytes(group.Media) / (1024L * 1024L * 1024L));

    private long CapacityInitialCandidateGiB(TierFields group) =>
        MaximumCapacityAlgorithm.InitialCandidateBytes(TierCapacityMaxBytes(group.Media))
            / MaximumCapacityAlgorithm.GiB;

    private int TierDataDiskCount(string media)
    {
        var pool = SelectedPool();
        return pool is null || pool.IsPrimordial
            ? 0
            : TierDataDisks(media).Count;
    }

    private IReadOnlyList<PhysicalDiskInfo> TierDataDisks(string media)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return [];
        }

        var tier = TierMap(pool.StableId).GetValueOrDefault(media);
        var memberIds = tier is { VirtualDiskStableId: null } && !EditWorkspace.IsDraftPool(pool.StableId)
            ? pool.MemberPhysicalDiskIds : tier?.MemberPhysicalDiskIds ?? [];
        return tier is null
            ? []
            : _working.PhysicalDisks
                .Where(disk => memberIds.Contains(
                    disk.StableId, StringComparer.OrdinalIgnoreCase)
                    && EditWorkspace.NormalizeMedia(disk.MediaType) == media
                    && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage))
                .ToArray();
    }

    private bool TierFieldChanged(TierFieldKind field, string media)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var tier = TierMap(pool.StableId).GetValueOrDefault(media);
        var group = GroupFor(media);
        if (tier is null || group is null)
        {
            return false;
        }

        if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(pool.StableId)
            && field == TierFieldKind.Size)
            return group.SizeBox.IsEnabled && !_maximumSizeFields.Contains(MaximumKey(group));

        var baselineTier = EditingSession.Baseline.StorageTiers.FirstOrDefault(item =>
            item.StableId.Equals(tier.StableId, StringComparison.OrdinalIgnoreCase));
        if (baselineTier is null)
        {
            var setting = group.ResiliencyBox.SelectedItem as string ?? "Simple";
            var count = TierDataDiskCount(media);
            return field switch
            {
                TierFieldKind.Size => group.SizeBox.IsEnabled && !_maximumSizeFields.Contains(MaximumKey(group)),
                TierFieldKind.Resiliency => group.ResiliencyBox.IsEnabled
                    && !SameToken(group.ResiliencyBox, EditWorkspace.RecommendedResiliency(media, count)),
                TierFieldKind.Copies => group.CopiesBox.IsEnabled
                    && !NumberEquals(NumValue(group.CopiesBox), EditWorkspace.RecommendedDataCopies(setting, count)),
                TierFieldKind.Failures => group.FailuresBox.IsEnabled
                    && !NumberEquals(NumValue(group.FailuresBox), EditWorkspace.RecommendedToleratedFailures(
                        setting, (int)(NumValue(group.CopiesBox) ?? 1))),
                TierFieldKind.Columns => group.ColumnsBox.IsEnabled
                    && !NumberEquals(NumValue(group.ColumnsBox), new StructureColumnSuggestion().Resolve(setting, TierDataDisks(media), (int)(NumValue(group.CopiesBox) ?? 1))),
                TierFieldKind.Stripe => group.InterleaveBox.IsEnabled && !SameToken(group.InterleaveBox, "64 KiB"),
                _ => false
            };
        }
        tier = baselineTier;

        return field switch
        {
            TierFieldKind.Size => group.SizeBox.IsEnabled
                && (_maximumSizeFields.Contains(MaximumKey(group))
                    ? true
                    : NumValue(group.SizeBox) is { } size
                        ? Math.Abs(size - Math.Round(tier.Size / 1024d / 1024d / 1024d, 2)) > 0.000001
                        : !CapacityTextMatchesCurrentTier(group)),
            TierFieldKind.Resiliency => group.ResiliencyBox.IsEnabled
                && !SameToken(group.ResiliencyBox, tier.ResiliencySettingName),
            TierFieldKind.Copies => group.CopiesBox.IsEnabled
                && !NumberEquals(NumValue(group.CopiesBox), tier.NumberOfDataCopies),
            TierFieldKind.Failures => group.FailuresBox.IsEnabled
                && !NumberEquals(NumValue(group.FailuresBox), tier.PhysicalDiskRedundancy),
            TierFieldKind.Columns => group.ColumnsBox.IsEnabled
                && !NumberEquals(NumValue(group.ColumnsBox), tier.NumberOfColumns),
            TierFieldKind.Stripe => group.InterleaveBox.IsEnabled
                && InterleaveToken(tier.Interleave) != (group.InterleaveBox.SelectedItem as string),
            _ => false
        };
    }

    /// <summary>Existing targets restore their actual baseline; new targets restore defaults.</summary>
    private void ResetTierField(TierFields group, string kind)
    {
        var selected = SelectedTier(group.Media);
        var baseline = selected is null ? null : EditingSession.Baseline.StorageTiers.FirstOrDefault(tier =>
            tier.StableId.Equals(selected.StableId, StringComparison.OrdinalIgnoreCase));
        var creatingRealVirtualDisk = !ViewModel.IsUsingSimulatedInventory
            && SelectedPool() is { } pool
            && !EditWorkspace.IsDraftPool(pool.StableId)
            && !_working.VirtualDisks.Any(vdisk => vdisk.PoolStableId == pool.StableId
                && !EditWorkspace.IsDraftVirtualDisk(vdisk.StableId));
        if (baseline is not null && !(kind == "Size" && creatingRealVirtualDisk))
        {
            var wasFilling = _filling;
            _filling = true;
            try
            {
                switch (kind)
                {
                    case "Size":
                        _maximumSizeFields.Remove(MaximumKey(group));
                        SetCapacityText(group, TierCapacityDisplay(baseline.Size));
                        break;
                    case "Resiliency": SetResiliency(group.ResiliencyBox, baseline.ResiliencySettingName); break;
                    case "Copies": SetNum(group.CopiesBox, baseline.NumberOfDataCopies); break;
                    case "Failures": SetNum(group.FailuresBox, baseline.PhysicalDiskRedundancy); break;
                    case "Columns": SetNum(group.ColumnsBox, baseline.NumberOfColumns); break;
                    case "Stripe": group.InterleaveBox.SelectedItem = InterleaveToken(baseline.Interleave); break;
                }
            }
            finally { _filling = wasFilling; }
            return;
        }
        if (!ViewModel.IsUsingSimulatedInventory && !EditWorkspace.IsDraftPool(_selectedPoolId ?? string.Empty)
            && kind == "Size")
        {
            if (group.SizeBox.IsEnabled)
            {
                _maximumSizeFields.Add(MaximumKey(group));
                UpdateMaximumSizeText(group);
                CaptureSelectedIntent();
            }
            return;
        }
        var choice = ColumnChoice(group);
        _columnSuggestions[ColumnSuggestionKey(group)] = kind switch
        {
            "Resiliency" => choice with { ExplicitResiliency = null },
            "Copies" => choice with { ExplicitCopies = null },
            "Failures" => choice with { ExplicitFailures = null },
            _ => choice
        };
        var media = group.Media;
        var count = TierDataDiskCount(media);
        switch (kind)
        {
            case "Size":
                _maximumSizeFields.Add(MaximumKey(group));
                UpdateMaximumSizeText(group);
                break;
            case "Resiliency":
                SetResiliency(group.ResiliencyBox, EditWorkspace.RecommendedResiliency(media, count));
                break;
            case "Copies":
                var resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
                SetNum(group.CopiesBox, EditWorkspace.RecommendedDataCopies(resiliency, count));
                break;
            case "Failures":
                resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
                var copies = NumValue(group.CopiesBox) is { } copyValue ? (int)copyValue : 1;
                SetNum(group.FailuresBox, EditWorkspace.RecommendedToleratedFailures(resiliency, copies));
                break;
            case "Columns":
                var columnResiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
                _columnSuggestions[ColumnSuggestionKey(group)] = ColumnChoice(group) with { ExplicitColumns = null };
                SetNum(group.ColumnsBox, new StructureColumnSuggestion().Resolve(columnResiliency, TierDataDisks(media),
                    (int)(NumValue(group.CopiesBox) ?? 1)));

                break;
            case "Stripe":
                group.InterleaveBox.SelectedItem = "64 KiB";
                break;
        }
    }

    private long? SizeBytes(TextBox box) =>
        NumValue(box) is { } gb ? (long)(gb * 1024L * 1024L * 1024L) : null;

    private string TierCapacityDisplay(long bytes) => ViewModel.IsUsingSimulatedInventory
        ? Math.Round(bytes / 1024d / 1024d / 1024d, 2)
            .ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
        : CreationGigabytes(bytes);

    private long? FormTierSizeBytes(TierFields group, StorageTierInfo tier)
    {
        if (!_maximumSizeFields.Contains(MaximumKey(group)))
        {
            var baseline = EditingSession.Baseline.StorageTiers.FirstOrDefault(item => item.StableId == tier.StableId);
            if (baseline is not null && group.SizeBox.Text == TierCapacityDisplay(baseline.Size))
                return baseline.Size;
            if (CapacityTextMatchesCurrentTier(group)) return tier.Size;
        }
        return SizeBytes(group.SizeBox);
    }

    private static int? CopiesCount(NumberBox box) =>
        NumValue(box) is { } value ? Math.Max(1, (int)value) : null;

    private static int? FailuresCount(NumberBox box) =>
        NumValue(box) is { } value ? Math.Max(0, (int)value) : null;

    private static int? ColumnsCount(NumberBox box) =>
        NumValue(box) is { } value ? (int)value : null;

    private VirtualDiskInfo? RealVdiskOf(StorageSnapshot snapshot, string poolId) =>
        snapshot.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && !EditWorkspace.IsDraftVirtualDisk(item.StableId));

    private bool PartitionStyleChanged()
    {
        if (!_partitionStyleBox.IsEnabled) return false;
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var vdisk = RealVdiskOf(EditingSession.Baseline, pool.StableId);
        if (vdisk is null)
        {
            return !SameToken(_partitionStyleBox, "GPT");
        }

        var osDisk = EditingSession.Baseline.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        if (osDisk is null)
        {
            return false;
        }

        var style = osDisk.PartitionStyle.Trim().ToUpperInvariant();
        return style is "MBR" or "GPT"
            && !SameToken(_partitionStyleBox, style);
    }

    private bool FileSystemChanged()
    {
        if (!_fileSystemBox.IsEnabled) return false;
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var partition = CommittedPrimaryPartition(pool.StableId);
        var baseline = partition is not null && !string.IsNullOrWhiteSpace(partition.FileSystem)
            ? partition.FileSystem
            : "NTFS";
        return !string.Equals(
            _fileSystemBox.SelectedItem as string,
            baseline,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool AllocationUnitChanged()
    {
        if (!_clusterBox.IsEnabled) return false;
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var partition = CommittedPrimaryPartition(pool.StableId);
        var baseline = partition?.AllocationUnitSize is { } bytes
            ? ClusterToken(bytes)
            : "64 KiB";
        return !string.Equals(
            _clusterBox.SelectedItem as string,
            baseline,
            StringComparison.OrdinalIgnoreCase);
    }

    private void ResetPartitionField(string kind)
    {
        var pool = SelectedPool();
        var partition = pool is null ? null : CommittedPrimaryPartition(pool.StableId);
        var baselineVdisk = pool is null ? null : RealVdiskOf(EditingSession.Baseline, pool.StableId);
        var baselineDisk = baselineVdisk is null ? null : EditingSession.Baseline.OsDisks.FirstOrDefault(disk =>
            disk.VirtualDiskStableId == baselineVdisk.StableId);
        switch (kind)
        {
            case "PartitionStyle":
                if (_partitionStyleBox.IsEnabled)
                {
                    var style = baselineDisk?.PartitionStyle.Trim().ToUpperInvariant();
                    _partitionStyleBox.SelectedItem = style is "MBR" or "GPT" ? style : "GPT";
                }

                break;
            case "FileSystem":
                if (_fileSystemBox.IsEnabled)
                {
                    _fileSystemBox.SelectedItem = string.IsNullOrWhiteSpace(partition?.FileSystem)
                        ? "NTFS" : partition.FileSystem;
                }

                break;
            case "AllocationUnit":
                if (_clusterBox.IsEnabled)
                {
                    SelectClusterSize(partition?.AllocationUnitSize
                        ?? FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes(
                            _fileSystemBox.SelectedItem as string ?? "NTFS"));
                }

                break;
        }
    }

}
