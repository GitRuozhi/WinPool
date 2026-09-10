namespace WinPool.Application;

public static class StorageRelationshipProjector
{
    public static IReadOnlyList<StorageRelationship> Project(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new List<StorageRelationship>();
        foreach (var pool in snapshot.StoragePools)
        {
            result.AddRange(pool.MemberPhysicalDiskIds.Select(
                id => new StorageRelationship(pool.StableId, id, "PoolMember")));
        }

        foreach (var tier in snapshot.StorageTiers)
        {
            if (tier.PoolStableId is not null)
            {
                result.Add(new StorageRelationship(tier.PoolStableId, tier.StableId, "ContainsTier"));
            }

            if (tier.VirtualDiskStableId is not null)
            {
                result.Add(new StorageRelationship(tier.VirtualDiskStableId, tier.StableId, "VirtualDiskTier"));
            }

            result.AddRange(tier.MemberPhysicalDiskIds.Select(
                id => new StorageRelationship(tier.StableId, id, "TierDiskReference")));
        }

        foreach (var virtualDisk in snapshot.VirtualDisks)
        {
            if (virtualDisk.PoolStableId is not null)
            {
                result.Add(new StorageRelationship(
                    virtualDisk.PoolStableId,
                    virtualDisk.StableId,
                    "ContainsVirtualDisk"));
            }

            foreach (var tierId in virtualDisk.TierStableIds)
            {
                result.Add(new StorageRelationship(virtualDisk.StableId, tierId, "UsesTier"));
            }
        }

        foreach (var osDisk in snapshot.OsDisks)
        {
            var parent = osDisk.VirtualDiskStableId ?? osDisk.PhysicalDiskStableId;
            if (parent is not null)
            {
                result.Add(new StorageRelationship(parent, osDisk.StableId, "MapsToOsDisk"));
            }
        }

        foreach (var partition in snapshot.Partitions)
        {
            if (partition.OsDiskStableId is not null)
            {
                result.Add(new StorageRelationship(
                    partition.OsDiskStableId,
                    partition.StableId,
                    "ContainsPartition"));
            }
        }

        foreach (var volume in snapshot.Volumes)
        {
            if (volume.PartitionStableId is not null)
            {
                result.Add(new StorageRelationship(
                    volume.PartitionStableId,
                    volume.StableId,
                    "HostsVolume"));
            }
        }

        return result;
    }

    public static StorageSnapshot Rebuild(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projected = ProjectPartitionVolumes(snapshot);
        return projected with { Relationships = Project(projected) };
    }

    public static StorageSnapshot ProjectPartitionVolumes(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var volumesByPartition = snapshot.Volumes
            .Where(item => !string.IsNullOrWhiteSpace(item.PartitionStableId))
            .GroupBy(item => item.PartitionStableId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var partitions = snapshot.Partitions
            .Select(partition =>
            {
                if (!volumesByPartition.TryGetValue(partition.StableId, out var volume))
                {
                    return partition with
                    {
                        DriveLetter = string.Empty,
                        FileSystemLabel = string.Empty,
                        FileSystem = string.Empty,
                        AllocationUnitSize = null,
                        SizeRemaining = 0,
                        Path = string.Empty,
                        HealthStatus = partition.HealthStatus,
                        OperationalStatus = partition.OperationalStatus
                    };
                }

                var letter = volume.DriveLetter;
                return partition with
                {
                    DriveLetter = letter,
                    FileSystemLabel = volume.FileSystemLabel,
                    FileSystem = volume.FileSystem,
                    AllocationUnitSize = volume.AllocationUnitSize,
                    SizeRemaining = volume.SizeRemaining,
                    Path = volume.AccessPaths.FirstOrDefault()
                        ?? (string.IsNullOrWhiteSpace(letter) ? string.Empty : $"{letter}:\\"),
                    HealthStatus = volume.HealthStatus,
                    OperationalStatus = volume.OperationalStatus
                };
            })
            .ToArray();
        return snapshot with { Partitions = partitions };
    }

    public static IReadOnlyList<string> Validate(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddId(string id, string kind)
        {
            if (!ids.Add(id))
            {
                errors.Add($"Duplicate {kind} identity '{id}'.");
            }
        }

        AddId(snapshot.Computer.StableId, "system");
        foreach (var item in snapshot.PhysicalDisks) AddId(item.StableId, "physical disk");
        foreach (var item in snapshot.StoragePools) AddId(item.StableId, "pool");
        foreach (var item in snapshot.StorageTiers) AddId(item.StableId, "tier");
        foreach (var item in snapshot.VirtualDisks) AddId(item.StableId, "virtual disk");
        foreach (var item in snapshot.OsDisks) AddId(item.StableId, "os disk");
        foreach (var item in snapshot.Partitions) AddId(item.StableId, "partition");
        foreach (var item in snapshot.Volumes) AddId(item.StableId, "volume");
        foreach (var item in snapshot.NetworkDisks) AddId(item.StableId, "network disk");

        foreach (var relationship in snapshot.Relationships)
        {
            if (!ids.Contains(relationship.FromStableId) || !ids.Contains(relationship.ToStableId))
            {
                errors.Add(
                    $"Relationship {relationship.RelationshipKind} has a missing endpoint.");
            }
        }

        foreach (var volume in snapshot.Volumes)
        {
            if (volume.PartitionStableId is not null
                && snapshot.Partitions.All(item => item.StableId != volume.PartitionStableId))
            {
                errors.Add($"Volume '{volume.StableId}' points at a missing partition.");
            }
        }

        foreach (var tier in snapshot.StorageTiers)
        {
            if (tier.VirtualDiskStableId is not null
                && snapshot.VirtualDisks.All(item => item.StableId != tier.VirtualDiskStableId))
            {
                errors.Add($"Tier '{tier.StableId}' points at a missing virtual disk.");
            }
        }

        return errors;
    }
}
