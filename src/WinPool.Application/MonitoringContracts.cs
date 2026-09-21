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
    IReadOnlyList<MonitorMetricValue> Values);

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
    int SubscriberCapacity = 0,
    long SessionElapsedMilliseconds = 0,
    long ConfirmedLostSamples = 0,
    int PendingPersistenceSamples = 0,
    long OldestPendingPersistenceMilliseconds = 0,
    bool PersistenceDelayed = false,
    bool PersistencePaused = false,
    bool RotationInProgress = false,
    int RotationBufferedSamples = 0,
    string? PersistenceFailure = null,
    int PendingArchives = 0,
    int FailedArchives = 0,
    string? ArchiveFailure = null,
    string? SamplingFailure = null,
    bool ArchiveRawDatabaseRetained = false,
    string? SessionOccurrenceId = null,
    string? PersistenceFailureOccurrenceId = null,
    string? ArchiveFailureOccurrenceId = null,
    bool HasRecoveredInterruptedSession = false,
    string? RecoveredInterruptedSessionOccurrenceId = null);

/// <summary>
/// Persistence-specific facts supplied by an optional monitoring writer.
/// These describe only known state: a writer may not invent an exact count for
/// samples that were interrupted before they reached its bounded ingress.
/// </summary>
public sealed record MonitorPersistenceDiagnostics(
    long ConfirmedLostSamples = 0,
    int PendingSamples = 0,
    long OldestPendingMilliseconds = 0,
    bool IsDelayed = false,
    bool IsPaused = false,
    bool RotationInProgress = false,
    int RotationBufferedSamples = 0,
    string? Failure = null,
    int PendingArchives = 0,
    int FailedArchives = 0,
    string? ArchiveFailure = null,
    bool ArchiveRawDatabaseRetained = false,
    string? FailureOccurrenceId = null,
    string? ArchiveFailureOccurrenceId = null,
    bool HasRecoveredInterruptedSession = false,
    string? RecoveredInterruptedSessionOccurrenceId = null);

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
