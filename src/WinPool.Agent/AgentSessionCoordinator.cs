using WinPool.Application;

namespace WinPool.Agent;

public enum AgentSessionState
{
    Running,
    ShuttingDown,
    Stopped
}

/// <summary>
/// Application-facing operations are expressed as closed, typed methods.
/// </summary>
public interface IAgentRequestOperations
{
    Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
        GetAgentSnapshotRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> GetDevelopmentDiagnosticsAsync(
        GetDevelopmentDiagnosticsRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
        OpenMainWindowRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StartTestAsync(
        StartAgentTestRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CancelTestAsync(
        CancelAgentTestRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> GetTestResultAsync(
        GetAgentTestResultRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ListTestRunsAsync(
        ListAgentTestRunsRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ListUserTestPresetsAsync(
        ListUserTestPresetsRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> SaveUserTestPresetAsync(
        SaveUserTestPresetRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> DeleteUserTestPresetAsync(
        DeleteUserTestPresetRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
        LoadAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> SaveWorkspaceStateAsync(
        SaveAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> ListSimulationDocumentsAsync(
        ListAgentSimulationDocumentsRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
        SaveAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
        DeleteAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
        CommitAgentSimulationEditRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    private static Task<ApplicationResult<AgentResponse>> UnsupportedPersistenceAsync(
        CorrelationId correlationId) =>
        Task.FromResult(
            ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    "agent.persistence.unsupported",
                    "Agent persistence is unavailable.",
                    string.Empty,
                    ApplicationMessageSeverity.Warning,
                    [])));

    Task<ApplicationResult<AgentResponse>> PersistDiteLegacyImportAsync(
        PersistDiteLegacyImportRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ListDiteLegacyImportsAsync(
        ListDiteLegacyImportsRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> GetDiteLegacyImportSummaryAsync(
        GetDiteLegacyImportSummaryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExportTestRunAsync(
        ExportAgentTestRunRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedPersistenceAsync(request.CorrelationId);

    Task<ApplicationResult<AgentResponse>> DetectToolAsync(
        DetectAgentToolRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> InstallMsiToolAsync(
        InstallAgentMsiToolRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
        ExportAgentMonitorCsvRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ReviewSystemSupportAsync(
        ReviewAgentSystemSupportRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExecuteSystemSupportAsync(
        ExecuteAgentSystemSupportRequest request,
        CancellationToken cancellationToken);
}

public sealed class AgentSessionCoordinator
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim shutdownGate = new(1, 1);
    private readonly IAgentRequestOperations operations;
    private readonly AgentShutdownWorkflow shutdownWorkflow;
    private AgentSessionState state = AgentSessionState.Running;
    private AgentShutdownExecution? shutdownExecution;

    public AgentSessionCoordinator(
        IAgentRequestOperations operations,
        AgentShutdownWorkflow shutdownWorkflow,
        AgentProcessRegistry processRegistry)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.shutdownWorkflow = shutdownWorkflow
            ?? throw new ArgumentNullException(nameof(shutdownWorkflow));
        ProcessRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
    }

    public AgentProcessRegistry ProcessRegistry { get; }

    public AgentSessionState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

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
            return state == AgentSessionState.Running
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
            if (state != AgentSessionState.Running)
            {
                return Task.FromResult(RejectNewRequest(request.CorrelationId));
            }
        }

        return request switch
        {
            GetAgentSnapshotRequest typed =>
                operations.GetSnapshotAsync(typed, cancellationToken),
            GetDevelopmentDiagnosticsRequest typed =>
                operations.GetDevelopmentDiagnosticsAsync(typed, cancellationToken),
            OpenMainWindowRequest typed =>
                operations.OpenMainWindowAsync(typed, cancellationToken),
            StartAgentMonitoringRequest typed =>
                operations.StartMonitoringAsync(typed, cancellationToken),
            StopAgentMonitoringRequest typed =>
                operations.StopMonitoringAsync(typed, cancellationToken),
            StartAgentTestRequest typed =>
                operations.StartTestAsync(typed, cancellationToken),
            CancelAgentTestRequest typed =>
                operations.CancelTestAsync(typed, cancellationToken),
            GetAgentTestResultRequest typed =>
                operations.GetTestResultAsync(typed, cancellationToken),
            ListAgentTestRunsRequest typed =>
                operations.ListTestRunsAsync(typed, cancellationToken),
            ListUserTestPresetsRequest typed =>
                operations.ListUserTestPresetsAsync(typed, cancellationToken),
            SaveUserTestPresetRequest typed =>
                operations.SaveUserTestPresetAsync(typed, cancellationToken),
            DeleteUserTestPresetRequest typed =>
                operations.DeleteUserTestPresetAsync(typed, cancellationToken),
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
            PersistDiteLegacyImportRequest typed =>
                operations.PersistDiteLegacyImportAsync(typed, cancellationToken),
            ListDiteLegacyImportsRequest typed =>
                operations.ListDiteLegacyImportsAsync(typed, cancellationToken),
            GetDiteLegacyImportSummaryRequest typed =>
                operations.GetDiteLegacyImportSummaryAsync(
                    typed,
                    cancellationToken),
            ExportAgentTestRunRequest typed =>
                operations.ExportTestRunAsync(typed, cancellationToken),
            CaptureAgentInventoryRequest typed =>
                operations.CaptureInventoryAsync(typed, cancellationToken),
            CaptureAgentManageInventoryRequest typed =>
                operations.CaptureManageInventoryAsync(typed, cancellationToken),
            LoadAgentManageInventoryRequest typed =>
                operations.LoadManageInventoryAsync(typed, cancellationToken),
            DetectAgentToolRequest typed =>
                operations.DetectToolAsync(typed, cancellationToken),
            InstallAgentMsiToolRequest typed =>
                operations.InstallMsiToolAsync(typed, cancellationToken),
            ExportAgentMonitorCsvRequest typed =>
                operations.ExportMonitorCsvAsync(typed, cancellationToken),
            ReviewAgentSystemSupportRequest typed =>
                operations.ReviewSystemSupportAsync(typed, cancellationToken),
            ExecuteAgentSystemSupportRequest typed =>
                operations.ExecuteSystemSupportAsync(typed, cancellationToken),
            _ => Task.FromResult(RejectUnsupportedRequest(request.CorrelationId))
        };
    }

    private async Task<ApplicationResult<AgentResponse>> BeginShutdownAsync(
        RequestAgentShutdownRequest request)
    {
        await shutdownGate.WaitAsync(CancellationToken.None);
        try
        {
            lock (stateLock)
            {
                if (state != AgentSessionState.Running)
                {
                    return RejectNewRequest(request.CorrelationId);
                }

                if (shutdownWorkflow.HasActiveTest
                    && !request.UserConfirmedActiveTestCancellation)
                {
                    return RequiresActiveTestConfirmation(request.CorrelationId);
                }

                // The state transition happens before notifications or any awaited work.
                state = AgentSessionState.ShuttingDown;
            }

            var execution = await shutdownWorkflow.ExecuteAsync(request.Reason);
            lock (stateLock)
            {
                shutdownExecution = execution;
                if (execution.CompletedSteps.Contains(AgentShutdownStep.ExitAgent))
                {
                    state = AgentSessionState.Stopped;
                }
            }

            var response = new ShutdownResponse(execution.Result);
            if (execution.Result.Completed)
            {
                return ApplicationResult<AgentResponse>.Succeeded(
                    response,
                    request.CorrelationId);
            }

            return new(
                ApplicationStatus.PartiallyCompleted,
                response,
                [
                    Message(
                        "agent.shutdown.incomplete",
                        ApplicationMessageSeverity.Warning)
                ],
                request.CorrelationId);
        }
        finally
        {
            shutdownGate.Release();
        }
    }

    private static ApplicationResult<AgentResponse> RejectNewRequest(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(
                "agent.request.rejected_shutting_down",
                ApplicationMessageSeverity.Warning));

    private static ApplicationResult<AgentResponse> RejectUnsupportedRequest(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(
                "agent.request.unsupported_type",
                ApplicationMessageSeverity.Warning));

    private static ApplicationResult<AgentResponse> RequiresActiveTestConfirmation(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.RequiresAuthorization,
            correlationId,
            Message(
                "agent.shutdown.active_test_confirmation_required",
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
