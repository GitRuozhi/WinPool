using WinPool.Domain;
using WinPool.Execution;
using OperationExecutionContext = WinPool.Execution.ExecutionContext;

namespace WinPool.Application;

public sealed record AuthorizationReceipt(
    Guid Nonce,
    string PlanHash,
    EnvironmentId EnvironmentId,
    string MachineBinding,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed class AuthorizedOperation
{
    internal AuthorizedOperation(OperationPlan plan, AuthorizationReceipt receipt)
    {
        Plan = plan;
        Receipt = receipt;
    }

    public OperationPlan Plan { get; }

    public AuthorizationReceipt Receipt { get; }
}

public interface IOperationPlanner
{
    Task<ApplicationResult<OperationPlan>> BuildAsync(
        OperationRequest request,
        CancellationToken cancellationToken);
}

public interface IOperationPolicyEvaluator
{
    Task<ApplicationResult<PolicyDecision>> EvaluateAsync(
        OperationPlan plan,
        OperationExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IOperationAuthorizationCoordinator
{
    Task<ApplicationResult<AuthorizedOperation>> AuthorizeAsync(
        OperationPlan plan,
        OperationExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IOperationExecutor
{
    ExecutionCapability Capability { get; }

    IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        AuthorizedOperation operation,
        CancellationToken cancellationToken);
}
