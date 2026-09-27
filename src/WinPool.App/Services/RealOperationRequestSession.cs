using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>
/// One UI preparation and one explicit acceptance. An uncertain reply is
/// resolved by querying the assigned operation, never by resubmitting Accept.
/// </summary>
public sealed class RealOperationRequestSession(
    IAgentConnection connection, string productSessionId)
{
    private readonly Guid preparationId = Guid.NewGuid();
    private OperationId? confirmedOperationId;
    private string? confirmedPlanHash;
    private bool acceptSent;

    public Guid PreparationId => preparationId;

    public async Task<ApplicationResult<AgentResponse>> PrepareAsync(
        RealOperationIntentRequest intent, CancellationToken cancellationToken)
    {
        ApplicationResult<AgentResponse>? result = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            result = await connection.SendAsync(
                new PrepareAgentRealOperationRequest(intent, preparationId,
                    productSessionId, CorrelationId.New()), cancellationToken);
            if (result.Status != ApplicationStatus.OutcomeUnknown)
                break;
        }
        return result!;
    }

    public void Confirm(AgentRealOperationResponse frozen)
    {
        if (frozen.State != RealOperationState.Prepared ||
            frozen.Plan.RealOperation is null ||
            frozen.Plan.OperationId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(frozen.Plan.PlanHash))
            throw new InvalidOperationException("Only an Agent-frozen prepared plan can be confirmed.");
        confirmedOperationId = frozen.Plan.OperationId;
        confirmedPlanHash = frozen.Plan.PlanHash;
    }

    public Task<ApplicationResult<AgentResponse>> AcceptOnceAsync(
        CancellationToken cancellationToken)
    {
        if (confirmedOperationId is not { } operationId ||
            confirmedPlanHash is null || acceptSent)
            throw new InvalidOperationException("Acceptance needs one explicit confirmed plan.");
        acceptSent = true;
        return connection.SendAsync(new AcceptAgentRealOperationRequest(
            operationId, confirmedPlanHash, productSessionId,
            CorrelationId.New()), cancellationToken);
    }

    public Task<ApplicationResult<AgentResponse>> QueryAsync(
        CancellationToken cancellationToken)
    {
        if (confirmedOperationId is not { } operationId || !acceptSent)
            throw new InvalidOperationException("Query needs a previously accepted operation identity.");
        return connection.SendAsync(new QueryAgentRealOperationRequest(
            operationId, CorrelationId.New()), cancellationToken);
    }
}
