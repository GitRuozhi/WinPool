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
        if (plan.Targets is null || plan.Parameters is null || plan.Steps is null)
        {
            return PolicyDecision.Reject("policy.plan-invalid", "The operation plan is incomplete.");
        }

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

        string computedHash;
        try
        {
            computedHash = OperationPlanHasher.Compute(plan);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                        NotSupportedException or NullReferenceException)
        {
            return PolicyDecision.Reject("policy.plan-invalid", "The operation plan cannot be validated.");
        }
        if (string.IsNullOrWhiteSpace(plan.PlanHash) ||
            !StringComparer.Ordinal.Equals(plan.PlanHash, computedHash))
        {
            return PolicyDecision.Reject("policy.plan-hash-invalid", "The operation plan changed after it was created.");
        }

        OperationSecurityDefinition definition;
        try
        {
            definition = OperationSecurityCatalog.Get(plan.Intent);
        }
        catch (ArgumentOutOfRangeException)
        {
            return PolicyDecision.Reject("policy.unknown-intent", "The operation intent is not supported.");
        }

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

        if (definition.MinimumRisk >= RiskLevel.R4StorageStructureMutation)
        {
            if (plan.Intent is OperationIntent.RepairStorageObject or OperationIntent.RawDeviceWrite ||
                plan.Risk > RiskLevel.R5IrreversibleOrBroadDestruction ||
                (plan.Risk >= RiskLevel.R5IrreversibleOrBroadDestruction &&
                 plan.Intent is not (OperationIntent.ConvertDisk or OperationIntent.ClearDisk or
                     OperationIntent.DeleteStoragePool or OperationIntent.RebuildStoragePool or
                     OperationIntent.DeleteVirtualDisk or OperationIntent.DeletePartition)))
            {
                return PolicyDecision.Reject("policy.real-operation-not-listed", "This real storage operation is outside the closed stage-one list.");
            }

            if (context.Environment.Kind != WinPool.Domain.EnvironmentKind.LocalMachine ||
                context.Environment.IsUserProvidedDisposableEnvironment)
            {
                return PolicyDecision.Reject("policy.local-machine-required", "Real storage mutation requires a verified local-machine environment.");
            }

            if (context.Mode != WinPool.Domain.ExecutionMode.Real ||
                context.Privilege != WinPool.Domain.PrivilegeState.Administrator ||
                context.RealSession is not { IsArmed: true, IsWellFormed: true } session)
            {
                return PolicyDecision.Reject("policy.real-session-required", "An armed administrator real session is required.");
            }

            if (!RealOperationValidator.IsValid(plan))
            {
                return PolicyDecision.Reject("policy.real-plan-invalid", "The plan lacks valid closed real storage steps.");
            }

            if (!StringComparer.Ordinal.Equals(plan.RealOperation!.MachineBinding, context.CurrentMachineBinding) ||
                !StringComparer.Ordinal.Equals(plan.RealOperation.SessionBinding, session.Binding) ||
                !StringComparer.Ordinal.Equals(plan.RealOperation.TargetFingerprint, context.CurrentTargetFingerprint) ||
                !StringComparer.Ordinal.Equals(plan.RealOperation.PhysicalMemberFingerprint, context.CurrentPhysicalMemberFingerprint))
            {
                return PolicyDecision.Reject("policy.real-binding-mismatch", "The machine, session or target facts changed after planning.");
            }

            return PolicyDecision.Confirm("policy.real-confirmation-required", "Confirm the exact prepared real storage plan and its data loss.");
        }

        if (context.IsReleaseBuild &&
            plan.Risk is RiskLevel.R2RecoverableFileWrite or RiskLevel.R3ControlledSystemSupport)
        {
            return PolicyDecision.Confirm("policy.release-confirmation", "This operation requires a warning or confirmation in release builds.");
        }

        return PolicyDecision.Allow();
    }
}
