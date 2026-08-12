using System.Threading.Channels;
using WinPool.Application;

namespace WinPool.Agent.Client;

/// <summary>
/// Fans each transport event out to every active watcher. A watcher that cannot
/// keep up is explicitly completed so the transport reader can recover from an
/// observable event gap without blocking every other watcher.
/// </summary>
internal sealed class AgentClientEventFanout : IDisposable
{
    private const int DefaultCapacity = 1_024;
    private readonly int capacity;
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, Channel<AgentEvent>> subscribers = [];
    private AgentSnapshot? latestSnapshot;
    private bool disposed;

    public AgentClientEventFanout(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public AgentClientEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentEvent>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (latestSnapshot is not null
                && !channel.Writer.TryWrite(new AgentStateReseedEvent(
                    latestSnapshot,
                    "agent.events.latest_snapshot",
                    DateTimeOffset.UtcNow)))
            {
                throw new InvalidOperationException(
                    "The initial Agent snapshot could not be queued for a watcher.");
            }

            subscribers.Add(id, channel);
        }

        return new AgentClientEventSubscription(id, channel.Reader, Remove);
    }

    public void CacheSnapshot(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (syncRoot)
        {
            latestSnapshot = snapshot;
        }
    }

    public AgentClientEventFanoutPublishResult PublishReseed(
        AgentSnapshot snapshot,
        string reason)
    {
        CacheSnapshot(snapshot);
        return Publish(new AgentStateReseedEvent(snapshot, reason, DateTimeOffset.UtcNow));
    }

    public AgentClientEventFanoutPublishResult Publish(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        KeyValuePair<Guid, Channel<AgentEvent>>[] targets;
        lock (syncRoot)
        {
            if (disposed)
            {
                return AgentClientEventFanoutPublishResult.None;
            }

            targets = subscribers.ToArray();
        }

        var delivered = 0;
        var overflowed = 0;
        foreach (var target in targets)
        {
            if (target.Value.Writer.TryWrite(agentEvent))
            {
                delivered++;
            }
            else
            {
                overflowed++;
                Remove(target.Key, target.Value);
            }
        }

        return new(delivered, overflowed);
    }

    public void Dispose()
    {
        KeyValuePair<Guid, Channel<AgentEvent>>[] targets;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            targets = subscribers.ToArray();
            subscribers.Clear();
        }

        foreach (var target in targets)
        {
            target.Value.Writer.TryComplete();
        }
    }

    private void Remove(Guid id) => Remove(id, null);

    private void Remove(Guid id, Channel<AgentEvent>? expected)
    {
        Channel<AgentEvent>? channel = null;
        lock (syncRoot)
        {
            if (subscribers.TryGetValue(id, out var current)
                && (expected is null || ReferenceEquals(current, expected)))
            {
                subscribers.Remove(id);
                channel = current;
            }
        }

        channel?.Writer.TryComplete();
    }
}

internal sealed record AgentClientEventFanoutPublishResult(
    int DeliveredSubscriberCount,
    int OverflowedSubscriberCount)
{
    public static AgentClientEventFanoutPublishResult None { get; } = new(0, 0);

    public bool HasEventGap => OverflowedSubscriberCount > 0;
}

internal sealed class AgentClientEventSubscription(
    Guid id,
    ChannelReader<AgentEvent> reader,
    Action<Guid> remove) : IDisposable
{
    private int disposed;

    public ChannelReader<AgentEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            remove(id);
        }
    }
}
