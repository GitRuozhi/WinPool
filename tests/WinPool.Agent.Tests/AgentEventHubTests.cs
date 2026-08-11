using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentEventHubTests
{
    [Fact]
    public async Task FullSubscriberIsCompletedInsteadOfDroppingOldestEvent()
    {
        var hub = new AgentEventHub();
        using var subscription = hub.Subscribe(capacity: 1);
        var first = new AgentShutdownEvent(
            ShutdownReason.TrayExit,
            DateTimeOffset.UtcNow);
        var second = new AgentShutdownEvent(
            ShutdownReason.Update,
            DateTimeOffset.UtcNow.AddSeconds(1));

        hub.Publish(first);
        hub.Publish(second);

        Assert.True(subscription.Reader.TryRead(out var received));
        Assert.Equal(first, received);
        Assert.False(await subscription.Reader.WaitToReadAsync());
    }
}
