using WinPool.Application;

namespace WinPool.Monitoring;

/// <summary>
/// ALG-MON-003: a bounded latest-value window. When full, the oldest sample is
/// discarded so a reconnected UI receives current state rather than stale data.
/// </summary>
public sealed class LatestMonitorWindow
{
    private readonly object gate = new();
    private readonly Queue<MonitorSample> samples;
    private long droppedSamples;

    public LatestMonitorWindow(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        samples = new Queue<MonitorSample>(capacity);
    }

    public int Capacity { get; }

    public long DroppedSamples
    {
        get
        {
            lock (gate)
            {
                return droppedSamples;
            }
        }
    }

    public void Add(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (gate)
        {
            if (samples.Count == Capacity)
            {
                samples.Dequeue();
                droppedSamples++;
            }

            samples.Enqueue(sample);
        }
    }

    public IReadOnlyList<MonitorSample> Snapshot()
    {
        lock (gate)
        {
            return samples.ToArray();
        }
    }
}
