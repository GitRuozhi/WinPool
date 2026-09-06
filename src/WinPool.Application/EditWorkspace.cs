using WinPool.Domain;

namespace WinPool.Application;

public static class EditWorkspace
{
    public const string PlusStableId = "edit:plus";
    public const string PoolRowStableId = "edit:pool-row";
    public const string PartitionRowStableId = "edit:partition-row";
    public const string DraftPrefix = "edit:draft:";
    public const string UnallocatedPrefix = "unallocated:";
    public const string PendingVirtualPrefix = "edit:pending-vdisk:";
    public const long DefaultUnallocatedIgnoreBytes = 8L * 1024 * 1024;

    public static bool IsPlus(string? id) =>
        string.Equals(id, PlusStableId, StringComparison.OrdinalIgnoreCase);

    public static bool IsPoolRow(string? id) =>
        string.Equals(id, PoolRowStableId, StringComparison.OrdinalIgnoreCase);

    public static bool IsDraftPool(string? id) =>
        id is not null && id.StartsWith(DraftPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnallocated(string? id) =>
        id is not null && id.StartsWith(UnallocatedPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsPendingVirtualDisk(string? id) =>
        id is not null && id.StartsWith(PendingVirtualPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseUnallocated(
        string id,
        out string osDiskId,
        out long offset,
        out long size)
    {
        osDiskId = string.Empty;
        offset = 0;
        size = 0;
        if (!IsUnallocated(id))
        {
            return false;
        }

        var rest = id[UnallocatedPrefix.Length..];
        var last = rest.LastIndexOf(':');
        if (last <= 0)
        {
            return false;
        }

        var beforeLast = rest.LastIndexOf(':', last - 1);
        if (beforeLast <= 0)
        {
            return false;
        }

        if (!long.TryParse(rest[(beforeLast + 1)..last], out offset)
            || !long.TryParse(rest[(last + 1)..], out size)
            || offset < 0
            || size <= 0)
        {
            return false;
        }

        osDiskId = rest[..beforeLast];
        return osDiskId.Length > 0;
    }

    public static bool HasScmDisk(StorageSnapshot snapshot) =>
        snapshot.PhysicalDisks.Any(disk =>
            disk.MediaType.Equals("SCM", StringComparison.OrdinalIgnoreCase));

    public static bool HasMultipleVirtualDisks(StorageSnapshot snapshot, string poolId) =>
        snapshot.VirtualDisks.Count(disk =>
            string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)) > 1;

    public static bool CanExecuteCreate(StorageSnapshot snapshot, string poolId)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase));
        return pool is { IsPrimordial: false }
            && pool.MemberPhysicalDiskIds.Count > 0
            && !HasMultipleVirtualDisks(snapshot, poolId);
    }

    public static string NormalizeMedia(string mediaType) =>
        mediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
        : mediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
        : mediaType.Equals("SCM", StringComparison.OrdinalIgnoreCase) ? "SCM"
        : "Unknown";

    public static string RequiredTierMedia(string mediaType)
    {
        var media = NormalizeMedia(mediaType);
        return media is "SSD" or "HDD" or "SCM"
            ? media
            : throw new InvalidOperationException("Unknown media cannot join a simulated pool.");
    }

    public static string RecommendedResiliency(string mediaType, int memberCount)
    {
        if (memberCount <= 1)
        {
            return "Simple";
        }

        return NormalizeMedia(mediaType) == "HDD" ? "Parity" : "Mirror";
    }

    public static int RecommendedDataCopies(string resiliency, int memberCount)
    {
        if (resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase) || memberCount <= 1)
        {
            return 1;
        }

        return resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    public static int RecommendedToleratedFailures(string resiliency, int dataCopies)
    {
        if (resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(0, dataCopies - 1);
        }

        return 1;
    }

    public static int RecommendedCapacityColumns(IReadOnlyList<PhysicalDiskInfo> members)
    {
        if (members.Count == 0)
        {
            return 1;
        }

        var sizes = members.Select(item => item.Size).Distinct().ToArray();
        return sizes.Length == 1 ? members.Count : Math.Max(1, members.Count - 1);
    }

    public static IReadOnlyList<TopologyNode> ProjectPartitionWorkspace(
        StorageSnapshot snapshot,
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ignore = Math.Max(0, minUnallocatedBytes);
        var nonPrimordialMembers = snapshot.StoragePools
            .Where(pool => !pool.IsPrimordial)
            .SelectMany(pool => pool.MemberPhysicalDiskIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var disks = snapshot.OsDisks
            .Where(PartitionableDiskPolicy.IsEligible)
            .Where(disk =>
            {
                if (!string.IsNullOrWhiteSpace(disk.VirtualDiskStableId))
                {
                    return true;
                }

                return string.IsNullOrWhiteSpace(disk.PhysicalDiskStableId)
                    || !nonPrimordialMembers.Contains(disk.PhysicalDiskStableId);
            })
            .OrderBy(disk => disk.Number)
            .ToArray();

        return disks.Select(disk => CreatePartitionableDiskNode(disk, snapshot, ignore)).ToArray();
    }

    public static TopologyNode ProjectPartitionWorkspaceRoot(
        StorageSnapshot snapshot,
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes)
    {
        var children = ProjectPartitionWorkspace(snapshot, minUnallocatedBytes);
        return new TopologyNode(
            new StorageUnitRef(PartitionRowStableId, StorageUnitKind.VirtualDiskGroup, string.Empty, false),
            string.Empty,
            children,
            isSelectable: false,
            childrenLayout: TopologyChildrenLayout.Stack);
    }

    public static TopologyNode ProjectPoolWorkspaceRoot(
        StorageSnapshot snapshot,
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes)
    {
        var children = ProjectPoolWorkspace(snapshot, minUnallocatedBytes);
        return new TopologyNode(
            new StorageUnitRef(PoolRowStableId, StorageUnitKind.VirtualDiskGroup, string.Empty, false),
            string.Empty,
            children,
            isSelectable: false,
            childrenLayout: TopologyChildrenLayout.WeightedFlow,
            layoutWeight: Math.Max(1, children.Count));
    }

    public static IReadOnlyList<TopologyNode> ProjectPoolWorkspace(
        StorageSnapshot snapshot,
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var nodes = new List<TopologyNode>();
        var ignore = Math.Max(0, minUnallocatedBytes);

        foreach (var pool in snapshot.StoragePools
                     .Where(pool => pool.IsPrimordial)
                     .Concat(snapshot.StoragePools.Where(pool => !pool.IsPrimordial))
                     .OrderBy(pool => pool.IsPrimordial ? 0 : 1)
                     .ThenBy(pool => IsDraftPool(pool.StableId) ? 1 : 0)
                     .ThenBy(pool => pool.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            nodes.Add(CreateEditPoolNode(pool, snapshot, ignore));
        }

        // Single-draft rule: the plus-pool affordance is present only while
        // no draft pool exists.
        if (!nodes.Any(item => IsDraftPool(item.Unit.StableId)))
        {
            nodes.Add(new TopologyNode(
                new StorageUnitRef(PlusStableId, StorageUnitKind.StoragePool, "+", false),
                "+",
                isSelectable: true,
                childrenLayout: TopologyChildrenLayout.Stack,
                layoutWeight: 1));
        }

        return nodes;
    }

    /// <summary>
    /// A partition holds stored data when it carries a file system and any
    /// of its capacity is consumed. When the snapshot cannot prove the
    /// partition empty, the conservative "holds data" answer applies
    /// (Plan §7.2).
    /// </summary>
    public static bool PartitionHoldsStoredData(PartitionInfo partition) =>
        !string.IsNullOrWhiteSpace(partition.FileSystem)
        && !string.Equals(partition.FileSystem, "RAW", StringComparison.OrdinalIgnoreCase)
        && partition.SizeRemaining < partition.Size;

    public static bool DiskSupportsStructureModification(
        StorageSnapshot snapshot,
        string stableId,
        bool isVirtualDisk)
    {
        var osDisks = isVirtualDisk
            ? snapshot.OsDisks.Where(item => item.VirtualDiskStableId == stableId)
            : snapshot.OsDisks.Where(item => item.PhysicalDiskStableId == stableId);
        return !osDisks.Any(osDisk => snapshot.Partitions.Any(
            partition => partition.OsDiskStableId == osDisk.StableId
                && PartitionHoldsStoredData(partition)));
    }

    public static bool PoolSupportsStructureModification(StorageSnapshot snapshot, string poolId) =>
        snapshot.VirtualDisks
            .Where(item => item.PoolStableId == poolId)
            .All(item => DiskSupportsStructureModification(snapshot, item.StableId, isVirtualDisk: true));

public enum StructureProblemKind
{
    PoolVirtualDiskData,
    DiskHoldsData
}

public sealed record StructureProblem(
    string StableId,
    string DisplayName,
    StructureProblemKind Kind);

    /// <summary>
    /// Lists every storage object involved in executing the pool that does
    /// not support structure modification (Plan §7.3).
    /// </summary>
    public static IReadOnlyList<StructureProblem> CollectStructureProblems(
        StorageSnapshot snapshot,
        string poolId)
    {
        var problems = new List<StructureProblem>();
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == poolId);
        if (pool is null || pool.IsPrimordial)
        {
            return problems;
        }

        if (!PoolSupportsStructureModification(snapshot, poolId))
        {
            problems.Add(new StructureProblem(
                pool.StableId,
                pool.FriendlyName,
                StructureProblemKind.PoolVirtualDiskData));
        }

        foreach (var virtualDisk in snapshot.VirtualDisks
                     .Where(item => item.PoolStableId == poolId))
        {
            if (!DiskSupportsStructureModification(snapshot, virtualDisk.StableId, isVirtualDisk: true))
            {
                problems.Add(new StructureProblem(
                    virtualDisk.StableId,
                    virtualDisk.FriendlyName,
                    StructureProblemKind.DiskHoldsData));
            }
        }

        foreach (var member in snapshot.PhysicalDisks
                     .Where(item => pool.MemberPhysicalDiskIds.Contains(
                         item.StableId, StringComparer.OrdinalIgnoreCase)))
        {
            if (!DiskSupportsStructureModification(snapshot, member.StableId, isVirtualDisk: false))
            {
                problems.Add(new StructureProblem(
                    member.StableId,
                    member.FriendlyName,
                    StructureProblemKind.DiskHoldsData));
            }
        }

        return problems;
    }

    public static bool DiskHasPartitions(StorageSnapshot snapshot, string stableId, bool isVirtualDisk)
    {
        var osDisks = isVirtualDisk
            ? snapshot.OsDisks.Where(item => item.VirtualDiskStableId == stableId)
            : snapshot.OsDisks.Where(item => item.PhysicalDiskStableId == stableId);
        return osDisks.Any(osDisk => snapshot.Partitions.Any(
            partition => partition.OsDiskStableId == osDisk.StableId));
    }

    public static StorageSnapshot InsertDraftPool(StorageSnapshot snapshot, string poolName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var name = string.IsNullOrWhiteSpace(poolName) ? "Pool" : poolName.Trim();
        var draftId = $"{DraftPrefix}{Guid.NewGuid():N}";
        var subsystem = snapshot.StoragePools.FirstOrDefault(pool => pool.IsPrimordial)?.SubsystemStableId
            ?? snapshot.StorageSubsystems.FirstOrDefault()?.StableId;
        var pool = new StoragePoolInfo(
            draftId,
            true,
            name,
            false,
            "Healthy",
            "OK",
            0,
            0,
            subsystem,
            []);
        var tiers = new List<StorageTierInfo>
        {
            DefaultTier(draftId, "SSD", "Performance"),
            DefaultTier(draftId, "HDD", "Capacity")
        };
        if (HasScmDisk(snapshot))
        {
            tiers.Add(DefaultTier(draftId, "SCM", "Dedicated"));
        }

        return snapshot with
        {
            StoragePools = snapshot.StoragePools.Append(pool).ToArray(),
            StorageTiers = snapshot.StorageTiers.Concat(tiers).ToArray()
        };
    }

    public static StorageSnapshot DiscardDraftPool(StorageSnapshot snapshot, string draftId)
    {
        if (!IsDraftPool(draftId))
        {
            throw new InvalidOperationException("Only a draft pool can be discarded.");
        }

        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, draftId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The draft pool was not found.");
        var primordial = snapshot.StoragePools.FirstOrDefault(item => item.IsPrimordial)
            ?? throw new InvalidOperationException("The simulated system has no primordial pool.");
        var members = pool.MemberPhysicalDiskIds;
        return snapshot with
        {
            StoragePools = snapshot.StoragePools
                .Where(item => item.StableId != pool.StableId)
                .Select(item => item.IsPrimordial
                    ? item with
                    {
                        MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Concat(members).ToArray()
                    }
                    : item)
                .ToArray(),
            StorageTiers = snapshot.StorageTiers
                .Where(tier => tier.PoolStableId != pool.StableId)
                .ToArray(),
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(disk => members.Contains(disk.StableId, StringComparer.OrdinalIgnoreCase)
                    ? disk with { PoolStableId = primordial.StableId, CanPool = true }
                    : disk)
                .ToArray()
        };
    }

    public static StorageTierInfo DefaultTier(string poolId, string mediaType, string friendlyName)
    {
        var resiliency = RecommendedResiliency(mediaType, mediaType == "HDD" ? 5 : 2);
        var copies = RecommendedDataCopies(resiliency, mediaType == "HDD" ? 5 : 2);
        var tolerated = RecommendedToleratedFailures(resiliency, copies);
        return new StorageTierInfo(
            $"{poolId}:tier:{mediaType.ToLowerInvariant()}",
            true,
            friendlyName,
            mediaType,
            resiliency,
            0,
            0,
            poolId,
            null,
            [],
            mediaType == "HDD" ? 5 : null,
            65536,
            copies,
            tolerated);
    }

    public static IReadOnlyList<(long Offset, long Size)> UnallocatedGaps(
        OsDiskInfo disk,
        IReadOnlyList<PartitionInfo> partitions)
    {
        var ordered = partitions.OrderBy(item => item.Offset).ToArray();
        var gaps = new List<(long Offset, long Size)>();
        long cursor = 0;
        foreach (var partition in ordered)
        {
            if (partition.Offset > cursor)
            {
                gaps.Add((cursor, partition.Offset - cursor));
            }

            cursor = Math.Max(cursor, partition.Offset + partition.Size);
        }

        if (disk.Size > cursor)
        {
            gaps.Add((cursor, disk.Size - cursor));
        }

        return gaps;
    }

    private static TopologyNode CreatePartitionableDiskNode(
        OsDiskInfo disk,
        StorageSnapshot snapshot,
        long minUnallocatedBytes)
    {
        var partitions = snapshot.Partitions
            .Where(item => item.OsDiskStableId == disk.StableId)
            .OrderBy(item => item.Offset)
            .ToArray();
        var capacityWeights = new List<double>();
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.OsDisk, disk.FriendlyName),
            TopologyProjector.JoinSummary(disk.PartitionStyle, TopologyProjector.FormatBytes(disk.Size)),
            childrenLayout: TopologyChildrenLayout.Flow,
            noWrapChildren: true,
            distributeByCapacity: true,
            capacityWeights: capacityWeights,
            adaptiveHeaderEnabled: true);
        foreach (var (child, capacityBytes) in InterleavePartitionsAndGaps(disk, partitions, minUnallocatedBytes))
        {
            node.Children.Add(child);
            capacityWeights.Add(capacityBytes);
        }

        return node;
    }

    private static IEnumerable<(TopologyNode Node, double CapacityBytes)> InterleavePartitionsAndGaps(
        OsDiskInfo disk,
        IReadOnlyList<PartitionInfo> partitions,
        long minUnallocatedBytes)
    {
        var ordered = partitions.OrderBy(item => item.Offset).ToArray();
        long cursor = 0;
        foreach (var partition in ordered)
        {
            var gap = partition.Offset - cursor;
            if (gap >= minUnallocatedBytes && gap > 0)
            {
                yield return (UnallocatedNode(disk, cursor, gap), gap);
            }

            yield return (
                new TopologyNode(
                    new StorageUnitRef(
                        partition.StableId,
                        StorageUnitKind.Partition,
                        TopologyProjector.PartitionDisplayName(partition),
                        partition.IsStable,
                        disk.StableId),
                    TopologyProjector.JoinSummary(
                        string.IsNullOrWhiteSpace(partition.FileSystem) ? "Unknown" : partition.FileSystem,
                        TopologyProjector.FormatBytes(partition.Size))),
                partition.Size);
            cursor = Math.Max(cursor, partition.Offset + partition.Size);
        }

        var tail = disk.Size - cursor;
        if (tail >= minUnallocatedBytes && tail > 0)
        {
            yield return (UnallocatedNode(disk, cursor, tail), tail);
        }
    }

    private static TopologyNode UnallocatedNode(OsDiskInfo disk, long offset, long size) =>
        new(
            new StorageUnitRef(
                $"{UnallocatedPrefix}{disk.StableId}:{offset}:{size}",
                StorageUnitKind.Partition,
                "Unallocated",
                false,
                disk.StableId),
            TopologyProjector.JoinSummary("Unallocated", TopologyProjector.FormatBytes(size)));

    private static TopologyNode CreateEditPoolNode(
        StoragePoolInfo pool,
        StorageSnapshot snapshot,
        long minUnallocatedBytes)
    {
        var members = snapshot.PhysicalDisks
            .Where(disk => pool.MemberPhysicalDiskIds.Contains(disk.StableId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var poolNode = new TopologyNode(
            new StorageUnitRef(
                pool.StableId,
                StorageUnitKind.StoragePool,
                pool.IsPrimordial ? "Primordial" : pool.FriendlyName,
                pool.IsStable),
            TopologyProjector.JoinSummary(
                $"{members.Count} physical disks",
                TopologyProjector.FormatBytes(members.Sum(item => item.Size))),
            childrenLayout: pool.IsPrimordial ? TopologyChildrenLayout.Flow : TopologyChildrenLayout.Stack,
            layoutWeight: pool.IsPrimordial
                ? Math.Max(1, members.Count)
                : TopologyProjector.CalculatePoolWeight(pool, snapshot));

        if (pool.IsPrimordial)
        {
            foreach (var member in members)
            {
                var diskNode = PhysicalDiskNode(member);
                diskNode.StructureModifiable = DiskSupportsStructureModification(
                    snapshot, member.StableId, isVirtualDisk: false);
                poolNode.Children.Add(diskNode);
            }

            return poolNode;
        }

        poolNode.StructureModifiable = PoolSupportsStructureModification(snapshot, pool.StableId);

        var virtualDisks = snapshot.VirtualDisks
            .Where(disk => disk.PoolStableId == pool.StableId)
            .ToList();
        if (virtualDisks.Count == 0)
        {
            poolNode.Children.Add(new TopologyNode(
                new StorageUnitRef(
                    $"{PendingVirtualPrefix}{pool.StableId}",
                    StorageUnitKind.VirtualDisk,
                    "Not created",
                    false,
                    pool.StableId),
                "Not created"));
        }
        else
        {
            foreach (var virtualDisk in virtualDisks)
            {
                poolNode.Children.Add(CreateVirtualDiskNode(virtualDisk, snapshot, minUnallocatedBytes));
            }
        }

        // Snapshot-driven tier cards, ordered like Manage: a tier renders
        // only when it exists and holds at least one member disk, so a draft
        // pool shows its performance/capacity tiers only after disks of the
        // matching media type join.
        foreach (var tier in snapshot.StorageTiers
                     .Where(item => item.PoolStableId == pool.StableId)
                     .Where(item => item.MemberPhysicalDiskIds.Count > 0)
                     .OrderBy(item => TopologyProjector.TierSortOrder(item.MediaType)))
        {
            poolNode.Children.Add(CreateTierNode(pool, tier, snapshot));
        }

        AddUnallocatedGroup(poolNode, pool, members, snapshot);
        return poolNode;
    }

    private static TopologyNode CreateTierNode(
        StoragePoolInfo pool,
        StorageTierInfo tier,
        StorageSnapshot snapshot)
    {
        var members = snapshot.PhysicalDisks
            .Where(disk => tier.MemberPhysicalDiskIds.Contains(disk.StableId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var node = new TopologyNode(
            new StorageUnitRef(
                tier.StableId,
                StorageUnitKind.StorageTier,
                tier.FriendlyName,
                tier.IsStable,
                pool.StableId),
            TopologyProjector.JoinSummary(
                $"{members.Count} physical disks",
                TopologyProjector.FormatBytes(members.Sum(item => item.Size))),
            childrenLayout: TopologyChildrenLayout.Flow);
        foreach (var member in members)
        {
            var diskNode = PhysicalDiskNode(member);
            diskNode.StructureModifiable = DiskSupportsStructureModification(
                snapshot, member.StableId, isVirtualDisk: false);
            node.Children.Add(diskNode);
        }

        return node;
    }

    /// <summary>
    /// Manage-equivalent Unallocated group: pool members not covered by any
    /// tier stay visible, selectable, and draggable instead of disappearing.
    /// </summary>
    private static void AddUnallocatedGroup(
        TopologyNode poolNode,
        StoragePoolInfo pool,
        IReadOnlyList<PhysicalDiskInfo> members,
        StorageSnapshot snapshot)
    {
        var tierMemberIds = snapshot.StorageTiers
            .Where(item => item.PoolStableId == pool.StableId)
            .SelectMany(item => item.MemberPhysicalDiskIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directMembers = members
            .Where(item => !tierMemberIds.Contains(item.StableId))
            .ToList();
        if (directMembers.Count == 0)
        {
            return;
        }

        var group = new TopologyNode(
            new StorageUnitRef(
                $"group:direct:{pool.StableId}",
                StorageUnitKind.DirectDiskGroup,
                "Unallocated"),
            TopologyProjector.JoinSummary(
                $"{directMembers.Count} physical disks",
                TopologyProjector.FormatBytes(directMembers.Sum(item => item.Size))),
            childrenLayout: TopologyChildrenLayout.Flow);
        foreach (var member in directMembers)
        {
            var diskNode = PhysicalDiskNode(member);
            diskNode.StructureModifiable = DiskSupportsStructureModification(
                snapshot, member.StableId, isVirtualDisk: false);
            group.Children.Add(diskNode);
        }

        poolNode.Children.Add(group);
    }

    private static TopologyNode CreateVirtualDiskNode(
        VirtualDiskInfo disk,
        StorageSnapshot snapshot,
        long minUnallocatedBytes)
    {
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.VirtualDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            TopologyProjector.JoinSummary("Virtual", TopologyProjector.FormatBytes(disk.Size)),
            childrenLayout: TopologyChildrenLayout.Flow);
        foreach (var osDisk in snapshot.OsDisks.Where(item => item.VirtualDiskStableId == disk.StableId))
        {
            foreach (var (child, _) in InterleavePartitionsAndGaps(
                         osDisk,
                         snapshot.Partitions.Where(item => item.OsDiskStableId == osDisk.StableId).ToArray(),
                         minUnallocatedBytes))
            {
                node.Children.Add(child);
            }
        }

        return node;
    }

    private static TopologyNode PhysicalDiskNode(PhysicalDiskInfo disk) =>
        new(
            new StorageUnitRef(disk.StableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            TopologyProjector.JoinSummary(NormalizeMedia(disk.MediaType), TopologyProjector.FormatBytes(disk.Size)));

    public static StorageSnapshot MoveDiskToPool(StorageSnapshot snapshot, string diskId, string poolId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected physical disk was not found.");
        if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
        {
            throw new InvalidOperationException("Boot, system, page-file, and crash-dump disks cannot move between pools.");
        }

        var media = RequiredTierMedia(disk.MediaType);
        var target = snapshot.StoragePools.FirstOrDefault(item =>
            item.StableId.Equals(poolId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The target pool was not found.");
        var pools = snapshot.StoragePools
            .Select(pool =>
            {
                var members = pool.MemberPhysicalDiskIds
                    .Where(id => !id.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (pool.StableId == target.StableId)
                {
                    members.Add(disk.StableId);
                }

                return pool with
                {
                    MemberPhysicalDiskIds = members,
                    Size = snapshot.PhysicalDisks
                        .Where(item => members.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
                            || (pool.StableId == target.StableId && item.StableId == disk.StableId))
                        .Sum(item => item.Size)
                };
            })
            .ToArray();
        var tiers = snapshot.StorageTiers
            .Select(tier => tier with
            {
                MemberPhysicalDiskIds = tier.MemberPhysicalDiskIds
                    .Where(id => !id.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            })
            .ToList();
        if (!target.IsPrimordial)
        {
            var existing = tiers.FirstOrDefault(tier =>
                tier.PoolStableId == target.StableId && NormalizeMedia(tier.MediaType) == media);
            if (existing is null)
            {
                var created = DefaultTier(
                    target.StableId,
                    media,
                    media switch
                    {
                        "SSD" => "Performance",
                        "HDD" => "Capacity",
                        _ => "Dedicated"
                    });
                tiers.Add(created with { MemberPhysicalDiskIds = [disk.StableId] });
            }
            else
            {
                var index = tiers.FindIndex(item => item.StableId == existing.StableId);
                tiers[index] = existing with
                {
                    MemberPhysicalDiskIds = existing.MemberPhysicalDiskIds.Append(disk.StableId).ToArray()
                };
            }
        }

        var moved = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId == disk.StableId
                    ? item with { PoolStableId = target.StableId, CanPool = target.IsPrimordial }
                    : item)
                .ToArray(),
            StoragePools = pools,
            StorageTiers = tiers
        };
        return IsDraftPool(target.StableId)
            ? RefreshDraftRecommendations(moved, target.StableId)
            : moved;
    }

    public static StorageSnapshot RefreshDraftRecommendations(StorageSnapshot snapshot, string poolId)
    {
        if (!IsDraftPool(poolId))
        {
            return snapshot;
        }

        return snapshot with
        {
            StorageTiers = snapshot.StorageTiers
                .Select(tier =>
                {
                    if (tier.PoolStableId != poolId)
                    {
                        return tier;
                    }

                    var members = snapshot.PhysicalDisks
                        .Where(disk => tier.MemberPhysicalDiskIds.Contains(
                            disk.StableId, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                    var media = NormalizeMedia(tier.MediaType);
                    var resiliency = RecommendedResiliency(media, members.Length);
                    var copies = RecommendedDataCopies(resiliency, members.Length);
                    return tier with
                    {
                        ResiliencySettingName = resiliency,
                        NumberOfDataCopies = copies,
                        PhysicalDiskRedundancy = RecommendedToleratedFailures(resiliency, copies),
                        NumberOfColumns = media == "HDD" ? RecommendedCapacityColumns(members) : null,
                        Size = members.Sum(item => item.Size),
                        FootprintOnPool = members.Sum(item => item.Size)
                    };
                })
                .ToArray()
        };
    }

    public static ManageTopologyNodeView ToManageView(
        TopologyNode node,
        SystemId systemId,
        string occurrenceKey)
    {
        var role = MapRole(node.Unit.Kind);
        var children = node.Children
            .Select((child, index) => ToManageView(
                child,
                systemId,
                $"{occurrenceKey}/{index}:{MapRole(child.Unit.Kind)}"))
            .ToArray();
        return new ManageTopologyNodeView(
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
            children,
            node.NoWrapChildren,
            node.DistributeByCapacity,
            node.CapacityWeights,
            node.StructureModifiable,
            node.AdaptiveHeaderEnabled);
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
        StorageUnitKind.VirtualDiskGroup => ManageObjectRole.VirtualDiskGroup,
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
        ManageObjectRole.Volume => StorageObjectKind.Partition,
        ManageObjectRole.NetworkGroup or ManageObjectRole.OtherGroup
            or ManageObjectRole.DirectDiskGroup
            or ManageObjectRole.VirtualDiskGroup => StorageObjectKind.LogicalGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
