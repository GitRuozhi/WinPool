using WinPool.Application;

namespace WinPool.TestWorker;

/// <summary>
/// A disconnect buffer that preferentially retains errors, state changes, and
/// final metrics. The buffer is thread-safe because stdout and stderr are read
/// concurrently.
/// </summary>
public sealed class BoundedWorkerEventBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<WorkerEvent> _events = [];
    private readonly Dictionary<WorkerEventKind, long> _droppedByKind = [];
    private readonly object _gate = new();
    private long _acceptedCount;
    private long _droppedCount;

    public BoundedWorkerEventBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public bool TryEnqueue(WorkerEvent workerEvent)
    {
        ArgumentNullException.ThrowIfNull(workerEvent);

        lock (_gate)
        {
            if (_events.Count < _capacity)
            {
                _events.AddLast(workerEvent);
                _acceptedCount++;
                return true;
            }

            var eviction = FindOldestLowerImportance(workerEvent.Importance);
            if (eviction is null)
            {
                RecordDrop(workerEvent.Kind);
                return false;
            }

            var evicted = eviction.Value;
            _events.Remove(eviction);
            RecordDrop(evicted.Kind);
            _events.AddLast(workerEvent);
            _acceptedCount++;
            return true;
        }
    }

    public IReadOnlyList<WorkerEvent> Drain()
    {
        lock (_gate)
        {
            var result = _events.ToArray();
            _events.Clear();
            return result;
        }
    }

    public WorkerEventBufferStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new WorkerEventBufferStatistics(
                _capacity,
                _events.Count,
                _acceptedCount,
                _droppedCount,
                new Dictionary<WorkerEventKind, long>(_droppedByKind));
        }
    }

    private LinkedListNode<WorkerEvent>? FindOldestLowerImportance(
        WorkerEventImportance incomingImportance)
    {
        var node = _events.First;
        while (node is not null)
        {
            if (node.Value.Importance < incomingImportance)
            {
                return node;
            }

            node = node.Next;
        }

        // Errors, state changes, and final metrics are all protected classes.
        // When every slot is already protected, retain the newest protected
        // event so a reconnect sees the latest terminal state.
        if (incomingImportance >= WorkerEventImportance.FinalMetric)
        {
            return _events.First;
        }

        return null;
    }

    private void RecordDrop(WorkerEventKind kind)
    {
        _droppedCount++;
        _droppedByKind.TryGetValue(kind, out var count);
        _droppedByKind[kind] = count + 1;
    }
}
