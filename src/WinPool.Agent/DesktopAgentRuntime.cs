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
using WinPool.Core;
using WinPool.Infrastructure.Windows;
using System.Collections.Concurrent;

namespace WinPool.Agent;

internal sealed class DesktopAgentRuntime :
    IAgentRequestOperations,
    IAgentShutdownActions
{
    private readonly TrayApplicationContext tray;
    private readonly AgentInstanceId instanceId;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly MonitorCsvExporter monitorCsvExporter;
    private readonly AgentProcessRegistry processRegistry;
    private readonly IExternalToolRegistry toolRegistry;
    private readonly ExternalToolStateRepository toolStateRepository;
    private readonly WorkerProcessRepository workerProcessRepository;
    private readonly TestRunRepository testRunRepository;
    private readonly UserTestPresetRepository userTestPresets;
    private readonly WorkspaceSessionStateRepository workspaceState;
    private readonly SimulationDocumentRepository simulationDocuments;
    private readonly CopyBatchRepository copyBatchRepository;
    private readonly DiteLegacyImportRepository diteLegacyImports;
    private readonly TestArtifactStore testArtifactStore;
    private readonly TestRunExporter testRunExporter;
    private readonly IInventoryProvider nativeInventoryProvider;
    private readonly IInventoryProvider legacyInventoryProvider;
    private readonly IHardwareInventoryProvider manageInventoryProvider;
    private readonly IPhysicalDiskDeviceResolver physicalDiskDeviceResolver;
    private readonly IInventoryComparer inventoryComparer;
    private readonly InventorySnapshotRepository inventorySnapshots;
    private readonly InventoryComparisonRepository inventoryComparisons;
    private readonly LocalInventoryDocumentRepository localInventoryDocument;
    private readonly TestWorkerProcessHost testWorkerHost;
    private readonly ElevatedBrokerProcessHost elevatedBrokerHost;
    private readonly SystemSupportAuditRepository systemSupportAuditRepository;
    private readonly SystemSupportRecoveryCoordinator systemSupportRecovery;
    private readonly TestProcessSchedulingScope testProcessSchedulingScope;
    private readonly TestPowerPlanScope testPowerPlanScope;
    private readonly IStorageHealthEventSource storageHealthEventSource;
    private readonly StorageHealthEventRepository storageHealthEventRepository;
    private readonly AgentEventHub agentEvents;
    private readonly CancellationTokenSource storageHealthEventCancellation = new();
    private readonly object storageHealthEventSync = new();
    private readonly Queue<StorageHealthEvent> recentStorageHealthEvents = new();
    private readonly SystemSupportReviewStore systemSupportReviews = new();
    private readonly Task storageHealthEventTask;
    private readonly object testSync = new();
    private readonly ConcurrentDictionary<int, string> physicalDeviceIds = new();
    private CancellationTokenSource? activeTestCancellation;
    private Task? activeTestTask;
    private TestRunId? activeTestRunId;

    public DesktopAgentRuntime(
        TrayApplicationContext tray,
        AgentInstanceId instanceId,
        MonitoringSessionCoordinator monitoring,
        MonitorCsvExporter monitorCsvExporter,
        AgentProcessRegistry processRegistry,
        IExternalToolRegistry toolRegistry,
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
        this.toolStateRepository = toolStateRepository
            ?? throw new ArgumentNullException(nameof(toolStateRepository));
        this.workerProcessRepository = workerProcessRepository
            ?? throw new ArgumentNullException(nameof(workerProcessRepository));
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
        this.diteLegacyImports = diteLegacyImports
            ?? throw new ArgumentNullException(nameof(diteLegacyImports));
        this.testArtifactStore = testArtifactStore
            ?? throw new ArgumentNullException(nameof(testArtifactStore));
        this.testRunExporter = testRunExporter
            ?? throw new ArgumentNullException(nameof(testRunExporter));
        this.nativeInventoryProvider = nativeInventoryProvider
            ?? throw new ArgumentNullException(nameof(nativeInventoryProvider));
        this.legacyInventoryProvider = legacyInventoryProvider
            ?? throw new ArgumentNullException(nameof(legacyInventoryProvider));
        this.manageInventoryProvider = manageInventoryProvider
            ?? throw new ArgumentNullException(nameof(manageInventoryProvider));
        this.physicalDiskDeviceResolver = physicalDiskDeviceResolver
            ?? new WindowsPhysicalDiskDeviceResolver();
        this.inventoryComparer = inventoryComparer
            ?? throw new ArgumentNullException(nameof(inventoryComparer));
        this.inventorySnapshots = inventorySnapshots
            ?? throw new ArgumentNullException(nameof(inventorySnapshots));
        this.inventoryComparisons = inventoryComparisons
            ?? throw new ArgumentNullException(nameof(inventoryComparisons));
        this.localInventoryDocument = localInventoryDocument
            ?? throw new ArgumentNullException(nameof(localInventoryDocument));
        this.testWorkerHost = testWorkerHost
            ?? throw new ArgumentNullException(nameof(testWorkerHost));
        this.elevatedBrokerHost = elevatedBrokerHost
            ?? throw new ArgumentNullException(nameof(elevatedBrokerHost));
        this.systemSupportAuditRepository = systemSupportAuditRepository
            ?? throw new ArgumentNullException(nameof(systemSupportAuditRepository));
        this.systemSupportRecovery = systemSupportRecovery
            ?? throw new ArgumentNullException(nameof(systemSupportRecovery));
        this.testProcessSchedulingScope = testProcessSchedulingScope
            ?? throw new ArgumentNullException(nameof(testProcessSchedulingScope));
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
        try
        {
            return await elevatedBrokerHost.ExecuteAsync(
                request,
                (processId, _) =>
                {
                    brokerProcessId = processId;
                    var now = DateTimeOffset.UtcNow;
                    if (!processRegistry.TryRegister(
                            new AgentManagedProcess(
                                processId,
                                AgentManagedProcessKind.ElevatedBroker,
                                correlationId,
                                now,
                                now,
                                SupervisedProcessState.Starting,
                                OwnsJobObject: false,
                                ShutdownDeadlineUtc: now.AddMinutes(1))) ||
                        !processRegistry.TryMarkRunning(processId, now))
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
            if (brokerProcessId > 0)
            {
                processRegistry.TryMarkExited(
                    brokerProcessId,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    public bool HasActiveTest
    {
        get
        {
            lock (testSync)
            {
                return activeTestTask is { IsCompleted: false };
            }
        }
    }

    public Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
        GetAgentSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new AgentSnapshot(
            instanceId,
            tray.IsTrayVisible,
            ActiveMonitoringSession: monitoring.CurrentSession,
            ActiveTestRunId: GetActiveTestRunId(),
            IsShuttingDown: tray.IsShuttingDown,
            Processes: processRegistry.Snapshot()
                .Select(ToProcessRegistration)
                .ToArray(),
            LatestMonitorSamples: monitoring.CurrentSamples,
            RecentStorageHealthEvents: GetRecentStorageHealthEvents(),
            MonitorDiagnostics: monitoring.CurrentDiagnostics);
        return SuccessAsync(new AgentSnapshotResponse(snapshot), request.CorrelationId);
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

    public async Task<ApplicationResult<AgentResponse>> ReviewSystemSupportAsync(
        ReviewAgentSystemSupportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(2);
        var candidate = request.ExecutionRequest with
        {
            Nonce = Guid.NewGuid(),
            AgentSessionId = instanceId.Value,
            AgentProcessId = Environment.ProcessId,
            UserSidHash = new string('a', 64),
            ExpiresAtUtc = now.AddMinutes(1)
        };
        var rejection = ElevatedBrokerExecutionValidator.Validate(
            candidate,
            candidate.Nonce,
            instanceId.Value,
            Environment.ProcessId,
            candidate.UserSidHash,
            now);
        if (rejection is not null)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Rejected,
                request.CorrelationId,
                AgentMessage(rejection, ApplicationMessageSeverity.Error));
        }

        var execution = request.ExecutionRequest with
        {
            Nonce = Guid.Empty,
            AgentSessionId = Guid.Empty,
            AgentProcessId = 0,
            UserSidHash = string.Empty,
            ExpiresAtUtc = expires
        };
        var review = systemSupportReviews.Create(
            execution,
            now,
            TimeSpan.FromMinutes(2));
        var actionKind = ToSystemSupportActionKind(execution.Operation);
        await WriteSystemSupportAuditAsync(
            execution,
            request.CorrelationId,
            actionKind,
            SystemSupportAuditStage.Review,
            "system-support.review-ready",
            cancellationToken).ConfigureAwait(false);
        var candidates = execution.TemporaryCleanupCandidates ?? [];
        var warningCode = candidates.Count ==
                          ElevatedBrokerExecutionValidator
                              .MaximumTemporaryCleanupCandidates
            ? "system-support.warning.candidate-batch-limit"
            : $"system-support.warning.{execution.Operation}";
        return ApplicationResult<AgentResponse>.Succeeded(
            new SystemSupportReviewResponse(
                review.ReviewId,
                execution.Operation,
                execution.PlanHash,
                expires,
                candidates.Count,
                candidates.Sum(item => item.Length),
                warningCode),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ExecuteSystemSupportAsync(
        ExecuteAgentSystemSupportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!systemSupportReviews.TryTake(
                request.ReviewId,
                DateTimeOffset.UtcNow,
                out var pending,
                out var reviewCode))
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                reviewCode == "system-support.review-expired"
                    ? ApplicationStatus.RequiresAuthorization
                    : ApplicationStatus.Rejected,
                request.CorrelationId,
                AgentMessage(
                    reviewCode,
                    ApplicationMessageSeverity.Error));
        }

        var executionRequest = pending!.ExecutionRequest;
        var actionKind = ToSystemSupportActionKind(
            executionRequest.Operation);
#if DEBUG
        const bool confirmationRequired = false;
#else
        const bool confirmationRequired = true;
#endif
        if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            (confirmationRequired && !request.UserConfirmed))
        {
            var code = pending.ExpiresAtUtc <= DateTimeOffset.UtcNow
                ? "system-support.review-expired"
                : "system-support.release-confirmation-required";
            await WriteSystemSupportAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Rejected,
                code,
                CancellationToken.None).ConfigureAwait(false);

            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.RequiresAuthorization,
                request.CorrelationId,
                AgentMessage(code, ApplicationMessageSeverity.Warning));
        }

        try
        {
            await WriteSystemSupportAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Started,
                "system-support.broker-started",
                cancellationToken).ConfigureAwait(false);
            var result = await ExecuteElevatedBrokerAsync(
                executionRequest,
                request.CorrelationId,
                cancellationToken).ConfigureAwait(false);
            await WriteSystemSupportAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                result.Succeeded
                    ? SystemSupportAuditStage.Completed
                    : SystemSupportAuditStage.Rejected,
                result.Code,
                CancellationToken.None).ConfigureAwait(false);
            return new ApplicationResult<AgentResponse>(
                result.Succeeded
                    ? ApplicationStatus.Succeeded
                    : ApplicationStatus.Rejected,
                new SystemSupportExecutionResponse(result),
                result.Succeeded
                    ? []
                    : [AgentMessage(result.Code, ApplicationMessageSeverity.Error)],
                request.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            await WriteSystemSupportAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Cancelled,
                "system-support.elevation-cancelled",
                CancellationToken.None).ConfigureAwait(false);
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId,
                AgentMessage(
                    "system-support.elevation-cancelled",
                    ApplicationMessageSeverity.Warning));
        }
        catch
        {
            await WriteSystemSupportAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Failed,
                "system-support.broker-failed",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask WriteSystemSupportAuditAsync(
        ElevatedBrokerExecutionRequest executionRequest,
        CorrelationId correlationId,
        SystemSupportActionKind actionKind,
        SystemSupportAuditStage stage,
        string code,
        CancellationToken cancellationToken) =>
        systemSupportAuditRepository.WriteAsync(
            new SystemSupportAuditEvent(
                correlationId,
                executionRequest.PlanHash,
                actionKind,
                stage,
                DateTimeOffset.UtcNow,
                code,
                code,
                $"operation={executionRequest.Operation};stage={stage}",
                "system-support-v1"),
            cancellationToken);

    internal static SystemSupportActionKind ToSystemSupportActionKind(
        ElevatedBrokerOperationKind operation) =>
        operation switch
        {
            ElevatedBrokerOperationKind.CleanTemporaryFiles =>
                SystemSupportActionKind.CleanTemporaryFiles,
            ElevatedBrokerOperationKind.ClearSystemFileCache =>
                SystemSupportActionKind.ClearSystemFileCache,
            ElevatedBrokerOperationKind.FlushVolume =>
                SystemSupportActionKind.FlushVolume,
            ElevatedBrokerOperationKind.TrimOrOptimizeVolume =>
                SystemSupportActionKind.TrimOrOptimizeVolume,
            ElevatedBrokerOperationKind.SetActivePowerPlan =>
                SystemSupportActionKind.UseTemporaryPowerPlan,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported elevated Broker operation.")
        };

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

        var physicalDeviceId = physicalDeviceIds.GetValueOrDefault(request.DiskNumber);
        if (string.IsNullOrWhiteSpace(physicalDeviceId))
        {
            physicalDeviceId = physicalDiskDeviceResolver.ResolvePnpDeviceId(
                request.DiskNumber);
            if (!string.IsNullOrWhiteSpace(physicalDeviceId))
            {
                physicalDeviceIds[request.DiskNumber] = physicalDeviceId;
            }
        }

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

    public async Task<ApplicationResult<AgentResponse>> StartTestAsync(
        StartAgentTestRequest request,
        CancellationToken cancellationToken)
    {
        var existingRun = await testRunRepository.GetAsync(
            request.Plan.RunId,
            cancellationToken);
        var authorizationCoordinator = new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(request.UserConfirmedWrite));
        var authorization = existingRun is
            {
                State: PersistedTestRunState.Interrupted
            }
            && StringComparer.Ordinal.Equals(
                existingRun.PlanHash,
                request.Plan.PlanHash)
                ? await authorizationCoordinator.AuthorizeResumeAsync(
                    request.Plan,
                    existingRun.PlanHash,
                    cancellationToken)
                : await authorizationCoordinator.AuthorizeAsync(
                    request.Plan,
                    cancellationToken);
        if (!authorization.IsSuccess)
        {
            return new(
                authorization.Status,
                null,
                authorization.Messages,
                request.CorrelationId);
        }

        var run = authorization.Value!;
        var supportActionError = ValidateTestSupportActions(run.Plan);
        if (supportActionError is not null)
        {
            return Reject(
                request.CorrelationId,
                supportActionError);
        }

        if (request.Definition.Id != run.Plan.DefinitionId
            || !string.Equals(
                request.Definition.Version,
                run.Plan.DefinitionVersion,
                StringComparison.Ordinal)
            || request.Definition.Tasks.Count == 0)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.definition_plan_mismatch");
        }

        var orderedSteps = OrderStepsForExecution(run.Plan.Steps);
        if (orderedSteps is null)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.invalid_step_graph");
        }

        if (orderedSteps.Any(step =>
                step.ToolId is null
                && !LocalTestStepExecutor.IsSupported(step.Action)))
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.non_tool_step_not_connected");
        }

        var preparedSteps = new List<PreparedExecutionStep>(orderedSteps.Count);
        foreach (var step in orderedSteps)
        {
            if (step.ToolId is null)
            {
                preparedSteps.Add(new(step, null, null));
                continue;
            }

            var tool = await toolRegistry.DetectAsync(
                step.ToolId.Value,
                cancellationToken);
            if (!tool.IsSuccess || tool.Value?.ExecutablePath is null)
            {
                return new(
                    tool.Status,
                    null,
                    tool.Messages,
                    request.CorrelationId);
            }

            await toolStateRepository.SaveAsync(
                tool.Value,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var adapter = CreateAdapter(tool.Value);
            if (adapter is null)
            {
                return Reject(
                    request.CorrelationId,
                    "agent.testing.tool_adapter_not_supported");
            }

            var invocation = adapter.BuildInvocation(
                step,
                run.Workspace,
                request.CorrelationId);
            if (!invocation.IsSuccess || invocation.Value is null)
            {
                return new(
                    invocation.Status,
                    null,
                    invocation.Messages,
                    request.CorrelationId);
            }

            preparedSteps.Add(
                new(
                    step,
                    new(
                        run.Plan.RunId,
                        step.Id,
                        invocation.Value,
                        tool.Value,
                        TimeSpan.FromSeconds(3)),
                    adapter));
        }

        CancellationTokenSource runCancellation;
        lock (testSync)
        {
            if (activeTestTask is { IsCompleted: false })
            {
                return Reject(
                    request.CorrelationId,
                    "agent.testing.already_running");
            }

            runCancellation = new CancellationTokenSource();
            activeTestCancellation = runCancellation;
            activeTestRunId = run.Plan.RunId;
            activeTestTask = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        try
        {
            await testRunRepository.SaveDefinitionAsync(
                request.Definition,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (existingRun is null)
            {
                await testRunRepository.CreateRunAsync(
                    run.Plan,
                    $$"""{"agentSession":"{{instanceId.Value:N}}","source":"WinPool.Agent"}""",
                    PersistedTestRunState.Running,
                    cancellationToken);
            }
            else if (existingRun.State is PersistedTestRunState.Interrupted
                     && StringComparer.Ordinal.Equals(
                         existingRun.PlanHash,
                         run.Plan.PlanHash))
            {
                await testRunRepository.ResumeInterruptedAsync(
                    run.Plan.RunId,
                    run.Plan.PlanHash,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(
                    "The test run identity already exists and is not resumable.");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or Microsoft.Data.Sqlite.SqliteException
                or InvalidOperationException
                or OperationCanceledException)
        {
            lock (testSync)
            {
                activeTestCancellation = null;
                activeTestRunId = null;
                activeTestTask = null;
            }

            runCancellation.Dispose();
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                Message("agent.testing.persistence_failed"));
        }

        lock (testSync)
        {
            activeTestTask = RunTestAsync(
                run,
                request.CorrelationId,
                preparedSteps,
                runCancellation);
        }

        tray.SetTestRun(run.Plan.RunId, "starting");
        return await SuccessAsync(
            new AgentAcknowledgement(),
            request.CorrelationId);
    }

    public Task<ApplicationResult<AgentResponse>> CancelTestAsync(
        CancelAgentTestRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (testSync)
        {
            if (activeTestRunId != request.RunId
                || activeTestTask is not { IsCompleted: false }
                || activeTestCancellation is null)
            {
                return RejectAsync(
                    request.CorrelationId,
                    "agent.testing.run_not_active");
            }

            activeTestCancellation.Cancel();
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

    public async Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SystemId.Value == Guid.Empty)
        {
            return Reject(
                request.CorrelationId,
                "agent.inventory.system_id_invalid");
        }

        try
        {
            var document = await manageInventoryProvider.CollectLocalAsync(
                cancellationToken);
            CachePhysicalDeviceIds(document);
            var sanitized = StorageSystemDocumentSanitizer.RedactSensitiveData(document);
            var projected = LegacyPowerShellInventoryProvider.Project(
                request.SystemId,
                sanitized.Snapshot,
                includeSensitiveValuesInMemory: false);
            var saved = await inventorySnapshots.SaveAsync(
                projected,
                PersistedSystemKind.Local,
                Environment.MachineName,
                cancellationToken);
            var payload = LocalInventoryDocumentCodec.Encode(sanitized);
            await localInventoryDocument.SaveAsync(
                saved.SnapshotId,
                payload,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ManageInventoryCaptureResponse(
                    saved.SnapshotId,
                    payload),
                request.CorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is InventoryScanException
                or IOException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException
                or UnauthorizedAccessException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                new ApplicationMessage(
                    "agent.inventory.manage_capture_failed",
                    "agent.inventory.manage_capture_failed",
                    string.Empty,
                    ApplicationMessageSeverity.Error,
                    []));
        }
    }

    private void CachePhysicalDeviceIds(StorageSystemDocument document)
    {
        physicalDeviceIds.Clear();
        foreach (var disk in document.Snapshot.PhysicalDisks)
        {
            if (disk.DeviceId is int diskNumber
                && !string.IsNullOrWhiteSpace(disk.PnpDeviceId))
            {
                physicalDeviceIds[diskNumber] = disk.PnpDeviceId;
            }
        }
    }

    public async Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await localInventoryDocument.LoadAsync(cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ManageInventoryLoadedResponse(
                    persisted?.SnapshotId,
                    persisted?.Document),
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                new ApplicationMessage(
                    "agent.inventory.cached_load_failed",
                    "agent.inventory.cached_load_failed",
                    string.Empty,
                    ApplicationMessageSeverity.Error,
                    []));
        }
    }

    public async Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SystemId.Value == Guid.Empty)
        {
            return Reject(
                request.CorrelationId,
                "agent.inventory.system_id_invalid");
        }

        var captureRequest = new InventoryRequest(
            request.SystemId,
            InventoryCaptureReason.Comparison,
            IncludeSensitiveValuesInMemory: false);
        var native = await nativeInventoryProvider.CaptureAsync(
            captureRequest,
            cancellationToken);
        if (!native.IsSuccess || native.Value is null)
        {
            return new(
                native.Status,
                null,
                native.Messages,
                request.CorrelationId);
        }

        try
        {
            var savedNative = await inventorySnapshots.SaveAsync(
                native.Value,
                PersistedSystemKind.Local,
                Environment.MachineName,
                cancellationToken);
            if (!request.IncludeLegacyComparison)
            {
                return ApplicationResult<AgentResponse>.Succeeded(
                    new InventoryCaptureResponse(
                        savedNative.SnapshotId,
                        savedNative.Snapshot,
                        null,
                        null,
                        null,
                        null),
                    request.CorrelationId);
            }

            var legacy = await legacyInventoryProvider.CaptureAsync(
                captureRequest,
                cancellationToken);
            if (!legacy.IsSuccess || legacy.Value is null)
            {
                return new(
                    ApplicationStatus.PartiallyCompleted,
                    new InventoryCaptureResponse(
                        savedNative.SnapshotId,
                        savedNative.Snapshot,
                        null,
                        null,
                        null,
                        null),
                    legacy.Messages,
                    request.CorrelationId);
            }

            var savedLegacy = await inventorySnapshots.SaveAsync(
                legacy.Value,
                PersistedSystemKind.Local,
                Environment.MachineName,
                cancellationToken);
            var comparison = inventoryComparer.Compare(
                savedLegacy.Snapshot,
                savedNative.Snapshot);
            var savedComparison = await inventoryComparisons.SaveAsync(
                savedLegacy.SnapshotId,
                savedNative.SnapshotId,
                comparison,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new InventoryCaptureResponse(
                    savedNative.SnapshotId,
                    savedNative.Snapshot,
                    savedLegacy.SnapshotId,
                    savedLegacy.Snapshot,
                    savedComparison.ComparisonId,
                    savedComparison.Comparison),
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException
                or ArgumentException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                new ApplicationMessage(
                    "agent.inventory.persistence_or_comparison_failed",
                    "agent.inventory.persistence_or_comparison_failed",
                    exception.Message,
                    ApplicationMessageSeverity.Error,
                    []));
        }
    }

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
        lock (testSync)
        {
            activeTestCancellation?.Cancel();
        }

        return Task.CompletedTask;
    }

    public async Task TerminateExternalToolJobsAsync(
        CancellationToken cancellationToken)
    {
        Task? task;
        lock (testSync)
        {
            activeTestCancellation?.Cancel();
            task = activeTestTask;
        }

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
            processRegistry.TryBeginStopping(registration.ProcessId, deadline);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            var anyLive = false;
            foreach (var registration in registrations)
            {
                if (IsProcessLive(registration.ProcessId))
                {
                    anyLive = true;
                }
                else
                {
                    processRegistry.TryMarkExited(
                        registration.ProcessId,
                        DateTimeOffset.UtcNow);
                }
            }

            if (!anyLive)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new InvalidOperationException(
            "The main application did not exit after the tray shutdown signal.");
    }

    public Task StopSupervisedProcessesAsync(CancellationToken cancellationToken) =>
        TerminateExternalToolJobsAsync(cancellationToken);

    public Task RemoveTrayIconAsync(CancellationToken cancellationToken)
    {
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

    private static ProcessRegistration ToProcessRegistration(
        AgentManagedProcess process) =>
        new(
            process.ProcessId,
            process.Kind switch
            {
                AgentManagedProcessKind.MainApplication =>
                    WorkerKind.MainApplication,
                AgentManagedProcessKind.TestWorker =>
                    WorkerKind.Test,
                AgentManagedProcessKind.InventoryWorker =>
                    WorkerKind.Inventory,
                AgentManagedProcessKind.ElevatedBroker =>
                    WorkerKind.ElevatedBroker,
                AgentManagedProcessKind.ExternalTool =>
                    WorkerKind.ExternalTool,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(process),
                    process.Kind,
                    "Unknown managed process kind.")
            },
            process.CorrelationId,
            process.StartedAtUtc,
            process.LastHeartbeatUtc,
            process.State,
            process.OwnsJobObject,
            process.ShutdownDeadlineUtc);

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

    private TestRunId? GetActiveTestRunId()
    {
        lock (testSync)
        {
            return activeTestTask is { IsCompleted: false }
                ? activeTestRunId
                : null;
        }
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

    private static bool RequiresDirectoryQuotaBoundary(TestStep step) =>
        step.Parameters.ContainsKey("targetRelativeDirectory")
        || step.Parameters.ContainsKey("destinationRelativeDirectory");

    private async Task ValidateExternalDirectoryOutputAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var relativeDirectory =
            GetTextParameter(step, "targetRelativeDirectory")
            ?? GetTextParameter(step, "destinationRelativeDirectory");
        if (relativeDirectory is null)
        {
            return;
        }

        var evidence = await new RegisteredTestDirectoryInspector().CaptureAsync(
            run,
            relativeDirectory,
            includeHashes: false,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "bounded_directory_file_count",
            evidence.ActualFileCount,
            "files",
            "observed",
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "bounded_directory_bytes",
            evidence.ActualBytes,
            "bytes",
            "observed",
            cancellationToken);
    }

    private async Task<CopyBatchManifest?> PrepareCopyBatchRecoveryAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        if (!IsRegisteredDirectoryCopy(step))
        {
            return null;
        }

        var sourcePath = GetTextParameter(
            step,
            "sourceRelativeDirectory")!;
        var destinationPath = GetTextParameter(
            step,
            "destinationRelativeDirectory")!;
        var inspector = new RegisteredTestDirectoryInspector();
        var source = await inspector.CaptureAsync(
            run,
            sourcePath,
            includeHashes: false,
            cancellationToken);
        var destination = await CaptureOrCreateEmptyDirectoryEvidenceAsync(
            run,
            destinationPath,
            inspector,
            cancellationToken);
        var manifest = await copyBatchRepository.GetManifestAsync(
            run.Plan.RunId,
            step.Id,
            cancellationToken);
        if (manifest is null)
        {
            var batchThresholdMiB = GetPositiveIntegerParameter(
                step,
                "copyBatchThresholdMiB",
                128 * 1024,
                1,
                1024 * 1024);
            var maximumFiles = GetPositiveIntegerParameter(
                step,
                "copyBatchMaximumFiles",
                10_000,
                1,
                100_000);
            manifest = new CopyBatchPlanner().Compile(
                run.Plan,
                step.Id,
                source,
                destination,
                checked(batchThresholdMiB * 1024L * 1024L),
                maximumFiles,
                DateTimeOffset.UtcNow);
            await copyBatchRepository.SaveManifestAsync(
                manifest,
                cancellationToken);
        }
        else
        {
            if (!StringComparer.Ordinal.Equals(
                    manifest.PlanHash,
                    run.Plan.PlanHash))
            {
                throw new UnauthorizedAccessException(
                    "The persisted copy recovery manifest belongs to a different plan.");
            }

            var report = new CopyBatchPlanner().Recover(
                manifest,
                source,
                destination);
            await copyBatchRepository.ApplyRecoveryReportAsync(
                run.Plan.RunId,
                step.Id,
                report,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (report.ConflictCount > 0)
            {
                throw new InvalidDataException(
                    "Copy recovery found source or destination conflicts; no files were overwritten.");
            }
        }

        return manifest;
    }

    private async Task FinalizeCopyBatchRecoveryAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        if (!IsRegisteredDirectoryCopy(step))
        {
            return;
        }

        var manifest = await copyBatchRepository.GetManifestAsync(
                run.Plan.RunId,
                step.Id,
                cancellationToken)
            ?? throw new InvalidDataException(
                "The copy step completed without its persisted recovery manifest.");
        var inspector = new RegisteredTestDirectoryInspector();
        var source = await inspector.CaptureAsync(
            run,
            GetTextParameter(step, "sourceRelativeDirectory")!,
            includeHashes: false,
            cancellationToken);
        var destination = await inspector.CaptureAsync(
            run,
            GetTextParameter(step, "destinationRelativeDirectory")!,
            includeHashes: false,
            cancellationToken);
        var report = new CopyBatchPlanner().Recover(
            manifest,
            source,
            destination);
        await copyBatchRepository.ApplyRecoveryReportAsync(
            run.Plan.RunId,
            step.Id,
            report,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (report.PendingCount > 0 || report.ConflictCount > 0)
        {
            throw new InvalidDataException(
                "RoboCopy returned but the persisted copy manifest did not fully match the destination.");
        }
    }

    private static async Task<RegisteredDirectoryEvidence>
        CaptureOrCreateEmptyDirectoryEvidenceAsync(
            AuthorizedTestRun run,
            string relativePath,
            RegisteredTestDirectoryInspector inspector,
            CancellationToken cancellationToken)
    {
        try
        {
            return await inspector.CaptureAsync(
                run,
                relativePath,
                includeHashes: false,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            var registration = run.Plan.Workspace.RegisteredDirectories.Single(
                item => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(
                        Path.Combine(
                            run.Plan.Workspace.NormalizedRootDirectory,
                            item.RelativePath)),
                    Path.GetFullPath(
                        Path.Combine(
                            run.Plan.Workspace.NormalizedRootDirectory,
                            relativePath))));
            return new(
                registration.RelativePath,
                registration.IdentityToken,
                registration.MaximumBytes,
                registration.MaximumFileCount,
                0,
                0,
                []);
        }
    }

    private static bool IsRegisteredDirectoryCopy(TestStep step) =>
        step.Action is TestActionKind.Copy
        && step.ToolId?.Value is "windows.robocopy"
        && GetTextParameter(step, "sourceRelativeDirectory") is not null
        && GetTextParameter(step, "destinationRelativeDirectory") is not null;

    private static int GetPositiveIntegerParameter(
        TestStep step,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!step.Parameters.TryGetValue(name, out var parameter))
        {
            return fallback;
        }

        if (parameter.Kind is not TestParameterKind.Integer
            || !int.TryParse(
                parameter.SerializedValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException(
                $"The copy batch parameter '{name}' is invalid.");
        }

        return value;
    }

    private static string? GetTextParameter(TestStep step, string key) =>
        step.Parameters.TryGetValue(key, out var parameter)
        && parameter.Kind is TestParameterKind.Text
        && !string.IsNullOrWhiteSpace(parameter.SerializedValue)
            ? parameter.SerializedValue
            : null;

    private async Task<bool> ExecuteCopyBatchStepAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        PreparedExecutionStep prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Adapter is not RoboCopyAdapter adapter
            || prepared.Request is null)
        {
            throw new InvalidOperationException(
                "A registered directory copy requires the RoboCopy adapter and tool identity.");
        }

        var manifest = await PrepareCopyBatchRecoveryAsync(
                run,
                prepared.Step,
                cancellationToken)
            ?? throw new InvalidDataException(
                "The directory copy did not produce a recovery manifest.");
        var checkpoints = await copyBatchRepository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            cancellationToken);
        var groups = new CopyBatchInvocationPlanner().Build(
            manifest,
            checkpoints,
            prepared.Step,
            run.Workspace,
            prepared.Request.ExpectedTool,
            adapter,
            correlationId);
        var ramMapAction = run.SupportActions
            .Select(item => item.Action)
            .OfType<ClearSystemFileCacheAction>()
            .SingleOrDefault();
        var flushAction = run.SupportActions
            .Select(item => item.Action)
            .OfType<FlushVolumeAction>()
            .SingleOrDefault();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (ramMapAction is not null)
            {
                await ExecuteRamMapBeforeBatchAsync(
                    manifest.RunId,
                    manifest.StepId,
                    manifest.PlanHash,
                    ramMapAction,
                    correlationId,
                    cancellationToken);
            }

            foreach (var chunk in group.Items.Chunk(512))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await copyBatchRepository.MarkEntriesCopyingAsync(
                    manifest.RunId,
                    manifest.StepId,
                    chunk.Select(item => item.Entry.Ordinal).ToArray(),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                TestWorkerRunResult result;
                try
                {
                    result = await RunSupervisedTestWorkerAsync(
                        run,
                        correlationId,
                        chunk.Select(item => item.Request).ToArray(),
                        new Dictionary<string, ToolId?>(StringComparer.Ordinal)
                        {
                            [manifest.StepId] = prepared.Step.ToolId
                        },
                        cancellationToken);
                }
                catch
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        manifest.RunId,
                        manifest.StepId,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                    throw;
                }

                await testArtifactStore.SaveWorkerOutputAsync(
                    manifest.RunId,
                    manifest.StepId,
                    result.Events,
                    CancellationToken.None);
                var processFailure = false;
                var failedEntries = new List<(int Ordinal, int ExitCode, string Code)>();
                for (var index = 0; index < result.ToolResults.Count; index++)
                {
                    var toolResult = result.ToolResults[index];
                    var processEvents = result.Events.Where(item =>
                            item.ProcessId == toolResult.Audit.Identity.ProcessId)
                        .ToArray();
                    var parseFailed = await new TestToolResultRepositoryWriter(
                            testRunRepository)
                        .PersistAsync(
                            manifest.RunId,
                            manifest.StepId,
                            adapter,
                            processEvents,
                            toolResult.Audit.ExitCode,
                            CancellationToken.None);
                    var itemFailed = parseFailed
                        || !ToolProcessExitPolicy.IsAccepted(
                            toolResult.Audit.ToolId,
                            toolResult.Audit.ExitCode)
                        || toolResult.Audit.TerminationReason
                            is not ToolProcessTerminationReason.Completed;
                    processFailure |= itemFailed;
                    if (itemFailed)
                    {
                        failedEntries.Add(
                            (
                                chunk[index].Entry.Ordinal,
                                toolResult.Audit.ExitCode,
                                parseFailed
                                    ? "copy.output_parse_failed"
                                    : "copy.process_failed"));
                    }
                }

                var incomplete = result.ToolResults.Count != chunk.Length;
                var inspector = new RegisteredTestDirectoryInspector();
                var source = await inspector.CaptureAsync(
                    run,
                    GetTextParameter(
                        prepared.Step,
                        "sourceRelativeDirectory")!,
                    includeHashes: false,
                    CancellationToken.None);
                var destination = await CaptureOrCreateEmptyDirectoryEvidenceAsync(
                    run,
                    GetTextParameter(
                        prepared.Step,
                        "destinationRelativeDirectory")!,
                    inspector,
                    CancellationToken.None);
                var report = new CopyBatchPlanner().Recover(
                    manifest,
                    source,
                    destination);
                await copyBatchRepository.ApplyRecoveryReportAsync(
                    manifest.RunId,
                    manifest.StepId,
                    report,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                if (failedEntries.Count > 0)
                {
                    var afterRecovery = (await copyBatchRepository
                            .ListEntryCheckpointsAsync(
                                manifest.RunId,
                                manifest.StepId,
                                CancellationToken.None))
                        .ToDictionary(item => item.Ordinal);
                    foreach (var failure in failedEntries)
                    {
                        var checkpoint = afterRecovery[failure.Ordinal];
                        if (checkpoint.State is CopyBatchEntryState.Pending)
                        {
                            await copyBatchRepository.UpdateEntryCheckpointAsync(
                                checkpoint with
                                {
                                    State = CopyBatchEntryState.Failed,
                                    LastExitCode = failure.ExitCode,
                                    DiagnosticCode = failure.Code,
                                    UpdatedAtUtc = DateTimeOffset.UtcNow
                                },
                                CancellationToken.None);
                        }
                    }
                }

                if (incomplete)
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        manifest.RunId,
                        manifest.StepId,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                }

                if (processFailure
                    || incomplete
                    || report.ConflictCount > 0)
                {
                    return false;
                }

                var refreshed = await copyBatchRepository.ListEntryCheckpointsAsync(
                    manifest.RunId,
                    manifest.StepId,
                    CancellationToken.None);
                var refreshedByOrdinal = refreshed.ToDictionary(
                    item => item.Ordinal);
                if (chunk.Any(item =>
                        refreshedByOrdinal[item.Entry.Ordinal].State
                            is not CopyBatchEntryState.Completed))
                {
                    return false;
                }
            }

            if (groupIndex < groups.Count - 1)
            {
                if (flushAction is not null)
                {
                    await ExecuteFlushBetweenCopyBatchesAsync(
                        run,
                        manifest,
                        group.Batch.BatchNumber,
                        flushAction,
                        correlationId,
                        cancellationToken);
                }

                await WaitForCopyBatchSettleAsync(
                    manifest,
                    group.Batch.BatchNumber,
                    cancellationToken);
            }
        }

        await ValidateExternalDirectoryOutputAsync(
            run,
            prepared.Step,
            CancellationToken.None);
        await FinalizeCopyBatchRecoveryAsync(
            run,
            prepared.Step,
            CancellationToken.None);
        return true;
    }

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

    private async Task WaitForCopyBatchSettleAsync(
        CopyBatchManifest manifest,
        int completedBatchNumber,
        CancellationToken cancellationToken)
    {
        if (monitoring.CurrentSession is null)
        {
            throw new InvalidOperationException(
                "A multi-batch copy requires an active Agent monitoring session for settle evidence.");
        }

        var evidence = await new MonitorIdleDetector().WaitAsync(
            () => monitoring.CurrentSamples,
            MonitorIdlePolicy.CopyBatchDefault,
            cancellationToken);
        var aggregation = $"copy-batch-{completedBatchNumber}";
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_seconds",
            (evidence.CompletedAtUtc - evidence.StartedAtUtc).TotalSeconds,
            "seconds",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_activity_percent",
            evidence.FinalObservation.MaximumActivityPercent,
            "percent",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_queue_length",
            evidence.FinalObservation.MaximumQueueLength,
            "count",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_combined_bytes_per_second",
            evidence.FinalObservation.MaximumCombinedBytesPerSecond,
            "bytes/s",
            aggregation,
            cancellationToken);
    }

    private async Task<TestWorkerRunResult> RunSupervisedTestWorkerAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        IReadOnlyList<ToolProcessRequest> requests,
        IReadOnlyDictionary<string, ToolId?> toolIds,
        CancellationToken cancellationToken)
    {
        var runId = run.Plan.RunId;
        var workerProcessId = 0;
        var workerFailed = false;
        var eventProjector = new TestWorkerAgentEventProjector(
            runId,
            correlationId,
            requests);
        PreparedTestProcessSchedulingScope? schedulingScope = null;
        try
        {
            return await testWorkerHost.RunAsync(
                requests,
                async (batch, callbackToken) =>
                {
                    await testRunRepository.AddWorkerEventsAsync(
                        runId,
                        batch.Events,
                        callbackToken);
                    foreach (var item in batch.Events)
                    {
                        if (item.Code == "tool.process.started")
                        {
                            await testRunRepository.UpdateStepStateAsync(
                                runId,
                                item.StepId,
                                ApplicationTaskState.Running,
                                callbackToken);
                            PublishTestEvent(
                                runId,
                                correlationId,
                                item.StepId,
                                TestEventKind.StateChanged,
                                ApplicationTaskEventKind.StateChanged,
                                ApplicationTaskState.Running,
                                item.Code,
                                item.OccurredAtUtc);
                        }
                        else if (item.Code == "tool.process.exited"
                                 && toolIds.TryGetValue(item.StepId, out var toolId))
                        {
                            var state = IsAcceptedToolExit(toolId, item.ExitCode ?? -1)
                                ? ApplicationTaskState.Succeeded
                                : ApplicationTaskState.Failed;
                            await testRunRepository.UpdateStepStateAsync(
                                runId,
                                item.StepId,
                                state,
                                callbackToken);
                            PublishTestEvent(
                                runId,
                                correlationId,
                                item.StepId,
                                TestEventKind.StateChanged,
                                ApplicationTaskEventKind.StateChanged,
                                state,
                                item.Code,
                                item.OccurredAtUtc);
                        }

                        var progressEvent = eventProjector.ProjectNativeProgress(item);
                        if (progressEvent is not null)
                        {
                            agentEvents.Publish(progressEvent);
                        }
                    }

                    if (workerProcessId > 0
                        && processRegistry.TryRecordHeartbeat(
                            workerProcessId,
                            DateTimeOffset.UtcNow)
                        && processRegistry.TryGet(
                            workerProcessId,
                            out var currentProcess)
                        && currentProcess is not null)
                    {
                        await workerProcessRepository.SaveAsync(
                            instanceId,
                            ToProcessRegistration(currentProcess),
                            callbackToken);
                    }
                },
                async (processId, callbackToken) =>
                {
                    workerProcessId = processId;
                    var now = DateTimeOffset.UtcNow;
                    var registration = new AgentManagedProcess(
                        processId,
                        AgentManagedProcessKind.TestWorker,
                        correlationId,
                        now,
                        now,
                        SupervisedProcessState.Running,
                        OwnsJobObject: true,
                        ShutdownDeadlineUtc: null);
                    if (!processRegistry.TryRegister(registration))
                    {
                        throw new InvalidOperationException(
                            "The TestWorker process identity was already registered.");
                    }

                    await workerProcessRepository.SaveAsync(
                        instanceId,
                        ToProcessRegistration(registration),
                        callbackToken);
                    agentEvents.Publish(
                        new AgentProcessStateEvent(
                            ToProcessRegistration(registration),
                            now));
                    var schedulingPolicy = run.SupportActions
                        .Select(item => item.Action)
                        .OfType<TestProcessSchedulingPolicyAction>()
                        .SingleOrDefault();
                    if (schedulingPolicy is not null)
                    {
                        schedulingScope =
                            await testProcessSchedulingScope.PrepareAsync(
                                run.Plan.PlanHash,
                                schedulingPolicy,
                                processId,
                                correlationId,
                                callbackToken);
                    }

                    tray.SetTestRun(runId, "running");
                },
                async (_, _) =>
                {
                    if (schedulingScope is not null)
                    {
                        await testProcessSchedulingScope.RestoreAsync(
                            schedulingScope,
                            correlationId);
                        schedulingScope = null;
                    }
                },
                cancellationToken);
        }
        catch
        {
            workerFailed = !cancellationToken.IsCancellationRequested;
            throw;
        }
        finally
        {
            Exception? schedulingRestoreFailure = null;
            if (workerProcessId > 0)
            {
                if (schedulingScope is not null)
                {
                    try
                    {
                        await testProcessSchedulingScope.RestoreAsync(
                            schedulingScope,
                            correlationId);
                    }
                    catch (Exception exception)
                    {
                        workerFailed = true;
                        schedulingRestoreFailure = exception;
                    }
                }

                processRegistry.TryMarkExited(
                    workerProcessId,
                    DateTimeOffset.UtcNow,
                    workerFailed);
                if (processRegistry.TryGet(workerProcessId, out var finalProcess)
                    && finalProcess is not null)
                {
                    try
                    {
                        await workerProcessRepository.SaveAsync(
                            instanceId,
                            ToProcessRegistration(finalProcess),
                            CancellationToken.None);
                        agentEvents.Publish(
                            new AgentProcessStateEvent(
                                ToProcessRegistration(finalProcess),
                                DateTimeOffset.UtcNow));
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or Microsoft.Data.Sqlite.SqliteException)
                    {
                    }
                }
            }

            if (schedulingRestoreFailure is not null)
            {
                throw new InvalidOperationException(
                    "The TestWorker scheduling state could not be restored; recovery evidence was retained.",
                    schedulingRestoreFailure);
            }
        }
    }

    private async Task RunTestAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        IReadOnlyList<PreparedExecutionStep> preparedSteps,
        CancellationTokenSource runCancellation)
    {
        var runId = run.Plan.RunId;
        var completedStepIds = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;
        var finalState = PersistedTestRunState.Completed;
        PreparedTestPowerPlanScope? powerPlanScope = null;
        try
        {
            var powerAction = run.SupportActions
                .Select(item => item.Action)
                .OfType<UseTemporaryPowerPlanAction>()
                .SingleOrDefault();
            if (powerAction is not null)
            {
                powerPlanScope = await testPowerPlanScope.PrepareAsync(
                    run.Plan.PlanHash,
                    powerAction.PowerPlanId,
                    correlationId,
                    runCancellation.Token);
            }

            var localExecutor = new LocalTestStepExecutor(
                testRunRepository,
                monitoring,
                testArtifactStore);
            var index = 0;
            while (index < preparedSteps.Count)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                var current = preparedSteps[index];
                if (current.Request is null)
                {
                    await localExecutor.ExecuteAsync(
                        run,
                        current.Step,
                        runCancellation.Token);
                    completedStepIds.Add(current.Step.Id);
                    index++;
                    continue;
                }

                if (IsRegisteredDirectoryCopy(current.Step))
                {
                    var copySucceeded = await ExecuteCopyBatchStepAsync(
                        run,
                        correlationId,
                        current,
                        runCancellation.Token);
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        current.Step.Id,
                        copySucceeded
                            ? ApplicationTaskState.Succeeded
                            : ApplicationTaskState.Failed,
                        CancellationToken.None);
                    if (!copySucceeded)
                    {
                        failed = true;
                        finalState = PersistedTestRunState.Failed;
                        break;
                    }

                    completedStepIds.Add(current.Step.Id);
                    index++;
                    continue;
                }

                var batchSteps = new List<PreparedExecutionStep>();
                while (index < preparedSteps.Count
                       && preparedSteps[index].Request is not null)
                {
                    var prepared = preparedSteps[index];
                    if (batchSteps.Count > 0
                        && IsRegisteredDirectoryCopy(prepared.Step))
                    {
                        break;
                    }

                    batchSteps.Add(prepared);
                    index++;
                    if (RequiresDirectoryQuotaBoundary(prepared.Step))
                    {
                        break;
                    }
                }

                var ramMapAction = run.SupportActions
                    .Select(item => item.Action)
                    .OfType<ClearSystemFileCacheAction>()
                    .SingleOrDefault();
                if (ramMapAction is not null)
                {
                    await ExecuteRamMapBeforeBatchAsync(
                        runId,
                        batchSteps[0].Step.Id,
                        run.Plan.PlanHash,
                        ramMapAction,
                        correlationId,
                        runCancellation.Token);
                }

                foreach (var copyStep in batchSteps.Where(
                             item => IsRegisteredDirectoryCopy(item.Step)))
                {
                    await PrepareCopyBatchRecoveryAsync(
                        run,
                        copyStep.Step,
                        runCancellation.Token);
                }

                var result = await RunSupervisedTestWorkerAsync(
                    run,
                    correlationId,
                    batchSteps.Select(item => item.Request!).ToArray(),
                    batchSteps.ToDictionary(
                        item => item.Step.Id,
                        item => item.Step.ToolId,
                        StringComparer.Ordinal),
                    runCancellation.Token);

                var parseFailed = false;
                foreach (var toolResult in result.ToolResults)
                {
                    var prepared = batchSteps.Single(
                        item => string.Equals(
                            item.Step.Id,
                            toolResult.Audit.StepId,
                            StringComparison.Ordinal));
                    await testArtifactStore.SaveWorkerOutputAsync(
                        runId,
                        prepared.Step.Id,
                        result.Events.Where(item => string.Equals(
                                item.StepId,
                                prepared.Step.Id,
                                StringComparison.Ordinal))
                            .ToArray(),
                        CancellationToken.None);
                    var stepParseFailed = await new TestToolResultRepositoryWriter(
                            testRunRepository)
                        .PersistAsync(
                            runId,
                            prepared.Step.Id,
                            prepared.Adapter!,
                            result.Events.Where(item => string.Equals(
                                    item.StepId,
                                    prepared.Step.Id,
                                    StringComparison.Ordinal))
                                .ToArray(),
                            toolResult.Audit.ExitCode,
                            CancellationToken.None);
                    parseFailed |= stepParseFailed;
                    var cancelled = toolResult.Audit.TerminationReason
                        is ToolProcessTerminationReason.Cancelled;
                    var stepSucceeded = IsAcceptedToolExit(
                            prepared.Step.ToolId,
                            toolResult.Audit.ExitCode)
                        && !stepParseFailed
                        && !cancelled;
                    if (stepSucceeded)
                    {
                        try
                        {
                            await ValidateExternalDirectoryOutputAsync(
                                run,
                                prepared.Step,
                                CancellationToken.None);
                            await FinalizeCopyBatchRecoveryAsync(
                                run,
                                prepared.Step,
                                CancellationToken.None);
                        }
                        catch (Exception exception) when (
                            exception is IOException
                                or UnauthorizedAccessException
                                or InvalidDataException)
                        {
                            stepParseFailed = true;
                            parseFailed = true;
                            stepSucceeded = false;
                        }
                    }

                    if (!stepSucceeded
                        && IsRegisteredDirectoryCopy(prepared.Step))
                    {
                        await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                            runId,
                            prepared.Step.Id,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None);
                    }

                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        prepared.Step.Id,
                        cancelled
                            ? ApplicationTaskState.Cancelled
                            : stepSucceeded
                                ? ApplicationTaskState.Succeeded
                                : ApplicationTaskState.Failed,
                        CancellationToken.None);
                    if (stepSucceeded)
                    {
                        completedStepIds.Add(prepared.Step.Id);
                    }
                }

                var batchCancelled = result.ToolResults.Any(item =>
                    item.Audit.TerminationReason
                    is ToolProcessTerminationReason.Cancelled);
                var incomplete = result.ToolResults.Count != batchSteps.Count;
                if (batchCancelled
                    || incomplete && runCancellation.IsCancellationRequested)
                {
                    finalState = PersistedTestRunState.Cancelled;
                    break;
                }

                if (incomplete
                    || parseFailed
                    || result.ToolResults.Any(item =>
                    {
                        var prepared = batchSteps.Single(
                            step => StringComparer.Ordinal.Equals(
                                step.Step.Id,
                                item.Audit.StepId));
                        return !IsAcceptedToolExit(
                            prepared.Step.ToolId,
                            item.Audit.ExitCode);
                    }))
                {
                    failed = true;
                    finalState = PersistedTestRunState.Failed;
                    break;
                }
            }

            if (completedStepIds.Count != preparedSteps.Count)
            {
                foreach (var skipped in preparedSteps.Where(
                             item => !completedStepIds.Contains(item.Step.Id)))
                {
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        skipped.Step.Id,
                        finalState == PersistedTestRunState.Cancelled
                            ? ApplicationTaskState.Cancelled
                            : ApplicationTaskState.Rejected,
                        CancellationToken.None);
                }
            }
        }
        catch (Exception exception)
        {
            failed = exception is not OperationCanceledException
                     && !runCancellation.IsCancellationRequested;
            finalState = failed
                ? PersistedTestRunState.Failed
                : PersistedTestRunState.Cancelled;
            foreach (var copyStep in preparedSteps.Where(
                         item => !completedStepIds.Contains(item.Step.Id)
                             && IsRegisteredDirectoryCopy(item.Step)))
            {
                try
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        runId,
                        copyStep.Step.Id,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                }
                catch (Exception persistenceException) when (
                    persistenceException is IOException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                }
            }

            foreach (var skipped in preparedSteps.Where(
                         item => !completedStepIds.Contains(item.Step.Id)))
            {
                try
                {
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        skipped.Step.Id,
                        finalState == PersistedTestRunState.Cancelled
                            ? ApplicationTaskState.Cancelled
                            : ApplicationTaskState.Rejected,
                        CancellationToken.None);
                }
                catch (Exception persistenceException) when (
                    persistenceException is IOException
                        or Microsoft.Data.Sqlite.SqliteException
                        or KeyNotFoundException)
                {
                }
            }
        }
        finally
        {
            if (powerPlanScope is not null)
            {
                try
                {
                    await testPowerPlanScope.RestoreAsync(
                        powerPlanScope,
                        correlationId);
                }
                catch
                {
                    failed = true;
                    finalState = PersistedTestRunState.Failed;
                }
            }

            try
            {
                await testRunRepository.CompleteAsync(
                    runId,
                    finalState,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException
                    or Microsoft.Data.Sqlite.SqliteException
                    or KeyNotFoundException)
            {
            }

            lock (testSync)
            {
                if (activeTestRunId == runId)
                {
                    activeTestRunId = null;
                    activeTestCancellation = null;
                }
            }

            runCancellation.Dispose();
            tray.SetTestRun(
                null,
                finalState switch
                {
                    PersistedTestRunState.Failed => "failed",
                    PersistedTestRunState.Cancelled => "cancelled",
                    _ => "completed"
                });
            PublishTestEvent(
                runId,
                correlationId,
                null,
                TestEventKind.StateChanged,
                ApplicationTaskEventKind.StateChanged,
                finalState switch
                {
                    PersistedTestRunState.Failed => ApplicationTaskState.Failed,
                    PersistedTestRunState.Cancelled => ApplicationTaskState.Cancelled,
                    _ => ApplicationTaskState.Succeeded
                },
                $"agent.testing.{finalState.ToString().ToLowerInvariant()}",
                DateTimeOffset.UtcNow);
        }
    }

    private void PublishTestEvent(
        TestRunId runId,
        CorrelationId correlationId,
        string? stepId,
        TestEventKind testKind,
        ApplicationTaskEventKind taskKind,
        ApplicationTaskState state,
        string code,
        DateTimeOffset occurredAtUtc,
        double? progressFraction = null)
    {
        agentEvents.Publish(
            new AgentTestEvent(
                new TestEvent(
                    runId,
                    testKind,
                    new ApplicationTaskEvent(
                        new ApplicationTaskId(runId.Value),
                        correlationId,
                        taskKind,
                        state,
                        occurredAtUtc,
                        code,
                        code,
                        string.Empty,
                        stepId,
                        progressFraction))));
    }

    internal static bool IsAcceptedToolExit(ToolId? toolId, int exitCode) =>
        ToolProcessExitPolicy.IsAccepted(toolId, exitCode);

    internal static IReadOnlyList<TestStep>? OrderStepsForExecution(
        IReadOnlyList<TestStep> steps)
    {
        var byId = steps.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (byId.Count != steps.Count
            || steps.Any(item => item.DependsOn.Any(
                dependency => !byId.ContainsKey(dependency))))
        {
            return null;
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TestStep>(steps.Count);
        while (ordered.Count < steps.Count)
        {
            var next = steps.FirstOrDefault(item =>
                !completed.Contains(item.Id)
                && item.DependsOn.All(completed.Contains));
            if (next is null)
            {
                return null;
            }

            ordered.Add(next);
            completed.Add(next.Id);
        }

        return ordered;
    }

    internal static string? ValidateTestSupportActions(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SupportActions.Count == 0)
        {
            return null;
        }

        if (plan.Risk != WinPool.Execution.RiskLevel.R3ControlledSystemSupport
            || plan.SupportActions.Count > 4
            || plan.SupportActions.GroupBy(item => item.Kind)
                .Any(group => group.Count() > 1)
            || plan.SupportActions.Any(item =>
                item is not TestProcessSchedulingPolicyAction
                    and not UseTemporaryPowerPlanAction
                    and not ClearSystemFileCacheAction
                    and not FlushVolumeAction))
        {
            return "agent.testing.support_actions_require_orchestration";
        }

        var policy = plan.SupportActions
            .OfType<TestProcessSchedulingPolicyAction>()
            .SingleOrDefault();
        if (policy is not null
            && (!Enum.IsDefined(policy.Priority)
                || policy.LogicalProcessorIndices.Count == 0
                || policy.LogicalProcessorIndices.Any(index =>
                    index < 0 || index >= Environment.ProcessorCount)
                || policy.LogicalProcessorIndices.Distinct().Count()
                != policy.LogicalProcessorIndices.Count))
        {
            return "agent.testing.scheduling_policy_invalid";
        }

        var ramMap = plan.SupportActions
            .OfType<ClearSystemFileCacheAction>()
            .SingleOrDefault();
        if (ramMap is not null
            && (ramMap.Mode
                    != RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList
                || ramMap.PlannedToolIdentity is not
                {
                    SignatureTrusted: true,
                    RequiresElevation: true
                } identity
                || identity.Sha256.Length != 64
                || string.IsNullOrWhiteSpace(identity.PathBindingHash)
                || string.IsNullOrWhiteSpace(identity.Version)
                || string.IsNullOrWhiteSpace(identity.Publisher)))
        {
            return "agent.testing.rammap_action_invalid";
        }

        var flush = plan.SupportActions
            .OfType<FlushVolumeAction>()
            .SingleOrDefault();
        if (flush is not null
            && (flush.VolumeId != plan.Target.VolumeId
                || flush.PlannedTarget is not { } snapshot
                || snapshot.VolumeId != flush.VolumeId
                || string.IsNullOrWhiteSpace(snapshot.StableIdentity)
                || !snapshot.StableIdentity.StartsWith(
                    @"\\?\VOLUME{",
                    StringComparison.OrdinalIgnoreCase)
                || !snapshot.StableIdentity.EndsWith('}')
                || string.IsNullOrWhiteSpace(snapshot.DisplayIdentity)
                || !Path.IsPathFullyQualified(snapshot.DisplayIdentity)
                || !plan.Steps.Any(step =>
                    step.Action == TestActionKind.Copy
                    && step.Parameters.ContainsKey("sourceRelativeDirectory")
                    && step.Parameters.ContainsKey("destinationRelativeDirectory"))))
        {
            return "agent.testing.flush_action_invalid";
        }

        return plan.SupportActions
            .OfType<UseTemporaryPowerPlanAction>()
            .Any(item => item.PowerPlanId == Guid.Empty)
                ? "agent.testing.power_plan_invalid"
                : null;
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

    private sealed record PreparedExecutionStep(
        TestStep Step,
        ToolProcessRequest? Request,
        IExternalToolAdapter? Adapter);

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

    private static bool IsProcessLive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record AgentEndpointRecord(
    int ProtocolVersion,
    string PipeName,
    Guid Nonce,
    Guid AgentSessionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc);
