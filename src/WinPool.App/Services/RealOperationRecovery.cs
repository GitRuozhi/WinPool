using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Read-only recovery must query a retained identity even after it leaves the unfinished list.</summary>
public static class RealOperationRecovery
{
    public static async Task RefreshAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken,
        Action<AgentRealStructureJobResponse>? observeJob = null)
    {
        await QueryRetainedAsync(connection, submission, observe, cancellationToken);

        var result = await connection.SendAsync(
            new ListAgentRecoverableRealOperationsRequest(CorrelationId.New()), cancellationToken);
        if (!result.IsSuccess || result.Value is not AgentRecoverableRealOperationsResponse recovered)
            return;
        var cache = RealOperationStatusCache.For(connection);
        foreach (var status in recovered.Operations)
        {
            var operation = await cache.ResolveAsync(status, cancellationToken);
            if (operation is not null) observe(operation);
        }
        foreach (var job in recovered.StructureJobs ?? []) observeJob?.Invoke(job);
    }

    public static async Task RecheckAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken)
    {
        if (submission.OperationId is not { } operationId) return;
        var result = await connection.SendAsync(new ReconcileAgentRealOperationRequest(
            operationId, CorrelationId.New()), cancellationToken);
        if (!result.IsSuccess) return;
        var operation = await RealOperationStatusCache.For(connection).ResolveAsync(result.Value, cancellationToken);
        if (operation is not null) observe(operation);
    }

    private static async Task QueryRetainedAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken)
    {
        if (submission.OperationId is not { } operationId) return;
        var planHash = submission.PlanHash;
        var status = await RealOperationStatusCache.For(connection).QueryAsync(operationId, cancellationToken);
        if (status is not null && status.Plan.OperationId == operationId
            && StringComparer.Ordinal.Equals(status.Plan.PlanHash, planHash))
            observe(status);
    }
}
