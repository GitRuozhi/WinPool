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

    Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
        OpenMainWindowRequest request,
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

    Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageScopedInventoryAsync(
        CaptureAgentManageScopedInventoryRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException("Scoped inventory is unavailable."));

    Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
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
    private IAgentRequestOperations operations = null!;
    private AgentShutdownWorkflow shutdownWorkflow = null!;
    private readonly AgentLifecycleStateStore lifecycle;
    private readonly Func<AgentSnapshot>? recoveringSnapshotFactory;
    private readonly AgentRealModeGate? realModeGate;
    private IRealOperationService? realOperationService;
    private AgentShutdownExecution? shutdownExecution;
    private Task<AgentShutdownExecution>? shutdownTask;

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

    public Task<ApplicationResult<AgentResponse>> HandleAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default) =>
        HandleAsync(request, null, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> HandleAsync(
        AgentRequest request,
        AgentVerifiedPeer? verifiedPeer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is RequestAgentShutdownRequest shutdownRequest)
        {
            return BeginShutdownAsync(shutdownRequest);
        }

        if (request is EnterAgentRealModeRequest
            or ExitAgentRealModeRequest
            or PrepareAgentRealOperationRequest
            or AcceptAgentRealOperationRequest
            or QueryAgentRealOperationRequest
            or QueryAgentRealVirtualDiskCreationRangeRequest
            or QueryAgentRealStructureCreationSupportRequest
            or ListAgentRecoverableRealOperationsRequest
            or QueryAgentRealPartitionResizeRangeRequest
            or StopAgentRealOperationFollowingStepsRequest)
        {
            return HandleRealOperationAsync(request, verifiedPeer, cancellationToken);
        }

        lock (stateLock)
        {
            if (lifecycle.State != AgentLifecycleState.Running)
            {
                if (request is GetAgentSnapshotRequest snapshotRequest)
                {
                    return recoveringSnapshotFactory is not null
                        ? RecoveringSnapshotAsync(snapshotRequest)
                        : operations.GetSnapshotAsync(snapshotRequest, cancellationToken);
                }

                return Task.FromResult(RejectUnavailableRequest(
                    request.CorrelationId,
                    lifecycle.State));
            }
        }

        return request switch
        {
            GetAgentSnapshotRequest typed =>
                operations.GetSnapshotAsync(typed, cancellationToken),
            OpenMainWindowRequest typed =>
                operations.OpenMainWindowAsync(typed, cancellationToken),
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
            CaptureAgentInventoryRequest typed =>
                operations.CaptureInventoryAsync(typed, cancellationToken),
            CaptureAgentManageInventoryRequest typed =>
                operations.CaptureManageInventoryAsync(typed, cancellationToken),
            CaptureAgentManageScopedInventoryRequest typed =>
                operations.CaptureManageScopedInventoryAsync(typed, cancellationToken),
            LoadAgentManageInventoryRequest typed =>
                operations.LoadManageInventoryAsync(typed, cancellationToken),
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
        RequestAgentShutdownRequest request)
    {
        if (recoveringSnapshotFactory is not null
            && lifecycle.State is AgentLifecycleState.Starting or AgentLifecycleState.Recovering)
        {
            return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
        }

        if (realOperationService is AgentRealOperationService realService
            && !await realService.TryCloseAdmissionForShutdownAsync())
        {
            return RejectRealRequest(
                request.CorrelationId, "agent.shutdown.real_operation_unfinished");
        }

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
                else
                {
                    // The gate guarantees one workflow. A subsequent request starts
                    // another attempt only after a completed pending shutdown.
                    lifecycle.MarkShuttingDown(DateTimeOffset.UtcNow);
                    shutdownTask = Task.Run(() => CompleteShutdownAsync(request.Reason));
                    executionTask = shutdownTask;
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
