using System.Collections.Concurrent;
using System.Threading.Channels;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class AppShutdownPreparationObserverTests
{
    [Fact]
    public async Task CancelledAttemptRejectsLateReadyAndNextAttemptCanStillFlush()
    {
        var connection = new EventConnection();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTimeOffset.UtcNow;
        var firstId = Guid.NewGuid();
        using var observer = new AppShutdownPreparationObserver(connection, 1234, at, async (request, token) =>
        {
            if (request.ShutdownAttemptId == firstId)
            { firstEntered.SetResult(); await releaseLate.Task; }
            return true;
        }, _ => ended.TrySetResult());
        observer.Start();
        var instance = ProcessInstanceId.New();
        await connection.Events.Writer.WriteAsync(new PrepareAgentShutdownEvent(firstId, instance, 1234, at,
            ShutdownReason.TrayExit, at));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.Events.Writer.WriteAsync(new AgentShutdownPreparationEndedEvent(firstId, false, at));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseLate.SetResult();
        var secondId = Guid.NewGuid();
        await connection.Events.Writer.WriteAsync(new PrepareAgentShutdownEvent(secondId, instance, 1234, at,
            ShutdownReason.ElevationRestart, at));
        await connection.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acknowledgement = Assert.Single(connection.Requests);
        Assert.Equal(secondId, acknowledgement.ShutdownAttemptId);
        Assert.True(acknowledgement.Prepared);
    }

    [Fact]
    public async Task PrepareForAnotherProcessIncarnationIsIgnored()
    {
        var connection = new EventConnection();
        var at = DateTimeOffset.UtcNow;
        var calls = 0;
        using var observer = new AppShutdownPreparationObserver(connection, 100, at,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(true); }, _ => { });
        observer.Start();
        await connection.Events.Writer.WriteAsync(new PrepareAgentShutdownEvent(Guid.NewGuid(), ProcessInstanceId.New(),
            100, at.AddSeconds(-1), ShutdownReason.TrayExit, at));
        await connection.Events.Writer.WriteAsync(new PrepareAgentShutdownEvent(Guid.NewGuid(), ProcessInstanceId.New(),
            101, at, ShutdownReason.TrayExit, at));
        await connection.Events.Writer.WriteAsync(new PrepareAgentShutdownEvent(Guid.NewGuid(), ProcessInstanceId.New(),
            100, at, ShutdownReason.TrayExit, at));
        await connection.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Single(connection.Requests);
    }

    private sealed class EventConnection : IAgentConnection
    {
        public Channel<AgentEvent> Events { get; } = Channel.CreateUnbounded<AgentEvent>();
        public ConcurrentQueue<AcknowledgeAgentShutdownPreparationRequest> Requests { get; } = new();
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) => Events.Reader.ReadAllAsync(cancellationToken);
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(Assert.IsType<AcknowledgeAgentShutdownPreparationRequest>(request));
            Acknowledged.TrySetResult();
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(new AgentAcknowledgement(), request.CorrelationId));
        }
    }
}
