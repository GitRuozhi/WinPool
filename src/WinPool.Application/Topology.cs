namespace WinPool.Application;

public enum TopologyChildrenLayout
{
    Stack,
    Flow,
    WeightedFlow
}

public sealed class TopologyNode
{
    public TopologyNode(
        StorageUnitRef unit,
        string summary = "",
        IEnumerable<TopologyNode>? children = null,
        bool isReference = false,
        bool isExpanded = true,
        bool isSelectable = true,
        TopologyChildrenLayout childrenLayout = TopologyChildrenLayout.Stack,
        int layoutWeight = 1,
        bool noWrapChildren = false,
        bool distributeByCapacity = false,
        IReadOnlyList<double>? capacityWeights = null,
        bool adaptiveHeaderEnabled = false)
    {
        Unit = unit;
        Summary = summary;
        Children = children?.ToList() ?? [];
        IsReference = isReference;
        IsExpanded = isExpanded;
        IsSelectable = isSelectable;
        ChildrenLayout = childrenLayout;
        LayoutWeight = Math.Max(1, layoutWeight);
        NoWrapChildren = noWrapChildren;
        DistributeByCapacity = distributeByCapacity;
        CapacityWeights = capacityWeights;
        AdaptiveHeaderEnabled = adaptiveHeaderEnabled;
    }

    public StorageUnitRef Unit { get; }
    public string Summary { get; }
    public List<TopologyNode> Children { get; }
    public bool IsReference { get; }
    public bool IsSelectable { get; }
    public bool IsExpanded { get; set; }
    public TopologyChildrenLayout ChildrenLayout { get; }
    public int LayoutWeight { get; }

    /// <summary>
    /// Layout-only strip metadata (Edit-upper partition strips): the
    /// children form one no-wrap horizontal row, and their spare width is
    /// distributed by the declared capacity weights through the engine's
    /// three-stage rule.
    /// </summary>
    public bool NoWrapChildren { get; }
    public bool DistributeByCapacity { get; }
    public IReadOnlyList<double>? CapacityWeights { get; }

    /// <summary>
    /// Edit-lower status indicators. False hides the whole cluster
    /// (tiers, groups, plus-pool, primordial pool card). Virtual disks
    /// may show HasStoredData. HasStoredData, CannotLeave, and pending
    /// are independent.
    /// </summary>
    public bool ShowsEditStatus { get; set; }

    public bool HasStoredData { get; set; }

    /// <summary>
    /// Disks only: boot/system disks, or original members of a data-bearing
    /// real pool. Never set on pool cards.
    /// </summary>
    public bool CannotLeave { get; set; }

    /// <summary>
    /// Width-adaptive header capability (Plan §6): the node may collapse its
    /// multi-line header to one line when its assigned width allows. Enabled
    /// per level; this stage enables it only for Edit-upper disks.
    /// </summary>
    public bool AdaptiveHeaderEnabled { get; }
}

public static class WorkspaceMapper
{
    public static WorkspaceSelection FromUnit(StorageUnitRef unit, StorageSnapshot snapshot) =>
        unit.Kind switch
        {
            StorageUnitKind.System or StorageUnitKind.StorageSubsystem =>
                new WorkspaceSelection(WorkspaceCategory.System, snapshot.Computer.StableId),
            StorageUnitKind.StoragePool =>
                new WorkspaceSelection(WorkspaceCategory.Pool, unit.StableId),
            StorageUnitKind.SyntheticStoragePool or StorageUnitKind.NetworkDiskGroup or StorageUnitKind.OtherDiskGroup =>
                new WorkspaceSelection(WorkspaceCategory.Pool, unit.StableId),
            StorageUnitKind.StorageTier or StorageUnitKind.SyntheticStorageTier or StorageUnitKind.DirectDiskGroup =>
                new WorkspaceSelection(WorkspaceCategory.Tier, unit.StableId),
            StorageUnitKind.PhysicalDisk or StorageUnitKind.VirtualDisk
                or StorageUnitKind.OsDisk or StorageUnitKind.VirtualDiskGroup =>
                new WorkspaceSelection(WorkspaceCategory.Disk, unit.StableId),
            StorageUnitKind.NetworkDisk or StorageUnitKind.Partition =>
                new WorkspaceSelection(WorkspaceCategory.Partition, unit.StableId),
            _ => new WorkspaceSelection(WorkspaceCategory.System, snapshot.Computer.StableId)
        };
}

