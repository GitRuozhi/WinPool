using System.Runtime.CompilerServices;

namespace WinPool.Execution;

public interface IOperationExecutor
{
    ExecutionCapability Capability { get; }

    IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        CancellationToken cancellationToken);
}

public sealed class AuthorizedOperation
{
    internal AuthorizedOperation(OperationPlan plan, ExecutionContext context)
    {
        Plan = plan;
        Context = context;
    }

    public OperationPlan Plan { get; }
    public ExecutionContext Context { get; }
}

public sealed class ExecutorGate(
    IOperationPolicyEvaluator policy,
    IOperationAuthority authority,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        OperationPlan plan,
        ExecutionContext context,
        OperationAuthorizationToken token,
        IOperationExecutor executor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(executor);

        var decision = await policy.EvaluateAsync(plan, context, cancellationToken).ConfigureAwait(false);
        if (decision.Kind == PolicyDecisionKind.Rejected)
        {
            yield return Rejected(plan, decision.Code, decision.Message);
            yield break;
        }

        if ((executor.Capability & plan.RequiredCapabilities) != plan.RequiredCapabilities)
        {
            yield return Rejected(plan, "executor.capability-mismatch", "The selected executor cannot satisfy the plan capabilities.");
            yield break;
        }

        var validation = authority.Consume(token, plan, context);
        if (!validation.IsValid)
        {
            yield return Rejected(plan, validation.Code, validation.Message);
            yield break;
        }

        var authorized = new AuthorizedOperation(plan, context);
        await foreach (var executionEvent in executor.ExecuteAsync(authorized, cancellationToken).ConfigureAwait(false))
        {
            yield return executionEvent;
        }
    }

    private ExecutionEvent Rejected(OperationPlan plan, string code, string message) =>
        new(plan.OperationId, ExecutionEventKind.Rejected, _timeProvider.GetUtcNow(), code, message);
}

public sealed class LocalStorageMutationExecutor(TimeProvider? timeProvider = null)
    : IOperationExecutor
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ExecutionCapability Capability => ExecutionCapability.MutateStorageStructure;

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        // Defense in depth: V0.2 contains no Windows storage mutation implementation.
        // This executor rejects even when invoked after a valid authorization in a
        // future/disposable environment.
        yield return new ExecutionEvent(
            operation.Plan.OperationId,
            ExecutionEventKind.Rejected,
            _timeProvider.GetUtcNow(),
            "executor.local-storage-mutation-unavailable",
            "WinPool V0.21 does not implement real local storage-structure mutation.");

        await Task.CompletedTask;
    }
}
