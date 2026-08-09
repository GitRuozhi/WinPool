using WinPool.Application;

namespace WinPool.TestWorker.Tests;

public sealed class BoundedWorkerEventBufferTests
{
    [Fact]
    public void HigherImportanceEventEvictsOldOutputAndRecordsDrop()
    {
        var runId = TestRunId.New();
        var buffer = new BoundedWorkerEventBuffer(3);
        buffer.TryEnqueue(Create(runId, WorkerEventKind.StandardOutput, WorkerEventImportance.Output));
        buffer.TryEnqueue(Create(runId, WorkerEventKind.StandardOutput, WorkerEventImportance.Output));
        buffer.TryEnqueue(Create(runId, WorkerEventKind.ProcessState, WorkerEventImportance.StateChange));

        var accepted = buffer.TryEnqueue(
            Create(runId, WorkerEventKind.Error, WorkerEventImportance.Error));

        var events = buffer.Drain();
        var statistics = buffer.GetStatistics();
        Assert.True(accepted);
        Assert.Equal(3, events.Count);
        Assert.Contains(events, item => item.Kind is WorkerEventKind.Error);
        Assert.Contains(events, item => item.Kind is WorkerEventKind.ProcessState);
        Assert.Equal(1, statistics.DroppedCount);
        Assert.Equal(1, statistics.DroppedByKind[WorkerEventKind.StandardOutput]);
    }

    [Fact]
    public void LowerImportanceEventCannotEvictProtectedEvents()
    {
        var runId = TestRunId.New();
        var buffer = new BoundedWorkerEventBuffer(2);
        buffer.TryEnqueue(Create(runId, WorkerEventKind.Error, WorkerEventImportance.Error));
        buffer.TryEnqueue(
            Create(runId, WorkerEventKind.FinalMetric, WorkerEventImportance.FinalMetric));

        var accepted = buffer.TryEnqueue(
            Create(runId, WorkerEventKind.StandardOutput, WorkerEventImportance.Output));

        var statistics = buffer.GetStatistics();
        Assert.False(accepted);
        Assert.Equal(1, statistics.DroppedCount);
        Assert.Equal(1, statistics.DroppedByKind[WorkerEventKind.StandardOutput]);
    }

    [Fact]
    public void LatestProtectedStateIsRetainedWhenProtectedBufferIsFull()
    {
        var runId = TestRunId.New();
        var buffer = new BoundedWorkerEventBuffer(2);
        buffer.TryEnqueue(Create(runId, WorkerEventKind.Error, WorkerEventImportance.Error));
        buffer.TryEnqueue(Create(runId, WorkerEventKind.Error, WorkerEventImportance.Error));

        var accepted = buffer.TryEnqueue(
            Create(runId, WorkerEventKind.ProcessState, WorkerEventImportance.StateChange));

        var events = buffer.Drain();
        Assert.True(accepted);
        Assert.Contains(events, item => item.Kind is WorkerEventKind.ProcessState);
        Assert.Equal(1, buffer.GetStatistics().DroppedCount);
    }

    private static WorkerEvent Create(
        TestRunId runId,
        WorkerEventKind kind,
        WorkerEventImportance importance) =>
        new(
            runId,
            "step",
            kind,
            importance,
            DateTimeOffset.UtcNow,
            "test",
            ReadOnlyMemory<byte>.Empty);
}
