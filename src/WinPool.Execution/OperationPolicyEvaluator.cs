namespace WinPool.Execution;

public interface IOperationPolicyEvaluator
{
    Task<PolicyDecision> EvaluateAsync(
        OperationPlan plan,
        ExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class OperationPolicyEvaluator : IOperationPolicyEvaluator
{
    public Task<PolicyDecision> EvaluateAsync(
        OperationPlan plan,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(Evaluate(plan, context));
    }

    public PolicyDecision Evaluate(OperationPlan plan, ExecutionContext context)
    {
        if (plan.EnvironmentId != context.Environment.Id)
        {
            return PolicyDecision.Reject("policy.environment-mismatch", "The plan belongs to another environment.");
        }

        if (string.IsNullOrWhiteSpace(context.CurrentMachineBinding) ||
            !StringComparer.Ordinal.Equals(context.Environment.MachineBinding, context.CurrentMachineBinding))
        {
            return PolicyDecision.Reject("policy.machine-mismatch", "The current machine does not match the environment binding.");
        }

        if (!StringComparer.Ordinal.Equals(plan.InventoryVersion, context.CurrentInventoryVersion))
        {
            return PolicyDecision.Reject("policy.inventory-changed", "The target inventory changed after planning.");
        }

        if (plan.Targets.Any(target => target.System != plan.SystemId))
        {
            return PolicyDecision.Reject("policy.target-system-mismatch", "A target does not belong to the planned system.");
        }

        var computedHash = OperationPlanHasher.Compute(plan);
        if (string.IsNullOrWhiteSpace(plan.PlanHash) ||
            !StringComparer.Ordinal.Equals(plan.PlanHash, computedHash))
        {
            return PolicyDecision.Reject("policy.plan-hash-invalid", "The operation plan changed after it was created.");
        }

        var definition = OperationSecurityCatalog.Get(plan.Intent);

        // This check deliberately precedes risk, capability and privilege handling.
        // Real mode, administrator elevation, or forged plan metadata can never
        // turn a protected-machine storage mutation into an approvable plan.
        if (context.Environment.Kind == WinPool.Domain.EnvironmentKind.ProtectedDevelopmentMachine &&
            definition.MinimumRisk >= RiskLevel.R4StorageStructureMutation)
        {
            return PolicyDecision.Reject(
                "policy.protected-machine-storage-mutation",
                "Real storage-structure mutation is forbidden on the protected development machine.");
        }

        if (plan.Risk < definition.MinimumRisk)
        {
            return PolicyDecision.Reject("policy.risk-downgrade", "The plan risk is below the minimum risk for this operation.");
        }

        if ((plan.RequiredCapabilities & definition.RequiredCapabilities) != definition.RequiredCapabilities)
        {
            return PolicyDecision.Reject("policy.capability-omitted", "The plan omitted a capability required by the operation.");
        }

        if ((context.Environment.AllowedCapabilities & plan.RequiredCapabilities) != plan.RequiredCapabilities)
        {
            return PolicyDecision.Reject("policy.capability-denied", "The environment has not granted every capability required by the plan.");
        }

        if (plan.Intent == OperationIntent.SimulateStorageMutation &&
            context.Environment.Kind != WinPool.Domain.EnvironmentKind.Simulation)
        {
            return PolicyDecision.Reject("policy.simulation-environment-required", "Simulation mutation requires a simulation environment.");
        }

        if (plan.Intent == OperationIntent.ReplayHistoricalEvents &&
            context.Environment.Kind != WinPool.Domain.EnvironmentKind.Replay)
        {
            return PolicyDecision.Reject("policy.replay-environment-required", "Historical event replay requires a replay environment.");
        }

        if (plan.Risk >= RiskLevel.R5IrreversibleOrBroadDestruction)
        {
            return PolicyDecision.Reject("policy.r5-not-implemented", "R5 operations are not implemented in the current WinPool release.");
        }

        if (context.IsReleaseBuild &&
            plan.Risk is RiskLevel.R2RecoverableFileWrite or RiskLevel.R3ControlledSystemSupport)
        {
            return PolicyDecision.Confirm("policy.release-confirmation", "This operation requires a warning or confirmation in release builds.");
        }

        return PolicyDecision.Allow();
    }
}
