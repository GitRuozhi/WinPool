using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>All partition/volume/logical-drive UI entry points use this one field projection.</summary>
internal static class ManagePartitionProjector
{
    internal static IReadOnlyList<ManagePropertyView> Properties(StorageSnapshot snapshot, StoragePartitionUnion item)
    {
        string Bytes(long? value) => value is { } bytes ? TopologyProjector.FormatBytes(bytes) : "—";
        var os = snapshot.OsDisks.FirstOrDefault(x => x.StableId == item.OsDiskId);
        var owner = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == os?.PhysicalDiskStableId)?.FriendlyName
            ?? snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == os?.VirtualDiskStableId)?.FriendlyName ?? os?.FriendlyName ?? "";
        var rows = new List<ManagePropertyView>
        {
            new("OwningDisk", owner), new("Type", item.Type, ManageValuePresentation.PartitionType),
            new("FileSystem", item.FileSystem), new("AllocationUnit", Bytes(item.AllocationUnitSize)),
            new("Capacity", Bytes(item.Size)), new("Available", Bytes(item.SizeRemaining)),
            new("SystemPartition", item.IsBoot == true || item.IsSystem == true ? "✓" : ""),
            new("PartitionStatus", item.OperationalStatus), new("Health", item.HealthStatus),
            new("StartOffset", Bytes(item.Offset)), new("DriveLetter", item.DriveLetter),
            new("VolumeLabel", item.Label), new("Path", string.Join("; ", item.AccessPaths))
        };
        if (item.PartitionSize.HasValue && item.FileSystemSize.HasValue && item.PartitionSize != item.FileSystemSize)
            rows.Insert(5, new("FileSystemCapacity", Bytes(item.FileSystemSize)));
        return WinPoolHardwarePresentation.MarkUnavailable(snapshot, item.Id, rows);
    }
}
