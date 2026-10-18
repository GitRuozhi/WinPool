using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using WinPool.Agent;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Ipc;

namespace WinPool.Agent.Client.Tests;

public sealed partial class NamedPipeAgentConnectionTests
{
    [Fact]
    public async Task TwoBlockedHandlersDoNotBlockLightControlOrCancelAnotherPendingRequest()
    {
        await using var harness = await MultiplexHarness.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstCancellation = new CancellationTokenSource();
        var first = harness.Connection.SendAsync(new LoadAgentWorkspaceStateRequest(CorrelationId.New()), firstCancellation.Token);
        var second = harness.Connection.SendAsync(new LoadAgentWorkspaceStateRequest(CorrelationId.New()), timeout.Token);
        await harness.Operations.TwoStarted.Task.WaitAsync(timeout.Token);
        var started = Stopwatch.GetTimestamp();
        Assert.True((await harness.Connection.SendAsync(new GetAgentSnapshotRequest(CorrelationId.New()), timeout.Token)
            .WaitAsync(TimeSpan.FromMilliseconds(250))).IsSuccess);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(250));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        firstCancellation.Cancel();
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await first.WaitAsync(timeout.Token)).Status);
        Assert.False(second.IsCompleted);
        harness.Operations.Release.TrySetResult();
        Assert.True((await second.WaitAsync(timeout.Token)).IsSuccess);
        Assert.True((await harness.Connection.SendAsync(new GetAgentSnapshotRequest(CorrelationId.New()), timeout.Token)).IsSuccess);
        Assert.Equal(1, harness.PersistedProcessCount); // one registration, no heartbeat write per read
    }

    [Fact]
    public async Task OrdinaryAdmissionIsBoundedWhileLightControlHasItsOwnQuota()
    {
        await using var harness = await MultiplexHarness.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rejected = Channel.CreateUnbounded<ApplicationResult<AgentResponse>>();
        var calls = Enumerable.Range(0, 20).Select(_ => harness.Connection.SendAsync(
            new LoadAgentWorkspaceStateRequest(CorrelationId.New()), timeout.Token)).ToArray();
        foreach (var call in calls)
            _ = call.ContinueWith(task =>
            {
                if (!task.Result.IsSuccess) rejected.Writer.TryWrite(task.Result);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        await harness.Operations.TwoStarted.Task.WaitAsync(timeout.Token);
        for (var count = 0; count < 2; count++)
            Assert.Contains((await rejected.Reader.ReadAsync(timeout.Token)).Messages, message => message.Code == "agent.request.busy");
        Assert.True((await harness.Connection.SendAsync(new GetAgentSnapshotRequest(CorrelationId.New()), timeout.Token)
            .WaitAsync(TimeSpan.FromMilliseconds(250))).IsSuccess);
        Assert.Equal(2, harness.Operations.Started);
        harness.Operations.Release.TrySetResult();
        var results = await Task.WhenAll(calls).WaitAsync(timeout.Token);
        Assert.Equal(18, results.Count(result => result.IsSuccess));
        Assert.Equal(2, results.Count(result => !result.IsSuccess));
    }

    private sealed class BlockedOperations(Guid sessionId) : SnapshotOperations(sessionId)
    {
        private int started;
        public int Started => Volatile.Read(ref started);
        public TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
            LoadAgentWorkspaceStateRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2) TwoStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.LoadWorkspaceStateAsync(request, cancellationToken);
        }
    }

    private sealed class MultiplexHarness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinPool.Multiplex.Tests", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource stop = new();
        private Task running = Task.CompletedTask;
        private int persisted;
        public int PersistedProcessCount => Volatile.Read(ref persisted);
        public BlockedOperations Operations { get; private set; } = null!;
        public NamedPipeAgentConnection Connection { get; private set; } = null!;

        public static async Task<MultiplexHarness> CreateAsync()
        {
            var harness = new MultiplexHarness();
            Directory.CreateDirectory(harness.directory);
            var endpointPath = Path.Combine(harness.directory, "agent-endpoint.json");
            var sid = IpcIdentity.HashUserSid(WindowsIdentity.GetCurrent().User!.Value);
            var session = Guid.NewGuid();
            var nonce = Guid.NewGuid();
            var pipe = IpcIdentity.CreateAgentControlPipeName(sid, nonce);
            harness.Operations = new(session);
            var registry = new AgentProcessRegistry();
            var coordinator = new AgentSessionCoordinator(harness.Operations,
                new AgentShutdownWorkflow(new NoOpShutdownActions(), registry), registry);
            var server = new CurrentUserAgentControlServer(pipe, nonce, sid, session, Environment.ProcessId, coordinator,
                persistProcess: (_, _) => { Interlocked.Increment(ref harness.persisted); return Task.CompletedTask; });
            harness.running = server.RunAsync(harness.stop.Token);
            await File.WriteAllTextAsync(endpointPath, JsonSerializer.Serialize(new AgentEndpoint(IpcProtocol.CurrentVersion,
                pipe, nonce, session, Environment.ProcessId, DateTimeOffset.UtcNow)));
            harness.Connection = new(endpointPath, new RecordingLauncher(), null, null, new TrueAgentProcessLiveness());
            Assert.True((await harness.Connection.ConnectAsync(CancellationToken.None)).IsSuccess);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            Operations.Release.TrySetResult();
            await Connection.DisposeAsync();
            stop.Cancel();
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            stop.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
