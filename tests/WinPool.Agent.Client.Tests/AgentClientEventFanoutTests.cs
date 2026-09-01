using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent.Client.Tests;

public sealed class AgentClientEventFanoutTests
{
    [Fact]
    public async Task SlowWatcherIsCompletedWithoutBlockingHealthyWatcher()
    {
        using var fanout = new AgentClientEventFanout(capacity: 1);
        using var slow = fanout.Subscribe();
        using var healthy = fanout.Subscribe();
        var first = new AgentShutdownEvent(ShutdownReason.TrayExit, DateTimeOffset.UtcNow);
        var second = new AgentShutdownEvent(ShutdownReason.TrayExit, DateTimeOffset.UtcNow);

        Assert.False(fanout.Publish(first).HasEventGap);
        Assert.True(await healthy.Reader.WaitToReadAsync());
        Assert.True(healthy.Reader.TryRead(out var healthyFirst));
        Assert.Same(first, healthyFirst);

        var result = fanout.Publish(second);

        Assert.True(result.HasEventGap);
        Assert.Equal(1, result.OverflowedSubscriberCount);
        Assert.True(await healthy.Reader.WaitToReadAsync());
        Assert.True(healthy.Reader.TryRead(out var healthySecond));
        Assert.Same(second, healthySecond);
        Assert.True(slow.Reader.TryRead(out var slowFirst));
        Assert.Same(first, slowFirst);
        Assert.False(await slow.Reader.WaitToReadAsync());
    }

    [Fact]
    public async Task NewWatcherReceivesTheLatestSnapshotAsAReseedBoundary()
    {
        using var fanout = new AgentClientEventFanout(capacity: 1);
        var snapshot = new AgentSnapshot(
            new AgentInstanceId(Guid.NewGuid()),
            true,
            null,
            new AgentShutdownStatus(
                AgentLifecycleState.Running,
                null,
                [],
                [],
                false),
            []);
        fanout.CacheSnapshot(snapshot);

        using var watcher = fanout.Subscribe();

        Assert.True(await watcher.Reader.WaitToReadAsync());
        var reseed = Assert.IsType<AgentStateReseedEvent>(
            await watcher.Reader.ReadAsync());
        Assert.Same(snapshot, reseed.Snapshot);
    }

    [Fact]
    public void WatcherDisposedBetweenPublishSnapshotAndWriteIsNotAnOverflow()
    {
        AgentClientEventSubscription? subscription = null;
        var disposeOnce = 0;
        using var fanout = new AgentClientEventFanout(
            capacity: 1,
            beforeTargetWrite: () =>
            {
                if (Interlocked.Exchange(ref disposeOnce, 1) == 0)
                {
                    subscription!.Dispose();
                }
            });
        subscription = fanout.Subscribe();

        var result = fanout.Publish(
            new AgentShutdownEvent(ShutdownReason.TrayExit, DateTimeOffset.UtcNow));

        Assert.Equal(0, result.DeliveredSubscriberCount);
        Assert.Equal(0, result.OverflowedSubscriberCount);
        Assert.False(result.HasEventGap);
        subscription.Dispose();
    }
}
