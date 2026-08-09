using WinPool.Application;
using WinPool.Core;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional projection boundary for the accepted V0.13 inventory model.
/// The App consumes the Application topology contract while the legacy
/// collector remains available for parity testing.
/// </summary>
public sealed class LegacyManageSystemProjector
    : IManageSystemProjector<StorageSystemDocument>
{
    public ManageSystemProjection Project(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var systemId = InternalStableIdentity.SystemFromDocumentId(document.Id);
        var root = TopologyProjector.Project(document.Snapshot);
        var rootView = Convert(root, systemId, $"{document.Id}:root") with
        {
            Id = new StorageObjectId(
                systemId,
                StorageObjectKind.System,
                document.Id),
            DisplayName = document.DisplayName
        };
        return new(
            systemId,
            document.Id,
            document.DisplayName,
            document.Kind == StorageSystemKind.Local
                ? StorageSystemSourceKind.Local
                : StorageSystemSourceKind.Simulation,
            $"legacy:{document.SchemaVersion}:{document.Snapshot.SchemaVersion}:{document.UpdatedAt.UtcTicks}:{document.Snapshot.ScannedAt.UtcTicks}",
            document.Snapshot.ScannedAt,
            rootView,
            CreateWorkspaceObjects(document, systemId));
    }

    private static IReadOnlyList<ManageObjectListItemView> CreateWorkspaceObjects(
        StorageSystemDocument document,
        SystemId systemId)
    {
        var snapshot = document.Snapshot;
        var result = new List<ManageObjectListItemView>
        {
            Item(
                systemId,
                document.Id,
                ManageObjectRole.System,
                ManageWorkspaceCategory.System,
                document.DisplayName,
                true,
                null,
                0)
        };
        var order = 0;
        foreach (var pool in snapshot.StoragePools
                     .OrderByDescending(item => item.IsPrimordial)
                     .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            result.Add(Item(
                systemId, pool.StableId, ManageObjectRole.StoragePool,
                ManageWorkspaceCategory.Pool,
                pool.IsPrimordial ? "Primordial" : pool.FriendlyName,
                pool.IsStable, null, order++,
                new Dictionary<string, string?>
                {
                    ["isPrimordial"] = pool.IsPrimordial.ToString()
                }));
        }
        if (snapshot.NetworkDisks.Count > 0)
        {
            result.Add(Item(
                systemId,
                TopologyProjector.NetworkGroupStableId(snapshot),
                ManageObjectRole.NetworkGroup,
                ManageWorkspaceCategory.Pool,
                "Network",
                true,
                null,
                order++));
        }
        if (TopologyProjector.GetOtherOsDisks(snapshot).Count > 0)
        {
            result.Add(Item(
                systemId,
                TopologyProjector.OtherGroupStableId(snapshot),
                ManageObjectRole.OtherGroup,
                ManageWorkspaceCategory.Pool,
                "Other",
                true,
                null,
                order++));
        }

        order = 0;
        foreach (var tier in snapshot.StorageTiers)
        {
            result.Add(Item(
                systemId, tier.StableId, ManageObjectRole.StorageTier,
                ManageWorkspaceCategory.Tier, tier.FriendlyName,
                tier.IsStable, tier.PoolStableId, order++));
        }

        order = 0;
        foreach (var disk in OrderPhysicalDisks(snapshot))
        {
            result.Add(Item(
                systemId, disk.StableId, ManageObjectRole.PhysicalDisk,
                ManageWorkspaceCategory.Disk, disk.FriendlyName,
                disk.IsStable, disk.PoolStableId, order++));
        }
        foreach (var disk in OrderVirtualDisks(snapshot))
        {
            result.Add(Item(
                systemId, disk.StableId, ManageObjectRole.VirtualDisk,
                ManageWorkspaceCategory.Disk, disk.FriendlyName,
                disk.IsStable, disk.PoolStableId, order++));
        }
        foreach (var disk in TopologyProjector.GetOtherOsDisks(snapshot)
                     .OrderBy(item => item.Number))
        {
            result.Add(Item(
                systemId, disk.StableId, ManageObjectRole.OsDisk,
                ManageWorkspaceCategory.Disk, disk.FriendlyName,
                true, null, order++));
        }

        order = 0;
        foreach (var partition in OrderPartitions(snapshot))
        {
            result.Add(Item(
                systemId, partition.StableId, ManageObjectRole.Partition,
                ManageWorkspaceCategory.Partition,
                TopologyProjector.PartitionDisplayName(partition),
                partition.IsStable, partition.OsDiskStableId, order++,
                new Dictionary<string, string?> { ["partitionType"] = partition.Type }));
        }
        foreach (var network in snapshot.NetworkDisks
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            result.Add(Item(
                systemId, network.StableId, ManageObjectRole.NetworkDisk,
                ManageWorkspaceCategory.Partition, network.Name,
                network.IsStable, null, order++));
        }
        return result;
    }

    private static ManageObjectListItemView Item(
        SystemId systemId,
        string providerKey,
        ManageObjectRole role,
        ManageWorkspaceCategory category,
        string displayName,
        bool stable,
        string? parent,
        int order,
        IReadOnlyDictionary<string, string?>? metadata = null) =>
        new(
            new StorageObjectId(systemId, MapKind(role), providerKey),
            role,
            category,
            displayName,
            stable,
            parent,
            order,
            metadata ?? new Dictionary<string, string?>());

    private static IReadOnlyList<PhysicalDiskInfo> OrderPhysicalDisks(
        StorageSnapshot snapshot)
    {
        var poolOrder = snapshot.StoragePools
            .OrderByDescending(item => item.IsPrimordial)
            .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Select((item, index) => (item.StableId, index))
            .ToDictionary(item => item.StableId, item => item.index, StringComparer.OrdinalIgnoreCase);
        var tierOrder = snapshot.StorageTiers
            .OrderBy(item => item.MediaType is "SSD" or "SCM" ? 0 : 1)
            .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Select((item, index) => (item.StableId, index))
            .ToDictionary(item => item.StableId, item => item.index, StringComparer.OrdinalIgnoreCase);
        return snapshot.PhysicalDisks
            .OrderBy(item => item.PoolStableId is not null
                && poolOrder.TryGetValue(item.PoolStableId, out var poolRank)
                    ? poolRank
                    : poolOrder.Count)
            .ThenBy(item => snapshot.StorageTiers
                .Where(tier => tier.MemberPhysicalDiskIds.Contains(
                    item.StableId,
                    StringComparer.OrdinalIgnoreCase))
                .Select(tier => tierOrder.GetValueOrDefault(tier.StableId, int.MaxValue))
                .DefaultIfEmpty(int.MaxValue)
                .Min())
            .ThenBy(item => item.DeviceId ?? int.MaxValue)
            .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<VirtualDiskInfo> OrderVirtualDisks(
        StorageSnapshot snapshot)
    {
        var poolOrder = snapshot.StoragePools
            .OrderByDescending(item => item.IsPrimordial)
            .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Select((item, index) => (item.StableId, index))
            .ToDictionary(item => item.StableId, item => item.index, StringComparer.OrdinalIgnoreCase);
        return snapshot.VirtualDisks
            .OrderBy(item => item.PoolStableId is not null
                && poolOrder.TryGetValue(item.PoolStableId, out var rank)
                    ? rank
                    : poolOrder.Count)
            .ThenBy(item => item.OsDiskNumbers.Count > 0
                ? item.OsDiskNumbers[0]
                : int.MaxValue)
            .ThenBy(item => item.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PartitionInfo> OrderPartitions(StorageSnapshot snapshot)
    {
        var backingOrder = OrderPhysicalDisks(snapshot)
            .Select(item => item.StableId)
            .Concat(OrderVirtualDisks(snapshot).Select(item => item.StableId))
            .Concat(TopologyProjector.GetOtherOsDisks(snapshot)
                .OrderBy(item => item.Number)
                .Select(item => item.StableId))
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var osDisks = snapshot.OsDisks.ToDictionary(
            item => item.StableId,
            StringComparer.OrdinalIgnoreCase);
        return snapshot.Partitions
            .OrderBy(item =>
            {
                if (item.OsDiskStableId is null
                    || !osDisks.TryGetValue(item.OsDiskStableId, out var osDisk))
                {
                    return int.MaxValue;
                }
                var backing = osDisk.VirtualDiskStableId
                    ?? osDisk.PhysicalDiskStableId
                    ?? osDisk.StableId;
                return backingOrder.GetValueOrDefault(backing, int.MaxValue);
            })
            .ThenBy(item => item.DiskNumber)
            .ThenBy(item => item.PartitionNumber)
            .ToArray();
    }

    private static ManageTopologyNodeView Convert(
        TopologyNode node,
        SystemId systemId,
        string occurrenceKey)
    {
        var role = MapRole(node.Unit.Kind);
        var children = node.Children
            .Select((child, index) => Convert(
                child,
                systemId,
                $"{occurrenceKey}/{index}:{MapRole(child.Unit.Kind)}"))
            .ToArray();
        return new(
            occurrenceKey,
            new StorageObjectId(systemId, MapKind(role), node.Unit.StableId),
            role,
            node.Unit.DisplayName,
            node.Unit.IsStable,
            node.Summary,
            node.IsReference,
            node.IsExpanded,
            node.IsSelectable,
            node.ChildrenLayout switch
            {
                TopologyChildrenLayout.Stack => ManageTopologyLayout.Stack,
                TopologyChildrenLayout.Flow => ManageTopologyLayout.Flow,
                TopologyChildrenLayout.WeightedFlow => ManageTopologyLayout.WeightedFlow,
                _ => throw new ArgumentOutOfRangeException(nameof(node))
            },
            node.LayoutWeight,
            children);
    }

    private static ManageObjectRole MapRole(StorageUnitKind kind) => kind switch
    {
        StorageUnitKind.System => ManageObjectRole.System,
        StorageUnitKind.StorageSubsystem => ManageObjectRole.StorageSubsystem,
        StorageUnitKind.StoragePool => ManageObjectRole.StoragePool,
        StorageUnitKind.StorageTier => ManageObjectRole.StorageTier,
        StorageUnitKind.PhysicalDisk => ManageObjectRole.PhysicalDisk,
        StorageUnitKind.VirtualDisk => ManageObjectRole.VirtualDisk,
        StorageUnitKind.NetworkDisk => ManageObjectRole.NetworkDisk,
        StorageUnitKind.OsDisk => ManageObjectRole.OsDisk,
        StorageUnitKind.Partition => ManageObjectRole.Partition,
        StorageUnitKind.NetworkDiskGroup => ManageObjectRole.NetworkGroup,
        StorageUnitKind.OtherDiskGroup => ManageObjectRole.OtherGroup,
        StorageUnitKind.DirectDiskGroup => ManageObjectRole.DirectDiskGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static StorageObjectKind MapKind(ManageObjectRole role) => role switch
    {
        ManageObjectRole.System => StorageObjectKind.System,
        ManageObjectRole.StorageSubsystem => StorageObjectKind.StorageSubsystem,
        ManageObjectRole.StoragePool => StorageObjectKind.StoragePool,
        ManageObjectRole.StorageTier => StorageObjectKind.StorageTier,
        ManageObjectRole.PhysicalDisk => StorageObjectKind.PhysicalDisk,
        ManageObjectRole.VirtualDisk => StorageObjectKind.VirtualDisk,
        ManageObjectRole.NetworkDisk => StorageObjectKind.NetworkDisk,
        ManageObjectRole.OsDisk => StorageObjectKind.OsDisk,
        ManageObjectRole.Partition => StorageObjectKind.Partition,
        ManageObjectRole.NetworkGroup or ManageObjectRole.OtherGroup
            or ManageObjectRole.DirectDiskGroup => StorageObjectKind.LogicalGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
