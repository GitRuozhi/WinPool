using WinPool.Domain;

namespace WinPool.Application;

public enum MonitorMetricKind
{
    ActiveTimePercent,
    ReadBytesPerSecond,
    WriteBytesPerSecond,
    ReadOperationsPerSecond,
    WriteOperationsPerSecond,
    AverageQueueLength,
    AverageLatencyMilliseconds,
    CpuPercent,
    VirtualDiskActiveBytes,
    VirtualDiskMissingBytes,
    VirtualDiskStaleBytes,
    VirtualDiskNeedRegenerationBytes,
    VirtualDiskRegeneratingBytes,
    VirtualDiskPendingDeletionBytes
}

public sealed record MonitorTarget(
    StorageObjectId ObjectId,
    string CounterIdentity);

public sealed record MonitorRequest(
    SessionId SessionId,
    SystemId SystemId,
    IReadOnlyList<MonitorTarget> Targets,
    IReadOnlyList<MonitorMetricKind> Metrics,
    TimeSpan SamplingInterval,
    bool ContinueWhenUiCloses);

public sealed record MonitorMetricValue(
    MonitorMetricKind Kind,
    double Value);

public sealed record MonitorSample(
    SessionId SessionId,
    StorageObjectId TargetId,
    DateTimeOffset SampledAtUtc,
    IReadOnlyList<MonitorMetricValue> Values,
    bool MayBeAffectedByActiveTest);

public enum MonitoringSessionState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Interrupted,
    Failed
}

public sealed record MonitoringSession(
    SessionId SessionId,
    MonitorRequest Request,
    MonitoringSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record MonitorRuntimeDiagnostics(
    long DroppedSamples,
    int BufferedSamples,
    long WindowDroppedSamples = 0,
    long PersistenceDroppedSamples = 0,
    long SubscriberDroppedSamples = 0,
    long RejectedSourceSamples = 0,
    int ActiveSubscribers = 0,
    int SubscriberBufferedSamples = 0,
    int SubscriberCapacity = 0);

public interface IMonitorSource
{
    IAsyncEnumerable<MonitorSample> SampleAsync(
        MonitorRequest request,
        CancellationToken cancellationToken);
}

public interface IMonitoringCoordinator
{
    Task<ApplicationResult<MonitoringSession>> StartAsync(
        MonitorRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<MonitoringSession>> StopAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<MonitorSample> WatchAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);
}

public interface IMonitoringQuery
{
    Task<ApplicationResult<MonitoringSession>> GetSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);

    Task<ApplicationResult<IReadOnlyList<MonitorSample>>> ReadSamplesAsync(
        SessionId sessionId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}

public enum StorageHealthEventSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public sealed record StorageHealthEvent(
    string Channel,
    string Provider,
    long? RecordId,
    int EventId,
    StorageHealthEventSeverity Severity,
    DateTimeOffset OccurredAtUtc,
    string Message);

public interface IStorageHealthEventSource
{
    IAsyncEnumerable<StorageHealthEvent> WatchAsync(
        CancellationToken cancellationToken);
}