public static class TopologyProjector
{
    private const string DoubleSpace = "  ";

    public static TopologyNode Project(StorageSnapshot snapshot)
    {
        var root = ProjectCore(snapshot);
        RemoveDuplicateOccurrences(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return root;
    }

    public static IReadOnlyList<StorageUnitRef> FindConflictingTopologyObjects(
        StorageSnapshot snapshot) =>
        Flatten(ProjectCore(snapshot))
            .GroupBy(ObjectKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.First().Unit)
            .ToArray();

    private static TopologyNode ProjectCore(StorageSnapshot snapshot)
    {
        var synthetic = SyntheticStorageProjection.For(snapshot);
        var uniquePhysical = snapshot.PhysicalDisks
            .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var root = new TopologyNode(
            new StorageUnitRef(snapshot.Computer.StableId, StorageUnitKind.System, snapshot.Computer.Name),
            JoinSummary(
                $"{snapshot.StoragePools.Count} pools",
                $"{uniquePhysical.Count} physical disks",
                snapshot.VirtualDisks.Count > 0
                    ? $"{snapshot.VirtualDisks.Count} virtual disks"
                    : null,
                snapshot.NetworkDisks.Count > 0
                    ? $"{snapshot.NetworkDisks.Count} network disks"
                    : null,
                FormatBytes(uniquePhysical.Sum(x => x.Size))),
            childrenLayout: TopologyChildrenLayout.WeightedFlow);

        foreach (var pool in snapshot.StoragePools
                     .OrderByDescending(x => x.IsPrimordial)
                     .ThenBy(x => x.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            root.Children.Add(CreatePoolNode(pool, snapshot, synthetic));
        }

        foreach (var pool in synthetic.Where(item => item.Kind == SyntheticStorageObjectKind.Pool)
                     .OrderBy(item => item.Name))
        {
            root.Children.Add(CreateSyntheticPoolNode(pool, snapshot));
        }

        return root;
    }

    private static void RemoveDuplicateOccurrences(
        TopologyNode node,
        HashSet<string> seen)
    {
        seen.Add(ObjectKey(node));
        for (var index = 0; index < node.Children.Count;)
        {
            var child = node.Children[index];
            if (!seen.Add(ObjectKey(child)))
            {
                node.Children.RemoveAt(index);
                continue;
            }

            RemoveDuplicateOccurrences(child, seen);
            index++;
        }
    }

    private static string ObjectKey(TopologyNode node) =>
        $"{(int)node.Unit.Kind}:{node.Unit.StableId}";

    public static string NetworkGroupStableId(StorageSnapshot snapshot) =>
        SyntheticStorageProjection.NetworkPoolStableId(snapshot);

    public static string OtherGroupStableId(StorageSnapshot snapshot) =>
        SyntheticStorageProjection.OtherPoolStableId(snapshot);

    public static IReadOnlyList<OsDiskInfo> GetOtherOsDisks(StorageSnapshot snapshot) =>
        snapshot.OsDisks
            .Where(x => string.IsNullOrWhiteSpace(x.PhysicalDiskStableId)
                        && string.IsNullOrWhiteSpace(x.VirtualDiskStableId))
            .ToList();

    public static int CalculatePoolWeight(StoragePoolInfo pool, StorageSnapshot snapshot)
    {
        var poolTiers = snapshot.StorageTiers.Where(x => x.PoolStableId == pool.StableId).ToList();
        var tierMembers = poolTiers
            .SelectMany(x => x.MemberPhysicalDiskIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directMembers = pool.MemberPhysicalDiskIds.Count(x => !tierMembers.Contains(x));
        var virtualCount = snapshot.VirtualDisks.Count(x => x.PoolStableId == pool.StableId);
        var maxTierMembers = poolTiers.Count == 0 ? 0 : poolTiers.Max(x => x.MemberPhysicalDiskIds.Count);
        return Math.Max(1, Math.Max(virtualCount, Math.Max(directMembers, maxTierMembers)));
    }

    public static IEnumerable<TopologyNode> Flatten(TopologyNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    public static IReadOnlyList<PartitionInfo> OrderPartitionsForWorkspace(StorageSnapshot snapshot)
    {
        var topologyOrder = Flatten(Project(snapshot))
            .Where(x => x.Unit.Kind == StorageUnitKind.Partition)
            .Select((node, index) => (node.Unit.StableId, Index: index))
            .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.StableId, x => x.Index, StringComparer.OrdinalIgnoreCase);

        return snapshot.Partitions
            .OrderBy(x => topologyOrder.GetValueOrDefault(x.StableId, int.MaxValue))
            .ThenBy(x => x.DiskNumber)
            .ThenBy(x => x.PartitionNumber)
            .ToList();
    }

    public static string PartitionDisplayName(PartitionInfo partition) =>
        PartitionDisplayName(snapshot: null, partition);

    public static string PartitionDisplayName(StorageSnapshot? snapshot, PartitionInfo partition)
    {
        var volume = snapshot?.VolumeForPartition(partition.StableId);
        var driveLetter = NormalizeDriveLetter(volume?.DriveLetter ?? partition.DriveLetter);
        var label = (volume?.FileSystemLabel ?? partition.FileSystemLabel).Replace('\0', ' ').Trim();
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            return string.Empty;
        }
        return string.IsNullOrWhiteSpace(label) ? $"{driveLetter}:" : $"{driveLetter}: {label}";
    }

    public static string NormalizeDriveLetter(string? value)
    {
        var candidate = (value ?? string.Empty)
            .Replace('\0', ' ')
            .Trim()
            .TrimEnd(':')
            .Trim();
        return candidate.Length == 1 && candidate[0] is >= 'A' and <= 'Z'
            ? candidate
            : candidate.Length == 1 && candidate[0] is >= 'a' and <= 'z'
                ? candidate.ToUpperInvariant()
                : string.Empty;
    }

    public static string JoinSummary(params string?[] fields) =>
        string.Join(DoubleSpace, fields.Where(x => !string.IsNullOrWhiteSpace(x)));

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }

    /// <summary>
    /// A tier has its own capacity field. Source failure leaves that value
    /// blank, while an explicitly collected zero remains a real zero.
    /// </summary>
    public static string TierCapacityText(StorageSnapshot snapshot, StorageTierInfo tier)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tier);
        return snapshot.FieldIssues.Any(issue =>
            issue.ObjectId.Equals(tier.StableId, StringComparison.OrdinalIgnoreCase)
            && issue.FieldName.Equals(nameof(StorageTierInfo.Size), StringComparison.OrdinalIgnoreCase)
            && issue.State != FieldReadState.Returned)
            ? string.Empty
            : FormatBytes(tier.Size);
    }

    private static TopologyNode CreatePoolNode(
        StoragePoolInfo pool,
        StorageSnapshot snapshot,
        IReadOnlyList<SyntheticStorageObject> synthetic)
    {
        var members = snapshot.PhysicalDisks
            .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var virtualDisks = snapshot.VirtualDisks.Where(x => x.PoolStableId == pool.StableId).ToList();
        var poolTiers = snapshot.StorageTiers.Where(x => x.PoolStableId == pool.StableId).ToList();
        var syntheticTiers = synthetic.Where(item => item.Kind == SyntheticStorageObjectKind.Tier
                         && string.Equals(item.ParentStableId, pool.StableId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Name)
                     .ToArray();
        // Usage marks for hot-spare and retired disks take visual precedence
        // over a concurrently reported tier-member relationship. The original
        // relationship remains in the snapshot and the empty/trimmed real tier
        // stays selectable for its source-backed details.
        var syntheticPriorityMemberIds = syntheticTiers
            .Where(item => item.Name is SyntheticStorageName.HotSpareLayer or SyntheticStorageName.RetiredLayer)
            .SelectMany(item => item.MemberStableIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var poolNode = new TopologyNode(
            new StorageUnitRef(pool.StableId, StorageUnitKind.StoragePool, pool.IsPrimordial ? "Primordial" : pool.FriendlyName, pool.IsStable),
            JoinSummary(
                $"{members.Count} physical disks",
                virtualDisks.Count > 0
                    ? $"{virtualDisks.Count} virtual disks"
                    : null,
                FormatBytes(members.Sum(x => x.Size))),
            childrenLayout: pool.IsPrimordial ? TopologyChildrenLayout.Flow : TopologyChildrenLayout.Stack,
            layoutWeight: CalculatePoolWeight(pool, snapshot));

        if (pool.IsPrimordial)
        {
            foreach (var member in members)
            {
                poolNode.Children.Add(CreatePhysicalDiskNode(member, snapshot, false, includeOsChildren: true));
            }
            return poolNode;
        }

        var virtualNodes = virtualDisks.Select(disk => CreateVirtualDiskNode(disk, snapshot)).ToList();
        if (virtualNodes.Count == 1)
        {
            poolNode.Children.Add(virtualNodes[0]);
        }
        else if (virtualNodes.Count > 1)
        {
            var virtualGroup = new TopologyNode(
                new StorageUnitRef(
                    $"group:vdisk:{pool.StableId}",
                    StorageUnitKind.VirtualDiskGroup,
                    "Virtual disks"),
                JoinSummary($"{virtualNodes.Count} virtual disks"),
                isSelectable: false,
                childrenLayout: TopologyChildrenLayout.Flow,
                layoutWeight: virtualNodes.Count);
            virtualGroup.Children.AddRange(virtualNodes);
            poolNode.Children.Add(virtualGroup);
        }

        foreach (var tier in syntheticTiers)
        {
            var tierMembers = snapshot.PhysicalDisks
                .Where(member => tier.MemberStableIds.Contains(member.StableId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var tierNode = new TopologyNode(
                new StorageUnitRef(
                    tier.StableId,
                    StorageUnitKind.SyntheticStorageTier,
                    tier.Name.ToString(),
                    true,
                    pool.StableId,
                    tier.Name),
                JoinSummary(
                    $"{tierMembers.Length} physical disks",
                    tier.UnknownMemberStableIds.Count > 0 ? "Membership unknown" : null),
                childrenLayout: TopologyChildrenLayout.Flow);
            foreach (var member in tierMembers)
            {
                tierNode.Children.Add(CreatePhysicalDiskNode(member, snapshot, true, includeOsChildren: false));
            }
            poolNode.Children.Add(tierNode);
        }

        foreach (var tier in poolTiers.OrderBy(x => TierSortOrder(x.MediaType)))
        {
            var tierMembers = snapshot.PhysicalDisks
                .Where(x => tier.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)
                    && !syntheticPriorityMemberIds.Contains(x.StableId))
                .ToList();
            var tierNode = new TopologyNode(
                new StorageUnitRef(tier.StableId, StorageUnitKind.StorageTier, tier.FriendlyName, tier.IsStable, pool.StableId),
                JoinSummary($"{tierMembers.Count} physical disks", TierCapacityText(snapshot, tier)),
                childrenLayout: TopologyChildrenLayout.Flow);
            foreach (var member in tierMembers)
            {
                tierNode.Children.Add(CreatePhysicalDiskNode(member, snapshot, true, includeOsChildren: false));
            }
            poolNode.Children.Add(tierNode);
        }

        return poolNode;
    }

    private static TopologyNode CreateSyntheticPoolNode(
        SyntheticStorageObject pool,
        StorageSnapshot snapshot)
    {
        var node = new TopologyNode(
            new StorageUnitRef(
                pool.StableId,
                StorageUnitKind.SyntheticStoragePool,
                pool.Name.ToString(),
                true,
                null,
                pool.Name),
            JoinSummary($"{pool.MemberStableIds.Count} members"),
            childrenLayout: TopologyChildrenLayout.Flow,
            layoutWeight: Math.Max(1, pool.MemberStableIds.Count));
        foreach (var memberId in pool.MemberStableIds)
        {
            if (snapshot.OsDisks.FirstOrDefault(disk => disk.StableId == memberId) is { } osDisk)
            {
                var diskNode = new TopologyNode(
                    new StorageUnitRef(osDisk.StableId, StorageUnitKind.OsDisk, osDisk.FriendlyName),
                    JoinSummary(osDisk.PartitionStyle, FormatBytes(osDisk.Size)));
                AddPartitions(diskNode, osDisk, snapshot);
                node.Children.Add(diskNode);
                continue;
            }

            if (snapshot.NetworkDisks.FirstOrDefault(disk => disk.StableId == memberId) is { } networkDisk)
            {
                node.Children.Add(CreateNetworkDiskNode(networkDisk));
                continue;
            }

            if (snapshot.PartitionUnions.FirstOrDefault(union => union.Id == memberId) is { } union)
            {
                node.Children.Add(new TopologyNode(
                    new StorageUnitRef(union.Id, StorageUnitKind.Partition, union.DisplayName, union.IsStable),
                    JoinSummary(union.FileSystem, union.Size is { } size ? FormatBytes(size) : "Unknown")));
            }
        }
        return node;
    }

    private static TopologyNode CreateVirtualDiskNode(VirtualDiskInfo disk, StorageSnapshot snapshot)
    {
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.VirtualDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            JoinSummary(disk.TierStableIds.Count > 0 ? "Tiered" : "Virtual", FormatBytes(disk.Size)));
        foreach (var osDisk in snapshot.OsDisks.Where(x => x.VirtualDiskStableId == disk.StableId))
        {
            AddPartitions(node, osDisk, snapshot);
        }
        return node;
    }

    private static TopologyNode CreatePhysicalDiskNode(
        PhysicalDiskInfo disk,
        StorageSnapshot snapshot,
        bool isReference,
        bool includeOsChildren)
    {
        var node = new TopologyNode(
            new StorageUnitRef(disk.StableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId),
            JoinSummary(NormalizeMedia(disk.MediaType), FormatBytes(disk.Size)),
            isReference: isReference);
        if (includeOsChildren)
        {
            foreach (var osDisk in snapshot.OsDisks.Where(x => x.PhysicalDiskStableId == disk.StableId))
            {
                AddPartitions(node, osDisk, snapshot);
            }
        }
        return node;
    }

    private static TopologyNode CreateNetworkDiskNode(NetworkDiskInfo disk) =>
        new(
            new StorageUnitRef(disk.StableId, StorageUnitKind.Partition, disk.Name, disk.IsStable),
            JoinSummary("Network", FormatBytes(disk.Size)));

    private static void AddPartitions(TopologyNode parent, OsDiskInfo osDisk, StorageSnapshot snapshot)
    {
        foreach (var partition in snapshot.Partitions
                     .Where(x => x.OsDiskStableId == osDisk.StableId)
                     .OrderBy(x => x.PartitionNumber))
        {
            parent.Children.Add(new TopologyNode(
                new StorageUnitRef(
                    partition.StableId,
                    StorageUnitKind.Partition,
                    PartitionDisplayName(partition),
                    partition.IsStable,
                    osDisk.StableId),
                JoinSummary(
                    string.IsNullOrWhiteSpace(snapshot.FileSystemOf(partition))
                        ? "Unknown"
                        : snapshot.FileSystemOf(partition),
                    FormatBytes(partition.Size))));
        }
    }

    internal static int TierSortOrder(string mediaType) =>
        NormalizeMedia(mediaType) switch
        {
            "SCM" => 0,
            "SSD" => 1,
            _ => 2
        };

    private static string NormalizeMedia(string value) =>
        value.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
        : value.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
        : value.Equals("SCM", StringComparison.OrdinalIgnoreCase) ? "SCM"
        : "Unknown";
}
