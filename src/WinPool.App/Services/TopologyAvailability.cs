using System.Text.Json;
using WinPool.Application;

namespace WinPool.App.Services;

public enum TopologyAvailabilityKind { Available, Offline, ReadOnly, Fault, Disconnected }

// Presentation only. This does not introduce an editing or authorization gate.
internal static class TopologyAvailability
{
    internal static TopologyAvailabilityKind Resolve(
        StorageUnitRef unit, StorageSnapshot snapshot, WinPoolFacts? facts)
    {
        if (unit.Kind is not (StorageUnitKind.PhysicalDisk or StorageUnitKind.VirtualDisk
            or StorageUnitKind.OsDisk or StorageUnitKind.Partition or StorageUnitKind.Volume
            or StorageUnitKind.StoragePool or StorageUnitKind.StorageTier))
            return TopologyAvailabilityKind.Available;

        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == unit.StableId);
        var volume = snapshot.Volumes.FirstOrDefault(x => x.StableId == unit.StableId);
        partition ??= snapshot.Partitions.FirstOrDefault(x => x.StableId == volume?.PartitionStableId);
        var disks = snapshot.OsDisks.Where(x => x.StableId == unit.StableId
            || x.PhysicalDiskStableId == unit.StableId || x.VirtualDiskStableId == unit.StableId
            || x.StableId == partition?.OsDiskStableId).ToArray();
        // A member's offline state does not make its whole pool offline.
        // Only the target disk and its own descendants inherit that state.
        if (disks.Any(x => x.IsOffline)
            || facts?.Objects.Any(x => x.Id == unit.StableId
                && x.Field("IsOffline") is { ReadState: FieldReadState.Returned,
                    Value: { ValueKind: JsonValueKind.True } }) == true)
            return TopologyAvailabilityKind.Offline;

        var ids = disks.Select(x => x.StableId).Append(unit.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (facts?.Objects.Any(x => ids.Contains(x.Id)
            && x.Field("IsReadOnly") is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.True } }) == true)
            return TopologyAvailabilityKind.ReadOnly;

        var physical = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == unit.StableId)
            ?? snapshot.PhysicalDisks.FirstOrDefault(x => disks.Any(d => d.PhysicalDiskStableId == x.StableId));
        var virtualDisk = snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == unit.StableId)
            ?? snapshot.VirtualDisks.FirstOrDefault(x => disks.Any(d => d.VirtualDiskStableId == x.StableId));
        var pool = snapshot.StoragePools.FirstOrDefault(x => x.StableId == unit.StableId);
        var statuses = new[]
        {
            (physical?.HealthStatus, physical?.OperationalStatus),
            (virtualDisk?.HealthStatus, virtualDisk?.OperationalStatus),
            (partition?.HealthStatus, partition?.OperationalStatus),
            (volume?.HealthStatus, volume?.OperationalStatus),
            (pool?.HealthStatus, pool?.OperationalStatus)
        };
        var operations = statuses.SelectMany(x => (x.Item2 ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (operations.Any(x => x is "LostCommunication" or "NoContact" or "Disconnected"))
            return TopologyAvailabilityKind.Disconnected;
        if (operations.Any(x => x is "Offline"))
            return TopologyAvailabilityKind.Offline;
        if (statuses.Any(x => x.Item1 is "Unhealthy")
            || operations.Any(x => x is "Error" or "Failed" or "Non-Recoverable Error"))
            return TopologyAvailabilityKind.Fault;
        return TopologyAvailabilityKind.Available;
    }
}
