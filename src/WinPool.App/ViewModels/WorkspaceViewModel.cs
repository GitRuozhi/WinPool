using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPool.App.Services;
using WinPool.Core;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly IHardwareInventoryProvider _hardwareInventoryProvider;
    private readonly IUserPreferencesService _preferencesService;
    private readonly IGlobalNotificationService _notificationService;
    private readonly IStorageSystemRepository _systemRepository;
    private readonly ISimulationOperationService _simulationOperations;
    private readonly IMachineRecordService _machineRecordService;
    private readonly IWorkspaceStateService _workspaceStateService;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, bool> _expandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkspaceCategory, string> _categorySelections = [];
    private bool _updatingComparisonSelection;
    private bool _suppressRelatedSelection;
    private StorageUnitRef? _contextUnit;
    private readonly HashSet<string> _shownFindings = new(StringComparer.Ordinal);
    public const string AddStorageSystemKey = "action:add-storage-system";
    private const string ScanningNotificationKey = "inventory:scanning";

    public WorkspaceViewModel(
        IHardwareInventoryProvider hardwareInventoryProvider,
        IPrivilegeService privilegeService,
        IUserPreferencesService preferencesService,
        IStorageSystemImportExportService importExportService,
        IStorageSystemRepository systemRepository,
        ISimulationOperationService simulationOperations,
        IGlobalNotificationService notificationService,
        IMachineRecordService machineRecordService,
        ICommandLogService commandLogService,
        IWorkspaceStateService workspaceStateService)
    {
        _hardwareInventoryProvider = hardwareInventoryProvider;
        _preferencesService = preferencesService;
        _notificationService = notificationService;
        _systemRepository = systemRepository;
        _simulationOperations = simulationOperations;
        _machineRecordService = machineRecordService;
        _workspaceStateService = workspaceStateService;
        CommandLog = commandLogService;
        ImportExportService = importExportService;
        PrivilegeState = privilegeService.Current;
        Execution = new ExecutionModeController(PrivilegeState);
        var localSnapshot = StorageSnapshot.Empty(Environment.MachineName);
        var local = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            $"local:{localSnapshot.Computer.StableId}",
            StorageSystemKind.Local,
            localSnapshot.Computer.Name,
            localSnapshot,
            HardwareInventoryReport.Empty(DateTimeOffset.MinValue),
            [],
            DateTimeOffset.MinValue);
        var simulatedSnapshot = SimulationStorageSnapshotFactory.Create();
        var simulation = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:builtin:desktop-pl96ukd-20260727-114130",
            StorageSystemKind.Simulation,
            "DESKTOP-PL96UKD",
            simulatedSnapshot,
            KsReferenceReportFactory.Create(),
            [],
            simulatedSnapshot.ScannedAt);
        SystemCatalog.ReplaceLocal(local);
        SystemCatalog.AddSimulation(simulation);
        SelectedSystem = simulation;
        Localization = new LocalizationService();
        _selectedCategory = WorkspaceCategory.System;
        RefreshLocalizedContent();
    }

    public LocalizationService Localization { get; }

    public IStorageSystemImportExportService ImportExportService { get; }

    public IGlobalNotificationService NotificationService => _notificationService;

    public MonitoringService Monitoring { get; } = new();

    public Action<StorageUnitRef, object>? NodeContextMenuRequested { get; set; }

    public ICommandLogService CommandLog { get; }

    public PrivilegeState PrivilegeState { get; }

    public ExecutionModeController Execution { get; }

    public bool CanUseRealMode => Execution.CanUseRealMode;

    public bool IsRealMode => Execution.Mode == ExecutionMode.Real;

    public StorageSystemCatalog SystemCatalog { get; } = new();

    public StorageSystemDocument SelectedSystem { get; private set; }

    public StorageSystemDocument ActiveDocument => SelectedSystem;

    public StorageSnapshot ActiveSnapshot => SelectedSystem.Snapshot;

    public StorageSnapshot Snapshot =>
        SystemCatalog.Systems.First(x => x.IsLocal).Snapshot;

    public StorageSnapshot SimulatedSnapshot =>
        SystemCatalog.Systems.First(x => !x.IsLocal).Snapshot;

    public bool IsUsingSimulatedInventory => SelectedSystem.Kind == StorageSystemKind.Simulation;

    public bool IsLocalSystem => SelectedSystem.Kind == StorageSystemKind.Local;

    public bool IsSelectedSystemLocalConsistent =>
        IsLocalSystem
        || (SelectedSystem.SourceHostName is not null
            && SelectedSystem.SourceHostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    public bool CanDeleteSelectedSimulation =>
        SelectedSystem.Kind == StorageSystemKind.Simulation
        && !SelectedSystem.Id.StartsWith("simulation:builtin", StringComparison.Ordinal);

    public async Task DeleteSimulationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDeleteSelectedSimulation)
        {
            return;
        }

        var id = SelectedSystem.Id;
        SystemCatalog.RemoveSimulation(id);
        await _systemRepository.DeleteSimulationAsync(id, cancellationToken);
        SelectedSystem = SystemCatalog.Systems.FirstOrDefault(x => !x.IsLocal)
            ?? SystemCatalog.Systems.First(x => x.IsLocal);
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        RebuildTopology();
        RebuildObjects(SelectedSystem.Id);
    }

    public async Task ConvertLocalToSimulationAsync(CancellationToken cancellationToken = default)
    {
        var local = SystemCatalog.Systems.First(x => x.IsLocal);
        var copy = StorageSystemDocumentSanitizer.RedactSensitiveData(local) with
        {
            Id = $"simulation:local-copy:{Guid.NewGuid():N}",
            Kind = StorageSystemKind.Simulation,
            DisplayName = $"{local.Snapshot.Computer.Name} {DateTime.Now:yyyy-MM-dd HH:mm}",
            SourceHostName = Environment.MachineName,
            Jobs = [],
            UpdatedAt = DateTimeOffset.Now
        };
        SystemCatalog.AddSimulation(copy);
        await _systemRepository.SaveSimulationAsync(copy, cancellationToken);
        SelectedSystem = copy;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        RebuildTopology();
        RebuildObjects(copy.Id);
    }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public ObservableCollection<WorkspaceItem> Objects { get; } = [];

    public ObservableCollection<DetailRow> Details { get; } = [];

    public ObservableCollection<ComparisonColumn> ComparisonColumns { get; } = [];

    [ObservableProperty]
    private ComparisonColumn? _selectedComparisonColumn;

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
        IsSelectedSystemLocalConsistent
        && ResolveDetailUnit()?.Kind == StorageUnitKind.Partition
        && ActiveSnapshot.Partitions.Any(x => x.StableId == ResolveDetailUnit()?.StableId);

    public bool HasRelatedTarget => GetPrimaryRelatedTarget() is not null;

    public string SelectedCategoryTitle =>
        Categories.FirstOrDefault(x => x.Category == SelectedCategory)?.Title ?? string.Empty;

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
        var related = _suppressRelatedSelection ? null : FindRelatedStableIdForCategory(value);
        RebuildObjects(related ?? _categorySelections.GetValueOrDefault(value));
        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == value);
        OnPropertyChanged(nameof(SelectedCategoryTitle));
    }

    private string? FindRelatedStableIdForCategory(WorkspaceCategory target)
    {
        var current = SelectedWorkspaceItem;
        if (current?.Unit is null || current.IsAction)
        {
            return null;
        }

        var unit = current.Unit;
        var snapshot = ActiveSnapshot;
        return target switch
        {
            WorkspaceCategory.System => SelectedSystem.Id,
            WorkspaceCategory.Pool => RelatedPoolId(unit, snapshot),
            WorkspaceCategory.Tier => RelatedTierId(unit, snapshot),
            WorkspaceCategory.Disk => RelatedDiskId(unit, snapshot),
            WorkspaceCategory.Partition => RelatedPartitionId(unit, snapshot),
            _ => null
        };
    }

    private string? RelatedPoolId(StorageUnitRef unit, StorageSnapshot snapshot)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.StoragePool:
            case StorageUnitKind.NetworkDiskGroup:
            case StorageUnitKind.OtherDiskGroup:
                return unit.StableId;
            case StorageUnitKind.StorageTier:
                return snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId;
            case StorageUnitKind.PhysicalDisk:
                return snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId
                    ?? snapshot.StoragePools.FirstOrDefault(x => x.IsPrimordial)?.StableId;
            case StorageUnitKind.VirtualDisk:
                return snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId)?.PoolStableId;
            case StorageUnitKind.NetworkDisk:
                return TopologyProjector.NetworkGroupStableId(snapshot);
            case StorageUnitKind.OsDisk:
                var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == unit.StableId);
                if (osDisk is null)
                {
                    return null;
                }
                if (osDisk.PhysicalDiskStableId is null && osDisk.VirtualDiskStableId is null)
                {
                    return TopologyProjector.OtherGroupStableId(snapshot);
                }
                var backing = osDisk.VirtualDiskStableId is not null
                    ? new StorageUnitRef(osDisk.VirtualDiskStableId, StorageUnitKind.VirtualDisk, string.Empty)
                    : new StorageUnitRef(osDisk.PhysicalDiskStableId!, StorageUnitKind.PhysicalDisk, string.Empty);
                return RelatedPoolId(backing, snapshot);
            case StorageUnitKind.Partition:
                var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId);
                var parentId = ResolvePartitionParent(partition);
                return parentId is null
                    ? null
                    : RelatedPoolId(new StorageUnitRef(parentId, StorageUnitKind.OsDisk, string.Empty), snapshot);
            default:
                return null;
        }
    }

    private string? RelatedTierId(StorageUnitRef unit, StorageSnapshot snapshot)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.StorageTier:
                return unit.StableId;
            case StorageUnitKind.StoragePool:
                return snapshot.StorageTiers.FirstOrDefault(x => x.PoolStableId == unit.StableId)?.StableId;
            case StorageUnitKind.PhysicalDisk:
                return snapshot.StorageTiers.FirstOrDefault(
                    x => x.MemberPhysicalDiskIds.Contains(unit.StableId, StringComparer.OrdinalIgnoreCase))?.StableId;
            case StorageUnitKind.VirtualDisk:
                return snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId)
                    ?.TierStableIds.FirstOrDefault();
            case StorageUnitKind.Partition:
                var diskId = RelatedDiskId(unit, snapshot);
                return diskId is null
                    ? null
                    : RelatedTierId(new StorageUnitRef(diskId, StorageUnitKind.VirtualDisk, string.Empty), snapshot);
            default:
                return null;
        }
    }

    private string? RelatedDiskId(StorageUnitRef unit, StorageSnapshot snapshot)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.PhysicalDisk:
            case StorageUnitKind.VirtualDisk:
            case StorageUnitKind.NetworkDisk:
            case StorageUnitKind.OsDisk:
                return unit.StableId;
            case StorageUnitKind.Partition:
                return ResolvePartitionParent(snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId));
            case StorageUnitKind.StorageTier:
                return snapshot.StorageTiers.FirstOrDefault(x => x.StableId == unit.StableId)
                    ?.MemberPhysicalDiskIds.FirstOrDefault();
            case StorageUnitKind.StoragePool:
                var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId);
                return pool?.MemberPhysicalDiskIds.FirstOrDefault()
                    ?? snapshot.VirtualDisks.FirstOrDefault(x => x.PoolStableId == unit.StableId)?.StableId;
            case StorageUnitKind.NetworkDiskGroup:
                return snapshot.NetworkDisks.FirstOrDefault()?.StableId;
            case StorageUnitKind.OtherDiskGroup:
                return TopologyProjector.GetOtherOsDisks(snapshot).FirstOrDefault()?.StableId;
            default:
                return null;
        }
    }

    private string? RelatedPartitionId(StorageUnitRef unit, StorageSnapshot snapshot)
    {
        switch (unit.Kind)
        {
            case StorageUnitKind.Partition:
            case StorageUnitKind.NetworkDisk:
                return unit.StableId;
            case StorageUnitKind.PhysicalDisk:
                return FindFirstPartitionForPhysicalDisk(unit.StableId);
            case StorageUnitKind.VirtualDisk:
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.VirtualDiskStableId == unit.StableId)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return snapshot.Partitions
                    .FirstOrDefault(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId))
                    ?.StableId;
            case StorageUnitKind.OsDisk:
                return snapshot.Partitions.FirstOrDefault(x => x.OsDiskStableId == unit.StableId)?.StableId;
            case StorageUnitKind.StoragePool:
            case StorageUnitKind.StorageTier:
            case StorageUnitKind.NetworkDiskGroup:
            case StorageUnitKind.OtherDiskGroup:
                var diskId = RelatedDiskId(unit, snapshot);
                if (diskId is null)
                {
                    return null;
                }
                var kind = snapshot.VirtualDisks.Any(x => x.StableId == diskId)
                    ? StorageUnitKind.VirtualDisk
                    : snapshot.NetworkDisks.Any(x => x.StableId == diskId)
                        ? StorageUnitKind.NetworkDisk
                        : snapshot.OsDisks.Any(x => x.StableId == diskId)
                          && snapshot.PhysicalDisks.All(x => x.StableId != diskId)
                            ? StorageUnitKind.OsDisk
                            : StorageUnitKind.PhysicalDisk;
                return RelatedPartitionId(new StorageUnitRef(diskId, kind, string.Empty), snapshot);
            default:
                return null;
        }
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
            RebuildComparisonColumns();
            RefreshTopologySelection();
            NotifySelectionState();
            WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SelectedCategory == WorkspaceCategory.System && value?.Unit?.Kind == StorageUnitKind.System)
        {
            SwitchSystem(value.StorageSystemId);
        }
        SelectedStableId = value?.Unit?.StableId;
        SelectedTopologyStableId = SelectedStableId;
        if (value?.Unit is not null)
        {
            _categorySelections[SelectedCategory] = value.Key;
        }
        BuildDetails();
        RebuildComparisonColumns();
        RefreshTopologySelection();
        ExpandSelectedTopologyPath();
        NotifySelectionState();
        WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedComparisonColumnChanged(ComparisonColumn? value)
    {
        if (_updatingComparisonSelection || value is null)
        {
            return;
        }
        var item = Objects.FirstOrDefault(x => x.Key == value.Key);
        if (item is not null && !ReferenceEquals(item, SelectedWorkspaceItem))
        {
            SelectedWorkspaceItem = item;
        }
    }

    partial void OnSelectedTopologyStableIdChanged(string? value) => RefreshTopologySelection();

    partial void OnScanErrorChanged(string value) => OnPropertyChanged(nameof(HasScanError));

    public async Task InitializeAsync()
    {
        var preferences = await _preferencesService.LoadAsync();
        Localization.Language = preferences.Language;
        CurrentPreferences = preferences;
        var cachedLocal = await _machineRecordService.LoadLocalScanAsync();
        if (cachedLocal is not null)
        {
            SystemCatalog.ReplaceLocal(cachedLocal);
            OnPropertyChanged(nameof(Snapshot));
        }
        var persisted = (await _systemRepository.LoadSimulationsAsync()).ToList();
        var builtinIndex = persisted.FindIndex(
            x => x.Id.StartsWith("simulation:builtin:", StringComparison.Ordinal));
        if (builtinIndex >= 0
            && (persisted[builtinIndex].Snapshot.Computer.Name != SimulationStorageSnapshotFactory.SimulatedComputerName
                || persisted[builtinIndex].Snapshot.SnapshotVersion != SimulationStorageSnapshotFactory.SimulatedSnapshotVersion))
        {
            persisted[builtinIndex] = persisted[builtinIndex] with
            {
                Snapshot = SimulationStorageSnapshotFactory.Create(),
                HardwareReport = KsReferenceReportFactory.Create(),
                Jobs = [],
                UpdatedAt = DateTimeOffset.Now
            };
            await _systemRepository.SaveSimulationAsync(persisted[builtinIndex]);
        }
        if (persisted.Count > 0)
        {
            var selectedId = SelectedSystem.Id;
            SystemCatalog.ReplaceSimulations(persisted);
            SelectedSystem = SystemCatalog.Find(selectedId)
                ?? SystemCatalog.Systems.First(x => !x.IsLocal);
        }
        else
        {
            await _systemRepository.SaveSimulationAsync(SelectedSystem);
        }
        RestoredUiState = await _workspaceStateService.LoadAsync();
        if (RestoredUiState is not null)
        {
            if (!string.IsNullOrWhiteSpace(RestoredUiState.ActiveSystemId))
            {
                SwitchSystem(RestoredUiState.ActiveSystemId);
            }
            if (RestoredUiState.CategorySelections is not null)
            {
                foreach (var pair in RestoredUiState.CategorySelections)
                {
                    _categorySelections[pair.Key] = pair.Value;
                }
            }
            SelectedCategory = RestoredUiState.Category;
        }
        RefreshLocalizedContent();
    }

    public WorkspaceUiState? RestoredUiState { get; private set; }

    public WorkspaceUiState CaptureUiState(string shellPage) =>
        new(
            shellPage,
            SelectedSystem.Id,
            SelectedCategory,
            new Dictionary<WorkspaceCategory, string>(_categorySelections));

    public UserPreferences CurrentPreferences { get; private set; } = new();

    public double TopologyHorizontalOffset { get; set; }

    public double TopologyVerticalOffset { get; set; }

    public bool AutoScanAttempted { get; set; }

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

    public async Task SetShowHardwareIdsAsync(bool show)
    {
        CurrentPreferences = CurrentPreferences with { ShowHardwareIds = show };
        BuildDetails();
        RebuildComparisonColumns();
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetCreateMsrOnInitializeAsync(bool create)
    {
        CurrentPreferences = CurrentPreferences with { CreateMsrOnInitialize = create };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetShowWelcomeAtStartAsync(bool show)
    {
        CurrentPreferences = CurrentPreferences with { ShowWelcomeAtStart = show };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public string FormatSerial(string? serial) =>
        CurrentPreferences.ShowHardwareIds
            ? (string.IsNullOrWhiteSpace(serial) ? "—" : serial)
            : StableId.MaskSerial(serial);

    public async Task<string?> ExportActiveSystemAsync(
        CancellationToken cancellationToken = default) =>
        await ImportExportService.ExportAsync(ActiveDocument, cancellationToken);

    public async Task<bool> ImportSystemAsync(CancellationToken cancellationToken = default)
    {
        var imported = await ImportExportService.ImportAsync(cancellationToken);
        if (imported is null)
        {
            return false;
        }
        SystemCatalog.AddSimulation(imported);
        await _systemRepository.SaveSimulationAsync(imported, cancellationToken);
        SelectedSystem = imported;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        RebuildTopology();
        RebuildObjects(imported.Id);
        return true;
    }

    public async Task<SimulationOperationResult> ApplySimulationOperationAsync(
        SimulationOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = _simulationOperations.Apply(ActiveDocument, request);
        if (!result.Succeeded)
        {
            return result;
        }
        foreach (var command in result.Commands)
        {
            CommandLog.Log("simulation", command, "OK", simulated: true);
        }
        SelectedSystem = result.Document;
        SystemCatalog.Update(result.Document);
        await _systemRepository.SaveSimulationAsync(result.Document, cancellationToken);
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        RebuildTopology();
        RebuildObjects(request.TargetStableId);
        BuildDetails();
        RebuildComparisonColumns();
        return result;
    }

    public async Task ResetActiveSimulationAsync()
    {
        if (!SelectedSystem.Id.StartsWith("simulation:builtin", StringComparison.Ordinal))
        {
            return;
        }
        var snapshot = SimulationStorageSnapshotFactory.Create();
        var document = SelectedSystem with
        {
            Snapshot = snapshot,
            HardwareReport = KsReferenceReportFactory.Create(),
            Jobs = [],
            UpdatedAt = DateTimeOffset.Now
        };
        SelectedSystem = document;
        SystemCatalog.Update(document);
        await _systemRepository.SaveSimulationAsync(document);
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        RebuildTopology();
        RebuildObjects(null);
        BuildDetails();
        RebuildComparisonColumns();
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
        _notificationService.DismissByKey(ScanningNotificationKey);
        _notificationService.PublishInfo(
            Localization["Scanning"],
            string.Empty,
            "inventory",
            ScanningNotificationKey,
            autoDismiss: false);
        var previous = new WorkspaceSelection(SelectedCategory, SelectedStableId, _contextUnit?.Kind, _contextUnit?.StableId);
        var previousTopologyStableId = SelectedTopologyStableId;
        try
        {
            var localDocument = await _hardwareInventoryProvider.CollectLocalAsync(CancellationToken.None);
            var snapshot = localDocument.Snapshot;
            CommandLog.Log(
                "inventory",
                "WinPool read-only inventory (embedded PowerShell)",
                $"{snapshot.PhysicalDisks.Count} disks, {snapshot.StoragePools.Count} pools, {snapshot.Partitions.Count} partitions",
                simulated: false);
            try
            {
                await _machineRecordService.RecordLocalScanAsync(localDocument, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            SystemCatalog.ReplaceLocal(localDocument);
            OnPropertyChanged(nameof(ActiveSnapshot));
            OnPropertyChanged(nameof(ActiveDocument));
            OnPropertyChanged(nameof(Snapshot));
            if (!IsLocalSystem)
            {
                RebuildTopology();
            }
            else
            {
                SelectedSystem = localDocument;
                var restored = WorkspaceState.Restore(snapshot, previous);
                SelectedCategory = restored.Category;
                RebuildTopology();
                RebuildObjects(restored.StableId);
                SelectedTopologyStableId = snapshot.FindUnit(previousTopologyStableId ?? string.Empty) is null
                    ? null
                    : previousTopologyStableId;
            }
            StatusMessage = $"{Localization["LastScan"]}: {snapshot.ScannedAt.LocalDateTime:G}";
            _notificationService.DismissByKey(ScanningNotificationKey);
            _notificationService.PublishInfo(
                Localization["ScanComplete"],
                StatusMessage,
                "inventory",
                $"inventory:scan-complete:{snapshot.ScannedAt.UtcTicks}");
            foreach (var warning in snapshot.Warnings)
            {
                _notificationService.PublishWarning(
                    Localization["Warning"],
                    warning.Message,
                    "inventory",
                    $"inventory:{snapshot.ScannedAt.UtcTicks}:{warning.Code}:{warning.StableId}");
            }
            PublishStorageFindings(snapshot);
            BuildDetails();
            RebuildComparisonColumns();
        }
        catch (Exception ex)
        {
            ScanError = $"{Localization["ScanFailed"]} {ex.Message}";
            StatusMessage = ScanError;
            _notificationService.DismissByKey(ScanningNotificationKey);
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

        var sourceDocument = SystemCatalog.Systems.FirstOrDefault(
            x => ReferenceEquals(x.Snapshot, sourceSnapshot))
            ?? SystemCatalog.Systems.FirstOrDefault(
                x => x.Snapshot.SnapshotVersion == sourceSnapshot.SnapshotVersion);
        var switchedInventory = SwitchSystem(sourceDocument?.Id);
        var selection = unit.Kind == StorageUnitKind.System && sourceDocument is not null
            ? new WorkspaceSelection(WorkspaceCategory.System, sourceDocument.Id)
            : WorkspaceMapper.FromUnit(unit, sourceSnapshot);
        if (selection.StableId is not null)
        {
            _categorySelections[selection.Category] = selection.StableId;
        }
        if (SelectedCategory != selection.Category)
        {
            _suppressRelatedSelection = true;
            SelectedCategory = selection.Category;
            _suppressRelatedSelection = false;
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
        OnPropertyChanged(nameof(SelectedCategoryTitle));
        RebuildObjects(_categorySelections.GetValueOrDefault(SelectedCategory) ?? SelectedStableId);
        RebuildTopology();
        if (preserveTopologySelection)
        {
            SelectedTopologyStableId = topologyStableId is not null
                && SystemCatalog.Systems.Any(system =>
                    system.Id == topologyStableId
                    || system.Snapshot.FindUnit(topologyStableId) is not null)
                    ? topologyStableId
                    : null;
        }
        BuildDetails();
    }

    private void PublishStorageFindings(StorageSnapshot snapshot)
    {
        var zh = Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        foreach (var finding in StorageFindingInspector.Evaluate(snapshot))
        {
            if (!_shownFindings.Add($"{finding.Kind}:{finding.TargetStableId}"))
            {
                continue;
            }
            var (title, message) = finding.Kind switch
            {
                StorageFindingKind.MultiplePerformanceTiers => (
                    zh ? "存储池布局建议" : "Pool layout suggestion",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个容量不为 0 的性能层。推荐一个存储池只保留 1 个性能层、1 个容量层、1 个虚拟磁盘，避免混淆。"
                        : $"Pool {finding.TargetName} has more than one non-empty performance tier. Keeping one performance tier, one capacity tier, and one virtual disk per pool is recommended."),
                StorageFindingKind.MultipleCapacityTiers => (
                    zh ? "存储池布局建议" : "Pool layout suggestion",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个容量不为 0 的容量层。推荐一个存储池只保留 1 个性能层、1 个容量层、1 个虚拟磁盘，避免混淆。"
                        : $"Pool {finding.TargetName} has more than one non-empty capacity tier. Keeping one performance tier, one capacity tier, and one virtual disk per pool is recommended."),
                StorageFindingKind.MultipleVirtualDisks => (
                    zh ? "存储池布局建议" : "Pool layout suggestion",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个虚拟磁盘。推荐一个存储池只保留 1 个性能层、1 个容量层、1 个虚拟磁盘，避免混淆。"
                        : $"Pool {finding.TargetName} has more than one virtual disk. Keeping one performance tier, one capacity tier, and one virtual disk per pool is recommended."),
                StorageFindingKind.LegacyDynamicVolume => (
                    zh ? "老旧卷类型" : "Legacy volume type",
                    zh
                        ? $"检测到动态磁盘卷（{finding.TargetName}）。这些老旧功能已被弃用，您仍可以进行管理，但不支持创建，建议您迁移到存储空间。如果您有任何问题，可以反馈。"
                        : $"A legacy dynamic-disk volume ({finding.TargetName}) was detected. These deprecated layouts can still be managed but not created; migrating to Storage Spaces is recommended. Feedback is welcome."),
                StorageFindingKind.MbrDisk => (
                    zh ? "MBR 磁盘" : "MBR disk",
                    zh
                        ? $"检测到 MBR 磁盘（{finding.TargetName}）。MBR 已不再受支持，您仍可以进行管理，但不支持创建，建议您迁移到 GPT。如果您仍要求创建，请使用开发板块。如果您有任何问题，可以反馈。"
                        : $"An MBR disk ({finding.TargetName}) was detected. MBR is no longer supported; it can still be managed but not created. Migrate to GPT, or use the Development area if creation is strictly required. Feedback is welcome."),
                _ => (Localization["Warning"], finding.TargetName)
            };
            _notificationService.PublishWarning(
                title,
                message,
                "storage-finding",
                $"storage-finding:{finding.Kind}:{finding.TargetStableId}");
        }
    }

    private void RebuildTopology()
    {
        TopologySystems.Clear();
        foreach (var system in SystemCatalog.Systems)
        {
            TopologySystems.Add(CreateTopologyRoot(system));
        }
        ApplySystemRootExpansion();
        RefreshTopologySelection();
    }

    private TopologyNodeViewModel CreateTopologyRoot(StorageSystemDocument document)
    {
        var projected = TopologyProjector.Project(document.Snapshot);
        var prefix = document.IsLocal
            ? (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]")
            : (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]");
        var root = new TopologyNode(
            new StorageUnitRef(document.Id, StorageUnitKind.System, $"{prefix} {document.DisplayName}"),
            projected.Summary,
            projected.Children,
            projected.IsReference,
            projected.IsExpanded,
            projected.IsSelectable,
            projected.ChildrenLayout,
            projected.LayoutWeight);
        return new TopologyNodeViewModel(
            root,
            this,
            document.Snapshot,
            $"{document.Id}:root");
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
                foreach (var system in SystemCatalog.Systems)
                {
                    var prefix = system.IsLocal
                        ? (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]")
                        : (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]");
                    yield return new WorkspaceItem(
                        system.Id,
                        $"{prefix} {system.DisplayName}",
                        new StorageUnitRef(system.Id, StorageUnitKind.System, system.DisplayName),
                        false,
                        system.Id);
                }
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
                foreach (var disk in OrderPhysicalDisks(ActiveSnapshot))
                {
                    yield return new WorkspaceItem(
                        disk.StableId,
                        disk.FriendlyName,
                        new StorageUnitRef(disk.StableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
                        false);
                }
                foreach (var virtualDisk in OrderVirtualDisks(ActiveSnapshot))
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
                foreach (var osDisk in ActiveSnapshot.OsDisks
                             .Where(x => string.IsNullOrWhiteSpace(x.PhysicalDiskStableId)
                                         && string.IsNullOrWhiteSpace(x.VirtualDiskStableId))
                             .OrderBy(x => x.Number))
                {
                    yield return new WorkspaceItem(
                        osDisk.StableId,
                        osDisk.FriendlyName,
                        new StorageUnitRef(osDisk.StableId, StorageUnitKind.OsDisk, osDisk.FriendlyName),
                        false);
                }
                break;
            case WorkspaceCategory.Partition:
                foreach (var partition in OrderPartitionsByDiskOrder(ActiveSnapshot))
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

    private (Dictionary<string, int> PoolOrder, Dictionary<string, int> TierOrder) BuildDiskRankOrders(
        StorageSnapshot snapshot)
    {
        var poolOrder = snapshot.StoragePools
            .OrderByDescending(x => x.IsPrimordial)
            .ThenBy(x => x.FriendlyName)
            .Select((x, index) => (x.StableId, Index: index))
            .ToDictionary(x => x.StableId, x => x.Index, StringComparer.OrdinalIgnoreCase);
        var tierOrder = snapshot.StorageTiers
            .OrderBy(x => x.MediaType is "SSD" or "SCM" ? 0 : 1)
            .ThenBy(x => x.FriendlyName)
            .Select((x, index) => (x.StableId, Index: index))
            .ToDictionary(x => x.StableId, x => x.Index, StringComparer.OrdinalIgnoreCase);
        return (poolOrder, tierOrder);
    }

    private IReadOnlyList<PhysicalDiskInfo> OrderPhysicalDisks(StorageSnapshot snapshot)
    {
        var (poolOrder, tierOrder) = BuildDiskRankOrders(snapshot);
        int PoolRank(string? poolId) =>
            poolId is not null && poolOrder.TryGetValue(poolId, out var rank) ? rank : poolOrder.Count;
        int TierRank(string diskId) =>
            snapshot.StorageTiers
                .Where(x => x.MemberPhysicalDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase))
                .Select(x => tierOrder.GetValueOrDefault(x.StableId, int.MaxValue))
                .DefaultIfEmpty(int.MaxValue)
                .Min();
        return snapshot.PhysicalDisks
            .OrderBy(x => PoolRank(x.PoolStableId))
            .ThenBy(x => TierRank(x.StableId))
            .ThenBy(x => x.DeviceId ?? int.MaxValue)
            .ThenBy(x => x.FriendlyName)
            .ToList();
    }

    private IReadOnlyList<VirtualDiskInfo> OrderVirtualDisks(StorageSnapshot snapshot)
    {
        var (poolOrder, _) = BuildDiskRankOrders(snapshot);
        return snapshot.VirtualDisks
            .OrderBy(x => x.PoolStableId is not null && poolOrder.TryGetValue(x.PoolStableId, out var rank)
                ? rank
                : poolOrder.Count)
            .ThenBy(x => x.OsDiskNumbers.Count > 0 ? x.OsDiskNumbers[0] : int.MaxValue)
            .ThenBy(x => x.FriendlyName)
            .ToList();
    }

    private IReadOnlyList<PartitionInfo> OrderPartitionsByDiskOrder(StorageSnapshot snapshot)
    {
        var backingOrder = OrderPhysicalDisks(snapshot).Select(x => x.StableId)
            .Concat(OrderVirtualDisks(snapshot).Select(x => x.StableId))
            .Concat(TopologyProjector.GetOtherOsDisks(snapshot)
                .OrderBy(x => x.Number)
                .Select(x => x.StableId))
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);
        int Rank(PartitionInfo partition)
        {
            var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
            var backing = osDisk?.VirtualDiskStableId ?? osDisk?.PhysicalDiskStableId ?? osDisk?.StableId;
            return backing is not null && backingOrder.TryGetValue(backing, out var rank)
                ? rank
                : int.MaxValue;
        }
        return snapshot.Partitions
            .OrderBy(Rank)
            .ThenBy(x => x.PartitionNumber)
            .ToList();
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
                Details.Add(new DetailRow(Localization["Serial"], FormatSerial(disk.MaskedSerialNumber)));
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

    private void RebuildComparisonColumns()
    {
        ComparisonColumns.Clear();
        foreach (var item in Objects.Where(x => !x.IsAction && x.Unit is not null))
        {
            var document = item.StorageSystemId is null
                ? ActiveDocument
                : SystemCatalog.Find(item.StorageSystemId) ?? ActiveDocument;
            ComparisonColumns.Add(new ComparisonColumn(
                item.Key,
                item.Title,
                BuildComparisonRows(item.Unit!, document)));
        }

        _updatingComparisonSelection = true;
        SelectedComparisonColumn = ComparisonColumns.FirstOrDefault(
            x => x.Key == SelectedWorkspaceItem?.Key);
        _updatingComparisonSelection = false;
    }

    private IReadOnlyList<DetailRow> BuildComparisonRows(
        StorageUnitRef unit,
        StorageSystemDocument document)
    {
        var snapshot = document.Snapshot;
        var rows = new List<DetailRow>();
        switch (unit.Kind)
        {
            case StorageUnitKind.System:
            {
                var uniquePhysical = snapshot.PhysicalDisks
                    .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rows.Add(new DetailRow(Localization["HostName"], snapshot.Computer.Name));
                rows.Add(new DetailRow(Localization["Version"], ProductDisplayName(snapshot.Computer.WindowsProductName)));
                rows.Add(new DetailRow(Localization["VersionNumber"], snapshot.Computer.DisplayVersion));
                rows.Add(new DetailRow(
                    Localization["OsBuild"],
                    string.IsNullOrWhiteSpace(snapshot.Computer.Ubr)
                        ? snapshot.Computer.OsBuild
                        : $"{snapshot.Computer.OsBuild}.{snapshot.Computer.Ubr}"));
                rows.Add(new DetailRow(Localization["Cpu"], ReportValue(document, "0401") ?? string.Empty));
                rows.Add(new DetailRow(Localization["Memory"], ReportMemory(document)));
                rows.Add(new DetailRow(
                    Localization["LocalStorage"],
                    TopologyProjector.FormatBytes(uniquePhysical.Sum(x => x.Size))));
                if (snapshot.NetworkDisks.Count > 0)
                {
                    rows.Add(new DetailRow(
                        Localization["ExternalStorage"],
                        TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size))));
                }
                rows.Add(new DetailRow(Localization["StoragePool"], snapshot.StoragePools.Count.ToString()));
                rows.Add(new DetailRow(Localization["PhysicalDisk"], uniquePhysical.Count.ToString()));
                if (snapshot.VirtualDisks.Count > 0)
                {
                    rows.Add(new DetailRow(Localization["VirtualDisk"], snapshot.VirtualDisks.Count.ToString()));
                }
                rows.Add(new DetailRow(Localization["Partition"], snapshot.Partitions.Count.ToString()));
                rows.Add(new DetailRow(
                    Localization["AccessibleVolumes"],
                    (snapshot.Partitions.Count(x => !string.IsNullOrWhiteSpace(x.Path))
                        + snapshot.NetworkDisks.Count(x => !string.IsNullOrWhiteSpace(x.DriveLetter))).ToString()));
                break;
            }
            case StorageUnitKind.StoragePool:
            {
                var pool = snapshot.StoragePools.First(x => x.StableId == unit.StableId);
                var poolVirtualDisks = snapshot.VirtualDisks
                    .Where(x => x.PoolStableId == pool.StableId)
                    .ToList();
                var poolTiers = snapshot.StorageTiers
                    .Where(x => x.PoolStableId == pool.StableId)
                    .ToList();
                var members = snapshot.PhysicalDisks
                    .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                    .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rows.Add(new DetailRow(
                    Localization["Type"],
                    pool.IsPrimordial ? Localization["OriginalPool"] : Localization["StoragePool"]));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(pool.Size)));
                rows.Add(new DetailRow(Localization["PhysicalDisk"], members.Count.ToString()));
                rows.Add(new DetailRow(Localization["VirtualDisk"], poolVirtualDisks.Count.ToString()));
                rows.Add(new DetailRow(Localization["RunningStatus"], Empty(pool.OperationalStatus)));
                rows.Add(new DetailRow(Localization["Health"], Empty(pool.HealthStatus)));
                rows.Add(new DetailRow(
                    Localization["ProvisioningType"],
                    FirstNonEmpty(
                        pool.ProvisioningTypeDefault,
                        string.Join(", ", poolVirtualDisks.Select(x => x.ProvisioningType).Distinct()))));
                rows.Add(new DetailRow(
                    Localization["Resiliency"],
                    FirstNonEmpty(string.Join(
                        ", ",
                        poolVirtualDisks.Select(x => x.ResiliencySettingName).Distinct()))));
                rows.Add(new DetailRow(
                    Localization["PhysicalSector"],
                    pool.PhysicalSectorSize is > 0
                        ? TopologyProjector.FormatBytes(pool.PhysicalSectorSize.Value)
                        : FirstNonEmpty(string.Join(
                            ", ",
                            members.Select(x => TopologyProjector.FormatBytes(x.PhysicalSectorSize)).Distinct()))));
                rows.Add(new DetailRow(
                    Localization["LogicalSector"],
                    pool.LogicalSectorSize is > 0
                        ? TopologyProjector.FormatBytes(pool.LogicalSectorSize.Value)
                        : FirstNonEmpty(string.Join(
                            ", ",
                            members.Select(x => TopologyProjector.FormatBytes(x.LogicalSectorSize)).Distinct()))));
                rows.Add(new DetailRow(
                    Localization["PerformanceTier"],
                    TierNames(poolTiers, media => media is "SSD" or "SCM")));
                rows.Add(new DetailRow(
                    Localization["CapacityTier"],
                    TierNames(poolTiers, media => media == "HDD")));
                break;
            }
            case StorageUnitKind.StorageTier:
            {
                var tier = snapshot.StorageTiers.First(x => x.StableId == unit.StableId);
                var virtualDisk = snapshot.VirtualDisks.FirstOrDefault(
                    x => x.StableId == tier.VirtualDiskStableId);
                rows.Add(new DetailRow(
                    Localization["PoolOwner"],
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == tier.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(new DetailRow(Localization["Media"], Empty(tier.MediaType)));
                rows.Add(new DetailRow(
                    Localization["Type"],
                    tier.MediaType is "SSD" or "SCM"
                        ? Localization["PerformanceTier"]
                        : tier.MediaType == "HDD"
                            ? Localization["CapacityTier"]
                            : Localization["StorageTier"]));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(tier.Size)));
                rows.Add(new DetailRow(
                    Localization["ProvisioningType"],
                    FirstNonEmpty(virtualDisk?.ProvisioningType ?? string.Empty)));
                rows.Add(new DetailRow(Localization["Resiliency"], Empty(tier.ResiliencySettingName)));
                rows.Add(new DetailRow(
                    Localization["FaultTolerance"],
                    tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
                        ? "0"
                        : tier.ResiliencySettingName.Equals("Parity", StringComparison.OrdinalIgnoreCase)
                            ? "1"
                            : string.Empty));
                rows.Add(new DetailRow(Localization["PhysicalDisk"], tier.MemberPhysicalDiskIds.Count.ToString()));
                rows.Add(new DetailRow(
                    Localization["Columns"],
                    (tier.NumberOfColumns ?? virtualDisk?.NumberOfColumns)?.ToString() ?? string.Empty));
                rows.Add(new DetailRow(
                    Localization["Interleave"],
                    (tier.Interleave ?? virtualDisk?.Interleave) is { } interleave
                        ? TopologyProjector.FormatBytes(interleave)
                        : string.Empty));
                rows.Add(new DetailRow(Localization["AllocationUnit"], string.Empty));
                break;
            }
            case StorageUnitKind.PhysicalDisk:
            {
                var physical = snapshot.PhysicalDisks.First(x => x.StableId == unit.StableId);
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.PhysicalDiskStableId == physical.StableId)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var partitionStyle = snapshot.OsDisks
                    .FirstOrDefault(x => x.PhysicalDiskStableId == physical.StableId)
                    ?.PartitionStyle;
                rows.Add(new DetailRow(Localization["DiskNumber"], physical.DeviceId?.ToString() ?? string.Empty));
                rows.Add(new DetailRow(
                    Localization["PoolOwner"],
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == physical.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(new DetailRow(Localization["Media"], Empty(physical.MediaType)));
                rows.Add(new DetailRow(Localization["PartitionTable"], Empty(partitionStyle ?? string.Empty)));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(physical.Size)));
                rows.Add(new DetailRow(
                    Localization["Partition"],
                    snapshot.Partitions.Count(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId)).ToString()));
                rows.Add(new DetailRow(Localization["RunningStatus"], Empty(physical.OperationalStatus)));
                rows.Add(new DetailRow(Localization["Health"], Empty(physical.HealthStatus)));
                rows.Add(new DetailRow(Localization["LogicalSector"], TopologyProjector.FormatBytes(physical.LogicalSectorSize)));
                rows.Add(new DetailRow(Localization["PhysicalSector"], TopologyProjector.FormatBytes(physical.PhysicalSectorSize)));
                rows.Add(new DetailRow(Localization["Model"], Empty(physical.Model)));
                rows.Add(new DetailRow(
                    Localization["Serial"],
                    string.IsNullOrWhiteSpace(physical.MaskedSerialNumber) || physical.MaskedSerialNumber == "—"
                        ? string.Empty
                        : FormatSerial(physical.MaskedSerialNumber)));
                rows.Add(new DetailRow(Localization["Firmware"], Empty(physical.FirmwareVersion)));
                rows.Add(new DetailRow(Localization["Bus"], Empty(physical.BusType)));
                rows.Add(new DetailRow(Localization["InterfaceType"], Empty(physical.InterfaceType)));
                rows.Add(new DetailRow(Localization["ProvisioningType"], Empty(physical.ProvisioningType)));
                break;
            }
            case StorageUnitKind.VirtualDisk:
            {
                var virtualDisk = snapshot.VirtualDisks.First(x => x.StableId == unit.StableId);
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.VirtualDiskStableId == virtualDisk.StableId)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.Add(new DetailRow(
                    Localization["DiskNumber"],
                    virtualDisk.OsDiskNumbers.Count > 0 ? virtualDisk.OsDiskNumbers[0].ToString() : string.Empty));
                rows.Add(new DetailRow(
                    Localization["PoolOwner"],
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == virtualDisk.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(new DetailRow(
                    Localization["PartitionTable"],
                    Empty(snapshot.OsDisks.FirstOrDefault(x => x.VirtualDiskStableId == virtualDisk.StableId)?.PartitionStyle ?? string.Empty)));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(virtualDisk.Size)));
                rows.Add(new DetailRow(
                    Localization["Partition"],
                    snapshot.Partitions.Count(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId)).ToString()));
                rows.Add(new DetailRow(Localization["RunningStatus"], Empty(virtualDisk.OperationalStatus)));
                rows.Add(new DetailRow(Localization["Health"], Empty(virtualDisk.HealthStatus)));
                rows.Add(new DetailRow(Localization["ProvisioningType"], Empty(virtualDisk.ProvisioningType)));
                break;
            }
            case StorageUnitKind.OsDisk:
            {
                var osDisk = snapshot.OsDisks.First(x => x.StableId == unit.StableId);
                rows.Add(new DetailRow(Localization["DiskNumber"], osDisk.Number.ToString()));
                rows.Add(new DetailRow(Localization["PartitionTable"], Empty(osDisk.PartitionStyle)));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(osDisk.Size)));
                rows.Add(new DetailRow(
                    Localization["Partition"],
                    snapshot.Partitions.Count(x => x.OsDiskStableId == osDisk.StableId).ToString()));
                rows.Add(new DetailRow(
                    Localization["RunningStatus"],
                    osDisk.IsOffline ? Localization["Offline"] : Localization["Online"]));
                break;
            }
            case StorageUnitKind.Partition:
            {
                var partition = snapshot.Partitions.First(x => x.StableId == unit.StableId);
                rows.Add(new DetailRow(Localization["OwningDisk"], PartitionOwnerName(snapshot, partition)));
                rows.Add(new DetailRow(Localization["Type"], PartitionTypeName(partition.Type)));
                rows.Add(new DetailRow(
                    Localization["FileSystem"],
                    string.IsNullOrWhiteSpace(partition.FileSystem) ? string.Empty : partition.FileSystem));
                rows.Add(new DetailRow(
                    Localization["AllocationUnit"],
                    partition.AllocationUnitSize is null ? string.Empty : TopologyProjector.FormatBytes(partition.AllocationUnitSize.Value)));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(partition.Size)));
                rows.Add(new DetailRow(
                    Localization["Available"],
                    string.IsNullOrWhiteSpace(partition.FileSystem)
                        ? string.Empty
                        : TopologyProjector.FormatBytes(partition.SizeRemaining)));
                rows.Add(new DetailRow(
                    Localization["SystemPartition"],
                    partition.IsBoot || partition.IsSystem ? "✓" : string.Empty));
                rows.Add(new DetailRow(Localization["PartitionStatus"], Empty(partition.OperationalStatus)));
                rows.Add(new DetailRow(Localization["StartOffset"], TopologyProjector.FormatBytes(partition.Offset)));
                rows.Add(new DetailRow(
                    Localization["DriveLetter"],
                    Empty(TopologyProjector.NormalizeDriveLetter(partition.DriveLetter))));
                rows.Add(new DetailRow(Localization["VolumeLabel"], Empty(partition.FileSystemLabel.Replace('\0', ' ').Trim())));
                rows.Add(new DetailRow(Localization["Path"], string.IsNullOrWhiteSpace(partition.Path) ? string.Empty : partition.Path));
                break;
            }
            case StorageUnitKind.NetworkDisk:
            {
                var network = snapshot.NetworkDisks.First(x => x.StableId == unit.StableId);
                rows.Add(new DetailRow(Localization["FileSystem"], Empty(network.FileSystem)));
                rows.Add(new DetailRow(Localization["Capacity"], TopologyProjector.FormatBytes(network.Size)));
                rows.Add(new DetailRow(Localization["Available"], TopologyProjector.FormatBytes(network.SizeRemaining)));
                rows.Add(new DetailRow(Localization["DriveLetter"], Empty(TopologyProjector.NormalizeDriveLetter(network.DriveLetter))));
                rows.Add(new DetailRow(Localization["Path"], Empty(network.ProviderPath)));
                break;
            }
            default:
                rows.Add(new DetailRow(Localization["Type"], KindName(unit.Kind)));
                break;
        }
        return rows;
    }

    private static string TierNames(
        IReadOnlyList<StorageTierInfo> tiers,
        Func<string, bool> mediaPredicate)
    {
        var names = tiers
            .Where(x => mediaPredicate(x.MediaType))
            .Select(x => x.FriendlyName)
            .ToList();
        return names.Count == 0 ? string.Empty : string.Join(", ", names);
    }

    private string PartitionOwnerName(StorageSnapshot snapshot, PartitionInfo partition)
    {
        var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
        if (osDisk is null)
        {
            return string.Empty;
        }
        if (osDisk.VirtualDiskStableId is not null)
        {
            return snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == osDisk.VirtualDiskStableId)?.FriendlyName ?? string.Empty;
        }
        if (osDisk.PhysicalDiskStableId is not null)
        {
            return snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == osDisk.PhysicalDiskStableId)?.FriendlyName ?? string.Empty;
        }
        return osDisk.FriendlyName;
    }

    private static string ProductDisplayName(string productName)
    {
        var trimmed = productName.Trim();
        return trimmed.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Microsoft ".Length..]
            : trimmed;
    }

    private static string Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string? ReportValue(StorageSystemDocument document, string itemId)
    {
        var item = document.HardwareReport.Items.FirstOrDefault(x => x.Id == itemId);
        if (item?.FinalValue is not { } element || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return element.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string ReportMemory(StorageSystemDocument document)
    {
        var item = document.HardwareReport.Items.FirstOrDefault(x => x.Id == "0504");
        if (item?.FinalValue is not { } element || element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        var values = element.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (values.Count == 0)
        {
            return string.Empty;
        }
        long total = 0;
        var parsed = 0;
        foreach (var value in values)
        {
            if (TryParseByteSize(value!, out var bytes))
            {
                total += bytes;
                parsed++;
            }
        }
        return parsed == values.Count && parsed > 0
            ? TopologyProjector.FormatBytes(total)
            : string.Join(" + ", values);
    }

    private static bool TryParseByteSize(string text, out long bytes)
    {
        bytes = 0;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], out var amount))
        {
            return false;
        }
        var multiplier = parts[1] switch
        {
            "B" => 1L,
            "KiB" => 1L << 10,
            "MiB" => 1L << 20,
            "GiB" => 1L << 30,
            "TiB" => 1L << 40,
            "PiB" => 1L << 50,
            _ => 0L
        };
        if (multiplier == 0)
        {
            return false;
        }
        bytes = (long)(amount * multiplier);
        return true;
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

    private bool SwitchSystem(string? systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }
        var system = SystemCatalog.Find(systemId);
        if (system is null || system.Id == SelectedSystem.Id)
        {
            return false;
        }

        SelectedSystem = system;
        _contextUnit = null;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(CanOpenSelectedPartition));
        RebuildTopology();
        return true;
    }

    private void ApplySystemRootExpansion()
    {
        var activeSystemId = ActiveDocument.Id;
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

        foreach (var root in TopologySystems.Where(
                     x => x.Unit.StableId == ActiveDocument.Id))
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
