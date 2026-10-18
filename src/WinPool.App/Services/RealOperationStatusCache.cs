using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>The App keeps the frozen plan once and merges durable status revisions.</summary>
public sealed class RealOperationStatusCache
{
    private static readonly ConditionalWeakTable<IAgentConnection, RealOperationStatusCache> Caches = new();
    private readonly IAgentConnection connection;
    private readonly ConcurrentDictionary<OperationId, CachedOperation> operations = new();
    private readonly ConcurrentDictionary<OperationId, AgentRealOperationStatusResponse> statuses = new();

    private RealOperationStatusCache(IAgentConnection connection) => this.connection = connection;

    public static RealOperationStatusCache For(IAgentConnection connection) =>
        Caches.GetValue(connection, static value => new(value));

    public void Remember(AgentRealOperationResponse operation) => operations.AddOrUpdate(
        operation.Plan.OperationId, new CachedOperation(operation, 0), (_, previous) =>
        {
            if (previous.Operation.Plan.PlanHash != operation.Plan.PlanHash || previous.Operation.Plan.SystemId != operation.Plan.SystemId)
                throw new InvalidDataException("The frozen operation identity changed.");
            return previous.Revision > 0 ? previous : previous with { Operation = operation };
        });

    public Task<AgentRealOperationResponse?> ResolveAsync(AgentResponse? response, CancellationToken cancellationToken) =>
        Task.FromResult(Resolve(response));

    private AgentRealOperationResponse? Resolve(AgentResponse? response)
    {
        if (response is AgentRealOperationEvidenceResponse evidence) response = evidence.Operation;
        if (response is AgentRealOperationResponse full)
        {
            Remember(full);
            return full;
        }
        if (response is not AgentRealOperationStatusResponse status) return null;
        RememberStatus(status);

        operations.TryGetValue(status.OperationId, out var known);
        if (known is not null && (status.PlanHash is not null && status.PlanHash != known.Operation.Plan.PlanHash
            || status.SystemId is not null && status.SystemId != known.Operation.Plan.SystemId))
            throw new InvalidDataException("The status belongs to another frozen operation identity.");
        if (known is not null && (status.Revision < known.Revision
            || status.Revision == known.Revision
                && status.ReconciliationDiagnostic == known.Operation.ReconciliationDiagnostic))
            return known.Operation;
        // A terminal status is authoritative without downloading historical evidence.
        // Unknown/recovered operations are handled through their light identity.
        if (known is null) return null;
        var merged = operations.AddOrUpdate(status.OperationId,
            _ => throw new InvalidOperationException("The frozen operation was not loaded."),
            (_, previous) => status.Revision < previous.Revision
                || (status.Revision == previous.Revision
                    && status.ReconciliationDiagnostic == previous.Operation.ReconciliationDiagnostic)
                ? previous : new CachedOperation(
                previous.Operation with
                {
                    State = status.State,
                    Steps = status.Steps.Select(step =>
                    {
                        var old = previous.Operation.Steps.FirstOrDefault(value => value.StepId == step.StepId);
                        return step with
                        {
                            TargetEvidence = step.TargetEvidence ?? old?.TargetEvidence,
                            ResultEvidence = step.ResultEvidence ?? old?.ResultEvidence,
                            ResultIdentity = step.ResultIdentity ?? old?.ResultIdentity
                        };
                    }).ToArray(),
                    Code = status.Code,
                    RequiresReconciliation = status.RequiresReconciliation,
                    ReconciliationDiagnostic = status.ReconciliationDiagnostic,
                    ActiveStepElapsedMilliseconds = status.ActiveStepElapsedMilliseconds
                }, status.Revision, previous.EvidenceRevision));
        return merged.Operation;
    }

