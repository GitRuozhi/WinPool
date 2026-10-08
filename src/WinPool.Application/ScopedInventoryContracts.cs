using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Provider identity, never a disk number, name or mount path. Before locators remain usable after deletion.</summary>
public sealed record StorageInventoryLocator(StorageObjectId Target, string ClassName,
    string IdentityProperty, string IdentityValue);

public sealed record StorageInventoryScope(SystemId SystemId, OperationId OperationId, string? StepId,
    IReadOnlyList<StorageObjectId> Targets, IReadOnlyList<string> BeforeObjectIds,
    IReadOnlyList<StorageInventoryLocator> BeforeLocators, long Generation = 0)
{
    public string Key => OperationId.Value.ToString("N") + ":" + (StepId ?? "result");

    public void Validate()
    {
        if (SystemId.Value == Guid.Empty || OperationId.Value == Guid.Empty || Generation < 0
            || Targets is null || Targets.Count == 0 || BeforeObjectIds is null || BeforeLocators is null
            || Targets.Distinct().Count() != Targets.Count || BeforeLocators.Count != Targets.Count
            || Targets.Any(x => x.System != SystemId || string.IsNullOrWhiteSpace(x.ProviderKey))
            || BeforeObjectIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("An inventory scope requires exact system and operation targets.");
        foreach (var locator in BeforeLocators)
            if (!Targets.Contains(locator.Target) || BeforeLocators.Count(x => x.Target == locator.Target) != 1
                || StorageInventoryScopeFactory.ClassFor(locator.Target.Kind) != locator.ClassName
                || locator.IdentityProperty is not ("UniqueId" or "ObjectId" or "Guid")
                || string.IsNullOrWhiteSpace(locator.IdentityValue))
                throw new InvalidDataException("The inventory scope contains an invalid provider locator.");
    }
}

/// <summary>Coverage is a deletion domain, not a claim that the rest of the machine was refreshed.</summary>
public sealed record InventorySourceCoverage(string ScopeKey, ImmutableArray<string> ObjectIds,
    bool Complete, long Generation = 0);

public sealed record ScopedInventoryCollection(StorageInventoryScope Scope, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, bool Complete, string? ReasonCode = null);

public sealed record CaptureAgentManageScopedInventoryRequest(StorageInventoryScope Scope, CorrelationId CorrelationId)
    : AgentRequest(CorrelationId);

public interface IScopedHardwareInventoryProvider
{
    Task<StorageSystemDocument> CollectScopedAsync(StorageInventoryScope scope, CancellationToken cancellationToken);
}

public interface IScopedInventoryCommandRunner
{
    Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken);
}

/// <summary>Builds query selectors from authoritative source facts. Cached observations only locate fresh queries.</summary>
public static class StorageInventoryScopeFactory
{
    internal static readonly Dictionary<FactObjectType, string> Classes = new()
    {
        [FactObjectType.PhysicalDisk] = "MSFT_PhysicalDisk", [FactObjectType.StoragePool] = "MSFT_StoragePool",
        [FactObjectType.VirtualDisk] = "MSFT_VirtualDisk", [FactObjectType.StorageTier] = "MSFT_StorageTier",
        [FactObjectType.Disk] = "MSFT_Disk", [FactObjectType.Partition] = "MSFT_Partition", [FactObjectType.Volume] = "MSFT_Volume"
    };

    internal static string? ClassFor(StorageObjectKind kind) => kind switch
    {
        StorageObjectKind.PhysicalDisk => "MSFT_PhysicalDisk", StorageObjectKind.StoragePool => "MSFT_StoragePool",
        StorageObjectKind.VirtualDisk => "MSFT_VirtualDisk", StorageObjectKind.StorageTier => "MSFT_StorageTier",
        StorageObjectKind.OsDisk => "MSFT_Disk", StorageObjectKind.Partition => "MSFT_Partition", StorageObjectKind.Volume => "MSFT_Volume",
        _ => null
    };

    public static StorageInventoryScope Create(WinPoolFacts facts, OperationId operationId, string? stepId,
        IReadOnlyList<StorageObjectId> targets, long generation = 0, bool includeRetainedRelationships = false)
    {
        facts.Validate();
        var locators = targets.Select(target => Resolve(facts, target)).ToArray();
        var objects = facts.Objects.ToDictionary(x => x.Id, StringComparer.Ordinal);
        bool Core(string id) => objects.TryGetValue(id, out var value) && Classes.ContainsKey(value.ObjectType)
            && !(value.ObjectType == FactObjectType.StoragePool && value.Field("IsPrimordial")?.DisplayValue() == "true");
        var visited = new HashSet<string>(targets.Select(x => x.ProviderKey), StringComparer.Ordinal);
        var pending = new Queue<string>(visited);
        while (pending.TryDequeue(out var id))
            foreach (var edge in facts.Relationships.Where(x => (includeRetainedRelationships || !x.IsRetained) && (x.FromId == id || x.ToId == id)))
            {
                var next = edge.FromId == id ? edge.ToId : edge.FromId;
                if (Core(next) && visited.Add(next)) pending.Enqueue(next);
            }
        // Supplements are display facts belonging to the selected disks; never traverse them to another device.
        foreach (var edge in facts.Relationships.Where(x => x.Kind == "disk-supplement" && visited.Contains(x.FromId)))
            visited.Add(edge.ToId);
        var scope = new StorageInventoryScope(facts.SystemId, operationId, stepId, targets,
            visited.Order(StringComparer.Ordinal).ToArray(), locators, generation);
        scope.Validate();
        return scope;
    }

    public static StorageInventoryLocator Resolve(WinPoolFacts facts, StorageObjectId target)
    {
        if (target.System != facts.SystemId || facts.Objects.SingleOrDefault(x => x.Id == target.ProviderKey) is not { HasReliableIdentity: true } item
            || !Classes.TryGetValue(item.ObjectType, out var className) || ClassFor(target.Kind) != className)
            throw new InvalidDataException("The inventory target has no authoritative provider identity.");
        foreach (var property in item.ObjectType == FactObjectType.Partition ? new[] { "Guid", "ObjectId" } : new[] { "UniqueId", "ObjectId" })
            if (item.Field(property) is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } value }
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return new(target, className, property, value.GetString()!);
        throw new InvalidDataException("The inventory target lacks an exact provider query locator.");
    }
}
