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
        int layoutWeight = 1)
    {
        Unit = unit;
        Summary = summary;
        Children = children?.ToList() ?? [];
        IsReference = isReference;
        IsExpanded = isExpanded;
        IsSelectable = isSelectable;
        ChildrenLayout = childrenLayout;
        LayoutWeight = Math.Max(1, layoutWeight);
    }

    public StorageUnitRef Unit { get; }
    public string Summary { get; }
    public List<TopologyNode> Children { get; }
    public bool IsReference { get; }
    public bool IsSelectable { get; }
    public bool IsExpanded { get; set; }
    public TopologyChildrenLayout ChildrenLayout { get; }
    public int LayoutWeight { get; }
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
            StorageUnitKind.NetworkDiskGroup or StorageUnitKind.OtherDiskGroup =>
                new WorkspaceSelection(WorkspaceCategory.Pool, unit.StableId),
            StorageUnitKind.StorageTier =>
                new WorkspaceSelection(WorkspaceCategory.Tier, unit.StableId),
            StorageUnitKind.PhysicalDisk or StorageUnitKind.VirtualDisk
                or StorageUnitKind.OsDisk or StorageUnitKind.DirectDiskGroup
                or StorageUnitKind.VirtualDiskGroup =>
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
            root.Children.Add(CreatePoolNode(pool, snapshot));
        }

        if (snapshot.NetworkDisks.Count > 0)
        {
            var networkGroup = new TopologyNode(
                new StorageUnitRef(
                    NetworkGroupStableId(snapshot),
                    StorageUnitKind.NetworkDiskGroup,
                    "Network"),
                JoinSummary(
                    $"{snapshot.NetworkDisks.Count} network disks",
                    FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size))),
                childrenLayout: TopologyChildrenLayout.Flow,
                layoutWeight: snapshot.NetworkDisks.Count);
            networkGroup.Children.AddRange(snapshot.NetworkDisks.Select(CreateNetworkDiskNode));
            root.Children.Add(networkGroup);
        }

        var otherOsDisks = GetOtherOsDisks(snapshot);
        if (otherOsDisks.Count > 0)
        {
            var otherGroup = new TopologyNode(
                new StorageUnitRef(
                    OtherGroupStableId(snapshot),
                    StorageUnitKind.OtherDiskGroup,
                    "Other"),
                JoinSummary(
                    $"{otherOsDisks.Count} other disks",
                    $"{snapshot.Partitions.Count(x => x.OsDiskStableId is not null && otherOsDisks.Any(disk => disk.StableId == x.OsDiskStableId))} partitions",
                    FormatBytes(otherOsDisks.Sum(x => x.Size))),
                childrenLayout: TopologyChildrenLayout.Flow,
                layoutWeight: otherOsDisks.Count);
            foreach (var osDisk in otherOsDisks)
            {
                var node = new TopologyNode(
                    new StorageUnitRef(osDisk.StableId, StorageUnitKind.OsDisk, osDisk.FriendlyName),
                    JoinSummary(osDisk.PartitionStyle, FormatBytes(osDisk.Size)));
                AddPartitions(node, osDisk, snapshot);
                otherGroup.Children.Add(node);
            }
            root.Children.Add(otherGroup);
        }

        return root;
    }

    public static string NetworkGroupStableId(StorageSnapshot snapshot) =>
        $"group:network:{snapshot.Computer.StableId}";

    public static string OtherGroupStableId(StorageSnapshot snapshot) =>
        $"group:other:{snapshot.Computer.StableId}";

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

    public static string PartitionDisplayName(PartitionInfo partition)
    {
        var driveLetter = NormalizeDriveLetter(partition.DriveLetter);
        var label = partition.FileSystemLabel.Replace('\0', ' ').Trim();
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

    private static TopologyNode CreatePoolNode(StoragePoolInfo pool, StorageSnapshot snapshot)
    {
        var members = snapshot.PhysicalDisks
            .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var virtualDisks = snapshot.VirtualDisks.Where(x => x.PoolStableId == pool.StableId).ToList();
        var poolTiers = snapshot.StorageTiers.Where(x => x.PoolStableId == pool.StableId).ToList();
        var tierMemberIds = poolTiers.SelectMany(x => x.MemberPhysicalDiskIds)
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
                    string.Empty),
                string.Empty,
                isSelectable: false,
                childrenLayout: TopologyChildrenLayout.Flow,
                layoutWeight: virtualNodes.Count);
            virtualGroup.Children.AddRange(virtualNodes);
            poolNode.Children.Add(virtualGroup);
        }

        foreach (var tier in poolTiers.OrderBy(x => TierSortOrder(x.MediaType)))
        {
            var tierMembers = snapshot.PhysicalDisks
                .Where(x => tier.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var tierNode = new TopologyNode(
                new StorageUnitRef(tier.StableId, StorageUnitKind.StorageTier, tier.FriendlyName, tier.IsStable, pool.StableId),
                JoinSummary($"{tierMembers.Count} physical disks", FormatBytes(tierMembers.Sum(x => x.Size))),
                childrenLayout: TopologyChildrenLayout.Flow);
            foreach (var member in tierMembers)
            {
                tierNode.Children.Add(CreatePhysicalDiskNode(member, snapshot, true, includeOsChildren: false));
            }
            poolNode.Children.Add(tierNode);
        }

        var directMembers = members.Where(x => !tierMemberIds.Contains(x.StableId)).ToList();
        if (directMembers.Count > 0)
        {
            var directGroup = new TopologyNode(
                new StorageUnitRef(
                    $"group:direct:{pool.StableId}",
                    StorageUnitKind.DirectDiskGroup,
                    string.Empty),
                string.Empty,
                isSelectable: false,
                childrenLayout: TopologyChildrenLayout.Flow);
            foreach (var member in directMembers)
            {
                directGroup.Children.Add(CreatePhysicalDiskNode(member, snapshot, true, includeOsChildren: false));
            }
            poolNode.Children.Add(directGroup);
        }

        return poolNode;
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
            new StorageUnitRef(disk.StableId, StorageUnitKind.NetworkDisk, disk.Name, disk.IsStable),
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
                    string.IsNullOrWhiteSpace(partition.FileSystem) ? "Unknown" : partition.FileSystem,
                    FormatBytes(partition.Size))));
        }
    }

    private static int TierSortOrder(string mediaType) =>
        mediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("SCM", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string NormalizeMedia(string value) =>
        value.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
        : value.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
        : value.Equals("SCM", StringComparison.OrdinalIgnoreCase) ? "SCM"
        : "Unknown";
}