    public AgentRealOperationStatusResponse? RememberStatus(AgentResponse? response)
    {
        if (response is AgentRealOperationResponse provided) Remember(provided);
        var status = response switch
        {
            AgentRealOperationStatusResponse light => light,
            AgentRealOperationResponse full => new AgentRealOperationStatusResponse(full.Plan.OperationId, 0,
                full.State, full.Steps, full.Code, full.RequiresReconciliation, full.ReconciliationDiagnostic,
                full.Plan.SystemId, full.Plan.PlanHash, full.Plan.Targets, full.ActiveStepElapsedMilliseconds),
            _ => null
        };
        if (status is null) return null;
        return statuses.AddOrUpdate(status.OperationId, status, (_, previous) =>
        {
            if (previous.PlanHash is not null && status.PlanHash is not null && previous.PlanHash != status.PlanHash
                || previous.SystemId is not null && status.SystemId is not null && previous.SystemId != status.SystemId)
                throw new InvalidDataException("The durable status identity changed.");
            return status.Revision < previous.Revision ? previous : status;
        });
    }

    public async Task<AgentRealOperationStatusResponse?> QueryStatusAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(new QueryAgentRealOperationRequest(operationId, CorrelationId.New()), cancellationToken);
        return result.IsSuccess ? RememberStatus(result.Value) : null;
    }

    public async IAsyncEnumerable<AgentRealOperationStatusResponse> WatchStatusAsync(OperationId operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var events = connection.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = events.MoveNextAsync().AsTask();
        var initial = await QueryStatusAsync(operationId, cancellationToken);
        if (initial is not null) yield return initial;
        while (await next)
        {
            AgentRealOperationStatusResponse? status = null;
            if (events.Current is AgentRealOperationStatusEvent changed && changed.Status.OperationId == operationId)
                status = RememberStatus(changed.Status);
            else if (events.Current is AgentStateReseedEvent { Reason: not "agent.events.latest_snapshot" })
                status = await QueryStatusAsync(operationId, cancellationToken);
            if (status is not null) yield return status;
            next = events.MoveNextAsync().AsTask();
        }
    }

    public async Task<AgentRealOperationEvidenceResponse?> LoadEvidenceAsync(LoadAgentRealOperationEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is not AgentRealOperationEvidenceResponse evidence) return null;
        if (evidence.Operation.Plan.OperationId != request.OperationId)
            throw new InvalidDataException("The evidence belongs to another operation.");
        // Optional history never changes the authoritative light state.
        return evidence;
    }

    public async Task<AgentRealOperationResponse?> QueryAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(new QueryAgentRealOperationRequest(
            operationId, CorrelationId.New()), cancellationToken);
        return result.IsSuccess ? await ResolveAsync(result.Value, cancellationToken) : null;
    }

    public async IAsyncEnumerable<AgentRealOperationResponse> WatchAsync(OperationId operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AgentRealOperationResponse? delivered = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var watcher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var events = connection.WatchAsync(watcher.Token).GetAsyncEnumerator(watcher.Token);
            var next = events.MoveNextAsync().AsTask();
            try
            {
                var initial = await QueryAsync(operationId, cancellationToken);
                if (initial is not null && !ReferenceEquals(initial, delivered))
                {
                    delivered = initial;
                    yield return initial;
                }
                while (await next)
                {
                    var item = events.Current;
                    AgentRealOperationResponse? operation = null;
                    if (item is AgentRealOperationStatusEvent changed && changed.Status.OperationId == operationId)
                        operation = await ResolveAsync(changed.Status, cancellationToken);
                    else if (item is AgentStateReseedEvent { Reason: not "agent.events.latest_snapshot" })
                        // Revisions are durable event IDs across Agent instances;
                        // wall-clock changes do not order operation status.
                        operation = await QueryAsync(operationId, cancellationToken);
                    if (operation is not null && !ReferenceEquals(operation, delivered))
                    {
                        delivered = operation;
                        yield return operation;
                    }
                    next = events.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                watcher.Cancel();
                try { await next; }
                catch (OperationCanceledException) when (watcher.IsCancellationRequested) { }
            }
        }
    }

    private sealed record CachedOperation(AgentRealOperationResponse Operation, long Revision, long EvidenceRevision = -1);
}
