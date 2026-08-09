using WinPool.Monitoring;

namespace WinPool.Monitoring.Tests;

public sealed class SamplingDiagnosticsTests
{
    [Fact]
    public void FailuresRemainVisibleUntilASuccessfulSample()
    {
        var tracker = new SamplingDiagnosticsTracker();

        tracker.RecordFailure("pdh.counter-unavailable");
        tracker.RecordFailure("pdh.counter-unavailable");
        var failed = tracker.Snapshot(17, 3);

        Assert.Equal(2, failed.ConsecutiveFailures);
        Assert.Equal("pdh.counter-unavailable", failed.LastFailureCode);
        Assert.Equal(17, failed.WindowSampleCount);
        Assert.Equal(3, failed.AgentDroppedSamples);

        var recoveredAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        tracker.RecordSuccess(recoveredAt);
        var recovered = tracker.Snapshot(18);

        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastFailureCode);
        Assert.Equal(recoveredAt, recovered.LastSuccessfulSampleUtc);
    }

    [Fact]
    public void EmptyFailureCodeIsRedactedToStableDiagnosticCode()
    {
        var tracker = new SamplingDiagnosticsTracker();

        tracker.RecordFailure(" ");

        Assert.Equal(
            "monitor.unknown-failure",
            tracker.Snapshot(0).LastFailureCode);
    }

    [Fact]
    public void AgentDropSourcesAndQueuePressureRemainSeparated()
    {
        var tracker = new SamplingDiagnosticsTracker();
        var snapshot = tracker.Snapshot(
            7,
            new WinPool.Application.MonitorRuntimeDiagnostics(
                DroppedSamples: 10,
                BufferedSamples: 4,
                WindowDroppedSamples: 1,
                PersistenceDroppedSamples: 2,
                SubscriberDroppedSamples: 3,
                RejectedSourceSamples: 4,
                ActiveSubscribers: 2,
                SubscriberBufferedSamples: 5,
                SubscriberCapacity: 8));

        Assert.Equal(10, snapshot.AgentDroppedSamples);
        Assert.Equal(1, snapshot.WindowDroppedSamples);
        Assert.Equal(2, snapshot.PersistenceDroppedSamples);
        Assert.Equal(3, snapshot.SubscriberDroppedSamples);
        Assert.Equal(4, snapshot.RejectedSourceSamples);
        Assert.Equal(2, snapshot.ActiveSubscribers);
        Assert.Equal(5, snapshot.SubscriberBufferedSamples);
        Assert.Equal(8, snapshot.SubscriberCapacity);
    }
}
