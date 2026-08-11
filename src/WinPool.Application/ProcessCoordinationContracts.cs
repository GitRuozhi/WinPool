using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

[Flags]
public enum AgentCapability
{
    None = 0,
    Monitoring = 1 << 0,
    Testing = 1 << 1,
    Inventory = 1 << 2,
    ToolManagement = 1 << 3,
    ElevatedBroker = 1 << 4,
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

public sealed record GetDevelopmentDiagnosticsRequest(
    int RecentRunLimit,
    CorrelationId CorrelationId)
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

public sealed record StartAgentTestRequest(
    TestDefinition Definition,
    TestPlan Plan,
    bool UserConfirmedWrite,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CancelAgentTestRequest(
    TestRunId RunId,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record GetAgentTestResultRequest(
    TestRunId RunId,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public enum TestRunHistoryFilter
{
    All,
    Completed,
    Failed,
    Cancelled,
    Active
}

public sealed record ListAgentTestRunsRequest(
    TestRunHistoryFilter Filter,
    int Limit,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record ListUserTestPresetsRequest(CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record SaveUserTestPresetRequest(
    UserTestPreset Preset,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record DeleteUserTestPresetRequest(
    Guid PresetId,
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

public sealed record PersistDiteLegacyImportRequest(
    string SourcePath,
    string ExpectedSha256,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record ListDiteLegacyImportsRequest(
    int Limit,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record GetDiteLegacyImportSummaryRequest(
    Guid ImportId,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public enum TestExportFormat
{
    Csv,
    Json,
    Markdown,
    EvidencePackage
}

public sealed record ExportAgentTestRunRequest(
    TestRunId RunId,
    TestExportFormat Format,
    string DestinationPath,
    bool UserConfirmedOverwrite,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record DetectAgentToolRequest(
    ToolId ToolId,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record ConfigureAgentToolPathRequest(
    ToolId ToolId,
    string? ExecutablePath,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record InstallAgentMsiToolRequest(
    ToolInstallPlan Plan,
    string PackageRelativePath,
    bool UserConfirmed,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CaptureAgentInventoryRequest(
    SystemId SystemId,
    bool IncludeLegacyComparison,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record CaptureAgentManageInventoryRequest(
    SystemId SystemId,
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

public sealed record ReviewAgentSystemSupportRequest(
    ElevatedBrokerExecutionRequest ExecutionRequest,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record ExecuteAgentSystemSupportRequest(
    Guid ReviewId,
    bool UserConfirmed,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public sealed record RequestAgentShutdownRequest(
    ShutdownReason Reason,
    bool UserConfirmedActiveTestCancellation,
    CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public abstract record AgentResponse;

public sealed record AgentAcknowledgement : AgentResponse;

public sealed record AgentSnapshotResponse(AgentSnapshot Snapshot) : AgentResponse;

public sealed record DevelopmentStepDiagnostic(
    string StepId,
    string Action,
    string State,
    string? ToolId,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> ParameterKeys);

public sealed record DevelopmentPlanDiagnostic(
    TestRunId RunId,
    string State,
    string PlanHash,
    AlgorithmIdentity PlannerAlgorithm,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<DevelopmentStepDiagnostic> Steps);

public sealed record DevelopmentDiagnostics(
    AgentSnapshot Agent,
    IReadOnlyList<DevelopmentPlanDiagnostic> RecentPlans,
    IReadOnlyList<AlgorithmIdentity> Algorithms);

public sealed record DevelopmentDiagnosticsResponse(
    DevelopmentDiagnostics Diagnostics)
    : AgentResponse;

public sealed record MonitoringSessionResponse(MonitoringSession Session) : AgentResponse;

public sealed record ToolStateResponse(ToolState ToolState) : AgentResponse;

public sealed record MsiToolInstallResponse(
    ElevatedBrokerExecutionResult Result)
    : AgentResponse;

public sealed record TestResultMetric(
    string MetricId,
    double Value,
    string Unit,
    string Aggregation,
    string? StepId = null,
    TestMetricSemantic? Semantic = null);

public sealed record TestMetricSemantic(
    string CanonicalMetricId,
    string CanonicalUnit,
    string WorkloadKey,
    string AggregationIntent,
    bool ComparableAcrossTools,
    string? LimitationCode = null);

public sealed record TestStepResult(
    string StepId,
    string State,
    ToolId? ToolId);

public sealed record TestResultArtifact(
    string RelativePath,
    string Sha256,
    long ByteLength,
    string MediaType);

public sealed record TestRunResultSummary(
    TestRunId RunId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    IReadOnlyList<TestStepResult> Steps,
    IReadOnlyList<TestResultMetric> Metrics,
    IReadOnlyList<TestResultArtifact> Artifacts);

public sealed record TestRunResultResponse(TestRunResultSummary Result)
    : AgentResponse;

public sealed record TestRunHistoryItem(
    TestRunId RunId,
    TestDefinitionId DefinitionId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record TestRunHistoryResponse(
    IReadOnlyList<TestRunHistoryItem> Runs)
    : AgentResponse;

public sealed record UserTestPresetListResponse(
    IReadOnlyList<UserTestPreset> Presets)
    : AgentResponse;

public sealed record UserTestPresetSavedResponse(UserTestPreset Preset)
    : AgentResponse;

public sealed record UserTestPresetDeletedResponse(
    Guid PresetId,
    bool Deleted)
    : AgentResponse;

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

public sealed record DiteLegacyImportPersistenceResponse(
    Guid ImportId,
    bool AlreadyExisted,
    int RunCount,
    int MetricCount)
    : AgentResponse;

public sealed record DiteLegacyImportHistoryItem(
    Guid ImportId,
    string SourceFileName,
    string SourceSha256,
    DateTimeOffset ImportedAtUtc,
    int RunCount,
    int MetricCount);

public sealed record DiteLegacyImportHistoryResponse(
    IReadOnlyList<DiteLegacyImportHistoryItem> Imports)
    : AgentResponse;

public sealed record DiteLegacyImportSummaryResponse(
    Guid ImportId,
    IReadOnlyList<DiteLegacyMetricSummary> Summaries)
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

public sealed record SystemSupportExecutionResponse(
    ElevatedBrokerExecutionResult Result)
    : AgentResponse;

public sealed record SystemSupportReviewResponse(
    Guid ReviewId,
    ElevatedBrokerOperationKind Operation,
    string PlanHash,
    DateTimeOffset ExpiresAtUtc,
    int CandidateCount,
    long CandidateBytes,
    string WarningCode)
    : AgentResponse;

public sealed record ShutdownResponse(ShutdownResult Result) : AgentResponse;

public sealed record AgentSnapshot(
    AgentInstanceId AgentInstanceId,
    bool IsTrayVisible,
    MonitoringSession? ActiveMonitoringSession,
    TestRunId? ActiveTestRunId,
    bool IsShuttingDown,
    IReadOnlyList<ProcessRegistration> Processes,
    IReadOnlyList<MonitorSample>? LatestMonitorSamples = null,
    IReadOnlyList<StorageHealthEvent>? RecentStorageHealthEvents = null,
    MonitorRuntimeDiagnostics? MonitorDiagnostics = null);

public abstract record AgentEvent(DateTimeOffset OccurredAtUtc);

public sealed record AgentTaskEvent(ApplicationTaskEvent TaskEvent)
    : AgentEvent(TaskEvent.OccurredAtUtc);

public sealed record AgentMonitorSampleEvent(MonitorSample Sample)
    : AgentEvent(Sample.SampledAtUtc);

public sealed record AgentTestEvent(TestEvent TestEvent)
    : AgentEvent(TestEvent.TaskEvent.OccurredAtUtc);

public sealed record AgentToolStateEvent(
    ToolState ToolState,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

public sealed record AgentProcessStateEvent(
    ProcessRegistration Registration,
    DateTimeOffset OccurredAtUtc)
    : AgentEvent(OccurredAtUtc);

public sealed record AgentShutdownEvent(
    ShutdownReason Reason,
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
    Test,
    Inventory,
    ElevatedBroker,
    MainApplication,
    ExternalTool
}

public abstract record WorkerRequest(
    WorkerKind Kind,
    CorrelationId CorrelationId);

public sealed record TestWorkerRequest(
    AuthorizedTestRun TestRun,
    CorrelationId CorrelationId)
    : WorkerRequest(WorkerKind.Test, CorrelationId);

public sealed record InventoryWorkerRequest(
    InventoryRequest InventoryRequest,
    CorrelationId CorrelationId)
    : WorkerRequest(WorkerKind.Inventory, CorrelationId);

public abstract record BrokerAction;

public sealed record BrokerSystemSupportAction(AuthorizedSystemSupportAction Action)
    : BrokerAction;

public sealed record BrokerToolInstallAction(AuthorizedToolInstall Install)
    : BrokerAction;

public sealed record ElevatedBrokerRequest(
    BrokerAction Action,
    Guid Nonce,
    string PlanHash,
    DateTimeOffset ExpiresAtUtc,
    CorrelationId CorrelationId)
    : WorkerRequest(WorkerKind.ElevatedBroker, CorrelationId);

public enum SupervisedProcessState
{
    Starting,
    Running,
    Stopping,
    Exited,
    Unresponsive,
    Failed
}

public sealed record WorkerHandle(
    Guid WorkerId,
    WorkerKind Kind,
    int ProcessId,
    DateTimeOffset StartedAtUtc);

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

public interface IProcessSupervisor
{
    Task<ApplicationResult<WorkerHandle>> StartWorkerAsync(
        WorkerRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationResult<ShutdownResult>> ShutdownAllAsync(
        ShutdownReason reason,
        CancellationToken cancellationToken);
}
