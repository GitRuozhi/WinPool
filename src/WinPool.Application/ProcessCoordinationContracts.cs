using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

[Flags]
public enum AgentCapability
{
    None = 0,
    Monitoring = 1 << 0,
    Inventory = 1 << 2,
    Tray = 1 << 5,
    Persistence = 1 << 6
}

public readonly record struct AgentInstanceId(Guid Value);

public sealed record AgentHandshake(
    int ProtocolVersion,
    AgentInstanceId AgentInstanceId,
    int ProcessId,
    AgentCapability Capabilities,
    DateTimeOffset StartedAtUtc);

public abstract record AgentRequest(CorrelationId CorrelationId);

public sealed record GetAgentSnapshotRequest(CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record OpenMainWindowRequest(
    WorkspacePage? Destination,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record OpenAgentNativePropertiesRequest(
    StorageObjectId Target,
    int DiskNumber,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record StartAgentMonitoringRequest(
    MonitorRequest MonitorRequest,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record StopAgentMonitoringRequest(
    SessionId SessionId,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record LoadAgentWorkspaceStateRequest(CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record SaveAgentWorkspaceStateRequest(
    WorkspaceSessionState State,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record SimulationDocumentPayload(
    string DocumentId,
    int DocumentSchemaVersion,
    string DisplayName,
    string SanitizedJson,
    string Sha256,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalInventoryDocumentPayload(
    string DocumentId,
    int DocumentSchemaVersion,
    string DisplayName,
    string SanitizedJson,
    string Sha256,
    DateTimeOffset CapturedAtUtc);

public sealed record ListAgentSimulationDocumentsRequest(CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record SaveAgentSimulationDocumentRequest(
    SimulationDocumentPayload Document,
    string? ExpectedPreviousSha256,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record DeleteAgentSimulationDocumentRequest(
    string DocumentId,
    string ExpectedSha256,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CommitAgentSimulationEditRequest(
    SimulationDocumentPayload Document,
    string ExpectedPreviousSha256,
    OperationPlan Plan,
    IReadOnlyList<ExecutionEvent> Events,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CaptureAgentInventoryRequest(
    bool IncludeLegacyComparison,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CaptureAgentManageInventoryRequest(
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record LoadAgentManageInventoryRequest(CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record ExportAgentMonitorCsvRequest(
    SessionId SessionId,
    string DestinationPath,
    bool UserConfirmedOverwrite,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record RequestAgentShutdownRequest(
    ShutdownReason Reason,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public abstract record AgentResponse;

public sealed record AgentAcknowledgement : AgentResponse;

public sealed record AgentSnapshotResponse(AgentSnapshot Snapshot) : AgentResponse;

public sealed record MonitoringSessionResponse(MonitoringSession Session) : AgentResponse;

public sealed record WorkspaceStateLoadedResponse(WorkspaceSessionState? State)
    : AgentResponse;

public sealed record WorkspaceStateSavedResponse(WorkspaceSessionState State)
    : AgentResponse;

public sealed record SimulationDocumentListResponse(
    IReadOnlyList<SimulationDocumentPayload> Documents)
    : AgentResponse;

public sealed record SimulationDocumentSavedResponse(
    SimulationDocumentPayload Document)
    : AgentResponse;

public sealed record SimulationDocumentDeletedResponse(
    string DocumentId,
    bool Deleted)
    : AgentResponse;

public sealed record InventoryCaptureResponse(
    Guid NativeSnapshotId,
    InventorySnapshot NativeSnapshot,
    Guid? LegacySnapshotId,
    InventorySnapshot? LegacySnapshot,
    Guid? ComparisonId,
    InventoryComparison? Comparison)
    : AgentResponse;

public sealed record ManageInventoryCaptureResponse(
    Guid SnapshotId,
    LocalInventoryDocumentPayload Document)
    : AgentResponse;

public sealed record ManageInventoryLoadedResponse(
    Guid? SnapshotId,
    LocalInventoryDocumentPayload? Document)
    : AgentResponse;

public sealed record ExportArtifactResponse(
    string DestinationPath,
    string Sha256,
    long RowCount)
    : AgentResponse;

public sealed record ShutdownResponse(ShutdownResult Result) : AgentResponse;

public enum AgentLifecycleState
{
    Starting,
    Recovering,
    Running,
    Failed,
    ShuttingDown,
    ShutdownPending,
    Stopped
}

public sealed record AgentShutdownStatus(
    AgentLifecycleState State,
    DateTimeOffset? AttemptedAtUtc,
    IReadOnlyList<string> FailedStepCodes,
    IReadOnlyList<int> RemainingProcessIds,
    bool CanRetry);

public sealed record AgentSnapshot(
    AgentInstanceId AgentInstanceId,
    bool IsTrayVisible,
    MonitoringSession? ActiveMonitoringSession,
    AgentShutdownStatus ShutdownStatus,
    IReadOnlyList<ProcessRegistration> Processes,
    IReadOnlyList<MonitorSample>? LatestMonitorSamples = null,
    IReadOnlyList<StorageHealthEvent>? RecentStorageHealthEvents = null,
    MonitorRuntimeDiagnostics? MonitorDiagnostics = null);

public abstract record AgentEvent(DateTimeOffset OccurredAtUtc);

public sealed record AgentTaskEvent(ApplicationTaskEvent TaskEvent)
    : AgentEvent(TaskEvent.OccurredAtUtc);

public sealed record AgentMonitorSampleEvent(MonitorSample Sample)
    : AgentEvent(Sample.SampledAtUtc);

public sealed record AgentProcessStateEvent(
    ProcessRegistration Registration,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

public sealed record AgentShutdownEvent(
    ShutdownReason Reason,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

/// <summary>
/// A complete replacement boundary after initial connection or an event gap.
/// Consumers must discard previously projected Agent state before applying it.
/// </summary>
public sealed record AgentStateReseedEvent(
    AgentSnapshot Snapshot,
    string Reason,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

public enum AgentEventTransportState
{
    Disconnected,
    Reconnecting,
    Reconnected
}

public sealed record AgentEventTransportStateEvent(
    AgentEventTransportState State,
    bool HasEventGap,
    string DiagnosticCode,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

public interface IAgentConnection
{
    Task<ApplicationResult<AgentHandshake>> ConnectAsync(
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentEvent> WatchAsync(
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> SendAsync(
        AgentRequest request,
        CancellationToken cancellationToken);
}

public enum WorkerKind
{
    Inventory,
    MainApplication
}

public enum SupervisedProcessState
{
    Starting,
    Running,
    Stopping,
    Exited,
    Unresponsive,
    Failed
}

public readonly record struct ProcessInstanceId(Guid Value)
{
    public static ProcessInstanceId New() => new(Guid.NewGuid());
}

public sealed record ProcessRegistration(
    ProcessInstanceId ProcessInstanceId,
    int ProcessId,
    WorkerKind Kind,
    CorrelationId CorrelationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastHeartbeatUtc,
    SupervisedProcessState State,
    bool OwnsJobObject,
    DateTimeOffset? ShutdownDeadlineUtc);

public enum ShutdownReason
{
    TrayExit,
    AgentFailure,
    OperatingSystemShutdown,
    Update,
    DevelopmentRestart,
    StorageLocationSwitch
}

public sealed record ShutdownResult(
    bool Completed,
    IReadOnlyList<int> RemainingProcessIds,
    int FlushedEventCount,
    bool TemporarySystemStateRestored);
