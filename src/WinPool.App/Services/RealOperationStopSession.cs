using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Uses the queried Agent plan identity when requesting a stop.</summary>
public sealed class RealOperationStopSession(
    IAgentConnection connection, string productSessionId)
{
    public Task<ApplicationResult<AgentResponse>> QueryAsync(
        OperationId operationId, CancellationToken cancellationToken) =>
        connection.SendAsync(new QueryAgentRealOperationRequest(
            operationId, CorrelationId.New()), cancellationToken);

    public Task<ApplicationResult<AgentResponse>>? StopAfterCurrentStepAsync(
        AgentRealOperationResponse observed, bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!confirmed)
            return null;
        if (observed.State is not (RealOperationState.Prepared
            or RealOperationState.Accepted or RealOperationState.Running))
            throw new InvalidOperationException("Only a pending real operation can be stopped.");
        return connection.SendAsync(new StopAgentRealOperationFollowingStepsRequest(
            observed.Plan.OperationId, observed.Plan.PlanHash,
            productSessionId, CorrelationId.New()), cancellationToken);
    }
}
