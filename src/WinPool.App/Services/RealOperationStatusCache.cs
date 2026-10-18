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
    private readonly SemaphoreSlim evidenceGate = new(1, 1);

    private RealOperationStatusCache(IAgentConnection connection) => this.connection = connection;

    public static RealOperationStatusCache For(IAgentConnection connection) =>
        Caches.GetValue(connection, static value => new(value));

    public void Remember(AgentRealOperationResponse operation) => operations.AddOrUpdate(
        operation.Plan.OperationId, new CachedOperation(operation, 0), (_, previous) => previous with { Operation = operation });

    public async Task<AgentRealOperationResponse?> ResolveAsync(AgentResponse? response, CancellationToken cancellationToken)
    {
        if (response is AgentRealOperationEvidenceResponse evidence) response = evidence.Operation;
        if (response is AgentRealOperationResponse full)
        {
            Remember(full);
            return full;
        }
        if (response is not AgentRealOperationStatusResponse status) return null;

        var terminal = status.State is not (RealOperationState.Prepared or RealOperationState.Accepted or RealOperationState.Running);
        operations.TryGetValue(status.OperationId, out var known);
        if (known is not null && (status.Revision < known.Revision
            || status.Revision == known.Revision
                && status.ReconciliationDiagnostic == known.Operation.ReconciliationDiagnostic))
            return known.Operation;
        if (known is null
            || (terminal && known.EvidenceRevision < status.Revision))
        {
            await evidenceGate.WaitAsync(cancellationToken);
            try
            {
                if (!operations.TryGetValue(status.OperationId, out known)
                    || (terminal && known.EvidenceRevision < status.Revision))
                {
                    // Initial/recovered operations load their frozen plan once.
                    var offset = 0;
                    var steps = new List<RealOperationStepProgress>();
                    AgentRealOperationResponse? loadedOperation = null;
                    do
                    {
                        var result = await connection.SendAsync(new LoadAgentRealOperationEvidenceRequest(
                            status.OperationId, offset, 16, CorrelationId.New()), cancellationToken);
                        if (!result.IsSuccess || result.Value is not AgentRealOperationEvidenceResponse loaded) return null;
                        if (loaded.Operation.Plan.OperationId != status.OperationId)
                            throw new InvalidDataException("The evidence belongs to another operation.");
                        loadedOperation = loaded.Operation;
                        steps.AddRange(loaded.Operation.Steps);
                        if (loaded.NextOffset is not { } nextOffset) break;
                        if (nextOffset <= offset) throw new InvalidDataException("The evidence page did not advance.");
                        offset = nextOffset;
                    } while (true);
                    var operation = loadedOperation! with { Steps = steps.ToArray() };
                    var candidate = new CachedOperation(operation, status.Revision, terminal ? status.Revision : -1);
                    operations.AddOrUpdate(status.OperationId, candidate,
                        (_, previous) => previous.Revision > status.Revision ? previous : candidate);
                }
            }
            finally { evidenceGate.Release(); }
        }

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
                            ResultEvidence = step.ResultEvidence ?? old?.ResultEvidence
                        };
                    }).ToArray(),
                    Code = status.Code,
                    RequiresReconciliation = status.RequiresReconciliation,
                    ReconciliationDiagnostic = status.ReconciliationDiagnostic
                }, status.Revision, previous.EvidenceRevision));
        return merged.Operation;
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
