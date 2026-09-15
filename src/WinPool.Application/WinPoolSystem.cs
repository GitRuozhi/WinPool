using System.Collections.Immutable;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>A read-only view over current facts; source details preserve ownership and original values.</summary>
public class WinPoolObject
{
    internal WinPoolObject(WinPoolSourceObject primary, ImmutableArray<WinPoolSourceObject> sources)
    {
        Primary = primary;
        Sources = sources;
    }
    public string Id => Primary.Id;
    public FactObjectType ObjectType => Primary.ObjectType;
    public WinPoolSourceObject Primary { get; }
    public ImmutableArray<WinPoolSourceObject> Sources { get; }
    public string DisplayName => Primary.Field("FriendlyName")?.DisplayValue()
        ?? Primary.Field("Name")?.DisplayValue() ?? Primary.ObjectType.ToString();
    public WinPoolSourceField? Field(string name) => WinPoolSourceDetails.Select(this, name).Value;
}

public sealed class WinPoolDisk : WinPoolObject
{
    internal WinPoolDisk(WinPoolSourceObject primary, ImmutableArray<WinPoolSourceObject> sources) : base(primary, sources) { }
    public bool IsVirtual => ObjectType == FactObjectType.VirtualDisk;
}
public sealed class WinPoolStoragePool : WinPoolObject
{
    internal WinPoolStoragePool(WinPoolSourceObject primary) : base(primary, [primary]) { }
}
public sealed class WinPoolStorageTier : WinPoolObject
{
    internal WinPoolStorageTier(WinPoolSourceObject primary) : base(primary, [primary]) { }
}
public sealed class WinPoolPartition : WinPoolObject
{
    internal WinPoolPartition(WinPoolSourceObject primary) : base(primary, [primary]) { }
}
public sealed class WinPoolVolume : WinPoolObject
{
    internal WinPoolVolume(WinPoolSourceObject primary, ImmutableArray<WinPoolSourceObject> sources) : base(primary, sources) { }
}
public sealed class WinPoolProcessor : WinPoolObject
{
    internal WinPoolProcessor(WinPoolSourceObject primary) : base(primary, [primary]) { }
}
public sealed record WinPoolDisplayGroup(string Kind, string? PoolId, ImmutableArray<string> MemberIds);

public sealed class WinPoolSystem
{
    public const int ProjectionVersion = 1;
    public SystemId SystemId { get; }
    public long Revision { get; }
    public ImmutableArray<WinPoolObject> Objects { get; }
    public ImmutableArray<WinPoolDisplayGroup> DisplayGroups { get; }
    public ImmutableArray<WinPoolSource> Sources { get; }
    public ImmutableArray<WinPoolCollectionState> Collections { get; }
    public string ProcessorNames => string.Join("; ", Objects.Where(x => x.ObjectType == FactObjectType.Processor)
        .Select(x => x.Field("Name")).Where(x => x is { ReadState: FieldReadState.Returned })
        .Select(x => x!.DisplayValue()).Where(x => x.Length > 0));

    public ulong? TotalMemoryBytes
    {
        get
        {
            var modules = Objects.Where(x => x.ObjectType == FactObjectType.MemoryModule).ToArray();
            if (modules.Length == 0) return null;
            ulong total = 0;
            foreach (var module in modules)
            {
                if (module.Field("Capacity") is not { ReadState: FieldReadState.Returned, Value: { } value }
                    || value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetUInt64(out var capacity)
                    || ulong.MaxValue - total < capacity) return null;
                total += capacity;
            }
            return total;
        }
    }

    public WinPoolSystem(WinPoolFacts facts)
    {
        facts.Validate();
        SystemId = facts.SystemId;
        Revision = facts.Revision;
        Sources = facts.Sources;
        Collections = facts.Collections;
        var objects = facts.Objects.ToDictionary(x => x.Id);
        var representedOsDisks = new HashSet<string>();
        var result = ImmutableArray.CreateBuilder<WinPoolObject>();
        foreach (var primary in facts.Objects.Where(x => x.ObjectType is FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk))
        {
            var views = facts.Relationships.Where(x => x.FromId == primary.Id && x.Kind == "same-device")
                .Select(x => objects[x.ToId]).Where(x => x.ObjectType == FactObjectType.Disk).ToArray();
            foreach (var view in views) representedOsDisks.Add(view.Id);
            result.Add(new WinPoolDisk(primary, new[] { primary }.Concat(views).ToImmutableArray()));
        }
        foreach (var item in facts.Objects.Where(x => x.ObjectType is not (FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk)))
        {
            if (representedOsDisks.Contains(item.Id)) continue;
            if (item.ObjectType == FactObjectType.LogicalDisk && facts.Relationships.Any(x => x.Kind == "same-volume" && x.ToId == item.Id)) continue;
            result.Add(item.ObjectType switch
            {
                FactObjectType.Disk => new WinPoolDisk(item, [item]),
                FactObjectType.StoragePool => new WinPoolStoragePool(item),
                FactObjectType.StorageTier => new WinPoolStorageTier(item),
                FactObjectType.Partition => new WinPoolPartition(item),
                FactObjectType.Volume => new WinPoolVolume(item, new[] { item }.Concat(facts.Relationships
                    .Where(x => x.Kind == "same-volume" && x.FromId == item.Id).Select(x => objects[x.ToId])).ToImmutableArray()),
                FactObjectType.Processor => new WinPoolProcessor(item),
                _ => new WinPoolObject(item, [item])
            });
        }
        Objects = result.ToImmutable();
        // Only known Usage and known membership can create a group. Missing evidence is not "unallocated".
        DisplayGroups = facts.Objects.Where(x => x.ObjectType == FactObjectType.PhysicalDisk)
            .Select(x => (Item: x, Usage: x.Field("Usage"), Pool: facts.Relationships
                .FirstOrDefault(r => r.ToId == x.Id && r.Kind == "pool-member")?.FromId))
            .Where(x => x.Usage is { ReadState: FieldReadState.Returned } && x.Pool is not null)
            .Select(x => (x.Item.Id, x.Pool, Kind: x.Usage!.DisplayValue() switch
            {
                "HotSpare" or "3" => "HotSpare",
                "Retired" or "4" => "Retired",
                _ => string.Empty
            }))
            .Where(x => x.Kind.Length > 0)
            .GroupBy(x => (x.Kind, x.Pool))
            .Select(x => new WinPoolDisplayGroup(x.Key.Kind, x.Key.Pool, x.Select(y => y.Id).ToImmutableArray()))
            .ToImmutableArray();
    }
}

