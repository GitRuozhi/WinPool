using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using WinPool_App.Controls;

namespace WinPool_App;

public sealed partial class StorageStructurePage
{
    private readonly StackPanel PendingActionsPanel = new() { Spacing = 6 };
    private readonly List<Action> _localizeForm = [];
    private readonly Dictionary<Grid, (Button Button, FontIcon Arrow, bool Collapsed)> _sectionHeaders = [];
    private readonly Dictionary<(WorkspaceViewModel Workspace, SystemId System), NavigationDraft> _navigationDrafts = [];
    private (WorkspaceViewModel Workspace, SystemId System)? _activeNavigationKey;
    private bool _committingLiveForm;
    private bool _formHistoryCaptured;
    private PendingBorderAnimation? _applyAnimation;
    private PendingBorderAnimation? _pendingAnimation;
    private int PendingActionCount => ViewModel.IsUsingSimulatedInventory
        ? _currentPlan?.Steps.Count ?? 0 : _realDraftPreview?.Actions.Count ?? 0;

    private sealed record FormInputs(string[] Names, object?[] Combos, string[] Capacities,
        string[] Numbers, bool Dirty, bool ShowHotSpare, bool ShowRetired, bool HistoryCaptured);
    private sealed record NavigationDraft(SimulationEditingSession Session, string? Pool, string? Disk,
        string? VirtualDisk, string? Tier, FormInputs Inputs, string[] InitializedMaximums,
        Dictionary<string, string> NameDefaults, bool HasChanges, long ContentRevision, long BindingGeneration);

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_activeNavigationKey is { } key && _formBuilt)
            _navigationDrafts[key] = new(EditingSession, _selectedPoolId, _selectedPoolDiskId,
                _selectedPoolVdiskId, _selectedPoolTierId, CaptureFormInputs(),
                _capacityDefaultModesInitialized.ToArray(), new(_nameDefaults, StringComparer.OrdinalIgnoreCase),
                HasUncommittedChanges(), EditingSession.ContentRevision, EditingSession.BindingGeneration);
        _applyAnimation?.Dispose();
        _applyAnimation = null;
        _pendingAnimation?.Dispose();
        _pendingAnimation = null;
        base.OnNavigatedFrom(e);
    }

    private FormInputs? RestoreNavigationDraft(string? targetStableId)
    {
        var key = (ViewModel, ViewModel.SelectedSystem.SystemId);
        _activeNavigationKey = key;
        var fresh = ViewModel.IsUsingSimulatedInventory
            ? EditWorkspace.NormalizeTierCapacities(ViewModel.EffectiveActiveSnapshot)
            : ViewModel.EffectiveActiveSnapshot;
        _programmaticCapacityText.Clear();
        _userCapacityEdits.Clear();
        _capacityDefaultModesInitialized.Clear();
        _nameDefaults.Clear();
        FormInputs? inputs = null;
        if (_navigationDrafts.TryGetValue(key, out var prior))
        {
            EditingSession = StructureDraftReentry.Enter(prior.Session, ViewModel.SelectedSystem,
                fresh, prior.HasChanges, out var rebound, prior.ContentRevision, prior.BindingGeneration);
            _selectedPoolId = prior.Pool;
            _selectedPoolDiskId = prior.Disk;
            _selectedPoolVdiskId = prior.VirtualDisk;
            _selectedPoolTierId = prior.Tier;
            var captureIsCurrent = !rebound && prior.ContentRevision == EditingSession.ContentRevision
                && prior.BindingGeneration == EditingSession.BindingGeneration;
            if (captureIsCurrent)
            {
                _capacityDefaultModesInitialized.UnionWith(prior.InitializedMaximums);
                foreach (var item in prior.NameDefaults) _nameDefaults[item.Key] = item.Value;
                inputs = prior.Inputs;
            }
        }
        else
        {
            EditingSession = StructureDraftReentry.Enter(null, ViewModel.SelectedSystem, fresh, false, out _);
            _selectedPoolId = null;
            _selectedPoolDiskId = null;
            _selectedPoolVdiskId = null;
            _selectedPoolTierId = null;
        }
        _formDirty = false;
        _formHistoryCaptured = false;
        NormalizeSelection();
        if (targetStableId is not null)
        {
            var targetPool = ResolvePoolId(targetStableId);
            // Navigating to the same pool keeps the current child selection and input.
            if (targetPool != _selectedPoolId)
            {
                _selectedPoolId = targetPool;
                _selectedPoolDiskId = null;
                _selectedPoolVdiskId = null;
                _selectedPoolTierId = null;
                inputs = null;
            }
        }
        return inputs;
    }

    private IEnumerable<ComboBox> FormCombos() => TierGroups().SelectMany(group =>
        new[] { group.ResiliencyBox, group.InterleaveBox, group.ProvisioningBox })
        .Concat([_partitionStyleBox, _fileSystemBox, _clusterBox]);
    private IEnumerable<NumberBox> FormNumbers() => TierGroups().SelectMany(group =>
        new[] { group.CopiesBox, group.FailuresBox, group.ColumnsBox });
    private FormInputs CaptureFormInputs() => new([_poolNameBox.Text, _virtualDiskNameBox.Text, _volumeNameBox.Text],
        FormCombos().Select(box => box.SelectedItem).ToArray(), TierGroups().Select(group => group.SizeBox.Text).ToArray(),
        FormNumbers().Select(box => box.Text).ToArray(), _formDirty, ShowHotSpareSwitch.IsOn, ShowRetiredSwitch.IsOn, _formHistoryCaptured);

    private void RestoreFormInputs(FormInputs state)
    {
        _filling = true;
        try
        {
            _poolNameBox.Text = state.Names[0];
            _virtualDiskNameBox.Text = state.Names[1];
            _volumeNameBox.Text = state.Names[2];
            foreach (var pair in FormCombos().Zip(state.Combos)) pair.First.SelectedItem = pair.Second;
            foreach (var pair in TierGroups().Zip(state.Capacities)) SetCapacityText(pair.First, pair.Second);
            foreach (var pair in FormNumbers().Zip(state.Numbers)) pair.First.Text = pair.Second;
            ShowHotSpareSwitch.IsOn = state.ShowHotSpare;
            ShowRetiredSwitch.IsOn = state.ShowRetired;
            _lastPoolName = _poolNameBox.Text;
            _lastVirtualDiskName = _virtualDiskNameBox.Text;
            _formDirty = state.Dirty;
            _formHistoryCaptured = state.HistoryCaptured;
        }
        finally { _filling = false; }
    }

    private bool HasIncompleteNumberInput() => FormNumbers().Any(box => box.IsEnabled
        && (string.IsNullOrWhiteSpace(box.Text) || !double.IsFinite(box.Value)));

    private void CommitLiveFormEdit()
    {
        if (_filling || _committingLiveForm || !_formDirty || !CanEditDraft
            || SelectedPool() is not { IsPrimordial: false } || HasIncompleteNumberInput())
            return;
        _committingLiveForm = true;
        try
        {
            // Layout and capacity are one edit transaction. Recompute MAX and
            // clamp an explicit size before projecting the final layout.
            _filling = true;
            try
            {
                foreach (var group in TierGroups())
                {
                    UpdateMaximumSizeText(group);
                    IntegerTextInput.RefreshBounds(group.SizeBox);
                }
            }
            finally { _filling = false; }
            if (HasInvalidSizeInput()) return;
            var before = CaptureDraftState();
            var merged = ApplyFormToWorking(_working);
            var currentIntent = SelectedCreationIntent();
            before.PoolIntents.TryGetValue(_selectedPoolId!, out var priorIntent);
            var objectChanged = !before.Snapshot.StoragePools.SequenceEqual(merged.StoragePools)
                || !before.Snapshot.StorageTiers.SequenceEqual(merged.StorageTiers)
                || !before.Snapshot.VirtualDisks.SequenceEqual(merged.VirtualDisks)
                || !before.Snapshot.OsDisks.SequenceEqual(merged.OsDisks);
            if (!objectChanged && SamePoolIntent(priorIntent, currentIntent))
            {
                _formDirty = false;
                _formHistoryCaptured = false;
                return;
            }
            if (!_formHistoryCaptured) _undoStack.Push(before);
            _redoStack.Clear();
            _working = merged;
            CaptureSelectedIntent();
            _formDirty = false;
            _formHistoryCaptured = false;
            RefreshTopology();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            // An incomplete edit stays on the form; a preview never replaces the last valid draft.
            _planBuildError = exception.Message;
        }
        finally { _committingLiveForm = false; }
    }

    private static bool SamePoolIntent(PoolEditIntent? left, PoolEditIntent right) => left is not null
        && (left with { Tiers = null, MemberUsages = null }) == (right with { Tiers = null, MemberUsages = null })
        && (left.Tiers ?? []).SequenceEqual(right.Tiers ?? [])
        && (left.MemberUsages ?? []).SequenceEqual(right.MemberUsages ?? []);

    private string FormLabel(string key, string? fallback) => key switch
    {
        "ShowHotSpareLayer" => Text("显示热备层", "Show hot-spare layer"),
        "ShowRetiredLayer" => Text("显示退役层", "Show retired layer"),
        "TierToleratedFailures" => Text("容忍故障数", "Tolerated failures"),
        "TierDataCopies" => Text("数据副本数", "Data copies"),
        "TierStripeSize" => Text("条带单元", "Stripe unit"),
        "FullStripe" => Text("完整条带", "Full stripe"),
        _ => fallback ?? ViewModel.Localization[key]
    };

    private void ApplySectionCollapse(Grid grid)
    {
        var state = _sectionHeaders[grid];
        state.Arrow.Glyph = state.Collapsed ? "\uE76C" : "\uE70D";
        foreach (var child in grid.Children.OfType<FrameworkElement>())
            if (Grid.GetRow(child) > 0)
                child.Visibility = state.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        for (var index = 1; index < grid.RowDefinitions.Count; index++)
        {
            grid.RowDefinitions[index].Height = state.Collapsed ? new GridLength(0) : GridLength.Auto;
            grid.RowDefinitions[index].MinHeight = state.Collapsed ? 0 : 40;
        }
        if (!state.Collapsed)
        {
            UpdateFieldResets();
            _multiVdiskWarning.Visibility = SelectedPool() is { } pool
                && EditWorkspace.HasMultipleVirtualDisks(_working, pool.StableId) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdatePendingActionCount()
    {
        PendingActionsButtonLabel.Text = Text($"{PendingActionCount} 动作等待实施", $"{PendingActionCount} actions waiting");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(PendingActionsButton, "StructurePendingActionsButton");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PendingActionsButton, PendingActionsButtonLabel.Text);
        PendingActionsButton.IsEnabled = true;
        var active = PendingActionCount > 0;
        _pendingAnimation ??= new PendingBorderAnimation(PendingButtonHost, PendingDefaultOutline,
            PendingThemeOutline, "structure-pending-count");
        PendingDefaultOutline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PendingThemeOutline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _pendingAnimation.SetActive(active);
    }

    private async void PendingActions_Click(object sender, RoutedEventArgs e)
    {
        RefreshPendingActions();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = Text("待实施动作", "Pending actions"), CloseButtonText = ViewModel.Localization["Close"],
            Content = new ScrollViewer { MaxHeight = 520, Content = PendingActionsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        try { await dialog.ShowAsync(); }
        finally { ((ScrollViewer)dialog.Content).Content = null; }
    }

    private void UpdateApplyAnimation(bool active)
    {
        _applyAnimation ??= new PendingBorderAnimation(ApplyButtonHost, ApplyDefaultOutline, ApplyThemeOutline,
            "structure-apply");
        ApplyDefaultOutline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ApplyThemeOutline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _applyAnimation.SetActive(active);
    }

    private static TextBox? FindNumberInput(DependencyObject parent)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox input) return input;
            if (FindNumberInput(child) is { } nested) return nested;
        }
        return null;
    }

    private void HookLiveNumberInput(NumberBox number)
    {
        var connected = false;
        number.Loaded += (_, _) =>
        {
            if (connected || FindNumberInput(number) is not { } input) return;
            connected = true;
            var updating = false;
            input.TextChanged += (_, _) =>
            {
                if (_filling || updating || !number.IsEnabled || SelectedPool() is not { IsPrimordial: false }) return;
                if (TierGroups().FirstOrDefault(group => ReferenceEquals(group.ColumnsBox, number)) is { } columnGroup
                    && int.TryParse(input.Text, out var chosenColumns))
                    _columnSuggestions[ColumnSuggestionKey(columnGroup)] = ColumnChoice(columnGroup).Choose(chosenColumns);
                _formDirty = true;
                if (long.TryParse(input.Text, out var typed) && typed >= number.Minimum && typed <= number.Maximum)
                {
                    updating = true;
                    try { number.Value = typed; }
                    finally { updating = false; }
                }
                UpdateButtonState();
            };
        };
    }

    private void RefreshFullStripes()
    {
        foreach (var group in TierGroups())
        {
            var tier = SelectedTier(group.Media);
            var editable = group.InterleaveBox.IsEnabled;
            var layout = editable ? group.ResiliencyBox.SelectedItem as string : tier?.ResiliencySettingName;
            var unit = editable && group.InterleaveBox.SelectedItem is string token ? ParseSize(token) : tier?.Interleave;
            var columns = editable ? ColumnsCount(group.ColumnsBox) : tier?.NumberOfColumns;
            var copies = editable ? CopiesCount(group.CopiesBox) : tier?.NumberOfDataCopies;
            var redundancy = editable ? FailuresCount(group.FailuresBox) : tier?.PhysicalDiskRedundancy;
            var stripe = FullStripeCalculator.Calculate(layout, unit, columns, copies, redundancy);
            group.FullStripeDisplay.Text = stripe is null ? string.Empty
                : $"{stripe.DataBytes / 1024m:0.##} KiB + {stripe.ExtraBytes / 1024m:0.##} KiB";
            var semantics = stripe is null ? string.Empty : Text(
                $"数据 + {(stripe.ExtraKind == StripeExtraKind.Copies ? "额外镜像副本" : stripe.ExtraKind == StripeExtraKind.Parity ? "校验" : "额外写入")}",
                $"Data + {(stripe.ExtraKind == StripeExtraKind.Copies ? "additional mirror copies" : stripe.ExtraKind == StripeExtraKind.Parity ? "parity" : "extra writes")}");
            ToolTipService.SetToolTip(group.FullStripeDisplay, semantics);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(group.FullStripeDisplay, semantics);
        }
    }
}
