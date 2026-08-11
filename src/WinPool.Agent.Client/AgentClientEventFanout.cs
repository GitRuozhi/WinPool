using System.Threading.Channels;
using WinPool.Application;

namespace WinPool.Agent.Client;

/// <summary>
/// Fans each transport event out to every active watcher. A slow watcher applies
/// backpressure to the event reader instead of silently discarding history.
/// </summary>
internal sealed class AgentClientEventFanout : IDisposable
{
    private const int DefaultCapacity = 1_024;
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, Channel<AgentEvent>> subscribers = [];
    private AgentSnapshot? latestSnapshot;
    private bool disposed;

    public AgentClientEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentEvent>(
            new BoundedChannelOptions(DefaultCapacity)
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

    public async Task PublishReseedAsync(
        AgentSnapshot snapshot,
        string reason,
        CancellationToken cancellationToken)
    {
        CacheSnapshot(snapshot);
        await PublishAsync(
                new AgentStateReseedEvent(snapshot, reason, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        KeyValuePair<Guid, Channel<AgentEvent>>[] targets;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            targets = subscribers.ToArray();
        }

        foreach (var target in targets)
        {
            try
            {
                await target.Value.Writer.WriteAsync(agentEvent, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                Remove(target.Key, target.Value);
            }
        }
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