/// <summary>Per-source replacement prevents a partial/failed collection from deleting cached devices.</summary>
public static class WinPoolFactRefresh
{
    public static WinPoolFacts Merge(WinPoolFacts current, WinPoolFacts incoming)
    {
        current.Validate();
        incoming.Validate();
        if (current.SystemId != incoming.SystemId) throw new InvalidOperationException("Collection belongs to another system.");
        if (current.IsSimulation || current.Sources.Any(x => x.Origin == FactOrigin.Simulation))
            throw new InvalidOperationException("A live collection cannot refresh a simulation or imported system.");
        static (string, string) Key(WinPoolSource source) => (source.Namespace, source.ClassName);
        var oldByKey = current.Sources.GroupBy(Key).ToDictionary(x => x.Key, x => x.Max(s => s.CapturedAt));
        var accepted = incoming.Sources.Where(x => !oldByKey.TryGetValue(Key(x), out var time) || x.CapturedAt > time).ToArray();
        if (accepted.Length == 0) return current;
        var replaceKeys = accepted.Where(x => x.ReadState == FieldReadState.Returned).Select(Key).ToHashSet();
        var removedSourceIds = current.Sources.Where(x => replaceKeys.Contains(Key(x))).Select(x => x.Id).ToHashSet();
        var acceptedSourceIds = accepted.Where(x => x.ReadState == FieldReadState.Returned).Select(x => x.Id).ToHashSet();
        var replacedObjectIds = current.Objects.Where(x => removedSourceIds.Contains(x.SourceRef)).Select(x => x.Id).ToHashSet();
        var acceptedObjectIds = incoming.Objects.Where(x => acceptedSourceIds.Contains(x.SourceRef)).Select(x => x.Id).ToHashSet();
        var staleObjectIds = incoming.Objects.Where(x => !acceptedSourceIds.Contains(x.SourceRef)).Select(x => x.Id).ToHashSet();
        var objects = current.Objects.Where(x => !removedSourceIds.Contains(x.SourceRef))
            .Concat(incoming.Objects.Where(x => acceptedSourceIds.Contains(x.SourceRef))).ToImmutableArray();
        var objectIds = objects.Select(x => x.Id).ToHashSet();
        var sources = current.Sources.Where(x => !removedSourceIds.Contains(x.Id)).Concat(accepted)
            .DistinctBy(x => x.Id).ToImmutableArray();
        var acceptedRelations = incoming.Relationships.Where(x =>
            (acceptedObjectIds.Contains(x.FromId) || acceptedObjectIds.Contains(x.ToId))
            && !staleObjectIds.Contains(x.FromId) && !staleObjectIds.Contains(x.ToId)).ToArray();
        var oldObjects = current.Objects.ToDictionary(x => x.Id);
        var oldSources = current.Sources.ToDictionary(x => x.Id);
        var retainedRelations = current.Relationships
            .Where(x => !(replacedObjectIds.Contains(x.FromId) && replacedObjectIds.Contains(x.ToId)))
            .Where(x => !acceptedRelations.Any(y => y.FromId == x.FromId && y.ToId == x.ToId && y.Kind == x.Kind))
            .Select(x => replacedObjectIds.Contains(x.FromId) || replacedObjectIds.Contains(x.ToId)
                ? x with { IsRetained = true, ReasonCode = "PartialCollection",
                    ObservedAt = x.ObservedAt ?? oldSources[oldObjects[x.ToId].SourceRef].CapturedAt }
                : x);
        var merged = current with
        {
            Revision = checked(current.Revision + 1), Sources = sources, Objects = objects,
            InventoryVersion = incoming.InventoryCapturedAt > current.InventoryCapturedAt ? incoming.InventoryVersion : current.InventoryVersion,
            InventoryCapturedAt = incoming.InventoryCapturedAt > current.InventoryCapturedAt ? incoming.InventoryCapturedAt : current.InventoryCapturedAt,
            Relationships = retainedRelations.Concat(acceptedRelations)
                .Where(x => objectIds.Contains(x.FromId) && objectIds.Contains(x.ToId))
                .DistinctBy(x => (x.FromId, x.ToId, x.Kind)).ToImmutableArray(),
            Identities = current.Identities.Concat(incoming.Identities)
                .DistinctBy(x => (x.ObjectType, x.SourceIdentity)).ToImmutableArray(),
            Collections = current.Collections.Concat(incoming.Collections).GroupBy(x => x.Purpose)
                .Select(x => x.MaxBy(y => y.StartedAt)!).ToImmutableArray()
        };
        merged.Validate();
        return merged;
    }
}
