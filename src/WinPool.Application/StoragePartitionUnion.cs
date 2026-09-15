namespace WinPool.Application;

/// <summary>Single business surface for partition, filesystem and logical-drive observations.
/// Source identities stay available for persistence adapters, never as additional UI objects.</summary>
public sealed record StoragePartitionUnion(
    string Id, bool IsStable, string DisplayName, string? OsDiskId,
    string Type, long? PartitionSize, long? FileSystemSize, long? Offset,
    string FileSystem, string Label, string DriveLetter, long? AllocationUnitSize,
    long? SizeRemaining, string HealthStatus, string OperationalStatus,
    IReadOnlyList<string> AccessPaths, bool? IsBoot, bool? IsSystem,
    bool HasGeometry, bool IsNetwork, IReadOnlyList<string> SourceIds)
{
    public long? Size => PartitionSize ?? FileSystemSize;

    public static IReadOnlyList<StoragePartitionUnion> Project(StorageSnapshot snapshot)
    {
        var result = new List<StoragePartitionUnion>();
        foreach (var p in snapshot.Partitions)
        {
            var v = snapshot.VolumeForPartition(p.StableId);
            result.Add(new(p.StableId, p.IsStable, TopologyProjector.PartitionDisplayName(snapshot, p), p.OsDiskStableId,
                p.Type, p.Size, v?.Size, p.Offset, snapshot.FileSystemOf(p), snapshot.FileSystemLabelOf(p), snapshot.DriveLetterOf(p),
                snapshot.AllocationUnitOf(p), v?.SizeRemaining, p.HealthStatus, p.OperationalStatus, snapshot.AccessPathsOf(p),
                p.IsBoot, p.IsSystem, true, false, v is null ? [p.StableId] : [p.StableId, v.StableId]));
        }
        foreach (var v in snapshot.Volumes.Where(v => v.PartitionStableId is null || snapshot.Partitions.All(p => p.StableId != v.PartitionStableId)))
            result.Add(new(v.StableId, v.IsStable, Name(v.DriveLetter, v.FileSystemLabel, "Unknown"), null,
                "Unknown", null, v.Size, null, v.FileSystem, v.FileSystemLabel, v.DriveLetter, v.AllocationUnitSize,
                v.SizeRemaining, v.HealthStatus, v.OperationalStatus, v.AccessPaths, null, null, false, false, [v.StableId]));
        foreach (var n in snapshot.NetworkDisks)
            result.Add(new(n.StableId, n.IsStable, Name(n.DriveLetter, n.Name.TrimEnd(':').Equals(n.DriveLetter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase) ? "" : n.Name, "Network"), null,
                "Network", null, n.Size, null, n.FileSystem, "", n.DriveLetter.TrimEnd(':'), null, n.SizeRemaining, "", "",
                string.IsNullOrEmpty(n.ProviderPath) ? [] : [n.ProviderPath], null, null, false, true, [n.StableId]));
        result.AddRange(snapshot.UnattachedPartitions);
        return result;
    }

    private static string Name(string letter, string label, string fallback) =>
        !string.IsNullOrWhiteSpace(letter) ? $"{letter.TrimEnd(':')}: {label}".Trim()
        : !string.IsNullOrWhiteSpace(label) ? label : fallback;
}
