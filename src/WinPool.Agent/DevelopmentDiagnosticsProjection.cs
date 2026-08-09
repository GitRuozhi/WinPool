using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Testing;

namespace WinPool.Agent;

internal static class DevelopmentDiagnosticsProjection
{
    public static DevelopmentPlanDiagnostic ProjectPlan(
        PersistedTestRun run,
        TestPlan plan,
        IReadOnlyList<PersistedTestStep> persistedSteps)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(persistedSteps);
        var states = persistedSteps.ToDictionary(
            item => item.StepId,
            StringComparer.Ordinal);
        return new DevelopmentPlanDiagnostic(
            run.RunId,
            run.State.ToString(),
            run.PlanHash,
            plan.PlannerAlgorithm,
            plan.CreatedAtUtc,
            plan.Steps.Select(step => new DevelopmentStepDiagnostic(
                    step.Id,
                    step.Action.ToString(),
                    states.TryGetValue(step.Id, out var persisted)
                        ? persisted.State.ToString()
                        : ApplicationTaskState.Created.ToString(),
                    step.ToolId?.Value,
                    step.DependsOn.ToArray(),
                    step.Parameters.Keys
                        .OrderBy(key => key, StringComparer.Ordinal)
                        .ToArray()))
                .ToArray());
    }

    public static IReadOnlyList<AlgorithmIdentity> Algorithms(
        IEnumerable<DevelopmentPlanDiagnostic> plans) =>
    [
        .. KnownAlgorithms()
            .Concat(plans.Select(item => item.PlannerAlgorithm))
            .GroupBy(
                item => $"{item.Id}\u001f{item.Version}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
    ];

    private static IReadOnlyList<AlgorithmIdentity> KnownAlgorithms() =>
    [
        StorageMath.CapacityAlgorithm,
        StorageMath.AlignmentAlgorithm,
        StorageMath.PercentageAlgorithm,
        TheoreticalPoolCapacity.Algorithm,
        DefaultOperationPlanner.Algorithm,
        TestPlanCompiler.Algorithm,
        TestMetrics.ThroughputAlgorithm,
        TestMetrics.LatencyAlgorithm,
        TestMetrics.RepeatAlgorithm,
        TestMetricSemanticsCatalog.Algorithm,
        CopyBatchPlanner.Algorithm,
        RegisteredTestFileExecutor.PatternAlgorithm
    ];
}
