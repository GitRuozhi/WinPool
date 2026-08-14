using WinPool.Application;
using WinPool.Testing.Tools;

namespace WinPool.Agent;

/// <summary>
/// Pure test-plan validation and ordering rules. Keeping these rules outside
/// the Agent runtime makes the request facade independent of execution policy.
/// </summary>
internal static class TestExecutionRules
{
    internal static bool IsAcceptedToolExit(ToolId? toolId, int exitCode) =>
        ToolProcessExitPolicy.IsAccepted(toolId, exitCode);

    internal static IReadOnlyList<TestStep>? OrderStepsForExecution(
        IReadOnlyList<TestStep> steps)
    {
        var byId = steps.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (byId.Count != steps.Count
            || steps.Any(item => item.DependsOn.Any(
                dependency => !byId.ContainsKey(dependency))))
        {
            return null;
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TestStep>(steps.Count);
        while (ordered.Count < steps.Count)
        {
            var next = steps.FirstOrDefault(item =>
                !completed.Contains(item.Id)
                && item.DependsOn.All(completed.Contains));
            if (next is null)
            {
                return null;
            }

            ordered.Add(next);
            completed.Add(next.Id);
        }

        return ordered;
    }

    internal static string? ValidateSupportActions(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SupportActions.Count == 0)
        {
            return null;
        }

        if (plan.Risk != WinPool.Execution.RiskLevel.R3ControlledSystemSupport
            || plan.SupportActions.Count > 4
            || plan.SupportActions.GroupBy(item => item.Kind)
                .Any(group => group.Count() > 1)
            || plan.SupportActions.Any(item =>
                item is not TestProcessSchedulingPolicyAction
                    and not UseTemporaryPowerPlanAction
                    and not ClearSystemFileCacheAction
                    and not FlushVolumeAction))
        {
            return "agent.testing.support_actions_require_orchestration";
        }

        var policy = plan.SupportActions
            .OfType<TestProcessSchedulingPolicyAction>()
            .SingleOrDefault();
        if (policy is not null
            && (!Enum.IsDefined(policy.Priority)
                || policy.LogicalProcessorIndices.Count == 0
                || policy.LogicalProcessorIndices.Any(index =>
                    index < 0 || index >= Environment.ProcessorCount)
                || policy.LogicalProcessorIndices.Distinct().Count()
                != policy.LogicalProcessorIndices.Count))
        {
            return "agent.testing.scheduling_policy_invalid";
        }

        var ramMap = plan.SupportActions
            .OfType<ClearSystemFileCacheAction>()
            .SingleOrDefault();
        if (ramMap is not null
            && (ramMap.Mode
                    != RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList
                || ramMap.PlannedToolIdentity is not
                {
                    SignatureTrusted: true,
                    RequiresElevation: true
                } identity
                || identity.Sha256.Length != 64
                || string.IsNullOrWhiteSpace(identity.PathBindingHash)
                || string.IsNullOrWhiteSpace(identity.Version)
                || string.IsNullOrWhiteSpace(identity.Publisher)))
        {
            return "agent.testing.rammap_action_invalid";
        }

        var flush = plan.SupportActions
            .OfType<FlushVolumeAction>()
            .SingleOrDefault();
        if (flush is not null
            && (flush.VolumeId != plan.Target.VolumeId
                || flush.PlannedTarget is not { } snapshot
                || snapshot.VolumeId != flush.VolumeId
                || string.IsNullOrWhiteSpace(snapshot.StableIdentity)
                || !snapshot.StableIdentity.StartsWith(
                    @"\\?\VOLUME{",
                    StringComparison.OrdinalIgnoreCase)
                || !snapshot.StableIdentity.EndsWith('}')
                || string.IsNullOrWhiteSpace(snapshot.DisplayIdentity)
                || !Path.IsPathFullyQualified(snapshot.DisplayIdentity)
                || !plan.Steps.Any(step =>
                    step.Action == TestActionKind.Copy
                    && step.Parameters.ContainsKey("sourceRelativeDirectory")
                    && step.Parameters.ContainsKey("destinationRelativeDirectory"))))
        {
            return "agent.testing.flush_action_invalid";
        }

        return plan.SupportActions
            .OfType<UseTemporaryPowerPlanAction>()
            .Any(item => item.PowerPlanId == Guid.Empty)
                ? "agent.testing.power_plan_invalid"
                : null;
    }
}
