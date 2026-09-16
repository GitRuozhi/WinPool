namespace WinPool.Application;

/// <summary>
/// Produces source-less storage containers from an already unified storage
/// snapshot. The result is presentation-only: it does not amend facts,
/// relationships, or editable storage records.
/// </summary>
public static class SyntheticStorageProjection
{
    private const string PoolPrefix = "synthetic:pool:";
    private const string TierPrefix = "synthetic:tier:";

    public static IReadOnlyList<SyntheticStorageObject> For(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Project(snapshot);
    }

    public static IReadOnlyList<SyntheticStorageObject> Project(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new List<SyntheticStorageObject>();
        var unknownMembership = snapshot.UnknownTierMembershipPhysicalDiskIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pool in snapshot.StoragePools.Where(pool => !pool.IsPrimordial))
        {
            var members = snapshot.PhysicalDisks
                .Where(disk => pool.MemberPhysicalDiskIds.Contains(
                    disk.StableId,
                    StringComparer.OrdinalIgnoreCase))
                .DistinctBy(disk => disk.StableId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            AddLayer(SyntheticStorageName.HotSpareLayer,
                members.Where(disk => disk.IsHotSpare).Select(disk => disk.StableId));
            AddLayer(SyntheticStorageName.RetiredLayer,
                members.Where(disk => disk.IsRetired).Select(disk => disk.StableId));

            var unallocated = snapshot.DirectPoolMembers(pool.StableId)
                .Select(disk => disk.StableId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unallocated.Length > 0)
            {
                // Older direct snapshots only retain the pool-level marker.
                // In that case every direct member remains visible but its
                // exact source membership cannot be distinguished.
                var unknown = unknownMembership.Count == 0
                    && snapshot.UnknownTierMembershipPools.Contains(
                        pool.StableId,
                        StringComparer.OrdinalIgnoreCase)
                    ? unallocated
                    : unallocated.Where(unknownMembership.Contains).ToArray();
                result.Add(new(
                    TierStableId(pool.StableId, SyntheticStorageName.UnallocatedLayer),
                    SyntheticStorageObjectKind.Tier,
                    SyntheticStorageName.UnallocatedLayer,
                    pool.StableId,
                    unallocated,
                    unknown));
            }

            void AddLayer(SyntheticStorageName name, IEnumerable<string> memberIds)
            {
                var ids = memberIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (ids.Length == 0)
                {
                    return;
                }

                result.Add(new(
                    TierStableId(pool.StableId, name),
                    SyntheticStorageObjectKind.Tier,
                    name,
                    pool.StableId,
                    ids,
                    []));
            }
        }

        var otherOsDisks = TopologyProjector.GetOtherOsDisks(snapshot)
            .Select(disk => disk.StableId);
        var unattachedUnions = snapshot.PartitionUnions
            .Where(union => !union.IsNetwork
                && (union.OsDiskId is null || snapshot.OsDisks.All(disk =>
                    !disk.StableId.Equals(union.OsDiskId, StringComparison.OrdinalIgnoreCase))))
            .Select(union => union.Id);
        var otherMembers = otherOsDisks.Concat(unattachedUnions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (otherMembers.Length > 0)
        {
            result.Add(new(
                OtherPoolStableId(snapshot),
                SyntheticStorageObjectKind.Pool,
                SyntheticStorageName.OtherDiskPool,
                null,
                otherMembers,
                []));
        }

        var networkMembers = snapshot.NetworkDisks
            .Select(disk => disk.StableId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (networkMembers.Length > 0)
        {
            result.Add(new(
                NetworkPoolStableId(snapshot),
                SyntheticStorageObjectKind.Pool,
                SyntheticStorageName.NetworkDiskPool,
                null,
                networkMembers,
                []));
        }

        return result;
    }

    public static string TierStableId(string poolStableId, SyntheticStorageName name) => name switch
    {
        SyntheticStorageName.HotSpareLayer => $"{TierPrefix}hot-spare:{poolStableId}",
        SyntheticStorageName.RetiredLayer => $"{TierPrefix}retired:{poolStableId}",
        SyntheticStorageName.UnallocatedLayer => $"{TierPrefix}unallocated:{poolStableId}",
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static string OtherPoolStableId(StorageSnapshot snapshot) =>
        $"{PoolPrefix}other:{snapshot.Computer.StableId}";

    public static string NetworkPoolStableId(StorageSnapshot snapshot) =>
        $"{PoolPrefix}network:{snapshot.Computer.StableId}";
}
