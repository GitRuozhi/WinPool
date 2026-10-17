using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

public sealed record RealStructurePoolGoal(string PoolId, PoolEditIntent Intent);
public sealed record RealStructureJobGoal(SystemId SystemId, StorageSnapshot Working,
    IReadOnlyList<RealStructurePoolGoal> PoolIntents, Guid? ResumeFromJobId = null);

public sealed record PrepareAgentRealStructureJobRequest(RealStructureJobGoal Goal, Guid JobId,
    string ProductSessionId, CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record AcceptAgentRealStructureJobRequest(Guid JobId, string GoalHash,
    string ProductSessionId, CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record QueryAgentRealStructureJobRequest(Guid JobId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record StopAgentRealStructureJobRequest(Guid JobId, string GoalHash,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record AgentRealStructureJobResponse(Guid JobId, string GoalHash, RealOperationState State,
    long Revision, IReadOnlyList<OperationId> Children, IReadOnlyList<string> Completed,
    string? Code, bool RequiresReconciliation, RealStructureJobGoal? RemainingGoal = null, SystemId? SystemId = null) : AgentResponse;
public sealed record AgentRealStructureJobStatusEvent(AgentRealStructureJobResponse Status,
    DateTimeOffset OccurredAtUtc) : AgentEvent(OccurredAtUtc);

public partial interface IRealOperationService
{
    Task<ApplicationResult<AgentResponse>> PrepareStructureJobAsync(PrepareAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> AcceptStructureJobAsync(AcceptAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> QueryStructureJobAsync(QueryAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> StopStructureJobAsync(StopAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
}
