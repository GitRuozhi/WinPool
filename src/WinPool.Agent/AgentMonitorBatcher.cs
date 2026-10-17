using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent;

/// <summary>Display transport runs independently of the persistence queue.</summary>
internal static class AgentMonitorBatcher
{
    internal const int MaximumSamples = 128;
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(250);

    public static async Task ForwardAsync(AgentInstanceId instanceId, SessionId sessionId,
        IAsyncEnumerable<MonitorSample> source, Action<AgentMonitorBatchEvent> publish,
        CancellationToken cancellationToken)
    {
        await using var samples = source.GetAsyncEnumerator(cancellationToken);
        var next = samples.MoveNextAsync().AsTask();
        long previous = 0;
        while (await next.ConfigureAwait(false))
        {
            var batch = new List<MonitorSample>(MaximumSamples) { samples.Current };
            var flush = Task.Delay(MaximumDelay, cancellationToken);
            next = samples.MoveNextAsync().AsTask();
            while (batch.Count < MaximumSamples
                && !flush.IsCompleted
                && await Task.WhenAny(next, flush).ConfigureAwait(false) == next)
            {
                if (!await next.ConfigureAwait(false)) break;
                batch.Add(samples.Current);
                next = samples.MoveNextAsync().AsTask();
            }
            var gap = batch[0].Sequence != previous + 1;
            for (var i = 1; i < batch.Count; i++)
                gap |= batch[i].Sequence != batch[i - 1].Sequence + 1;
            publish(new(instanceId, sessionId, batch[0].Sequence, batch[^1].Sequence,
                batch.ToArray(), gap, DateTimeOffset.UtcNow));
            previous = batch[^1].Sequence;
        }
    }
}
