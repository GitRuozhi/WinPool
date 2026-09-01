using WinPool.Application;

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

    Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
        SaveAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
        DeleteAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
        CommitAgentSimulationEditRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
        ExportAgentMonitorCsvRequest request,
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
    private AgentShutdownExecution? shutdownExecution;

    public AgentSessionCoordinator(
        IAgentRequestOperations operations,
        AgentShutdownWorkflow shutdownWorkflow,
        AgentProcessRegistry processRegistry,
        AgentLifecycleStateStore? lifecycle = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.shutdownWorkflow = shutdownWorkflow
            ?? throw new ArgumentNullException(nameof(shutdownWorkflow));
        ProcessRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.lifecycle = lifecycle ?? new AgentLifecycleStateStore(ProcessRegistry);
    }

    public AgentSessionCoordinator(
        AgentProcessRegistry processRegistry,
        AgentLifecycleStateStore lifecycle,
        Func<AgentSnapshot> recoveringSnapshotFactory)
    {
        ProcessRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.lifecycle = lifecycle
            ?? throw new ArgumentNullException(nameof(lifecycle));
        this.recoveringSnapshotFactory = recoveringSnapshotFactory
            ?? throw new ArgumentNullException(nameof(recoveringSnapshotFactory));
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is RequestAgentShutdownRequest shutdownRequest)
        {
            return BeginShutdownAsync(shutdownRequest);
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
            SaveAgentSimulationDocumentRequest typed =>
                operations.SaveSimulationDocumentAsync(typed, cancellationToken),
            DeleteAgentSimulationDocumentRequest typed =>
                operations.DeleteSimulationDocumentAsync(typed, cancellationToken),
            CommitAgentSimulationEditRequest typed =>
                operations.CommitSimulationEditAsync(typed, cancellationToken),
            CaptureAgentInventoryRequest typed =>
                operations.CaptureInventoryAsync(typed, cancellationToken),
            CaptureAgentManageInventoryRequest typed =>
                operations.CaptureManageInventoryAsync(typed, cancellationToken),
            LoadAgentManageInventoryRequest typed =>
                operations.LoadManageInventoryAsync(typed, cancellationToken),
            ExportAgentMonitorCsvRequest typed =>
                operations.ExportMonitorCsvAsync(typed, cancellationToken),
            _ => Task.FromResult(RejectUnsupportedRequest(request.CorrelationId))
        };
    }

    private async Task<ApplicationResult<AgentResponse>> BeginShutdownAsync(
        RequestAgentShutdownRequest request)
    {
        if (recoveringSnapshotFactory is not null
            && lifecycle.State is AgentLifecycleState.Starting or AgentLifecycleState.Recovering)
        {
            return RejectUnavailableRequest(request.CorrelationId, lifecycle.State);
        }

        await shutdownGate.WaitAsync(CancellationToken.None);
        try
        {
            lock (stateLock)
            {
                if (lifecycle.State == AgentLifecycleState.Stopped && shutdownExecution is not null)
                {
                    return ResultForExecution(shutdownExecution, request.CorrelationId);
                }

                // The gate guarantees one workflow. A second request joins the first,
                // then retries only after the first has reached ShutdownPending.
                lifecycle.MarkShuttingDown(DateTimeOffset.UtcNow);
            }

            var execution = await shutdownWorkflow.ExecuteAsync(request.Reason);
            lock (stateLock)
            {
                shutdownExecution = execution;
                lifecycle.RecordExecution(execution);
            }

            return ResultForExecution(execution, request.CorrelationId);
        }
        finally
        {
            shutdownGate.Release();
        }
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
