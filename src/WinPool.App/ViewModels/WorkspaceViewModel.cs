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
    private readonly TaskCompletionSource _workspaceReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, bool> _expandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(SystemId System, ManageWorkspaceCategory Category), ManageSelectionKey>
        _categorySelections = [];
    private bool _updatingComparisonSelection;
    private bool _suppressRelatedSelection;
    private StorageUnitRef? _contextUnit;
    private ManageSelectionKey? _selectedSelection;
    private ManageObjectTarget? _selectedTopologyTarget;
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
        _selectedCategory = ManageWorkspaceCategory.System;
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
        RebuildObjects(RememberedSelection(SelectedCategory));
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
        RebuildObjects(RememberedSelection(SelectedCategory));
    }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public ObservableCollection<WorkspaceItem> Objects { get; } = [];

    public ObservableCollection<DetailRow> Details { get; } = [];

    public ObservableCollection<ComparisonColumn> ComparisonColumns { get; } = [];

    [ObservableProperty]
    private ComparisonColumn? _selectedComparisonColumn;

    [ObservableProperty]
    private ManageWorkspaceCategory _selectedCategory;

    [ObservableProperty]
    private CategoryItem? _selectedCategoryItem;

    [ObservableProperty]
    private WorkspaceItem? _selectedWorkspaceItem;

    public ObservableCollection<TopologyNodeViewModel> TopologySystems { get; } = [];

    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    private string _detailSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isPreparingWorkspace;

    [ObservableProperty]
    private string _scanError = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasScanError => !string.IsNullOrWhiteSpace(ScanError);

    public bool ShowInventoryStatus => IsPreparingWorkspace || IsScanning;

    public Task WhenWorkspaceReady => _workspaceReady.Task;

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

    partial void OnSelectedCategoryChanged(ManageWorkspaceCategory value)
    {
        var related = _suppressRelatedSelection ? null : FindRelatedSelectionForCategory(value);
        RebuildObjects(related ?? RememberedSelection(value));
        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == value);
        OnPropertyChanged(nameof(SelectedCategoryTitle));
    }

    private ManageSelectionKey? FindRelatedSelectionForCategory(ManageWorkspaceCategory target)
    {
        var current = SelectedWorkspaceItem;
        if (current?.Projection is null || current.IsAction)
        {
            return null;
        }

        var navigation = _manageNavigationProjector.Project(
            ActiveDocument,
            current.Projection.Id,
            current.Projection.Role);
        var related = navigation.RelatedSelections.GetValueOrDefault(target);
        return related is null
            ? null
            : new ManageSelectionKey(related.Id, related.Role, target);
    }

    partial void OnSelectedWorkspaceItemChanged(WorkspaceItem? value)
    {
        _contextUnit = null;
        if (value?.IsAction == true)
        {
            SetSelectionState(null, null);
            BuildDetails();
            RebuildComparisonColumns();
            RefreshTopologySelection();
            NotifySelectionState();
            WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (value?.Projection is null)
        {
            SetSelectionState(null, null);
            BuildDetails();
            RebuildComparisonColumns();
            RefreshTopologySelection();
            NotifySelectionState();
            WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var selection = SelectionFor(value.Projection);
        if (SelectedCategory == ManageWorkspaceCategory.System
            && value.Unit?.Kind == StorageUnitKind.System
            && SwitchSystem(value.StorageSystemId, selection))
        {
            return;
        }
        SetSelectionState(selection, ManageSelectionRules.TopologyTargetFor(selection));
        _categorySelections[(selection.Id.System, selection.Category)] = selection;
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

    partial void OnScanErrorChanged(string value) => OnPropertyChanged(nameof(HasScanError));

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ShowInventoryStatus));

    partial void OnIsPreparingWorkspaceChanged(bool value) =>
        OnPropertyChanged(nameof(ShowInventoryStatus));

    public void BeginWorkspacePrepare()
    {
        IsPreparingWorkspace = true;
        StatusMessage = Localization["ConnectingAgent"];
    }

    public void NotifyWorkspaceLoading()
    {
        if (IsPreparingWorkspace)
        {
            StatusMessage = Localization["LoadingWorkspace"];
        }
    }

    public void CompleteWorkspacePrepare()
    {
        IsPreparingWorkspace = false;
        if (!IsScanning && string.IsNullOrWhiteSpace(ScanError))
        {
            StatusMessage = string.Empty;
        }

        _workspaceReady.TrySetResult();
    }

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
                    var restored = ResolveSelection(
                        SelectedSystem,
                        pair.Key,
                        pair.Value);
                    if (restored is not null)
                    {
                        _categorySelections[(restored.Id.System, restored.Category)] = restored;
                    }
                }
            }
            SelectedCategory = RestoredUiState.Category;
            _selectedTopologyTarget = ResolveTopologyTarget(
                SelectedSystem,
                RestoredUiState.HighlightedTopologyStableId);
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
            _categorySelections
                .Where(pair => pair.Key.System == ActiveDocument.SystemId)
                .ToDictionary(
                    pair => pair.Key.Category,
                    pair => pair.Value.Id.ProviderKey),
            _selectedTopologyTarget?.Id.ProviderKey ?? string.Empty);

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
        RebuildObjects(RememberedSelection(SelectedCategory));
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
        RebuildObjects(_selectedSelection);
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

        await WhenWorkspaceReady;
        IsScanning = true;
        ScanError = string.Empty;
        StatusMessage = Localization["Scanning"];
        _notificationService.DismissByKey(ScanningNotificationKey);
        var previous = _selectedSelection;
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
            OnPropertyChanged(nameof(Snapshot));
            if (!IsLocalSystem)
            {
                RebuildTopology();
            }
            else
            {
                var preferredSelection = previous is null
                    ? null
                    : ResolveSelection(
                        localDocument,
                        SelectedCategory,
                        previous.Id.ProviderKey);
                SelectedSystem = localDocument;
                OnPropertyChanged(nameof(SelectedSystem));
                OnPropertyChanged(nameof(ActiveSnapshot));
                OnPropertyChanged(nameof(ActiveDocument));
                OnPropertyChanged(nameof(CanOpenSelectedPartition));
                RebuildTopology();
                RebuildObjects(preferredSelection);
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
        var category = WinPool.Application.ManageSelectionRules.CategoryFor(role);
        var selection = new ManageSelectionKey(objectId, role, category);
        var topologyTarget = new ManageObjectTarget(objectId, role);
        if (ManageSelectionRules.SameSelection(_selectedSelection, selection)
            && ManageSelectionRules.SameTarget(_selectedTopologyTarget, topologyTarget))
        {
            return;
        }

        var sourceDocument = SystemCatalog.Systems.FirstOrDefault(
            document => _manageProjector.Project(document).SystemId == objectId.System)
            ?? SystemCatalog.Systems.FirstOrDefault(
            x => ReferenceEquals(x.Snapshot, sourceSnapshot))
            ?? SystemCatalog.Systems.FirstOrDefault(
                x => x.Snapshot.SnapshotVersion == sourceSnapshot.SnapshotVersion);
        SwitchSystem(sourceDocument?.Id, selection);
        _contextUnit = null;
        _categorySelections[(objectId.System, category)] = selection;
        if (SelectedCategory != category)
        {
            _suppressRelatedSelection = true;
            SelectedCategory = category;
            _suppressRelatedSelection = false;
        }
        else
        {
            RebuildObjects(selection);
        }

        var item = Objects.FirstOrDefault(x =>
            x.Projection is not null
            && ManageSelectionRules.SameSelection(SelectionFor(x.Projection), selection));
        if (item is not null)
        {
            SelectedWorkspaceItem = item;
            SetSelectionState(selection, topologyTarget);
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
            SelectedCategory);
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
        Categories.Clear();
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.System, Localization["System"], "\uE7F8"));
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.Pool, Localization["Pool"], "\uE8F1"));
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.Tier, Localization["Tier"], "\uE8FD"));
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.Disk, Localization["Disk"], "\uEDA2"));
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.Partition, Localization["Partition"], "\uE7C3"));
        Categories.Add(new CategoryItem(ManageWorkspaceCategory.Volume, Localization["Volume"], "\uE7C3"));
        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == category);
        OnPropertyChanged(nameof(SelectedCategoryTitle));
        RebuildObjects(RememberedSelection(SelectedCategory) ?? _selectedSelection);
        RebuildTopology();
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
                StorageFindingKind.ConflictingTopologyRelationship => (
                    zh ? "存储关系冲突" : "Storage relationship conflict",
                    zh
                        ? $"同一存储对象（{finding.TargetName}）被报告在多个拓扑位置。WinPool 已只显示一次；请刷新清单并检查存储关系。"
                        : $"The same storage object ({finding.TargetName}) was reported in more than one topology location. WinPool shows it once; refresh inventory and inspect the storage relationships."),
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

    private static ManageSelectionKey SelectionFor(ManageObjectListItemView item) =>
        new(item.Id, item.Role, item.Category);

    private ManageSelectionKey? RememberedSelection(ManageWorkspaceCategory category) =>
        _categorySelections.GetValueOrDefault((ActiveDocument.SystemId, category));

    private ManageSelectionKey? ResolveSelection(
        StorageSystemDocument document,
        ManageWorkspaceCategory category,
        string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var item = _manageProjector.Project(document).WorkspaceObjects.FirstOrDefault(candidate =>
            candidate.Category == category
            && candidate.Id.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : SelectionFor(item);
    }

    private ManageObjectTarget? ResolveTopologyTarget(
        StorageSystemDocument document,
        string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var node = Flatten(_manageProjector.Project(document).Root).FirstOrDefault(candidate =>
            candidate.Id.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        return node is null ? null : new ManageObjectTarget(node.Id, node.Role);
    }

    private static IEnumerable<ManageTopologyNodeView> Flatten(ManageTopologyNodeView root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private void SetSelectionState(
        ManageSelectionKey? selection,
        ManageObjectTarget? topologyTarget)
    {
        var changed = !ManageSelectionRules.SameSelection(_selectedSelection, selection)
            || !ManageSelectionRules.SameTarget(_selectedTopologyTarget, topologyTarget);
        _selectedSelection = selection;
        _selectedTopologyTarget = topologyTarget;
        if (changed)
        {
            RefreshTopologySelection();
        }
    }

    public bool IsTopologySelected(StorageObjectId objectId, ManageObjectRole role) =>
        ManageSelectionRules.SameTarget(
            _selectedTopologyTarget,
            new ManageObjectTarget(objectId, role));

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

    private void RebuildObjects(ManageSelectionKey? preferredSelection)
    {
        Objects.Clear();
        foreach (var item in CreateWorkspaceItems(SelectedCategory))
        {
            Objects.Add(item);
        }

        SelectedWorkspaceItem =
            Objects.FirstOrDefault(x =>
                x.Projection is not null
                && ManageSelectionRules.SameSelection(
                    SelectionFor(x.Projection),
                    preferredSelection))
            ?? Objects.FirstOrDefault();
    }

    private IEnumerable<WorkspaceItem> CreateWorkspaceItems(ManageWorkspaceCategory category)
    {
        if (category == ManageWorkspaceCategory.System)
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

        var activeProjection = _manageProjector.Project(ActiveDocument);
        foreach (var item in activeProjection.WorkspaceObjects
                     .Where(candidate => candidate.Category == category)
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
            WinPool.Application.ManageObjectRole.DirectDiskGroup => Localization["UnallocatedLayer"],
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
        WinPool.Application.ManageObjectRole.DirectDiskGroup => Localization["UnallocatedLayer"],
        WinPool.Application.ManageObjectRole.Partition => Localization["Partition"],
        WinPool.Application.ManageObjectRole.Volume => Localization["Volume"],
        _ => role.ToString()
    };

    private bool SwitchSystem(string? systemId, ManageSelectionKey? preferredSelection = null)
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
        RebuildObjects(preferredSelection ?? RememberedSelection(SelectedCategory));
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
        if (_selectedTopologyTarget is null)
        {
            return;
        }

        foreach (var root in TopologySystems.Where(
                     x => x.ObjectId.System == _selectedTopologyTarget.Id.System))
        {
            if (root.ExpandPathTo(_selectedTopologyTarget))
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
