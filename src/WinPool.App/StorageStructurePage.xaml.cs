using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationKind = WinPool.Application.SimulationEditKind;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;

namespace WinPool_App;

/// <summary>
/// Storage structure editor (V0.47 control spec): left pool topology with a
/// structural draft; upper-right structure operations (undo/redo/discard-all/
/// apply-all, pool, disk-layer, virtual-disk); lower-right grouped pool,
/// real-tier, and disk-and-partition properties with one Save row.
/// Simulation only.
/// </summary>
public sealed partial class StorageStructurePage : EditorPageBase
{
    private string? _selectedPoolId;
    private string? _selectedPoolDiskId;
    private string? _selectedPoolVdiskId;
    private TopologyEditInteraction _interaction = null!;
    private bool _formBuilt;
    private double _viewportWidth = WorkspaceViewModel.DefaultSurfaceViewportWidth;
    private bool _filling;
    private bool _formDirty;

    // Structural draft history. Every topology drag or structure-button
    // change commits one step; Save checkpoints the history away.
    private readonly Stack<StorageSnapshot> _undoStack = [];
    private readonly Stack<StorageSnapshot> _redoStack = [];

    private sealed record TierFields(
        string Media,
        string TitleKey,
        ComboBox ResiliencyBox,
        ComboBox InterleaveBox,
        NumberBox SizeBox,
        NumberBox CopiesBox,
        NumberBox FailuresBox,
        NumberBox ColumnsBox,
        TextBox DiskCountBox,
        TextBlock ProvisioningText,
        List<FrameworkElement> Rows);

    /// <summary>Reset button shown only while its field differs from the
    /// committed state (it replaces the former dot marker).</summary>
    private sealed record FieldReset(Button ResetButton, Func<bool> IsChanged);

    private readonly List<FieldReset> _fieldResets = [];
    private readonly TextBox _poolNameBox = new();
    private readonly TextBox _virtualDiskNameBox = new();
    private readonly TextBox _volumeNameBox = new();
    private readonly ToggleSwitch _autoVdiskSwitch = new() { IsOn = true };
    private readonly ToggleSwitch _autoPartitionSwitch = new() { IsOn = true };
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

