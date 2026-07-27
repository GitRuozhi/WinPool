namespace WinPool.Core;

public enum StorageFindingKind
{
    MultiplePerformanceTiers,
    MultipleCapacityTiers,
    MultipleVirtualDisks,
    LegacyDynamicVolume,
    MbrDisk
}

public sealed record StorageFinding(
    StorageFindingKind Kind,
    string TargetName,
    string TargetStableId);

public static class StorageFindingInspector
{
    private static readonly HashSet<string> LegacyVolumeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Simple",
        "Spanned",
        "Striped",
        "Mirrored",
        "Raid5"
    };

    public static IReadOnlyList<StorageFinding> Evaluate(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var findings = new List<StorageFinding>();

        foreach (var pool in snapshot.StoragePools.Where(x => !x.IsPrimordial))
        {
            var tiers = snapshot.StorageTiers
                .Where(x => x.PoolStableId == pool.StableId && x.Size > 0)
                .ToList();
            if (tiers.Count(x => x.MediaType is "SSD" or "SCM") > 1)
            {
                findings.Add(new StorageFinding(
                    StorageFindingKind.MultiplePerformanceTiers, pool.FriendlyName, pool.StableId));
            }
            if (tiers.Count(x => x.MediaType == "HDD") > 1)
            {
                findings.Add(new StorageFinding(
                    StorageFindingKind.MultipleCapacityTiers, pool.FriendlyName, pool.StableId));
            }
            if (snapshot.VirtualDisks.Count(x => x.PoolStableId == pool.StableId) > 1)
            {
                findings.Add(new StorageFinding(
                    StorageFindingKind.MultipleVirtualDisks, pool.FriendlyName, pool.StableId));
            }
        }

        foreach (var partition in snapshot.Partitions.Where(x => LegacyVolumeTypes.Contains(x.Type)))
        {
            findings.Add(new StorageFinding(
                StorageFindingKind.LegacyDynamicVolume,
                TopologyProjector.PartitionDisplayName(partition),
                partition.StableId));
        }

        foreach (var disk in snapshot.OsDisks.Where(
                     x => x.PartitionStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new StorageFinding(
                StorageFindingKind.MbrDisk, disk.FriendlyName, disk.StableId));
        }

        return findings;
    }

    public static bool IsUnhealthy(string? healthStatus, string? operationalStatus) =>
        !(string.IsNullOrWhiteSpace(healthStatus)
          || healthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
        || !(string.IsNullOrWhiteSpace(operationalStatus)
             || operationalStatus.Equals("OK", StringComparison.OrdinalIgnoreCase));
}
