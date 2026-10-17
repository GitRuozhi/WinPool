using System.Runtime.CompilerServices;
using WinPool.Domain;

namespace WinPool.Execution;

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
