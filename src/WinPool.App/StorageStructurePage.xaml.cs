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
/// Storage structure editor: left pool topology, bottom-left wrapping
/// structure operations, and a right-hand property card that sizes to its
/// grouped pool / tier / disk-and-partition fields plus one Save row.
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
    private readonly Stack<EditorDraftState> _undoStack = [];
    private readonly Stack<EditorDraftState> _redoStack = [];
    private readonly HashSet<string> _maximumSizeFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PoolEditIntent> _poolIntents = new(StringComparer.OrdinalIgnoreCase);
    private SimulationDraftPlan? _currentPlan;
    private string _planBuildError = string.Empty;
    private bool _outcomeUnknown;
    private bool _renameInProgress;

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
        List<int> RowIndices);

    private sealed record PoolEditIntent(
        bool AutoCreateVirtualDisk,
        bool AutoCreatePartition,
        string FileSystem,
        long AllocationUnitSize,
        string VolumeName);

    private sealed record EditorDraftState(
        StorageSnapshot Snapshot,
        IReadOnlyDictionary<string, PoolEditIntent> PoolIntents,
        IReadOnlySet<string> MaximumSizeFields);

    /// <summary>Reset affordances shown only while a field differs from the
    /// committed state.</summary>
    private sealed record FieldReset(
        Button ResetButton,
        FrameworkElement ChangeIndicator,
        Func<bool> IsChanged);

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

    private static NumberBox CreateNumberField(double minimum = double.NaN, double maximum = double.NaN)
    {
        var box = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            SmallChange = 1,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
        };
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

    private static TierFields CreateTierFields(string media, string titleKey) =>
        new(
            media,
            titleKey,
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch },
            new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "GiB" },
            new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(6),
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

        _working = EditWorkspace.NormalizeTierCapacities(ViewModel.ActiveSnapshot);
        _undoStack.Clear();
        _redoStack.Clear();
        _poolIntents.Clear();
        _maximumSizeFields.Clear();
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
        PendingActionsTitle.Text = ViewModel.Localization["PendingActions"];
        PendingActionsEmptyText.Text = ViewModel.Localization["NoPendingActions"];
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
        FillCombo(_clusterBox, ["4 KiB", "8 KiB", "16 KiB", "32 KiB", "64 KiB"], 4);
        foreach (var group in TierGroups())
        {
            FillCombo(group.ResiliencyBox, ["Simple", "Mirror", "Parity"], 1);
            FillCombo(group.InterleaveBox, ["16 KiB", "32 KiB", "64 KiB", "128 KiB", "256 KiB"], 2);
            FillCombo(group.ProvisioningBox, ["Fixed"], 0);
            ToolTipService.SetToolTip(group.MaximumButton, ViewModel.Localization["UseMaximumSize"]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                group.MaximumButton,
                ViewModel.Localization["UseMaximumSize"]);
            group.MaximumButton.Click += (_, _) => ToggleMaximumSize(group);
        }

        foreach (var group in TierGroups())
        {
            HookFormField(group.ResiliencyBox, isCombo: true);
            HookFormField(group.InterleaveBox, isCombo: true);
            HookFormField(group.ProvisioningBox, isCombo: true);
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
        HookNameField(_poolNameBox);
        HookNameField(_virtualDiskNameBox);
        HookNameField(_volumeNameBox);
        _autoVdiskSwitch.Toggled += (_, _) => CommitAutoCreateToggle(virtualDisk: true);
        _autoPartitionSwitch.Toggled += (_, _) => CommitAutoCreateToggle(virtualDisk: false);

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

    private void HookNameField(TextBox box)
    {
        box.TextChanged += (_, _) =>
        {
            if (_filling)
            {
                return;
            }

            var pool = SelectedPool();
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
                    if (!_filling)
                    {
                        var sizeGroup = TierGroups().FirstOrDefault(group => ReferenceEquals(group.SizeBox, box));
                        if (sizeGroup is not null)
                        {
                            var maximumKey = MaximumKey(sizeGroup);
                            if (_maximumSizeFields.Contains(maximumKey))
                            {
                                CaptureSelectedIntent();
                                _undoStack.Push(CaptureDraftState());
                                _redoStack.Clear();
                            }
                            _maximumSizeFields.Remove(maximumKey);
                        }
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

                NormalizeTierNumber(number);
            }

            Focus(FocusState.Programmatic);
            UpdateButtonState();
        };
    }

    private async Task CommitNameAsync(TextBox field)
    {
        if (_filling || _renameInProgress || !ViewModel.IsUsingSimulatedInventory)
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
            var volume = partition is null ? null : ViewModel.ActiveSnapshot.VolumeForPartition(partition.StableId);
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

        _renameInProgress = true;
        try
        {
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
                await ShowMessageAsync(Text("改名失败", "Rename failed"), exception.Message);
                return;
            }

            if (!result.IsSuccess || result.Value is null)
            {
                await ShowMessageAsync(
                    result.Status == ApplicationStatus.OutcomeUnknown
                        ? Text("提交结果未知", "Commit outcome unknown")
                        : Text("改名失败", "Rename failed"),
                    result.Messages.FirstOrDefault()?.UserTextKey
                        ?? Text("名称未保存，可以修正后重试。", "The name was not saved. Correct it and try again."));
                return;
            }

            SynchronizeCommittedName(targetId);
            _currentPlan = null;
            RefreshTopology();
            UpdateButtonState();
        }
        finally
        {
            _renameInProgress = false;
        }
    }

    private void SynchronizeCommittedName(string targetId)
    {
        var committed = ViewModel.ActiveSnapshot;
        _working = SynchronizeCommittedName(_working, committed, targetId);
        SynchronizeHistoryNames(_undoStack, committed, targetId);
        SynchronizeHistoryNames(_redoStack, committed, targetId);
    }

    private static void SynchronizeHistoryNames(
        Stack<EditorDraftState> stack,
        StorageSnapshot committed,
        string targetId)
    {
        var states = stack.ToArray();
        stack.Clear();
        for (var index = states.Length - 1; index >= 0; index--)
        {
            stack.Push(states[index] with
            {
                Snapshot = SynchronizeCommittedName(states[index].Snapshot, committed, targetId)
            });
        }
    }

    private static StorageSnapshot SynchronizeCommittedName(
        StorageSnapshot snapshot,
        StorageSnapshot committed,
        string targetId)
    {
        var pool = committed.StoragePools.FirstOrDefault(item => item.StableId == targetId);
        var tier = committed.StorageTiers.FirstOrDefault(item => item.StableId == targetId);
        var physical = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == targetId);
        var vdisk = committed.VirtualDisks.FirstOrDefault(item => item.StableId == targetId);
        var osDisk = committed.OsDisks.FirstOrDefault(item => item.StableId == targetId);
        var projectedOsDisks = vdisk is null
            ? osDisk is null ? snapshot.OsDisks : snapshot.OsDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = osDisk.FriendlyName } : item).ToArray()
            : snapshot.OsDisks
                .Select(item => item.VirtualDiskStableId == targetId
                    ? item with { FriendlyName = vdisk.FriendlyName }
                    : item).ToArray();
        var volume = committed.Volumes.FirstOrDefault(item => item.StableId == targetId);
        var partition = committed.Partitions.FirstOrDefault(item => item.StableId == targetId)
            ?? (volume?.PartitionStableId is string partitionId
                ? committed.Partitions.FirstOrDefault(item => item.StableId == partitionId)
                : null);
        return snapshot with
        {
            StoragePools = pool is null ? snapshot.StoragePools : snapshot.StoragePools
                .Select(item => item.StableId == targetId ? item with { FriendlyName = pool.FriendlyName } : item).ToArray(),
            StorageTiers = tier is null ? snapshot.StorageTiers : snapshot.StorageTiers
                .Select(item => item.StableId == targetId ? item with { FriendlyName = tier.FriendlyName } : item).ToArray(),
            PhysicalDisks = physical is null ? snapshot.PhysicalDisks : snapshot.PhysicalDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = physical.FriendlyName } : item).ToArray(),
            VirtualDisks = vdisk is null ? snapshot.VirtualDisks : snapshot.VirtualDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = vdisk.FriendlyName } : item).ToArray(),
            OsDisks = projectedOsDisks,
            Partitions = partition is null ? snapshot.Partitions : snapshot.Partitions
                .Select(item => item.StableId == partition.StableId
                    ? item with { FileSystemLabel = partition.FileSystemLabel }
                    : item).ToArray(),
            Volumes = volume is null ? snapshot.Volumes : snapshot.Volumes
                .Select(item => item.StableId == targetId ? item with { FileSystemLabel = volume.FileSystemLabel } : item).ToArray()
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
        var sizePanel = new Grid { ColumnSpacing = 6 };
        sizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(group.MaximumButton, 1);
        sizePanel.Children.Add(group.SizeBox);
        sizePanel.Children.Add(group.MaximumButton);
        row = AddTierRow(row, group, "TierSize", sizePanel, () => ResetTierField(group, "Size"));
        row = AddTierRow(row, group, "TierProvisioning", group.ProvisioningBox, null);
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
        Action? reset)
    {
        group.RowIndices.Add(row);
        return AddFormRow(row, key, value, reset, TierRowChanged(group, key), group.Rows);
    }

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
                Margin = new Thickness(0, 10, 0, 2),
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
            Margin = new Thickness(0, first ? 0 : 4, 0, 8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
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
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = ViewModel.Localization[key]
        };
        var indicator = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed
        };
        var labelPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelPanel.Children.Add(label);
        labelPanel.Children.Add(indicator);
        value.VerticalAlignment = VerticalAlignment.Center;
        if (value is not ToggleSwitch)
        {
            value.Height = 32;
        }

        Grid.SetRow(labelPanel, row);
        Grid.SetColumn(labelPanel, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        PoolFormGrid.Children.Add(labelPanel);
        PoolFormGrid.Children.Add(value);
        visibilityGroup?.Add(labelPanel);
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
        _fieldResets.Add(new FieldReset(button, indicator, changed ?? (() => false)));
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
            _undoStack.Push(priorState!);
            _redoStack.Clear();
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

                // Fixed 40px rows keep their height when hidden; zero them so
                // a hidden tier never leaves a tall blank block in the form.
                foreach (var index in group.RowIndices)
                {
                    if (index < PoolFormGrid.RowDefinitions.Count)
                    {
                        PoolFormGrid.RowDefinitions[index].Height =
                            new GridLength(visible ? 40 : 0);
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

                if (partition.AllocationUnitSize is long cluster)
                {
                    _clusterBox.SelectedItem = ClusterToken(cluster);
                }
            }

            if (_poolIntents.TryGetValue(pool.StableId, out var intent))
            {
                _autoVdiskSwitch.IsOn = intent.AutoCreateVirtualDisk;
                _autoPartitionSwitch.IsOn = intent.AutoCreatePartition;
                if (_fileSystemBox.Items.Contains(intent.FileSystem))
                {
                    _fileSystemBox.SelectedItem = intent.FileSystem;
                }
                _clusterBox.SelectedItem = ClusterToken(intent.AllocationUnitSize);
                if (CommittedPrimaryPartition(pool.StableId) is null)
                {
                    _volumeNameBox.Text = intent.VolumeName;
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
        _clusterBox.SelectedItem = "64 KiB";
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
            group.ProvisioningBox.SelectedItem = "Fixed";
            UpdateMaximumSizeText(group);
            return;
        }

        SetResiliency(group.ResiliencyBox, tier.ResiliencySettingName);
        SetInterleave(group.InterleaveBox, tier.Interleave ?? 65536);
        var tierBytes = tier.Size > 0 ? tier.Size : TierCapacityMaxBytes(group.Media);
        SetNum(group.SizeBox, tierBytes > 0 ? Math.Round(tierBytes / 1024d / 1024d / 1024d, 2) : null);
        SetNum(group.ColumnsBox, tier.NumberOfColumns);
        SetNum(group.CopiesBox, tier.NumberOfDataCopies ?? 1);
        SetNum(group.FailuresBox, tier.PhysicalDiskRedundancy ?? 1);
        group.DiskCountBox.Text = tier.MemberPhysicalDiskIds.Count.ToString();
        group.ProvisioningBox.SelectedItem = "Fixed";
        UpdateMaximumSizeText(group);
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
        || HasStoredPartitionIntentChanges();

    private bool HasStoredPartitionIntentChanges()
    {
        foreach (var pair in _poolIntents)
        {
            if (!_working.StoragePools.Any(item => item.StableId == pair.Key)
                || PrimaryPartition(pair.Key) is not { } finalPartition
                || CommittedPrimaryPartition(pair.Key) is not { } committedPartition
                || finalPartition.StableId != committedPartition.StableId)
            {
                continue;
            }

            if (!string.Equals(pair.Value.FileSystem, committedPartition.FileSystem, StringComparison.OrdinalIgnoreCase)
                || pair.Value.AllocationUnitSize != committedPartition.AllocationUnitSize)
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshPendingActions()
    {
        PendingActionsPanel.Children.Clear();
        _planBuildError = string.Empty;
        try
        {
            var projected = _formDirty ? ApplyFormToWorking(_working) : _working;
            var built = SimulationDraftPlanner.Build(ViewModel.ActiveSnapshot, projected);
            var enriched = built.Steps.Select(EnrichPlanIntent).ToList();
            AppendPartitionIntents(enriched);
            _currentPlan = SimulationDraftPlanner.Precheck(ViewModel.ActiveSnapshot, enriched);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            _currentPlan = null;
            _planBuildError = exception.Message;
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

        foreach (var item in _currentPlan.DisplayItems.Where(item => item.ParentId is null))
        {
            var children = _currentPlan.DisplayItems.Where(candidate => candidate.ParentId == item.Id).ToArray();
            if (children.Length == 0)
            {
                PendingActionsPanel.Children.Add(ActionText(item));
                continue;
            }

            var childPanel = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4, 0, 0) };
            foreach (var child in children)
            {
                childPanel.Children.Add(ActionText(child));
            }

            PendingActionsPanel.Children.Add(new Expander
            {
                Header = ActionText(item),
                Content = childPanel,
                IsExpanded = false
            });
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

        return step with
        {
            FileSystem = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.FileSystem ?? (selected ? _fileSystemBox.SelectedItem as string : null) ?? step.FileSystem
                : step.FileSystem,
            AllocationUnitSize = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.AllocationUnitSize
                    ?? (selected ? ParseSize(_clusterBox.SelectedItem as string ?? "64 KiB") : (long?)null)
                    ?? step.AllocationUnitSize
                : step.AllocationUnitSize,
            VolumeName = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.VolumeName ?? (selected ? _volumeNameBox.Text.Trim() : null) ?? step.VolumeName
                : step.VolumeName,
            CreateVirtualDisk = step.Kind == SimulationEditKind.CreateTieredPool
                ? intent?.AutoCreateVirtualDisk ?? step.CreateVirtualDisk
                : step.CreateVirtualDisk,
            CreatePartition = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                ? intent?.AutoCreatePartition ?? step.CreatePartition
                : step.CreatePartition,
            AllocatedPartitionId = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                && intent?.AutoCreatePartition == true
                ? step.AllocatedPartitionId ?? StableIntentObjectId("sim:partition", intentKey)
                : step.AllocatedPartitionId,
            AllocatedVolumeId = step.Kind is SimulationEditKind.CreateTieredPool or SimulationEditKind.CreateVirtualDisk
                && intent?.AutoCreatePartition == true
                ? step.AllocatedVolumeId ?? StableIntentObjectId("sim:volume", intentKey)
                : step.AllocatedVolumeId,
            PerformanceUseMaximum = intentKey is not null && _maximumSizeFields.Contains($"{intentKey}|SSD"),
            CapacityUseMaximum = intentKey is not null && _maximumSizeFields.Contains($"{intentKey}|HDD"),
            ScmUseMaximum = intentKey is not null && _maximumSizeFields.Contains($"{intentKey}|SCM"),
            ProvisioningType = "Fixed"
        };
    }

    private static string StableIntentObjectId(string prefix, string? intentKey)
    {
        var source = string.IsNullOrWhiteSpace(intentKey) ? "pending" : intentKey;
        var suffix = source[(source.LastIndexOf(':') + 1)..];
        return $"{prefix}:{suffix}";
    }

    private void CaptureSelectedIntent()
    {
        if (_filling || string.IsNullOrWhiteSpace(_selectedPoolId))
        {
            return;
        }

        var savedPartition = CommittedPrimaryPartition(_selectedPoolId);
        _poolIntents[_selectedPoolId] = new PoolEditIntent(
            _autoVdiskSwitch.IsOn,
            _autoPartitionSwitch.IsOn,
            _fileSystemBox.SelectedItem as string ?? "NTFS",
            ParseSize(_clusterBox.SelectedItem as string ?? "64 KiB"),
            savedPartition is null ? _volumeNameBox.Text.Trim() : savedPartition.FileSystemLabel);
    }

    private EditorDraftState CaptureDraftState() => new(
        _working,
        new Dictionary<string, PoolEditIntent>(_poolIntents, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_maximumSizeFields, StringComparer.OrdinalIgnoreCase));

    private void RestoreDraftState(EditorDraftState state)
    {
        _working = state.Snapshot;
        _poolIntents.Clear();
        foreach (var pair in state.PoolIntents)
        {
            _poolIntents[pair.Key] = pair.Value;
        }
        _maximumSizeFields.Clear();
        _maximumSizeFields.UnionWith(state.MaximumSizeFields);
        _formDirty = false;
    }

    private void AppendPartitionIntents(List<SimulationEditRequest> steps)
    {
        foreach (var pair in _poolIntents)
        {
            AppendPartitionIntent(steps, pair.Key, pair.Value);
        }
    }

    private void AppendPartitionIntent(
        List<SimulationEditRequest> steps,
        string poolId,
        PoolEditIntent intent)
    {
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

    private TextBlock ActionText(SimulationPlanItem item)
    {
        var title = ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn
            ? $"{DescribePlanStep(item.Request)} {item.Title}"
            : item.Title;
        var reason = item.Decision?.Verdict == StorageRuleVerdict.Allow
            ? string.Empty
            : item.Decision?.Message ?? string.Empty;
        var dataLoss = item.CausesDataLoss
            ? Text("\n会删除已使用的数据。", "\nDeletes used data.")
            : string.Empty;
        var max = item.Request.PerformanceUseMaximum
            || item.Request.CapacityUseMaximum
            || item.Request.ScmUseMaximum
                ? " · MAX"
                : string.Empty;
        var unknown = _outcomeUnknown
            ? Text(" · 结果待确认", " · outcome pending confirmation")
            : string.Empty;
        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reason)
                ? $"{title}{max}{unknown}{dataLoss}"
                : $"{title}{max}{unknown}{dataLoss}\n{reason}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = item.Decision?.Verdict == StorageRuleVerdict.Allow && !item.CausesDataLoss
                ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
    }

    private bool HasInvalidSizeInput()
    {
        var pool = SelectedPool();
        return pool is not null && TierGroups().Any(group =>
            TierVisible(pool.StableId, group.Media)
            && group.SizeBox.IsEnabled
            && !_maximumSizeFields.Contains(MaximumKey(group))
            && NumValue(group.SizeBox) is null);
    }

    private void UpdateButtonState()
    {
        RefreshPendingActions();
        var simulated = ViewModel.IsUsingSimulatedInventory;
        var pool = SelectedPool();
        var hasUnapplied = HasUncommittedChanges();

        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
        DiscardAllButton.IsEnabled = hasUnapplied;
        var planBlocked = _currentPlan?.DisplayItems.Any(item =>
            item.Decision?.Verdict != StorageRuleVerdict.Allow) == true;
        ApplyAllButton.IsEnabled = simulated
            && hasUnapplied
            && _currentPlan is not null
            && string.IsNullOrWhiteSpace(_planBuildError)
            && !planBlocked
            && !HasInvalidSizeInput()
            && !_outcomeUnknown;
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
        ToolTipService.SetToolTip(
            _poolNameBox,
            isDraft
                ? Text("名称将在创建时生效。", "The name takes effect when the object is created.")
                : Text("按 Enter 保存名称。", "Press Enter to save the name."));
        _poolNameBox.IsEnabled = formEnabled;
        _virtualDiskNameBox.IsEnabled = formEnabled;
        var volumePartition = pool is not null && vdisk is not null
            ? PrimaryPartition(pool.StableId)
            : null;
        var virtualDiskIsDraft = vdisk is null || EditWorkspace.IsDraftVirtualDisk(vdisk.StableId);
        ToolTipService.SetToolTip(
            _virtualDiskNameBox,
            virtualDiskIsDraft
                ? Text("名称将在创建时生效。", "The name takes effect when the object is created.")
                : Text("按 Enter 保存名称。", "Press Enter to save the name."));
        ToolTipService.SetToolTip(
            _volumeNameBox,
            volumePartition is null
                ? Text("卷标将在创建时生效。", "The volume label takes effect when the volume is created.")
                : Text("按 Enter 保存卷标。", "Press Enter to save the volume label."));
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
            var sizeEditable = tierVisible && tier is not null && !holdsData;
            var specEditable = sizeEditable;
            group.SizeBox.IsEnabled = sizeEditable;
            group.MaximumButton.IsEnabled = sizeEditable && TierCapacityMaxBytes(group.Media) > 0;
            var provisioning = string.IsNullOrWhiteSpace(vdisk?.ProvisioningType)
                ? "Fixed"
                : vdisk.ProvisioningType;
            if (!group.ProvisioningBox.Items.Contains(provisioning))
            {
                group.ProvisioningBox.Items.Add(provisioning);
            }
            group.ProvisioningBox.SelectedItem = provisioning;
            group.ProvisioningBox.IsEnabled = sizeEditable
                && provisioning.Equals("Fixed", StringComparison.OrdinalIgnoreCase);
            group.ResiliencyBox.IsEnabled = specEditable;
            var supportedInterleave = tier?.Interleave is null
                || tier.Interleave is 16384 or 32768 or 65536 or 131072 or 262144;
            group.InterleaveBox.IsEnabled = specEditable && supportedInterleave;
            ToolTipService.SetToolTip(
                group.InterleaveBox,
                supportedInterleave
                    ? null
                    : Text("当前 Interleave 值超出编辑范围，已按原值保留。", "The current interleave is outside the editable range and is preserved."));
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
            UpdateMaximumSizeText(group);
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

        CaptureSelectedIntent();
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

        _undoStack.Push(CaptureDraftState());
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

        CaptureSelectedIntent();
        if (_formDirty)
        {
            _working = ApplyFormToWorking(_working);
        }
        _redoStack.Push(CaptureDraftState());
        RestoreDraftState(_undoStack.Pop());
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

        _undoStack.Push(CaptureDraftState());
        RestoreDraftState(_redoStack.Pop());
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
        _maximumSizeFields.Clear();
        _poolIntents.Clear();
        _outcomeUnknown = false;
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
            var next = EditWorkspace.InsertDraftVirtualDisk(_working, pool.StableId, name, resiliency, interleave);
            _selectedPoolVdiskId = next.VirtualDisks.Last(item =>
                string.Equals(item.PoolStableId, pool.StableId, StringComparison.OrdinalIgnoreCase)).StableId;
            _selectedPoolDiskId = null;
            _undoStack.Push(priorState);
            _redoStack.Clear();
            _working = next;
            _formDirty = false;
            RefreshAll();
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

        var planned = _currentPlan ?? SimulationDraftPlanner.Build(ViewModel.ActiveSnapshot, pending);
        var blockedItem = planned.DisplayItems.FirstOrDefault(item =>
            item.Decision?.Verdict != StorageRuleVerdict.Allow);
        if (blockedItem is not null)
        {
            await ShowMessageAsync(
                Text("计划被规则阻止", "Plan blocked by rules"),
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

        var plan = planned;
        if (plan.IsEmpty)
        {
            await ShowMessageAsync(
                ViewModel.Localization["NoApplyChangesTitle"],
                ViewModel.Localization["NoApplyChangesMessage"]);
            return;
        }

        var applied = await ViewModel.ApplySimulationPlanAsync(plan);
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
        _working = ViewModel.ActiveSnapshot;
        _selectedPoolId = EditWorkspace.IsDraftPool(_selectedPoolId ?? string.Empty)
            ? _working.StoragePools.LastOrDefault(item => !item.IsPrimordial)?.StableId
            : _selectedPoolId;
        NormalizeSelection();
        ResetLayerSwitchesForSelection();
        _formDirty = false;
        RefreshAll();
    }

    private async Task HandleFailedApplyAsync(
        ApplicationResult<SimulationEditReceipt> applied,
        StorageSnapshot pending)
    {
        var detail = applied.Messages.FirstOrDefault()?.UserTextKey;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = applied.Status.ToString();
        }

        if (applied.Status == ApplicationStatus.OutcomeUnknown)
        {
            await ShowMessageAsync(
                Text("提交结果未知", "Commit outcome unknown"),
                detail);
            _outcomeUnknown = true;
            _working = pending;
            _formDirty = false;
            RefreshAll();
            return;
        }

        await ShowMessageAsync(Text("操作不可用", "Operation unavailable"), detail);
        if (!ReferenceEquals(pending, _working))
        {
            _working = pending;
            _formDirty = false;
        }

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
            var interleave = ParseSize(group.InterleaveBox.SelectedItem as string ?? "64 KiB");
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
                var parity = resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1, failures)
                    : 0;
                var footprint = size is null
                    ? tier.FootprintOnPool
                    : ConservativeCapacity.PhysicalFootprintForLogical(
                        size.Value, resiliency, copies, columns, parity);
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
            var finalSize = result.StorageTiers.Where(item => item.PoolStableId == pool.StableId).Sum(item => item.Size);
            var finalFootprint = result.StorageTiers.Where(item => item.PoolStableId == pool.StableId).Sum(item => item.FootprintOnPool);
            result = result with
            {
                VirtualDisks = result.VirtualDisks.Select(item => item.StableId == draftVdisk.StableId
                    ? item with { Size = finalSize, FootprintOnPool = finalFootprint }
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
                ? $"设置磁盘用途为 {step.Name}。"
                : $"Set disk usage to {step.Name}.",
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
        && left.NumberOfColumns == right.NumberOfColumns;

    private async Task<bool> ConfirmUnrecommendedAsync()
    {
        if (new[] { Performance, Capacity, Dedicated }
                .Any(group => (group.InterleaveBox.SelectedItem as string) == "256 KiB")
            && !await ConfirmAsync(
                Text("256 KiB 交织警告", "256 KiB interleave warning"),
                Text(
                    "256 KiB 交织不在当前测试推荐中。当前测试推荐为 64 KiB 交织 + 64 KiB NTFS 簇。确定继续？",
                    "256 KiB interleave is outside the tested recommendation (64 KiB interleave + 64 KiB NTFS cluster). Continue anyway?")))
        {
            return false;
        }

        if (string.Equals(_fileSystemBox.SelectedItem as string, "ReFS", StringComparison.OrdinalIgnoreCase)
            && !await ConfirmAsync(
                Text("ReFS 提示", "ReFS notice"),
                Text(
                    "ReFS 没有与 NTFS 64 KiB 同等的长期测试证据。确定继续？",
                    "ReFS has no long-run evidence equivalent to NTFS 64 KiB. Continue anyway?")))
        {
            return false;
        }

        return true;
    }

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

        var priorState = CaptureDraftState();
        CaptureSelectedIntent();
        var merged = EditWorkspace.NormalizeTierCapacities(ApplyFormToWorking(_working));
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

        _undoStack.Push(priorState);
        _redoStack.Clear();
        _working = merged;
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

        var fileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        var cluster = ParseSize(_clusterBox.SelectedItem as string ?? "64 KiB");
        var fsChanged = !string.Equals(
            fileSystem,
            partition.FileSystem,
            StringComparison.OrdinalIgnoreCase);
        var clusterChanged = cluster != partition.AllocationUnitSize;
        reformatsPartition = fsChanged || clusterChanged;
        return reformatsPartition;
    }

    private static bool SameToken(ComboBox box, string value) =>
        string.Equals(box.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);

    private static string InterleaveToken(long? bytes) =>
        $"{Math.Max(1, (bytes ?? 65536) / 1024)} KiB";

    private static string ClusterToken(long? bytes) =>
        bytes switch
        {
            4096 => "4 KiB",
            8192 => "8 KiB",
            16384 => "16 KiB",
            32768 => "32 KiB",
            _ => "64 KiB"
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
            return TierCapacityMaxBytes(group.Media) / 1024d / 1024d / 1024d;
        }

        return double.TryParse(
            box.Text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture,
            out var value)
            && double.IsFinite(value)
            && value >= 0
                ? value
                : null;
    }

    private static void SetNum(NumberBox box, double? value) =>
        box.Value = value ?? double.NaN;

    private static void SetNum(TextBox box, double? value) =>
        box.Text = value?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    private string MaximumKey(TierFields group) => $"{_selectedPoolId}|{group.Media}";

    private void ToggleMaximumSize(TierFields group)
    {
        MergeFormIntoWorking();
        CaptureSelectedIntent();
        _undoStack.Push(CaptureDraftState());
        _redoStack.Clear();
        var key = MaximumKey(group);
        var maximum = TierCapacityMaxBytes(group.Media);
        if (_maximumSizeFields.Remove(key))
        {
            _filling = true;
            SetNum(group.SizeBox, maximum / 1024d / 1024d / 1024d);
            _filling = false;
        }
        else if (maximum > 0)
        {
            _maximumSizeFields.Add(key);
            UpdateMaximumSizeText(group);
        }

        _formDirty = true;
        UpdateButtonState();
    }

    private void UpdateMaximumSizeText(TierFields group)
    {
        if (!_maximumSizeFields.Contains(MaximumKey(group)))
        {
            return;
        }

        var maximumGiB = TierCapacityMaxBytes(group.Media) / 1024L / 1024L / 1024L;
        _filling = true;
        group.SizeBox.Text = $"MAX({maximumGiB}GiB)";
        _filling = false;
    }

    private static bool NumberEquals(double? actual, int? expected) =>
        expected is not null
        && actual is not null
        && Math.Abs(actual.Value - expected.Value) < 0.001;

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

        var members = _working.PhysicalDisks
            .Where(disk => tier.MemberPhysicalDiskIds.Contains(
                    disk.StableId, StringComparer.OrdinalIgnoreCase)
                && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage))
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
            TierFieldKind.Copies => group.CopiesBox.IsEnabled
                && !NumberEquals(NumValue(group.CopiesBox), tier.NumberOfDataCopies),
            TierFieldKind.Failures => group.FailuresBox.IsEnabled
                && !NumberEquals(NumValue(group.FailuresBox), tier.PhysicalDiskRedundancy),
            TierFieldKind.Columns => group.ColumnsBox.IsEnabled
                && !NumberEquals(NumValue(group.ColumnsBox), tier.NumberOfColumns),
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
                group.InterleaveBox.SelectedItem = "64 KiB";
                break;
        }
    }

    private long? SizeBytes(TextBox box) =>
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
            : "64 KiB";
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
                    _clusterBox.SelectedItem = "64 KiB";
                }

                break;
        }
    }

    private void CommitAutoCreateToggle(bool virtualDisk)
    {
        var pool = SelectedPool();
        if (_filling || pool is null || pool.IsPrimordial || !ViewModel.IsUsingSimulatedInventory)
        {
            return;
        }

        var poolId = pool.StableId;
        var priorIntents = new Dictionary<string, PoolEditIntent>(_poolIntents, StringComparer.OrdinalIgnoreCase)
        {
            [poolId] = new PoolEditIntent(
                virtualDisk ? !_autoVdiskSwitch.IsOn : _autoVdiskSwitch.IsOn,
                virtualDisk ? _autoPartitionSwitch.IsOn : !_autoPartitionSwitch.IsOn,
                _fileSystemBox.SelectedItem as string ?? "NTFS",
                ParseSize(_clusterBox.SelectedItem as string ?? "64 KiB"),
                CommittedPrimaryPartition(poolId)?.FileSystemLabel ?? _volumeNameBox.Text.Trim())
        };
        var prior = new EditorDraftState(
            _working,
            priorIntents,
            new HashSet<string>(_maximumSizeFields, StringComparer.OrdinalIgnoreCase));
        CaptureSelectedIntent();
        var next = _working;
        var placeholder = _working.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
            && EditWorkspace.IsDraftVirtualDisk(item.StableId));
        try
        {
            if (virtualDisk
                && EditWorkspace.IsDraftPool(poolId)
                && _autoVdiskSwitch.IsOn
                && placeholder is null)
            {
                next = EditWorkspace.InsertDraftVirtualDisk(
                    _working,
                    poolId,
                    ViewModel.Localization["NotCreatedVirtualDisk"],
                    "Simple",
                    65536);
            }
            else if (virtualDisk && !_autoVdiskSwitch.IsOn && placeholder is not null)
            {
                next = EditWorkspace.DeleteVirtualDiskFromWorking(_working, placeholder.StableId);
            }

            _undoStack.Push(prior);
            _redoStack.Clear();
            _working = next;
            _formDirty = false;
            RefreshAll();
        }
        catch (InvalidOperationException exception)
        {
            _ = ShowMessageAsync(ViewModel.Localization["OperationFailed"], exception.Message);
        }
    }

}
