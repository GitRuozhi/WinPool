using WinPool.Application;

namespace WinPool.App.Services;

public static class StructureCreationPolicy
{
    public static PoolEditIntent NewPool(string name, bool createMsr) =>
        new(true, true, "NTFS", 65536, name, VirtualDiskName: name, CreateMsr: createMsr);

    /// <summary>Choose a default for a new VD before freezing its automatic layout.</summary>
    public static PoolEditIntent WithDefaultDriveLetter(StorageSnapshot snapshot, string poolId,
        PoolEditIntent intent, IReadOnlyDictionary<string, PoolEditIntent> activeIntents, bool simulation)
    {
        if (intent.DriveLetter is not null || !intent.AutoCreateVirtualDisk
            || !intent.AutoCreatePartition || intent.PendingAutomaticLayout)
            return intent;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in snapshot.Partitions.Select(partition => partition.DriveLetter)
            .Concat(snapshot.Partitions.SelectMany(snapshot.AccessPathsOf))
            .Concat(snapshot.Volumes.SelectMany(volume => volume.AccessPaths))
            .Concat(snapshot.NetworkDisks.Select(disk => disk.DriveLetter)))
            if (StorageAccessPath.TryGetDriveLetter(path, out var letter)) used.Add(letter);

        // Callers pass only new-VD or pending-layout goals, not observed VDs.
        foreach (var pair in activeIntents)
            if (!pair.Key.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                && snapshot.StoragePools.Any(pool => pool.StableId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                && pair.Value.AutoCreateVirtualDisk && pair.Value.AutoCreatePartition
                && pair.Value.DriveLetter is { } reserved)
                used.Add(char.ToUpperInvariant(reserved).ToString());

        foreach (var candidate in simulation ? "CDEFGHIJKLMNOPQRSTUVWXYZ" : "DEFGHIJKLMNOPQRSTUVWXYZ")
            if (!used.Contains(candidate.ToString())) return intent with { DriveLetter = candidate };
        return intent;
    }

    public static bool WantsVirtualDisk(StorageSnapshot working, string? poolId, PoolEditIntent? previous) =>
        working.VirtualDisks.Any(vd => vd.PoolStableId == poolId)
        || EditWorkspace.IsDraftPool(poolId ?? "") && (previous?.AutoCreateVirtualDisk ?? true);

    public static PoolEditIntent ChooseVirtualDisk(PoolEditIntent intent, bool exists) => intent with
    {
        AutoCreateVirtualDisk = exists,
        AutoCreatePartition = true,
        PendingAutomaticLayout = exists && intent.PendingAutomaticLayout,
        VerifiedVirtualDiskId = exists ? intent.VerifiedVirtualDiskId : null
    };
}
