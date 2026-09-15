using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Publishes a validated simulation candidate as facts. It never changes a live capture.</summary>
public static class WinPoolSimulationFacts
{
    private static readonly HashSet<string> NonFields = new(StringComparer.Ordinal)
    {
        "StableId", "PoolStableId", "SubsystemStableId", "VirtualDiskStableId", "PhysicalDiskStableId",
        "OsDiskStableId", "PartitionStableId", "MemberPhysicalDiskIds", "TierStableIds", "OsDiskNumbers",
        "IsRetired", "IsHotSpare"
    };

    public static WinPoolFacts Create(StorageSnapshot snapshot, SystemId systemId) =>
        ApplyCandidate(null, snapshot, snapshot, systemId);

    public static WinPoolFacts ApplyCandidate(WinPoolFacts? previous, StorageSnapshot before, StorageSnapshot candidate, SystemId systemId)
    {
        var sourceRef = "simulation:" + candidate.SnapshotVersion;
        var source = new WinPoolSource(sourceRef, FactOrigin.Simulation, "WinPool", "Simulation", DateTimeOffset.Now, CollectionPurpose.Storage);
        var prior = previous?.Objects.ToDictionary(x => x.Id) ?? [];
        var beforeFields = Enumerate(before).ToDictionary(x => x.Id, x => Fields(x.Value, sourceRef));
        var objects = ImmutableArray.CreateBuilder<WinPoolSourceObject>();
        var bindings = previous?.Identities.ToBuilder() ?? ImmutableArray.CreateBuilder<WinPoolIdentityBinding>();
        foreach (var item in Enumerate(candidate))
        {
            var newFields = Fields(item.Value, sourceRef);
            if (prior.TryGetValue(item.Id, out var existing))
            {
                var fields = existing.Fields.ToDictionary(x => x.Name);
                foreach (var field in newFields)
                {
                    var old = beforeFields.TryGetValue(item.Id, out var oldFields) ? oldFields.FirstOrDefault(x => x.Name == field.Name) : null;
                    if (old is null || !JsonElement.DeepEquals(old.Value ?? default, field.Value ?? default))
                        fields[field.Name] = field;
                }
                objects.Add(existing with { Fields = fields.Values.ToImmutableArray() });
            }
            else
            {
                var identity = WinPoolIdentityRegistry.OpaqueSourceIdentity("WinPool", item.Type.ToString(), item.Id);
                objects.Add(new(item.Id, item.Type, sourceRef, identity, true, newFields));
                if (!bindings.Any(x => x.ObjectType == item.Type && x.SourceIdentity == identity))
                    bindings.Add(new(item.Type, identity, item.Id));
            }
        }
        // Non-storage observations copied from the original are immutable and remain read-only.
        foreach (var item in prior.Values.Where(x => x.ObjectType is not (
            FactObjectType.Computer or FactObjectType.StorageSubsystem or FactObjectType.StoragePool or FactObjectType.StorageTier
            or FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk or FactObjectType.Disk or FactObjectType.Partition or FactObjectType.Volume or FactObjectType.NetworkDisk)))
            objects.Add(item);
        var ids = objects.Select(x => x.Id).ToHashSet();
        var relationships = ImmutableArray.CreateBuilder<WinPoolFactRelationship>();
        void Link(string? a, string? b, string kind) { if (a is not null && b is not null && ids.Contains(a) && ids.Contains(b)) relationships.Add(new(a, b, kind)); }
        foreach (var pool in candidate.StoragePools)
        {
            Link(pool.SubsystemStableId, pool.StableId, "subsystem-pool");
            foreach (var disk in pool.MemberPhysicalDiskIds) Link(pool.StableId, disk, "pool-member");
        }
        foreach (var disk in candidate.OsDisks)
        {
            Link(disk.PhysicalDiskStableId, disk.StableId, "same-device");
            Link(disk.VirtualDiskStableId, disk.StableId, "same-device");
        }
        foreach (var tier in candidate.StorageTiers)
        {
            Link(tier.PoolStableId, tier.StableId, "pool-tier");
            Link(tier.VirtualDiskStableId, tier.StableId, "virtual-disk-tier");
            foreach (var disk in tier.MemberPhysicalDiskIds) Link(tier.StableId, disk, "tier-member");
        }
        foreach (var disk in candidate.VirtualDisks) Link(disk.PoolStableId, disk.StableId, "pool-virtual-disk");
        foreach (var partition in candidate.Partitions) Link(partition.OsDiskStableId, partition.StableId, "disk-partition");
        foreach (var volume in candidate.Volumes) Link(volume.PartitionStableId, volume.StableId, "partition-volume");
        // Supplementary source associations survive storage edits when both observations still exist.
        if (previous is not null)
            relationships.AddRange(previous.Relationships.Where(x => x.Kind == "same-volume"
                && ids.Contains(x.FromId) && ids.Contains(x.ToId)));
        var used = objects.Select(x => x.SourceRef).Concat(objects.SelectMany(x => x.Fields.Select(f => f.SourceRef))).ToHashSet();
        var sources = (previous?.Sources.AsEnumerable() ?? []).Where(x => x.Id != sourceRef).Append(source)
            .Where(x => used.Contains(x.Id)).ToImmutableArray();
        var result = new WinPoolFacts(1, systemId, checked((previous?.Revision ?? 0) + 1), sources, objects.ToImmutable(),
            relationships.ToImmutable(), bindings.ToImmutable(), previous?.Collections ?? [])
        {
            IsSimulation = true, InventoryVersion = candidate.SnapshotVersion, InventoryCapturedAt = candidate.ScannedAt
        };
        result.Validate();
        return result;
    }

