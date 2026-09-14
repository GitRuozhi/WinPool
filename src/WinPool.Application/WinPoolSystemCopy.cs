using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Remaps every storage reference into the copy's identity scope.</summary>
internal static class WinPoolSystemCopy
{
    public static StorageSnapshot Snapshot(StorageSnapshot value, SystemId target)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string id, FactObjectType type) => ids[id] = WinPoolIdentityRegistry.ScopedId(target, type, id);
        Add(value.Computer.StableId, FactObjectType.Computer);
        foreach (var x in value.StorageSubsystems) Add(x.StableId, FactObjectType.StorageSubsystem);
        foreach (var x in value.PhysicalDisks) Add(x.StableId, FactObjectType.PhysicalDisk);
        foreach (var x in value.StoragePools) Add(x.StableId, FactObjectType.StoragePool);
        foreach (var x in value.StorageTiers) Add(x.StableId, FactObjectType.StorageTier);
        foreach (var x in value.VirtualDisks) Add(x.StableId, FactObjectType.VirtualDisk);
        foreach (var x in value.OsDisks) Add(x.StableId, FactObjectType.Disk);
        foreach (var x in value.Partitions) Add(x.StableId, FactObjectType.Partition);
        foreach (var x in value.Volumes) Add(x.StableId, FactObjectType.Volume);
        foreach (var x in value.NetworkDisks) Add(x.StableId, FactObjectType.NetworkDisk);
        string Id(string id) => ids.TryGetValue(id, out var mapped) ? mapped
            : WinPoolIdentityRegistry.ScopedId(target, FactObjectType.Computer, id);
        string? Optional(string? id) => id is null ? null : Id(id);
        return value with
        {
            SnapshotVersion = Guid.NewGuid().ToString("N"),
            Computer = value.Computer with { StableId = Id(value.Computer.StableId) },
            StorageSubsystems = value.StorageSubsystems.Select(x => x with { StableId = Id(x.StableId) }).ToArray(),
            PhysicalDisks = value.PhysicalDisks.Select(x => x with { StableId = Id(x.StableId), PoolStableId = Optional(x.PoolStableId) }).ToArray(),
            StoragePools = value.StoragePools.Select(x => x with { StableId = Id(x.StableId), SubsystemStableId = Optional(x.SubsystemStableId), MemberPhysicalDiskIds = x.MemberPhysicalDiskIds.Select(Id).ToArray() }).ToArray(),
            StorageTiers = value.StorageTiers.Select(x => x with { StableId = Id(x.StableId), PoolStableId = Optional(x.PoolStableId), VirtualDiskStableId = Optional(x.VirtualDiskStableId), MemberPhysicalDiskIds = x.MemberPhysicalDiskIds.Select(Id).ToArray() }).ToArray(),
            VirtualDisks = value.VirtualDisks.Select(x => x with { StableId = Id(x.StableId), PoolStableId = Optional(x.PoolStableId), TierStableIds = x.TierStableIds.Select(Id).ToArray(), OsDiskNumbers = x.OsDiskNumbers.ToArray() }).ToArray(),
            OsDisks = value.OsDisks.Select(x => x with { StableId = Id(x.StableId), PhysicalDiskStableId = Optional(x.PhysicalDiskStableId), VirtualDiskStableId = Optional(x.VirtualDiskStableId) }).ToArray(),
            Partitions = value.Partitions.Select(x => x with { StableId = Id(x.StableId), OsDiskStableId = Optional(x.OsDiskStableId) }).ToArray(),
            Volumes = value.Volumes.Select(x => x with { StableId = Id(x.StableId), PartitionStableId = Optional(x.PartitionStableId), AccessPaths = x.AccessPaths.ToArray() }).ToArray(),
            NetworkDisks = value.NetworkDisks.Select(x => x with { StableId = Id(x.StableId) }).ToArray(),
            Relationships = value.Relationships.Select(x => x with { FromStableId = Id(x.FromStableId), ToStableId = Id(x.ToStableId) }).ToArray(),
            Warnings = value.Warnings.Select(x => x with { StableId = Optional(x.StableId) }).ToArray()
        };
    }
}
