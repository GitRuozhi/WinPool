using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional relationship navigation for the accepted V0.13 snapshot.
/// All results use stable Application identities so the App does not inspect
/// snapshot relationships when switching Manage categories.
/// </summary>
public sealed class ManageNavigationProjector
    : IManageNavigationProjector<StorageSystemDocument>
{
    public ManageObjectNavigationView Project(
        StorageSystemDocument document,
        StorageObjectId objectId,
        ManageObjectRole role)
    {
        ArgumentNullException.ThrowIfNull(document);
        var systemId = document.SystemId;
        if (objectId.System != systemId)
        {
            throw new ArgumentException(
                "The navigation object does not belong to the supplied document.",
                nameof(objectId));
        }

        var origin = new ManageObjectTarget(objectId, role);
        var snapshot = document.Snapshot;
        var related = new Dictionary<ManageWorkspaceCategory, ManageObjectTarget?>
        {
            [ManageWorkspaceCategory.System] = new(
                new StorageObjectId(systemId, StorageObjectKind.System, document.Id),
                ManageObjectRole.System),
            [ManageWorkspaceCategory.Pool] = RelatedPool(snapshot, systemId, origin),
            [ManageWorkspaceCategory.Tier] = RelatedTier(snapshot, systemId, origin),
            [ManageWorkspaceCategory.Disk] = RelatedDisk(snapshot, systemId, origin),
            [ManageWorkspaceCategory.Partition] = RelatedPartition(snapshot, systemId, origin)
        };
        return new ManageObjectNavigationView(
            objectId,
            related,
            Primary(snapshot, systemId, origin));
    }

    private static ManageObjectTarget? RelatedPool(
        StorageSnapshot snapshot,
        SystemId systemId,
        ManageObjectTarget origin)
    {
        var key = origin.Id.ProviderKey;
        switch (origin.Role)
        {
            case ManageObjectRole.StoragePool:
            case ManageObjectRole.NetworkGroup:
            case ManageObjectRole.OtherGroup:
                return origin;
            case ManageObjectRole.StorageTier:
                return Target(snapshot, systemId,
                    snapshot.StorageTiers.FirstOrDefault(x => x.StableId == key)?.PoolStableId);
            case ManageObjectRole.PhysicalDisk:
                return Target(snapshot, systemId,
                    snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == key)?.PoolStableId
                    ?? snapshot.StoragePools.FirstOrDefault(x => x.IsPrimordial)?.StableId);
            case ManageObjectRole.VirtualDisk:
                return Target(snapshot, systemId,
                    snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == key)?.PoolStableId);
            case ManageObjectRole.NetworkDisk:
                return Target(snapshot, systemId, TopologyProjector.NetworkGroupStableId(snapshot));
            case ManageObjectRole.OsDisk:
            {
                var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == key);
                if (osDisk is null)
                {
                    return null;
                }
                if (osDisk.PhysicalDiskStableId is null && osDisk.VirtualDiskStableId is null)
                {
                    return Target(snapshot, systemId, TopologyProjector.OtherGroupStableId(snapshot));
                }
                var backing = Target(
                    snapshot,
                    systemId,
                    osDisk.VirtualDiskStableId ?? osDisk.PhysicalDiskStableId);
                return backing is null ? null : RelatedPool(snapshot, systemId, backing);
            }
            case ManageObjectRole.Partition:
            {
                var backing = PartitionBacking(
                    snapshot,
                    systemId,
                    snapshot.Partitions.FirstOrDefault(x => x.StableId == key));
                return backing is null ? null : RelatedPool(snapshot, systemId, backing);
            }
            default:
                return null;
        }
    }

    private static ManageObjectTarget? RelatedTier(
        StorageSnapshot snapshot,
        SystemId systemId,
        ManageObjectTarget origin)
    {
        var key = origin.Id.ProviderKey;
        switch (origin.Role)
        {
            case ManageObjectRole.StorageTier:
                return origin;
            case ManageObjectRole.StoragePool:
                return Target(snapshot, systemId,
                    snapshot.StorageTiers.FirstOrDefault(x => x.PoolStableId == key)?.StableId);
            case ManageObjectRole.PhysicalDisk:
                return Target(snapshot, systemId,
                    snapshot.StorageTiers.FirstOrDefault(
                        x => x.MemberPhysicalDiskIds.Contains(key, StringComparer.OrdinalIgnoreCase))?.StableId);
            case ManageObjectRole.VirtualDisk:
                return Target(snapshot, systemId,
                    snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == key)?.TierStableIds.FirstOrDefault());
            case ManageObjectRole.Partition:
            {
                var disk = RelatedDisk(snapshot, systemId, origin);
                return disk is null ? null : RelatedTier(snapshot, systemId, disk);
            }
            default:
                return null;
        }
    }

    private static ManageObjectTarget? RelatedDisk(
        StorageSnapshot snapshot,
        SystemId systemId,
        ManageObjectTarget origin)
    {
        var key = origin.Id.ProviderKey;
        switch (origin.Role)
        {
            case ManageObjectRole.PhysicalDisk:
            case ManageObjectRole.VirtualDisk:
            case ManageObjectRole.NetworkDisk:
            case ManageObjectRole.OsDisk:
                return origin;
            case ManageObjectRole.Partition:
                return PartitionBacking(
                    snapshot,
                    systemId,
                    snapshot.Partitions.FirstOrDefault(x => x.StableId == key));
            case ManageObjectRole.StorageTier:
                return Target(snapshot, systemId,
                    snapshot.StorageTiers.FirstOrDefault(x => x.StableId == key)?.MemberPhysicalDiskIds.FirstOrDefault());
            case ManageObjectRole.StoragePool:
            {
                var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == key);
                return Target(
                    snapshot,
                    systemId,
                    pool?.MemberPhysicalDiskIds.FirstOrDefault()
                    ?? snapshot.VirtualDisks.FirstOrDefault(x => x.PoolStableId == key)?.StableId);
            }
            case ManageObjectRole.NetworkGroup:
                return Target(snapshot, systemId, snapshot.NetworkDisks.FirstOrDefault()?.StableId);
            case ManageObjectRole.OtherGroup:
                return Target(snapshot, systemId, TopologyProjector.GetOtherOsDisks(snapshot).FirstOrDefault()?.StableId);
            default:
                return null;
        }
    }

    private static ManageObjectTarget? RelatedPartition(
        StorageSnapshot snapshot,
        SystemId systemId,
        ManageObjectTarget origin)
    {
        var key = origin.Id.ProviderKey;
        switch (origin.Role)
        {
            case ManageObjectRole.Partition:
            case ManageObjectRole.NetworkDisk:
                return origin;
            case ManageObjectRole.PhysicalDisk:
                return Target(snapshot, systemId, FirstPartitionForPhysicalDisk(snapshot, key));
            case ManageObjectRole.VirtualDisk:
            {
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.VirtualDiskStableId == key)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Target(snapshot, systemId,
                    snapshot.Partitions.FirstOrDefault(
                        x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId))?.StableId);
            }
            case ManageObjectRole.OsDisk:
                return Target(snapshot, systemId,
                    snapshot.Partitions.FirstOrDefault(x => x.OsDiskStableId == key)?.StableId);
            case ManageObjectRole.StoragePool:
            case ManageObjectRole.StorageTier:
            case ManageObjectRole.NetworkGroup:
            case ManageObjectRole.OtherGroup:
            {
                var disk = RelatedDisk(snapshot, systemId, origin);
                return disk is null ? null : RelatedPartition(snapshot, systemId, disk);
            }
            default:
                return null;
        }
    }

    private static ManageObjectTarget? Primary(
        StorageSnapshot snapshot,
        SystemId systemId,
        ManageObjectTarget origin)
    {
        var key = origin.Id.ProviderKey;
        var targetKey = origin.Role switch
        {
            ManageObjectRole.StoragePool =>
                snapshot.StoragePools.FirstOrDefault(x => x.StableId == key)?.MemberPhysicalDiskIds.FirstOrDefault(),
            ManageObjectRole.NetworkGroup => snapshot.NetworkDisks.FirstOrDefault()?.StableId,
            ManageObjectRole.OtherGroup => TopologyProjector.GetOtherOsDisks(snapshot).FirstOrDefault()?.StableId,
            ManageObjectRole.StorageTier =>
                snapshot.StorageTiers.FirstOrDefault(x => x.StableId == key)?.PoolStableId,
            ManageObjectRole.VirtualDisk =>
                snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == key)?.PoolStableId,
            ManageObjectRole.PhysicalDisk =>
                snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == key)?.PoolStableId
                ?? FirstPartitionForPhysicalDisk(snapshot, key),
            ManageObjectRole.OsDisk =>
                snapshot.Partitions.FirstOrDefault(x => x.OsDiskStableId == key)?.StableId,
            _ => null
        };
        if (origin.Role == ManageObjectRole.Partition)
        {
            return PartitionBacking(
                snapshot,
                systemId,
                snapshot.Partitions.FirstOrDefault(x => x.StableId == key));
        }
        return Target(snapshot, systemId, targetKey);
    }

    private static ManageObjectTarget? PartitionBacking(
        StorageSnapshot snapshot,
        SystemId systemId,
        PartitionInfo? partition)
    {
        var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition?.OsDiskStableId);
        return Target(
            snapshot,
            systemId,
            osDisk?.VirtualDiskStableId ?? osDisk?.PhysicalDiskStableId ?? osDisk?.StableId);
    }

    private static string? FirstPartitionForPhysicalDisk(
        StorageSnapshot snapshot,
        string physicalDiskId)
    {
        var osDiskIds = snapshot.OsDisks
            .Where(x => x.PhysicalDiskStableId == physicalDiskId)
            .Select(x => x.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Partitions
            .FirstOrDefault(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId))
            ?.StableId;
    }

    private static ManageObjectTarget? Target(
        StorageSnapshot snapshot,
        SystemId systemId,
        string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        (StorageObjectKind Kind, ManageObjectRole Role)? identity =
            snapshot.StoragePools.Any(x => x.StableId == providerKey)
            ? (StorageObjectKind.StoragePool, ManageObjectRole.StoragePool)
            : snapshot.StorageTiers.Any(x => x.StableId == providerKey)
                ? (StorageObjectKind.StorageTier, ManageObjectRole.StorageTier)
                : snapshot.PhysicalDisks.Any(x => x.StableId == providerKey)
                    ? (StorageObjectKind.PhysicalDisk, ManageObjectRole.PhysicalDisk)
                    : snapshot.VirtualDisks.Any(x => x.StableId == providerKey)
                        ? (StorageObjectKind.VirtualDisk, ManageObjectRole.VirtualDisk)
                        : snapshot.OsDisks.Any(x => x.StableId == providerKey)
                            ? (StorageObjectKind.OsDisk, ManageObjectRole.OsDisk)
                            : snapshot.Partitions.Any(x => x.StableId == providerKey)
                                ? (StorageObjectKind.Partition, ManageObjectRole.Partition)
                                : snapshot.NetworkDisks.Any(x => x.StableId == providerKey)
                                    ? (StorageObjectKind.NetworkDisk, ManageObjectRole.NetworkDisk)
                                    : providerKey.Equals(
                                        TopologyProjector.NetworkGroupStableId(snapshot),
                                        StringComparison.OrdinalIgnoreCase)
                                        ? (StorageObjectKind.LogicalGroup, ManageObjectRole.NetworkGroup)
                                        : providerKey.Equals(
                                            TopologyProjector.OtherGroupStableId(snapshot),
                                            StringComparison.OrdinalIgnoreCase)
                                            ? (StorageObjectKind.LogicalGroup, ManageObjectRole.OtherGroup)
                                            : null;
        return identity is null
            ? null
            : new ManageObjectTarget(
                new StorageObjectId(systemId, identity.Value.Kind, providerKey),
                identity.Value.Role);
    }
}