    private static ImmutableArray<WinPoolSourceField> Fields(object value, string sourceRef) => JsonSerializer.SerializeToElement(value)
        .EnumerateObject().Where(x => !NonFields.Contains(x.Name) && x.Name != "DeviceId").Select(property =>
        {
            var name = property.Name switch { "DeviceIdentifier" => "DeviceId", _ => property.Name };
            var element = property.Value;
            if (property.Name == "DeviceIdentifier" && element.GetString() == "" && value is PhysicalDiskInfo { DeviceId: { } number })
                element = JsonSerializer.SerializeToElement(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var declared = value.GetType().GetProperty(property.Name)!.PropertyType;
            declared = Nullable.GetUnderlyingType(declared) ?? declared;
            var type = declared == typeof(bool) ? FactValueType.Boolean
                : declared == typeof(long) || declared == typeof(int) || declared.IsEnum ? FactValueType.Int64
                : typeof(IEnumerable<string>).IsAssignableFrom(declared) && declared != typeof(string) ? FactValueType.StringArray
                : element.ValueKind == JsonValueKind.Array ? FactValueType.Int64Array : FactValueType.String;
            return new WinPoolSourceField(name, type, element.Clone(), FieldReadState.Returned, sourceRef,
                name is "Size" or "SizeRemaining" or "Offset" or "AllocatedSize" or "FootprintOnPool" or "AllocationUnitSize" or "Interleave" ? "bytes" : null);
        }).DistinctBy(x => x.Name).ToImmutableArray();

    private static IEnumerable<(string Id, FactObjectType Type, object Value)> Enumerate(StorageSnapshot snapshot)
    {
        yield return (snapshot.Computer.StableId, FactObjectType.Computer, snapshot.Computer);
        foreach (var x in snapshot.StorageSubsystems) yield return (x.StableId, FactObjectType.StorageSubsystem, x);
        foreach (var x in snapshot.StoragePools) yield return (x.StableId, FactObjectType.StoragePool, x);
        foreach (var x in snapshot.StorageTiers) yield return (x.StableId, FactObjectType.StorageTier, x);
        foreach (var x in snapshot.PhysicalDisks) yield return (x.StableId, FactObjectType.PhysicalDisk, x);
        foreach (var x in snapshot.VirtualDisks) yield return (x.StableId, FactObjectType.VirtualDisk, x);
        foreach (var x in snapshot.OsDisks) yield return (x.StableId, FactObjectType.Disk, x);
        foreach (var x in snapshot.Partitions) yield return (x.StableId, FactObjectType.Partition, x);
        foreach (var x in snapshot.Volumes) yield return (x.StableId, FactObjectType.Volume, x);
        foreach (var x in snapshot.NetworkDisks) yield return (x.StableId, FactObjectType.NetworkDisk, x);
    }
}
