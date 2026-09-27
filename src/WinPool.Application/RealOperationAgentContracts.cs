using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

/// <summary>
/// Real mode belongs to one verified App incarnation and one product session.
/// These requests are transport intents; the Agent must derive trust from its
/// pipe peer, its own token, and fresh Windows inventory.
/// </summary>
public sealed record EnterAgentRealModeRequest(
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record ExitAgentRealModeRequest(
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record PrepareAgentRealOperationRequest(
    RealOperationIntentRequest Intent,
    Guid PreparationId,
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

/// <summary>
/// The App submits only the identity and hash of the Agent's prepared plan.
/// Accept must persist the authorization before starting background execution.
/// </summary>
public sealed record AcceptAgentRealOperationRequest(
    OperationId OperationId,
    string PlanHash,
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record QueryAgentRealOperationRequest(
    OperationId OperationId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record QueryAgentRealPartitionResizeRangeRequest(
    StorageObjectId Partition,
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record StopAgentRealOperationFollowingStepsRequest(
    OperationId OperationId,
    string PlanHash,
    string ProductSessionId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record AgentRealModeResponse(
    bool IsArmed,
    string Code) : AgentResponse;

public sealed record AgentRealPartitionResizeRangeResponse(
    RealPartitionResizeRange Range) : AgentResponse;

public enum RealOperationState
{
    Prepared,
    Accepted,
    Running,
    Succeeded,
    Rejected,
    Cancelled,
    Failed,
    PartiallyCompleted,
    OutcomeUnknown
}

public enum RealOperationStepState
{
    Pending,
    PreparingCall,
    CallIssued,
    WaitingForProvider,
    Verifying,
    Verified,
    Failed,
    OutcomeUnknown,
    StoppedBeforeCall
}

public sealed record RealOperationStepProgress(
    string StepId,
    RealOperationStepState State,
    string? Code,
    string? TargetEvidence,
    string? ResultEvidence);

/// <summary>
/// The persisted status is authoritative; a lost Accept reply is recovered by
/// querying the same OperationId, never by submitting another operation.
/// </summary>
public sealed record AgentRealOperationResponse(
    OperationPlan Plan,
    RealOperationState State,
    IReadOnlyList<RealOperationStepProgress> Steps,
    string? Code,
    bool RequiresReconciliation) : AgentResponse;

public interface IRealOperationService
{
    Task<ApplicationResult<AgentResponse>> EnterModeAsync(
        EnterAgentRealModeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> ExitModeAsync(
        ExitAgentRealModeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> PrepareAsync(
        PrepareAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> AcceptAsync(
        AcceptAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> QueryAsync(
        QueryAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> QueryPartitionResizeRangeAsync(
        QueryAgentRealPartitionResizeRangeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<ApplicationResult<AgentResponse>> StopFollowingStepsAsync(
        StopAgentRealOperationFollowingStepsRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken);
}
