using System.Runtime.CompilerServices;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Read-only recovery must query a retained identity even after it leaves the unfinished list.</summary>
public static class RealOperationRecovery
{
    public static async Task RefreshAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, Action<AgentRealOperationResponse> observe,
        CancellationToken cancellationToken,
        Action<AgentRealStructureJobResponse>? observeJob = null,
        AgentRealOperationResponse? retainedStatus = null)
    {
        if (retainedStatus is not null && Matches(submission, retainedStatus)) observe(retainedStatus);
        else await QueryRetainedAsync(connection, submission, observe, cancellationToken);

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

    /// <summary>Follow the retained identity after recovery, independently of its originating page.</summary>
    public static async IAsyncEnumerable<AgentRealOperationResponse> WatchAsync(IAgentConnection connection,
        RealOperationSubmissionState submission, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (submission.OperationId is not { } operationId) yield break;
        var planHash = submission.PlanHash;
        // The cache subscribes before querying, closing the gap between the
        // initial unfinished-list read and this workspace-owned subscription.
        await foreach (var status in RealOperationStatusCache.For(connection).WatchAsync(operationId, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (submission.OperationId != operationId || submission.PlanHash != planHash) yield break;
            if (Matches(submission, status)) yield return status;
            if (submission.OperationId != operationId || submission.PlanHash != planHash) yield break;
        }
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

    private static bool Matches(RealOperationSubmissionState submission, AgentRealOperationResponse status) =>
        submission.OperationId == status.Plan.OperationId
        && StringComparer.Ordinal.Equals(submission.PlanHash, status.Plan.PlanHash);
}
