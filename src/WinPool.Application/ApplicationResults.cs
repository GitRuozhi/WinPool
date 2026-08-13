using WinPool.Domain;

namespace WinPool.Application;

public readonly record struct CorrelationId(Guid Value)
{
    public static CorrelationId New() => new(Guid.NewGuid());
}

public enum ApplicationStatus
{
    Succeeded,
    Rejected,
    Cancelled,
    OutcomeUnknown,
    Failed,
    PartiallyCompleted,
    RequiresAuthorization,
    RequiresEnvironment
}

public enum ApplicationMessageSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>
/// A persistence-safe application message. DiagnosticText must already be redacted.
/// </summary>
public sealed record ApplicationMessage(
    string Code,
    string UserTextKey,
    string DiagnosticText,
    ApplicationMessageSeverity Severity,
    IReadOnlyList<StorageObjectId> RelatedObjects);

public sealed record ApplicationResult(
    ApplicationStatus Status,
    IReadOnlyList<ApplicationMessage> Messages,
    CorrelationId CorrelationId)
{
    public bool IsSuccess => Status is ApplicationStatus.Succeeded or ApplicationStatus.PartiallyCompleted;

    public static ApplicationResult Succeeded(CorrelationId correlationId) =>
        new(ApplicationStatus.Succeeded, Array.Empty<ApplicationMessage>(), correlationId);
}

public sealed record ApplicationResult<T>(
    ApplicationStatus Status,
    T? Value,
    IReadOnlyList<ApplicationMessage> Messages,
    CorrelationId CorrelationId)
{
    public bool IsSuccess => Status is ApplicationStatus.Succeeded or ApplicationStatus.PartiallyCompleted;

    public static ApplicationResult<T> Succeeded(T value, CorrelationId correlationId) =>
        new(ApplicationStatus.Succeeded, value, Array.Empty<ApplicationMessage>(), correlationId);

    public static ApplicationResult<T> FromStatus(
        ApplicationStatus status,
        CorrelationId correlationId,
        params ApplicationMessage[] messages) =>
        new(status, default, messages, correlationId);
}
