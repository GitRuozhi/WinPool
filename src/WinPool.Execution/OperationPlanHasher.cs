using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinPool.Execution;

public static class OperationPlanHasher
{
    public static string Compute(OperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = new
        {
            plan.OperationId,
            plan.EnvironmentId,
            plan.SystemId,
            plan.Intent,
            Targets = plan.Targets
                .Select(target => new { target.System, target.Kind, target.ProviderKey })
                .OrderBy(target => target.System.Value)
                .ThenBy(target => target.Kind)
                .ThenBy(target => target.ProviderKey, StringComparer.Ordinal),
            Parameters = plan.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            plan.RequiredCapabilities,
            plan.Risk,
            plan.InventoryVersion,
            plan.Preconditions,
            Steps = plan.Steps.Select(step => new
            {
                step.Id,
                step.Action,
                DependsOn = step.DependsOn.Order(StringComparer.Ordinal),
                step.IsCancellationBoundary
            }),
            plan.EstimatedWriteBytes,
            plan.ImpactScope,
            plan.RollbackDescription,
            plan.IrreversibleEffects,
            plan.PlannerAlgorithm,
            plan.CreatedAt
        };

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
