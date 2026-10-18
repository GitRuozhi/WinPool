using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentShutdownPreparationCoordinatorTests
{
    [Fact]
    public async Task OnlyTheRegisteredAppIncarnationCanAcknowledgeTheCurrentAttempt()
    {
        var registry = new AgentProcessRegistry();
        var app = RegisterApp(registry);
        var ready = new TaskCompletionSource<PrepareAgentShutdownEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AgentShutdownPreparationCoordinator(registry, new AppVerifier(app), @"D:\WinPool\WinPool.App.exe",
            report => { if (report is PrepareAgentShutdownEvent request) ready.TrySetResult(request); });
        var attempt = Guid.NewGuid();
        var preparation = coordinator.PrepareClientsAsync(attempt, ShutdownReason.TrayExit, CancellationToken.None);
        var request = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(app.ProcessInstanceId, request.AppInstanceId);
        Assert.Equal(app.StartedAtUtc, request.AppStartedAtUtc);
        var acknowledgement = new AcknowledgeAgentShutdownPreparationRequest(attempt, app.ProcessInstanceId, true, CorrelationId.New());
        Assert.False(coordinator.AcknowledgePreparation(acknowledgement, null));
        Assert.False(coordinator.AcknowledgePreparation(acknowledgement with { ShutdownAttemptId = Guid.NewGuid() }, Peer(app)));
        Assert.False(coordinator.AcknowledgePreparation(acknowledgement, Peer(app) with { StartedAtUtc = app.StartedAtUtc.AddSeconds(1) }));
        Assert.False(preparation.IsCompleted);
        Assert.True(coordinator.AcknowledgePreparation(acknowledgement, Peer(app)));
        Assert.True(await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.AcknowledgePreparation(acknowledgement, Peer(app)));
    }

    [Fact]
    public async Task CancelledAttemptCannotAcceptLateReadyAndDoesNotConsumeTheNextAttempt()
    {
        var registry = new AgentProcessRegistry();
        var app = RegisterApp(registry);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AgentShutdownPreparationCoordinator(registry, new AppVerifier(app), @"D:\WinPool\WinPool.App.exe",
            report => { if (report is PrepareAgentShutdownEvent) ready.TrySetResult(); });
        using var cancellation = new CancellationTokenSource();
        var attempt = Guid.NewGuid();
        var preparation = coordinator.PrepareClientsAsync(attempt, ShutdownReason.ElevationRestart, cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        coordinator.EndPreparation(attempt, false);
        Assert.False(coordinator.AcknowledgePreparation(new(attempt, app.ProcessInstanceId, true, CorrelationId.New()), Peer(app)));
        var replacement = Guid.NewGuid();
        var next = coordinator.PrepareClientsAsync(replacement, ShutdownReason.TrayExit, CancellationToken.None);
        Assert.True(coordinator.AcknowledgePreparation(new(replacement, app.ProcessInstanceId, true, CorrelationId.New()), Peer(app)));
        Assert.True(await next);
    }

    private static AgentManagedProcess RegisterApp(AgentProcessRegistry registry)
    {
        var at = DateTimeOffset.UnixEpoch;
        var app = new AgentManagedProcess(ProcessInstanceId.New(), 1234, AgentManagedProcessKind.MainApplication,
            CorrelationId.New(), at, at, SupervisedProcessState.Running, false, null);
        Assert.True(registry.TryRegister(app));
        return app;
    }
    private static AgentVerifiedPeer Peer(AgentManagedProcess app) => new(app.ProcessId, app.StartedAtUtc,
        @"D:\WinPool\WinPool.App.exe", app.ProcessInstanceId, Guid.NewGuid());
    private sealed class AppVerifier(AgentManagedProcess app) : IProcessIncarnationVerifier
    {
        public ProcessIncarnation? TryRead(int processId) => processId == app.ProcessId
            ? new(processId, @"D:\WinPool\WinPool.App.exe", app.StartedAtUtc) : null;
        public bool IsExpectedExecutable(int processId, string expectedExecutablePath) => processId == app.ProcessId;
        public bool Matches(AgentManagedProcess registration, string expectedExecutablePath) => registration == app;
    }
}
