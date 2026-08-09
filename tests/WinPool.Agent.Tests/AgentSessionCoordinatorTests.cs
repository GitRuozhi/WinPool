using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentSessionCoordinatorTests
{
    [Theory]
    [InlineData(
        ElevatedBrokerOperationKind.CleanTemporaryFiles,
        SystemSupportActionKind.CleanTemporaryFiles)]
    [InlineData(
        ElevatedBrokerOperationKind.ClearSystemFileCache,
        SystemSupportActionKind.ClearSystemFileCache)]
    [InlineData(
        ElevatedBrokerOperationKind.FlushVolume,
        SystemSupportActionKind.FlushVolume)]
    [InlineData(
        ElevatedBrokerOperationKind.TrimOrOptimizeVolume,
        SystemSupportActionKind.TrimOrOptimizeVolume)]
    [InlineData(
        ElevatedBrokerOperationKind.SetActivePowerPlan,
        SystemSupportActionKind.UseTemporaryPowerPlan)]
    public void ElevatedBrokerOperationsHaveClosedAuditMappings(
        ElevatedBrokerOperationKind operation,
        SystemSupportActionKind expected)
    {
        Assert.Equal(
            expected,
            DesktopAgentRuntime.ToSystemSupportActionKind(operation));
    }

    [Fact]
    public async Task ActiveTestRequiresConfirmationBeforeStateTransition()
    {
        var actions = new RecordingShutdownActions { HasActiveTest = true };
        var coordinator = CreateCoordinator(actions);
        var correlationId = CorrelationId.New();

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                false,
                correlationId));

        Assert.Equal(ApplicationStatus.RequiresAuthorization, result.Status);
        Assert.Equal(AgentSessionState.Running, coordinator.State);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task ShutdownRunsTheRequiredOrderAndStopsSession()
    {
        var actions = new RecordingShutdownActions
        {
            HasActiveTest = true,
            FlushedEventCount = 42
        };
        var coordinator = CreateCoordinator(actions);

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.Equal(AgentSessionState.Stopped, coordinator.State);
        Assert.Equal(
            [
                AgentShutdownStep.NotifyClients,
                AgentShutdownStep.RequestTestCancellation,
                AgentShutdownStep.TerminateExternalToolJobs,
                AgentShutdownStep.StopMonitoring,
                AgentShutdownStep.RestoreTemporarySystemState,
                AgentShutdownStep.FlushSqliteQueues,
                AgentShutdownStep.CloseNamedPipes,
                AgentShutdownStep.CloseMainApplication,
                AgentShutdownStep.StopSupervisedProcesses,
                AgentShutdownStep.RemoveTrayIcon,
                AgentShutdownStep.ExitAgent
            ],
            actions.Calls);
        var response = Assert.IsType<ShutdownResponse>(result.Value);
        Assert.Equal(42, response.Result.FlushedEventCount);
        Assert.True(response.Result.TemporarySystemStateRestored);
        Assert.Equal(
            AgentShutdownStep.MarkShuttingDown,
            coordinator.ShutdownExecution!.CompletedSteps[0]);
    }

    [Fact]
    public async Task NewRequestsAreRejectedAsSoonAsShutdownStarts()
    {
        var actions = new RecordingShutdownActions { PauseNotification = true };
        var operations = new RecordingRequestOperations();
        var coordinator = CreateCoordinator(actions, operations);
        var shutdown = coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));
        await actions.NotificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "agent.request.rejected_shutting_down",
            Assert.Single(result.Messages).Code);
        Assert.Equal(0, operations.RequestCount);

        actions.ContinueNotification.SetResult();
        await shutdown;
    }

    [Fact]
    public async Task FailedStepDoesNotSkipLaterSafetyAndEvidenceSteps()
    {
        var actions = new RecordingShutdownActions
        {
            StepToFail = AgentShutdownStep.StopMonitoring
        };
        var coordinator = CreateCoordinator(actions);

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.PartiallyCompleted, result.Status);
        Assert.Contains(
            AgentShutdownStep.StopMonitoring,
            coordinator.ShutdownExecution!.FailedSteps);
        Assert.Contains(AgentShutdownStep.FlushSqliteQueues, actions.Calls);
        Assert.Contains(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.Contains(AgentShutdownStep.ExitAgent, actions.Calls);
    }

    [Fact]
    public async Task TimedOutStepDoesNotBlockCompleteExitCleanup()
    {
        var actions = new RecordingShutdownActions
        {
            StepToHang = AgentShutdownStep.TerminateExternalToolJobs
        };
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            new RecordingRequestOperations(),
            new AgentShutdownWorkflow(
                actions,
                registry,
                TimeSpan.FromMilliseconds(50)),
            registry);

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.PartiallyCompleted, result.Status);
        Assert.Contains(
            AgentShutdownStep.TerminateExternalToolJobs,
            coordinator.ShutdownExecution!.FailedSteps);
        Assert.Contains(AgentShutdownStep.StopMonitoring, actions.Calls);
        Assert.Contains(AgentShutdownStep.FlushSqliteQueues, actions.Calls);
        Assert.Contains(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.Contains(AgentShutdownStep.ExitAgent, actions.Calls);
    }

    [Fact]
    public async Task StorageLocationSwitchStopsAgentWithoutClosingMainApplication()
    {
        var actions = new RecordingShutdownActions();
        var coordinator = CreateCoordinator(actions);

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.StorageLocationSwitch,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.Succeeded, result.Status);
        Assert.DoesNotContain(AgentShutdownStep.CloseMainApplication, actions.Calls);
        Assert.Contains(AgentShutdownStep.StopMonitoring, actions.Calls);
        Assert.Contains(AgentShutdownStep.FlushSqliteQueues, actions.Calls);
        Assert.Contains(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.Contains(AgentShutdownStep.ExitAgent, actions.Calls);
    }

    [Fact]
    public async Task ProcessRegistrationIsClosedAfterShutdownBegins()
    {
        var actions = new RecordingShutdownActions { PauseNotification = true };
        var coordinator = CreateCoordinator(actions);
        var shutdown = coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.DevelopmentRestart,
                true,
                CorrelationId.New()));
        await actions.NotificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var now = DateTimeOffset.UtcNow;
        Assert.False(coordinator.TryRegisterProcess(
            new(
                987,
                AgentManagedProcessKind.TestWorker,
                CorrelationId.New(),
                now,
                now,
                SupervisedProcessState.Starting,
                true,
                null)));

        actions.ContinueNotification.SetResult();
        await shutdown;
    }

    [Fact]
    public async Task AgentDoesNotHideOrExitWhileARegisteredProcessRemainsLive()
    {
        var actions = new RecordingShutdownActions();
        var coordinator = CreateCoordinator(actions);
        var now = DateTimeOffset.UtcNow;
        Assert.True(coordinator.TryRegisterProcess(
            new(
                654,
                AgentManagedProcessKind.ExternalTool,
                CorrelationId.New(),
                now,
                now,
                SupervisedProcessState.Running,
                true,
                null)));

        var result = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.PartiallyCompleted, result.Status);
        Assert.Equal(AgentSessionState.ShuttingDown, coordinator.State);
        Assert.DoesNotContain(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.DoesNotContain(AgentShutdownStep.ExitAgent, actions.Calls);
        var response = Assert.IsType<ShutdownResponse>(result.Value);
        Assert.Equal([654], response.Result.RemainingProcessIds);
    }

    private static AgentSessionCoordinator CreateCoordinator(
        RecordingShutdownActions actions,
        RecordingRequestOperations? operations = null)
    {
        var registry = new AgentProcessRegistry();
        return new(
            operations ?? new RecordingRequestOperations(),
            new AgentShutdownWorkflow(actions, registry),
            registry);
    }

    private sealed class RecordingRequestOperations : IAgentRequestOperations
    {
        public int RequestCount { get; private set; }

        public Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
            GetAgentSnapshotRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
            OpenMainWindowRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
            StartAgentMonitoringRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
            StopAgentMonitoringRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> StartTestAsync(
            StartAgentTestRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> CancelTestAsync(
            CancelAgentTestRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> GetTestResultAsync(
            GetAgentTestResultRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ListTestRunsAsync(
            ListAgentTestRunsRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ListUserTestPresetsAsync(
            ListUserTestPresetsRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> SaveUserTestPresetAsync(
            SaveUserTestPresetRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> DeleteUserTestPresetAsync(
            DeleteUserTestPresetRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> PersistDiteLegacyImportAsync(
            PersistDiteLegacyImportRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ListDiteLegacyImportsAsync(
            ListDiteLegacyImportsRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>>
            GetDiteLegacyImportSummaryAsync(
                GetDiteLegacyImportSummaryRequest request,
                CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ExportTestRunAsync(
            ExportAgentTestRunRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
            CaptureAgentInventoryRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> DetectToolAsync(
            DetectAgentToolRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> InstallMsiToolAsync(
            InstallAgentMsiToolRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
            ExportAgentMonitorCsvRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ReviewSystemSupportAsync(
            ReviewAgentSystemSupportRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ExecuteSystemSupportAsync(
            ExecuteAgentSystemSupportRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        private Task<ApplicationResult<AgentResponse>> Succeed(AgentRequest request)
        {
            RequestCount++;
            return Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new AgentAcknowledgement(),
                    request.CorrelationId));
        }
    }

    private sealed class RecordingShutdownActions : IAgentShutdownActions
    {
        public bool HasActiveTest { get; init; }

        public int FlushedEventCount { get; init; } = 7;

        public bool PauseNotification { get; init; }

        public AgentShutdownStep? StepToFail { get; init; }

        public AgentShutdownStep? StepToHang { get; init; }

        public List<AgentShutdownStep> Calls { get; } = [];

        public TaskCompletionSource NotificationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueNotification { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task NotifyClientsAsync(
            ShutdownReason reason,
            CancellationToken cancellationToken)
        {
            Record(AgentShutdownStep.NotifyClients);
            NotificationEntered.TrySetResult();
            if (PauseNotification)
            {
                await ContinueNotification.Task;
            }
        }

        public Task RequestTestCancellationAsync(CancellationToken cancellationToken) =>
            RecordAsync(
                AgentShutdownStep.RequestTestCancellation,
                cancellationToken);

        public Task TerminateExternalToolJobsAsync(CancellationToken cancellationToken) =>
            RecordAsync(
                AgentShutdownStep.TerminateExternalToolJobs,
                cancellationToken);

        public Task StopMonitoringAsync(CancellationToken cancellationToken) =>
            RecordAsync(AgentShutdownStep.StopMonitoring, cancellationToken);

        public Task<bool> RestoreTemporarySystemStateAsync(
            CancellationToken cancellationToken)
        {
            Record(AgentShutdownStep.RestoreTemporarySystemState);
            return Task.FromResult(true);
        }

        public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken)
        {
            Record(AgentShutdownStep.FlushSqliteQueues);
            return Task.FromResult(FlushedEventCount);
        }

        public Task CloseNamedPipesAsync(CancellationToken cancellationToken) =>
            RecordAsync(AgentShutdownStep.CloseNamedPipes, cancellationToken);

        public Task CloseMainApplicationAsync(CancellationToken cancellationToken) =>
            RecordAsync(AgentShutdownStep.CloseMainApplication, cancellationToken);

        public Task StopSupervisedProcessesAsync(CancellationToken cancellationToken) =>
            RecordAsync(
                AgentShutdownStep.StopSupervisedProcesses,
                cancellationToken);

        public Task RemoveTrayIconAsync(CancellationToken cancellationToken) =>
            RecordAsync(AgentShutdownStep.RemoveTrayIcon, cancellationToken);

        public Task ExitAgentAsync(CancellationToken cancellationToken) =>
            RecordAsync(AgentShutdownStep.ExitAgent, cancellationToken);

        private async Task RecordAsync(
            AgentShutdownStep step,
            CancellationToken cancellationToken)
        {
            Record(step);
            if (StepToHang == step)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        private void Record(AgentShutdownStep step)
        {
            Calls.Add(step);
            if (StepToFail == step)
            {
                throw new ControlledShutdownStepException();
            }
        }
    }

    private sealed class ControlledShutdownStepException : Exception;
}
