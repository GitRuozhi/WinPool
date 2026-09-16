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
    public virtual FactObjectType ObjectType => Primary.ObjectType;
    public WinPoolSourceObject Primary { get; }
    public ImmutableArray<WinPoolSourceObject> Sources { get; }
    public virtual string DisplayName => Primary.Field("FriendlyName")?.DisplayValue()
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
    internal WinPoolPartition(WinPoolSourceObject primary, ImmutableArray<WinPoolSourceObject> sources) : base(primary, sources) { }
    public override FactObjectType ObjectType => FactObjectType.Partition;
    public override string DisplayName => Field("DriveLetter")?.DisplayValue() is { Length: > 0 } letter
        ? letter.TrimEnd(':') + ": " + (Field("FileSystemLabel")?.DisplayValue() ?? "")
        : Field("FileSystemLabel")?.DisplayValue() is { Length: > 0 } label ? label : base.DisplayName;
}
public sealed class WinPoolProcessor : WinPoolObject
{
    internal WinPoolProcessor(WinPoolSourceObject primary) : base(primary, [primary]) { }
}
public sealed record WinPoolDisplayGroup(string Kind, string? PoolId, ImmutableArray<string> MemberIds);

public sealed class WinPoolSystem
{
    public const int ProjectionVersion = 2;
    private readonly Lazy<ImmutableArray<SyntheticStorageObject>> _syntheticStorageObjects;
    public WinPoolObject? Resolve(string sourceId) => Objects.FirstOrDefault(x => x.Id == sourceId || x.Sources.Any(s => s.Id == sourceId));
    public SystemId SystemId { get; }
    public long Revision { get; }
    public ImmutableArray<WinPoolObject> Objects { get; }
    public ImmutableArray<WinPoolDisplayGroup> DisplayGroups => SyntheticStorageObjects
        .Where(item => item.Kind == SyntheticStorageObjectKind.Tier
            && item.Name is SyntheticStorageName.HotSpareLayer or SyntheticStorageName.RetiredLayer)
        .Select(item => new WinPoolDisplayGroup(
            item.Name == SyntheticStorageName.HotSpareLayer ? "HotSpare" : "Retired",
            item.ParentStableId,
            item.MemberStableIds.ToImmutableArray()))
        .ToImmutableArray();
    /// <summary>
    /// Source-less pool and tier containers owned by the unified projection.
    /// They are deliberately separate from <see cref="Objects"/>, whose
    /// entries always retain a real source observation.
    /// </summary>
    public ImmutableArray<SyntheticStorageObject> SyntheticStorageObjects => _syntheticStorageObjects.Value;
    public SyntheticStorageObject? FindSyntheticStorageObject(string? stableId) =>
        string.IsNullOrWhiteSpace(stableId)
            ? null
            : SyntheticStorageObjects.FirstOrDefault(item =>
                item.StableId.Equals(stableId, StringComparison.OrdinalIgnoreCase));
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
        _syntheticStorageObjects = new Lazy<ImmutableArray<SyntheticStorageObject>>(
            () => WinPoolStorageProjection.Project(facts).GetSyntheticStorageObjects().ToImmutableArray());
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
            var supplements = facts.Relationships.Where(x => x.Kind == "disk-supplement" && views.Any(d => d.Id == x.FromId))
                .Concat(facts.Relationships.Where(x => x.Kind == "disk-supplement" && x.FromId == primary.Id))
                .Select(x => objects[x.ToId]).DistinctBy(x => x.Id).ToArray();
            foreach (var supplement in supplements) representedOsDisks.Add(supplement.Id);
            result.Add(new WinPoolDisk(primary, new[] { primary }.Concat(views).Concat(supplements).ToImmutableArray()));
        }
        foreach (var relation in facts.Relationships.Where(x => x.Kind == "disk-supplement"))
            representedOsDisks.Add(relation.ToId);
        var represented = new HashSet<string>();
        bool IsPartitionSource(WinPoolSourceObject x) => x.ObjectType is FactObjectType.Partition or FactObjectType.Volume or FactObjectType.LogicalDisk or FactObjectType.NetworkDisk;
        foreach (var primary in facts.Objects.Where(IsPartitionSource).OrderBy(x => x.ObjectType switch
                 { FactObjectType.Partition => 0, FactObjectType.Volume => 1, _ => 2 }))
        {
            if (represented.Contains(primary.Id)) continue;
            var members = new List<WinPoolSourceObject> { primary };
            // Only unambiguous observed associations join a union. Never infer one from a label.
            void Attach(string from, string kind)
            {
                var edges = facts.Relationships.Where(r => r.FromId == from && r.Kind == kind).ToArray();
                if (edges.Length != 1) return;
                var edge = edges[0];
                if (facts.Relationships.Count(r => r.ToId == edge.ToId && r.Kind == kind) != 1 || represented.Contains(edge.ToId)) return;
                members.Add(objects[edge.ToId]);
            }
            if (primary.ObjectType == FactObjectType.Partition) Attach(primary.Id, "partition-volume");
            foreach (var volume in members.Where(x => x.ObjectType == FactObjectType.Volume).ToArray()) Attach(volume.Id, "same-volume");
            foreach (var member in members) represented.Add(member.Id);
            result.Add(new WinPoolPartition(primary, members.ToImmutableArray()));
        }
        foreach (var item in facts.Objects.Where(x => x.ObjectType is not (FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk)))
        {
            if (representedOsDisks.Contains(item.Id) || represented.Contains(item.Id)) continue;
            result.Add(item.ObjectType switch
            {
                FactObjectType.Disk => new WinPoolDisk(item, new[] { item }.Concat(facts.Relationships
                    .Where(r => r.Kind == "disk-supplement" && r.FromId == item.Id).Select(r => objects[r.ToId])).ToImmutableArray()),
                FactObjectType.StoragePool => new WinPoolStoragePool(item),
                FactObjectType.StorageTier => new WinPoolStorageTier(item),
                FactObjectType.Processor => new WinPoolProcessor(item),
                _ => new WinPoolObject(item, [item])
            });
        }
        Objects = result.ToImmutable();
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
        var usedSources = objects.Select(x => x.SourceRef).Concat(objects.SelectMany(x => x.Fields.Select(f => f.SourceRef))).ToHashSet();
        var latestSources = sources.GroupBy(Key).Select(x => x.MaxBy(s => s.CapturedAt)!.Id).ToHashSet();
        sources = sources.Where(x => usedSources.Contains(x.Id) || latestSources.Contains(x.Id)).ToImmutableArray();
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
