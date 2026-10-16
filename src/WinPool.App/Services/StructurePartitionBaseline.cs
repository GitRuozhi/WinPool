using WinPool.Application;

namespace WinPool.App.Services;

/// <summary>Reads format facts from the exact primary partition and its uniquely linked volume.</summary>
public static class StructurePartitionBaseline
{
    public static PartitionInfo? ForPool(StorageSnapshot snapshot, string poolId)
    {
        var vds = snapshot.VirtualDisks.Where(vd => string.Equals(vd.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (vds.Length != 1) return null;
        var disks = snapshot.OsDisks.Where(disk => disk.VirtualDiskStableId == vds[0].StableId).ToArray();
        if (disks.Length != 1) return null;
        var partition = snapshot.Partitions.Where(part => part.OsDiskStableId == disks[0].StableId
            && part.Type is "Primary" or "BasicData").OrderBy(part => part.Offset).FirstOrDefault();
        return partition is null ? null : Resolve(snapshot, partition);
    }

    public static PartitionInfo Resolve(StorageSnapshot snapshot, PartitionInfo partition)
    {
        var volumes = snapshot.Volumes.Where(volume => volume.PartitionStableId == partition.StableId).ToArray();
        var volume = volumes.Length == 1 ? volumes[0] : null;
        var volumeFileSystem = volume?.FileSystem;
        var fileSystem = !string.IsNullOrWhiteSpace(volumeFileSystem) ? volumeFileSystem : partition.FileSystem;
        return partition with
        {
            FileSystem = CanonicalFileSystem(fileSystem),
            AllocationUnitSize = volume?.AllocationUnitSize ?? partition.AllocationUnitSize,
            FileSystemLabel = volume?.FileSystemLabel ?? partition.FileSystemLabel
        };
    }

    public static string CanonicalFileSystem(string? fileSystem) => fileSystem?.Trim().ToUpperInvariant() switch
    {
        "NTFS" => "NTFS", "REFS" => "ReFS", "EXFAT" => "exFAT", _ => fileSystem?.Trim() ?? ""
    };
}
