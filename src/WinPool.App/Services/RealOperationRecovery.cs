using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Read-only recovery must query a retained identity even after it leaves the unfinished list.</summary>
public static class RealOperationRecovery
{
    public static async Task RefreshAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken)
    {
        var retained = submission.OperationId;
        await QueryRetainedAsync(connection, submission, observe, cancellationToken);

        var result = await connection.SendAsync(
            new ListAgentRecoverableRealOperationsRequest(CorrelationId.New()), cancellationToken);
        if (!result.IsSuccess || result.Value is not AgentRecoverableRealOperationsResponse recovered)
            return;
        foreach (var status in recovered.Operations)
            observe(status);

        // Listing may start asynchronous reconciliation. Query the newly found
        // exact operation as well; subsequent refreshes query it until closed.
        if (submission.OperationId != retained)
            await QueryRetainedAsync(connection, submission, observe, cancellationToken);
    }

    private static async Task QueryRetainedAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken)
    {
        if (submission.OperationId is not { } operationId) return;
        var planHash = submission.PlanHash;
        var result = await connection.SendAsync(
            new QueryAgentRealOperationRequest(operationId, CorrelationId.New()), cancellationToken);
        if (result.IsSuccess && result.Value is AgentRealOperationResponse status
            && status.Plan.OperationId == operationId
            && StringComparer.Ordinal.Equals(status.Plan.PlanHash, planHash))
            observe(status);
    }
}
