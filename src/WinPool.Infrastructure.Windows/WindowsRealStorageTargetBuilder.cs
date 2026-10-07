using System.Globalization;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public static class WindowsRealStorageTargetBuilder
{
    public static WindowsStorageCommandTarget Build(
        WindowsRealStorageTopology topology,
        RealTargetReference reference,
        IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(verifiedStepOutputs);
        var id = ResolveId(topology, reference, verifiedStepOutputs);
        if (id.Kind != reference.Kind)
        {
            throw new InvalidDataException("A created storage target has the wrong object kind.");
        }

        var item = topology.RequireObject(id);
        var closure = topology.RequireSinglePhysicalClosure([id]);
        var physical = topology.Snapshot.PhysicalDisks.SingleOrDefault(disk =>
            StringComparer.Ordinal.Equals(disk.StableId, closure.PhysicalDiskId))
            ?? throw new InvalidDataException("The physical member is absent from the same fresh capture.");
        var osDisk = ResolveOsDisk(topology.Snapshot, item.Id, reference.Kind);
        var partition = reference.Kind switch
        {
            StorageObjectKind.Partition => topology.Snapshot.Partitions.SingleOrDefault(value =>
                StringComparer.Ordinal.Equals(value.StableId, item.Id)),
            StorageObjectKind.Volume => topology.Snapshot.Volumes
                .Where(value => StringComparer.Ordinal.Equals(value.StableId, item.Id))
                .Select(value => topology.Snapshot.Partitions.SingleOrDefault(part =>
                    StringComparer.Ordinal.Equals(part.StableId, value.PartitionStableId)))
                .SingleOrDefault(),
            _ => null
        };
        if (reference.Kind == StorageObjectKind.Partition && partition is null)
            throw new InvalidDataException("The partition is absent from the same fresh capture.");

        var parentId = ParentId(topology.Snapshot, item.Id, reference.Kind);
        var parent = parentId is null ? null : topology.Facts.Objects.SingleOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Id, parentId));
        var subsystem = ResolveSubsystem(topology, item.Id, reference.Kind);

        return new WindowsStorageCommandTarget(
            reference.Kind,
            Text(item, "UniqueId"),
            Text(item, "ObjectId"),
            physical.SerialNumber.Trim(),
            osDisk is null ? string.Empty : Text(
                topology.Facts.Objects.Single(value => value.Id == osDisk.StableId), "UniqueId"),
            osDisk is null ? string.Empty : Text(
                topology.Facts.Objects.Single(value => value.Id == osDisk.StableId), "Path"),
            osDisk?.Number ?? partition?.DiskNumber,
            partition?.PartitionNumber,
            partition?.Guid ?? string.Empty,
            partition?.Offset,
            partition?.Size ?? osDisk?.Size,
            parent is null ? string.Empty : reference.Kind == StorageObjectKind.Volume
                ? Text(parent, "Guid")
                : Text(parent, "UniqueId"),
            Text(topology.Facts.Objects.Single(value =>
                StringComparer.Ordinal.Equals(value.Id, closure.PhysicalDiskId)), "UniqueId"),
            subsystem is null ? string.Empty : Text(subsystem, "UniqueId"),
            closure.Fingerprint,
            PartitionTypeGuid: partition?.PartitionTypeId ?? string.Empty,
            StorageSubsystemObjectId: subsystem is null ? string.Empty : Text(subsystem, "ObjectId"),
            PhysicalMemberObjectId: Text(topology.Facts.Objects.Single(value =>
                value.Id == closure.PhysicalDiskId), "ObjectId"));
    }

    public static RealTargetReference GetReference(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand value => value.Disk,
        InitializeGptCommand value => value.Disk,
        ClearDiskCommand value => value.Disk,
        CreatePartitionCommand value => value.Disk,
        DeletePartitionCommand value => value.Partition,
        ResizePartitionCommand value => value.Partition,
        FormatVolumeCommand value => value.Partition,
        SetDriveLetterCommand value => value.Partition,
        RenameVolumeCommand value => value.Volume,
        CreatePoolCommand value => value.PhysicalDisk,
        DeletePoolCommand value => value.Pool,
        RenamePoolCommand value => value.Pool,
        CreateVirtualDiskCommand value => value.Pool,
        CreateTieredVirtualDiskCommand value => value.Pool,
        DeleteVirtualDiskCommand value => value.VirtualDisk,
        ResizeVirtualDiskCommand value => value.VirtualDisk,
        RenameVirtualDiskCommand value => value.VirtualDisk,
        CreateTierCommand value => value.Pool,
        DeleteTierCommand value => value.Tier,
        ResizeTierCommand value => value.Tier,
        RenameTierCommand value => value.Tier,
        _ => throw new InvalidDataException("Unknown real command kind.")
    };

    internal static StorageObjectId ResolveId(
        WindowsRealStorageTopology topology,
        RealTargetReference reference,
        IReadOnlyDictionary<string, string> outputs) =>
        reference.Existing ?? ResolveCreated(topology, reference, outputs);

    private static StorageObjectId ResolveCreated(
        WindowsRealStorageTopology topology,
        RealTargetReference reference,
        IReadOnlyDictionary<string, string> outputs)
    {
        if (string.IsNullOrWhiteSpace(reference.CreatedByStep)
            || !outputs.TryGetValue(reference.CreatedByStep, out var json))
        {
            throw new InvalidDataException("A prior verified step output is required.");
        }
        using var document = JsonDocument.Parse(json);
        var outputName = reference.Kind == StorageObjectKind.OsDisk
            ? "CreatedOsDiskId" : "CreatedObjectId";
        if (!document.RootElement.TryGetProperty(outputName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException("The prior step did not record the created object identity.");
        }
        return new StorageObjectId(topology.SystemId, reference.Kind, property.GetString()!);
    }

    private static OsDiskInfo? ResolveOsDisk(
        StorageSnapshot snapshot,
        string id,
        StorageObjectKind kind) => kind switch
    {
        StorageObjectKind.OsDisk => snapshot.OsDisks.SingleOrDefault(value =>
            StringComparer.Ordinal.Equals(value.StableId, id)),
        StorageObjectKind.Partition => snapshot.Partitions
            .Where(value => StringComparer.Ordinal.Equals(value.StableId, id))
            .Select(value => snapshot.OsDisks.SingleOrDefault(disk =>
                StringComparer.Ordinal.Equals(disk.StableId, value.OsDiskStableId)))
            .SingleOrDefault(),
        StorageObjectKind.Volume => snapshot.Volumes
            .Where(value => StringComparer.Ordinal.Equals(value.StableId, id))
            .Select(value => snapshot.Partitions.SingleOrDefault(partition =>
                StringComparer.Ordinal.Equals(partition.StableId, value.PartitionStableId)))
            .Where(value => value is not null)
            .Select(value => snapshot.OsDisks.SingleOrDefault(disk =>
                StringComparer.Ordinal.Equals(disk.StableId, value!.OsDiskStableId)))
            .SingleOrDefault(),
        _ => null
    };

    private static string? ParentId(StorageSnapshot snapshot, string id, StorageObjectKind kind) => kind switch
    {
        StorageObjectKind.Partition => snapshot.Partitions.Single(value => value.StableId == id).OsDiskStableId,
        StorageObjectKind.Volume => snapshot.Volumes.Single(value => value.StableId == id).PartitionStableId,
        StorageObjectKind.VirtualDisk => snapshot.VirtualDisks.Single(value => value.StableId == id).PoolStableId,
        StorageObjectKind.StorageTier => snapshot.StorageTiers.Single(value => value.StableId == id).PoolStableId,
        StorageObjectKind.StoragePool => snapshot.StoragePools.Single(value => value.StableId == id).SubsystemStableId,
        _ => null
    };

    private static WinPoolSourceObject? ResolveSubsystem(
        WindowsRealStorageTopology topology,
        string id,
        StorageObjectKind kind)
    {
        string? subsystemId = kind switch
        {
            StorageObjectKind.StoragePool => topology.Snapshot.StoragePools
                .Single(value => value.StableId == id).SubsystemStableId,
            StorageObjectKind.VirtualDisk => topology.Snapshot.VirtualDisks
                .Where(value => value.StableId == id)
                .Select(value => topology.Snapshot.StoragePools.SingleOrDefault(pool =>
                    StringComparer.Ordinal.Equals(pool.StableId, value.PoolStableId))?.SubsystemStableId)
                .SingleOrDefault(),
            StorageObjectKind.StorageTier => topology.Snapshot.StorageTiers
                .Where(value => value.StableId == id)
                .Select(value => topology.Snapshot.StoragePools.SingleOrDefault(pool =>
                    StringComparer.Ordinal.Equals(pool.StableId, value.PoolStableId))?.SubsystemStableId)
                .SingleOrDefault(),
            StorageObjectKind.PhysicalDisk => topology.Facts.Objects
                .Where(value => value.ObjectType == FactObjectType.StorageSubsystem)
                .Select(value => value.Id).SingleOrDefault(),
            _ => null
        };
        return subsystemId is null ? null : topology.Facts.Objects.SingleOrDefault(value =>
            value.ObjectType == FactObjectType.StorageSubsystem
            && StringComparer.Ordinal.Equals(value.Id, subsystemId));
    }

    private static string Text(WinPoolSourceObject item, string name)
    {
        var field = item.Field(name);
        return field is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }
}
