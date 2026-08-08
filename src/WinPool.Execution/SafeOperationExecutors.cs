using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using WinPool.Domain;

namespace WinPool.Execution;

public sealed record SimulationDocumentSnapshot
{
    public SimulationDocumentSnapshot(
        SystemId systemId,
        long revision,
        IEnumerable<KeyValuePair<string, string>> values)
    {
        if (systemId.Value == Guid.Empty)
        {
            throw new ArgumentException("A simulation system identity is required.", nameof(systemId));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        ArgumentNullException.ThrowIfNull(values);
        SystemId = systemId;
        Revision = revision;
        Values = ToReadOnlyValues(values);
    }

    public SystemId SystemId { get; }
    public long Revision { get; }
    public IReadOnlyDictionary<string, string> Values { get; }

    public static SimulationDocumentSnapshot Create(
        SystemId systemId,
        long revision,
        IEnumerable<KeyValuePair<string, string>> values) =>
        new(systemId, revision, values);

    internal static IReadOnlyDictionary<string, string> ToReadOnlyValues(
        IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(
                values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal));
}

public sealed record SimulationMutationReceipt(
    SimulationDocumentSnapshot Before,
    SimulationDocumentSnapshot After);

public interface ISimulationDocumentStore
{
    Task<SimulationMutationReceipt> ApplyAsync(
        OperationPlan plan,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        SimulationMutationReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed class InMemorySimulationDocumentStore : ISimulationDocumentStore
{
    private readonly object _sync = new();
    private readonly Dictionary<SystemId, SimulationDocumentSnapshot> _documents;

    public InMemorySimulationDocumentStore(IEnumerable<SimulationDocumentSnapshot> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents.ToDictionary(document => document.SystemId);
    }

    public SimulationDocumentSnapshot Get(SystemId systemId)
    {
        lock (_sync)
        {
            if (!_documents.TryGetValue(systemId, out var document))
            {
                throw new KeyNotFoundException("The simulation document does not exist.");
            }

            return document;
        }
    }

    public Task<SimulationMutationReceipt> ApplyAsync(
        OperationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_documents.TryGetValue(plan.SystemId, out var before))
            {
                throw new KeyNotFoundException("The simulation document does not exist.");
            }

            var values = before.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var parameter in plan.Parameters)
            {
                values[parameter.Key] = parameter.Value;
            }

            var after = SimulationDocumentSnapshot.Create(plan.SystemId, checked(before.Revision + 1), values);
            _documents[plan.SystemId] = after;
            return Task.FromResult(new SimulationMutationReceipt(before, after));
        }
    }

    public Task RestoreAsync(
        SimulationMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_documents.TryGetValue(receipt.After.SystemId, out var current) ||
                current.Revision != receipt.After.Revision)
            {
                throw new InvalidOperationException("The simulation document changed after the failed operation and cannot be restored automatically.");
            }

            _documents[receipt.Before.SystemId] = receipt.Before;
            return Task.CompletedTask;
        }
    }
}

public enum SimulationExecutionCheckpoint
{
    BeforeApply,
    AfterApply,
    BeforeComplete
}

public interface ISimulationExecutionFaultInjector
{
    Task InspectAsync(
        SimulationExecutionCheckpoint checkpoint,
        OperationPlan plan,
        CancellationToken cancellationToken);
}

public sealed class NoSimulationExecutionFaults : ISimulationExecutionFaultInjector
{
    public static NoSimulationExecutionFaults Instance { get; } = new();

    private NoSimulationExecutionFaults()
    {
    }

