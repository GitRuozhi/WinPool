using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;

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
        Assert.Equal(AgentLifecycleState.Running, coordinator.State);
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
        Assert.Equal(AgentLifecycleState.Stopped, coordinator.State);
        Assert.Equal(
            [
                AgentShutdownStep.NotifyClients,
                AgentShutdownStep.RequestTestCancellation,
                AgentShutdownStep.TerminateExternalToolJobs,
                AgentShutdownStep.StopMonitoring,
                AgentShutdownStep.RestoreTemporarySystemState,
                AgentShutdownStep.FlushSqliteQueues,
                AgentShutdownStep.CloseMainApplication,
                AgentShutdownStep.StopSupervisedProcesses,
                AgentShutdownStep.CloseNamedPipes,
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
            new OpenMainWindowRequest(null, CorrelationId.New()));

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal(
            "agent.request.rejected_shutting_down",
            Assert.Single(result.Messages).Code);
        Assert.Equal(0, operations.RequestCount);

        actions.ContinueNotification.SetResult();
        await shutdown;
    }

    [Fact]
    public async Task RecoveringAgentAllowsSnapshotButRejectsWorkWithoutFailureSemantics()
    {
        var actions = new RecordingShutdownActions();
        var operations = new RecordingRequestOperations();
        var registry = new AgentProcessRegistry();
        var lifecycle = new AgentLifecycleStateStore(
            registry,
            AgentLifecycleState.Starting);
        lifecycle.MarkRecovering();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(actions, registry),
            registry,
            lifecycle);

        var snapshot = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        var work = await coordinator.HandleAsync(
            new StartAgentMonitoringRequest(
                new MonitorRequest(
                    SessionId.New(),
                    SystemId.New(),
                    [],
                    [],
                    TimeSpan.FromSeconds(1),
                    true),
                CorrelationId.New()));

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(ApplicationStatus.Rejected, work.Status);
        Assert.Equal("agent.request.recovering", Assert.Single(work.Messages).Code);
        Assert.Equal(1, operations.RequestCount);
    }

    [Fact]
    public async Task BootstrapCoordinatorPublishesRecoveringSnapshotBeforeRuntimeAttachment()
    {
        var registry = new AgentProcessRegistry();
        var lifecycle = new AgentLifecycleStateStore(
            registry,
            AgentLifecycleState.Starting);
        lifecycle.MarkRecovering();
        var instanceId = new AgentInstanceId(Guid.NewGuid());
        var coordinator = new AgentSessionCoordinator(
            registry,
            lifecycle,
            () => new AgentSnapshot(
                instanceId,
                IsTrayVisible: false,
                ActiveMonitoringSession: null,
                ActiveTestRunId: null,
                lifecycle.Snapshot(),
                [],
                [],
                [],
                new MonitorRuntimeDiagnostics(0, 0),
                []));

        var snapshot = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        var rejected = await coordinator.HandleAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()));

        var response = Assert.IsType<AgentSnapshotResponse>(snapshot.Value);
        Assert.Equal(instanceId, response.Snapshot.AgentInstanceId);
        Assert.Equal(AgentLifecycleState.Recovering, response.Snapshot.ShutdownStatus.State);
        Assert.Equal(ApplicationStatus.Rejected, rejected.Status);
        Assert.Equal("agent.request.recovering", Assert.Single(rejected.Messages).Code);

        var operations = new RecordingRequestOperations();
        coordinator.AttachRuntime(
            operations,
            new AgentShutdownWorkflow(new RecordingShutdownActions(), registry));
        lifecycle.MarkReady();
        Assert.True((await coordinator.HandleAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()))).IsSuccess);
        Assert.Equal(1, operations.RequestCount);
    }

    [Fact]
    public async Task ReadyTransitionAdmitsRequestsAfterRecovery()
    {
        var actions = new RecordingShutdownActions();
        var operations = new RecordingRequestOperations();
        var registry = new AgentProcessRegistry();
        var lifecycle = new AgentLifecycleStateStore(
            registry,
            AgentLifecycleState.Starting);
        lifecycle.MarkRecovering();
        lifecycle.MarkReady();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(actions, registry),
            registry,
            lifecycle);

        var result = await coordinator.HandleAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()));

        Assert.True(result.IsSuccess);
        Assert.Equal(AgentLifecycleState.Running, coordinator.State);
        Assert.Equal(1, operations.RequestCount);
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
        Assert.DoesNotContain(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.DoesNotContain(AgentShutdownStep.ExitAgent, actions.Calls);
        Assert.Equal(AgentLifecycleState.ShutdownPending, coordinator.State);
        Assert.Equal(AgentLifecycleState.ShutdownPending, coordinator.ShutdownStatus.State);
        Assert.True(coordinator.ShutdownStatus.CanRetry);
        Assert.Contains(
            "agent.shutdown.stop_monitoring",
            coordinator.ShutdownStatus.FailedStepCodes);
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
        Assert.DoesNotContain(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.DoesNotContain(AgentShutdownStep.ExitAgent, actions.Calls);
        Assert.Equal(AgentLifecycleState.ShutdownPending, coordinator.State);
    }

    [Fact]
    public async Task NonCooperativeShutdownStepTimesOutWithoutBlockingWorkflow()
    {
        var actions = new RecordingShutdownActions
        {
            StepToIgnoreCancellation = AgentShutdownStep.StopMonitoring
        };
        var workflow = new AgentShutdownWorkflow(
            actions,
            new AgentProcessRegistry(),
            TimeSpan.FromMilliseconds(50));

        var execution = await workflow.ExecuteAsync(ShutdownReason.TrayExit)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(AgentShutdownStep.StopMonitoring, execution.FailedSteps);
        Assert.DoesNotContain(AgentShutdownStep.ExitAgent, execution.CompletedSteps);
        actions.ContinueNonCooperativeStep.TrySetResult();
    }

    [Fact]
    public async Task TimedOutTerminalActionCannotCommitAfterItsAttemptExpires()
    {
        var actions = new FencedTerminalShutdownActions();
        var workflow = new AgentShutdownWorkflow(
            actions,
            new AgentProcessRegistry(),
            TimeSpan.FromMilliseconds(50));
        var executionTask = workflow.ExecuteAsync(ShutdownReason.TrayExit);
        await actions.ExitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(2));
        actions.AllowLateExit.TrySetResult();
        await actions.LateExitFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(AgentShutdownStep.ExitAgent, execution.FailedSteps);
        Assert.False(actions.ExitCommitted);
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
                ProcessInstanceId.New(),
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
                ProcessInstanceId.New(),
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
        Assert.Equal(AgentLifecycleState.ShutdownPending, coordinator.State);
        Assert.DoesNotContain(AgentShutdownStep.RemoveTrayIcon, actions.Calls);
        Assert.DoesNotContain(AgentShutdownStep.ExitAgent, actions.Calls);
        var response = Assert.IsType<ShutdownResponse>(result.Value);
        Assert.Equal([654], response.Result.RemainingProcessIds);
    }

    [Fact]
    public async Task ShutdownPendingKeepsSnapshotAvailableAndCanRetryToStopped()
    {
        var actions = new RecordingShutdownActions();
        var operations = new RecordingRequestOperations();
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(actions, registry),
            registry);
        var now = DateTimeOffset.UtcNow;
        var registration = new AgentManagedProcess(
            ProcessInstanceId.New(),
            765,
            AgentManagedProcessKind.ExternalTool,
            CorrelationId.New(),
            now,
            now,
            SupervisedProcessState.Running,
            true,
            null);
        Assert.True(coordinator.TryRegisterProcess(registration));

        var first = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.PartiallyCompleted, first.Status);
        Assert.Equal(AgentLifecycleState.ShutdownPending, coordinator.State);
        var snapshot = await coordinator.HandleAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()));
        Assert.Equal(ApplicationStatus.Succeeded, snapshot.Status);

        Assert.True(registry.TryMarkExited(
            registration.ProcessInstanceId,
            registration.ProcessId,
            DateTimeOffset.UtcNow,
            out _));
        var retried = await coordinator.HandleAsync(
            new RequestAgentShutdownRequest(
                ShutdownReason.TrayExit,
                true,
                CorrelationId.New()));

        Assert.Equal(ApplicationStatus.Succeeded, retried.Status);
        Assert.Equal(AgentLifecycleState.Stopped, coordinator.State);
        Assert.Contains(AgentShutdownStep.CloseNamedPipes, actions.Calls);
        Assert.Contains(AgentShutdownStep.ExitAgent, actions.Calls);
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

        public Task<ApplicationResult<AgentResponse>> PauseTestAsync(
            PauseAgentTestRequest request,
            CancellationToken cancellationToken) =>
            Succeed(request);

        public Task<ApplicationResult<AgentResponse>> ResumeTestAsync(
            ResumeAgentTestRequest request,
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

        public AgentShutdownStep? StepToIgnoreCancellation { get; init; }

        public List<AgentShutdownStep> Calls { get; } = [];

        public TaskCompletionSource NotificationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueNotification { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueNonCooperativeStep { get; } =
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
            else if (StepToIgnoreCancellation == step)
            {
                await ContinueNonCooperativeStep.Task;
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

    private sealed class FencedTerminalShutdownActions :
        IAgentShutdownActions,
        IAgentShutdownTerminalActions
    {
        public bool HasActiveTest => false;

        public bool ExitCommitted { get; private set; }

        public TaskCompletionSource ExitEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowLateExit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LateExitFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NotifyClientsAsync(ShutdownReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RequestTestCancellationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TerminateExternalToolJobsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RestoreTemporarySystemStateAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task CloseNamedPipesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseMainApplicationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopSupervisedProcessesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveTrayIconAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExitAgentAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseNamedPipesAsync(AgentShutdownAttempt attempt, CancellationToken cancellationToken)
        {
            attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
            return Task.CompletedTask;
        }

        public Task RemoveTrayIconAsync(AgentShutdownAttempt attempt, CancellationToken cancellationToken)
        {
            attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
            return Task.CompletedTask;
        }

        public async Task ExitAgentAsync(AgentShutdownAttempt attempt, CancellationToken cancellationToken)
        {
            ExitEntered.TrySetResult();
            await AllowLateExit.Task;
            try
            {
                attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
                ExitCommitted = true;
            }
            finally
            {
                LateExitFinished.TrySetResult();
            }
        }
    }

    private sealed class ControlledShutdownStepException : Exception;
}
