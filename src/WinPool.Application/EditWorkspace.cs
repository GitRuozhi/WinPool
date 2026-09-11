using WinPool.Domain;

namespace WinPool.Application;

public static class EditWorkspace
{
    public const string PlusStableId = "edit:plus";
    public const string PoolRowStableId = "edit:pool-row";
    public const string PartitionRowStableId = "edit:partition-row";
    public const string DraftPrefix = "edit:draft:";
    public const string UnallocatedPrefix = "unallocated:";
    public const string RetiredLayerPrefix = "edit:retired-layer:";
    public const string HotSpareLayerPrefix = "edit:hotspare-layer:";
    public const long DefaultUnallocatedIgnoreBytes = 8L * 1024 * 1024;

    public static bool IsPlus(string? id) =>
        string.Equals(id, PlusStableId, StringComparison.OrdinalIgnoreCase);

    public const string DraftVirtualDiskPrefix = "edit:draft-vdisk:";

    public static bool IsDraftVirtualDisk(string? id) =>
        id is not null && id.StartsWith(DraftVirtualDiskPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsRetiredLayer(string? id) =>
        id is not null && id.StartsWith(RetiredLayerPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsHotSpareLayer(string? id) =>
        id is not null && id.StartsWith(HotSpareLayerPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSimulatedLayer(string? id) => IsRetiredLayer(id) || IsHotSpareLayer(id);

    public static string SimulatedLayerId(string poolId, string usage) => usage switch
    {
        "Retired" => $"{RetiredLayerPrefix}{poolId}",
        "HotSpare" => $"{HotSpareLayerPrefix}{poolId}",
        _ => throw new ArgumentOutOfRangeException(nameof(usage))
    };

    public static string DiskUsage(PhysicalDiskInfo disk) =>
        PhysicalDiskUsage.ToSimulatedLayer(disk.Usage);

    public static bool IsPoolRow(string? id) =>
        string.Equals(id, PoolRowStableId, StringComparison.OrdinalIgnoreCase);

    public static bool IsDraftPool(string? id) =>
        id is not null && id.StartsWith(DraftPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnallocated(string? id) =>
        id is not null && id.StartsWith(UnallocatedPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsPartitionTableInitialized(OsDiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return !string.IsNullOrWhiteSpace(disk.PartitionStyle)
            && !string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase);
    }

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

        return NormalizeMedia(mediaType) == "HDD"
            ? memberCount >= 3 ? "Parity" : "Simple"
            : "Mirror";
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

        // One rule drives this page: the OS-disk view of every physical disk
        // that is free in the primordial pool, plus the OS-disk view of every
        // virtual disk in any pool. Pooled physical disks, network disks, and
        // other external groups do not appear here.
        var primordialMembers = snapshot.StoragePools
            .FirstOrDefault(pool => pool.IsPrimordial)
            ?.MemberPhysicalDiskIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var disks = snapshot.OsDisks
            .Where(disk => !string.IsNullOrWhiteSpace(disk.VirtualDiskStableId)
                || (!string.IsNullOrWhiteSpace(disk.PhysicalDiskStableId)
                    && primordialMembers.Contains(disk.PhysicalDiskStableId)))
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
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes,
        StorageSnapshot? committed = null,
        IReadOnlyCollection<string>? visibleSimulatedLayers = null)
    {
        var children = ProjectPoolWorkspace(snapshot, minUnallocatedBytes, committed, visibleSimulatedLayers);
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
        long minUnallocatedBytes = DefaultUnallocatedIgnoreBytes,
        StorageSnapshot? committed = null,
        IReadOnlyCollection<string>? visibleSimulatedLayers = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var baseline = committed ?? snapshot;
        var nodes = new List<TopologyNode>();
        var ignore = Math.Max(0, minUnallocatedBytes);

        foreach (var pool in snapshot.StoragePools
                     .Where(pool => pool.IsPrimordial)
                     .Concat(snapshot.StoragePools.Where(pool => !pool.IsPrimordial))
                     .OrderBy(pool => pool.IsPrimordial ? 0 : 1)
                     .ThenBy(pool => IsDraftPool(pool.StableId) ? 1 : 0)
                     .ThenBy(pool => pool.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            nodes.Add(CreateEditPoolNode(pool, snapshot, ignore, baseline, visibleSimulatedLayers));
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
    /// Authoritative stored-data predicate for editor status, rules, and
    /// plan risk. A formatted partition holds data when any capacity is
    /// consumed. RAW and unformatted partitions do not expose file-system
    /// data and remain data-free even if a provider reports no free bytes.
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

    public static bool PoolSupportsStructureModification(StorageSnapshot snapshot, string poolId)
    {
        // Every virtual disk must be data-free...
        if (!snapshot.VirtualDisks
                .Where(item => item.PoolStableId == poolId)
                .All(item => DiskSupportsStructureModification(snapshot, item.StableId, isVirtualDisk: true)))
        {
            return false;
        }

        // ...and so must every member disk: a draft pool into which a
        // data-bearing disk was dragged can never be executed, and the pool
        // icon must say so instead of hiding behind the (empty) virtual-disk
        // check.
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == poolId);
        if (pool is null)
        {
            return true;
        }

        return !snapshot.PhysicalDisks
            .Where(item => pool.MemberPhysicalDiskIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase))
            .Any(item => snapshot.OsDisks
                .Where(osDisk => osDisk.PhysicalDiskStableId == item.StableId)
                .Any(osDisk => snapshot.Partitions.Any(
                    partition => partition.OsDiskStableId == osDisk.StableId
                        && PartitionHoldsStoredData(partition))));
    }

    public static bool DiskHoldsStoredData(
        StorageSnapshot snapshot,
        string stableId,
        bool isVirtualDisk = false) =>
        !DiskSupportsStructureModification(snapshot, stableId, isVirtualDisk);

    public static bool PoolHoldsStoredData(StorageSnapshot snapshot, string poolId) =>
        !PoolSupportsStructureModification(snapshot, poolId);

    public enum DiskEvictCheck
    {
        Allowed,
        ConfirmPageFile,
        ConfirmCrashDump,
        DeniedSystem
    }

    public static DiskEvictCheck ClassifyDiskEvict(PhysicalDiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.IsBoot || disk.IsSystem)
        {
            return DiskEvictCheck.DeniedSystem;
        }

        if (disk.IsPageFile)
        {
            return DiskEvictCheck.ConfirmPageFile;
        }

        if (disk.IsCrashDump)
        {
            return DiskEvictCheck.ConfirmCrashDump;
        }

        return DiskEvictCheck.Allowed;
    }

    public static bool DiskIsAssignedToTier(StorageSnapshot snapshot, string diskId) =>
        snapshot.StorageTiers.Any(tier =>
            tier.MemberPhysicalDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> TierMemberIds(StorageSnapshot snapshot, string poolId) =>
        snapshot.StorageTiers
            .Where(tier => string.Equals(tier.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(tier => tier.MemberPhysicalDiskIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static StorageSnapshot ClearEvictableSpecialRoles(StorageSnapshot snapshot, string diskId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase)
                    ? item with { IsPageFile = false, IsCrashDump = false }
                    : item)
                .ToArray()
        };
    }

    /// <summary>
    /// Keeps the disk in its pool and removes it from every tier so it
    /// appears under 未划层. Boot and system disks are refused.
    /// </summary>
    public static StorageSnapshot EvictDiskToUnallocated(StorageSnapshot snapshot, string diskId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected physical disk was not found.");
        if (disk.IsBoot || disk.IsSystem)
        {
            throw new InvalidOperationException("Boot and system disks cannot leave a pool.");
        }

        if (string.IsNullOrEmpty(disk.PoolStableId))
        {
            throw new InvalidOperationException("The selected physical disk is not in a pool.");
        }

        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            item.StableId.Equals(disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected pool was not found.");
        if (pool.IsPrimordial)
        {
            throw new InvalidOperationException("Disks cannot be evicted from the primordial pool.");
        }

        return snapshot with
        {
            StorageTiers = snapshot.StorageTiers
                .Select(tier => tier with
                {
                    MemberPhysicalDiskIds = tier.MemberPhysicalDiskIds
                        .Where(id => !id.Equals(diskId, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                })
                .ToArray()
        };
    }

    /// <summary>
    /// Moves a pooled physical disk into or out of the retired / hot-spare
    /// simulated layer (V0.47 control spec §A). The disk stays in its pool
    /// and leaves every real tier. Usage is "", "Retired", or "HotSpare".
    /// Boot, system, page-file, and crash-dump disks are refused; the page
    /// stages removal of an evictable special role before calling this.
    /// </summary>
    public static StorageSnapshot SetDiskUsage(StorageSnapshot snapshot, string diskId, string usage)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (usage is not ("" or "Retired" or "HotSpare"))
        {
            throw new InvalidOperationException(
                $"Unknown simulated layer usage '{usage}'.");
        }

        var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected physical disk was not found.");
        if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
        {
            throw new InvalidOperationException(
                "Boot, system, page-file, and crash-dump disks cannot enter a simulated layer.");
        }

        if (string.IsNullOrEmpty(disk.PoolStableId))
        {
            throw new InvalidOperationException("The selected physical disk is not in a pool.");
        }

        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            item.StableId.Equals(disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected pool was not found.");
        if (pool.IsPrimordial)
        {
            throw new InvalidOperationException("Primordial disks cannot enter a simulated layer.");
        }

        if (IsDraftPool(pool.StableId))
        {
            throw new InvalidOperationException(
                "Draft pool members cannot enter a simulated layer; apply the pool first.");
        }

        var current = DiskUsage(disk);
        if (current == usage)
        {
            return snapshot;
        }

        var wantLayer = usage.Length > 0;
        return snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        Usage = PhysicalDiskUsage.FromSimulatedLayer(usage)
                    }
                    : item)
                .ToArray(),
            StorageTiers = wantLayer
                ? snapshot.StorageTiers
                    .Select(tier => tier with
                    {
                        MemberPhysicalDiskIds = tier.MemberPhysicalDiskIds
                            .Where(id => !id.Equals(diskId, StringComparison.OrdinalIgnoreCase))
                            .ToArray()
                    })
                    .ToArray()
                : snapshot.StorageTiers
        };
    }

    /// <summary>
    /// A disk cannot leave its current pool when it is a boot/system disk,
    /// still holds a page-file or crash-dump role, or is an original member
    /// of a data-bearing real pool that is still assigned to a tier.
    /// Disks already in 未划层, draft members, and disks moved in this
    /// session can still leave.
    /// </summary>
    public static bool DiskCannotLeave(
        StorageSnapshot working,
        StorageSnapshot committed,
        PhysicalDiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
        {
            return true;
        }

        var poolId = disk.PoolStableId;
        if (string.IsNullOrEmpty(poolId) || IsDraftPool(poolId))
        {
            return false;
        }

        var pool = working.StoragePools.FirstOrDefault(item =>
            item.StableId.Equals(poolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null || pool.IsPrimordial || !PoolHoldsStoredData(working, poolId))
        {
            return false;
        }

        if (!DiskIsAssignedToTier(working, disk.StableId))
        {
            return false;
        }

        var committedDisk = committed.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
        return committedDisk is not null
            && string.Equals(committedDisk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// True when the working copy holds unexecuted modifications for the
    /// object: a moved disk, a draft pool, or a pool whose membership or
    /// virtual-disk set differs from the committed snapshot (Plan §7).
    /// </summary>
    public static bool HasPendingModifications(
        StorageSnapshot working,
        StorageSnapshot committed,
        string stableId)
    {
        var workingDisk = working.PhysicalDisks.FirstOrDefault(item => item.StableId == stableId);
        if (workingDisk is not null)
        {
            var committedDisk = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == stableId);
            if (committedDisk is null
                || !string.Equals(
                    committedDisk.PoolStableId,
                    workingDisk.PoolStableId,
                    StringComparison.OrdinalIgnoreCase)
                || committedDisk.IsPageFile != workingDisk.IsPageFile
                || committedDisk.IsCrashDump != workingDisk.IsCrashDump)
            {
                return true;
            }

            return DiskIsAssignedToTier(working, stableId) != DiskIsAssignedToTier(committed, stableId);
        }

        var workingPool = working.StoragePools.FirstOrDefault(item => item.StableId == stableId);
        if (workingPool is not null)
        {
            if (IsDraftPool(stableId))
            {
                return true;
            }

            var committedPool = committed.StoragePools.FirstOrDefault(item => item.StableId == stableId);
            if (committedPool is null)
            {
                return true;
            }

            if (!workingPool.MemberPhysicalDiskIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(committedPool.MemberPhysicalDiskIds))
            {
                return true;
            }

            var workingVirtualDisks = working.VirtualDisks
                .Where(item => item.PoolStableId == stableId)
                .Select(item => item.StableId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var committedVirtualDisks = committed.VirtualDisks
                .Where(item => item.PoolStableId == stableId)
                .Select(item => item.StableId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!workingVirtualDisks.SetEquals(committedVirtualDisks))
            {
                return true;
            }

            var workingTierMembers = TierMemberIds(working, stableId);
            var committedTierMembers = TierMemberIds(committed, stableId);
            return !workingTierMembers.SetEquals(committedTierMembers);
        }

        return false;
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
        if (snapshot.StoragePools.Any(item => IsDraftPool(item.StableId)))
        {
            throw new InvalidOperationException("Only one draft pool can exist at a time.");
        }

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
        if (!IsPartitionTableInitialized(disk))
        {
            return node;
        }

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
                        string.IsNullOrWhiteSpace(partition.FileSystem) ? "RAW" : partition.FileSystem,
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
        long minUnallocatedBytes,
        StorageSnapshot committed,
        IReadOnlyCollection<string>? visibleSimulatedLayers)
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
                poolNode.Children.Add(PhysicalDiskNode(member, snapshot, committed));
            }

            return poolNode;
        }

        poolNode.ShowsEditStatus = true;
        poolNode.HasStoredData = PoolHoldsStoredData(snapshot, pool.StableId);

        AddVirtualDisks(poolNode, pool, snapshot, minUnallocatedBytes);

        // Snapshot-driven tier cards, ordered like Manage: a tier renders
        // only when it exists and holds at least one member disk, so a draft
        // pool shows its performance/capacity tiers only after disks of the
        // matching media type join.
        foreach (var tier in snapshot.StorageTiers
                     .Where(item => item.PoolStableId == pool.StableId)
                     .Where(item => item.MemberPhysicalDiskIds.Count > 0)
                     .OrderBy(item => TopologyProjector.TierSortOrder(item.MediaType)))
        {
            poolNode.Children.Add(CreateTierNode(pool, tier, snapshot, committed));
        }

        AddSimulatedLayers(poolNode, pool, members, snapshot, committed, visibleSimulatedLayers);
        AddUnallocatedGroup(poolNode, pool, members, snapshot, committed);
        return poolNode;
    }

    /// <summary>
    /// Manage-equivalent virtual-disk row: one disk stays a direct stack
    /// child; two or more sit in a headerless VirtualDiskGroup so they
    /// share a horizontal Flow instead of stacking in the pool.
    /// </summary>
    private static void AddVirtualDisks(
        TopologyNode poolNode,
        StoragePoolInfo pool,
        StorageSnapshot snapshot,
        long minUnallocatedBytes)
    {
        var virtualNodes = snapshot.VirtualDisks
            .Where(disk => disk.PoolStableId == pool.StableId)
            .Select(disk => CreateVirtualDiskNode(disk, snapshot, minUnallocatedBytes))
            .ToList();
        if (virtualNodes.Count == 1)
        {
            poolNode.Children.Add(virtualNodes[0]);
            return;
        }

        if (virtualNodes.Count == 0)
        {
            return;
        }

        var virtualGroup = new TopologyNode(
            new StorageUnitRef(
                $"group:vdisk:{pool.StableId}",
                StorageUnitKind.VirtualDiskGroup,
                "Virtual disks"),
            TopologyProjector.JoinSummary($"{virtualNodes.Count} virtual disks"),
            isSelectable: false,
            childrenLayout: TopologyChildrenLayout.Flow,
            layoutWeight: virtualNodes.Count);
        virtualGroup.Children.AddRange(virtualNodes);
        poolNode.Children.Add(virtualGroup);
    }

    private static TopologyNode CreateTierNode(
        StoragePoolInfo pool,
        StorageTierInfo tier,
        StorageSnapshot snapshot,
        StorageSnapshot committed)
    {
        var members = snapshot.PhysicalDisks
            .Where(disk => tier.MemberPhysicalDiskIds.Contains(disk.StableId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        // The tier card mirrors every spec the property panel saves: the
        // resiliency / stripe spec, and the tier's CAPACITY reservation
        // (an unset capacity means "member capacity"). Saved edits are
        // therefore visible on the left after each save.
        var tierCapacity = tier.Size > 0 ? tier.Size : members.Sum(item => item.Size);
        var spec = tier.Interleave is { } interleave and > 0
            ? $"{tier.ResiliencySettingName} {interleave / 1024}K"
            : tier.ResiliencySettingName;
        var node = new TopologyNode(
            new StorageUnitRef(
                tier.StableId,
                StorageUnitKind.StorageTier,
                tier.FriendlyName,
                tier.IsStable,
                pool.StableId),
            TopologyProjector.JoinSummary(
                spec,
                $"{members.Count} physical disks",
                TopologyProjector.FormatBytes(tierCapacity)),
            childrenLayout: TopologyChildrenLayout.Flow);
        foreach (var member in members)
        {
            node.Children.Add(PhysicalDiskNode(member, snapshot, committed));
        }

        return node;
    }

    /// <summary>
    /// Manage-equivalent Unallocated group: pool members not covered by any
    /// tier stay visible, selectable, and draggable instead of disappearing.
    /// Retired and hot-spare disks belong to their simulated layers, never
    /// to this group.
    /// </summary>
    private static void AddUnallocatedGroup(
        TopologyNode poolNode,
        StoragePoolInfo pool,
        IReadOnlyList<PhysicalDiskInfo> members,
        StorageSnapshot snapshot,
        StorageSnapshot committed)
    {
        var tierMemberIds = snapshot.StorageTiers
            .Where(item => item.PoolStableId == pool.StableId)
            .SelectMany(item => item.MemberPhysicalDiskIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Role disks always belong to their simulated layer, which is drawn
        // whenever it holds disks; they never appear here.
        var directMembers = members
            .Where(item => !tierMemberIds.Contains(item.StableId)
                && !item.IsRetired
                && !item.IsHotSpare)
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
            group.Children.Add(PhysicalDiskNode(member, snapshot, committed));
        }

        poolNode.Children.Add(group);
    }

    /// <summary>
    /// Retired and hot-spare simulated layers (V0.47 control spec §3, §4):
    /// drawn only while the page switch shows that layer, and drawn even
    /// when empty so disks can be dropped into it. The layer node itself is
    /// a drop target; its id is recognized by <see cref="IsSimulatedLayer"/>.
    /// </summary>
    private static void AddSimulatedLayers(
        TopologyNode poolNode,
        StoragePoolInfo pool,
        IReadOnlyList<PhysicalDiskInfo> members,
        StorageSnapshot snapshot,
        StorageSnapshot committed,
        IReadOnlyCollection<string>? visibleSimulatedLayers)
    {
        if (pool.IsPrimordial || visibleSimulatedLayers is null)
        {
            return;
        }

        foreach (var usage in new[] { "HotSpare", "Retired" })
        {
            var layerMembers = members
                .Where(item => usage == "Retired" ? item.IsRetired : item.IsHotSpare)
                .ToList();
            // A layer that holds disks is always drawn; the switch only
            // previews an empty layer so disks can be dropped into it.
            var switchVisible = visibleSimulatedLayers?.Contains(usage, StringComparer.OrdinalIgnoreCase) == true;
            if (layerMembers.Count == 0 && !switchVisible)
            {
                continue;
            }

            var node = new TopologyNode(
                new StorageUnitRef(
                    SimulatedLayerId(pool.StableId, usage),
                    StorageUnitKind.StorageTier,
                    usage == "HotSpare" ? "Hot spare" : "Retired",
                    false,
                    pool.StableId),
                TopologyProjector.JoinSummary(
                    $"{layerMembers.Count} physical disks",
                    TopologyProjector.FormatBytes(layerMembers.Sum(item => item.Size))),
                childrenLayout: TopologyChildrenLayout.Flow);
            foreach (var member in layerMembers)
            {
                node.Children.Add(PhysicalDiskNode(member, snapshot, committed));
            }

            poolNode.Children.Add(node);
        }
    }

    private static TopologyNode CreateVirtualDiskNode(
        VirtualDiskInfo disk,
        StorageSnapshot snapshot,
        long minUnallocatedBytes)
    {
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.VirtualDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            // Draft placeholders show no size: the virtual disk does not
            // exist yet and its capacity is not decided.
            IsDraftVirtualDisk(disk.StableId)
                ? TopologyProjector.JoinSummary("Virtual", "unknown size")
                : TopologyProjector.JoinSummary("Virtual", TopologyProjector.FormatBytes(disk.Size)),
            childrenLayout: TopologyChildrenLayout.Flow)
        {
            ShowsEditStatus = true,
            HasStoredData = DiskHoldsStoredData(snapshot, disk.StableId, isVirtualDisk: true)
        };
        // Edit lower shows no partition strips anywhere (Plan §3): virtual
        // disks render as bare cards. Data / pending marks still apply.
        return node;
    }

    private static TopologyNode PhysicalDiskNode(
        PhysicalDiskInfo disk,
        StorageSnapshot working,
        StorageSnapshot committed)
    {
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            TopologyProjector.JoinSummary(NormalizeMedia(disk.MediaType), TopologyProjector.FormatBytes(disk.Size)))
        {
            ShowsEditStatus = true,
            HasStoredData = DiskHoldsStoredData(working, disk.StableId),
            CannotLeave = DiskCannotLeave(working, committed, disk)
        };
        return node;
    }

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
                // A tier starts sized to its member capacity, never at zero:
                // the capacity field means "max usable size" (recommended
                // value = sum of member disks).
                tiers.Add(created with
                {
                    MemberPhysicalDiskIds = [disk.StableId],
                    Size = disk.Size,
                    FootprintOnPool = disk.Size
                });
            }
            else
            {
                var index = tiers.FindIndex(item => item.StableId == existing.StableId);
                var nextMembers = existing.MemberPhysicalDiskIds.Append(disk.StableId).ToArray();
                var sizeNeedsReset = existing.Size <= 0 || existing.MemberPhysicalDiskIds.Count == 0;
                tiers[index] = existing with
                {
                    MemberPhysicalDiskIds = nextMembers,
                    Size = sizeNeedsReset
                        ? snapshot.PhysicalDisks
                            .Where(item => nextMembers.Contains(item.StableId, StringComparer.OrdinalIgnoreCase))
                            .Sum(item => item.Size)
                        : existing.Size,
                    FootprintOnPool = sizeNeedsReset
                        ? snapshot.PhysicalDisks
                            .Where(item => nextMembers.Contains(item.StableId, StringComparer.OrdinalIgnoreCase))
                            .Sum(item => item.Size)
                        : existing.FootprintOnPool
                };
            }
        }

        var moved = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId == disk.StableId
                    ? item with
                    {
                        PoolStableId = target.StableId,
                        CanPool = target.IsPrimordial,
                        // Leaving a simulated layer returns the disk to the
                        // data path; a moved disk never keeps a layer role.
                        Usage = PhysicalDiskUsage.AutoSelect
                    }
                    : item)
                .ToArray(),
            StoragePools = pools,
            StorageTiers = tiers
        };
        var result = IsDraftPool(target.StableId)
            ? RefreshDraftRecommendations(moved, target.StableId)
            : moved;
        return target.IsPrimordial
            ? EnsureFreeDisksHaveOsDisks(result, [disk.StableId])
            : result;
    }

    /// <summary>
    /// Ensures every free physical disk (a primordial member) exposes an
    /// OS-disk view, so the Disk partition editor can always show and
    /// initialize a disk that is not currently pooled.
    /// </summary>
    public static StorageSnapshot EnsureFreeDisksHaveOsDisks(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.StoragePools.FirstOrDefault(pool => pool.IsPrimordial) is { } primordial
            ? EnsureFreeDisksHaveOsDisks(snapshot, primordial.MemberPhysicalDiskIds)
            : snapshot;
    }

    /// <summary>
    /// A physical disk that has left a pool (dissolved pool, or a member
    /// dragged back to the primordial pool) is a free Windows disk and must
    /// expose an uninitialized OS-disk view so the Disk partition editor can
    /// show and initialize it. Pooled member disks stay invisible there
    /// (V0.47 design §11).
    /// </summary>
    public static StorageSnapshot EnsureFreeDisksHaveOsDisks(
        StorageSnapshot snapshot,
        IReadOnlyCollection<string> physicalDiskIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (physicalDiskIds.Count == 0)
        {
            return snapshot;
        }

        var primordial = snapshot.StoragePools.FirstOrDefault(item => item.IsPrimordial);
        if (primordial is null)
        {
            return snapshot;
        }

        var freeMembers = new HashSet<string>(
            primordial.MemberPhysicalDiskIds,
            StringComparer.OrdinalIgnoreCase);
        var alreadyMapped = snapshot.OsDisks
            .Where(item => !string.IsNullOrWhiteSpace(item.PhysicalDiskStableId))
            .Select(item => item.PhysicalDiskStableId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedNumbers = snapshot.OsDisks.Select(item => item.Number).ToHashSet();
        var nextNumber = usedNumbers.Count > 0 ? usedNumbers.Max() + 1 : 0;
        var added = new List<OsDiskInfo>();
        foreach (var diskId in physicalDiskIds)
        {
            if (!freeMembers.Contains(diskId) || alreadyMapped.Contains(diskId))
            {
                continue;
            }

            var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase));
            if (disk is null)
            {
                continue;
            }

            var candidate = disk.DeviceId ?? nextNumber;
            var number = usedNumbers.Contains(candidate) ? nextNumber++ : candidate;
            usedNumbers.Add(number);
            added.Add(new OsDiskInfo(
                $"{disk.StableId}:os",
                disk.FriendlyName,
                number,
                "RAW",
                disk.Size,
                false,
                false,
                false,
                disk.StableId,
                null));
        }

        return added.Count == 0
            ? snapshot
            : snapshot with { OsDisks = snapshot.OsDisks.Concat(added).ToArray() };
    }

    /// <summary>
    /// A tier whose capacity was never set (Size <= 0) means "the member
    /// capacity": its recommended maximum. Normalizes the working copy so the
    /// form shows and edits a real number instead of a bare zero.
    /// </summary>
    public static StorageSnapshot NormalizeTierCapacities(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changed = false;
        var tiers = snapshot.StorageTiers
            .Select(tier =>
            {
                if (tier.Size > 0 || tier.MemberPhysicalDiskIds.Count == 0)
                {
                    return tier;
                }

                var members = snapshot.PhysicalDisks
                    .Where(disk => tier.MemberPhysicalDiskIds.Contains(
                        disk.StableId, StringComparer.OrdinalIgnoreCase)
                        && PhysicalDiskUsage.ContributesDataCapacity(disk.Usage))
                    .ToArray();
                if (members.Length == 0)
                {
                    return tier;
                }

                var copies = tier.NumberOfDataCopies
                    ?? (string.Equals(tier.ResiliencySettingName, "Mirror", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
                ConservativeCapacityEstimate estimate;
                try
                {
                    estimate = ConservativeCapacity.PlanLogicalUpperBound(
                        members.Select(disk => disk.Size).ToArray(),
                        tier.ResiliencySettingName,
                        Math.Max(1, copies),
                        tier.NumberOfColumns,
                        tier.PhysicalDiskRedundancy ?? 0,
                        tier.Interleave ?? 65536);
                }
                catch (ArgumentException)
                {
                    return tier;
                }
                if (estimate.AlignedLogicalBytes <= 0)
                {
                    return tier;
                }

                changed = true;
                return tier with
                {
                    Size = estimate.AlignedLogicalBytes,
                    FootprintOnPool = estimate.PhysicalFootprintBytes,
                    SizeSource = CapacitySourceKind.SimulatedEstimate
                };
            })
            .ToArray();
        return changed ? snapshot with { StorageTiers = tiers } : snapshot;
    }

    /// <summary>
    /// True when the working copy holds any structural draft change against
    /// the committed snapshot: pool or virtual-disk existence, disk pool
    /// membership, simulated-layer role, or real-tier membership.
    /// </summary>
    /// <summary>
    /// True when the working copy changed pool-level property values that a
    /// structural diff does not see: pool name, virtual-disk names, or tier
    /// parameters. Partition-level form values are not object-backed and are
    /// tracked by the page's own dirty flag.
    /// </summary>
    public static bool HasPoolPropertyChanges(
        StorageSnapshot working,
        StorageSnapshot committed,
        string poolId)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        var workingPool = working.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase));
        var committedPool = committed.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase));
        if (workingPool is null
            || committedPool is null
            || workingPool.IsPrimordial
            || IsDraftPool(poolId))
        {
            return false;
        }

        if (!string.Equals(
                workingPool.FriendlyName,
                committedPool.FriendlyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var workingNames = working.VirtualDisks
            .Where(item => string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)
                && !IsDraftVirtualDisk(item.StableId))
            .ToDictionary(item => item.StableId, item => item.FriendlyName, StringComparer.OrdinalIgnoreCase);
        var committedNames = committed.VirtualDisks
            .Where(item => string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.StableId, item => item.FriendlyName, StringComparer.OrdinalIgnoreCase);
        if (!workingNames.OrderBy(pair => pair.Key).SequenceEqual(
                committedNames.OrderBy(pair => pair.Key)))
        {
            return true;
        }

        foreach (var workingTier in working.StorageTiers.Where(item =>
                     string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)))
        {
            var committedTier = committed.StorageTiers.FirstOrDefault(item =>
                string.Equals(item.StableId, workingTier.StableId, StringComparison.OrdinalIgnoreCase));
            if (committedTier is null)
            {
                continue;
            }

            if (!string.Equals(
                    workingTier.ResiliencySettingName,
                    committedTier.ResiliencySettingName,
                    StringComparison.OrdinalIgnoreCase)
                || workingTier.Interleave != committedTier.Interleave
                || workingTier.NumberOfDataCopies != committedTier.NumberOfDataCopies
                || workingTier.PhysicalDiskRedundancy != committedTier.PhysicalDiskRedundancy
                || workingTier.NumberOfColumns != committedTier.NumberOfColumns
                || workingTier.Size != committedTier.Size)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAnyPoolPropertyChanges(StorageSnapshot working, StorageSnapshot committed) =>
        working.StoragePools
            .Where(pool => !pool.IsPrimordial && !IsDraftPool(pool.StableId))
            .Any(pool => HasPoolPropertyChanges(working, committed, pool.StableId));

    public static bool HasStructuralChanges(StorageSnapshot working, StorageSnapshot committed)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        var workingPools = working.StoragePools
            .Where(pool => !pool.IsPrimordial)
            .Select(pool => pool.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committedPools = committed.StoragePools
            .Where(pool => !pool.IsPrimordial)
            .Select(pool => pool.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!workingPools.SetEquals(committedPools))
        {
            return true;
        }

        var workingVdisks = working.VirtualDisks
            .Select(disk => disk.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committedVdisks = committed.VirtualDisks
            .Select(disk => disk.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!workingVdisks.SetEquals(committedVdisks))
        {
            return true;
        }

        foreach (var disk in working.PhysicalDisks)
        {
            var committedDisk = committed.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
            if (committedDisk is null)
            {
                return true;
            }

            if (!string.Equals(committedDisk.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase)
                || committedDisk.IsRetired != disk.IsRetired
                || committedDisk.IsHotSpare != disk.IsHotSpare
                || committedDisk.IsPageFile != disk.IsPageFile
                || committedDisk.IsCrashDump != disk.IsCrashDump)
            {
                return true;
            }

            var workingTiers = TierIdsContaining(working, disk.StableId);
            var committedTiers = TierIdsContaining(committed, disk.StableId);
            if (!workingTiers.SetEquals(committedTiers))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> TierIdsContaining(StorageSnapshot snapshot, string diskId) =>
        snapshot.StorageTiers
            .Where(tier => tier.MemberPhysicalDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase))
            .Select(tier => tier.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool HasStructuralChanges(StorageSnapshot working, StorageSnapshot committed, string poolId)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        var workingPool = working.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase));
        var committedPool = committed.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase));
        if ((workingPool is null) != (committedPool is null))
        {
            return true;
        }

        if (workingPool is null)
        {
            return false;
        }

        var workingVdisks = working.VirtualDisks
            .Where(disk => string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .Select(disk => disk.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committedVdisks = committed.VirtualDisks
            .Where(disk => string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .Select(disk => disk.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!workingVdisks.SetEquals(committedVdisks))
        {
            return true;
        }

        foreach (var disk in working.PhysicalDisks.Where(disk =>
                     string.Equals(disk.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)))
        {
            var committedDisk = committed.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
            if (committedDisk is null
                || committedDisk.IsRetired != disk.IsRetired
                || committedDisk.IsHotSpare != disk.IsHotSpare)
            {
                return true;
            }

            var workingTiers = TierIdsContaining(working, disk.StableId)
                .Where(id => id.StartsWith(poolId, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var committedTiers = TierIdsContaining(committed, disk.StableId)
                .Where(id => id.StartsWith(poolId, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!workingTiers.SetEquals(committedTiers))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts a draft virtual disk card on the working copy of a committed
    /// pool. The pool already exists; the card disappears on undo, discard,
    /// or a delete step, and Apply materializes it through the simulation
    /// operation sequence.
    /// </summary>
    public static StorageSnapshot InsertDraftVirtualDisk(
        StorageSnapshot snapshot,
        string poolId,
        string name,
        string resiliency,
        long interleave)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected pool was not found.");
        if (pool.IsPrimordial)
        {
            throw new InvalidOperationException("A simulated virtual disk requires a non-primordial pool.");
        }

        if (IsDraftPool(pool.StableId))
        {
            // Draft-pool placeholder: the pool is not applied yet, so the
            // virtual disk card only previews the coming creation. Apply
            // materializes the pool together with its one virtual disk.
            if (snapshot.VirtualDisks.Any(item =>
                    string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The pool already has a virtual disk.");
            }

            var placeholderSize = EstimatePoolLogicalCapacity(snapshot, poolId, resiliency, interleave);
            var placeholder = new VirtualDiskInfo(
                $"{DraftVirtualDiskPrefix}{Guid.NewGuid():N}",
                false,
                string.IsNullOrWhiteSpace(name) ? pool.FriendlyName : name.Trim(),
                "Healthy",
                "OK",
                resiliency,
                "Fixed",
                1,
                interleave,
                placeholderSize,
                placeholderSize > 0
                    ? EstimatePoolPhysicalFootprint(snapshot, poolId, placeholderSize, resiliency)
                    : 0,
                pool.StableId,
                [],
                []);
            return snapshot with
            {
                VirtualDisks = snapshot.VirtualDisks.Append(placeholder).ToArray()
            };
        }

        if (snapshot.VirtualDisks.Any(item =>
                string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The pool already has a virtual disk.");
        }

        var diskName = string.IsNullOrWhiteSpace(name) ? pool.FriendlyName : name.Trim();
        var free = EstimatePoolLogicalCapacity(snapshot, poolId, resiliency, interleave);
        if (free <= 0)
        {
            throw new InvalidOperationException("The simulated pool has no createable logical capacity.");
        }
        var vdisk = new VirtualDiskInfo(
            $"{DraftVirtualDiskPrefix}{Guid.NewGuid():N}",
            false,
            diskName,
            "Healthy",
            "OK",
            resiliency,
            "Fixed",
            1,
            interleave,
            free,
            EstimatePoolPhysicalFootprint(snapshot, poolId, free, resiliency),
            pool.StableId,
            [],
            []);
        return snapshot with
        {
            VirtualDisks = snapshot.VirtualDisks.Append(vdisk).ToArray()
        };
    }

    public static long EstimatePoolLogicalCapacity(
        StorageSnapshot snapshot,
        string poolId,
        string resiliency,
        long interleave)
    {
        var tiers = snapshot.StorageTiers
            .Where(item => item.PoolStableId == poolId)
            .ToArray();
        if (tiers.Length > 0)
        {
            return tiers.Sum(item => item.Size);
        }

        var pool = snapshot.StoragePools.First(item => item.StableId == poolId);
        var members = snapshot.PhysicalDisks
            .Where(item => pool.MemberPhysicalDiskIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
                && PhysicalDiskUsage.ContributesDataCapacity(item.Usage))
            .ToArray();
        var copies = RecommendedDataCopies(resiliency, members.Length);
        var columns = string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase)
            ? members.Length
            : (int?)null;
        var parity = string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return ConservativeCapacity.PlanLogicalUpperBound(
            members.Select(item => item.Size).ToArray(),
            resiliency,
            copies,
            columns,
            parity,
            interleave,
            snapshot.VirtualDisks
                .Where(item => item.PoolStableId == poolId)
                .Sum(item => item.FootprintOnPool)).AlignedLogicalBytes;
    }

    private static long EstimatePoolPhysicalFootprint(
        StorageSnapshot snapshot,
        string poolId,
        long logicalBytes,
        string resiliency)
    {
        var tiers = snapshot.StorageTiers.Where(item => item.PoolStableId == poolId).ToArray();
        if (tiers.Length > 0)
        {
            return tiers.Sum(item => item.FootprintOnPool);
        }

        var memberCount = snapshot.StoragePools.First(item => item.StableId == poolId)
            .MemberPhysicalDiskIds.Count;
        var copies = RecommendedDataCopies(resiliency, memberCount);
        return ConservativeCapacity.PhysicalFootprintForLogical(
            logicalBytes,
            resiliency,
            copies,
            string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase) ? memberCount : null,
            string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }

    public static StorageSnapshot DeleteVirtualDiskFromWorking(StorageSnapshot snapshot, string vdiskId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.VirtualDisks.Any(item =>
                item.StableId.Equals(vdiskId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The selected virtual disk was not found.");
        }

        return snapshot with
        {
            VirtualDisks = snapshot.VirtualDisks
                .Where(item => !item.StableId.Equals(vdiskId, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
    }

    /// <summary>
    /// Dissolves a committed pool inside the working copy only: tiers,
    /// virtual disks, OS disks, and partitions go away and member disks
    /// return to the primordial pool. Apply later materializes the same
    /// removal through DissolveStoragePool.
    /// </summary>
    public static StorageSnapshot DissolvePoolInWorking(StorageSnapshot snapshot, string poolId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pool = snapshot.StoragePools.FirstOrDefault(item =>
            string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected pool was not found.");
        if (pool.IsPrimordial || IsDraftPool(pool.StableId))
        {
            throw new InvalidOperationException("Only a committed non-primordial pool can be dissolved.");
        }

        var primordial = snapshot.StoragePools.FirstOrDefault(item => item.IsPrimordial)
            ?? throw new InvalidOperationException("The simulated system has no primordial pool.");
        var vdiskIds = snapshot.VirtualDisks
            .Where(item => string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var osDiskIds = snapshot.OsDisks
            .Where(item => item.VirtualDiskStableId is not null
                && vdiskIds.Contains(item.VirtualDiskStableId))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = pool.MemberPhysicalDiskIds;
        return snapshot with
        {
            StoragePools = snapshot.StoragePools
                .Where(item => !string.Equals(item.StableId, poolId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.IsPrimordial
                    ? item with
                    {
                        MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Concat(members).ToArray()
                    }
                    : item)
                .ToArray(),
            StorageTiers = snapshot.StorageTiers
                .Where(item => !string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            VirtualDisks = snapshot.VirtualDisks
                .Where(item => !string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            OsDisks = snapshot.OsDisks
                .Where(item => item.VirtualDiskStableId is null
                    || !osDiskIds.Contains(item.VirtualDiskStableId))
                .ToArray(),
            Partitions = snapshot.Partitions
                .Where(item => item.OsDiskStableId is null
                    || !osDiskIds.Contains(item.OsDiskStableId))
                .ToArray(),
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => members.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
                    ? item with
                    {
                        PoolStableId = primordial.StableId,
                        CanPool = true,
                        Usage = PhysicalDiskUsage.AutoSelect
                    }
                    : item)
                .ToArray()
        };
    }

    /// <summary>
    /// True when the working copy put a still-resident pool member back
    /// onto a tier and the committed snapshot still has it unallocated.
    /// </summary>
    public static bool DiskNeedsSamePoolTierAssignment(
        StorageSnapshot working,
        StorageSnapshot committed,
        string diskId)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        var workingDisk = working.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase));
        var committedDisk = committed.PhysicalDisks.FirstOrDefault(item =>
            item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase));
        return workingDisk is not null
            && committedDisk is not null
            && !string.IsNullOrEmpty(workingDisk.PoolStableId)
            && !IsDraftPool(workingDisk.PoolStableId)
            && string.Equals(
                workingDisk.PoolStableId,
                committedDisk.PoolStableId,
                StringComparison.OrdinalIgnoreCase)
            && DiskIsAssignedToTier(working, diskId)
            && !DiskIsAssignedToTier(committed, diskId);
    }

    /// <summary>
    /// Reapplies working-copy pool membership, including same-pool
    /// unallocated-to-tier moves and a leftover draft, onto a newer
    /// committed snapshot such as one produced by a properties-only
    /// confirm.
    /// </summary>
    public static StorageSnapshot RestoreWorkingMembership(
        StorageSnapshot committed,
        StorageSnapshot working)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(working);
        var result = committed;
        var draftIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in working.StoragePools.Where(pool => IsDraftPool(pool.StableId)))
        {
            if (!result.StoragePools.Any(pool => IsDraftPool(pool.StableId)))
            {
                result = InsertDraftPool(result, draft.FriendlyName);
            }

            var created = result.StoragePools.Last(pool => IsDraftPool(pool.StableId));
            draftIdMap[draft.StableId] = created.StableId;
        }

        foreach (var disk in working.PhysicalDisks)
        {
            var current = result.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                continue;
            }

            var targetPoolId = disk.PoolStableId;
            if (targetPoolId is not null && draftIdMap.TryGetValue(targetPoolId, out var mapped))
            {
                targetPoolId = mapped;
            }

            if (string.IsNullOrEmpty(targetPoolId))
            {
                continue;
            }

            var samePool = string.Equals(
                current.PoolStableId,
                targetPoolId,
                StringComparison.OrdinalIgnoreCase);
            var wantAssigned = DiskIsAssignedToTier(working, disk.StableId);
            var haveAssigned = DiskIsAssignedToTier(result, disk.StableId);
            if (!samePool)
            {
                result = MoveDiskToPool(result, disk.StableId, targetPoolId);
            }
            else if (wantAssigned && !haveAssigned)
            {
                result = MoveDiskToPool(result, disk.StableId, targetPoolId);
            }
            else if (!wantAssigned && haveAssigned)
            {
                result = EvictDiskToUnallocated(result, disk.StableId);
            }
        }

        return result;
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
                    var failures = RecommendedToleratedFailures(resiliency, copies);
                    var columns = media == "HDD" ? RecommendedCapacityColumns(members) : (int?)null;
                    ConservativeCapacityEstimate? estimate = null;
                    try
                    {
                        estimate = ConservativeCapacity.PlanLogicalUpperBound(
                            members.Select(item => item.Size).ToArray(),
                            resiliency,
                            copies,
                            columns,
                            resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, failures) : 0,
                            tier.Interleave ?? 65536);
                    }
                    catch (ArgumentException)
                    {
                        // An incomplete draft remains visible with zero capacity;
                        // the shared rule evaluator supplies the blocking reason.
                    }
                    return tier with
                    {
                        ResiliencySettingName = resiliency,
                        NumberOfDataCopies = copies,
                        PhysicalDiskRedundancy = failures,
                        NumberOfColumns = columns,
                        Size = estimate?.AlignedLogicalBytes ?? 0,
                        FootprintOnPool = estimate?.PhysicalFootprintBytes ?? 0,
                        SizeSource = CapacitySourceKind.SimulatedEstimate
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
            node.ShowsEditStatus,
            node.HasStoredData,
            node.CannotLeave,
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
