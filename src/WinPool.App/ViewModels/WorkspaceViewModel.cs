using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using IAgentConnection = WinPool.Application.IAgentConnection;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly IHardwareInventoryProvider _hardwareInventoryProvider;
    private readonly IUserPreferencesService _preferencesService;
    private bool _preferencesInitialized;
    private readonly IGlobalNotificationService _notificationService;
    private readonly IStorageSystemRepository _systemRepository;
    private readonly ISimulationOperationService _simulationOperations;
    private readonly WinPool.Application.ISimulationEditCoordinator _simulationEditCoordinator;
    private readonly IMachineRecordService _machineRecordService;
    private readonly IWorkspaceStateService _workspaceStateService;
    private readonly IAgentConnection? _agentConnection;
    private readonly WinPool.Application.IManageSystemProjector<StorageSystemDocument> _manageProjector;
    private readonly WinPool.Application.IManageComparisonProjector<StorageSystemDocument> _manageComparisonProjector;
    private readonly WinPool.Application.IManageDetailsProjector<StorageSystemDocument> _manageDetailsProjector;
    private readonly WinPool.Application.IManageNavigationProjector<StorageSystemDocument> _manageNavigationProjector;
    private readonly WinPool.Application.IManageCommandProjector<StorageSystemDocument> _manageCommandProjector;
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
        IWorkspaceStateService workspaceStateService,
        IAgentConnection? agentConnection = null,
        WinPool.Application.IManageSystemProjector<StorageSystemDocument>? manageProjector = null,
        WinPool.Application.IManageComparisonProjector<StorageSystemDocument>? manageComparisonProjector = null,
        WinPool.Application.IManageDetailsProjector<StorageSystemDocument>? manageDetailsProjector = null,
        WinPool.Application.IManageNavigationProjector<StorageSystemDocument>? manageNavigationProjector = null,
        WinPool.Application.IManageCommandProjector<StorageSystemDocument>? manageCommandProjector = null)
    {
        _hardwareInventoryProvider = hardwareInventoryProvider;
        _preferencesService = preferencesService;
        _notificationService = notificationService;
        _systemRepository = systemRepository;
        _simulationOperations = simulationOperations;
        _machineRecordService = machineRecordService;
        _workspaceStateService = workspaceStateService;
        _agentConnection = agentConnection;
        _manageProjector = manageProjector ?? new ManageSystemProjector();
        _manageComparisonProjector = manageComparisonProjector ?? new ManageComparisonProjector();
        _manageDetailsProjector = manageDetailsProjector ?? new ManageDetailsProjector();
        _manageNavigationProjector = manageNavigationProjector ?? new ManageNavigationProjector();
        _manageCommandProjector = manageCommandProjector ?? new ManageCommandProjector();
        _simulationEditCoordinator = new SimulationEditCoordinator(
            () => ActiveDocument,
            CommitSimulationDocumentAsync,
            _simulationOperations,
            ResetBuiltInSimulation);
        Monitoring = new MonitoringService(agentConnection);
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
        SystemCatalog.ReplaceLocal(local);
        foreach (var simulation in SimulationCatalog.CreateDocuments())
        {
            SystemCatalog.AddSimulation(simulation);
        }
        SelectedSystem = SystemCatalog.Systems.First(system => !system.IsLocal);
        Localization = new LocalizationService();
        _selectedCategory = WorkspaceCategory.System;
        RefreshLocalizedContent();
    }

    public LocalizationService Localization { get; }

    public IStorageSystemImportExportService ImportExportService { get; }

    public IGlobalNotificationService NotificationService => _notificationService;

    public void PresentNotification(WinPool.Application.ApplicationNotification notification) =>
        new ApplicationNotificationPresenter(_notificationService, Localization).Present(notification);

    public MonitoringService Monitoring { get; }

    public IAgentConnection? AgentConnection => _agentConnection;

    public Action<WinPool.Application.ManageObjectTarget, Microsoft.UI.Xaml.FrameworkElement, Windows.Foundation.Point>?
        NodeContextMenuRequested { get; set; }

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
        var copy = StorageSystemDocumentSanitizer.RedactSensitiveData(local)
            .AsImportedSimulation(
                $"{local.Snapshot.Computer.Name} {DateTime.Now:yyyy-MM-dd HH:mm}") with
        {
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

    public bool HasSelection => SelectedWorkspaceItem?.Projection is not null;

    public bool CanOpenSelectedPartition =>
        IsSelectedSystemLocalConsistent
        && SelectedWorkspaceItem?.Projection?.Role is WinPool.Application.ManageObjectRole.Partition
            or WinPool.Application.ManageObjectRole.Volume
        && ActiveSnapshot.Partitions.Any(
            partition => partition.StableId == SelectedWorkspaceItem.Projection.Id.ProviderKey);

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
        if (current?.Projection is null || current.IsAction)
        {
            return null;
        }

        var contractCategory = target switch
        {
            WorkspaceCategory.System => WinPool.Application.ManageWorkspaceCategory.System,
            WorkspaceCategory.Pool => WinPool.Application.ManageWorkspaceCategory.Pool,
            WorkspaceCategory.Tier => WinPool.Application.ManageWorkspaceCategory.Tier,
            WorkspaceCategory.Disk => WinPool.Application.ManageWorkspaceCategory.Disk,
            WorkspaceCategory.Partition => WinPool.Application.ManageWorkspaceCategory.Partition,
            WorkspaceCategory.Volume => WinPool.Application.ManageWorkspaceCategory.Volume,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var navigation = _manageNavigationProjector.Project(
            ActiveDocument,
            current.Projection.Id,
            current.Projection.Role);
        return navigation.RelatedSelections.GetValueOrDefault(contractCategory)?.Id.ProviderKey;
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
        await InitializePreferencesAsync();
        var cachedLocal = await _machineRecordService.LoadLocalScanAsync();
        if (cachedLocal is not null)
        {
            SystemCatalog.ReplaceLocal(cachedLocal);
            OnPropertyChanged(nameof(Snapshot));
        }
        var persisted = (await _systemRepository.LoadSimulationsAsync()).ToList();
        var merged = await MergeBuiltInSimulationsAsync(persisted);
        var selectedId = SelectedSystem.Id;
        SystemCatalog.ReplaceSimulations(merged);
        SelectedSystem = SystemCatalog.Find(selectedId)
            ?? SystemCatalog.Systems.First(x => !x.IsLocal);
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
            SelectedTopologyStableId = string.IsNullOrWhiteSpace(
                RestoredUiState.HighlightedTopologyStableId)
                ? null
                : RestoredUiState.HighlightedTopologyStableId;
        }
        RefreshLocalizedContent();
    }

    public async Task InitializePreferencesAsync()
    {
        if (_preferencesInitialized)
        {
            return;
        }

        var preferences = await _preferencesService.LoadAsync();
        Localization.Language = preferences.Language;
        CurrentPreferences = preferences;
        _preferencesInitialized = true;
    }

    public async Task RefreshPreferencesAsync(bool refreshLocalizedContent = true)
    {
        var preferences = await _preferencesService.LoadAsync();
        CurrentPreferences = preferences;
        Localization.Language = preferences.Language;
        if (refreshLocalizedContent)
        {
            RefreshLocalizedContent();
        }
    }

    public WorkspaceUiState? RestoredUiState { get; private set; }

    public WorkspaceUiState CaptureUiState(string shellPage) =>
        new(
            shellPage,
            SelectedSystem.Id,
            SelectedCategory,
            new Dictionary<WorkspaceCategory, string>(_categorySelections),
            SelectedTopologyStableId ?? string.Empty);

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

    public async Task SetLastActivePageAsync(string page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return;
        }

        CurrentPreferences = CurrentPreferences with
        {
            LastActivePage = page
        };
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

    public async Task SetContinuousMonitoringAsync(bool enabled)
    {
        CurrentPreferences = CurrentPreferences with
        {
            ContinuousMonitoringEnabled = enabled
        };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetMonitoringSampleRateAsync(double rateHz)
    {
        if (!double.IsFinite(rateHz) || rateHz is < 0.2 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(rateHz));
        }

        CurrentPreferences = CurrentPreferences with { MonitoringSampleRateHz = rateHz };
        await _preferencesService.SaveAsync(CurrentPreferences);
    }

    public async Task SetStartAgentAtLoginAsync(bool enabled)
    {
        CurrentPreferences = CurrentPreferences with { StartAgentAtLogin = enabled };
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

    public async Task<WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt>> ApplySimulationOperationAsync(
        WinPool.Application.SimulationEditRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _simulationEditCoordinator.ExecuteAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return result;
        }

        foreach (var command in result.Value.SimulatedCommands)
        {
            CommandLog.Log("simulation", command, "OK", simulated: true);
        }

        return result;
    }

    private async Task CommitSimulationDocumentAsync(
        SimulationEditCommit commit,
        CancellationToken cancellationToken)
    {
        var document = commit.Document;
        if (_systemRepository is IStructuredSimulationEditRepository structured)
        {
            await structured.SaveEditAsync(
                document,
                commit.Plan,
                commit.Events,
                cancellationToken);
        }
        else
        {
            await _systemRepository.SaveSimulationAsync(document, cancellationToken);
        }
        SelectedSystem = document;
        SystemCatalog.Update(document);
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        RebuildTopology();
        RebuildObjects(SelectedStableId);
        BuildDetails();
        RebuildComparisonColumns();
    }

    public async Task ResetActiveSimulationAsync()
    {
        await ApplySimulationOperationAsync(
            new WinPool.Application.SimulationEditRequest(
                WinPool.Application.SimulationEditKind.ResetDocument,
                ActiveSnapshot.Computer.StableId));
    }

    private async Task<List<StorageSystemDocument>> MergeBuiltInSimulationsAsync(
        List<StorageSystemDocument> persisted)
    {
        var builtins = SimulationCatalog.CreateDocuments();
        var persistedById = persisted.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var merged = new List<StorageSystemDocument>();
        foreach (var builtin in builtins)
        {
            if (!persistedById.TryGetValue(builtin.Id, out var existing)
                || existing.Snapshot.Computer.Name != builtin.Snapshot.Computer.Name
                || existing.Snapshot.SnapshotVersion != builtin.Snapshot.SnapshotVersion)
            {
                var updated = existing is null
                    ? builtin
                    : existing with
                    {
                        DisplayName = builtin.DisplayName,
                        Snapshot = builtin.Snapshot,
                        HardwareReport = builtin.HardwareReport,
                        Jobs = [],
                        Revision = checked(existing.Revision + 1),
                        UpdatedAt = DateTimeOffset.Now
                    };
                await _systemRepository.SaveSimulationAsync(updated);
                merged.Add(updated);
                continue;
            }

            merged.Add(existing);
        }

        foreach (var extra in persisted.Where(
                     item => !item.Id.StartsWith("simulation:builtin:", StringComparison.Ordinal)))
        {
            merged.Add(extra);
        }

        return merged;
    }

    private static SimulationOperationResult ResetBuiltInSimulation(
        StorageSystemDocument document)
    {
        var builtin = SimulationCatalog.TryCreateDocument(document.Id);
        if (builtin is null)
        {
            return SimulationOperationResult.Failure(
                document,
                "Only a built-in simulation document can be reset.");
        }

        return new SimulationOperationResult(
            true,
            document with
            {
                DisplayName = builtin.DisplayName,
                Snapshot = builtin.Snapshot,
                HardwareReport = builtin.HardwareReport,
                Jobs = [],
                UpdatedAt = DateTimeOffset.Now
            },
            string.Empty,
            ["Reset-SimulationDocument -BuiltIn"]);
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
        PresentNotification(WinPool.Application.WorkspaceNotificationFactory.ScanStarted());
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
                var restored = WorkspaceSelectionState.Restore(snapshot, previous);
                SelectedCategory = restored.Category;
                RebuildTopology();
                RebuildObjects(restored.StableId);
                SelectedTopologyStableId = snapshot.FindUnit(previousTopologyStableId ?? string.Empty) is null
                    ? null
                    : previousTopologyStableId;
            }
            StatusMessage = $"{Localization["LastScan"]}: {snapshot.ScannedAt.LocalDateTime:G}";
            _notificationService.DismissByKey(ScanningNotificationKey);
            PresentNotification(WinPool.Application.WorkspaceNotificationFactory.ScanCompleted(
                StatusMessage,
                snapshot.ScannedAt));
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
            PresentNotification(WinPool.Application.WorkspaceNotificationFactory.ScanFailed(
                $"inventory-error:{DateTimeOffset.UtcNow.Ticks}"));
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
        var sourceDocument = SystemCatalog.Systems.FirstOrDefault(
            document => ReferenceEquals(document.Snapshot, sourceSnapshot))
            ?? ActiveDocument;
        var role = unit.Kind switch
        {
            StorageUnitKind.System => WinPool.Application.ManageObjectRole.System,
            StorageUnitKind.StorageSubsystem => WinPool.Application.ManageObjectRole.StorageSubsystem,
            StorageUnitKind.StoragePool => WinPool.Application.ManageObjectRole.StoragePool,
            StorageUnitKind.StorageTier => WinPool.Application.ManageObjectRole.StorageTier,
            StorageUnitKind.PhysicalDisk => WinPool.Application.ManageObjectRole.PhysicalDisk,
            StorageUnitKind.VirtualDisk => WinPool.Application.ManageObjectRole.VirtualDisk,
            StorageUnitKind.NetworkDisk => WinPool.Application.ManageObjectRole.NetworkDisk,
            StorageUnitKind.OsDisk => WinPool.Application.ManageObjectRole.OsDisk,
            StorageUnitKind.Partition => WinPool.Application.ManageObjectRole.Partition,
            StorageUnitKind.NetworkDiskGroup => WinPool.Application.ManageObjectRole.NetworkGroup,
            StorageUnitKind.OtherDiskGroup => WinPool.Application.ManageObjectRole.OtherGroup,
            StorageUnitKind.DirectDiskGroup => WinPool.Application.ManageObjectRole.DirectDiskGroup,
            StorageUnitKind.VirtualDiskGroup => WinPool.Application.ManageObjectRole.VirtualDiskGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        SelectManageTopologyNode(
            new WinPool.Domain.StorageObjectId(
                sourceDocument.SystemId,
                role is WinPool.Application.ManageObjectRole.NetworkGroup
                    or WinPool.Application.ManageObjectRole.OtherGroup
                    or WinPool.Application.ManageObjectRole.DirectDiskGroup
                    or WinPool.Application.ManageObjectRole.VirtualDiskGroup
                    ? WinPool.Domain.StorageObjectKind.LogicalGroup
                    : MapDomainKind(role),
                unit.StableId),
            role,
            sourceSnapshot);
    }

    public void SelectManageTopologyNode(
        WinPool.Domain.StorageObjectId objectId,
        WinPool.Application.ManageObjectRole role,
        StorageSnapshot sourceSnapshot)
    {
        if (SelectedTopologyStableId?.Equals(
                objectId.ProviderKey,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            SelectedTopologyStableId = null;
            return;
        }

        var sourceDocument = SystemCatalog.Systems.FirstOrDefault(
            document => _manageProjector.Project(document).SystemId == objectId.System)
            ?? SystemCatalog.Systems.FirstOrDefault(
            x => ReferenceEquals(x.Snapshot, sourceSnapshot))
            ?? SystemCatalog.Systems.FirstOrDefault(
                x => x.Snapshot.SnapshotVersion == sourceSnapshot.SnapshotVersion);
        var switchedInventory = SwitchSystem(sourceDocument?.Id);
        _contextUnit = null;
        var contractCategory = WinPool.Application.ManageSelectionRules.CategoryFor(role);
        var category = contractCategory switch
        {
            WinPool.Application.ManageWorkspaceCategory.System => WorkspaceCategory.System,
            WinPool.Application.ManageWorkspaceCategory.Pool => WorkspaceCategory.Pool,
            WinPool.Application.ManageWorkspaceCategory.Tier => WorkspaceCategory.Tier,
            WinPool.Application.ManageWorkspaceCategory.Disk => WorkspaceCategory.Disk,
            WinPool.Application.ManageWorkspaceCategory.Partition => WorkspaceCategory.Partition,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var stableId = role == WinPool.Application.ManageObjectRole.System
            && sourceDocument is not null
                ? sourceDocument.Id
                : objectId.ProviderKey;
        _categorySelections[category] = stableId;
        if (SelectedCategory != category)
        {
            _suppressRelatedSelection = true;
            SelectedCategory = category;
            _suppressRelatedSelection = false;
        }
        else if (switchedInventory)
        {
            RebuildObjects(stableId);
        }

        var item = Objects.FirstOrDefault(x => x.Unit?.StableId == stableId);
        if (item is not null)
        {
            SelectedWorkspaceItem = item;
            SelectedTopologyStableId = objectId.ProviderKey;
            ExpandSelectedTopologyPath();
        }
    }

    private static WinPool.Domain.StorageObjectKind MapDomainKind(
        WinPool.Application.ManageObjectRole role) => role switch
    {
        WinPool.Application.ManageObjectRole.System => WinPool.Domain.StorageObjectKind.System,
        WinPool.Application.ManageObjectRole.StorageSubsystem => WinPool.Domain.StorageObjectKind.StorageSubsystem,
        WinPool.Application.ManageObjectRole.StoragePool => WinPool.Domain.StorageObjectKind.StoragePool,
        WinPool.Application.ManageObjectRole.StorageTier => WinPool.Domain.StorageObjectKind.StorageTier,
        WinPool.Application.ManageObjectRole.PhysicalDisk => WinPool.Domain.StorageObjectKind.PhysicalDisk,
        WinPool.Application.ManageObjectRole.VirtualDisk => WinPool.Domain.StorageObjectKind.VirtualDisk,
        WinPool.Application.ManageObjectRole.OsDisk => WinPool.Domain.StorageObjectKind.OsDisk,
        WinPool.Application.ManageObjectRole.Partition => WinPool.Domain.StorageObjectKind.Partition,
        WinPool.Application.ManageObjectRole.Volume => WinPool.Domain.StorageObjectKind.Partition,
        WinPool.Application.ManageObjectRole.NetworkDisk => WinPool.Domain.StorageObjectKind.NetworkDisk,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public StorageUnitRef? ResolveDetailUnit() => _contextUnit ?? SelectedWorkspaceItem?.Unit;

    public WinPool.Application.ManageCommandSurfaceView? GetSelectedCommandSurface()
    {
        var item = SelectedWorkspaceItem;
        if (item?.Projection is null || item.IsAction)
        {
            return null;
        }
        var category = SelectedCategory switch
        {
            WorkspaceCategory.System => WinPool.Application.ManageWorkspaceCategory.System,
            WorkspaceCategory.Pool => WinPool.Application.ManageWorkspaceCategory.Pool,
            WorkspaceCategory.Tier => WinPool.Application.ManageWorkspaceCategory.Tier,
            WorkspaceCategory.Disk => WinPool.Application.ManageWorkspaceCategory.Disk,
            WorkspaceCategory.Partition => WinPool.Application.ManageWorkspaceCategory.Partition,
            WorkspaceCategory.Volume => WinPool.Application.ManageWorkspaceCategory.Volume,
            _ => throw new ArgumentOutOfRangeException()
        };
        var activeSystemId = ActiveDocument.SystemId;
        if (item.Projection.Id.System != activeSystemId)
        {
            // A restored selection can briefly refer to the previous document
            // while a local inventory is being replaced. Defer the command
            // surface until the page rebuilds against the active document.
            return null;
        }
        return _manageCommandProjector.Project(
            ActiveDocument,
            SystemCatalog.Systems.First(document => document.IsLocal),
            item.Projection.Id,
            item.Projection.Role,
            category);
    }

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
            SelectManageTopologyNode(target.Id, target.Role, ActiveSnapshot);
        }
    }

    public void RefreshLocalizedContent()
    {
        var category = SelectedCategory;
        var topologyStableId = SelectedTopologyStableId;
        var preserveTopologySelection = Categories.Count > 0;
        Categories.Clear();
        Categories.Add(new CategoryItem(WorkspaceCategory.System, Localization["System"], "\uE7F8"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Pool, Localization["Pool"], "\uE8F1"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Tier, Localization["Tier"], "\uE8FD"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Disk, Localization["Disk"], "\uEDA2"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Partition, Localization["Partition"], "\uE7C3"));
        Categories.Add(new CategoryItem(WorkspaceCategory.Volume, Localization["Volume"], "\uE7C3"));
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
        var projected = _manageProjector.Project(document);
        var prefix = document.IsLocal
            ? (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]")
            : (Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]");
        var root = projected.Root with
        {
            DisplayName = $"{prefix} {projected.DisplayName}"
        };
        return new TopologyNodeViewModel(
            root,
            this,
            document.Snapshot);
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
        if (category == WorkspaceCategory.System)
        {
            foreach (var system in SystemCatalog.Systems)
            {
                var projection = _manageProjector.Project(system);
                var item = projection.WorkspaceObjects.Single(
                    candidate => candidate.Category == WinPool.Application.ManageWorkspaceCategory.System);
                yield return CreateWorkspaceItem(item, projection, system.Id);
            }
            yield return new WorkspaceItem(
                AddStorageSystemKey,
                Localization["AddStorageSystem"],
                null,
                true);
            yield break;
        }

        var requestedCategory = category switch
        {
            WorkspaceCategory.Pool => WinPool.Application.ManageWorkspaceCategory.Pool,
            WorkspaceCategory.Tier => WinPool.Application.ManageWorkspaceCategory.Tier,
            WorkspaceCategory.Disk => WinPool.Application.ManageWorkspaceCategory.Disk,
            WorkspaceCategory.Partition => WinPool.Application.ManageWorkspaceCategory.Partition,
            WorkspaceCategory.Volume => WinPool.Application.ManageWorkspaceCategory.Volume,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        var activeProjection = _manageProjector.Project(ActiveDocument);
        foreach (var item in activeProjection.WorkspaceObjects
                     .Where(candidate => candidate.Category == requestedCategory)
                     .OrderBy(candidate => candidate.SortOrder))
        {
            yield return CreateWorkspaceItem(item, activeProjection, ActiveDocument.Id);
        }
    }

    private WorkspaceItem CreateWorkspaceItem(
        WinPool.Application.ManageObjectListItemView item,
        WinPool.Application.ManageSystemProjection projection,
        string? storageSystemId = null)
    {
        var kind = item.Role switch
        {
            WinPool.Application.ManageObjectRole.System => StorageUnitKind.System,
            WinPool.Application.ManageObjectRole.StoragePool => StorageUnitKind.StoragePool,
            WinPool.Application.ManageObjectRole.StorageTier => StorageUnitKind.StorageTier,
            WinPool.Application.ManageObjectRole.PhysicalDisk => StorageUnitKind.PhysicalDisk,
            WinPool.Application.ManageObjectRole.VirtualDisk => StorageUnitKind.VirtualDisk,
            WinPool.Application.ManageObjectRole.OsDisk => StorageUnitKind.OsDisk,
            WinPool.Application.ManageObjectRole.Partition => StorageUnitKind.Partition,
            WinPool.Application.ManageObjectRole.Volume => StorageUnitKind.Partition,
            WinPool.Application.ManageObjectRole.NetworkDisk => StorageUnitKind.NetworkDisk,
            WinPool.Application.ManageObjectRole.NetworkGroup => StorageUnitKind.NetworkDiskGroup,
            WinPool.Application.ManageObjectRole.OtherGroup => StorageUnitKind.OtherDiskGroup,
            WinPool.Application.ManageObjectRole.DirectDiskGroup => StorageUnitKind.DirectDiskGroup,
            WinPool.Application.ManageObjectRole.VirtualDiskGroup => StorageUnitKind.VirtualDiskGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
        var title = item.Role switch
        {
            WinPool.Application.ManageObjectRole.System =>
                $"{(projection.SourceKind == WinPool.Application.StorageSystemSourceKind.Local
                    ? Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[本机]" : "[Local]"
                    : Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]")} {item.DisplayName}",
            WinPool.Application.ManageObjectRole.NetworkGroup => Localization["Network"],
            WinPool.Application.ManageObjectRole.OtherGroup => Localization["Other"],
            WinPool.Application.ManageObjectRole.Partition when string.IsNullOrWhiteSpace(item.DisplayName) =>
                PartitionTypeName(item.Metadata.GetValueOrDefault("partitionType") ?? "Unknown"),
            _ => item.DisplayName
        };
        return new WorkspaceItem(
            item.Id.ProviderKey,
            title,
            new StorageUnitRef(
                item.Id.ProviderKey,
                kind,
                title,
                item.IsStableIdentity,
                item.ParentProviderKey),
            false,
            storageSystemId,
            item);
    }

    private void BuildDetails()
    {
        Details.Clear();
        var item = SelectedWorkspaceItem;
        if (item?.Projection is null || item.IsAction)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            NotifySelectionState();
            return;
        }

        var document = item.StorageSystemId is null
            ? ActiveDocument
            : SystemCatalog.Find(item.StorageSystemId) ?? ActiveDocument;
        var details = _manageDetailsProjector.Project(
            document,
            item.Projection.Id,
            item.Projection.Role,
            item.Title);
        DetailTitle = details.DisplayName;
        DetailSubtitle = TopologyProjector.JoinSummary(
            ManageRoleName(details.Role),
            Localization["ReadOnly"]);
        foreach (var property in details.Properties)
        {
            Details.Add(new DetailRow(
                Localization[property.PropertyTextKey],
                PresentManageValue(property)));
        }
        NotifySelectionState();
    }


    private void RebuildComparisonColumns()
    {
        ComparisonColumns.Clear();
        foreach (var item in Objects.Where(x => !x.IsAction && x.Projection is not null))
        {
            var document = item.StorageSystemId is null
                ? ActiveDocument
                : SystemCatalog.Find(item.StorageSystemId) ?? ActiveDocument;
            ComparisonColumns.Add(new ComparisonColumn(
                item.Key,
                item.Title,
                BuildProjectedComparisonRows(item.Projection!, document)));
        }

        _updatingComparisonSelection = true;
        SelectedComparisonColumn = ComparisonColumns.FirstOrDefault(
            x => x.Key == SelectedWorkspaceItem?.Key);
        _updatingComparisonSelection = false;
    }

    private IReadOnlyList<DetailRow> BuildProjectedComparisonRows(
        WinPool.Application.ManageObjectListItemView item,
        StorageSystemDocument document)
    {
        var comparison = _manageComparisonProjector.Project(
            document,
            item.Id,
            item.Role);
        return comparison.Properties
            .Select(property => new DetailRow(
                Localization[property.PropertyTextKey],
                PresentManageValue(property)))
            .ToArray();
    }

    private string PresentManageValue(
        WinPool.Application.ManagePropertyView property) =>
        property.Presentation switch
        {
            WinPool.Application.ManageValuePresentation.Plain => property.RawValue,
            WinPool.Application.ManageValuePresentation.LocalizationKey =>
                Localization[property.RawValue],
            WinPool.Application.ManageValuePresentation.PartitionType =>
                PartitionTypeName(property.RawValue),
            WinPool.Application.ManageValuePresentation.MaskedSerial =>
                FormatSerial(property.RawValue),
            WinPool.Application.ManageValuePresentation.ProductName =>
                ProductDisplayName(property.RawValue),
            WinPool.Application.ManageValuePresentation.LocalDateTime =>
                DateTimeOffset.TryParse(property.RawValue, out var timestamp)
                    ? timestamp.LocalDateTime.ToString("G")
                    : "—",
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };

    private static string ProductDisplayName(string productName)
    {
        var trimmed = productName.Trim();
        return trimmed.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Microsoft ".Length..]
            : trimmed;
    }

    private WinPool.Application.ManageObjectTarget? GetPrimaryRelatedTarget()
    {
        var item = SelectedWorkspaceItem;
        if (item?.Projection is null || item.IsAction)
        {
            return null;
        }
        return _manageNavigationProjector.Project(
            ActiveDocument,
            item.Projection.Id,
            item.Projection.Role).PrimaryTarget;
    }

    private string ManageRoleName(WinPool.Application.ManageObjectRole role) => role switch
    {
        WinPool.Application.ManageObjectRole.System => Localization["System"],
        WinPool.Application.ManageObjectRole.StoragePool => Localization["StoragePool"],
        WinPool.Application.ManageObjectRole.StorageTier => Localization["StorageTier"],
        WinPool.Application.ManageObjectRole.PhysicalDisk => Localization["PhysicalDisk"],
        WinPool.Application.ManageObjectRole.VirtualDisk => Localization["VirtualDisk"],
        WinPool.Application.ManageObjectRole.NetworkDisk => Localization["NetworkDisk"],
        WinPool.Application.ManageObjectRole.OsDisk => Localization["OtherDisk"],
        WinPool.Application.ManageObjectRole.NetworkGroup => Localization["NetworkStorageGroup"],
        WinPool.Application.ManageObjectRole.OtherGroup => Localization["OtherStorageGroup"],
        WinPool.Application.ManageObjectRole.Partition => Localization["Partition"],
        WinPool.Application.ManageObjectRole.Volume => Localization["Volume"],
        _ => role.ToString()
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
        RebuildObjects(SelectedStableId);
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