    private static NumberBox CreateNumberField(double minimum = double.NaN, double maximum = double.NaN) =>
        new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            SmallChange = 1,
            Minimum = minimum,
            Maximum = maximum,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
        };

    private static TierFields CreateTierFields(string media, string titleKey) =>
        new(
            media,
            titleKey,
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            CreateNumberField(minimum: 0),
            CreateNumberField(minimum: 1, maximum: 16),
            CreateNumberField(minimum: 0, maximum: 16),
            CreateNumberField(minimum: 1, maximum: 64),
            new TextBox { IsReadOnly = true },
            new TextBlock
            {
                Text = "Fixed",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            },
            []);

    public StorageStructurePage()
    {
        InitializeComponent();
        Performance = CreateTierFields("SSD", "PerformanceTier");
        Capacity = CreateTierFields("HDD", "CapacityTier");
        Dedicated = CreateTierFields("SCM", "DedicatedTier");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is EditorNavigationParameter parameter)
        {
            ViewModel = parameter.ViewModel;
            _selectedPoolId = ResolvePoolId(parameter.TargetStableId);
        }
        else
        {
            ViewModel = (WorkspaceViewModel)e.Parameter;
        }

        _working = ViewModel.ActiveSnapshot;
        _undoStack.Clear();
        _redoStack.Clear();
        _formDirty = false;
        _interaction = new TopologyEditInteraction(
            IsTopologyNodeSelected,
            OnTopologySelected,
            ViewModel.IsUsingSimulatedInventory,
            OnDiskDropped);
        LocalizeChrome();
        EnsureForm();
        RefreshAll();
    }

    private bool HasRoleDisks(string poolId, string usage) =>
        _working.PhysicalDisks.Any(disk =>
            string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && (usage == "Retired" ? disk.IsRetired : disk.IsHotSpare));

    private void LocalizeChrome()
    {
        UndoButtonLabel.Text = ViewModel.Localization["Undo"];
        RedoButtonLabel.Text = ViewModel.Localization["Redo"];
        DiscardAllButtonLabel.Text = ViewModel.Localization["DiscardAll"];
        ApplyAllButtonLabel.Text = ViewModel.Localization["ApplyAll"];
        CreatePoolButtonLabel.Text = ViewModel.Localization["CreatePool"];
        DissolveButtonLabel.Text = ViewModel.Localization["DissolvePool"];
        RetireButtonLabel.Text = ViewModel.Localization["RetireDisk"];
        HotSpareButtonLabel.Text = ViewModel.Localization["HotSpareDisk"];
        CreateVdiskButtonLabel.Text = ViewModel.Localization["CreateVirtualDisk"];
        DeleteVdiskButtonLabel.Text = ViewModel.Localization["DeleteVirtualDisk"];
        SavePoolPropertiesButtonLabel.Text = ViewModel.Localization["SavePoolProperties"];
        ShowHotSpareLabel.Text = ViewModel.Localization["ShowHotSpareLayer"];
        ShowRetiredLabel.Text = ViewModel.Localization["ShowRetiredLayer"];
        _multiVdiskWarning.Text = ViewModel.Localization["MultipleVirtualDiskWarning"];
        _volumeNameBox.PlaceholderText = ViewModel.Localization["VolumeName"];
    }

    private void EnsureForm()
    {
        if (_formBuilt)
        {
            return;
        }

        _formBuilt = true;
        PoolFormGrid.ColumnDefinitions.Clear();
        PoolFormGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        PoolFormGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        PoolFormGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FillCombo(_partitionStyleBox, ["GPT", "MBR"], 0);
        FillCombo(_fileSystemBox, ["NTFS", "ReFS"], 0);
        FillCombo(_clusterBox, ["4K", "8K", "16K", "32K", "64K"], 4);
        foreach (var group in TierGroups())
        {
            FillCombo(group.ResiliencyBox, ["Simple", "Mirror", "Parity"], 1);
            FillCombo(group.InterleaveBox, ["16K", "32K", "64K", "128K", "256K"], 2);
        }

        foreach (var group in TierGroups())
        {
            HookFormField(group.ResiliencyBox, isCombo: true);
            HookFormField(group.InterleaveBox, isCombo: true);
            HookFormField(group.SizeBox);
            HookFormField(group.CopiesBox);
            HookFormField(group.FailuresBox);
            HookFormField(group.ColumnsBox);
            HookEnterCommit(group.SizeBox);
            HookEnterCommit(group.CopiesBox);
            HookEnterCommit(group.FailuresBox);
            HookEnterCommit(group.ColumnsBox);
        }

        HookFormField(_partitionStyleBox, isCombo: true);
        HookFormField(_fileSystemBox, isCombo: true);
        HookFormField(_clusterBox, isCombo: true);
        HookFormField(_volumeNameBox);
        HookFormField(_virtualDiskNameBox);
        HookFormField(_poolNameBox);
        HookEnterCommit(_poolNameBox);
        HookEnterCommit(_virtualDiskNameBox);
        HookEnterCommit(_volumeNameBox);
        _autoVdiskSwitch.Toggled += (_, _) =>
        {
            _formDirty = true;
            UpdateButtonState();
            SyncAutoVirtualDiskPlaceholder();
        };
        _autoPartitionSwitch.Toggled += (_, _) =>
        {
            _formDirty = true;
            UpdateButtonState();
        };

        var row = 0;
        row = AddSectionHeader(row, "PoolPropertiesSection", first: true);
        row = AddFormRow(row, "PoolName", _poolNameBox);
        row = AddFormRow(row, "VirtualDiskName", _virtualDiskNameBox);
        row = AddFormRow(row, "VolumeName", _volumeNameBox);
        row = AddFormRow(row, "AutoCreateVirtualDisk", _autoVdiskSwitch);
        row = AddFormRow(row, "AutoCreatePartition", _autoPartitionSwitch);
        foreach (var group in TierGroups())
        {
            row = AddTierGroup(row, group);
        }

        row = AddSectionHeader(row, "DiskAndPartitionSection");
        row = AddPartitionRow(row, "PartitionTableStyle", _partitionStyleBox, PartitionStyleChanged, () => ResetPartitionField("PartitionStyle"));
        row = AddPartitionRow(row, "FileSystem", _fileSystemBox, FileSystemChanged, () => ResetPartitionField("FileSystem"));
        row = AddPartitionRow(row, "AllocationUnit", _clusterBox, AllocationUnitChanged, () => ResetPartitionField("AllocationUnit"));

        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _multiVdiskWarning.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(_multiVdiskWarning, row);
        Grid.SetColumn(_multiVdiskWarning, 0);
        Grid.SetColumnSpan(_multiVdiskWarning, 3);
        PoolFormGrid.Children.Add(_multiVdiskWarning);
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
                    if (!_filling)
                    {
                        _formDirty = true;
                        UpdateButtonState();
                    }
                };
                break;
            case NumberBox number:
                number.ValueChanged += (_, _) =>
                {
                    if (!_filling)
                    {
                        _formDirty = true;
                        UpdateButtonState();
                    }
                };
                break;
            case ComboBox combo when isCombo:
                combo.SelectionChanged += (_, _) =>
                {
                    if (!_filling)
                    {
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
                NormalizeTierNumber(number);
            }

            Focus(FocusState.Programmatic);
            UpdateButtonState();
        };
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

            if (ReferenceEquals(group.SizeBox, number))
            {
                var maxBytes = TierCapacityMaxBytes(group.Media);
                if (NumValue(number) is null)
                {
                    SetNum(number, tier.Size > 0 ? Math.Round(tier.Size / 1024d / 1024d / 1024d, 2) : 0);
                }
                else if (maxBytes > 0
                    && NumValue(number) is { } sizeGb
                    && (long)(sizeGb * 1024d * 1024d * 1024d) > maxBytes)
                {
                    SetNum(number, Math.Round(maxBytes / 1024d / 1024d / 1024d, 2));
                }

                return;
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
                SetNum(number, Math.Clamp(value, 1, 64));
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

    private int AddTierGroup(int row, TierFields group)
    {
        row = AddSectionHeader(row, group.TitleKey, visibilityGroup: group.Rows);
        row = AddTierRow(row, group, "TierSize", group.SizeBox, () => ResetTierField(group, "Size"));
        row = AddTierRow(row, group, "TierProvisioning", group.ProvisioningText, null);
        row = AddTierRow(row, group, "TierResiliency", group.ResiliencyBox, () => ResetTierField(group, "Resiliency"));
        row = AddTierRow(row, group, "TierDataCopies", group.CopiesBox, () => ResetTierField(group, "Copies"));
        row = AddTierRow(row, group, "TierToleratedFailures", group.FailuresBox, () => ResetTierField(group, "Failures"));
        row = AddTierRow(row, group, "TierDiskCount", group.DiskCountBox, null);
        row = AddTierRow(row, group, "TierColumns", group.ColumnsBox, () => ResetTierField(group, "Columns"));
        row = AddTierRow(row, group, "TierStripeSize", group.InterleaveBox, () => ResetTierField(group, "Stripe"));
        return row;
    }

    private int AddTierRow(
        int row,
        TierFields group,
        string key,
        FrameworkElement value,
        Action? reset) =>
        AddFormRow(row, key, value, reset, TierRowChanged(group, key), group.Rows);

    private int AddPartitionRow(
        int row,
        string key,
        FrameworkElement value,
        Func<bool> changed,
        Action reset) =>
        AddFormRow(row, key, value, reset, changed, null);

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
            Width = 24,
            Height = 26,
            Padding = new Thickness(4),
            Content = new FontIcon
            {
                Glyph = "\uE777",
                FontSize = 12
            }
        };
        ToolTipService.SetToolTip(button, ViewModel.Localization["ResetRecommended"]);
        button.Click += (_, _) =>
        {
            if (_filling)
            {
                return;
            }

            reset();
            _formDirty = true;
            UpdateLinkedFields();
            UpdateButtonState();
        };
        return button;
    }

    /// <summary>
    /// Adds a section separator line plus its title. Every call consumes two
    /// grid rows; the separator is skipped for the first form section.
    /// </summary>
    private int AddSectionHeader(
        int row,
        string key,
        bool first = false,
        List<FrameworkElement>? visibilityGroup = null)
    {
        if (!first)
        {
            PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 6, 0, 3),
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
            };
            Grid.SetRow(line, row);
            Grid.SetColumn(line, 0);
            Grid.SetColumnSpan(line, 3);
            PoolFormGrid.Children.Add(line);
            visibilityGroup?.Add(line);
            row += 1;
        }

        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Margin = new Thickness(0, first ? 0 : 2, 0, 2),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            Text = ViewModel.Localization[key]
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetColumnSpan(label, 3);
        PoolFormGrid.Children.Add(label);
        visibilityGroup?.Add(label);
        return row + 1;
    }

    /// <summary>
    /// One uniform-height row: label (col 0), a fixed reset-button lane
    /// (col 1, blank on rows without one), value (col 2). Parameter rows
    /// show their reset button only while the field differs from the
    /// committed state.
    /// </summary>
    private int AddFormRow(
        int row,
        string key,
        FrameworkElement value,
        Action? reset = null,
        Func<bool>? changed = null,
        List<FrameworkElement>? visibilityGroup = null)
    {
        PoolFormGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = ViewModel.Localization[key]
        };
        value.VerticalAlignment = VerticalAlignment.Center;
        if (value is not ToggleSwitch)
        {
            value.Height = 32;
        }

        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        PoolFormGrid.Children.Add(label);
        PoolFormGrid.Children.Add(value);
        visibilityGroup?.Add(label);
        visibilityGroup?.Add(value);
        if (reset is null)
        {
            return row + 1;
        }

        var button = CreateResetButton(reset);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Visibility = Visibility.Collapsed;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, 1);
        PoolFormGrid.Children.Add(button);
        visibilityGroup?.Add(button);
        _fieldResets.Add(new FieldReset(button, changed ?? (() => false)));
        return row + 1;
    }

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

        var snapshot = ViewModel.ActiveSnapshot;
        if (snapshot.StoragePools.Any(item => item.StableId == stableId))
        {
            return stableId;
        }

        return snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == stableId)?.PoolStableId
            ?? snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == stableId)?.PoolStableId;
    }

    private void RefreshAll()
    {
        RefreshTopology();
        FillPoolForm();
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

        var root = EditWorkspace.ProjectPoolWorkspaceRoot(
            _working,
            UnallocatedIgnoreBytes,
            ViewModel.ActiveSnapshot,
            visible);
        var rootViewModel = new TopologyNodeViewModel(
            EditWorkspace.ToManageView(root, ViewModel.ActiveDocument.SystemId, "edit-pool-row"),
            ViewModel,
            _working,
            _interaction,
            isLayoutRoot: true);
        rootViewModel.SetSurfaceViewportWidth(_viewportWidth);
        ApplyPendingModificationStates(rootViewModel);
        TopologyControl.ItemsSource = new[] { rootViewModel };
    }

    private void ApplyPendingModificationStates(TopologyNodeViewModel root)
    {
        var committed = ViewModel.ActiveSnapshot;
        var queue = new Queue<TopologyNodeViewModel>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.ShowsEditStatus)
            {
                node.SetPendingModifications(
                    EditWorkspace.HasPendingModifications(_working, committed, node.Unit.StableId));
            }

            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    private StoragePoolInfo? SelectedPool() =>
        _working.StoragePools.FirstOrDefault(item => item.StableId == _selectedPoolId);

    private bool IsTopologyNodeSelected(TopologyNodeViewModel node) =>
        node.Unit.Kind switch
        {
            StorageUnitKind.PhysicalDisk => node.Unit.StableId == _selectedPoolDiskId,
            StorageUnitKind.VirtualDisk => node.Unit.StableId == _selectedPoolVdiskId,
            StorageUnitKind.StoragePool => node.Unit.StableId == _selectedPoolId
                && string.IsNullOrEmpty(_selectedPoolDiskId)
                && string.IsNullOrEmpty(_selectedPoolVdiskId),
            _ => false
        };

    private void OnTopologySelected(TopologyNodeViewModel node)
    {
        if (EditWorkspace.IsPlus(node.Unit.StableId))
        {
            CreateDraftPoolAndSelect();
            return;
        }

        switch (node.Unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
                _selectedPoolDiskId = node.Unit.StableId;
                _selectedPoolVdiskId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.PhysicalDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            case StorageUnitKind.VirtualDisk:
                _selectedPoolVdiskId = node.Unit.StableId;
                _selectedPoolDiskId = null;
                _selectedPoolId = node.Unit.ParentStableId
                    ?? _working.VirtualDisks.FirstOrDefault(item =>
                        item.StableId == node.Unit.StableId)?.PoolStableId;
                break;
            default:
                _selectedPoolVdiskId = null;
                _selectedPoolDiskId = null;
                _selectedPoolId = node.Unit.Kind == StorageUnitKind.StoragePool
                    ? node.Unit.StableId
                    : node.Unit.ParentStableId;
                break;
        }

        ResetLayerSwitchesForSelection();
        RefreshTopology();
        FillPoolForm();
        UpdateButtonState();
    }

    private void OnDiskDropped(string diskId, string targetId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
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

        _ = DropDiskIntoPoolAsync(diskId, targetId);
    }

    private async Task DropDiskIntoSimulatedLayerAsync(string diskId, string usage, string targetLayerId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        if (workingDisk is null)
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

        var confirm = usage == "Retired"
            ? ConfirmAsync(
                ViewModel.Localization["RetireDiskConfirmTitle"],
                ViewModel.Localization["RetireDiskConfirmMessage"])
            : ConfirmAsync(
                ViewModel.Localization["HotSpareDiskConfirmTitle"],
                ViewModel.Localization["HotSpareDiskConfirmMessage"]);
        if (!await confirm)
        {
            return;
        }

        if (!await ConfirmDiskSpecialRoleDrop(workingDisk))
        {
            return;
        }

        try
        {
            var next = EditWorkspace.SetDiskUsage(_working, diskId, usage);
            if (usage == "Retired")
            {
            }
            else
            {
            }

            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    private async Task EvictDroppedDiskToUnallocatedAsync(string diskId, string targetGroupId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        var groupPoolId = targetGroupId["group:direct:".Length..];
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        var selected = SelectedPool();
        if (workingDisk is null
            || selected is null
            || !selected.StableId.Equals(groupPoolId, StringComparison.OrdinalIgnoreCase))
        {
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
                _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
            }

            return;
        }

        if (!EditWorkspace.DiskIsAssignedToTier(_working, diskId))
        {
            return;
        }

        if (!await ConfirmDiskSpecialRoleDrop(workingDisk))
        {
            return;
        }

        try
        {
            CommitWorkingStep(EditWorkspace.EvictDiskToUnallocated(_working, diskId));
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    private async Task DropDiskIntoPoolAsync(string diskId, string poolId)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        // Drag-out rule: a real pool whose structure modification is
        // unsupported freezes its ORIGINAL committed members. A disk moved
        // in during this session can always be dragged back out.
        var workingDisk = _working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == diskId);
        var sourcePoolId = workingDisk?.PoolStableId;
        if (!string.IsNullOrEmpty(sourcePoolId)
            && !EditWorkspace.IsDraftPool(sourcePoolId)
            && _working.StoragePools.FirstOrDefault(candidate =>
                candidate.StableId == sourcePoolId) is { IsPrimordial: false } sourcePoolInfo
            && !EditWorkspace.PoolSupportsStructureModification(_working, sourcePoolInfo.StableId))
        {
            var committedDisk = ViewModel.ActiveSnapshot.PhysicalDisks.FirstOrDefault(
                item => item.StableId == diskId);
            var wasOriginalMember = committedDisk is not null
                && string.Equals(
                    committedDisk.PoolStableId,
                    sourcePoolId,
                    StringComparison.OrdinalIgnoreCase);
            if (wasOriginalMember && EditWorkspace.DiskIsAssignedToTier(_working, diskId))
            {
                return;
            }
        }

        try
        {
            var next = _working;
            if (EditWorkspace.IsPlus(poolId))
            {
                next = CreateDraftPoolStep(next);
            }
            else if (!EditWorkspace.IsPoolRow(poolId))
            {
                next = EditWorkspace.MoveDiskToPool(next, diskId, poolId);
            }

            if (!ReferenceEquals(next, _working))
            {
                CommitWorkingStep(next);
            }
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    /// <summary>
    /// Shared gate for retiring, hot-sparing, and evicting a pooled disk:
    /// page-file and crash-dump roles need one confirmation, system and
    /// boot disks are refused.
    /// </summary>
    private async Task<bool> ConfirmDiskSpecialRoleDrop(PhysicalDiskInfo disk)
    {
        switch (EditWorkspace.ClassifyDiskEvict(disk))
        {
            case EditWorkspace.DiskEvictCheck.DeniedSystem:
                await ShowMessageAsync(
                    ViewModel.Localization["SystemDiskCannotEvictTitle"],
                    ViewModel.Localization["SystemDiskCannotEvictMessage"]);
                return false;
            case EditWorkspace.DiskEvictCheck.ConfirmPageFile:
                if (!await ConfirmAsync(
                        ViewModel.Localization["RemovePageFileTitle"],
                        ViewModel.Localization["RemovePageFileMessage"]))
                {
                    return false;
                }

                _working = EditWorkspace.ClearEvictableSpecialRoles(_working, disk.StableId);
                break;
            case EditWorkspace.DiskEvictCheck.ConfirmCrashDump:
                if (!await ConfirmAsync(
                        ViewModel.Localization["RemoveCrashDumpTitle"],
                        ViewModel.Localization["RemoveCrashDumpMessage"]))
                {
                    return false;
                }

                _working = EditWorkspace.ClearEvictableSpecialRoles(_working, disk.StableId);
                break;
        }

        return true;
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

    private void FillPoolForm()
    {
        _filling = true;
        try
        {
            var pool = SelectedPool();
            var isCreateContext = pool is null || EditWorkspace.IsDraftPool(pool.StableId);
            var multi = pool is not null && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId);
            _multiVdiskWarning.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

            foreach (var group in TierGroups())
            {
                var visible = pool is not null
                    && !pool.IsPrimordial
                    && TierVisible(pool.StableId, group.Media);
                foreach (var element in group.Rows)
                {
                    element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (pool is null || pool.IsPrimordial)
            {
                FillRecommendedDefaults();
                return;
            }

            var tierCache = TierMap(pool.StableId);
            _poolNameBox.Text = pool.FriendlyName;
            var vdisk = _working.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            _virtualDiskNameBox.Text = vdisk?.FriendlyName ?? pool.FriendlyName;
            var partition = PrimaryPartition(pool.StableId);
            _volumeNameBox.Text = partition is not null
                ? partition.FileSystemLabel
                : vdisk?.FriendlyName ?? pool.FriendlyName;
            foreach (var group in TierGroups())
            {
                var tier = tierCache.GetValueOrDefault(group.Media);
                FillTierForm(group, tier);
            }

            _partitionStyleBox.SelectedItem = "GPT";
            _fileSystemBox.SelectedItem = "NTFS";
            _clusterBox.SelectedItem = "64K";
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

                if (partition.AllocationUnitSize is long cluster)
                {
                    _clusterBox.SelectedItem = ClusterToken(cluster);
                }
            }

            UpdateLinkedFields();
        }
        finally
        {
            _filling = false;
            _formDirty = false;
        }
    }

    private void FillRecommendedDefaults()
    {
        var nextName = NextPoolName();
        _poolNameBox.Text = nextName;
        _virtualDiskNameBox.Text = nextName;
        _volumeNameBox.Text = nextName;
        _autoPartitionSwitch.IsOn = true;
        foreach (var group in TierGroups())
        {
            SetResiliency(group.ResiliencyBox, "Mirror");
            SetInterleave(group.InterleaveBox, 65536);
            SetNum(group.SizeBox, null);
            SetNum(group.ColumnsBox, null);
            SetNum(group.CopiesBox, 2);
            SetNum(group.FailuresBox, 1);
            group.DiskCountBox.Text = string.Empty;
        }

        SetResiliency(Capacity.ResiliencyBox, "Parity");
        SetNum(Capacity.CopiesBox, 1);
        _partitionStyleBox.SelectedItem = "GPT";
        _fileSystemBox.SelectedItem = "NTFS";
        _clusterBox.SelectedItem = "64K";
        UpdateLinkedFields();
    }

    private void FillTierForm(TierFields group, StorageTierInfo? tier)
    {
        if (tier is null)
        {
            SetResiliency(group.ResiliencyBox, group.Media == "HDD" ? "Parity" : "Mirror");
            SetInterleave(group.InterleaveBox, 65536);
            SetNum(group.SizeBox, null);
            SetNum(group.ColumnsBox, null);
            SetNum(group.CopiesBox, 2);
            SetNum(group.FailuresBox, 1);
            group.DiskCountBox.Text = "0";
            return;
        }

        SetResiliency(group.ResiliencyBox, tier.ResiliencySettingName);
        SetInterleave(group.InterleaveBox, tier.Interleave ?? 65536);
        SetNum(group.SizeBox, tier.Size > 0 ? Math.Round(tier.Size / 1024d / 1024d / 1024d, 2) : null);
        SetNum(group.ColumnsBox, tier.NumberOfColumns);
        SetNum(group.CopiesBox, tier.NumberOfDataCopies ?? 1);
        SetNum(group.FailuresBox, tier.PhysicalDiskRedundancy ?? 1);
        group.DiskCountBox.Text = tier.MemberPhysicalDiskIds.Count.ToString();
    }

    private Dictionary<string, StorageTierInfo> TierMap(string poolId) =>
        _working.StorageTiers
            .Where(item => string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                item => EditWorkspace.NormalizeMedia(item.MediaType),
                StringComparer.OrdinalIgnoreCase);

    private int DataDiskCount(StoragePoolInfo pool, string media) =>
        _working.PhysicalDisks.Count(disk =>
            string.Equals(disk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && !disk.IsRetired
            && !disk.IsHotSpare
            && EditWorkspace.NormalizeMedia(disk.MediaType) == media);

    /// <summary>True when the tier object exists and holds member disks.</summary>
    private bool TierVisible(string poolId, string media) =>
        TierMap(poolId).GetValueOrDefault(media) is { MemberPhysicalDiskIds.Count: > 0 };

    /// <summary>Member disk count of the tier (0 when the tier does not exist).</summary>
    private int TierMemberCount(string poolId, string media) =>
        TierMap(poolId).GetValueOrDefault(media)?.MemberPhysicalDiskIds.Count ?? 0;

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
            .Where(item => item.OsDiskStableId == osDisk?.StableId && item.Type == "Primary")
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
            .Where(item => item.OsDiskStableId == osDisk?.StableId && item.Type == "Primary")
            .OrderBy(item => item.Offset)
            .ToArray();
    }

    private PartitionInfo? CommittedPrimaryPartition(string poolId)
    {
        var committed = ViewModel.ActiveSnapshot;
        var vdisk = committed.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase));
        if (vdisk is null)
        {
            return null;
        }

        var osDisk = committed.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        return committed.Partitions
            .Where(item => item.OsDiskStableId == osDisk?.StableId && item.Type == "Primary")
            .OrderBy(item => item.Offset)
            .FirstOrDefault();
    }

    private static void SetResiliency(ComboBox box, string value)
    {
        var match = box.Items.OfType<string>().FirstOrDefault(item =>
            item.Equals(value, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<string>().First();
    }

    private static void SetInterleave(ComboBox box, long bytes)
    {
        var token = $"{Math.Max(1, bytes / 1024)}K";
        box.SelectedItem = box.Items.OfType<string>().Contains(token) ? token : "64K";
    }

    private static string ToGigabytes(long? bytes) =>
        bytes is > 0 ? Math.Round(bytes.Value / 1024d / 1024d / 1024d, 2).ToString("0.##") : string.Empty;

    private void UpdateLinkedFields()
    {
        if (_filling)
        {
            return;
        }

        LinkResiliency(Performance);
        LinkResiliency(Capacity);
        LinkResiliency(Dedicated);
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
            return;
        }

        if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            var copies = NumValue(group.CopiesBox) is { } existing && existing >= 2
                ? (int)existing
                : 2;
            SetNum(group.CopiesBox, copies);
            SetNum(group.FailuresBox, Math.Max(0, copies - 1));
            return;
        }

        SetNum(group.CopiesBox, 1);
        var tolerated = NumValue(group.FailuresBox) is { } failures && failures >= 1
            ? (int)failures
            : 1;
        SetNum(group.FailuresBox, tolerated);
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
    /// property saves, form-level partition edits, or plain dirty fields.
    /// </summary>
    private bool HasUncommittedChanges() =>
        _formDirty
        || EditWorkspace.HasStructuralChanges(_working, ViewModel.ActiveSnapshot)
        || EditWorkspace.HasAnyPoolPropertyChanges(_working, ViewModel.ActiveSnapshot)
        || PartitionFormDiffersFromCommitted();

    private bool PartitionFormDiffersFromCommitted()
    {
        var poolId = _selectedPoolId;
        if (poolId is null || EditWorkspace.IsDraftPool(poolId))
        {
            return false;
        }

        var partition = CommittedPrimaryPartition(poolId);
        if (partition is null)
        {
            return false;
        }

        var volumeName = _volumeNameBox.Text.Trim();
        var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        var cluster = ParseSize(_clusterBox.SelectedItem as string ?? "64K");
        return !string.Equals(volumeName, partition.FileSystemLabel, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fileSystem, partition.FileSystem, StringComparison.OrdinalIgnoreCase)
            || cluster != partition.AllocationUnitSize;
    }

    private void UpdateButtonState()
    {
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var pool = SelectedPool();
        var hasUnapplied = HasUncommittedChanges();

        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
        DiscardAllButton.IsEnabled = hasUnapplied;
        ApplyAllButton.IsEnabled = simulated && hasUnapplied;
        CreatePoolButton.IsEnabled = simulated
            && !_working!.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId));
        DissolveButton.IsEnabled = simulated && pool is { IsPrimordial: false };
        SavePoolPropertiesButton.IsEnabled = simulated
            && _formDirty
            && pool is { IsPrimordial: false } && !EditWorkspace.IsDraftPool(pool.StableId);

        var selectedDisk = _working!.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        var canLayerDisk = simulated
            && pool is { IsPrimordial: false }
            && !EditWorkspace.IsDraftPool(pool.StableId)
            && selectedDisk is not null
            && string.Equals(selectedDisk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase);
        RetireButton.IsEnabled = canLayerDisk && !selectedDisk!.IsRetired;
        HotSpareButton.IsEnabled = canLayerDisk && !selectedDisk!.IsHotSpare;

        var (poolVdisks, deleteTarget) = PoolVirtualDiskState(pool);
        var realVdisk = poolVdisks.FirstOrDefault(item => !EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var canEditPool = simulated
            && pool is { IsPrimordial: false }
            && !EditWorkspace.IsDraftPool(pool.StableId);
        CreateVdiskButton.IsEnabled = canEditPool
            && realVdisk is null
            && DataDiskCount(pool!, "SSD") + DataDiskCount(pool!, "HDD") + DataDiskCount(pool!, "SCM") > 0;
        DeleteVdiskButton.IsEnabled = canEditPool && deleteTarget is not null;

        var formEnabled = simulated
            && pool is { IsPrimordial: false }
            && poolVdisks.Count(item => !EditWorkspace.IsDraftVirtualDisk(item.StableId)) <= 1;
        ShowHotSpareSwitch.IsEnabled = simulated;
        ShowRetiredSwitch.IsEnabled = simulated;
        UpdateFormStates(formEnabled, pool, realVdisk, realVdisk is not null);
        UpdateFieldResets();
    }

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
        _poolNameBox.IsEnabled = formEnabled;
        _virtualDiskNameBox.IsEnabled = formEnabled;
        var volumePartition = pool is not null && vdisk is not null
            ? PrimaryPartition(pool.StableId)
            : null;
        _volumeNameBox.IsEnabled = formEnabled
            && (vdisk is null || volumePartition is not null);
        // Auto-create virtual disk only matters while a draft pool is being
        // put together; committed pools create through the upper-right
        // button and never auto-create.
        _autoVdiskSwitch.IsEnabled = formEnabled && isDraft;
        // Auto-create partition applies whenever a virtual disk can still be
        // created here: a draft with auto-vdisk on, or an empty committed
        // pool before its first virtual disk.
        var creationContext = formEnabled && !hasVdisk;
        _autoPartitionSwitch.IsEnabled = creationContext
            && (!isDraft || _autoVdiskSwitch.IsOn);

        if (!formEnabled || pool is null)
        {
            return;
        }

        var holdsData = EditWorkspace.PoolHoldsStoredData(_working, pool.StableId);
        foreach (var group in TierGroups())
        {
            var tier = TierMap(pool.StableId).GetValueOrDefault(group.Media);
            var tierVisible = TierVisible(pool.StableId, group.Media);
            // Capacity (Size) is a reservation: it stays editable even on
            // data-bearing pools. Destructive spec fields are not.
            var sizeEditable = tierVisible && tier is not null;
            var specEditable = sizeEditable && !holdsData;
            group.SizeBox.IsEnabled = sizeEditable;
            group.ResiliencyBox.IsEnabled = specEditable;
            group.InterleaveBox.IsEnabled = specEditable;
            group.DiskCountBox.IsReadOnly = true;
            if (!tierVisible)
            {
                continue;
            }

            var resiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
            var isMirror = resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase);
            var isSimple = resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase);
            LinkResiliency(group);
            group.DiskCountBox.Text = TierMemberCount(pool.StableId, group.Media).ToString();
            group.CopiesBox.IsEnabled = specEditable && isMirror;
            group.FailuresBox.IsEnabled = specEditable && !isMirror && !isSimple;
            group.ColumnsBox.IsEnabled = specEditable && !isMirror && !isSimple;
            if (isMirror || isSimple)
            {
                SetNum(group.ColumnsBox, null);
            }
        }

        // Disk and partition group.
        var partition = PrimaryPartition(pool.StableId);
        var userPartitions = UserPartitions(pool.StableId);
        var canEditPartition = !hasVdisk
            && _autoPartitionSwitch.IsOn
            && _autoPartitionSwitch.IsEnabled
            && !holdsData;
        var partitionHasData = hasVdisk && EditWorkspace.DiskHoldsStoredData(
            _working,
            vdisk!.StableId,
            isVirtualDisk: true);
        var initialized = hasVdisk && partition is not null;
        _partitionStyleBox.IsEnabled = canEditPartition || (hasVdisk && !initialized && !holdsData);
        var fsEditable = hasVdisk
            ? initialized && !partitionHasData && userPartitions.Count <= 1
            : canEditPartition;
        _fileSystemBox.IsEnabled = fsEditable;
        _clusterBox.IsEnabled = fsEditable;
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

        var merged = ApplyFormToWorking(_working);
        _working = merged;
        _formDirty = false;
    }

    private void CommitWorkingStep(StorageSnapshot next)
    {
        if (ReferenceEquals(next, _working))
        {
            // Nothing object-backed changed (e.g. Save with only form-level
            // partition fields edited, which Apply consumes directly). Keep
            // the form text untouched and treat the edit as saved.
            _formDirty = false;
            UpdateButtonState();
            return;
        }

        _undoStack.Push(_working);
        _redoStack.Clear();
        _working = next;
        _formDirty = false;
        RefreshAll();
    }

    private void CreateDraftPoolAndSelect()
    {
        if (!ViewModel.IsUsingSimulatedInventory
            || _working.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            return;
        }

        MergeFormIntoWorking();
        try
        {
            var next = CreateDraftPoolStep(_working);
            var draftId = next.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
            if (_autoVdiskSwitch.IsOn)
            {
                next = EditWorkspace.InsertDraftVirtualDisk(
                    next,
                    draftId,
                    ViewModel.Localization["NotCreatedVirtualDisk"],
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
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
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
        if (_undoStack.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["UndoStepTitle"],
                ViewModel.Localization["UndoStepMessage"]);
            return;
        }

        _redoStack.Push(_working);
        _working = _undoStack.Pop();
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["RedoStepTitle"],
                ViewModel.Localization["RedoStepMessage"]);
            return;
        }

        _undoStack.Push(_working);
        _working = _redoStack.Pop();
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    private async void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        if (!HasUncommittedChanges())
        {
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        _working = ViewModel.ActiveSnapshot;
        _formDirty = false;
        _selectedPoolId = null;
        _selectedPoolDiskId = null;
        _selectedPoolVdiskId = null;
        ResetLayerSwitchesForSelection();
        RefreshAll();
    }

    /// <summary>Keeps only selection ids that still exist in the working copy.</summary>
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
    }

    private async void CreatePool_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsUsingSimulatedInventory
            || _working.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            return;
        }

        CreateDraftPoolAndSelect();
    }

    private async void Dissolve_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        try
        {
            var next = _working;
            if (EditWorkspace.IsDraftPool(pool.StableId))
            {
                if (!await ConfirmAsync(
                        ViewModel.Localization["DissolvePoolTitle"],
                        ViewModel.Localization["DraftPoolDiscardConfirmMessage"]))
                {
                    return;
                }

                next = EditWorkspace.DiscardDraftPool(next, pool.StableId);
            }
            else
            {
                if (!await ConfirmAsync(
                        ViewModel.Localization["DissolvePoolTitle"],
                        ViewModel.Localization["DissolvePoolMessage"]))
                {
                    return;
                }

                next = EditWorkspace.DissolvePoolInWorking(next, pool.StableId);
            }

            _selectedPoolId = null;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            ResetLayerSwitchesForSelection();
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    private async void Retire_Click(object sender, RoutedEventArgs e) =>
        await ChangeDiskUsageAsync("Retired");

    private async void HotSpare_Click(object sender, RoutedEventArgs e) =>
        await ChangeDiskUsageAsync("HotSpare");

    private async Task ChangeDiskUsageAsync(string usage)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        var disk = _working.PhysicalDisks.FirstOrDefault(item => item.StableId == _selectedPoolDiskId);
        var pool = SelectedPool();
        if (disk is null
            || pool is null
            || pool.IsPrimordial
            || EditWorkspace.IsDraftPool(pool.StableId)
            || !string.Equals(disk.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var confirm = usage == "Retired"
            ? ConfirmAsync(
                ViewModel.Localization["RetireDiskConfirmTitle"],
                ViewModel.Localization["RetireDiskConfirmMessage"])
            : ConfirmAsync(
                ViewModel.Localization["HotSpareDiskConfirmTitle"],
                ViewModel.Localization["HotSpareDiskConfirmMessage"]);
        if (!await confirm)
        {
            return;
        }

        // Layer confirmation comes first: the role-dropping confirmation
        // below clears page-file / crash-dump roles immediately, and no
        // further user cancel point may follow it.
        if (!await ConfirmDiskSpecialRoleDrop(disk))
        {
            return;
        }

        try
        {
            var next = EditWorkspace.SetDiskUsage(_working, disk.StableId, usage);
            if (usage == "Retired")
            {
            }
            else
            {
            }

            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    private async void CreateVdisk_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null
            || pool.IsPrimordial
            || EditWorkspace.IsDraftPool(pool.StableId)
            || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

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
            var next = EditWorkspace.InsertDraftVirtualDisk(_working, pool.StableId, name, resiliency, interleave);
            _selectedPoolVdiskId = next.VirtualDisks.Last(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)).StableId;
            _selectedPoolDiskId = null;
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
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

    private async void DeleteVdisk_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (pool is null
            || pool.IsPrimordial
            || EditWorkspace.IsDraftPool(pool.StableId)
            || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        MergeFormIntoWorking();
        var (_, deleteTarget) = PoolVirtualDiskState(pool);
        if (deleteTarget is null)
        {
            return;
        }

        var vdisk = deleteTarget;
        var holdsData = EditWorkspace.DiskHoldsStoredData(_working, vdisk.StableId, isVirtualDisk: true);
        var committedVdisk = ViewModel.ActiveSnapshot.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.StableId, vdisk.StableId, StringComparison.OrdinalIgnoreCase));
        if (committedVdisk is not null)
        {
            if (holdsData
                && !await ConfirmAsync(
                    Text("删除虚拟磁盘", "Delete virtual disk"),
                    Text(
                        "该虚拟磁盘上有已使用的数据。删除会移除该虚拟磁盘及其卷。确定继续？",
                        "This virtual disk holds used data. Deleting removes the virtual disk and its volume. Continue anyway?")))
            {
                return;
            }
        }

        try
        {
            var next = EditWorkspace.DeleteVirtualDiskFromWorking(_working, vdisk.StableId);
            _selectedPoolVdiskId = null;
            CommitWorkingStep(next);
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

    // ---- Apply-all -----------------------------------------------------

    private async void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsUsingSimulatedInventory)
        {
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
            if (!EditWorkspace.HasPoolPropertyChanges(pending, ViewModel.ActiveSnapshot, pool.StableId)
                || !EditWorkspace.PoolHoldsStoredData(ViewModel.ActiveSnapshot, pool.StableId)
                || !RebuildsTierParameters(pending, ViewModel.ActiveSnapshot, pool.StableId))
            {
                continue;
            }

            await ShowMessageAsync(
                ViewModel.Localization["RebuildBlockedTitle"],
                ViewModel.Localization["RebuildBlockedMessage"]);
            return;
        }

        var preview = BuildApplyPreviewSteps(pending, ViewModel.ActiveSnapshot);
        if (preview.Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.Localization["NoApplyChangesTitle"],
                ViewModel.Localization["NoApplyChangesMessage"]);
            return;
        }

        var createsDraftPool = pending.StoragePools.Any(item => EditWorkspace.IsDraftPool(item.StableId));
        var createsVdisk = pending.VirtualDisks.Any(item => EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var reFsInvolved = string.Equals(
            _fileSystemBox.SelectedItem as string,
            "ReFS",
            StringComparison.OrdinalIgnoreCase)
            && (createsDraftPool || createsVdisk || ReFsWouldChangeOnApply());
        var touchesUnrecommended = pending.StorageTiers.Any(tier =>
                (tier.Interleave ?? 0) == 256 * 1024)
            || createsDraftPool
            || createsVdisk
            || reFsInvolved;
        if (touchesUnrecommended && !await ConfirmUnrecommendedAsync())
        {
            return;
        }

        var joiningWithData = pending.PhysicalDisks
            .Where(disk =>
            {
                var committedDisk = ViewModel.ActiveSnapshot.PhysicalDisks.FirstOrDefault(item =>
                    item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
                return committedDisk is not null
                    && !string.Equals(committedDisk.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(disk.PoolStableId)
                    && EditWorkspace.DiskHoldsStoredData(ViewModel.ActiveSnapshot, disk.StableId);
            })
            .ToArray();
        if (joiningWithData.Length > 0)
        {
            var names = string.Join(", ", joiningWithData.Select(disk => disk.FriendlyName));
            if (!await ConfirmAsync(
                    Text("加入磁盘将清除数据", "Joining disks clears data"),
                    Text(
                        $"以下磁盘带有已用数据，加入池后这些数据将不再可用：{names}。确定继续？",
                        $"These disks hold used data that becomes unusable after joining the pool: {names}. Continue?")))
            {
                return;
            }
        }

        var lines = string.Join("\n", preview.Select(step => "• " + step));
        if (!await ConfirmAsync(ViewModel.Localization["ApplyPreviewTitle"], lines))
        {
            return;
        }

        if (!await ApplyPendingSequenceAsync(pending))
        {
            // A mid-sequence rejection leaves some edits already applied.
            // Sync the working copy to the committed snapshot and drop the
            // draft, so it can never be applied a second time (which would
            // duplicate a pool or virtual disk).
            _undoStack.Clear();
            _redoStack.Clear();
            _working = ViewModel.ActiveSnapshot;
            _formDirty = false;
            NormalizeSelection();
            ResetLayerSwitchesForSelection();
            RefreshAll();
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        _working = ViewModel.ActiveSnapshot;
        _selectedPoolId = EditWorkspace.IsDraftPool(_selectedPoolId ?? string.Empty)
            ? _working.StoragePools.LastOrDefault(item => !item.IsPrimordial)?.StableId
            : _selectedPoolId;
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        _formDirty = false;
        RefreshAll();
    }

    /// <summary>
    /// Writes every edited property-form field into the working copy as one
    /// structural draft step (undoable). Partition-level create/format
    /// values stay on the form and are consumed by Apply.
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

        if (!pool.FriendlyName.Equals(poolName, StringComparison.OrdinalIgnoreCase))
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

        foreach (var group in TierGroups())
        {
            var tier = result.StorageTiers.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
                && EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
            if (tier is null || tier.MemberPhysicalDiskIds.Count == 0)
            {
                continue;
            }

            var resiliency = group.ResiliencyBox.SelectedItem as string ?? tier.ResiliencySettingName;
            var interleave = ParseSize(group.InterleaveBox.SelectedItem as string ?? "64K");
            var copies = NumValue(group.CopiesBox) is { } copyValue
                ? Math.Max(1, (int)copyValue)
                : tier.NumberOfDataCopies ?? 1;
            var failures = NumValue(group.FailuresBox) is { } failureValue
                ? Math.Max(0, (int)failureValue)
                : tier.PhysicalDiskRedundancy ?? 1;
            var columns = NumValue(group.ColumnsBox) is { } columnValue
                ? (int)columnValue
                : tier.NumberOfColumns;
            var size = NumValue(group.SizeBox) is { } sizeValue
                ? (long)(sizeValue * 1024L * 1024L * 1024L)
                : (long?)null;
            var changed = !string.Equals(resiliency, tier.ResiliencySettingName, StringComparison.OrdinalIgnoreCase)
                || interleave != (tier.Interleave ?? 0)
                || copies != tier.NumberOfDataCopies
                || failures != tier.PhysicalDiskRedundancy
                || columns != tier.NumberOfColumns
                || (size is not null && size != tier.Size);
            if (changed)
            {
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
                                FootprintOnPool = size ?? tier.Size
                            }
                            : item)
                        .ToArray()
                };
            }
        }

        return result;
    }

    private IReadOnlyList<string> BuildApplyPreviewSteps(StorageSnapshot working, StorageSnapshot committed)
    {
        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var steps = new List<string>();

        // Dissolving committed pools.
        foreach (var committedPool in committed.StoragePools.Where(item => !item.IsPrimordial))
        {
            if (!working.StoragePools.Any(item =>
                    item.StableId.Equals(committedPool.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(zh
                    ? $"解散存储池“{committedPool.FriendlyName}”，成员磁盘回到原始池。"
                    : $"Dissolve storage pool \"{committedPool.FriendlyName}\"; member disks return to the primordial pool.");
            }
        }

        // Creating draft pools.
        foreach (var draft in working.StoragePools.Where(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            var name = draft.FriendlyName;
            var memberCount = draft.MemberPhysicalDiskIds.Count;
            steps.Add(zh
                ? $"创建存储池“{name}”，加入 {memberCount} 个物理磁盘，并按介质划分层。"
                : $"Create storage pool \"{name}\", join {memberCount} physical disk(s), and tier them by media.");
            var tierLines = draft.MemberPhysicalDiskIds
                .Select(id => working.PhysicalDisks.FirstOrDefault(disk => disk.StableId == id))
                .Where(disk => disk is not null)
                .GroupBy(disk => EditWorkspace.NormalizeMedia(disk!.MediaType))
                .Select(group => TierPreview(group.Key, working, draft.StableId));
            foreach (var line in tierLines.Where(line => line is not null))
            {
                steps.Add(line!);
            }

            var draftVirtualName = _virtualDiskNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(draftVirtualName))
            {
                draftVirtualName = draft.FriendlyName;
            }

            steps.Add(zh
                ? $"在该池上创建虚拟磁盘“{draftVirtualName}”。"
                : $"Create virtual disk \"{draftVirtualName}\" on the pool.");
            if (_autoPartitionSwitch.IsOn)
            {
                steps.Add(zh
                    ? $"创建 {_fileSystemBox.SelectedItem} 用户分区并格式化（簇 {_clusterBox.SelectedItem}）。"
                    : $"Create and format a {_fileSystemBox.SelectedItem} user partition ({_clusterBox.SelectedItem} cluster).");
            }
            else
            {
                steps.Add(zh
                    ? "虚拟磁盘保持未初始化（RAW），稍后在磁盘分区页初始化。"
                    : "Leave the virtual disk RAW; initialize it on the Disk partition page.");
            }
        }

        // Virtual-disk deletions.
        foreach (var vdisk in committed.VirtualDisks)
        {
            if (!working.VirtualDisks.Any(item =>
                    item.StableId.Equals(vdisk.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(zh
                    ? $"删除虚拟磁盘“{vdisk.FriendlyName}”及其卷。"
                    : $"Delete virtual disk \"{vdisk.FriendlyName}\" and its volumes.");
            }
        }

        // Virtual-disk creations (committed pools only).
        foreach (var draftVdisk in working.VirtualDisks.Where(item =>
                     EditWorkspace.IsDraftVirtualDisk(item.StableId)))
        {
            var poolName = working.StoragePools.FirstOrDefault(item =>
                item.StableId == draftVdisk.PoolStableId)?.FriendlyName ?? draftVdisk.PoolStableId;
            steps.Add(zh
                ? $"在存储池“{poolName}”上创建虚拟磁盘“{draftVdisk.FriendlyName}”。"
                : $"Create virtual disk \"{draftVdisk.FriendlyName}\" on pool \"{poolName}\".");
            if (_autoPartitionSwitch.IsOn)
            {
                steps.Add(zh
                    ? $"创建 {_fileSystemBox.SelectedItem} 用户分区并格式化（簇 {_clusterBox.SelectedItem}）。"
                    : $"Create and format a {_fileSystemBox.SelectedItem} user partition ({_clusterBox.SelectedItem} cluster).");
            }
            else
            {
                steps.Add(zh
                    ? "虚拟磁盘保持未初始化（RAW），稍后在磁盘分区页初始化。"
                    : "Leave the virtual disk RAW; initialize it on the Disk partition page.");
            }
        }

        // Disk-level structure differences.
        foreach (var disk in working.PhysicalDisks)
        {
            var committedDisk = committed.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
            if (committedDisk is null)
            {
                continue;
            }

            var movedPool = !string.Equals(committedDisk.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase);
            var roleChanged = committedDisk.IsRetired != disk.IsRetired
                || committedDisk.IsHotSpare != disk.IsHotSpare;
            var assigned = EditWorkspace.DiskIsAssignedToTier(working, disk.StableId);
            var wasAssigned = EditWorkspace.DiskIsAssignedToTier(committed, disk.StableId);
            if (movedPool)
            {
                var target = string.IsNullOrEmpty(disk.PoolStableId)
                    ? zh ? "原始池" : "the primordial pool"
                    : working.StoragePools.FirstOrDefault(pool =>
                        pool.StableId.Equals(disk.PoolStableId, StringComparison.OrdinalIgnoreCase))?.FriendlyName
                        ?? disk.PoolStableId;
                steps.Add(zh
                    ? $"将磁盘“{disk.FriendlyName}”移入“{target}”。"
                    : $"Move disk \"{disk.FriendlyName}\" into \"{target}\".");
                continue;
            }

            if (roleChanged)
            {
                if (disk.IsRetired)
                {
                    steps.Add(zh
                        ? $"将磁盘“{disk.FriendlyName}”移入退役层。"
                        : $"Move disk \"{disk.FriendlyName}\" into the retired layer.");
                }
                else if (disk.IsHotSpare)
                {
                    steps.Add(zh
                        ? $"将磁盘“{disk.FriendlyName}”移入热备层。"
                        : $"Move disk \"{disk.FriendlyName}\" into the hot-spare layer.");
                }
                else
                {
                    steps.Add(zh
                        ? $"将磁盘“{disk.FriendlyName}”从模拟层移回。"
                        : $"Move disk \"{disk.FriendlyName}\" out of the simulated layer.");
                }
            }

            if (wasAssigned && !assigned && !disk.IsRetired && !disk.IsHotSpare)
            {
                steps.Add(zh
                    ? $"将磁盘“{disk.FriendlyName}”从数据层退出，保留在池的未分配区域。"
                    : $"Remove disk \"{disk.FriendlyName}\" from its tier, keeping it unallocated in the pool.");
            }
            else if (!wasAssigned && assigned)
            {
                steps.Add(zh
                    ? $"将磁盘“{disk.FriendlyName}”重新加入匹配数据层。"
                    : $"Reassign disk \"{disk.FriendlyName}\" to its matching data tier.");
            }
        }

        // Property changes on committed pools (object-backed names and tier
        // parameters), for every pool whose saved draft differs.
        foreach (var pool in working.StoragePools.Where(item =>
                     !item.IsPrimordial && !EditWorkspace.IsDraftPool(item.StableId)))
        {
            if (!EditWorkspace.HasPoolPropertyChanges(working, committed, pool.StableId))
            {
                continue;
            }

            var committedPool = committed.StoragePools.FirstOrDefault(item =>
                string.Equals(item.StableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            if (committedPool is null)
            {
                continue;
            }

            if (!pool.FriendlyName.Equals(committedPool.FriendlyName, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(zh
                    ? $"将存储池重命名为“{pool.FriendlyName}”。"
                    : $"Rename the pool to \"{pool.FriendlyName}\".");
            }

            var workingVdisk = working.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
                && !EditWorkspace.IsDraftVirtualDisk(item.StableId));
            var committedVdisk = committed.VirtualDisks.FirstOrDefault(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase));
            if (workingVdisk is not null
                && committedVdisk is not null
                && !workingVdisk.FriendlyName.Equals(
                    committedVdisk.FriendlyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(zh
                    ? $"将虚拟磁盘重命名为“{workingVdisk.FriendlyName}”。"
                    : $"Rename the virtual disk to \"{workingVdisk.FriendlyName}\".");
            }

            foreach (var group in TierGroups())
            {
                var workingTier = working.StorageTiers.FirstOrDefault(item =>
                    string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
                    && EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
                var committedTier = committed.StorageTiers.FirstOrDefault(item =>
                    string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
                    && EditWorkspace.NormalizeMedia(item.MediaType) == group.Media);
                if (workingTier is null || committedTier is null
                    || TierSpecsEqual(workingTier, committedTier))
                {
                    continue;
                }

                steps.Add(zh
                    ? $"重建“{group.TitleKey}”层参数（冗余、交织、容量、列数）。"
                    : $"Rebuild {TierFriendly(group)} tier parameters (resiliency, interleave, capacity, columns).");
            }
        }

        // Form-level edits on the selected pool's user partition.
        var propertyPoolId = _selectedPoolId;
        if (propertyPoolId is not null && !EditWorkspace.IsDraftPool(propertyPoolId))
        {
            var committedPartition = CommittedPrimaryPartition(propertyPoolId);
            if (committedPartition is not null)
            {
                var volumeName = _volumeNameBox.Text.Trim();
                var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
                var fsChanged = !string.Equals(
                    fileSystem,
                    committedPartition.FileSystem,
                    StringComparison.OrdinalIgnoreCase);
                var clusterChanged = ParseSize(_clusterBox.SelectedItem as string ?? "64K")
                    != committedPartition.AllocationUnitSize;
                var labelChanged = !string.Equals(
                    volumeName,
                    committedPartition.FileSystemLabel,
                    StringComparison.OrdinalIgnoreCase);
                if (fsChanged || clusterChanged)
                {
                    steps.Add(zh
                        ? $"将用户卷重新格式化为 {fileSystem}（簇 {_clusterBox.SelectedItem}）。"
                        : $"Reformat the user volume as {fileSystem} ({_clusterBox.SelectedItem} cluster).");
                }
                else if (labelChanged)
                {
                    steps.Add(zh
                        ? $"将卷重命名为“{volumeName}”。"
                        : $"Rename the volume to \"{volumeName}\".");
                }
            }
        }

        return steps;
    }

    private string? TierPreview(string media, StorageSnapshot snapshot, string poolId)
    {
        var tier = snapshot.StorageTiers.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && EditWorkspace.NormalizeMedia(item.MediaType) == media);
        if (tier is null)
        {
            return null;
        }

        var zh = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var tierName = media switch
        {
            "SSD" => "SSD",
            "HDD" => "HDD",
            _ => "SCM"
        };
        return zh
            ? $"{tierName} 层：{ResiliencyName(tier.ResiliencySettingName)}，交织 {tier.Interleave / 1024}K。"
            : $"{tierName} tier: {tier.ResiliencySettingName}, {tier.Interleave / 1024}K interleave.";
    }

    private string TierFriendly(TierFields group) => group.TitleKey switch
    {
        "PerformanceTier" => "performance",
        "DedicatedTier" => "dedicated",
        _ => "capacity"
    };

    /// <summary>
    /// True when destructive tier parameters changed (resiliency, stripe,
    /// copies, failures, columns). Size is a capacity reservation and can be
    /// edited even on data-bearing pools, so it is excluded here.
    /// </summary>
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

    private bool ReFsWouldChangeOnApply()
    {
        var poolId = _selectedPoolId;
        if (poolId is null || EditWorkspace.IsDraftPool(poolId))
        {
            return false;
        }

        var partition = CommittedPrimaryPartition(poolId);
        return partition is not null
            && !string.Equals(partition.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TierSpecsEqual(StorageTierInfo left, StorageTierInfo right) =>
        string.Equals(left.ResiliencySettingName, right.ResiliencySettingName, StringComparison.OrdinalIgnoreCase)
        && left.Interleave == right.Interleave
        && left.NumberOfDataCopies == right.NumberOfDataCopies
        && left.PhysicalDiskRedundancy == right.PhysicalDiskRedundancy
        && left.NumberOfColumns == right.NumberOfColumns
        && left.Size == right.Size;

    private string ResiliencyName(string resiliency) =>
        resiliency switch
        {
            "Simple" => "无冗余 (Simple)",
            "Mirror" => "镜像 (Mirror)",
            "Parity" => "奇偶校验 (Parity)",
            _ => resiliency
        };

    /// <summary>
    /// The research defaults are changeable, but 256K interleave and ReFS are
    /// outside the tested recommendation and are never applied silently
    /// (V0.47 design §8).
    /// </summary>
    private async Task<bool> ConfirmUnrecommendedAsync()
    {
        if (new[] { Performance, Capacity, Dedicated }
                .Any(group => (group.InterleaveBox.SelectedItem as string) == "256K")
            && !await ConfirmAsync(
                Text("256K 交织警告", "256K interleave warning"),
                Text(
                    "256K 交织不在当前测试推荐中。当前测试推荐为 64K 交织 + 64K NTFS 簇。确定继续？",
                    "256K interleave is outside the tested recommendation (64K interleave + 64K NTFS cluster). Continue anyway?")))
        {
            return false;
        }

        if (string.Equals(_fileSystemBox.SelectedItem as string, "ReFS", StringComparison.OrdinalIgnoreCase)
            && !await ConfirmAsync(
                Text("ReFS 提示", "ReFS notice"),
                Text(
                    "ReFS 没有与 NTFS 64K 同等的长期测试证据。确定继续？",
                    "ReFS has no long-run evidence equivalent to NTFS 64K. Continue anyway?")))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> ApplyPendingSequenceAsync(StorageSnapshot pending)
    {
        // 1. Dissolve committed pools removed from the working copy.
        var committed = ViewModel.ActiveSnapshot;
        foreach (var pool in committed.StoragePools.Where(item => !item.IsPrimordial))
        {
            if (pending.StoragePools.Any(item =>
                    item.StableId.Equals(pool.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.DissolveStoragePool,
                    pool.StableId)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 2. Create draft pools with their media tiers and one virtual disk.
        foreach (var draft in pending.StoragePools.Where(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            if (await ApplyAsync(BuildPoolCreateRequest(SimulationOperationKind.CreateTieredPool, draft)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 3. Delete committed virtual disks removed from the working copy.
        foreach (var vdisk in committed.VirtualDisks)
        {
            if (pending.VirtualDisks.Any(item =>
                    item.StableId.Equals(vdisk.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.DeleteVirtualDisk,
                    vdisk.StableId)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 4. Create draft virtual disks on committed pools. A draft-pool
        // placeholder was already materialized by CreateTieredPool above.
        foreach (var draftVdisk in pending.VirtualDisks.Where(item =>
                     EditWorkspace.IsDraftVirtualDisk(item.StableId)
                     && !EditWorkspace.IsDraftPool(item.PoolStableId ?? string.Empty)))
        {
            if (!await ApplyDraftVirtualDiskAsync(pending, draftVdisk))
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 5. Move disks between pools.
        foreach (var disk in pending.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.MovePhysicalDisk,
                    disk.StableId,
                    Name: disk.PoolStableId ?? string.Empty)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 6. Evict disks that left their real tier (same pool).
        foreach (var disk in pending.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || !string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase)
                || disk.IsRetired
                || disk.IsHotSpare
                || EditWorkspace.DiskIsAssignedToTier(pending, disk.StableId)
                || !EditWorkspace.DiskIsAssignedToTier(committed, disk.StableId))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.EvictPhysicalDiskFromTiers,
                    disk.StableId)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 7. Reassign same-pool unallocated disks back onto their tier.
        foreach (var disk in pending.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            if (disk.IsRetired
                || disk.IsHotSpare
                || !EditWorkspace.DiskNeedsSamePoolTierAssignment(pending, committed, disk.StableId)
                || string.IsNullOrEmpty(disk.PoolStableId))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.MovePhysicalDisk,
                    disk.StableId,
                    Name: disk.PoolStableId)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 8. Simulated-layer role changes (same pool).
        foreach (var disk in pending.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || !string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var usage = EditWorkspace.DiskUsage(disk);
            if (usage == EditWorkspace.DiskUsage(original))
            {
                continue;
            }

            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.SetDiskUsage,
                    disk.StableId,
                    Name: usage)) is null)
            {
                return false;
            }
        }

        committed = ViewModel.ActiveSnapshot;

        // 9. Property edits on committed pools (names and tier parameters
        // that were saved into the draft), for every pool with a difference.
        foreach (var pool in pending.StoragePools.Where(item =>
                     !item.IsPrimordial && !EditWorkspace.IsDraftPool(item.StableId)))
        {
            if (!EditWorkspace.HasPoolPropertyChanges(pending, committed, pool.StableId))
            {
                continue;
            }

            var hasData = EditWorkspace.PoolHoldsStoredData(committed, pool.StableId);
            var tierChanged = RebuildsTierParameters(pending, committed, pool.StableId);
            if (hasData && tierChanged)
            {
                await ShowMessageAsync(
                    ViewModel.Localization["RebuildBlockedTitle"],
                    ViewModel.Localization["RebuildBlockedMessage"]);
                return false;
            }

            if (await ApplyAsync(BuildPoolPropertyRequest(pool, pending)) is null)
            {
                return false;
            }

            // Form-level partition edits belong to the selected pool, whose
            // form is on screen.
            if (string.Equals(pool.StableId, _selectedPoolId, StringComparison.OrdinalIgnoreCase)
                && await ApplyPartitionChangesAsync(
                    pending,
                    ViewModel.ActiveSnapshot,
                    pool.StableId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies form-level partition edits (volume name, file system, cluster)
    /// against the committed user partition. Returns true when a step failed
    /// and the caller must stop.
    /// </summary>
    private async Task<bool> ApplyPartitionChangesAsync(
        StorageSnapshot pending,
        StorageSnapshot committed,
        string poolId)
    {
        var committedVdisk = committed.VirtualDisks.FirstOrDefault(item => item.PoolStableId == poolId);
        if (committedVdisk is null)
        {
            return false;
        }

        var committedOsDisk = committed.OsDisks.FirstOrDefault(item =>
            item.VirtualDiskStableId == committedVdisk.StableId);
        var committedPartition = committed.Partitions
            .Where(item => item.OsDiskStableId == committedOsDisk?.StableId && item.Type == "Primary")
            .OrderBy(item => item.Offset)
            .FirstOrDefault();
        if (committedPartition is null)
        {
            return false;
        }

        var volumeName = _volumeNameBox.Text.Trim();
        var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        var cluster = ParseSize(_clusterBox.SelectedItem as string ?? "64K");
        var fsChanged = !string.Equals(
            fileSystem,
            committedPartition.FileSystem,
            StringComparison.OrdinalIgnoreCase);
        var clusterChanged = cluster != committedPartition.AllocationUnitSize;
        var labelChanged = !string.Equals(
            volumeName,
            committedPartition.FileSystemLabel,
            StringComparison.OrdinalIgnoreCase);
        if (!fsChanged && !clusterChanged && !labelChanged)
        {
            return false;
        }

        var holdsData = EditWorkspace.DiskHoldsStoredData(committed, committedVdisk.StableId, isVirtualDisk: true);
        if (holdsData && (fsChanged || clusterChanged))
        {
            await ShowMessageAsync(
                ViewModel.Localization["RebuildBlockedTitle"],
                ViewModel.Localization["RebuildBlockedMessage"]);
            return true;
        }

        if (fsChanged || clusterChanged)
        {
            // Reformatting also carries the volume label.
            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.FormatPartition,
                    committedPartition.StableId,
                    Name: string.IsNullOrWhiteSpace(volumeName) ? committedVdisk.FriendlyName : volumeName,
                    FileSystem: fileSystem,
                    AllocationUnitSize: cluster)) is null)
            {
                return true;
            }
        }
        else if (labelChanged)
        {
            if (await ApplyAsync(new SimulationOperationRequest(
                    SimulationOperationKind.Rename,
                    committedPartition.StableId,
                    Name: volumeName)) is null)
            {
                return true;
            }
        }

        return false;
    }

    private SimulationOperationRequest BuildPoolCreateRequest(
        SimulationOperationKind kind,
        StoragePoolInfo pool)
    {
        var virtualName = _virtualDiskNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(virtualName))
        {
            virtualName = pool.FriendlyName;
        }

        var memberIds = pool.MemberPhysicalDiskIds;
        return new SimulationOperationRequest(
            kind,
            "primordial",
            Name: pool.FriendlyName,
            FileSystem: _fileSystemBox.SelectedItem as string ?? "NTFS",
            AllocationUnitSize: ParseSize(_clusterBox.SelectedItem as string ?? "64K"),
            MemberDiskIds: memberIds,
            VirtualDiskName: virtualName,
            PerformanceResiliency: Performance.ResiliencyBox.SelectedItem as string,
            PerformanceInterleaveBytes: ParseSize(Performance.InterleaveBox.SelectedItem as string ?? "64K"),
            PerformanceSizeBytes: SizeBytes(Performance.SizeBox),
            PerformanceDataCopies: CopiesCount(Performance.CopiesBox),
            CapacityResiliency: Capacity.ResiliencyBox.SelectedItem as string,
            CapacityInterleaveBytes: ParseSize(Capacity.InterleaveBox.SelectedItem as string ?? "64K"),
            CapacitySizeBytes: SizeBytes(Capacity.SizeBox),
            CapacityColumns: ColumnsCount(Capacity.ColumnsBox),
            CapacityToleratedFailures: FailuresCount(Capacity.FailuresBox),
            ScmResiliency: Dedicated.ResiliencyBox.SelectedItem as string,
            ScmInterleaveBytes: ParseSize(Dedicated.InterleaveBox.SelectedItem as string ?? "64K"),
            ScmDataCopies: CopiesCount(Dedicated.CopiesBox),
            CreatePartition: _autoPartitionSwitch.IsOn,
            CreateVirtualDisk: _autoVdiskSwitch.IsOn);
    }

    /// <summary>
    /// Builds the property update request from the working-copy OBJECTS, not
    /// the form: Apply may commit property drafts of pools other than the
    /// currently selected one, whose form shows a different pool.
    /// </summary>
    private SimulationOperationRequest BuildPoolPropertyRequest(
        StoragePoolInfo pool,
        StorageSnapshot working)
    {
        var vdisk = working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && !EditWorkspace.IsDraftVirtualDisk(item.StableId));
        StorageTierInfo? TierOf(string media) => working.StorageTiers.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)
            && EditWorkspace.NormalizeMedia(item.MediaType) == media);
        var performance = TierOf("SSD");
        var capacity = TierOf("HDD");
        var dedicated = TierOf("SCM");
        return new SimulationOperationRequest(
            SimulationOperationKind.UpdateStoragePool,
            pool.StableId,
            Name: pool.FriendlyName,
            VirtualDiskName: vdisk?.FriendlyName,
            PerformanceResiliency: performance?.ResiliencySettingName,
            PerformanceInterleaveBytes: performance?.Interleave,
            PerformanceSizeBytes: performance is { Size: > 0 } ? performance.Size : null,
            PerformanceDataCopies: performance?.NumberOfDataCopies,
            CapacityResiliency: capacity?.ResiliencySettingName,
            CapacityInterleaveBytes: capacity?.Interleave,
            CapacitySizeBytes: capacity is { Size: > 0 } ? capacity.Size : null,
            CapacityColumns: capacity?.NumberOfColumns,
            CapacityToleratedFailures: capacity?.PhysicalDiskRedundancy,
            ScmResiliency: dedicated?.ResiliencySettingName,
            ScmInterleaveBytes: dedicated?.Interleave,
            ScmDataCopies: dedicated?.NumberOfDataCopies);
    }

    private async Task<bool> ApplyDraftVirtualDiskAsync(
        StorageSnapshot pending,
        VirtualDiskInfo draftVdisk)
    {
        var name = draftVdisk.FriendlyName;
        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.CreateVirtualDisk,
                draftVdisk.PoolStableId ?? string.Empty,
                Name: name,
                Resiliency: draftVdisk.ResiliencySettingName,
                InterleaveBytes: draftVdisk.Interleave,
                AllocationUnitSize: ParseSize(_clusterBox.SelectedItem as string ?? "64K"))) is null)
        {
            return false;
        }

        if (_autoPartitionSwitch.IsOn != true)
        {
            return true;
        }

        // Locate the fresh RAW OS disk of the pool's virtual disk.
        var committed = ViewModel.ActiveSnapshot;
        var vdisk = committed.VirtualDisks.LastOrDefault(item =>
            string.Equals(item.PoolStableId, draftVdisk.PoolStableId, StringComparison.OrdinalIgnoreCase)
            && item.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (vdisk is null)
        {
            return false;
        }

        var osDisk = committed.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        if (osDisk is null)
        {
            return false;
        }

        var style = _partitionStyleBox.SelectedItem as string ?? "GPT";
        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.InitializeDisk,
                osDisk.StableId,
                Name: style,
                CreateMsr: true)) is null)
        {
            return false;
        }

        if (await ApplyAsync(new SimulationOperationRequest(
                SimulationOperationKind.CreatePartition,
                osDisk.StableId)) is null)
        {
            return false;
        }

        var partition = ViewModel.ActiveSnapshot.Partitions
            .Where(item => item.OsDiskStableId == osDisk.StableId)
            .OrderBy(item => item.Offset)
            .LastOrDefault();
        if (partition is null)
        {
            return false;
        }

        return await ApplyAsync(new SimulationOperationRequest(
            SimulationOperationKind.FormatPartition,
            partition.StableId,
            Name: _volumeNameBox.Text.Trim(),
            FileSystem: _fileSystemBox.SelectedItem as string ?? "NTFS",
            AllocationUnitSize: ParseSize(_clusterBox.SelectedItem as string ?? "64K"))) is not null;
    }

    /// <summary>
    /// Save pool properties persists the property form immediately: pool and
    /// virtual-disk names, tier parameters, and the user-partition volume
    /// label / file system / cluster are written to the simulation document
    /// now. Structural draft steps survive the save (RestoreWorkingMembership
    /// replays them) but the undo history is checkpointed away, because the
    /// property values are no longer part of the draft. Apply-all covers the
    /// same property diff for users who skip Save.
    /// </summary>
    private async void SavePoolProperties_Click(object sender, RoutedEventArgs e)
    {
        var pool = SelectedPool();
        if (!ViewModel.IsUsingSimulatedInventory
            || pool is null
            || pool.IsPrimordial
            || EditWorkspace.IsDraftPool(pool.StableId))
        {
            return;
        }

        if (!_formDirty)
        {
            return;
        }

        var merged = ApplyFormToWorking(_working);
        var committed = ViewModel.ActiveSnapshot;
        var objectChanged = EditWorkspace.HasPoolPropertyChanges(merged, committed, pool.StableId);
        var tierChanged = objectChanged && RebuildsTierParameters(merged, committed, pool.StableId);
        var hasData = EditWorkspace.PoolHoldsStoredData(committed, pool.StableId);
        if (hasData && tierChanged)
        {
            await ShowMessageAsync(
                ViewModel.Localization["RebuildBlockedTitle"],
                ViewModel.Localization["RebuildBlockedMessage"]);
            return;
        }

        var partitionChanged = FormPartitionDiffers(committed, pool.StableId, out var reformatsPartition);
        if (hasData && partitionChanged && reformatsPartition)
        {
            await ShowMessageAsync(
                ViewModel.Localization["RebuildBlockedTitle"],
                ViewModel.Localization["RebuildBlockedMessage"]);
            return;
        }

        if (!objectChanged && !partitionChanged)
        {
            await ShowMessageAsync(
                Text("没有可保存的属性修改", "No pool properties to save"),
                Text(
                    "当前表单没有可保存的池属性修改。",
                    "The property form holds no changes to save."));
            _formDirty = true;
            return;
        }

        if (tierChanged || reformatsPartition)
        {
            if (!await ConfirmUnrecommendedAsync())
            {
                return;
            }
        }

        if (objectChanged)
        {
            if (await ApplyAsync(BuildPoolPropertyRequest(pool, merged)) is null)
            {
                return;
            }
        }

        if (partitionChanged)
        {
            if (await ApplyPartitionChangesAsync(merged, ViewModel.ActiveSnapshot, pool.StableId))
            {
                return;
            }
        }

        // Keep structural draft steps on the new committed snapshot; the
        // saved property values are checkpointed, so undo history ends here.
        _working = EditWorkspace.RestoreWorkingMembership(ViewModel.ActiveSnapshot, merged);
        _undoStack.Clear();
        _redoStack.Clear();
        _formDirty = false;
        RefreshAll();
    }

    private bool FormPartitionDiffers(
        StorageSnapshot committed,
        string poolId,
        out bool reformatsPartition)
    {
        reformatsPartition = false;
        var partition = CommittedPrimaryPartition(poolId);
        if (partition is null)
        {
            return false;
        }

        var volumeName = _volumeNameBox.Text.Trim();
        var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        var cluster = ParseSize(_clusterBox.SelectedItem as string ?? "64K");
        var fsChanged = !string.Equals(
            fileSystem,
            partition.FileSystem,
            StringComparison.OrdinalIgnoreCase);
        var clusterChanged = cluster != partition.AllocationUnitSize;
        reformatsPartition = fsChanged || clusterChanged;
        return reformatsPartition
            || !string.Equals(volumeName, partition.FileSystemLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameToken(ComboBox box, string value) =>
        string.Equals(box.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);

    private static string InterleaveToken(long? bytes) =>
        $"{Math.Max(1, (bytes ?? 65536) / 1024)}K";

    private static string ClusterToken(long? bytes) =>
        bytes switch
        {
            4096 => "4K",
            8192 => "8K",
            16384 => "16K",
            32768 => "32K",
            _ => "64K"
        };
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

    /// <summary>NumberBox stores its empty state as NaN, not null.</summary>
    private static double? NumValue(NumberBox box) =>
        double.IsNaN(box.Value) ? null : box.Value;

    private static void SetNum(NumberBox box, double? value) =>
        box.Value = value ?? double.NaN;

    private static bool NumberEquals(double? actual, int? expected) =>
        actual is null
            ? expected is null
            : expected is not null && Math.Abs(actual.Value - expected.Value) < 0.001;

    private TierFields? GroupFor(string media) =>
        TierGroups().FirstOrDefault(group => group.Media == media);

    private void UpdateFieldResets()
    {
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
        return tier is null
            ? 0
            : _working.PhysicalDisks
                .Where(disk => tier.MemberPhysicalDiskIds.Contains(
                    disk.StableId, StringComparer.OrdinalIgnoreCase))
                .Sum(disk => disk.Size);
    }

    private int TierDataDiskCount(string media)
    {
        var pool = SelectedPool();
        return pool is null || pool.IsPrimordial
            ? 0
            : TierMap(pool.StableId).GetValueOrDefault(media)?.MemberPhysicalDiskIds.Count ?? 0;
    }

    private IReadOnlyList<PhysicalDiskInfo> TierDataDisks(string media)
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return [];
        }

        var tier = TierMap(pool.StableId).GetValueOrDefault(media);
        return tier is null
            ? []
            : _working.PhysicalDisks
                .Where(disk => tier.MemberPhysicalDiskIds.Contains(
                    disk.StableId, StringComparer.OrdinalIgnoreCase))
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

        return field switch
        {
            TierFieldKind.Size => Math.Abs(((NumValue(group.SizeBox) ?? 0) * 1024d * 1024d * 1024d) - tier.Size) > 1,
            TierFieldKind.Resiliency => !SameToken(group.ResiliencyBox, tier.ResiliencySettingName),
            TierFieldKind.Copies => !NumberEquals(NumValue(group.CopiesBox), tier.NumberOfDataCopies),
            TierFieldKind.Failures => !NumberEquals(NumValue(group.FailuresBox), tier.PhysicalDiskRedundancy),
            TierFieldKind.Columns => !NumberEquals(NumValue(group.ColumnsBox), tier.NumberOfColumns),
            TierFieldKind.Stripe => InterleaveToken(tier.Interleave) != (group.InterleaveBox.SelectedItem as string),
            _ => false
        };
    }

    /// <summary>Restores one parameter field to its recommended value.</summary>
    private void ResetTierField(TierFields group, string kind)
    {
        var media = group.Media;
        var count = TierDataDiskCount(media);
        switch (kind)
        {
            case "Size":
                var max = TierCapacityMaxBytes(media);
                SetNum(group.SizeBox, max > 0 ? Math.Round(max / 1024d / 1024d / 1024d, 2) : 0);
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
                // Mirror and Simple keep an automatic, read-only column
                // count; only Parity exposes an editable column number.
                var columnResiliency = group.ResiliencyBox.SelectedItem as string ?? "Simple";
                if (!columnResiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase)
                    && !columnResiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase))
                {
                    SetNum(group.ColumnsBox, EditWorkspace.RecommendedCapacityColumns(TierDataDisks(media)));
                }

                break;
            case "Stripe":
                group.InterleaveBox.SelectedItem = "64K";
                break;
        }
    }

    private static long? SizeBytes(NumberBox box) =>
        NumValue(box) is { } gb ? (long)(gb * 1024L * 1024L * 1024L) : null;

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

    private bool PoolNameChanged()
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        return !string.Equals(
            _poolNameBox.Text.Trim(),
            pool.FriendlyName,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool VirtualDiskNameChanged()
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var vdisk = RealVdiskOf(_working, pool.StableId);
        var baseline = vdisk?.FriendlyName ?? pool.FriendlyName;
        return !string.Equals(
            _virtualDiskNameBox.Text.Trim(),
            baseline,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool VolumeNameChanged()
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var partition = PrimaryPartition(pool.StableId);
        var vdisk = RealVdiskOf(_working, pool.StableId);
        var baseline = partition is not null
            ? partition.FileSystemLabel
            : vdisk?.FriendlyName ?? pool.FriendlyName;
        return !string.Equals(
            _volumeNameBox.Text.Trim(),
            baseline,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool PartitionStyleChanged()
    {
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var vdisk = RealVdiskOf(_working, pool.StableId);
        if (vdisk is null)
        {
            return false;
        }

        var osDisk = _working.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
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
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var partition = PrimaryPartition(pool.StableId);
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
        var pool = SelectedPool();
        if (pool is null || pool.IsPrimordial)
        {
            return false;
        }

        var partition = PrimaryPartition(pool.StableId);
        var baseline = partition?.AllocationUnitSize is { } bytes
            ? ClusterToken(bytes)
            : "64K";
        return !string.Equals(
            _clusterBox.SelectedItem as string,
            baseline,
            StringComparison.OrdinalIgnoreCase);
    }

    private void ResetPartitionField(string kind)
    {
        switch (kind)
        {
            case "PartitionStyle":
                if (_partitionStyleBox.IsEnabled)
                {
                    _partitionStyleBox.SelectedItem = "GPT";
                }

                break;
            case "FileSystem":
                if (_fileSystemBox.IsEnabled)
                {
                    _fileSystemBox.SelectedItem = "NTFS";
                }

                break;
            case "AllocationUnit":
                if (_clusterBox.IsEnabled)
                {
                    _clusterBox.SelectedItem = "64K";
                }

                break;
        }
    }

    private void SyncAutoVirtualDiskPlaceholder()
    {
        var pool = SelectedPool();
        if (pool is null
            || !EditWorkspace.IsDraftPool(pool.StableId)
            || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var poolId = pool.StableId;
        var placeholder = _working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && EditWorkspace.IsDraftVirtualDisk(item.StableId));
        try
        {
            if (_autoVdiskSwitch.IsOn && placeholder is null)
            {
                var next = EditWorkspace.InsertDraftVirtualDisk(
                    _working,
                    poolId,
                    ViewModel.Localization["NotCreatedVirtualDisk"],
                    "Simple",
                    65536);
                CommitWorkingStep(next);
            }
            else if (!_autoVdiskSwitch.IsOn && placeholder is not null)
            {
                var next = EditWorkspace.DeleteVirtualDiskFromWorking(_working, placeholder.StableId);
                CommitWorkingStep(next);
            }
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

}