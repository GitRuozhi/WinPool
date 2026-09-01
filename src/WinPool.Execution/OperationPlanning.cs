using WinPool.Domain;

namespace WinPool.Execution;

public interface IOperationPlanner
{
    Task<OperationPlan> BuildAsync(
        OperationRequest request,
        CancellationToken cancellationToken);
}

public interface IOperationInventoryVersionSource
{
    Task<string> GetCurrentVersionAsync(
        EnvironmentId environmentId,
        SystemId systemId,
        CancellationToken cancellationToken);
}

public sealed class FixedOperationInventoryVersionSource(string inventoryVersion)
    : IOperationInventoryVersionSource
{
    private readonly string _inventoryVersion =
        !string.IsNullOrWhiteSpace(inventoryVersion)
            ? inventoryVersion
            : throw new ArgumentException("An inventory version is required.", nameof(inventoryVersion));

    public Task<string> GetCurrentVersionAsync(
        EnvironmentId environmentId,
        SystemId systemId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_inventoryVersion);
    }
}

public sealed class DefaultOperationPlanner(IOperationInventoryVersionSource inventoryVersions)
    : IOperationPlanner
{
    public static readonly AlgorithmIdentity Algorithm =
        new("ALGO-EXEC-PLAN-001", "1.0.0", AlgorithmConfidence.Derived, "docs/Archive/V0.2/03_执行器端口与安全模型.md");

    public async Task<OperationPlan> BuildAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var inventoryVersion = await inventoryVersions
            .GetCurrentVersionAsync(request.EnvironmentId, request.SystemId, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(inventoryVersion))
        {
            throw new InvalidOperationException("The inventory source returned an empty version.");
        }

        var definition = OperationSecurityCatalog.Get(request.Intent);
        var estimatedWriteBytes = ParseEstimatedWriteBytes(request.Parameters);
        var preconditions = BuildPreconditions(request, definition);
        var steps = BuildSteps(request, definition);

        return OperationPlan.Create(
            request,
            definition.RequiredCapabilities,
            definition.MinimumRisk,
            inventoryVersion,
            preconditions,
            steps,
            estimatedWriteBytes,
            definition.ImpactScope,
            definition.RollbackDescription,
            definition.IrreversibleEffects,
            Algorithm,
            DateTimeOffset.UtcNow);
    }

    private static void ValidateRequest(OperationRequest request)
    {
        if (request.Id.Value == Guid.Empty)
        {
            throw new ArgumentException("The operation id is required.", nameof(request));
        }

        if (request.EnvironmentId.Value == Guid.Empty || request.SystemId.Value == Guid.Empty)
        {
            throw new ArgumentException("The environment and system identities are required.", nameof(request));
        }

        if (request.Targets is null || request.Parameters is null)
        {
            throw new ArgumentException("Targets and parameters must be supplied.", nameof(request));
        }

        if (request.Targets.Any(target => target.System != request.SystemId))
        {
            throw new ArgumentException("Every target must belong to the request system.", nameof(request));
        }
    }

    private static long? ParseEstimatedWriteBytes(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("EstimatedWriteBytes", out var text))
        {
            return null;
        }

        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            value < 0)
        {
            throw new ArgumentException("EstimatedWriteBytes must be a non-negative integer.", nameof(parameters));
        }

        return value;
    }

    private static IReadOnlyList<string> BuildPreconditions(
        OperationRequest request,
        OperationSecurityDefinition definition)
    {
        var preconditions = new List<string>
        {
            "Refresh inventory and match every target by stable identity.",
            "Re-evaluate policy and capabilities immediately before execution."
        };

        if (OperationSecurityCatalog.IsStorageStructureMutation(request.Intent))
        {
            preconditions.Add("A production local-storage mutation executor is intentionally unavailable in the current WinPool release.");
        }

        return preconditions;
    }

    private static IReadOnlyList<PlanStep> BuildSteps(
        OperationRequest request,
        OperationSecurityDefinition definition)
    {
        var steps = new List<PlanStep>
        {
            new("refresh-inventory", "Refresh target inventory and compare stable identities.", []),
            new("revalidate", "Revalidate policy, capabilities, plan hash, inventory and authorization.", ["refresh-inventory"], true),
            new("execute", $"Execute the typed {request.Intent} adapter.", ["revalidate"], true),
            new("verify", "Verify post-operation state and record an audit event.", ["execute"])
        };

        return steps;
    }
}
