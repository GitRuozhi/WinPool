using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly UserPreferencesUpdateCoordinator _preferencesCoordinator;
    private bool _preferencesInitialized;
    private readonly IGlobalNotificationService _notificationService;
    private readonly IStorageSystemRepository _systemRepository;
    private readonly ISimulationOperationService _simulationOperations;
    private readonly WinPool.Application.ISimulationEditCoordinator _simulationEditCoordinator;
    private readonly SimulationDocumentSaveCoordinator _simulationSaves;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, (WinPoolFacts? Facts, string Name, LanguagePreference Language, TopologyNodeViewModel Root)> _topologyRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (StorageSystemDocument Document, ManageSystemProjection Projection)> _projections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (WinPoolFacts? Facts, ManageObjectListItemView Projection, string Title,
        LanguagePreference Language, ComparisonColumn Column)> _comparisonCache = new(StringComparer.Ordinal);
    private LanguagePreference? _renderedLanguage;
    private readonly IMachineRecordService _machineRecordService;
    private readonly IWorkspaceStateService _workspaceStateService;
    private readonly IAgentConnection _agentConnection;
    private readonly AgentRealModeSession _realModeSession;
    private readonly WinPool.Infrastructure.Windows.ManageSystemProjector _manageProjector;
    private readonly WinPool.Infrastructure.Windows.ManageComparisonProjector _manageComparisonProjector;
    private readonly WinPool.Infrastructure.Windows.ManageDetailsProjector _manageDetailsProjector;
    private readonly WinPool.Infrastructure.Windows.ManageNavigationProjector _manageNavigationProjector;
    private readonly WinPool.Infrastructure.Windows.ManageCommandProjector _manageCommandProjector;
    private readonly ManualScanRunner _manualScanRunner = new();
    private readonly TaskCompletionSource _workspaceReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, bool> _expandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(SystemId System, ManageWorkspaceCategory Category), ManageSelectionKey>
        _categorySelections = [];
    private bool _updatingComparisonSelection;
    private bool _suppressRelatedSelection;
    private bool _startupPreviewApplied;
    private StorageUnitRef? _contextUnit;
    private ManageSelectionKey? _selectedSelection;
    private ManageObjectTarget? _selectedTopologyTarget;
    private readonly HashSet<string> _shownFindings = new(StringComparer.Ordinal);
    public const string AddStorageSystemKey = "action:add-storage-system";
    private const string WorkspacePrepareNotificationKey = "startup:workspace-prepare";

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
        IAgentConnection agentConnection,
        WinPool.Infrastructure.Windows.ManageSystemProjector? manageProjector = null,
        WinPool.Infrastructure.Windows.ManageComparisonProjector? manageComparisonProjector = null,
        WinPool.Infrastructure.Windows.ManageDetailsProjector? manageDetailsProjector = null,
        WinPool.Infrastructure.Windows.ManageNavigationProjector? manageNavigationProjector = null,
        WinPool.Infrastructure.Windows.ManageCommandProjector? manageCommandProjector = null)
    {
        ArgumentNullException.ThrowIfNull(agentConnection);
        _hardwareInventoryProvider = hardwareInventoryProvider;
        _preferencesCoordinator = new UserPreferencesUpdateCoordinator(
            preferencesService,
            () => CurrentPreferences,
            preferences =>
            {
                CurrentPreferences = preferences;
                if (_preferencesInitialized)
                {
                    OnPropertyChanged(nameof(CurrentPreferences));
                }
            });
        _notificationService = notificationService;
        _systemRepository = systemRepository;
        _simulationOperations = simulationOperations;
        _machineRecordService = machineRecordService;
        _workspaceStateService = workspaceStateService;
        _agentConnection = agentConnection;
        _realModeSession = new AgentRealModeSession(agentConnection);
        RealOperationSubmission.Changed += () => OnPropertyChanged(nameof(CanSubmitRealOperation));
        _manageProjector = manageProjector ?? new ManageSystemProjector();
        _manageComparisonProjector = manageComparisonProjector ?? new ManageComparisonProjector();
        _manageDetailsProjector = manageDetailsProjector ?? new ManageDetailsProjector();
        _manageNavigationProjector = manageNavigationProjector ?? new ManageNavigationProjector();
        _manageCommandProjector = manageCommandProjector ?? new ManageCommandProjector();
        _simulationSaves = new SimulationDocumentSaveCoordinator(async (commit, token) =>
        {
            if (_systemRepository is not IStructuredSimulationEditRepository structured)
                throw new InvalidOperationException("The simulation repository must support typed CAS commits.");
            return await structured.SaveEditAsync(commit.Document, commit.Intent, token).ConfigureAwait(false);
        });
        _simulationSaves.Changed += state =>
        {
            void Publish()
            {
                if (_simulationSaves.GetState(state.DocumentId) != state) return;
                OnPropertyChanged(nameof(ActiveSimulationSaveState));
                OnPropertyChanged(nameof(SimulationSaveStatus));
                SimulationSaveStateChanged?.Invoke(state);
            }
            if (_dispatcher is null || _dispatcher.HasThreadAccess) Publish();
            else _dispatcher.TryEnqueue(Publish);
        };
        _simulationEditCoordinator = new SimulationEditCoordinator(
            () => ActiveDocument,
            CommitSimulationDocumentAsync,
            _simulationOperations);
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
            [],
            DateTimeOffset.MinValue);
        SystemCatalog.ReplaceLocal(local);
        // A new workspace has no remembered system selection. Start from the
        // local placeholder so the first usable screen always represents this
        // computer; a persisted workspace state may still select a simulation
        // during RestoreWorkspaceUiStateAsync.
        SelectedSystem = SystemCatalog.Systems.First(system => system.IsLocal);
        Localization = new LocalizationService();
        _selectedCategory = ManageWorkspaceCategory.System;
        RefreshLocalizedContent();
    }

    public LocalizationService Localization { get; }

    public IStorageSystemImportExportService ImportExportService { get; }

    public IGlobalNotificationService NotificationService => _notificationService;

    /// <summary>Display-only diagnostics location; reading or creating it is not required.</summary>
    public string DiagnosticsDirectoryPath => DataRootLayout.DiagnosticsDirectory(StorageDataLocations.CurrentRoot);

    public bool DiagnosticsDirectoryExists => Directory.Exists(DiagnosticsDirectoryPath);

    public void RefreshDiagnosticsDirectoryState()
    {
        OnPropertyChanged(nameof(DiagnosticsDirectoryPath));
        OnPropertyChanged(nameof(DiagnosticsDirectoryExists));
    }

    public void PresentNotification(WinPool.Application.ApplicationNotification notification) =>
        new ApplicationNotificationPresenter(_notificationService, Localization).Present(notification);

    public MonitoringService Monitoring { get; }

    public IAgentConnection? AgentConnection => _agentConnection;

    public string RealProductSessionId => _realModeSession?.ProductSessionId ?? string.Empty;

    public Action<WinPool.Application.ManageObjectTarget, Microsoft.UI.Xaml.FrameworkElement, Windows.Foundation.Point>?
        NodeContextMenuRequested { get; set; }

    public ICommandLogService CommandLog { get; }

    public PrivilegeState PrivilegeState { get; }

    public ExecutionModeController Execution { get; }

    public bool CanUseRealMode => Execution.CanUseRealMode;

    public bool IsRealMode => Execution.Mode == ExecutionMode.Real;

    public RealOperationSubmissionState RealOperationSubmission { get; } = new();

    public bool CanSubmitRealOperation => IsRealMode && _realModeSession?.IsArmed == true && IsLocalSystem
        && !RealOperationSubmission.IsBlocked && !_refreshingRecoveredRealOperation;

    public StorageSystemCatalog SystemCatalog { get; } = new();

    public StorageSystemDocument SelectedSystem { get; private set; }

    public StorageSystemDocument ActiveDocument => SelectedSystem;

    public StorageSnapshot ActiveSnapshot => SelectedSystem.Snapshot;

    public StorageSnapshot Snapshot =>
        SystemCatalog.Systems.First(x => x.IsLocal).Snapshot;

    public bool IsUsingSimulatedInventory => SelectedSystem.Kind == StorageSystemKind.Simulation;

    public bool IsLocalSystem => SelectedSystem.Kind == StorageSystemKind.Local;

    public bool IsSelectedSystemLocalConsistent =>
        IsLocalSystem
        || (SelectedSystem.SourceHostName is not null
            && SelectedSystem.SourceHostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    public bool CanDeleteSelectedSimulation =>
        SelectedSystem.Kind == StorageSystemKind.Simulation;

    public event Action<SimulationSaveState>? SimulationSaveStateChanged;
    public event Action<string>? SimulationDraftDiscarded;
    public Func<StorageSystemDocument, Exception, Task<bool>>? ConfirmLocalDraftExportAsync { get; set; }
    public SimulationSaveState? ActiveSimulationSaveState => _simulationSaves.GetState(ActiveDocument.Id);
    public string SimulationSaveStatus => ActiveSimulationSaveState?.Phase switch
    {
        SimulationSavePhase.Pending => Localization.IsChinese ? "模拟修改：等待保存" : "Simulation: waiting to save",
        SimulationSavePhase.Saving => Localization.IsChinese ? "模拟修改：保存中" : "Simulation: saving",
        SimulationSavePhase.Failed => Localization.IsChinese ? "模拟修改：保存失败（草稿已保留）" : "Simulation: save failed (draft retained)",
        SimulationSavePhase.Conflict => Localization.IsChinese ? "模拟修改：版本冲突（草稿已保留）" : "Simulation: revision conflict (draft retained)",
        SimulationSavePhase.OutcomeUnknown => Localization.IsChinese ? "模拟修改：保存结果未知（草稿已保留）" : "Simulation: save outcome unknown (draft retained)",
        _ => Localization.IsChinese ? "模拟修改：已保存" : "Simulation: saved"
    };
    public Task FlushSimulationEditsAsync(CancellationToken cancellationToken = default) => _simulationSaves.FlushAllAsync(cancellationToken);
    public bool HasUnsavedSimulationChanges => SystemCatalog.Systems.Any(document => !document.IsLocal
        && _simulationSaves.GetState(document.Id) is { IsDurable: false, Phase: not SimulationSavePhase.Deleted });
    public void SetSimulationEditingEnabled(bool enabled) => _simulationSaves.SetAccepting(enabled);
    public async Task DiscardUnsavedSimulationEditsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var document in SystemCatalog.Systems.Where(document => !document.IsLocal).ToArray())
        {
            if (_simulationSaves.GetState(document.Id) is not { } state || state.IsDurable) continue;
            await DiscardSimulationDraftAsync(document.Id, cancellationToken);
        }
        BindSelectedSystemToCatalog();
        RebuildTopology(); RebuildObjects(_selectedSelection);
    }
    public async Task DiscardSimulationDraftAsync(string documentId, CancellationToken cancellationToken = default)
    {
        _simulationSaves.PauseDocument(documentId);
        StorageSystemDocument? durable;
        try { durable = await _systemRepository.LoadSimulationAsync(documentId, cancellationToken); }
        catch { _simulationSaves.CancelDelete(documentId); throw; }
        _simulationSaves.Discard(documentId);
        if (durable is not null) SystemCatalog.Update(durable);
        _simulationSaves.Forget(documentId);
        BindSelectedSystemToCatalog();
        RebuildTopology(); RebuildObjects(_selectedSelection);
        SimulationDraftDiscarded?.Invoke(documentId);
    }
    public async Task<bool> ExportUnsavedSimulationDraftsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var document in SystemCatalog.Systems.Where(document => !document.IsLocal).ToArray())
            if (_simulationSaves.GetState(document.Id) is { IsDurable: false }
                && await ImportExportService.ExportAsync(document, cancellationToken) is null) return false;
        return true;
    }
    public async Task RetryAllSimulationSavesAsync(CancellationToken cancellationToken = default)
    {
        var tasks = SystemCatalog.Systems.Where(document => !document.IsLocal
            && _simulationSaves.GetState(document.Id) is { IsDurable: false })
            .Select(document => _simulationSaves.RetryAsync(document.Id, cancellationToken));
        await Task.WhenAll(tasks);
    }
    public Task RetrySimulationSaveAsync(string documentId, CancellationToken cancellationToken = default) => _simulationSaves.RetryAsync(documentId, cancellationToken);
    public Task<string?> ExportLocalSimulationDraftAsync(CancellationToken cancellationToken = default) => ImportExportService.ExportAsync(ActiveDocument, cancellationToken);

    public async Task DeleteSimulationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDeleteSelectedSimulation)
        {
            return;
        }

        var id = SelectedSystem.Id;
        try
        {
            await _simulationSaves.PrepareDeleteAsync(id, cancellationToken);
            await _systemRepository.DeleteSimulationAsync(id, cancellationToken);
        }
        catch { _simulationSaves.CancelDelete(id); throw; }
        _simulationSaves.Forget(id);
        SystemCatalog.RemoveSimulation(id);
        SelectedSystem = SystemCatalog.Systems.FirstOrDefault(x => !x.IsLocal)
            ?? SystemCatalog.Systems.First(x => x.IsLocal);
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
        RebuildTopology();
        RebuildObjects(RememberedSelection(SelectedCategory));
        RaiseWorkspaceSelectionChanged();
    }

    public async Task ConvertLocalToSimulationAsync(CancellationToken cancellationToken = default)
    {
        var local = SystemCatalog.Systems.First(x => x.IsLocal);
        var copy = local.AsImportedSimulation(
                $"{local.Snapshot.Computer.Name} {DateTime.Now:yyyy-MM-dd HH:mm}") with
        {
            SourceHostName = Environment.MachineName,
            Jobs = [],
            UpdatedAt = DateTimeOffset.Now
        };
        await _systemRepository.SaveSimulationAsync(copy, cancellationToken);
        SystemCatalog.AddSimulation(copy);
        SelectedSystem = copy;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
        RebuildTopology();
        RebuildObjects(RememberedSelection(SelectedCategory));
        RaiseWorkspaceSelectionChanged();
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

        if (current.Projection.Id.System != ActiveDocument.SystemId)
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

    async partial void OnSelectedWorkspaceItemChanged(WorkspaceItem? value)
    {
        if (value?.StorageSystemId is { } lazyId && SystemCatalog.Find(lazyId) is null && SystemCatalog.Contains(lazyId))
        {
            await SelectSystemAsync(lazyId);
            return;
        }
        _contextUnit = null;
        if (value?.IsAction == true)
        {
            SetSelectionState(null, null);
            BuildDetails();
            SelectComparisonColumn();
            RefreshTopologySelection();
            NotifySelectionState();
            RaiseWorkspaceSelectionChanged();
            return;
        }

        if (value?.Projection is null)
        {
            SetSelectionState(null, null);
            BuildDetails();
            SelectComparisonColumn();
            RefreshTopologySelection();
            NotifySelectionState();
            RaiseWorkspaceSelectionChanged();
            return;
        }

        var selection = SelectionFor(value.Projection);
        if (SelectedCategory == ManageWorkspaceCategory.System
            && value.Unit?.Kind == StorageUnitKind.System
            && SwitchSystem(value.StorageSystemId, selection))
        {
            return;
        }
        SetSelectionState(selection, ManageSelectionRules.TopologyTargetFor(selection, ActiveDocument.Snapshot));
        _categorySelections[(selection.Id.System, selection.Category)] = selection;
        BuildDetails();
        SelectComparisonColumn();
        RefreshTopologySelection();
        ExpandSelectedTopologyPath();
        NotifySelectionState();
        RaiseWorkspaceSelectionChanged();
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

    public void BeginWorkspacePrepare()
    {
        IsPreparingWorkspace = true;
        StatusMessage = Localization["ConnectingAgent"];
        ReplacePersistentInfo(
            Localization["ConnectingAgent"],
            WorkspacePrepareNotificationKey);
    }

    public void NotifyWorkspaceLoading()
    {
        if (!IsPreparingWorkspace)
        {
            return;
        }

        StatusMessage = Localization["LoadingWorkspace"];
        ReplacePersistentInfo(
            Localization["LoadingWorkspace"],
            WorkspacePrepareNotificationKey);
    }

    public void CompleteWorkspacePrepare()
    {
        IsPreparingWorkspace = false;
        if (!IsScanning && string.IsNullOrWhiteSpace(ScanError))
        {
            StatusMessage = string.Empty;
        }

        _notificationService.DismissByKey(WorkspacePrepareNotificationKey);
        _workspaceReady.TrySetResult();
        _ = RecoverRealOperationsAsync();
    }

    private void ReplacePersistentInfo(string title, string occurrenceKey)
    {
        _notificationService.DismissByKey(occurrenceKey);
        _notificationService.Publish(
            GlobalNotificationSeverity.Info,
            title,
            string.Empty,
            "startup",
            new GlobalNotificationOptions
            {
                OccurrenceKey = occurrenceKey,
                RecordInHistory = false,
                IsProgress = true
            });
    }

    public async Task InitializeAsync()
    {
        await InitializePreferencesAsync();
        var cachedLocal = await TryLoadCachedLocalAsync();
        if (cachedLocal is not null)
        {
            ApplyLocalInventory(cachedLocal);
        }

        var metadata = (await _systemRepository.LoadSimulationsAsync()).ToList();
        SystemCatalog.ReplaceSimulationMetadata(metadata);
        if (!CurrentPreferences.BuiltInSimulationCatalogSeeded)
        {
            foreach (var builtin in SimulationCatalog.CreateDocuments())
            {
                if (SystemCatalog.Contains(builtin.Id)) continue;
                await _systemRepository.SaveSimulationAsync(builtin);
                SystemCatalog.CacheSimulation(builtin);
            }
            await UpdateCurrentPreferencesAsync(preferences => preferences with { BuiltInSimulationCatalogSeeded = true });
        }
        await EnsureWorkspaceUiStateLoadedAsync();
        var previousSystem = SelectedSystem;
        var selectedId = RestoredUiState?.ActiveSystemId ?? SelectedSystem.Id;
        if (SystemCatalog.Contains(selectedId) && SystemCatalog.Entries.Any(entry => entry.Id == selectedId && !entry.IsLocal))
        {
            var selected = await _systemRepository.LoadSimulationAsync(selectedId);
            if (selected is not null) SystemCatalog.CacheSimulation(selected);
        }
        SelectedSystem = SystemCatalog.Find(selectedId)
            ?? SystemCatalog.Find(cachedLocal?.Id ?? string.Empty)
            ?? SystemCatalog.Systems.FirstOrDefault(system => system.IsLocal)
            ?? SystemCatalog.Systems.First(x => !x.IsLocal);
        if (!ReferenceEquals(previousSystem, SelectedSystem))
        {
            _contextUnit = null;
            OnPropertyChanged(nameof(SelectedSystem));
            OnPropertyChanged(nameof(ActiveDocument));
            OnPropertyChanged(nameof(ActiveSnapshot));
            OnPropertyChanged(nameof(Snapshot));
            OnPropertyChanged(nameof(IsLocalSystem));
            OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
            OnPropertyChanged(nameof(IsUsingSimulatedInventory));
            OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
            OnPropertyChanged(nameof(CanOpenSelectedPartition));
            if (!StringComparer.OrdinalIgnoreCase.Equals(previousSystem.Id, SelectedSystem.Id))
            {
                RaiseWorkspaceSelectionChanged();
            }
        }
        // Publish the cached local snapshot immediately. Restore only adjusts
        // which system/object is selected; it must not be what makes the
        // last inventory visible.
        RefreshLocalizedContent();
        await RestoreWorkspaceUiStateAsync();
    }

    /// <summary>
    /// Applies an integrity-checked read-only startup preview while the Agent
    /// catalog is loading. Persistence remains disarmed until Agent restore
    /// verifies the state and active document.
    /// </summary>
    internal bool ApplyWorkspaceStartupPreview(
        WorkspaceSessionState state,
        StorageSystemDocument activeDocument)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(activeDocument);
        if (_workspaceStateLoadAttempted
            || _startupPreviewApplied
            || !WorkspaceSessionStateValidator.IsValid(state)
            || string.IsNullOrWhiteSpace(state.ActiveDocumentId)
            || !StringComparer.OrdinalIgnoreCase.Equals(state.ActiveDocumentId, activeDocument.Id))
        {
            return false;
        }

        try
        {
            activeDocument.ValidateCurrentFormat();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (activeDocument.IsLocal)
        {
            SystemCatalog.TryReplaceLocalReport(activeDocument);
        }
        else
        {
            var simulations = SystemCatalog.Systems
                .Where(system => !system.IsLocal
                    && !StringComparer.OrdinalIgnoreCase.Equals(system.Id, activeDocument.Id))
                .Append(activeDocument)
                .ToArray();
            SystemCatalog.ReplaceSimulations(simulations);
        }

        if (SystemCatalog.Find(state.ActiveDocumentId) is null)
        {
            return false;
        }

        RestoredUiState = new WorkspaceUiState(
            state.ActiveDocumentId,
            state.ActiveCategory,
            state.RememberedProviderKeys.ToDictionary(pair => pair.Key, pair => pair.Value),
            state.HighlightedTopologyProviderKey);
        _restoreInProgress = true;
        try
        {
            ApplyRestoredUiState();
            _persistAllowed = false;
            _startupPreviewApplied = true;
            return true;
        }
        finally
        {
            _restoreInProgress = false;
        }
    }

    private async Task<StorageSystemDocument?> TryLoadCachedLocalAsync()
    {
        try
        {
            return await _machineRecordService.LoadLocalScanAsync();
        }
        catch (Exception exception) when (
            exception is InventoryScanException
                or IOException
                or InvalidDataException
                or InvalidOperationException)
        {
            return null;
        }
    }

    private bool _restoreInProgress;

    private bool _persistAllowed;

    private bool _workspaceStateLoadAttempted;

    /// <summary>
    /// Persist only after restore has finished applying, or after the user
    /// makes a selection. Saving earlier overwrites a remembered local
    /// object with the empty startup placeholder.
    /// </summary>
    public bool CanPersistWorkspaceUiState =>
        WorkspaceUiRestorePolicy.ShouldPersist(_restoreInProgress, _persistAllowed);

    /// <summary>
    /// Loads persisted Manage-page system/object selection once the catalog
    /// is populated. Call again after the local inventory becomes real so
    /// objects that were missing on the startup placeholder can resolve.
    /// </summary>
    public async Task RestoreWorkspaceUiStateAsync()
    {
        if (_restoreInProgress)
        {
            return;
        }

        _restoreInProgress = true;
        try
        {
            await EnsureWorkspaceUiStateLoadedAsync();
            if (_startupPreviewApplied)
            {
                var restoredSystem = string.IsNullOrWhiteSpace(RestoredUiState?.ActiveSystemId)
                    ? null
                    : SystemCatalog.Find(RestoredUiState.ActiveSystemId);
                if (restoredSystem is null
                    && SystemCatalog.Systems.FirstOrDefault(system => system.IsLocal) is { } local)
                {
                    SwitchSystem(local.Id);
                }
                _startupPreviewApplied = false;
            }
            if (RestoredUiState?.ActiveSystemId is { Length: > 0 } restoredId && SystemCatalog.Contains(restoredId)
                && SystemCatalog.Find(restoredId) is null)
                await SelectSystemAsync(restoredId);
            ApplyRestoredUiState();
        }
        finally
        {
            _restoreInProgress = false;
        }
    }

    private async Task EnsureWorkspaceUiStateLoadedAsync()
    {
        if (_workspaceStateLoadAttempted)
        {
            return;
        }

        _workspaceStateLoadAttempted = true;
        try
        {
            RestoredUiState = await _workspaceStateService.LoadAsync();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or InvalidOperationException)
        {
            RestoredUiState = null;
        }
    }

    private void ApplyRestoredUiState()
    {
        if (RestoredUiState is null)
        {
            _persistAllowed = true;
            return;
        }

        var state = RestoredUiState;
        var target = string.IsNullOrWhiteSpace(state.ActiveSystemId)
            ? null
            : SystemCatalog.Find(state.ActiveSystemId);
        if (target is not null)
        {
            SwitchSystem(state.ActiveSystemId);
        }

        var resolveDocument = target ?? SelectedSystem;
        if (state.CategorySelections is not null)
        {
            foreach (var pair in state.CategorySelections)
            {
                var restored = ResolveSelection(resolveDocument, pair.Key, pair.Value);
                if (restored is not null)
                {
                    _categorySelections[(restored.Id.System, restored.Category)] = restored;
                }
            }
        }

        if (SelectedCategory != state.Category)
        {
            SelectedCategory = state.Category == ManageWorkspaceCategory.Volume ? ManageWorkspaceCategory.Partition : state.Category;
        }

        var topologyTarget = ResolveTopologyTarget(
            SelectedSystem,
            state.HighlightedTopologyStableId);
        if (topologyTarget is not null)
        {
            _selectedTopologyTarget = topologyTarget;
        }

        var remembered = RememberedSelection(state.Category)
            ?? ResolveSelection(
                resolveDocument,
                state.Category,
                WorkspaceUiRestorePolicy.WantedObjectKey(state));
        if (remembered is null && state.Category == ManageWorkspaceCategory.System)
        {
            remembered = SelectionForCurrentSystemList();
        }

        RebuildObjects(remembered);
        _persistAllowed = WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            state,
            SelectedSystem.Id,
            SystemCatalog.Find(state.ActiveSystemId) is not null,
            remembered is not null);
    }

    public async Task InitializePreferencesAsync()
    {
        if (_preferencesInitialized)
        {
            return;
        }

        var preferences = await _preferencesCoordinator.LoadAsync();
        Localization.Language = preferences.Language;
        _preferencesInitialized = true;
    }

    public async Task RefreshPreferencesAsync(bool refreshLocalizedContent = true)
    {
        var preferences = await _preferencesCoordinator.LoadAsync();
        Localization.Language = preferences.Language;
        if (refreshLocalizedContent)
        {
            RefreshLocalizedContent();
        }
    }

    public WorkspaceUiState? RestoredUiState { get; private set; }

    public WorkspaceUiState CaptureUiState() =>
        new(
            SelectedSystem.Id,
            SelectedCategory,
            _categorySelections
                .Where(pair => pair.Key.System == ActiveDocument.SystemId)
                .ToDictionary(
                    pair => pair.Key.Category,
                    pair => pair.Value.Id.ProviderKey),
            _selectedTopologyTarget?.Id.ProviderKey ?? string.Empty);

    public UserPreferences CurrentPreferences { get; private set; } = new();

    /// <summary>
    /// Background preferences owned by the Agent. Read-only here: changes go
    /// through the typed Agent request and come back through
    /// ApplyAgentPreferences or the saved-response snapshot.
    /// </summary>
    public AgentPreferences CurrentAgentPreferences { get; private set; } = new();

    public bool AgentPreferencesLoaded { get; private set; }

    public void ApplyAgentPreferences(AgentPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        CurrentAgentPreferences = preferences;
        AgentPreferencesLoaded = true;
        OnPropertyChanged(nameof(CurrentAgentPreferences));
    }

    public double TopologyHorizontalOffset { get; set; }

    public double TopologyVerticalOffset { get; set; }

    public async Task SetThemeAsync(ThemePreference theme)
    {
        await UpdateCurrentPreferencesAsync(preferences => preferences with { Theme = theme });
    }

    public async Task SetAccentColorAsync(AccentColorPreference accentColor)
    {
        await UpdateCurrentPreferencesAsync(preferences => preferences with { AccentColor = accentColor });
    }

    public async Task SetLanguageAsync(LanguagePreference language)
    {
        await UpdateCurrentPreferencesAsync(preferences => preferences with { Language = language });
        Localization.Language = language;
        RefreshLocalizedContent();
    }

    public async Task SetDeveloperModeAsync(bool enabled)
    {
        await UpdateCurrentPreferencesAsync(preferences => preferences with { DeveloperMode = enabled });
    }

    public async Task SetLastActivePageAsync(string page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return;
        }

        await UpdateCurrentPreferencesAsync(preferences => preferences with { LastActivePage = page }, cancellationToken);
    }

    public async Task SetCreateMsrOnInitializeAsync(bool create)
    {
        await UpdateCurrentPreferencesAsync(preferences => preferences with { CreateMsrOnInitialize = create });
    }

    public async Task SetPartitionIgnoreSizeMibAsync(double mib)
    {
        if (!double.IsFinite(mib))
        {
            throw new ArgumentOutOfRangeException(nameof(mib));
        }

        var bytes = (long)Math.Round(Math.Clamp(mib, 0, 1024) * 1024d * 1024d);
        await UpdateCurrentPreferencesAsync(preferences => preferences with { PartitionIgnoreSizeBytes = bytes });
    }

    public async Task SetContinuousMonitoringAsync(bool enabled)
    {
        await SendAgentPreferenceAsync(
            AgentPreferenceField.ContinuousMonitoringEnabled,
            enabled,
            null,
            null);
    }

    public async Task SetMonitoringSampleRateAsync(double rateHz)
    {
        if (!double.IsFinite(rateHz) || rateHz is < 0.2 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(rateHz));
        }

        await SendAgentPreferenceAsync(
            AgentPreferenceField.MonitoringSampleRateHz,
            null,
            rateHz,
            null);
    }

    public async Task SetStartAgentAtLoginAsync(bool enabled)
    {
        await SendAgentPreferenceAsync(
            AgentPreferenceField.StartAgentAtLogin,
            enabled,
            null,
            null);
    }

    public async Task SetSevenZipExecutablePathAsync(string? executablePath)
    {
        await SendAgentPreferenceAsync(
            AgentPreferenceField.SevenZipExecutablePath,
            null,
            null,
            executablePath);
    }

    /// <summary>
    /// Resets every preference and every built-in simulation document to its
    /// initial state. User-created simulation documents are preserved.
    /// Background preferences are also reset; when the Agent is unavailable
    /// they are skipped and the caller is told through the return value.
    /// </summary>
    public async Task<bool> ResetAllToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new UserPreferences();
        await ReplaceCurrentPreferencesAsync(defaults, cancellationToken);
        Localization.Language = defaults.Language;
        RefreshLocalizedContent();

        foreach (var entry in SystemCatalog.Entries.Where(entry => BuiltInSimulationCatalogPolicy.IsBuiltIn(entry.Id)).ToArray())
        {
            if (SystemCatalog.Find(entry.Id) is null && await _systemRepository.LoadSimulationAsync(entry.Id, cancellationToken) is { } loaded)
                SystemCatalog.CacheSimulation(loaded);
        }
        var resetPlan = BuiltInSimulationCatalogPolicy.PlanReset(
            SystemCatalog.Systems,
            SimulationCatalog.CreateDocuments());
        foreach (var reset in resetPlan.DocumentsToPersist)
        {
            try
            {
                await _simulationSaves.PrepareDeleteAsync(reset.Id, cancellationToken);
                await _systemRepository.SaveSimulationAsync(reset, cancellationToken);
            }
            catch { _simulationSaves.CancelDelete(reset.Id); throw; }
            _simulationSaves.Forget(reset.Id);
            if (SystemCatalog.Find(reset.Id) is null)
            {
                SystemCatalog.AddSimulation(reset);
            }
            else
            {
                SystemCatalog.Update(reset);
            }

            if (SelectedSystem.Id.Equals(reset.Id, StringComparison.OrdinalIgnoreCase))
            {
                SelectedSystem = reset;
                OnPropertyChanged(nameof(SelectedSystem));
                OnPropertyChanged(nameof(ActiveDocument));
                OnPropertyChanged(nameof(ActiveSnapshot));
                RebuildTopology();
                RebuildObjects(_selectedSelection);
            }
        }

        if (resetPlan.MarkCatalogSeededAfterPersist)
        {
            await UpdateCurrentPreferencesAsync(
                preferences => preferences with { BuiltInSimulationCatalogSeeded = true },
                cancellationToken);
        }

        var backgroundResetFailed = false;
        try
        {
            await SetContinuousMonitoringAsync(false);
            await SetMonitoringSampleRateAsync(5);
            await SetSevenZipExecutablePathAsync(null);
            await SetStartAgentAtLoginAsync(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            backgroundResetFailed = true;
        }

        return backgroundResetFailed;
    }

    private async Task SendAgentPreferenceAsync(
        AgentPreferenceField field,
        bool? booleanValue,
        double? numberValue,
        string? textValue)
    {
        if (_agentConnection is null)
        {
            throw new InvalidOperationException(
                "The WinPool tray Agent is unavailable; background settings cannot be changed.");
        }

        var result = await _agentConnection.SendAsync(
            new SetAgentPreferenceRequest(
                field,
                booleanValue,
                numberValue,
                CorrelationId.New(),
                textValue),
            CancellationToken.None);
        if (!result.IsSuccess
            || result.Value is not AgentPreferenceSavedResponse saved)
        {
            var code = result.Messages.FirstOrDefault()?.Code ?? "agent.preference.request_failed";
            throw new InvalidOperationException($"Agent preference change failed: {code}");
        }

        CurrentAgentPreferences = saved.Preferences;
        AgentPreferencesLoaded = true;
        OnPropertyChanged(nameof(CurrentAgentPreferences));
    }

    public async Task<string?> ExportActiveSystemAsync(
        CancellationToken cancellationToken = default)
    {
        var documentId = ActiveDocument.Id;
        try { await _simulationSaves.FlushAsync(documentId, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            var draft = SystemCatalog.Find(documentId)!;
            if (ConfirmLocalDraftExportAsync is null || !await ConfirmLocalDraftExportAsync(draft, exception)) return null;
            return await ImportExportService.ExportAsync(draft, cancellationToken);
        }
        return await ImportExportService.ExportAsync(SystemCatalog.Find(documentId)!, cancellationToken);
    }

    public async Task<bool> ImportSystemAsync(CancellationToken cancellationToken = default)
    {
        var imported = await ImportExportService.ImportAsync(cancellationToken);
        if (imported is null)
        {
            return false;
        }
        await _systemRepository.SaveSimulationAsync(imported, cancellationToken);
        SystemCatalog.AddSimulation(imported);
        SelectedSystem = imported;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
        RebuildTopology();
        RebuildObjects(RememberedSelection(SelectedCategory));
        RaiseWorkspaceSelectionChanged();
        return true;
    }

    public async Task<WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt>> ApplySimulationPlanAsync(
        WinPool.Application.SimulationDraftPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Steps.Any(IsBlockedByOfflineDisk))
        {
            return OfflineEditRejected();
        }
        var result = await _simulationEditCoordinator.ExecutePlanAsync(plan, cancellationToken);
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

    public async Task<WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt>> ApplySimulationOperationAsync(
        WinPool.Application.SimulationEditRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsBlockedByOfflineDisk(request))
        {
            return OfflineEditRejected();
        }
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

    private bool IsBlockedByOfflineDisk(WinPool.Application.SimulationEditRequest request)
    {
        if (request.Kind is WinPool.Application.SimulationEditKind.SetDiskOffline
            or WinPool.Application.SimulationEditKind.ResetDocument)
        {
            return false;
        }

        var snapshot = ActiveSnapshot;
        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { request.TargetProviderKey };
        foreach (var memberId in request.MemberDiskIds ?? [])
        {
            targetIds.Add(memberId);
        }

        return WinPool.Application.StorageEditRules.TouchesOfflineDisk(snapshot, targetIds);
    }

    private static WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt> OfflineEditRejected()
    {
        var correlation = CorrelationId.New();
        return WinPool.Application.ApplicationResult<WinPool.Application.SimulationEditReceipt>.FromStatus(
            WinPool.Application.ApplicationStatus.Rejected,
            correlation,
            new WinPool.Application.ApplicationMessage(
                "simulation.disk-offline.read-only",
                "An offline disk is read-only. Bring it online before changing it.",
                string.Empty,
                WinPool.Application.ApplicationMessageSeverity.Warning,
                []));
    }

    private Task CommitSimulationDocumentAsync(
        SimulationEditCommit commit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var operationId = new OperationId(Guid.ParseExact(commit.Intent.CommitId, "N"));
        var document = _simulationSaves.Accept(commit);
        StorageOperationTiming.Record("simulation.edit.queue", started, operationId,
            document.Id, scope: "simulation", generation: commit.Intent.ExpectedRevision);
        started = Stopwatch.GetTimestamp();
        SystemCatalog.Update(document);
        if (SelectedSystem.Id == document.Id)
        {
            SelectedSystem = document;
            OnPropertyChanged(nameof(SelectedSystem));
            OnPropertyChanged(nameof(ActiveDocument));
            OnPropertyChanged(nameof(ActiveSnapshot));
        }
        RebuildTopology();
        RebuildObjects(_selectedSelection);
        StorageOperationTiming.Record("simulation.edit.publish", started, operationId,
            document.Id, scope: "simulation", generation: commit.Intent.ExpectedRevision);
        return Task.CompletedTask;
    }

    /// <summary>
    /// All App preference entry points share the same serialized read/merge/save/apply path.
    /// </summary>
    private Task UpdateCurrentPreferencesAsync(
        Func<UserPreferences, UserPreferences> update,
        CancellationToken cancellationToken = default) =>
        _preferencesCoordinator.UpdateAsync(update, cancellationToken);

    private Task ReplaceCurrentPreferencesAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken) =>
        _preferencesCoordinator.ReplaceAsync(preferences, cancellationToken);



    public async Task<string?> EnterRealModeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseRealMode || _realModeSession is null)
            return "agent.real_mode.unavailable";

        try
        {
            var error = await _realModeSession.EnterAsync(cancellationToken);
            if (error is not null)
            {
                TrySetExecutionMode(ExecutionMode.Simulation);
                if (error == "agent.real_operation.write_barrier")
                    await RecoverRealOperationsAsync();
                return error;
            }
            TrySetExecutionMode(ExecutionMode.Real);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TrySetExecutionMode(ExecutionMode.Simulation);
            return exception.Message;
        }
    }

    public async Task<string?> ExitRealModeAsync(CancellationToken cancellationToken = default)
    {
        TrySetExecutionMode(ExecutionMode.Simulation);
        if (_realModeSession is null)
            return "agent.real_mode.unavailable";

        try
        {
            var error = await _realModeSession.ExitAsync(cancellationToken);
            await RecoverRealOperationsAsync();
            return error;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    public bool TrySetExecutionMode(ExecutionMode mode)
    {
        if (mode == ExecutionMode.Real && _realModeSession?.IsArmed != true)
            return false;
        if (mode == ExecutionMode.Simulation)
            _realModeSession?.DisarmLocally();
        var changed = Execution.TrySetMode(mode);
        OnPropertyChanged(nameof(IsRealMode));
        OnPropertyChanged(nameof(CanUseRealMode));
        OnPropertyChanged(nameof(CanSubmitRealOperation));
        return changed;
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        _ = await ScanCoreAsync(CollectionPurpose.Storage, CancellationToken.None);
    }

    public bool ApplyLocalInventory(StorageSystemDocument document)
    {
        if (!SystemCatalog.TryReplaceLocalReport(document)) return false;
        var restoring = _restoreInProgress;
        _restoreInProgress = true;
        try
        {
            // The startup placeholder need not have the persisted local ID.
            // Keep a selected simulation selected when a background report arrives.
            if (SelectedSystem.IsLocal)
            {
                SelectedSystem = document;
                OnPropertyChanged(nameof(SelectedSystem));
                OnPropertyChanged(nameof(ActiveDocument));
                OnPropertyChanged(nameof(ActiveSnapshot));
                OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
                OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
            }
            RefreshLocalizedContent();
            if (_workspaceStateLoadAttempted && !_persistAllowed) ApplyRestoredUiState();
            OnPropertyChanged(nameof(Snapshot));
        }
        finally { _restoreInProgress = restoring; }
        return true;
    }

    public Task<ManualScanOutcome> RefreshHardwareAsync(CancellationToken cancellationToken) =>
        SelectedSystem.IsLocal
            ? ScanCoreAsync(CollectionPurpose.Hardware, cancellationToken)
            : Task.FromResult(ManualScanOutcome.Skipped);

    private long _systemSelectionGeneration;
    public async Task<bool> SelectSystemAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var generation = ++_systemSelectionGeneration;
        if (!SystemCatalog.Contains(systemId)) return false;
        if (SystemCatalog.Find(systemId) is null)
        {
            try
            {
                var document = await _systemRepository.LoadSimulationAsync(systemId, cancellationToken);
                if (generation != _systemSelectionGeneration || document is null) return false;
                SystemCatalog.CacheSimulation(document);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                if (generation == _systemSelectionGeneration)
                    _notificationService.PublishError("WinPool", exception.Message, "simulation", "simulation:load-failed");
                return false;
            }
        }
        if (generation != _systemSelectionGeneration) return false;
        return SwitchSystem(systemId);
    }

    private async Task<ManualScanOutcome> ScanCoreAsync(CollectionPurpose purpose, CancellationToken cancellationToken)
    {
        StorageSnapshot? completedSnapshot = null;
        var progressKey = ManualProgressKey(purpose);
        return await _manualScanRunner.RunAsync(async token =>
        {
            await WhenWorkspaceReady;
            IsScanning = true;
            ScanError = string.Empty;
            StatusMessage = Localization["Scanning"];
            _notificationService.DismissByKey(progressKey);
            PresentNotification(WinPool.Application.WorkspaceNotificationFactory.ScanStarted(progressKey));
            var localDocument = purpose == CollectionPurpose.Hardware
                ? await _hardwareInventoryProvider.CollectHardwareAsync(token)
                : await _hardwareInventoryProvider.CollectLocalAsync(token);
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
            if (!ApplyLocalInventory(localDocument)
                && !SystemCatalog.Systems.Any(document => document.IsLocal && document.Id == localDocument.Id
                    && document.UpdatedAt >= localDocument.UpdatedAt))
            {
                return false;
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
            PublishStorageFindings(snapshot);
            completedSnapshot = snapshot;
            return true;
        }, result =>
        {
            try
            {
                _notificationService.DismissByKey(progressKey);
                switch (result.Outcome)
                {
                    case ManualScanOutcome.Completed:
                        _notificationService.ResolveByKey(ManualFailedKey(purpose));
                        PresentNotification(WinPool.Application.WorkspaceNotificationFactory.ScanCompleted(
                            StatusMessage,
                            completedSnapshot!.ScannedAt,
                            ManualCompletedKey(purpose)));
                        break;
                    case ManualScanOutcome.Canceled:
                    case ManualScanOutcome.Skipped:
                        StatusMessage = string.Empty;
                        break;
                    case ManualScanOutcome.Failed:
                    {
                        var diagnostic = result.Error is InventoryScanException inventoryFailure
                            ? BoundInventoryDiagnostic(inventoryFailure.Diagnostic)
                            : string.Empty;
                        ScanError = string.IsNullOrWhiteSpace(diagnostic)
                            ? $"{Localization["ScanFailed"]} {result.Error!.Message}"
                            : $"{Localization["ScanFailed"]} {result.Error!.Message}{Environment.NewLine}{diagnostic}";
                        StatusMessage = ScanError;
                        var failure = WinPool.Application.WorkspaceNotificationFactory.ScanFailed(
                            ManualFailedKey(purpose));
                        PresentNotification(string.IsNullOrWhiteSpace(diagnostic)
                            ? failure
                            : failure with { UserDetailText = diagnostic });
                        break;
                    }
                }
            }
            finally
            {
                IsScanning = false;
            }
        }, cancellationToken);
    }

    private static string ManualProgressKey(CollectionPurpose purpose) => $"inventory:manual:{purpose}:scanning";

    private static string ManualCompletedKey(CollectionPurpose purpose) => $"inventory:manual:{purpose}:completed";

    private static string ManualFailedKey(CollectionPurpose purpose) => $"inventory:manual:{purpose}:failed";

    private static string BoundInventoryDiagnostic(string diagnostic) =>
        diagnostic.Length <= 2048 ? diagnostic : diagnostic[..2048];

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
            StorageUnitKind.SyntheticStoragePool => WinPool.Application.ManageObjectRole.SyntheticStoragePool,
            StorageUnitKind.SyntheticStorageTier => WinPool.Application.ManageObjectRole.SyntheticStorageTier,
            StorageUnitKind.PhysicalDisk => WinPool.Application.ManageObjectRole.PhysicalDisk,
            StorageUnitKind.VirtualDisk => WinPool.Application.ManageObjectRole.VirtualDisk,
            StorageUnitKind.NetworkDisk => WinPool.Application.ManageObjectRole.NetworkDisk,
            StorageUnitKind.OsDisk => WinPool.Application.ManageObjectRole.OsDisk,
            StorageUnitKind.Partition => WinPool.Application.ManageObjectRole.Partition,
            StorageUnitKind.Volume => WinPool.Application.ManageObjectRole.Volume,
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
                    or WinPool.Application.ManageObjectRole.SyntheticStoragePool
                    or WinPool.Application.ManageObjectRole.SyntheticStorageTier
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
            document => document.SystemId == objectId.System)
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
        WinPool.Application.ManageObjectRole.SyntheticStoragePool
            or WinPool.Application.ManageObjectRole.SyntheticStorageTier => WinPool.Domain.StorageObjectKind.LogicalGroup,
        WinPool.Application.ManageObjectRole.PhysicalDisk => WinPool.Domain.StorageObjectKind.PhysicalDisk,
        WinPool.Application.ManageObjectRole.VirtualDisk => WinPool.Domain.StorageObjectKind.VirtualDisk,
        WinPool.Application.ManageObjectRole.OsDisk => WinPool.Domain.StorageObjectKind.OsDisk,
        WinPool.Application.ManageObjectRole.Partition => WinPool.Domain.StorageObjectKind.Partition,
        WinPool.Application.ManageObjectRole.Volume => WinPool.Domain.StorageObjectKind.Volume,
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

    /// <summary>
    /// Per-surface viewport fallbacks. There is deliberately no shared
    /// topology viewport width: Manage, Edit upper, and Edit lower each
    /// keep their own host width on their root view models, so resizing
    /// one surface can never mutate another surface's layout inputs.
    /// </summary>
    public const double DefaultSurfaceViewportWidth = 1400;

    public const double MinSurfaceViewportWidth = 320;

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
        var languageChanged = _renderedLanguage != Localization.EffectiveLanguage;
        var previousSelection = SelectedWorkspaceItem;
        if (languageChanged)
        {
            _renderedLanguage = Localization.EffectiveLanguage;
            Categories.Clear();
            Categories.Add(new CategoryItem(ManageWorkspaceCategory.System, Localization["System"], "\uE7F8"));
            Categories.Add(new CategoryItem(ManageWorkspaceCategory.Pool, Localization["Pool"], "\uE8F1"));
            Categories.Add(new CategoryItem(ManageWorkspaceCategory.Tier, Localization["Tier"], "\uE8FD"));
            Categories.Add(new CategoryItem(ManageWorkspaceCategory.Disk, Localization["Disk"], "\uEDA2"));
            Categories.Add(new CategoryItem(ManageWorkspaceCategory.Partition, Localization["Partition"], "\uE7C3"));
        }

        SelectedCategoryItem = Categories.FirstOrDefault(x => x.Category == category);
        OnPropertyChanged(nameof(SelectedCategoryTitle));
        RebuildObjects(
            RememberedSelection(SelectedCategory)
            ?? _selectedSelection
            ?? SelectionForCurrentSystemList());
        RebuildTopology();
        if (languageChanged)
        {
            RebuildComparisonColumns();
            if (ReferenceEquals(previousSelection, SelectedWorkspaceItem)) BuildDetails();
        }
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
                    zh ? "存储池布局" : "Pool layout",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个容量不为 0 的性能层。请按各层的实际关联查看布局。"
                        : $"Pool {finding.TargetName} has more than one non-empty performance tier. Inspect the layout using each tier's actual associations."),
                StorageFindingKind.MultipleCapacityTiers => (
                    zh ? "存储池布局" : "Pool layout",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个容量不为 0 的容量层。请按各层的实际关联查看布局。"
                        : $"Pool {finding.TargetName} has more than one non-empty capacity tier. Inspect the layout using each tier's actual associations."),
                StorageFindingKind.MultipleVirtualDisks => (
                    zh ? "存储池布局" : "Pool layout",
                    zh
                        ? $"存储池 {finding.TargetName} 含有多个虚拟磁盘。请按各虚拟磁盘的实际关联查看布局。"
                        : $"Pool {finding.TargetName} has more than one virtual disk. Inspect the layout using each virtual disk's actual associations."),
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

        if (category == ManageWorkspaceCategory.System)
        {
            foreach (var system in SystemCatalog.Systems)
            {
                var systemItem = MatchWorkspaceObject(system, category, providerKey);
                if (systemItem is not null)
                {
                    return SelectionFor(systemItem);
                }
            }
        }

        var item = MatchWorkspaceObject(document, category, providerKey);
        return item is null ? null : SelectionFor(item);
    }

    private ManageObjectListItemView? MatchWorkspaceObject(
        StorageSystemDocument document,
        ManageWorkspaceCategory category,
        string providerKey) =>
        ProjectionFor(document).WorkspaceObjects.FirstOrDefault(candidate =>
            candidate.Category == (category == ManageWorkspaceCategory.Volume ? ManageWorkspaceCategory.Partition : category)
            && candidate.Id.ProviderKey.Equals(document.Snapshot.ResolvePartitionUnion(providerKey)?.Id ?? providerKey, StringComparison.OrdinalIgnoreCase));

    private ManageObjectTarget? ResolveTopologyTarget(
        StorageSystemDocument document,
        string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var node = Flatten(ProjectionFor(document).Root).FirstOrDefault(candidate =>
            candidate.Id.ProviderKey.Equals(document.Snapshot.ResolvePartitionUnion(providerKey)?.Id ?? providerKey, StringComparison.OrdinalIgnoreCase));
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
        var systems = SystemCatalog.Systems.ToArray();
        var live = systems.Select(system => system.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _topologyRoots.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _topologyRoots.Remove(id); _projections.Remove(id);
        }
        for (var index = 0; index < systems.Length; index++)
        {
            var system = systems[index];
            if (!_topologyRoots.TryGetValue(system.Id, out var cached)
                || !ReferenceEquals(cached.Facts, system.SourceFacts) || cached.Name != system.DisplayName
                || cached.Language != Localization.EffectiveLanguage)
            {
                cached = (system.SourceFacts, system.DisplayName, Localization.EffectiveLanguage, CreateTopologyRoot(system));
                _topologyRoots[system.Id] = cached;
            }
            if (index >= TopologySystems.Count) TopologySystems.Add(cached.Root);
            else if (!ReferenceEquals(TopologySystems[index], cached.Root)) TopologySystems[index] = cached.Root;
        }
        while (TopologySystems.Count > systems.Length) TopologySystems.RemoveAt(TopologySystems.Count - 1);
        ApplySystemRootExpansion();
        RefreshTopologySelection();
    }

    private ManageSystemProjection ProjectionFor(StorageSystemDocument document)
    {
        if (_projections.TryGetValue(document.Id, out var cached)
            && ReferenceEquals(cached.Document.SourceFacts, document.SourceFacts)
            && cached.Document.DisplayName == document.DisplayName && cached.Document.Kind == document.Kind)
            return cached.Projection;
        var projected = _manageProjector.Project(document);
        _projections[document.Id] = (document, projected);
        return projected;
    }

    private TopologyNodeViewModel CreateTopologyRoot(StorageSystemDocument document)
    {
        var projected = ProjectionFor(document);
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
            document.Snapshot,
            isLayoutRoot: true);
    }

    private void RebuildObjects(ManageSelectionKey? preferredSelection)
    {
        var items = CreateWorkspaceItems(SelectedCategory).ToArray();
        var changed = items.Length != Objects.Count;
        for (var index = 0; index < items.Length; index++)
        {
            if (index >= Objects.Count) { Objects.Add(items[index]); changed = true; }
            else if (Objects[index] != items[index]) { Objects[index] = items[index]; changed = true; }
        }
        while (Objects.Count > items.Length) { Objects.RemoveAt(Objects.Count - 1); changed = true; }
        if (changed) RebuildComparisonColumns();

        var preferredMatch = preferredSelection is null
            ? null
            : Objects.FirstOrDefault(item =>
                item.Projection is not null
                && ManageSelectionRules.SameSelection(
                    SelectionFor(item.Projection),
                    preferredSelection));
        preferredMatch ??= preferredSelection is null
            ? null
            : Objects.FirstOrDefault(item =>
                item.Projection is not null
                && item.Projection.Id.ProviderKey.Equals(
                    preferredSelection.Id.ProviderKey,
                    StringComparison.OrdinalIgnoreCase)
                && item.Projection.Category == preferredSelection.Category);
        if (preferredMatch is null
            && preferredSelection is null
            && SelectedCategory == ManageWorkspaceCategory.System)
        {
            preferredMatch = Objects.FirstOrDefault(item =>
                item.StorageSystemId is not null
                && item.StorageSystemId.Equals(SelectedSystem.Id, StringComparison.OrdinalIgnoreCase));
        }

        SelectedWorkspaceItem = preferredMatch
            ?? (preferredSelection is null ? Objects.FirstOrDefault() : null);
    }

    private ManageSelectionKey? SelectionForCurrentSystemList()
    {
        if (SelectedCategory != ManageWorkspaceCategory.System)
        {
            return _selectedSelection;
        }

        var item = ProjectionFor(SelectedSystem).WorkspaceObjects.FirstOrDefault(
            candidate => candidate.Category == ManageWorkspaceCategory.System);
        return item is null ? null : SelectionFor(item);
    }

    private IEnumerable<WorkspaceItem> CreateWorkspaceItems(ManageWorkspaceCategory category)
    {
        if (category == ManageWorkspaceCategory.System)
        {
            foreach (var entry in SystemCatalog.Entries)
            {
                var system = SystemCatalog.Find(entry.Id);
                if (system is null)
                {
                    var prefix = Localization.EffectiveLanguage == LanguagePreference.ZhCn ? "[模拟]" : "[Simulation]";
                    yield return new WorkspaceItem(entry.Id, $"{prefix} {entry.DisplayName}",
                        new StorageUnitRef(entry.Id, StorageUnitKind.System, entry.DisplayName), false, entry.Id);
                    continue;
                }
                var projection = ProjectionFor(system);
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

        var activeProjection = ProjectionFor(ActiveDocument);
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
            WinPool.Application.ManageObjectRole.SyntheticStoragePool => StorageUnitKind.SyntheticStoragePool,
            WinPool.Application.ManageObjectRole.SyntheticStorageTier => StorageUnitKind.SyntheticStorageTier,
            WinPool.Application.ManageObjectRole.PhysicalDisk => StorageUnitKind.PhysicalDisk,
            WinPool.Application.ManageObjectRole.VirtualDisk => StorageUnitKind.VirtualDisk,
            WinPool.Application.ManageObjectRole.OsDisk => StorageUnitKind.OsDisk,
            WinPool.Application.ManageObjectRole.Partition => StorageUnitKind.Partition,
            WinPool.Application.ManageObjectRole.Volume => StorageUnitKind.Volume,
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
            WinPool.Application.ManageObjectRole.SyntheticStoragePool
                or WinPool.Application.ManageObjectRole.SyntheticStorageTier =>
                LocalizeSyntheticName(item.Metadata.GetValueOrDefault("syntheticName")),
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
                item.ParentProviderKey,
                SyntheticNameFor(item)),
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
        if (item.Projection.Id.System != document.SystemId)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            NotifySelectionState();
            return;
        }

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
        var next = new List<ComparisonColumn>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Objects.Where(x => !x.IsAction && x.Projection is not null))
        {
            var document = item.StorageSystemId is null
                ? ActiveDocument
                : SystemCatalog.Find(item.StorageSystemId) ?? ActiveDocument;
            live.Add(item.Key);
            if (!_comparisonCache.TryGetValue(item.Key, out var cached)
                || !ReferenceEquals(cached.Facts, document.SourceFacts) || !ReferenceEquals(cached.Projection, item.Projection)
                || cached.Title != item.Title || cached.Language != Localization.EffectiveLanguage)
            {
                cached = (document.SourceFacts, item.Projection!, item.Title, Localization.EffectiveLanguage,
                    new ComparisonColumn(item.Key, item.Title, BuildProjectedComparisonRows(item.Projection!, document)));
                _comparisonCache[item.Key] = cached;
            }
            next.Add(cached.Column);
        }
        foreach (var key in _comparisonCache.Keys.Where(key => !live.Contains(key)).ToArray()) _comparisonCache.Remove(key);
        for (var index = 0; index < next.Count; index++)
            if (index >= ComparisonColumns.Count) ComparisonColumns.Add(next[index]);
            else if (!ReferenceEquals(ComparisonColumns[index], next[index])) ComparisonColumns[index] = next[index];
        while (ComparisonColumns.Count > next.Count) ComparisonColumns.RemoveAt(ComparisonColumns.Count - 1);

        SelectComparisonColumn();
    }

    private void SelectComparisonColumn()
    {
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
            WinPool.Application.ManageValuePresentation.SerialNumber => property.RawValue,
            WinPool.Application.ManageValuePresentation.ProductName =>
                ProductDisplayName(property.RawValue),
            WinPool.Application.ManageValuePresentation.LocalDateTime =>
                DateTimeOffset.TryParse(property.RawValue, out var timestamp)
                    ? timestamp.LocalDateTime.ToString("G")
                    : string.Empty,
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

        if (item.Projection.Id.System != ActiveDocument.SystemId)
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
        WinPool.Application.ManageObjectRole.SyntheticStoragePool => Localization["SyntheticStoragePool"],
        WinPool.Application.ManageObjectRole.SyntheticStorageTier => Localization["SyntheticStorageTier"],
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

    private SyntheticStorageName? SyntheticNameFor(
        WinPool.Application.ManageObjectListItemView item) =>
        Enum.TryParse<SyntheticStorageName>(
            item.Metadata.GetValueOrDefault("syntheticName"),
            ignoreCase: false,
            out var name)
            ? name
            : null;

    private string LocalizeSyntheticName(string? rawName) =>
        Enum.TryParse<SyntheticStorageName>(rawName, ignoreCase: false, out var name)
            ? name switch
            {
                SyntheticStorageName.HotSpareLayer => Localization["HotSpareLayer"],
                SyntheticStorageName.RetiredLayer => Localization["RetiredLayer"],
                SyntheticStorageName.UnallocatedLayer => Localization["UnallocatedLayer"],
                SyntheticStorageName.OtherDiskPool => Localization["OtherDiskPool"],
                SyntheticStorageName.NetworkDiskPool => Localization["NetworkDiskPool"],
                _ => rawName ?? string.Empty
            }
            : rawName ?? string.Empty;

    private bool SwitchSystem(string? systemId, ManageSelectionKey? preferredSelection = null)
    {
        // Synchronous selections also invalidate an older outstanding body load.
        _systemSelectionGeneration++;
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }
        var system = SystemCatalog.Find(systemId);
        if (system is null)
        {
            return false;
        }

        if (ReferenceEquals(system, SelectedSystem))
        {
            return false;
        }

        var identityChanged = !string.Equals(system.Id, SelectedSystem.Id, StringComparison.OrdinalIgnoreCase);
        if (identityChanged && !SelectedSystem.IsLocal) _ = FlushOnSwitchAsync(SelectedSystem.Id);
        SelectedSystem = system;
        _contextUnit = null;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
        OnPropertyChanged(nameof(CanOpenSelectedPartition));
        OnPropertyChanged(nameof(ActiveSimulationSaveState));
        OnPropertyChanged(nameof(SimulationSaveStatus));
        RebuildTopology();
        RebuildObjects(preferredSelection ?? RememberedSelection(SelectedCategory) ?? SelectionForCurrentSystemList());
        if (identityChanged)
        {
            RaiseWorkspaceSelectionChanged();
        }

        return identityChanged;
    }

    private async Task FlushOnSwitchAsync(string documentId)
    {
        try { await _simulationSaves.FlushAsync(documentId); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _notificationService.PublishError("WinPool", exception.Message, "simulation", $"simulation:save:{documentId}");
        }
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
        "Network" => Localization["NetworkDisk"],
        "Primary" => Localization["PrimaryPartition"],
        "BasicData" => Localization["PrimaryPartition"],
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

    private void RaiseWorkspaceSelectionChanged()
    {
        if (WorkspaceUiRestorePolicy.CanArmPersistFromUserSelection(
                _restoreInProgress,
                _workspaceStateLoadAttempted))
        {
            _persistAllowed = true;
        }

        WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BindSelectedSystemToCatalog()
    {
        var current = SystemCatalog.Find(SelectedSystem.Id);
        if (current is null || ReferenceEquals(current, SelectedSystem))
        {
            return;
        }

        SelectedSystem = current;
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveSnapshot));
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsSelectedSystemLocalConsistent));
        OnPropertyChanged(nameof(IsUsingSimulatedInventory));
        OnPropertyChanged(nameof(CanDeleteSelectedSimulation));
        OnPropertyChanged(nameof(CanOpenSelectedPartition));
    }

}
