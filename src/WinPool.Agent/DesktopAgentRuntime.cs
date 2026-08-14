using WinPool.Application;
using Microsoft.Data.Sqlite;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Monitoring;
using WinPool.Infrastructure.Sqlite;
using WinPool.Ipc;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Testing;
using WinPool.Testing.Tools;
using WinPool.ToolManagement;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

internal sealed class DesktopAgentRuntime :
    IAgentRequestOperations,
    IAgentShutdownActions,
    IAgentShutdownTerminalActions
{
    private readonly TrayApplicationContext tray;
    private readonly AgentInstanceId instanceId;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly MonitorCsvExporter monitorCsvExporter;
    private readonly AgentProcessRegistry processRegistry;
    private readonly IExternalToolRegistry toolRegistry;
    private readonly ToolPathConfigurationCoordinator toolPathCoordinator;
    private readonly ExternalToolStateRepository toolStateRepository;
    private readonly TestRunRepository testRunRepository;
    private readonly UserTestPresetRepository userTestPresets;
    private readonly WorkspaceSessionStateRepository workspaceState;
    private readonly SimulationDocumentRepository simulationDocuments;
    private readonly CopyBatchRepository copyBatchRepository;
    private readonly CopyBatchRecoveryCoordinator copyBatchRecovery;
    private readonly CopyBatchExecutionCoordinator copyBatchExecutor;
    private readonly AgentTestRunWorkflow testRunWorkflow;
    private readonly TestRunStartCoordinator testRunStartCoordinator;
    private readonly DiteLegacyImportRepository diteLegacyImports;
    private readonly TestArtifactStore testArtifactStore;
    private readonly TestRunExporter testRunExporter;
    private readonly AgentInventoryCoordinator inventoryCoordinator;
    private readonly ElevatedBrokerProcessHost elevatedBrokerHost;
    private readonly AgentSystemSupportCoordinator systemSupportCoordinator;
    private readonly SystemSupportRecoveryCoordinator systemSupportRecovery;
    private readonly TestWorkerSupervisor testWorkerSupervisor;
    private readonly TestPowerPlanScope testPowerPlanScope;
    private readonly IStorageHealthEventSource storageHealthEventSource;
    private readonly StorageHealthEventRepository storageHealthEventRepository;
    private readonly AgentEventHub agentEvents;
    private readonly AgentLifecycleStateStore lifecycle;
    private readonly IProcessIncarnationVerifier processIncarnationVerifier;
    private readonly string mainApplicationExecutablePath;
    private readonly CancellationTokenSource storageHealthEventCancellation = new();
    private readonly object storageHealthEventSync = new();
    private readonly Queue<StorageHealthEvent> recentStorageHealthEvents = new();
    private readonly Task storageHealthEventTask;
    private readonly AgentTestCoordinator testCoordinator = new();

    public DesktopAgentRuntime(
        TrayApplicationContext tray,
        AgentInstanceId instanceId,
        MonitoringSessionCoordinator monitoring,
        MonitorCsvExporter monitorCsvExporter,
        AgentProcessRegistry processRegistry,
        IExternalToolRegistry toolRegistry,
        ToolPathConfigurationCoordinator toolPathCoordinator,
        ExternalToolStateRepository toolStateRepository,
        WorkerProcessRepository workerProcessRepository,
        TestRunRepository testRunRepository,
        UserTestPresetRepository userTestPresets,
        WorkspaceSessionStateRepository workspaceState,
        SimulationDocumentRepository simulationDocuments,
        CopyBatchRepository copyBatchRepository,
        DiteLegacyImportRepository diteLegacyImports,
        TestArtifactStore testArtifactStore,
        TestRunExporter testRunExporter,
        IInventoryProvider nativeInventoryProvider,
        IInventoryProvider legacyInventoryProvider,
        IHardwareInventoryProvider manageInventoryProvider,
        IInventoryComparer inventoryComparer,
        InventorySnapshotRepository inventorySnapshots,
        InventoryComparisonRepository inventoryComparisons,
        LocalInventoryDocumentRepository localInventoryDocument,
        LocalSystemIdentityResolver localSystemIdentity,
        IProcessIncarnationVerifier processIncarnationVerifier,
        string mainApplicationExecutablePath,
        TestWorkerProcessHost testWorkerHost,
        ElevatedBrokerProcessHost elevatedBrokerHost,
        SystemSupportAuditRepository systemSupportAuditRepository,
        SystemSupportRecoveryCoordinator systemSupportRecovery,
        TestProcessSchedulingScope testProcessSchedulingScope,
        ITemporaryPowerPlanPort testPowerPlanPort,
        ISystemSupportRecoveryStore systemSupportRecoveryStore,
        IStorageHealthEventSource storageHealthEventSource,
        StorageHealthEventRepository storageHealthEventRepository,
        IReadOnlyList<StorageHealthEvent> initialStorageHealthEvents,
        AgentEventHub agentEvents,
        AgentLifecycleStateStore lifecycle,
        IPhysicalDiskDeviceResolver? physicalDiskDeviceResolver = null)
    {
        this.tray = tray ?? throw new ArgumentNullException(nameof(tray));
        this.instanceId = instanceId;
        this.monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        this.monitorCsvExporter = monitorCsvExporter
            ?? throw new ArgumentNullException(nameof(monitorCsvExporter));
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.toolRegistry = toolRegistry
            ?? throw new ArgumentNullException(nameof(toolRegistry));
        this.toolPathCoordinator = toolPathCoordinator
            ?? throw new ArgumentNullException(nameof(toolPathCoordinator));
        this.toolStateRepository = toolStateRepository
            ?? throw new ArgumentNullException(nameof(toolStateRepository));
        ArgumentNullException.ThrowIfNull(workerProcessRepository);
        this.testRunRepository = testRunRepository
            ?? throw new ArgumentNullException(nameof(testRunRepository));
        this.userTestPresets = userTestPresets
            ?? throw new ArgumentNullException(nameof(userTestPresets));
        this.workspaceState = workspaceState
            ?? throw new ArgumentNullException(nameof(workspaceState));
        this.simulationDocuments = simulationDocuments
            ?? throw new ArgumentNullException(nameof(simulationDocuments));
        this.copyBatchRepository = copyBatchRepository
            ?? throw new ArgumentNullException(nameof(copyBatchRepository));
        copyBatchRecovery = new(this.copyBatchRepository);
        this.diteLegacyImports = diteLegacyImports
            ?? throw new ArgumentNullException(nameof(diteLegacyImports));
        this.testArtifactStore = testArtifactStore
            ?? throw new ArgumentNullException(nameof(testArtifactStore));
        this.testRunExporter = testRunExporter
            ?? throw new ArgumentNullException(nameof(testRunExporter));
        this.processIncarnationVerifier = processIncarnationVerifier
            ?? throw new ArgumentNullException(nameof(processIncarnationVerifier));
        if (string.IsNullOrWhiteSpace(mainApplicationExecutablePath)
            || !Path.IsPathFullyQualified(mainApplicationExecutablePath))
        {
            throw new ArgumentException(
                "The expected Main App executable path is required.",
                nameof(mainApplicationExecutablePath));
        }

        this.mainApplicationExecutablePath = Path.GetFullPath(mainApplicationExecutablePath);
        inventoryCoordinator = new(
            nativeInventoryProvider,
            legacyInventoryProvider,
            manageInventoryProvider,
            inventoryComparer,
            inventorySnapshots,
            inventoryComparisons,
            localInventoryDocument,
            localSystemIdentity,
            physicalDiskDeviceResolver ?? new WindowsPhysicalDiskDeviceResolver());
        ArgumentNullException.ThrowIfNull(testWorkerHost);
        this.elevatedBrokerHost = elevatedBrokerHost
            ?? throw new ArgumentNullException(nameof(elevatedBrokerHost));
        ArgumentNullException.ThrowIfNull(systemSupportAuditRepository);
        systemSupportCoordinator = new(
            instanceId,
            systemSupportAuditRepository,
            ExecuteElevatedBrokerAsync);
        this.systemSupportRecovery = systemSupportRecovery
            ?? throw new ArgumentNullException(nameof(systemSupportRecovery));
        ArgumentNullException.ThrowIfNull(testProcessSchedulingScope);
        ArgumentNullException.ThrowIfNull(testPowerPlanPort);
        ArgumentNullException.ThrowIfNull(systemSupportRecoveryStore);
        testPowerPlanScope = new(
            testPowerPlanPort,
            systemSupportRecoveryStore,
            systemSupportAuditRepository,
            SetActivePowerPlanForTestAsync);
        this.storageHealthEventSource = storageHealthEventSource
            ?? throw new ArgumentNullException(nameof(storageHealthEventSource));
        this.storageHealthEventRepository = storageHealthEventRepository
            ?? throw new ArgumentNullException(nameof(storageHealthEventRepository));
        this.agentEvents = agentEvents ?? throw new ArgumentNullException(nameof(agentEvents));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        testWorkerSupervisor = new(
            instanceId,
            testWorkerHost,
            processRegistry,
            workerProcessRepository,
            testRunRepository,
            testProcessSchedulingScope,
            this.agentEvents,
            this.tray);
        copyBatchExecutor = new(
            copyBatchRecovery,
            this.copyBatchRepository,
            testWorkerSupervisor,
            testArtifactStore,
            testRunRepository,
            monitoring,
            ExecuteRamMapBeforeBatchAsync,
            ExecuteFlushBetweenCopyBatchesAsync);
        testRunWorkflow = new(
            testCoordinator,
            testPowerPlanScope,
            testRunRepository,
            monitoring,
            testArtifactStore,
            this.copyBatchRepository,
            copyBatchRecovery,
            copyBatchExecutor,
            testWorkerSupervisor,
            this.tray,
            this.agentEvents,
            ExecuteRamMapBeforeBatchAsync);
        testRunStartCoordinator = new(
            instanceId,
            testRunRepository,
            toolRegistry,
            toolStateRepository,
            testCoordinator,
            testRunWorkflow,
            this.tray);
        ArgumentNullException.ThrowIfNull(initialStorageHealthEvents);
        foreach (var storageEvent in initialStorageHealthEvents.TakeLast(200))
        {
            recentStorageHealthEvents.Enqueue(storageEvent);
        }

        storageHealthEventTask = Task.Run(CaptureStorageHealthEventsAsync);
    }

    internal async Task<ElevatedBrokerExecutionResult> ExecuteElevatedBrokerAsync(
        ElevatedBrokerExecutionRequest request,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var brokerProcessId = 0;
        ProcessInstanceId? brokerProcessInstanceId = null;
        try
        {
            return await elevatedBrokerHost.ExecuteAsync(
                request,
                (processId, _) =>
                {
                    brokerProcessId = processId;
                    var now = DateTimeOffset.UtcNow;
                    brokerProcessInstanceId = ProcessInstanceId.New();
                    if (!processRegistry.TryRegister(
                            new AgentManagedProcess(
                                brokerProcessInstanceId.Value,
                                processId,
                                AgentManagedProcessKind.ElevatedBroker,
                                correlationId,
                                AgentProcessProjection.GetStartedAtUtc(processId),
                                now,
                                SupervisedProcessState.Starting,
                                OwnsJobObject: false,
                                ShutdownDeadlineUtc: null)) ||
                        !processRegistry.TryMarkRunning(
                            brokerProcessInstanceId.Value,
                            processId,
                            now))
                    {
                        throw new InvalidOperationException(
                            "The elevated Broker process identity was already registered.");
                    }

                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (brokerProcessId > 0 && brokerProcessInstanceId is { } processInstanceId)
            {
                processRegistry.TryMarkExited(
                    processInstanceId,
                    brokerProcessId,
                    DateTimeOffset.UtcNow,
                    out _);
            }
        }
    }

    public bool HasActiveTest => testCoordinator.HasActiveTest;

    public async Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
        GetAgentSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolStates = await toolRegistry.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshot = new AgentSnapshot(
            instanceId,
            tray.IsTrayVisible,
            ActiveMonitoringSession: monitoring.CurrentSession,
            ActiveTestRunId: testCoordinator.ActiveRunId,
            ShutdownStatus: lifecycle.Snapshot(),
            Processes: processRegistry.Snapshot()
                .Select(AgentProcessProjection.ToRegistration)
                .ToArray(),
            LatestMonitorSamples: monitoring.CurrentSamples,
            RecentStorageHealthEvents: GetRecentStorageHealthEvents(),
            MonitorDiagnostics: monitoring.CurrentDiagnostics,
            CurrentToolStates: toolStates.Value ?? []);
        return await SuccessAsync(new AgentSnapshotResponse(snapshot), request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> GetDevelopmentDiagnosticsAsync(
        GetDevelopmentDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RecentRunLimit is <= 0 or > 20)
        {
            return Reject(
                request.CorrelationId,
                "agent.development.invalid_recent_run_limit");
        }

        var snapshotResult = await GetSnapshotAsync(
            new GetAgentSnapshotRequest(request.CorrelationId),
            cancellationToken).ConfigureAwait(false);
        var snapshot = ((AgentSnapshotResponse)snapshotResult.Value!).Snapshot;
        var runs = await testRunRepository.ListRunsAsync(
            states: null,
            request.RecentRunLimit,
            cancellationToken).ConfigureAwait(false);
        var plans = new List<DevelopmentPlanDiagnostic>(runs.Count);
        foreach (var run in runs)
        {
            var plan = await testRunRepository.GetPlanAsync(
                run.RunId,
                cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                continue;
            }

            var persistedSteps = await testRunRepository.ListStepsAsync(
                run.RunId,
                cancellationToken).ConfigureAwait(false);
            plans.Add(DevelopmentDiagnosticsProjection.ProjectPlan(
                run,
                plan,
                persistedSteps));
        }

        var algorithms = DevelopmentDiagnosticsProjection.Algorithms(plans);
        return ApplicationResult<AgentResponse>.Succeeded(
            new DevelopmentDiagnosticsResponse(
                new DevelopmentDiagnostics(snapshot, plans, algorithms)),
            request.CorrelationId);
    }

    public Task<ApplicationResult<AgentResponse>> ReviewSystemSupportAsync(
        ReviewAgentSystemSupportRequest request,
        CancellationToken cancellationToken) =>
        systemSupportCoordinator.ReviewAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> ExecuteSystemSupportAsync(
        ExecuteAgentSystemSupportRequest request,
        CancellationToken cancellationToken) =>
        systemSupportCoordinator.ExecuteAsync(request, cancellationToken);

    internal static SystemSupportActionKind ToSystemSupportActionKind(
        ElevatedBrokerOperationKind operation) =>
        AgentSystemSupportCoordinator.ToSystemSupportActionKind(operation);

    private ValueTask WriteSystemSupportAuditAsync(
        ElevatedBrokerExecutionRequest executionRequest,
        CorrelationId correlationId,
        SystemSupportActionKind actionKind,
        SystemSupportAuditStage stage,
        string code,
        CancellationToken cancellationToken) =>
        systemSupportCoordinator.WriteAuditAsync(
            executionRequest,
            correlationId,
            actionKind,
            stage,
            code,
            cancellationToken);

    private static ApplicationMessage AgentMessage(
        string code,
        ApplicationMessageSeverity severity) =>
        new(code, code, string.Empty, severity, []);

    public Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
        OpenMainWindowRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tray.OpenMainApplication(request.Destination?.ToString());
        return SuccessAsync(new AgentAcknowledgement(), request.CorrelationId);
    }

    public Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync(
        OpenAgentNativePropertiesRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DiskNumber < 0)
        {
            return Task.FromResult(
                Reject(request.CorrelationId, "agent.native-properties.disk-number-invalid"));
        }

        var physicalDeviceId = inventoryCoordinator.ResolvePhysicalDeviceId(
            request.DiskNumber);

        if (!string.IsNullOrWhiteSpace(physicalDeviceId))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("devmgr.dll,DeviceProperties_RunDLL");
            startInfo.ArgumentList.Add("/DeviceID");
            startInfo.ArgumentList.Add(physicalDeviceId);
            Process.Start(startInfo);
            return SuccessAsync(new AgentAcknowledgement(), request.CorrelationId);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "diskmgmt.msc",
            UseShellExecute = true
        });
        return Task.FromResult<ApplicationResult<AgentResponse>>(new(
            ApplicationStatus.PartiallyCompleted,
            new AgentAcknowledgement(),
            [AgentMessage(
                "agent.native-properties.disk-management-fallback",
                ApplicationMessageSeverity.Warning)],
            request.CorrelationId));
    }

    public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken) =>
        StartMonitoringCoreAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken) =>
        StopMonitoringCoreAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> StartTestAsync(
        StartAgentTestRequest request,
        CancellationToken cancellationToken) =>
        testRunStartCoordinator.StartAsync(request, cancellationToken);
    public Task<ApplicationResult<AgentResponse>> CancelTestAsync(
        CancelAgentTestRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!testCoordinator.TryCancel(request.RunId))
        {
            return RejectAsync(
                request.CorrelationId,
                "agent.testing.run_not_active");
        }

        tray.SetTestRun(request.RunId, "cancelling");
        return SuccessAsync(new AgentAcknowledgement(), request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> GetTestResultAsync(
        GetAgentTestResultRequest request,
        CancellationToken cancellationToken)
    {
        var run = await testRunRepository.GetAsync(
            request.RunId,
            cancellationToken);
        if (run is null)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.RequiresEnvironment,
                request.CorrelationId,
                Message("agent.testing.result_not_found"));
        }

        var metrics = await testRunRepository.ListStepMetricsAsync(
            request.RunId,
            cancellationToken);
        var plan = await testRunRepository.GetPlanAsync(
            request.RunId,
            cancellationToken);
        var steps = await testRunRepository.ListStepsAsync(
            request.RunId,
            cancellationToken);
        var artifacts = await testArtifactStore.ListRunArtifactsAsync(
            request.RunId,
            cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new TestRunResultResponse(
                new(
                    run.RunId,
                    run.State.ToString(),
                    run.StartedAtUtc,
                    run.EndedAtUtc,
                    steps.Select(step => new TestStepResult(
                            step.StepId,
                            step.State.ToString(),
                            step.ToolId))
                        .ToArray(),
                    metrics.Select(metric => new TestResultMetric(
                            metric.MetricId,
                            metric.Value,
                            metric.Unit,
                            metric.Aggregation,
                            metric.StepId,
                            TestMetricSemanticsCatalog.Describe(
                                metric.StepId is null
                                    ? null
                                    : plan?.Steps.FirstOrDefault(step =>
                                        StringComparer.Ordinal.Equals(
                                            step.Id,
                                            metric.StepId)),
                                metric.MetricId,
                                metric.Unit)))
                        .ToArray(),
                    artifacts.Select(artifact => new TestResultArtifact(
                            artifact.RelativePath,
                            artifact.Sha256,
                            artifact.ByteLength,
                            artifact.MediaType))
                        .ToArray())),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ListTestRunsAsync(
        ListAgentTestRunsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Limit is <= 0 or > 200
            || !Enum.IsDefined(request.Filter))
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.history_filter_invalid");
        }

        IReadOnlyCollection<PersistedTestRunState>? states = request.Filter switch
        {
            TestRunHistoryFilter.All => null,
            TestRunHistoryFilter.Completed => [PersistedTestRunState.Completed],
            TestRunHistoryFilter.Failed => [PersistedTestRunState.Failed],
            TestRunHistoryFilter.Cancelled => [PersistedTestRunState.Cancelled],
            TestRunHistoryFilter.Active =>
            [
                PersistedTestRunState.Created,
                PersistedTestRunState.Running
            ],
            _ => null
        };
        var runs = await testRunRepository.ListRunsAsync(
            states,
            request.Limit,
            cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new TestRunHistoryResponse(
                runs.Select(item => new TestRunHistoryItem(
                        item.RunId,
                        item.DefinitionId,
                        item.State.ToString(),
                        item.StartedAtUtc,
                        item.EndedAtUtc))
                    .ToArray()),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ListUserTestPresetsAsync(
        ListUserTestPresetsRequest request,
        CancellationToken cancellationToken)
    {
        var presets = await userTestPresets.ListAsync(cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new UserTestPresetListResponse(presets),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> SaveUserTestPresetAsync(
        SaveUserTestPresetRequest request,
        CancellationToken cancellationToken)
    {
        if (!UserTestPresetValidator.IsValid(request.Preset))
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.preset_invalid");
        }

        var saved = await userTestPresets.SaveAsync(
            request.Preset,
            cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new UserTestPresetSavedResponse(saved),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> DeleteUserTestPresetAsync(
        DeleteUserTestPresetRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PresetId == Guid.Empty)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.preset_id_invalid");
        }

        var deleted = await userTestPresets.DeleteAsync(
            request.PresetId,
            cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new UserTestPresetDeletedResponse(request.PresetId, deleted),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
        LoadAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken)
    {
        var state = await workspaceState.LoadAsync(cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new WorkspaceStateLoadedResponse(state),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> SaveWorkspaceStateAsync(
        SaveAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.State is null
            || !WorkspaceSessionStateValidator.IsValid(request.State))
        {
            return Reject(request.CorrelationId, "agent.persistence.workspace_state_invalid");
        }
        try
        {
            var state = await workspaceState.SaveAsync(request.State, cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new WorkspaceStateSavedResponse(state),
                request.CorrelationId);
        }
        catch (ArgumentException)
        {
            return Reject(request.CorrelationId, "agent.persistence.workspace_state_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> ListSimulationDocumentsAsync(
        ListAgentSimulationDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var documents = await simulationDocuments.ListAsync(cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new SimulationDocumentListResponse(documents),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
        SaveAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await simulationDocuments.SaveAsync(
                request.Document,
                request.ExpectedPreviousSha256,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentSavedResponse(document),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or SimulationDocumentConflictException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_save_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
        DeleteAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await simulationDocuments.DeleteAsync(
                request.DocumentId,
                request.ExpectedSha256,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentDeletedResponse(request.DocumentId, deleted),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or SimulationDocumentConflictException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_delete_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
        CommitAgentSimulationEditRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await simulationDocuments.CommitEditAsync(
                request.Document,
                request.ExpectedPreviousSha256,
                request.Plan,
                request.Events,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentSavedResponse(document),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or InvalidDataException
            or SimulationDocumentConflictException or SqliteException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_edit_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> PersistDiteLegacyImportAsync(
        PersistDiteLegacyImportRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath)
            || request.ExpectedSha256.Length != 64
            || request.ExpectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.dite_import_request_invalid");
        }

        try
        {
            var import = await new DiteLegacyResultImporter()
                .ImportAsync(request.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    import.SourceSha256,
                    request.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    request.CorrelationId,
                    "agent.testing.dite_import_source_changed");
            }

            var saved = await diteLegacyImports.SaveAsync(
                    import,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return ApplicationResult<AgentResponse>.Succeeded(
                new DiteLegacyImportPersistenceResponse(
                    saved.Import.ImportId,
                    saved.AlreadyExisted,
                    saved.Import.RunCount,
                    saved.Import.MetricCount),
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                new ApplicationMessage(
                    "agent.testing.dite_import_failed",
                    "agent.testing.dite_import_failed",
                    exception.Message,
                    ApplicationMessageSeverity.Error,
                    []));
        }
    }

    public async Task<ApplicationResult<AgentResponse>> ListDiteLegacyImportsAsync(
        ListDiteLegacyImportsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Limit is <= 0 or > 200)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.dite_import_history_limit_invalid");
        }

        var imports = await diteLegacyImports
            .ListAsync(request.Limit, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult<AgentResponse>.Succeeded(
            new DiteLegacyImportHistoryResponse(
                imports.Select(item => new DiteLegacyImportHistoryItem(
                        item.ImportId,
                        item.SourceFileName,
                        item.SourceSha256,
                        item.ImportedAtUtc,
                        item.RunCount,
                        item.MetricCount))
                    .ToArray()),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>>
        GetDiteLegacyImportSummaryAsync(
            GetDiteLegacyImportSummaryRequest request,
            CancellationToken cancellationToken)
    {
        if (request.ImportId == Guid.Empty)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.dite_import_id_invalid");
        }

        var summaries = await diteLegacyImports
            .GetSummariesAsync(request.ImportId, cancellationToken)
            .ConfigureAwait(false);
        return summaries is null
            ? ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.RequiresEnvironment,
                request.CorrelationId,
                Message("agent.testing.dite_import_not_found"))
            : ApplicationResult<AgentResponse>.Succeeded(
                new DiteLegacyImportSummaryResponse(
                    request.ImportId,
                    summaries.Select(summary =>
                            summary.Semantic is null
                                ? summary with
                                {
                                    Semantic = TestMetricSemanticsCatalog
                                        .DescribeLegacy(
                                            summary.MetricId,
                                            summary.Unit)
                                }
                                : summary)
                        .ToArray()),
                request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ExportTestRunAsync(
        ExportAgentTestRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await testRunExporter.ExportAsync(
                request.RunId,
                request.Format,
                request.DestinationPath,
                request.UserConfirmedOverwrite,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ExportArtifactResponse(
                    result.DestinationPath,
                    result.Sha256,
                    result.ItemCount),
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                new ApplicationMessage(
                    "agent.testing.export_failed",
                    "agent.testing.export_failed",
                    exception.Message,
                    ApplicationMessageSeverity.Error,
                    []));
        }
    }

    public Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.CaptureManageAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.LoadManageAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.CaptureComparisonAsync(request, cancellationToken);

    public async Task<ApplicationResult<AgentResponse>> DetectToolAsync(
        DetectAgentToolRequest request,
        CancellationToken cancellationToken)
    {
        var result = await toolRegistry.DetectAsync(
            request.ToolId,
            cancellationToken);
        if (result.Value is { } state)
        {
            await toolStateRepository.SaveAsync(
                state,
                DateTimeOffset.UtcNow,
                cancellationToken);
            agentEvents.Publish(
                new AgentToolStateEvent(state, DateTimeOffset.UtcNow));
        }

        return result.Value is null
            ? new ApplicationResult<AgentResponse>(
                result.Status,
                null,
                result.Messages,
                request.CorrelationId)
            : new ApplicationResult<AgentResponse>(
                result.Status,
                new ToolStateResponse(result.Value),
                result.Messages,
                request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ConfigureToolPathAsync(
        ConfigureAgentToolPathRequest request,
        CancellationToken cancellationToken)
    {
        var detected = await toolPathCoordinator.ConfigureAsync(
            request.ToolId,
            request.ExecutablePath,
            request.CorrelationId,
            cancellationToken);
        if (detected.Value is not { } state)
        {
            return new(
                detected.Status,
                null,
                detected.Messages,
                request.CorrelationId);
        }

        await toolStateRepository.SaveAsync(
            state,
            DateTimeOffset.UtcNow,
            cancellationToken);
        agentEvents.Publish(new AgentToolStateEvent(state, DateTimeOffset.UtcNow));
        return new(
            detected.Status,
            new ToolStateResponse(state),
            detected.Messages,
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> InstallMsiToolAsync(
        InstallAgentMsiToolRequest request,
        CancellationToken cancellationToken)
    {
        var catalog = new ToolCatalog();
        if (!catalog.TryGet(request.Plan.ToolId, out var descriptor) ||
            descriptor.InstallerKind != ToolInstallerKind.Msi ||
            request.Plan.InstallerKind != ToolInstallerKind.Msi ||
            request.Plan.OfficialSource != descriptor.OfficialInstallSource ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                request.Plan.ExpectedSha256,
                descriptor.OfficialPackageSha256) ||
            !StringComparer.Ordinal.Equals(
                request.Plan.PlanHash,
                ToolInstallPlanHasher.Compute(
                    descriptor,
                    request.Plan.Location,
                    request.Plan.ExpectedSha256,
                    request.Plan.CreatedAtUtc,
                    request.Plan.ExpiresAtUtc)))
        {
            return RejectMsiInstall(request.CorrelationId, "agent.msi-install.plan-invalid");
        }

        var authorization = ToolInstallAuthorization.Authorize(
            request.Plan,
            request.UserConfirmed,
            DateTimeOffset.UtcNow,
            request.CorrelationId);
        if (!authorization.IsSuccess)
        {
            return new(
                authorization.Status,
                null,
                authorization.Messages,
                request.CorrelationId);
        }

        var brokerRequest = new ElevatedBrokerExecutionRequest(
            Guid.Empty,
            Guid.Empty,
            0,
            string.Empty,
            request.Plan.PlanHash,
            DateTimeOffset.MinValue,
            ElevatedBrokerOperationKind.InstallMsiTool,
            MsiToolInstall: new MsiToolInstallSnapshot(
                request.Plan.ToolId,
                request.PackageRelativePath,
                request.Plan.ExpectedSha256));
        var result = await ExecuteElevatedBrokerAsync(
            brokerRequest,
            request.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ApplicationResult<AgentResponse>.Succeeded(
                new MsiToolInstallResponse(result),
                request.CorrelationId)
            : RejectMsiInstall(request.CorrelationId, result.Code);
    }

    private static ApplicationResult<AgentResponse> RejectMsiInstall(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                code,
                ApplicationMessageSeverity.Error,
                []));

    public async Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
        ExportAgentMonitorCsvRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (monitoring.CurrentSession?.SessionId == request.SessionId)
            {
                var flush = await monitoring.FlushAsync(
                    request.SessionId,
                    cancellationToken);
                if (!flush.IsSuccess)
                {
                    return new(
                        flush.Status,
                        null,
                        flush.Messages,
                        request.CorrelationId);
                }
            }

            var export = await monitorCsvExporter.ExportAsync(
                request.SessionId,
                request.DestinationPath,
                request.UserConfirmedOverwrite,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ExportArtifactResponse(
                    export.DestinationPath,
                    export.Sha256,
                    export.RowCount),
                request.CorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId,
                Message("agent.monitor.export_cancelled"));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                Message("agent.monitor.export_failed"));
        }
    }

    public Task NotifyClientsAsync(
        ShutdownReason reason,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RequestTestCancellationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = testCoordinator.CancelActive();

        return Task.CompletedTask;
    }

    public async Task TerminateExternalToolJobsAsync(
        CancellationToken cancellationToken)
    {
        var task = testCoordinator.CancelActive();

        if (task is not null)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken)
    {
        var session = monitoring.CurrentSession;
        if (session is not null)
        {
            await monitoring.StopAsync(session.SessionId, cancellationToken);
            tray.SetMonitoringSession(null);
        }
    }

    public async Task<bool> RestoreTemporarySystemStateAsync(
        CancellationToken cancellationToken)
    {
        var summary = await systemSupportRecovery
            .RecoverPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        return summary.Failed == 0;
    }

    public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(0);

    public Task CloseNamedPipesAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task CloseMainApplicationAsync(CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(
                    AppExitSignal.CreateName(IpcIdentity.HashUserSid(sid)));
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
        }

        var registrations = processRegistry.Snapshot()
            .Where(item => item.Kind == AgentManagedProcessKind.MainApplication)
            .ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        foreach (var registration in registrations)
        {
            processRegistry.TryBeginStopping(
                registration.ProcessInstanceId,
                registration.ProcessId,
                deadline);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            var anyLive = false;
            foreach (var registration in registrations)
            {
                if (processIncarnationVerifier.Matches(
                        registration,
                        mainApplicationExecutablePath))
                {
                    anyLive = true;
                }
                else
                {
                    processRegistry.TryMarkExited(
                        registration.ProcessInstanceId,
                        registration.ProcessId,
                        DateTimeOffset.UtcNow,
                        out _);
                }
            }

            if (!anyLive)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        foreach (var registration in registrations)
        {
            processRegistry.TryMarkExited(
                registration.ProcessInstanceId,
                registration.ProcessId,
                DateTimeOffset.UtcNow,
                out _);
        }
    }

    public Task StopSupervisedProcessesAsync(CancellationToken cancellationToken) =>
        TerminateExternalToolJobsAsync(cancellationToken);

    public Task RemoveTrayIconAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tray.HideTrayIcon();
        return Task.CompletedTask;
    }

    public async Task ExitAgentAsync(CancellationToken cancellationToken)
    {
        storageHealthEventCancellation.Cancel();
        try
        {
            await storageHealthEventTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }

        tray.ExitAgentThread();
    }

    Task IAgentShutdownTerminalActions.CloseNamedPipesAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        return CloseNamedPipesAsync(cancellationToken);
    }

    Task IAgentShutdownTerminalActions.RemoveTrayIconAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        return RemoveTrayIconAsync(cancellationToken);
    }

    async Task IAgentShutdownTerminalActions.ExitAgentAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        storageHealthEventCancellation.Cancel();
        try
        {
            await storageHealthEventTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }

        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        tray.ExitAgentThread();
    }

    private async Task CaptureStorageHealthEventsAsync()
    {
        try
        {
            await foreach (var storageEvent in storageHealthEventSource.WatchAsync(
                               storageHealthEventCancellation.Token))
            {
                try
                {
                    await storageHealthEventRepository.AddAsync(
                        storageEvent,
                        storageHealthEventCancellation.Token);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidOperationException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    // Keep the live warning even if persistence is temporarily unavailable.
                }

                lock (storageHealthEventSync)
                {
                    recentStorageHealthEvents.Enqueue(storageEvent);
                    while (recentStorageHealthEvents.Count > 200)
                    {
                        recentStorageHealthEvents.Dequeue();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or System.Diagnostics.Eventing.Reader.EventLogException)
        {
        }
    }

    private IReadOnlyList<StorageHealthEvent> GetRecentStorageHealthEvents()
    {
        lock (storageHealthEventSync)
        {
            return recentStorageHealthEvents.ToArray();
        }
    }

    private static Task<ApplicationResult<AgentResponse>> SuccessAsync(
        AgentResponse response,
        CorrelationId correlationId) =>
        Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, correlationId));

    private async Task<ApplicationResult<AgentResponse>> StartMonitoringCoreAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.StartAsync(
            request.MonitorRequest,
            cancellationToken);
        tray.SetMonitoringSession(result.Value);
        if (result.Value is { } session)
        {
            return new(
                result.Status,
                new MonitoringSessionResponse(session),
                result.Messages,
                request.CorrelationId);
        }

        return new(
            result.Status,
            null,
            result.Messages,
            request.CorrelationId);
    }

    private async Task<ApplicationResult<AgentResponse>> StopMonitoringCoreAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.StopAsync(
            request.SessionId,
            cancellationToken);
        tray.SetMonitoringSession(result.Value);
        if (result.Value is { } session)
        {
            return new(
                result.Status,
                new MonitoringSessionResponse(session),
                result.Messages,
                request.CorrelationId);
        }

        return new(
            result.Status,
            null,
            result.Messages,
            request.CorrelationId);
    }

    private static IExternalToolAdapter? CreateAdapter(ToolState tool) =>
        tool.ToolId.Value switch
        {
            "microsoft.diskspd" => new DiskSpdAdapter(tool.ExecutablePath!),
            "fio" => new FioAdapter(tool.ExecutablePath!),
            "windows.robocopy" => new RoboCopyAdapter(tool.ExecutablePath!),
            "dite.filegen" => new DiteFileGenAdapter(tool.ExecutablePath!),
            _ => null
        };

    private async Task ExecuteFlushBetweenCopyBatchesAsync(
        AuthorizedTestRun run,
        CopyBatchManifest manifest,
        int completedBatchNumber,
        FlushVolumeAction action,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (action.VolumeId != run.Plan.Target.VolumeId
            || action.PlannedTarget is not { } target
            || target.VolumeId != action.VolumeId)
        {
            throw new UnauthorizedAccessException(
                "The copy-batch Flush target no longer matches the immutable test target.");
        }

        var request = new ElevatedBrokerExecutionRequest(
            Guid.Empty,
            Guid.Empty,
            0,
            string.Empty,
            manifest.PlanHash,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ElevatedBrokerOperationKind.FlushVolume,
            VolumeTarget: target);
        await WriteSystemSupportAuditAsync(
            request,
            correlationId,
            SystemSupportActionKind.FlushVolume,
            SystemSupportAuditStage.Started,
            "system-support.test-copy-flush-started",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ExecuteElevatedBrokerAsync(
                request,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || result.VolumeEvidence is null)
            {
                throw new InvalidOperationException(result.Code);
            }

            var artifact = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    format = "WinPool.CopyBatchFlushEvidence",
                    version = 1,
                    runId = manifest.RunId.Value,
                    stepId = manifest.StepId,
                    batchNumber = completedBatchNumber,
                    planHash = manifest.PlanHash,
                    target,
                    result.VolumeEvidence
                });
            await testArtifactStore.SaveGeneratedArtifactAsync(
                manifest.RunId,
                $"copy-flush-{manifest.StepId}-{completedBatchNumber}",
                "application/json",
                artifact,
                CancellationToken.None);
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.FlushVolume,
                SystemSupportAuditStage.Completed,
                "system-support.test-copy-flush-completed",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.FlushVolume,
                SystemSupportAuditStage.Cancelled,
                "system-support.test-copy-flush-cancelled",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.FlushVolume,
                SystemSupportAuditStage.Failed,
                "system-support.test-copy-flush-failed",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteRamMapBeforeBatchAsync(
        TestRunId runId,
        string firstBatchStepId,
        string planHash,
        ClearSystemFileCacheAction action,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var request = new ElevatedBrokerExecutionRequest(
            Guid.Empty,
            Guid.Empty,
            0,
            string.Empty,
            planHash,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ElevatedBrokerOperationKind.ClearSystemFileCache,
            RamMapMode: action.Mode,
            PlannedRamMapIdentity: action.PlannedToolIdentity);
        await WriteSystemSupportAuditAsync(
            request,
            correlationId,
            SystemSupportActionKind.ClearSystemFileCache,
            SystemSupportAuditStage.Started,
            "system-support.test-rammap-started",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ExecuteElevatedBrokerAsync(
                request,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded
                || result.RamMapEvidence is not
                {
                    ExitCode: 0,
                    UsedElevatedBroker: true
                } evidence
                || !evidence.Arguments.SequenceEqual(["-Es", "-Et"]))
            {
                throw new InvalidOperationException(result.Code);
            }

            var artifact = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    format = "WinPool.RamMapBatchEvidence",
                    version = 1,
                    runId = runId.Value,
                    firstBatchStepId,
                    planHash,
                    mode = action.Mode.ToString(),
                    tool = action.PlannedToolIdentity,
                    evidence
                });
            await testArtifactStore.SaveGeneratedArtifactAsync(
                runId,
                $"rammap-before-{firstBatchStepId}",
                "application/json",
                artifact,
                CancellationToken.None);
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.ClearSystemFileCache,
                SystemSupportAuditStage.Completed,
                "system-support.test-rammap-completed",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.ClearSystemFileCache,
                SystemSupportAuditStage.Cancelled,
                "system-support.test-rammap-cancelled",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await WriteSystemSupportAuditAsync(
                request,
                correlationId,
                SystemSupportActionKind.ClearSystemFileCache,
                SystemSupportAuditStage.Failed,
                "system-support.test-rammap-failed",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SetActivePowerPlanForTestAsync(
        Guid powerPlanId,
        string planHash,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteElevatedBrokerAsync(
            new(
                Guid.Empty,
                Guid.Empty,
                0,
                string.Empty,
                planHash,
                DateTimeOffset.UtcNow.AddMinutes(1),
                ElevatedBrokerOperationKind.SetActivePowerPlan,
                PowerPlanId: powerPlanId),
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Code);
        }
    }

    private static ApplicationResult<AgentResponse> Reject(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code));

    private static Task<ApplicationResult<AgentResponse>> RejectAsync(
        CorrelationId correlationId,
        string code) =>
        Task.FromResult(Reject(correlationId, code));

    private static Task<ApplicationResult<AgentResponse>> NotConnectedAsync(
        CorrelationId correlationId,
        string code) =>
        Task.FromResult(
            ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.RequiresEnvironment,
                correlationId,
                new ApplicationMessage(
                    code,
                    code,
                    string.Empty,
                    ApplicationMessageSeverity.Warning,
                [])));

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);

}

internal sealed record AgentEndpointRecord(
    int ProtocolVersion,
    string PipeName,
    Guid Nonce,
    Guid AgentSessionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc);
