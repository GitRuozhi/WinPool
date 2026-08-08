using WinPool.Application;

namespace WinPool.Monitoring;

public sealed record SamplingDiagnostics(
    DateTimeOffset? LastSuccessfulSampleUtc,
    int ConsecutiveFailures,
    string? LastFailureCode,
    int WindowSampleCount,
    long AgentDroppedSamples,
    long WindowDroppedSamples = 0,
    long PersistenceDroppedSamples = 0,
    long SubscriberDroppedSamples = 0,
    long RejectedSourceSamples = 0,
    int ActiveSubscribers = 0,
    int SubscriberBufferedSamples = 0,
    int SubscriberCapacity = 0);

public sealed class SamplingDiagnosticsTracker
{
    private DateTimeOffset? lastSuccessfulSampleUtc;
    private int consecutiveFailures;
    private string? lastFailureCode;

    public void RecordSuccess(DateTimeOffset sampledAtUtc)
    {
        lastSuccessfulSampleUtc = sampledAtUtc;
        consecutiveFailures = 0;
        lastFailureCode = null;
    }

    public void RecordFailure(string? code)
    {
        consecutiveFailures = Math.Min(int.MaxValue, consecutiveFailures + 1);
        lastFailureCode = string.IsNullOrWhiteSpace(code)
            ? "monitor.unknown-failure"
            : code.Trim();
    }

    public SamplingDiagnostics Snapshot(
        int windowSampleCount,
        MonitorRuntimeDiagnostics? agentDiagnostics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowSampleCount);
        var diagnostics = agentDiagnostics ?? new MonitorRuntimeDiagnostics(0, 0);
        return new(
            lastSuccessfulSampleUtc,
            consecutiveFailures,
            lastFailureCode,
            windowSampleCount,
            diagnostics.DroppedSamples,
            diagnostics.WindowDroppedSamples,
            diagnostics.PersistenceDroppedSamples,
            diagnostics.SubscriberDroppedSamples,
            diagnostics.RejectedSourceSamples,
            diagnostics.ActiveSubscribers,
            diagnostics.SubscriberBufferedSamples,
            diagnostics.SubscriberCapacity);
    }

    public SamplingDiagnostics Snapshot(
        int windowSampleCount,
        long agentDroppedSamples) =>
        Snapshot(
            windowSampleCount,
            new MonitorRuntimeDiagnostics(agentDroppedSamples, 0));
}
