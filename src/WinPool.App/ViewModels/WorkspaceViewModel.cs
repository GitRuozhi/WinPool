using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPool.App.Services;
using WinPool.Core;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly IStorageInventoryProvider _inventoryProvider;
    private readonly IUserPreferencesService _preferencesService;
    private readonly IGlobalNotificationService _notificationService;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, bool> _expandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkspaceCategory, string> _categorySelections = [];
    private StorageUnitRef? _contextUnit;
    public const string AddStorageSystemKey = "action:add-storage-system";

    public WorkspaceViewModel(
        IStorageInventoryProvider inventoryProvider,
        IPrivilegeService privilegeService,
        IUserPreferencesService preferencesService,
        IExportService exportService,
        IGlobalNotificationService notificationService)
    {
        _inventoryProvider = inventoryProvider;
        _preferencesService = preferencesService;
        _notificationService = notificationService;
        ExportService = exportService;
        PrivilegeState = privilegeService.Current;
        Execution = new ExecutionModeController(PrivilegeState);
        Snapshot = StorageSnapshot.Empty(Environment.MachineName);
        SimulatedSnapshot = SimulationStorageSnapshotFactory.Create();
        IsUsingSimulatedInventory = true;
        Localization = new LocalizationService();
        _selectedCategory = WorkspaceCategory.System;
        RefreshLocalizedContent();
    }

    public LocalizationService Localization { get; }

    public IExportService ExportService { get; }

    public IGlobalNotificationService NotificationService => _notificationService;

    public PrivilegeState PrivilegeState { get; }

    public ExecutionModeController Execution { get; }

    public bool CanUseRealMode => Execution.CanUseRealMode;

    public bool IsRealMode => Execution.Mode == ExecutionMode.Real;

    public StorageSnapshot Snapshot { get; private set; }

    public StorageSnapshot SimulatedSnapshot { get; }

    public StorageSnapshot ActiveSnapshot =>
        IsUsingSimulatedInventory ? SimulatedSnapshot : Snapshot;

    public bool IsUsingSimulatedInventory { get; private set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public ObservableCollection<WorkspaceItem> Objects { get; } = [];

    public ObservableCollection<DetailRow> Details { get; } = [];

    [ObservableProperty]
    private WorkspaceCategory _selectedCategory;

    [ObservableProperty]
    private CategoryItem? _selectedCategoryItem;

    [ObservableProperty]
    private WorkspaceItem? _selectedWorkspaceItem;

    public ObservableCollection<TopologyNodeViewModel> TopologySystems { get; } = [];

    [ObservableProperty]
    private string? _selectedStableId;

    [ObservableProperty]
    private string? _selectedTopologyStableId;

    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    private string _detailSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanError = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasScanError => !string.IsNullOrWhiteSpace(ScanError);

    public bool HasSelection => SelectedWorkspaceItem?.Unit is not null;

    public bool CanOpenSelectedPartition =>
        !IsUsingSimulatedInventory
        && ResolveDetailUnit()?.Kind == StorageUnitKind.Partition
        && ActiveSnapshot.Partitions.Any(x => x.StableId == ResolveDetailUnit()?.StableId && Directory.Exists(x.Path));

    public bool HasRelatedTarget => GetPrimaryRelatedTarget() is not null;

    public event EventHandler? WorkspaceSelectionChanged;

    partial void OnSelectedCategoryItemChanged(CategoryItem? value)
    {
        if (value is not null && value.Category != SelectedCategory)
        {
            SelectedCategory = value.Category;
        }
    }

    partial void OnSelectedCategoryChanged(WorkspaceCategory value)
    {
        RebuildObjects(_categorySelections.GetValueOrDefault(value));
        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == value);
    }

    partial void OnSelectedWorkspaceItemChanged(WorkspaceItem? value)
    {
        _contextUnit = null;
        if (value?.IsAction == true)
        {
            SelectedStableId = null;
            SelectedTopologyStableId = null;
            _categorySelections[SelectedCategory] = value.Key;
            BuildDetails();
            RefreshTopologySelection();
            NotifySelectionState();
            WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SelectedCategory == WorkspaceCategory.System && value?.Unit?.Kind == StorageUnitKind.System)
        {
            SwitchInventory(value.Unit.StableId == SimulatedSnapshot.Computer.StableId);
        }
        SelectedStableId = value?.Unit?.StableId;
        SelectedTopologyStableId = SelectedStableId;
        if (value?.Unit is not null)
        {
            _categorySelections[SelectedCategory] = value.Key;
        }
        BuildDetails();
        RefreshTopologySelection();
        ExpandSelectedTopologyPath();
        NotifySelectionState();
        WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedTopologyStableIdChanged(string? value) => RefreshTopologySelection();

    partial void OnScanErrorChanged(string value) => OnPropertyChanged(nameof(HasScanError));

    public async Task InitializeAsync()
    {
        var preferences = await _preferencesService.LoadAsync();
        Localization.Language = preferences.Language;
        CurrentPreferences = preferences;
        RefreshLocalizedContent();
    }

    public UserPreferences CurrentPreferences { get; private set; } = new();

    public double TopologyHorizontalOffset { get; set; }

    public double TopologyVerticalOffset { get; set; }

    public async Task SetThemeAsync(ThemePreference theme)
    {
        CurrentPreferences = CurrentPreferences with { Theme = theme };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetAccentColorAsync(AccentColorPreference accentColor)
    {
        CurrentPreferences = CurrentPreferences with { AccentColor = accentColor };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetLanguageAsync(LanguagePreference language)
    {
        CurrentPreferences = CurrentPreferences with { Language = language };
        Localization.Language = language;
        RefreshLocalizedContent();
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public bool TrySetExecutionMode(ExecutionMode mode)
    {
        var changed = Execution.TrySetMode(mode);
        OnPropertyChanged(nameof(IsRealMode));
        OnPropertyChanged(nameof(CanUseRealMode));
        return changed;
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (!await _scanGate.WaitAsync(0))
        {
            return;
        }

        IsScanning = true;
        ScanError = string.Empty;
        StatusMessage = Localization["Scanning"];
        var previous = new WorkspaceSelection(SelectedCategory, SelectedStableId, _contextUnit?.Kind, _contextUnit?.StableId);
        var previousTopologyStableId = SelectedTopologyStableId;
        try
        {
            var snapshot = await _inventoryProvider.ScanAsync(CancellationToken.None);
            Snapshot = snapshot;
            OnPropertyChanged(nameof(ActiveSnapshot));
            if (IsUsingSimulatedInventory)
            {
                RebuildTopology();
            }
            else
            {
                var restored = WorkspaceState.Restore(snapshot, previous);
                SelectedCategory = restored.Category;
                RebuildTopology();
                RebuildObjects(restored.StableId);
                SelectedTopologyStableId = snapshot.FindUnit(previousTopologyStableId ?? string.Empty) is null
                    ? null
                    : previousTopologyStableId;
            }
            StatusMessage = $"{Localization["LastScan"]}: {snapshot.ScannedAt.LocalDateTime:G}";
            foreach (var warning in snapshot.Warnings)
            {
                _notificationService.PublishWarning(
                    Localization["Warning"],
                    warning.Message,
                    "inventory",
                    $"inventory:{snapshot.ScannedAt.UtcTicks}:{warning.Code}:{warning.StableId}");
            }
            BuildDetails();
        }
        catch (Exception ex)
        {
            ScanError = $"{Localization["ScanFailed"]} {ex.Message}";
            StatusMessage = ScanError;
            _notificationService.PublishError(
                Localization["Error"],
                ScanError,
                "inventory",
                $"inventory-error:{DateTimeOffset.UtcNow.Ticks}");
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }

    public void SelectTopologyUnit(StorageUnitRef unit) =>
        SelectTopologyUnit(unit, ActiveSnapshot);

    public void SelectTopologyUnit(StorageUnitRef unit, StorageSnapshot sourceSnapshot)
    {
        if (SelectedTopologyStableId?.Equals(unit.StableId, StringComparison.OrdinalIgnoreCase) == true)
        {
            SelectedTopologyStableId = null;
            return;
        }

        var switchedInventory = SwitchInventory(
            sourceSnapshot.Computer.StableId == SimulatedSnapshot.Computer.StableId);
        var selection = WorkspaceMapper.FromUnit(unit, sourceSnapshot);
        if (selection.StableId is not null)
        {
            _categorySelections[selection.Category] = selection.StableId;
        }
        if (SelectedCategory != selection.Category)
        {
            SelectedCategory = selection.Category;
        }
        else if (switchedInventory)
        {
            RebuildObjects(selection.StableId);
        }
        if (selection.ContextStableId is not null)
        {
            _contextUnit = sourceSnapshot.FindUnit(selection.ContextStableId);
        }

        var item = Objects.FirstOrDefault(x => x.Unit?.StableId == selection.StableId);
        if (item is not null)
        {
            SelectedWorkspaceItem = item;
            SelectedTopologyStableId = unit.StableId;
            if (selection.ContextStableId is not null)
            {
                _contextUnit = sourceSnapshot.FindUnit(selection.ContextStableId);
                BuildDetails();
            }
            ExpandSelectedTopologyPath();
        }
    }

    public StorageUnitRef? ResolveDetailUnit() => _contextUnit ?? SelectedWorkspaceItem?.Unit;

    public bool GetExpandedState(string stableId, bool defaultValue) =>
        _expandedStates.GetValueOrDefault(stableId, defaultValue);

    public void SaveExpandedState(string stableId, bool isExpanded) =>
        _expandedStates[stableId] = isExpanded;

    public void SystemRootExpanded(TopologyNodeViewModel expandedRoot)
    {
        foreach (var root in TopologySystems.Where(x => !ReferenceEquals(x, expandedRoot) && x.IsExpanded))
        {
            root.IsExpanded = false;
        }
    }

    public double TopologyViewportWidth { get; private set; } = 1400;

    public void UpdateTopologyViewportWidth(double width)
    {
        var normalized = Math.Max(320, width);
        if (Math.Abs(TopologyViewportWidth - normalized) < 1)
        {
            return;
        }

        TopologyViewportWidth = normalized;
        foreach (var root in TopologySystems)
        {
            root.RefreshLayout();
        }
    }

    public string CreateSelectedSummary()
    {
        var unit = ResolveDetailUnit();
        if (unit is null)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            new[] { $"{unit.DisplayName} ({unit.Kind})" }
                .Concat(Details.Select(x => $"{x.Label}: {x.Value}")));
    }

    public void NavigateToPrimaryRelatedTarget()
    {
        var target = GetPrimaryRelatedTarget();
        if (target is not null)
        {
            SelectTopologyUnit(target);
        }
    }

    public void RefreshLocalizedContent()
    {
        var category = SelectedCategory;
        var topologyStableId = SelectedTopologyStableId;
        var preserveTopologySelection = Categories.Count > 0;
        Categories.Clear();
        Categories.Add(new CategoryItem(WorkspaceCategory.System, Localization["System"], "\uE7F8"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Pool, Localization["Pool"], "\uEDA2"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Tier, Localization["Tier"], "\uE8FD"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Disk, Localization["Disk"], "\uEDA2"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Partition, Localization["Partition"], "\uE7C3"));
        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == category);
        RebuildObjects(SelectedStableId);
        RebuildTopology();
        if (preserveTopologySelection)
        {
            SelectedTopologyStableId = topologyStableId is not null
                && (SimulatedSnapshot.FindUnit(topologyStableId) is not null
                    || Snapshot.FindUnit(topologyStableId) is not null)
                    ? topologyStableId
                    : null;
        }
        BuildDetails();
    }

    private void RebuildTopology()
    {
        TopologySystems.Clear();
        TopologySystems.Add(CreateTopologyRoot(SimulatedSnapshot));
        TopologySystems.Add(CreateTopologyRoot(Snapshot));
        ApplySystemRootExpansion();
        RefreshTopologySelection();
    }

    private TopologyNodeViewModel CreateTopologyRoot(StorageSnapshot snapshot)
    {
        var root = TopologyProjector.Project(snapshot);
        return new TopologyNodeViewModel(
            root,
            this,
            snapshot,
            $"{snapshot.Computer.StableId}:root");
    }

    private void RebuildObjects(string? preferredStableId)
    {
        Objects.Clear();
        foreach (var item in CreateWorkspaceItems(SelectedCategory))
        {
            Objects.Add(item);
        }

        SelectedWorkspaceItem =
            Objects.FirstOrDefault(x => x.Key == preferredStableId)
            ?? Objects.FirstOrDefault();
    }

    private IEnumerable<WorkspaceItem> CreateWorkspaceItems(WorkspaceCategory category)
    {
        switch (category)
        {
            case WorkspaceCategory.System:
                yield return new WorkspaceItem(
                    SimulatedSnapshot.Computer.StableId,
                    Localization["SimulatedComputer"],
                    new StorageUnitRef(
                        SimulatedSnapshot.Computer.StableId,
                        StorageUnitKind.System,
                        Localization["SimulatedComputer"]));
                yield return new WorkspaceItem(
                    Snapshot.Computer.StableId,
                    Snapshot.Computer.Name,
                    new StorageUnitRef(Snapshot.Computer.StableId, StorageUnitKind.System, Snapshot.Computer.Name),
                    false);
                yield return new WorkspaceItem(
                    AddStorageSystemKey,
                    Localization["AddStorageSystem"],
                    null,
                    true);
                break;
            case WorkspaceCategory.Pool:
                foreach (var pool in ActiveSnapshot.StoragePools
                             .OrderByDescending(x => x.IsPrimordial)
                             .ThenBy(x => x.FriendlyName))
                {
                    yield return new WorkspaceItem(
                        pool.StableId,
                        pool.IsPrimordial ? "Primordial" : pool.FriendlyName,
                        new StorageUnitRef(pool.StableId, StorageUnitKind.StoragePool, pool.FriendlyName, pool.IsStable),
                        false);
                }
                if (ActiveSnapshot.NetworkDisks.Count > 0)
                {
                    var stableId = TopologyProjector.NetworkGroupStableId(ActiveSnapshot);
                    yield return new WorkspaceItem(
                        stableId,
                        Localization["Network"],
                        new StorageUnitRef(stableId, StorageUnitKind.NetworkDiskGroup, Localization["Network"]),
                        false);
                }
                if (TopologyProjector.GetOtherOsDisks(ActiveSnapshot).Count > 0)
                {
                    var stableId = TopologyProjector.OtherGroupStableId(ActiveSnapshot);
                    yield return new WorkspaceItem(
                        stableId,
                        Localization["Other"],
                        new StorageUnitRef(stableId, StorageUnitKind.OtherDiskGroup, Localization["Other"]),
                        false);
                }
                break;
            case WorkspaceCategory.Tier:
                foreach (var tier in ActiveSnapshot.StorageTiers)
                {
                    var poolName = ActiveSnapshot.StoragePools.FirstOrDefault(x => x.StableId == tier.PoolStableId)?.FriendlyName;
                    yield return new WorkspaceItem(
                        tier.StableId,
                        tier.FriendlyName,
                        new StorageUnitRef(tier.StableId, StorageUnitKind.StorageTier, tier.FriendlyName, tier.IsStable, tier.PoolStableId),
                        false);
                }
                break;
            case WorkspaceCategory.Disk:
                foreach (var disk in ActiveSnapshot.PhysicalDisks.OrderBy(x => x.FriendlyName))
                {
                    yield return new WorkspaceItem(
                        disk.StableId,
                        disk.FriendlyName,
                        new StorageUnitRef(disk.StableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
                        false);
                }
                foreach (var virtualDisk in ActiveSnapshot.VirtualDisks.OrderBy(x => x.FriendlyName))
                {
                    yield return new WorkspaceItem(
                        virtualDisk.StableId,
                        virtualDisk.FriendlyName,
                        new StorageUnitRef(
                            virtualDisk.StableId,
                            StorageUnitKind.VirtualDisk,
                            virtualDisk.FriendlyName,
                            virtualDisk.IsStable,
                            virtualDisk.PoolStableId),
                        false);
                }
                foreach (var network in ActiveSnapshot.NetworkDisks.OrderBy(x => x.Name))
                {
                    yield return new WorkspaceItem(
                        network.StableId,
                        network.Name,
                        new StorageUnitRef(network.StableId, StorageUnitKind.NetworkDisk, network.Name, network.IsStable),
                        false);
                }
                foreach (var osDisk in ActiveSnapshot.OsDisks
                             .Where(x => string.IsNullOrWhiteSpace(x.PhysicalDiskStableId)
                                         && string.IsNullOrWhiteSpace(x.VirtualDiskStableId))
                             .OrderBy(x => x.FriendlyName))
                {
                    yield return new WorkspaceItem(
                        osDisk.StableId,
                        osDisk.FriendlyName,
                        new StorageUnitRef(osDisk.StableId, StorageUnitKind.OsDisk, osDisk.FriendlyName),
                        false);
                }
                break;
            case WorkspaceCategory.Partition:
                foreach (var partition in TopologyProjector.OrderPartitionsForWorkspace(ActiveSnapshot))
                {
                    var title = TopologyProjector.PartitionDisplayName(partition);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = PartitionTypeName(partition.Type);
                    }
                    yield return new WorkspaceItem(
                        partition.StableId,
                        title,
                        new StorageUnitRef(partition.StableId, StorageUnitKind.Partition, title, partition.IsStable, partition.OsDiskStableId),
                        false);
                }
                foreach (var network in ActiveSnapshot.NetworkDisks.OrderBy(x => x.Name))
                {
                    yield return new WorkspaceItem(
                        network.StableId,
                        network.Name,
                        new StorageUnitRef(network.StableId, StorageUnitKind.NetworkDisk, network.Name, network.IsStable),
                        false);
                }
                break;
        }
    }

    private void BuildDetails()
    {
        Details.Clear();
        var unit = ResolveDetailUnit();
        if (unit is null)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            return;
        }

        DetailTitle = unit.DisplayName;
        DetailSubtitle = TopologyProjector.JoinSummary(KindName(unit.Kind), Localization["ReadOnly"]);
        switch (unit.Kind)
        {
            case StorageUnitKind.System:
                Details.Add(new DetailRow("Windows", $"{ActiveSnapshot.Computer.WindowsProductName} {ActiveSnapshot.Computer.WindowsVersion} ({ActiveSnapshot.Computer.OsBuild})"));
                Details.Add(new DetailRow(Localization["PhysicalDisk"], ActiveSnapshot.PhysicalDisks.Count.ToString()));
                Details.Add(new DetailRow(Localization["StoragePool"], ActiveSnapshot.StoragePools.Count.ToString()));
                Details.Add(new DetailRow(Localization["StorageTier"], ActiveSnapshot.StorageTiers.Count.ToString()));
                Details.Add(new DetailRow(Localization["VirtualDisk"], ActiveSnapshot.VirtualDisks.Count.ToString()));
                Details.Add(new DetailRow(Localization["NetworkDisk"], ActiveSnapshot.NetworkDisks.Count.ToString()));
                Details.Add(new DetailRow(Localization["Partition"], ActiveSnapshot.Partitions.Count.ToString()));
                break;
            case StorageUnitKind.StoragePool:
                var pool = ActiveSnapshot.StoragePools.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["Type"], pool.IsPrimordial ? Localization["OriginalPool"] : Localization["StoragePool"]));
                Details.Add(new DetailRow(Localization["Health"], TopologyProjector.JoinSummary(pool.HealthStatus, pool.OperationalStatus)));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(pool.Size)));
                Details.Add(new DetailRow(Localization["Allocated"], TopologyProjector.FormatBytes(pool.AllocatedSize)));
                Details.Add(new DetailRow(Localization["Members"], pool.MemberPhysicalDiskIds.Count.ToString()));
                break;
            case StorageUnitKind.StorageTier:
                var tier = ActiveSnapshot.StorageTiers.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["Media"], tier.MediaType));
                Details.Add(new DetailRow(Localization["Role"], tier.ResiliencySettingName));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(tier.Size)));
                Details.Add(new DetailRow(Localization["Members"], tier.MemberPhysicalDiskIds.Count.ToString()));
                break;
            case StorageUnitKind.PhysicalDisk:
                var disk = ActiveSnapshot.PhysicalDisks.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["Model"], disk.Model));
                Details.Add(new DetailRow(Localization["Serial"], disk.MaskedSerialNumber));
                Details.Add(new DetailRow(Localization["Bus"], disk.BusType));
                Details.Add(new DetailRow(Localization["Media"], disk.MediaType));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(disk.Size)));
                Details.Add(new DetailRow(Localization["Health"], TopologyProjector.JoinSummary(disk.HealthStatus, disk.OperationalStatus)));
                Details.Add(new DetailRow(Localization["CanPool"], disk.CanPool ? Localization["Yes"] : Localization["No"]));
                if (!disk.CanPool && !string.IsNullOrWhiteSpace(disk.CannotPoolReason))
                {
                    Details.Add(new DetailRow(Localization["CannotPoolReason"], disk.CannotPoolReason));
                }
                break;
            case StorageUnitKind.VirtualDisk:
                var virtualDisk = ActiveSnapshot.VirtualDisks.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["Health"], TopologyProjector.JoinSummary(virtualDisk.HealthStatus, virtualDisk.OperationalStatus)));
                Details.Add(new DetailRow(Localization["Role"], virtualDisk.ResiliencySettingName));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(virtualDisk.Size)));
                Details.Add(new DetailRow("Columns", virtualDisk.NumberOfColumns?.ToString() ?? "—"));
                Details.Add(new DetailRow("Interleave", virtualDisk.Interleave is null ? "—" : TopologyProjector.FormatBytes(virtualDisk.Interleave.Value)));
                break;
            case StorageUnitKind.Partition:
                var partition = ActiveSnapshot.Partitions.First(x => x.StableId == unit.StableId);
                DetailTitle = TopologyProjector.PartitionDisplayName(partition);
                Details.Add(new DetailRow(Localization["Type"], PartitionTypeName(partition.Type)));
                Details.Add(new DetailRow(Localization["FileSystem"], string.IsNullOrWhiteSpace(partition.FileSystem) ? Localization["Unknown"] : partition.FileSystem));
                Details.Add(new DetailRow(Localization["AllocationUnit"], partition.AllocationUnitSize is null ? Localization["Unknown"] : TopologyProjector.FormatBytes(partition.AllocationUnitSize.Value)));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(partition.Size)));
                Details.Add(new DetailRow(Localization["Available"], TopologyProjector.FormatBytes(partition.SizeRemaining)));
                Details.Add(new DetailRow(Localization["Health"], TopologyProjector.JoinSummary(partition.HealthStatus, partition.OperationalStatus)));
                Details.Add(new DetailRow(Localization["Path"], string.IsNullOrWhiteSpace(partition.Path) ? "—" : partition.Path));
                break;
            case StorageUnitKind.NetworkDisk:
                var network = ActiveSnapshot.NetworkDisks.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["FileSystem"], network.FileSystem));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(network.Size)));
                Details.Add(new DetailRow(Localization["Available"], TopologyProjector.FormatBytes(network.SizeRemaining)));
                Details.Add(new DetailRow(Localization["Path"], network.ProviderPath));
                break;
            case StorageUnitKind.NetworkDiskGroup:
                Details.Add(new DetailRow(Localization["Type"], Localization["NetworkStorageGroup"]));
                Details.Add(new DetailRow(Localization["NetworkDisk"], ActiveSnapshot.NetworkDisks.Count.ToString()));
                Details.Add(new DetailRow(
                    Localization["Capacity"],
                    TopologyProjector.FormatBytes(ActiveSnapshot.NetworkDisks.Sum(x => x.Size))));
                Details.Add(new DetailRow(
                    Localization["Available"],
                    TopologyProjector.FormatBytes(ActiveSnapshot.NetworkDisks.Sum(x => x.SizeRemaining))));
                break;
            case StorageUnitKind.OtherDiskGroup:
                var otherDisks = TopologyProjector.GetOtherOsDisks(ActiveSnapshot);
                var otherIds = otherDisks.Select(x => x.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var otherPartitions = ActiveSnapshot.Partitions
                    .Where(x => x.OsDiskStableId is not null && otherIds.Contains(x.OsDiskStableId))
                    .ToList();
                Details.Add(new DetailRow(Localization["Type"], Localization["OtherStorageGroup"]));
                Details.Add(new DetailRow(Localization["OtherDisk"], otherDisks.Count.ToString()));
                Details.Add(new DetailRow(Localization["Partition"], otherPartitions.Count.ToString()));
                Details.Add(new DetailRow(
                    Localization["Capacity"],
                    TopologyProjector.FormatBytes(otherDisks.Sum(x => x.Size))));
                Details.Add(new DetailRow(
                    Localization["Available"],
                    TopologyProjector.FormatBytes(otherPartitions.Sum(x => x.SizeRemaining))));
                break;
            case StorageUnitKind.OsDisk:
                var osDisk = ActiveSnapshot.OsDisks.First(x => x.StableId == unit.StableId);
                Details.Add(new DetailRow(Localization["Type"], osDisk.PartitionStyle));
                Details.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(osDisk.Size)));
                break;
        }

        Details.Add(new DetailRow(Localization["LastScan"], ActiveSnapshot.ScannedAt == DateTimeOffset.MinValue ? "—" : ActiveSnapshot.ScannedAt.LocalDateTime.ToString("G")));
        NotifySelectionState();
    }

    private StorageUnitRef? GetPrimaryRelatedTarget()
    {
        var unit = ResolveDetailUnit();
        if (unit is null)
        {
            return null;
        }

        string? targetId = unit.Kind switch
        {
            StorageUnitKind.StoragePool =>
                ActiveSnapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId)?.MemberPhysicalDiskIds.FirstOrDefault(),
            StorageUnitKind.NetworkDiskGroup =>
                ActiveSnapshot.NetworkDisks.FirstOrDefault()?.StableId,
            StorageUnitKind.OtherDiskGroup =>
                TopologyProjector.GetOtherOsDisks(ActiveSnapshot).FirstOrDefault()?.StableId,
            StorageUnitKind.StorageTier =>
                ActiveSnapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId,
            StorageUnitKind.VirtualDisk =>
                ActiveSnapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId,
            StorageUnitKind.PhysicalDisk =>
                ActiveSnapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId
                ?? FindFirstPartitionForPhysicalDisk(unit.StableId),
            StorageUnitKind.Partition =>
                ResolvePartitionParent(ActiveSnapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId)),
            StorageUnitKind.OsDisk =>
                ActiveSnapshot.Partitions.FirstOrDefault(x => x.OsDiskStableId == unit.StableId)?.StableId,
            _ => null
        };
        return ActiveSnapshot.FindUnit(targetId);
    }

    private string? FindFirstPartitionForPhysicalDisk(string physicalDiskId)
    {
        var osDiskIds = ActiveSnapshot.OsDisks
            .Where(x => x.PhysicalDiskStableId == physicalDiskId)
            .Select(x => x.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ActiveSnapshot.Partitions
            .Where(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId))
            .Select(x => x.StableId)
            .FirstOrDefault();
    }

    private string KindName(StorageUnitKind kind) => kind switch
    {
        StorageUnitKind.System => Localization["System"],
        StorageUnitKind.StoragePool => Localization["StoragePool"],
        StorageUnitKind.StorageTier => Localization["StorageTier"],
        StorageUnitKind.PhysicalDisk => Localization["PhysicalDisk"],
        StorageUnitKind.VirtualDisk => Localization["VirtualDisk"],
        StorageUnitKind.NetworkDisk => Localization["NetworkDisk"],
        StorageUnitKind.OsDisk => Localization["OtherDisk"],
        StorageUnitKind.NetworkDiskGroup => Localization["NetworkStorageGroup"],
        StorageUnitKind.OtherDiskGroup => Localization["OtherStorageGroup"],
        StorageUnitKind.Partition => Localization["Partition"],
        _ => kind.ToString()
    };

    private bool SwitchInventory(bool useSimulation)
    {
        if (IsUsingSimulatedInventory == useSimulation)
        {
            return false;
        }

        IsUsingSimulatedInventory = useSimulation;
        _contextUnit = null;
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(CanOpenSelectedPartition));
        RebuildTopology();
        return true;
    }

    private void ApplySystemRootExpansion()
    {
        var activeSystemId = ActiveSnapshot.Computer.StableId;
        foreach (var root in TopologySystems)
        {
            root.IsExpanded = root.Unit.StableId == activeSystemId;
        }
    }

    private void RefreshTopologySelection()
    {
        foreach (var root in TopologySystems)
        {
            root.RefreshSelection();
        }
    }

    private string? ResolvePartitionParent(PartitionInfo? partition)
    {
        var osDisk = ActiveSnapshot.OsDisks.FirstOrDefault(x => x.StableId == partition?.OsDiskStableId);
        return osDisk?.VirtualDiskStableId ?? osDisk?.PhysicalDiskStableId ?? osDisk?.StableId;
    }

    public string PartitionTypeName(string type) => type switch
    {
        "Primary" => Localization["PrimaryPartition"],
        "Extended" => Localization["ExtendedPartition"],
        "Simple" => Localization["SimpleVolume"],
        "Spanned" => Localization["SpannedVolume"],
        "Striped" => Localization["StripedVolume"],
        "WindowsRecovery" => Localization["WindowsRecovery"],
        "EfiSystem" => Localization["EfiSystem"],
        "MicrosoftReserved" => Localization["MicrosoftReserved"],
        "SystemReserved" => Localization["SystemReserved"],
        _ => Localization["UnknownPartition"]
    };

    private void ExpandSelectedTopologyPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedStableId))
        {
            return;
        }

        foreach (var root in TopologySystems)
        {
            if (root.ExpandPathTo(SelectedStableId))
            {
                root.IsExpanded = true;
                SystemRootExpanded(root);
                break;
            }
        }
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenSelectedPartition));
        OnPropertyChanged(nameof(HasRelatedTarget));
    }

}
