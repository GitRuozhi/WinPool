using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Agent;

/// <summary>
/// Application-facing operations are expressed as closed, typed methods.
/// </summary>
public interface IAgentRequestOperations
{
    Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
        GetAgentSnapshotRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync(
        OpenAgentNativePropertiesRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
        LoadAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> SaveWorkspaceStateAsync(
        SaveAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ListSimulationDocumentsAsync(
        ListAgentSimulationDocumentsRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> LoadSimulationDocumentAsync(
        LoadAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
        SaveAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
        DeleteAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
        CommitAgentSimulationEditRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> LookupSimulationCommitAsync(
        LookupAgentSimulationCommitRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageScopedInventoryAsync(
        CaptureAgentManageScopedInventoryRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException("Scoped inventory is unavailable."));

    Task<ApplicationResult<AgentResponse>> LoadInventoryCacheAsync(
        LoadAgentInventoryCacheRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
        ExportAgentMonitorCsvRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> SetAgentPreferenceAsync(
        SetAgentPreferenceRequest request,
        CancellationToken cancellationToken);
}

public sealed class AgentSessionCoordinator
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim shutdownGate = new(1, 1);
    private readonly TaskCompletionSource runtimeAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAgentRequestOperations operations = null!;
    private AgentShutdownWorkflow shutdownWorkflow = null!;
    private readonly AgentLifecycleStateStore lifecycle;
    private readonly Func<AgentSnapshot>? recoveringSnapshotFactory;
    private readonly AgentRealModeGate? realModeGate;
    private IRealOperationService? realOperationService;
    private AgentShutdownExecution? shutdownExecution;
    private Task<AgentShutdownExecution>? shutdownTask;
    private int admittedRequests;
    private bool preparingShutdown;
    private TaskCompletionSource requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<CancellationToken, Task<bool>>? PrepareHostShutdownAsync { get; set; }


    public AgentSessionCoordinator(
        IAgentRequestOperations operations,
        AgentShutdownWorkflow shutdownWorkflow,
        AgentProcessRegistry processRegistry,
        AgentLifecycleStateStore? lifecycle = null,
        AgentRealModeGate? realModeGate = null,
        IRealOperationService? realOperationService = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.shutdownWorkflow = shutdownWorkflow
            ?? throw new ArgumentNullException(nameof(shutdownWorkflow));
        ProcessRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.lifecycle = lifecycle ?? new AgentLifecycleStateStore(ProcessRegistry);
        this.realModeGate = realModeGate;
        this.realOperationService = realOperationService;
    }

    public AgentSessionCoordinator(
        AgentProcessRegistry processRegistry,
        AgentLifecycleStateStore lifecycle,
        Func<AgentSnapshot> recoveringSnapshotFactory,
        AgentRealModeGate? realModeGate = null)
    {
        ProcessRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.lifecycle = lifecycle
            ?? throw new ArgumentNullException(nameof(lifecycle));
        this.recoveringSnapshotFactory = recoveringSnapshotFactory
            ?? throw new ArgumentNullException(nameof(recoveringSnapshotFactory));
        this.realModeGate = realModeGate;
    }

    public AgentProcessRegistry ProcessRegistry { get; }

    public AgentLifecycleState State => lifecycle.State;

    public AgentShutdownStatus ShutdownStatus => lifecycle.Snapshot();

    public AgentShutdownExecution? ShutdownExecution
    {
        get
        {
            lock (stateLock)
            {
                return shutdownExecution;
            }
        }
    }

    public bool TryRegisterProcess(AgentManagedProcess registration)
    {
        lock (stateLock)
        {
            return lifecycle.State == AgentLifecycleState.Running
                   && ProcessRegistry.TryRegister(registration);
        }
    }

    public bool CanRegisterApp(ProcessInstanceId instanceId, int processId)
    {
        lock (stateLock)
            return (lifecycle.State is AgentLifecycleState.Starting or AgentLifecycleState.Recovering or AgentLifecycleState.Running) && (!preparingShutdown
                || ProcessRegistry.TryGet(instanceId, out var process) && process?.ProcessId == processId);
    }

    public Task<ApplicationResult<AgentResponse>> HandleAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default) =>
        HandleAsync(request, null, cancellationToken);

    public async Task<ApplicationResult<AgentResponse>> HandleAsync(
        AgentRequest request, AgentVerifiedPeer? verifiedPeer, CancellationToken cancellationToken = default)
    {
        if (request is RequestAgentShutdownRequest shutdown)
            return await BeginShutdownAsync(shutdown, cancellationToken);
        if (request is AcknowledgeAgentShutdownPreparationRequest acknowledgement)
            return operations is IAgentShutdownPreparation preparation && preparation.AcknowledgePreparation(acknowledgement, verifiedPeer)
                ? ApplicationResult<AgentResponse>.Succeeded(new AgentAcknowledgement(), request.CorrelationId)
                : RejectRealRequest(request.CorrelationId, "agent.shutdown.preparation_stale");
        var admitted = false;
        lock (stateLock)
        {
            if (lifecycle.State == AgentLifecycleState.Running)
            {
                if (admittedRequests++ == 0) requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                admitted = true;
            }
            else if (request is not GetAgentSnapshotRequest)
                return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
        }
        try { return await HandleCoreAsync(request, verifiedPeer, cancellationToken); }
        finally
        {
            if (admitted) lock (stateLock) if (--admittedRequests == 0) requestsDrained.TrySetResult();
        }
    }

    private Task<ApplicationResult<AgentResponse>> HandleCoreAsync(
        AgentRequest request, AgentVerifiedPeer? verifiedPeer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is EnterAgentRealModeRequest
            or ExitAgentRealModeRequest
            or PrepareAgentRealOperationRequest
            or AcceptAgentRealOperationRequest
            or QueryAgentRealOperationRequest
            or ReconcileAgentRealOperationRequest
            or LoadAgentRealOperationEvidenceRequest
            or PrepareAgentRealStructureJobRequest
            or AcceptAgentRealStructureJobRequest
            or QueryAgentRealStructureJobRequest
            or StopAgentRealStructureJobRequest
            or QueryAgentRealVirtualDiskCreationRangeRequest
            or QueryAgentRealStructureCreationSupportRequest
            or ListAgentRecoverableRealOperationsRequest
            or QueryAgentRealPartitionResizeRangeRequest
            or StopAgentRealOperationFollowingStepsRequest)
        {
            return HandleRealOperationAsync(request, verifiedPeer, cancellationToken);
        }

        if (request is GetAgentSnapshotRequest snapshotRequest && lifecycle.State != AgentLifecycleState.Running)
        {
            return recoveringSnapshotFactory is not null ? RecoveringSnapshotAsync(snapshotRequest)
                : operations.GetSnapshotAsync(snapshotRequest, cancellationToken);
        }

        return request switch
        {
            GetAgentSnapshotRequest typed =>
                operations.GetSnapshotAsync(typed, cancellationToken),
            OpenAgentNativePropertiesRequest typed =>
                operations.OpenNativePropertiesAsync(typed, cancellationToken),
            StartAgentMonitoringRequest typed =>
                operations.StartMonitoringAsync(typed, cancellationToken),
            StopAgentMonitoringRequest typed =>
                operations.StopMonitoringAsync(typed, cancellationToken),
            LoadAgentWorkspaceStateRequest typed =>
                operations.LoadWorkspaceStateAsync(typed, cancellationToken),
            SaveAgentWorkspaceStateRequest typed =>
                operations.SaveWorkspaceStateAsync(typed, cancellationToken),
            ListAgentSimulationDocumentsRequest typed =>
                operations.ListSimulationDocumentsAsync(typed, cancellationToken),
            LoadAgentSimulationDocumentRequest typed =>
                operations.LoadSimulationDocumentAsync(typed, cancellationToken),
            SaveAgentSimulationDocumentRequest typed =>
                operations.SaveSimulationDocumentAsync(typed, cancellationToken),
            DeleteAgentSimulationDocumentRequest typed =>
                operations.DeleteSimulationDocumentAsync(typed, cancellationToken),
            CommitAgentSimulationEditRequest typed =>
                operations.CommitSimulationEditAsync(typed, cancellationToken),
            LookupAgentSimulationCommitRequest typed =>
                operations.LookupSimulationCommitAsync(typed, cancellationToken),
            CaptureAgentManageInventoryRequest typed =>
                operations.CaptureManageInventoryAsync(typed, cancellationToken),
            CaptureAgentManageScopedInventoryRequest typed =>
                operations.CaptureManageScopedInventoryAsync(typed, cancellationToken),
            LoadAgentInventoryCacheRequest typed =>
                operations.LoadInventoryCacheAsync(typed, cancellationToken),
            ExportAgentMonitorCsvRequest typed =>
                operations.ExportMonitorCsvAsync(typed, cancellationToken),
            SetAgentPreferenceRequest typed =>
                operations.SetAgentPreferenceAsync(typed, cancellationToken),
            _ => Task.FromResult(RejectUnsupportedRequest(request.CorrelationId))
        };
    }

    public void AttachRealOperationService(IRealOperationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (stateLock)
        {
            if (realOperationService is not null)
            {
                throw new InvalidOperationException("A real operation service is already attached.");
            }

            realOperationService = service;
        }
    }

    private async Task<ApplicationResult<AgentResponse>> HandleRealOperationAsync(
        AgentRequest request,
        AgentVerifiedPeer? peer,
        CancellationToken cancellationToken)
    {
        IRealOperationService? service;
        lock (stateLock)
        {
            if (lifecycle.State != AgentLifecycleState.Running)
            {
                return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
            }

            service = realOperationService;
        }

        if (service is null || realModeGate is null || peer is null)
        {
            return RejectRealRequest(request.CorrelationId, "agent.real_operation.unavailable");
        }

        if (!realModeGate.TryValidatePeer(peer, out var code))
        {
            return RejectRealRequest(request.CorrelationId, code);
        }

        if (request is EnterAgentRealModeRequest enter)
        {
            if (!realModeGate.TryEnter(peer, enter.ProductSessionId, out code))
            {
                return RejectRealRequest(request.CorrelationId, code);
            }

            try
            {
                var entered = await service.EnterModeAsync(
                    enter, TrustedSession(peer, enter.ProductSessionId, true), cancellationToken);
                if (!entered.IsSuccess)
                {
                    realModeGate.TryExit(peer, enter.ProductSessionId);
                }

                return entered;
            }
            catch
            {
                realModeGate.TryExit(peer, enter.ProductSessionId);
                throw;
            }
        }

        if (request is ExitAgentRealModeRequest exit)
        {
            var wasArmed = realModeGate.TryExit(peer, exit.ProductSessionId);
            return await service.ExitModeAsync(
                exit, TrustedSession(peer, exit.ProductSessionId, wasArmed), cancellationToken);
        }

        if (request is ListAgentRecoverableRealOperationsRequest list)
            return await service.ListRecoverableAsync(list,
                TrustedSession(peer, string.Empty, false), cancellationToken);

        if (request is QueryAgentRealOperationRequest query)
        {
            // Read-only reconciliation remains possible after mode exit or App replacement.
            return await service.QueryAsync(
                query, TrustedSession(peer, string.Empty, false), cancellationToken);
        }

        if (request is ReconcileAgentRealOperationRequest recheck)
            return await service.RecheckAsync(recheck,
                TrustedSession(peer, string.Empty, false), cancellationToken);

        if (request is LoadAgentRealOperationEvidenceRequest evidence)
            return await service.LoadEvidenceAsync(evidence,
                TrustedSession(peer, string.Empty, false), cancellationToken);

        if (request is QueryAgentRealStructureJobRequest queryJob)
            return await service.QueryStructureJobAsync(queryJob,
                TrustedSession(peer, string.Empty, false), cancellationToken);
        if (request is StopAgentRealStructureJobRequest stopJob)
            return await service.StopStructureJobAsync(stopJob,
                TrustedSession(peer, string.Empty, false), cancellationToken);
        if (request is AcceptAgentRealStructureJobRequest acceptJob)
        {
            var armed = realModeGate.TryUseForNewWrite(peer, acceptJob.ProductSessionId, out _);
            return await service.AcceptStructureJobAsync(acceptJob,
                TrustedSession(peer, acceptJob.ProductSessionId, armed), cancellationToken);
        }

        if (request is StopAgentRealOperationFollowingStepsRequest stop)
        {
            // Stopping steps that have not begun is available after mode exit.
            // The service must match the persisted operation and plan hash.
            var isArmed = realModeGate.TryUseForNewWrite(
                peer, stop.ProductSessionId, out _);
            return await service.StopFollowingStepsAsync(
                stop, TrustedSession(peer, stop.ProductSessionId, isArmed), cancellationToken);
        }

        if (request is AcceptAgentRealOperationRequest accept)
        {
            // A repeated Accept for an already persisted operation is a status
            // lookup. The service must reject a new acceptance unless armed.
            var isArmed = realModeGate.TryUseForNewWrite(
                peer, accept.ProductSessionId, out _);
            return await service.AcceptAsync(
                accept, TrustedSession(peer, accept.ProductSessionId, isArmed),
                cancellationToken);
        }

        var productSessionId = request switch
        {
            PrepareAgentRealOperationRequest typed => typed.ProductSessionId,
            PrepareAgentRealStructureJobRequest typed => typed.ProductSessionId,
            QueryAgentRealVirtualDiskCreationRangeRequest typed => typed.ProductSessionId,
            QueryAgentRealStructureCreationSupportRequest typed => typed.ProductSessionId,
            QueryAgentRealPartitionResizeRangeRequest typed => typed.ProductSessionId,
            _ => string.Empty
        };
        if (!realModeGate.TryUseForNewWrite(peer, productSessionId, out code))
        {
            return RejectRealRequest(request.CorrelationId, code);
        }

        var session = TrustedSession(peer, productSessionId, true);
        return request switch
        {
            PrepareAgentRealOperationRequest typed =>
                await service.PrepareAsync(typed, session, cancellationToken),
            PrepareAgentRealStructureJobRequest typed =>
                await service.PrepareStructureJobAsync(typed, session, cancellationToken),
            QueryAgentRealVirtualDiskCreationRangeRequest typed =>
                await service.QueryVirtualDiskCreationRangeAsync(typed, session, cancellationToken),
            QueryAgentRealStructureCreationSupportRequest typed =>
                await service.QueryStructureCreationSupportAsync(typed, session, cancellationToken),
            QueryAgentRealPartitionResizeRangeRequest typed =>
                await service.QueryPartitionResizeRangeAsync(
                    typed, session, cancellationToken),
            _ => RejectUnsupportedRequest(request.CorrelationId)
        };
    }

    private static TrustedRealSession TrustedSession(
        AgentVerifiedPeer peer,
        string productSessionId,
        bool isArmed) =>
        new(
            new SessionId(peer.AgentSessionId),
            productSessionId,
            peer.ProcessInstanceId.Value.ToString("D"),
            peer.ProcessId,
            peer.StartedAtUtc,
            peer.ImagePath,
            isArmed);

    private static ApplicationResult<AgentResponse> RejectRealRequest(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code, ApplicationMessageSeverity.Warning));

    private async Task<ApplicationResult<AgentResponse>> BeginShutdownAsync(
        RequestAgentShutdownRequest request,
        CancellationToken cancellationToken)
    {
        Task? attachmentWait = null;
        lock (stateLock)
        {
            if (shutdownWorkflow is null)
            {
                if (lifecycle.State is not (AgentLifecycleState.Starting or AgentLifecycleState.Recovering))
                {
                    return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
                }
                attachmentWait = runtimeAttached.Task;
            }
        }
        if (attachmentWait is not null)
        {
            try
            {
                // The endpoint precedes runtime attachment. Wait without
                // closing admission or changing lifecycle state. The server's
                // token covers its lifetime, so also bound this wait before
                // the App's 12-second shutdown request can expire.
                await attachmentWait.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        Task<AgentShutdownExecution> executionTask;
        await shutdownGate.WaitAsync(CancellationToken.None);
        try
        {
            lock (stateLock)
            {
                if (lifecycle.State == AgentLifecycleState.Stopped && shutdownExecution is not null)
                {
                    return ResultForExecution(shutdownExecution, request.CorrelationId);
                }

                if (shutdownTask is { IsCompleted: false })
                {
                    executionTask = shutdownTask;
                }
                else executionTask = null!;
            }
            if (executionTask is null)
            {
                var attemptId = Guid.NewGuid();
                var preparation = operations as IAgentShutdownPreparation;
                var proceeding = false;
                try
                {
                    lock (stateLock) preparingShutdown = true;
                    if (preparation is not null
                        && !await preparation.PrepareClientsAsync(attemptId, request.Reason, cancellationToken))
                        return RejectRealRequest(request.CorrelationId, "agent.shutdown.preparation_cancelled");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PrepareHostShutdownAsync is not null && !await PrepareHostShutdownAsync(cancellationToken))
                        return RejectRealRequest(request.CorrelationId, "agent.shutdown.real_operation_active");
                    if (realOperationService is AgentRealOperationService realService
                        && !await realService.TryCloseAdmissionForShutdownAsync())
                        return RejectRealRequest(request.CorrelationId, "agent.shutdown.real_operation_active");
                    Task drained;
                    lock (stateLock)
                    {
                        lifecycle.MarkShuttingDown(DateTimeOffset.UtcNow);
                        drained = admittedRequests == 0 ? Task.CompletedTask : requestsDrained.Task;
                        shutdownTask = Task.Run(async () =>
                        {
                            await drained.ConfigureAwait(false);
                            return await CompleteShutdownAsync(request.Reason).ConfigureAwait(false);
                        });
                        executionTask = shutdownTask;
                    }
                    proceeding = true;
                }
                finally
                {
                    lock (stateLock) preparingShutdown = false;
                    preparation?.EndPreparation(attemptId, proceeding);
                }
            }

            if (request.BeginInBackground)
            {
                // The old App needs this reply before it closes. The workflow
                // then observes that exact App instance leave and only then lets
                // the old Agent release its endpoint and SQLite writer lease.
                return ApplicationResult<AgentResponse>.Succeeded(
                    new AgentAcknowledgement(),
                    request.CorrelationId);
            }
        }
        finally
        {
            shutdownGate.Release();
        }

        var execution = await executionTask;
        return ResultForExecution(execution, request.CorrelationId);
    }

    private async Task<AgentShutdownExecution> CompleteShutdownAsync(ShutdownReason reason)
    {
        var execution = await shutdownWorkflow.ExecuteAsync(reason).ConfigureAwait(false);
        lock (stateLock)
        {
            shutdownExecution = execution;
            lifecycle.RecordExecution(execution);
        }

        return execution;
    }

    private Task<ApplicationResult<AgentResponse>> RecoveringSnapshotAsync(
        GetAgentSnapshotRequest request)
    {
        var snapshot = recoveringSnapshotFactory!();
        return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(
            new AgentSnapshotResponse(snapshot),
            request.CorrelationId));
    }

    /// <summary>
    /// Attaches runtime work after the endpoint is already available. Lifecycle
    /// admission still keeps it unavailable until recovery reaches Ready.
    /// </summary>
    public void AttachRuntime(
        IAgentRequestOperations runtimeOperations,
        AgentShutdownWorkflow runtimeShutdownWorkflow)
    {
        ArgumentNullException.ThrowIfNull(runtimeOperations);
        ArgumentNullException.ThrowIfNull(runtimeShutdownWorkflow);
        lock (stateLock)
        {
            operations = runtimeOperations;
            shutdownWorkflow = runtimeShutdownWorkflow;
        }
        runtimeAttached.TrySetResult();
    }

    private static ApplicationResult<AgentResponse> ResultForExecution(
        AgentShutdownExecution execution,
        CorrelationId correlationId)
    {
        var response = new ShutdownResponse(execution.Result);
        return execution.Result.Completed
            ? ApplicationResult<AgentResponse>.Succeeded(response, correlationId)
            : new(
                ApplicationStatus.PartiallyCompleted,
                response,
                [Message("agent.shutdown.incomplete", ApplicationMessageSeverity.Warning)],
                correlationId);
    }

    private static ApplicationResult<AgentResponse> RejectUnavailableRequest(
        CorrelationId correlationId,
        AgentLifecycleState state) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(
                state is AgentLifecycleState.Starting or AgentLifecycleState.Recovering
                    ? "agent.request.recovering"
                    : "agent.request.rejected_shutting_down",
                ApplicationMessageSeverity.Warning));

    private static ApplicationResult<AgentResponse> RejectUnsupportedRequest(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(
                "agent.request.unsupported_type",
                ApplicationMessageSeverity.Warning));

    private static ApplicationMessage Message(
        string code,
        ApplicationMessageSeverity severity) =>
        new(
            code,
            code,
            string.Empty,
            severity,
            []);
}