    public Task InspectAsync(
        SimulationExecutionCheckpoint checkpoint,
        OperationPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class SimulationOperationExecutor(
    ISimulationDocumentStore documents,
    ISimulationExecutionFaultInjector? faultInjector = null,
    TimeProvider? timeProvider = null)
    : IOperationExecutor
{
    private readonly ISimulationDocumentStore _documents =
        documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly ISimulationExecutionFaultInjector _faultInjector =
        faultInjector ?? NoSimulationExecutionFaults.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ExecutionCapability Capability => ExecutionCapability.SimulateStorageMutation;

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var invalid = Validate(operation);
        if (invalid is not null)
        {
            yield return Event(operation.Plan, ExecutionEventKind.Rejected, invalid.Value.Code, invalid.Value.Message);
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Accepted, "simulation.accepted", "The authorized simulation operation was accepted.");

        var beforeApply = await InvokeAsync(
            () => _faultInjector.InspectAsync(
                SimulationExecutionCheckpoint.BeforeApply,
                operation.Plan,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (beforeApply is not null)
        {
            yield return TerminalForFailure(operation.Plan, beforeApply.Value, false);
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Started, "simulation.started", "The simulation document update started.");

        SimulationMutationReceipt? receipt = null;
        var apply = await InvokeAsync(
            async () => receipt = await _documents.ApplyAsync(operation.Plan, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (apply is not null)
        {
            yield return TerminalForFailure(operation.Plan, apply.Value, false);
            yield break;
        }

        yield return Event(
            operation.Plan,
            ExecutionEventKind.Progress,
            "simulation.document-updated",
            $"The simulation document advanced from revision {receipt!.Before.Revision} to {receipt.After.Revision}.");

        var afterApply = await InvokeAsync(
            () => _faultInjector.InspectAsync(
                SimulationExecutionCheckpoint.AfterApply,
                operation.Plan,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (afterApply is not null)
        {
            var recovered = await TryRestoreAsync(receipt, cancellationToken).ConfigureAwait(false);
            yield return TerminalForFailure(operation.Plan, afterApply.Value, recovered);
            yield break;
        }

        var beforeComplete = await InvokeAsync(
            () => _faultInjector.InspectAsync(
                SimulationExecutionCheckpoint.BeforeComplete,
                operation.Plan,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (beforeComplete is not null)
        {
            var recovered = await TryRestoreAsync(receipt, cancellationToken).ConfigureAwait(false);
            yield return TerminalForFailure(operation.Plan, beforeComplete.Value, recovered);
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Completed, "simulation.completed", "The simulation operation completed.");
    }

    private static (string Code, string Message)? Validate(AuthorizedOperation operation)
    {
        if (operation.Context.Environment.Kind != EnvironmentKind.Simulation)
        {
            return ("simulation.environment-required", "The simulation executor cannot operate on a real or replay environment.");
        }

        if (operation.Plan.Intent != OperationIntent.SimulateStorageMutation ||
            operation.Plan.RequiredCapabilities != ExecutionCapability.SimulateStorageMutation ||
            operation.Plan.Risk != RiskLevel.R1SimulationWrite)
        {
            return ("simulation.plan-not-supported", "The simulation executor accepts only an R1 simulation-mutation plan.");
        }

        if (operation.Plan.Targets.Any(target => target.System != operation.Plan.SystemId))
        {
            return ("simulation.target-system-mismatch", "A simulation target belongs to another system.");
        }

        return null;
    }

    private async Task<bool> TryRestoreAsync(
        SimulationMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _documents.RestoreAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<ExecutionFailure?> InvokeAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(true);
        }
        catch (Exception)
        {
            return new(false);
        }
    }

    private ExecutionEvent TerminalForFailure(
        OperationPlan plan,
        ExecutionFailure failure,
        bool recovered)
    {
        var recovery = recovered ? " The previous simulation revision was restored." : string.Empty;
        return failure.WasCancelled
            ? Event(plan, ExecutionEventKind.Cancelled, "simulation.cancelled", $"The simulation operation was cancelled.{recovery}")
            : Event(plan, ExecutionEventKind.Failed, "simulation.failed", $"The simulation operation failed.{recovery}");
    }

    private ExecutionEvent Event(
        OperationPlan plan,
        ExecutionEventKind kind,
        string code,
        string message) =>
        new(plan.OperationId, kind, _timeProvider.GetUtcNow(), code, message);

    private readonly record struct ExecutionFailure(bool WasCancelled);
}

public interface IReadOnlyWindowsOperations
{
    Task<ReadOnlyWindowsResult> ReadInventoryAsync(
        SystemId systemId,
        IReadOnlyList<StorageObjectId> targets,
        CancellationToken cancellationToken);

    Task<ReadOnlyWindowsResult> ReadPerformanceCountersAsync(
        SystemId systemId,
        IReadOnlyList<StorageObjectId> targets,
        CancellationToken cancellationToken);

    Task<ReadOnlyWindowsResult> OpenNativePropertiesAsync(
        StorageObjectId target,
        CancellationToken cancellationToken);
}

public sealed record ReadOnlyWindowsResult(string Code, string Message);

public sealed class ReadOnlyWindowsExecutor(
    IReadOnlyWindowsOperations operations,
    TimeProvider? timeProvider = null)
    : IOperationExecutor
{
    private const ExecutionCapability SupportedCapabilities =
        ExecutionCapability.ReadInventory |
        ExecutionCapability.ReadPerformanceCounters |
        ExecutionCapability.OpenNativeProperties;

    private readonly IReadOnlyWindowsOperations _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ExecutionCapability Capability => SupportedCapabilities;

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var requiredCapability = RequiredCapability(operation.Plan.Intent);
        if (operation.Context.Environment.Kind is EnvironmentKind.Simulation or EnvironmentKind.Replay ||
            operation.Plan.Risk != RiskLevel.R0ReadOnly ||
            requiredCapability == ExecutionCapability.None ||
            operation.Plan.RequiredCapabilities != requiredCapability)
        {
            yield return Event(
                operation.Plan,
                ExecutionEventKind.Rejected,
                "windows-readonly.plan-not-supported",
                "The Windows read-only executor accepts only a matching R0 Windows query plan.");
            yield break;
        }

        if (operation.Plan.Intent == OperationIntent.OpenNativeProperties &&
            operation.Plan.Targets.Count != 1)
        {
            yield return Event(
                operation.Plan,
                ExecutionEventKind.Rejected,
                "windows-readonly.single-target-required",
                "Opening native properties requires exactly one stable target.");
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Accepted, "windows-readonly.accepted", "The authorized Windows read-only operation was accepted.");
        yield return Event(operation.Plan, ExecutionEventKind.Started, "windows-readonly.started", "The Windows read-only operation started.");

        ReadOnlyWindowsResult? result = null;
        var failure = await InvokeAsync(async () =>
        {
            result = operation.Plan.Intent switch
            {
                OperationIntent.ReadInventory => await _operations
                    .ReadInventoryAsync(operation.Plan.SystemId, operation.Plan.Targets, cancellationToken)
                    .ConfigureAwait(false),
                OperationIntent.ReadPerformanceCounters => await _operations
                    .ReadPerformanceCountersAsync(operation.Plan.SystemId, operation.Plan.Targets, cancellationToken)
                    .ConfigureAwait(false),
                OperationIntent.OpenNativeProperties => await _operations
                    .OpenNativePropertiesAsync(operation.Plan.Targets[0], cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException("The read-only intent was not validated.")
            };
        }, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            yield return failure.Value.WasCancelled
                ? Event(operation.Plan, ExecutionEventKind.Cancelled, "windows-readonly.cancelled", "The Windows read-only operation was cancelled.")
                : Event(operation.Plan, ExecutionEventKind.Failed, "windows-readonly.failed", "The Windows read-only operation failed.");
            yield break;
        }

        if (result is null || string.IsNullOrWhiteSpace(result.Code) || string.IsNullOrWhiteSpace(result.Message))
        {
            yield return Event(operation.Plan, ExecutionEventKind.Failed, "windows-readonly.invalid-result", "The Windows read-only provider returned an invalid result.");
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Completed, result.Code, result.Message);
    }

    private static ExecutionCapability RequiredCapability(OperationIntent intent) =>
        intent switch
        {
            OperationIntent.ReadInventory => ExecutionCapability.ReadInventory,
            OperationIntent.ReadPerformanceCounters => ExecutionCapability.ReadPerformanceCounters,
            OperationIntent.OpenNativeProperties => ExecutionCapability.OpenNativeProperties,
            _ => ExecutionCapability.None
        };

    private static async Task<ExecutionFailure?> InvokeAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(true);
        }
        catch (Exception)
        {
            return new(false);
        }
    }

    private ExecutionEvent Event(
        OperationPlan plan,
        ExecutionEventKind kind,
        string code,
        string message) =>
        new(plan.OperationId, kind, _timeProvider.GetUtcNow(), code, message);

    private readonly record struct ExecutionFailure(bool WasCancelled);
}

public interface IReplayEventSource
{
    IAsyncEnumerable<ExecutionEvent> ReadAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryReplayEventSource(IEnumerable<ExecutionEvent> events)
    : IReplayEventSource
{
    private readonly IReadOnlyList<ExecutionEvent> _events =
        events?.ToArray() ?? throw new ArgumentNullException(nameof(events));

    public async IAsyncEnumerable<ExecutionEvent> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var executionEvent in _events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return executionEvent;
        }

        await Task.CompletedTask;
    }
}

public sealed class ReplayExecutor(
    IReplayEventSource source,
    TimeProvider? timeProvider = null)
    : IOperationExecutor
{
    private readonly IReplayEventSource _source =
        source ?? throw new ArgumentNullException(nameof(source));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ExecutionCapability Capability => ExecutionCapability.ReplayEvidence;

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.Context.Environment.Kind != EnvironmentKind.Replay ||
            operation.Plan.Intent != OperationIntent.ReplayHistoricalEvents ||
            operation.Plan.Risk != RiskLevel.R0ReadOnly ||
            operation.Plan.RequiredCapabilities != ExecutionCapability.ReplayEvidence)
        {
            yield return Event(
                operation.Plan,
                ExecutionEventKind.Rejected,
                "replay.plan-not-supported",
                "The replay executor accepts only an R0 historical-event plan in a replay environment.");
            yield break;
        }

        yield return Event(operation.Plan, ExecutionEventKind.Accepted, "replay.accepted", "The authorized historical-event replay was accepted.");
        yield return Event(operation.Plan, ExecutionEventKind.Started, "replay.started", "Historical event replay started.");

        var enumerator = _source.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext = false;
                ReplayReadFailure failure = ReplayReadFailure.None;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failure = ReplayReadFailure.Cancelled;
                }
                catch (Exception)
                {
                    failure = ReplayReadFailure.Failed;
                }

                if (failure == ReplayReadFailure.Cancelled)
                {
                    yield return Event(operation.Plan, ExecutionEventKind.Cancelled, "replay.cancelled", "Historical event replay was cancelled.");
                    yield break;
                }

                if (failure == ReplayReadFailure.Failed)
                {
                    yield return Event(operation.Plan, ExecutionEventKind.Failed, "replay.failed", "Historical event replay failed.");
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                var historical = enumerator.Current;
                yield return historical with
                {
                    OperationId = operation.Plan.OperationId,
                    SourceOperationId = historical.SourceOperationId ?? historical.OperationId
                };
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        yield return Event(operation.Plan, ExecutionEventKind.Completed, "replay.completed", "Historical event replay completed.");
    }

    private ExecutionEvent Event(
        OperationPlan plan,
        ExecutionEventKind kind,
        string code,
        string message) =>
        new(plan.OperationId, kind, _timeProvider.GetUtcNow(), code, message);

    private enum ReplayReadFailure
    {
        None,
        Cancelled,
        Failed
    }
}
