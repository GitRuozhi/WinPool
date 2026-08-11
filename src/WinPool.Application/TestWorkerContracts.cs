using WinPool.Application;

namespace WinPool.Application;

/// <summary>
/// The complete typed boundary accepted by the external-process runner.
/// It intentionally contains argument tokens and never a command-line string.
/// </summary>
public sealed record ToolProcessRequest(
    TestRunId RunId,
    string StepId,
    ToolInvocation Invocation,
    ToolState ExpectedTool,
    TimeSpan GracefulShutdownTimeout);

public enum ToolProcessTerminationReason
{
    Completed,
    Cancelled,
    TimedOut
}

public sealed record ToolProcessIdentity(
    int ProcessId,
    string ExecutablePath,
    string FileVersion,
    string Sha256,
    DateTimeOffset StartedAtUtc);

public sealed record ToolProcessAudit(
    TestRunId RunId,
    string StepId,
    ToolId ToolId,
    ToolProcessIdentity Identity,
    DateTimeOffset ExitedAtUtc,
    int ExitCode,
    ToolProcessTerminationReason TerminationReason,
    bool GracefulTerminationRequested,
    bool GracefulTerminationAccepted,
    bool JobTerminationRequired);

public sealed record ToolProcessResult(
    ToolProcessAudit Audit,
    WorkerEventBufferStatistics BufferStatistics);

public enum WorkerEventKind
{
    ProcessState,
    StandardOutput,
    StandardError,
    FinalMetric,
    Error
}

public enum WorkerEventImportance
{
    Output = 0,
    Progress = 1,
    FinalMetric = 2,
    StateChange = 3,
    Error = 4
}

public sealed record WorkerEvent(
    TestRunId RunId,
    string StepId,
    WorkerEventKind Kind,
    WorkerEventImportance Importance,
    DateTimeOffset OccurredAtUtc,
    string Code,
    ReadOnlyMemory<byte> RawBytes,
    int? ProcessId = null,
    int? ExitCode = null,
    ToolProcessIdentity? ProcessIdentity = null,
    int? OutputCodePage = null);

public sealed record WorkerEventBufferStatistics(
    int Capacity,
    int BufferedCount,
    long AcceptedCount,
    long DroppedCount,
    IReadOnlyDictionary<WorkerEventKind, long> DroppedByKind);

public sealed class ToolProcessValidationException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StartTestWorkerCommand(
    IReadOnlyList<ToolProcessRequest> Requests);

public sealed record CancelToolProcessCommand(TestRunId RunId);

public sealed record AcknowledgeTestWorkerCompletionCommand(TestRunId RunId);

public sealed record TestWorkerEventBatch(
    IReadOnlyList<WorkerEvent> Events,
    WorkerEventBufferStatistics BufferStatistics);

public sealed record TestWorkerCompleted(
    IReadOnlyList<ToolProcessResult> Results);

public sealed record TestWorkerFailure(
    TestRunId RunId,
    string Code,
    string Diagnostic);
