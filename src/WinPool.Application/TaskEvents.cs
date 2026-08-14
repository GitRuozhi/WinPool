using WinPool.Domain;

namespace WinPool.Application;

public readonly record struct ApplicationTaskId(Guid Value)
{
    public static ApplicationTaskId New() => new(Guid.NewGuid());
}

public enum ApplicationTaskState
{
    Created,
    Planning,
    Validating,
    AwaitingAuthorization,
    Queued,
    Running,
    Verifying,
    Succeeded,
    Failed,
    Cancelled,
    PartiallyCompleted,
    Rejected
}

public enum ApplicationTaskEventKind
{
    StateChanged,
    Progress,
    EvidenceRecorded,
    CancellationRequested,
    Diagnostic
}

/// <summary>
/// Common event envelope for every observable long-running Application task.
/// DiagnosticText must be redacted before the event crosses this boundary.
/// </summary>
public sealed record ApplicationTaskEvent(
    ApplicationTaskId TaskId,
    CorrelationId CorrelationId,
    ApplicationTaskEventKind Kind,
    ApplicationTaskState State,
    DateTimeOffset OccurredAtUtc,
    string Code,
    string UserTextKey,
    string DiagnosticText,
    string? StepId = null,
    double? ProgressFraction = null,
    OperationId? OperationId = null,
    SessionId? SessionId = null);
