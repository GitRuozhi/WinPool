using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

/// <summary>Publishes an editable snapshot as Windows-shaped source facts without claiming that Windows was queried.</summary>
public static class WinPoolSimulationFacts
{
    private const string StorageNamespace = "root/microsoft/windows/storage";
    private const string CimV2Namespace = "root/cimv2";
    private const string NativeNamespace = "winpool/native";

    private static readonly HashSet<string> RebuiltClasses = new(StringComparer.Ordinal)
    {
        "Win32_ComputerSystem", "Win32_OperatingSystem", "Registry.CurrentVersion",
        "MSFT_StorageSubSystem", "MSFT_StoragePool", "MSFT_StorageTier", "MSFT_PhysicalDisk",
        "MSFT_VirtualDisk", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume",
        "Win32_LogicalDisk", "Win32_DiskDrive", "Windows.DiskRoles"
    };

    public static WinPoolFacts Create(StorageSnapshot snapshot, SystemId systemId) =>
        Build(snapshot, systemId, 1);

    public static WinPoolFacts ApplyCandidate(WinPoolFacts? previous, StorageSnapshot before, StorageSnapshot candidate, SystemId systemId)
    {
        _ = before;
        var generated = Build(candidate, systemId, checked((previous?.Revision ?? 0) + 1));
        if (previous is null) return generated;

        var previousSources = previous.Sources.ToDictionary(x => x.Id);
        var preservedObjects = previous.Objects.Where(item =>
            !RebuiltClasses.Contains(previousSources[item.SourceRef].ClassName)
            && generated.Objects.All(candidateItem => candidateItem.Id != item.Id)).ToArray();
        if (preservedObjects.Length == 0) return generated with
        {
            Collections = PreserveHardwareCollection(previous.Collections, generated.Collections)
        };

        var objects = generated.Objects.AddRange(preservedObjects);
        var objectIds = objects.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var usedPreviousSourceIds = preservedObjects.Select(x => x.SourceRef)
            .Concat(preservedObjects.SelectMany(x => x.Fields.Select(field => field.SourceRef)))
            .ToHashSet(StringComparer.Ordinal);
        var sources = generated.Sources.AddRange(previous.Sources.Where(x =>
            usedPreviousSourceIds.Contains(x.Id) && generated.Sources.All(candidateSource => candidateSource.Id != x.Id)));
        var relationships = generated.Relationships.AddRange(previous.Relationships.Where(x =>
                objectIds.Contains(x.FromId) && objectIds.Contains(x.ToId))
            .Where(x => generated.Relationships.All(candidateRelationship =>
                candidateRelationship.FromId != x.FromId || candidateRelationship.ToId != x.ToId || candidateRelationship.Kind != x.Kind)));
        var preservedIds = preservedObjects.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var identities = generated.Identities.AddRange(previous.Identities.Where(x => preservedIds.Contains(x.ObjectId))
            .Where(x => generated.Identities.All(candidateIdentity =>
                candidateIdentity.ObjectType != x.ObjectType || candidateIdentity.SourceIdentity != x.SourceIdentity)));
        var result = generated with
        {
            Sources = sources,
            Objects = objects,
            Relationships = relationships,
            Identities = identities,
            Collections = PreserveHardwareCollection(previous.Collections, generated.Collections)
        };
        result.Validate();
        return result;
    }

    private static ImmutableArray<WinPoolCollectionState> PreserveHardwareCollection(
        ImmutableArray<WinPoolCollectionState> previous,
        ImmutableArray<WinPoolCollectionState> generated) =>
        generated.AddRange(previous.Where(x => x.Purpose != CollectionPurpose.Storage));

    private static WinPoolFacts Build(StorageSnapshot snapshot, SystemId systemId, long revision)
    {
        snapshot = EditWorkspace.EnsureFreeDisksHaveOsDisks(snapshot);
        var builder = new Builder(snapshot, systemId);
        builder.AddWindowsContext();
        builder.AddStorage();
        return builder.Complete(revision);
    }

    private sealed class Builder
    {
        private readonly StorageSnapshot snapshot;
        private readonly SystemId systemId;
        private readonly Dictionary<string, WinPoolSource> sources = new(StringComparer.Ordinal);
        private readonly List<WinPoolSourceObject> objects = [];
        private readonly List<WinPoolFactRelationship> relationships = [];
        private readonly List<WinPoolIdentityBinding> identities = [];

        public Builder(StorageSnapshot snapshot, SystemId systemId)
        {
            this.snapshot = snapshot;
            this.systemId = systemId;
        }

        public void AddWindowsContext()
        {
            var computer = snapshot.Computer;
            AddObject(computer.StableId, FactObjectType.Computer, CimV2Namespace, "Win32_ComputerSystem",
                S("Name", computer.Name), S("Caption", computer.Name));
            AddObject(computer.StableId + ":operating-system", FactObjectType.OperatingSystem, CimV2Namespace, "Win32_OperatingSystem",
                S("Caption", computer.WindowsProductName), S("Version", computer.WindowsVersion), S("BuildNumber", computer.OsBuild),
                S("LastBootUpTime", computer.LastBootTime.ToString("O")));
            AddObject(computer.StableId + ":registry-current-version", FactObjectType.HardwareSupplement, NativeNamespace, "Registry.CurrentVersion",
                S("DisplayVersion", computer.DisplayVersion), I("UBR", ParseInt64(computer.Ubr)));
        }

        public void AddStorage()
        {
            foreach (var subsystem in snapshot.StorageSubsystems)
                AddObject(subsystem.StableId, FactObjectType.StorageSubsystem, StorageNamespace, "MSFT_StorageSubSystem",
                    S("ObjectId", subsystem.StableId), S("UniqueId", subsystem.StableId), S("FriendlyName", subsystem.FriendlyName),
                    U("HealthStatus", Health(subsystem.HealthStatus)), UA("OperationalStatus", Operational(subsystem.OperationalStatus)));

            foreach (var pool in snapshot.StoragePools)
            {
                var members = snapshot.PhysicalDisks.Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.Ordinal)).ToArray();
                AddObject(pool.StableId, FactObjectType.StoragePool, StorageNamespace, "MSFT_StoragePool",
                    S("ObjectId", pool.StableId), S("UniqueId", pool.StableId), S("FriendlyName", pool.FriendlyName),
                    B("IsPrimordial", pool.IsPrimordial), U("HealthStatus", Health(pool.HealthStatus)),
                    UA("OperationalStatus", Operational(pool.OperationalStatus)), U("Size", pool.Size, "bytes"),
                    U("AllocatedSize", pool.AllocatedSize, "bytes"),
                    UN("LogicalSectorSize", pool.LogicalSectorSize ?? members.Select(x => (long?)x.LogicalSectorSize).FirstOrDefault(), "bytes"),
                    UN("PhysicalSectorSize", pool.PhysicalSectorSize ?? members.Select(x => (long?)x.PhysicalSectorSize).FirstOrDefault(), "bytes"),
                    U("ProvisioningTypeDefault", Provisioning(pool.ProvisioningTypeDefault)));
            }

            foreach (var disk in snapshot.PhysicalDisks)
            {
                AddObject(disk.StableId, FactObjectType.PhysicalDisk, StorageNamespace, "MSFT_PhysicalDisk",
                    S("ObjectId", disk.StableId), S("UniqueId", disk.StableId), S("Description", ""),
                    S("FriendlyName", disk.FriendlyName), U("HealthStatus", Health(disk.HealthStatus)), S("Manufacturer", ""),
                    S("Model", disk.Model), SA("OperationalDetails", []), UA("OperationalStatus", Operational(disk.OperationalStatus)),
                    S("PhysicalLocation", ""), S("SerialNumber", disk.SerialNumber), S("AdapterSerialNumber", ""),
                    U("BusType", Bus(disk.BusType)), UA("CannotPoolReason", CannotPoolReasons(disk)), B("CanPool", disk.CanPool),
                    S("DeviceId", disk.DeviceIdentifier.Length > 0 ? disk.DeviceIdentifier : disk.DeviceId?.ToString() ?? ""),
                    S("FirmwareVersion", disk.FirmwareVersion), B("IsIndicationEnabled", false), B("IsPartial", false),
                    U("LogicalSectorSize", disk.LogicalSectorSize, "bytes"), U("MediaType", Media(disk.MediaType)),
                    S("OtherCannotPoolReasonDescription", ""), U("PhysicalSectorSize", disk.PhysicalSectorSize, "bytes"),
                    U("Size", disk.Size, "bytes"), U("Usage", Usage(disk.Usage)));

                var number = disk.DeviceId ?? snapshot.OsDisks.FirstOrDefault(x => x.PhysicalDiskStableId == disk.StableId)?.Number ?? 0;
                var driveId = disk.StableId + ":win32-disk-drive";
                var pnp = disk.PnpDeviceId;
                AddObject(driveId, FactObjectType.HardwareSupplement, CimV2Namespace, "Win32_DiskDrive",
                    S("Caption", disk.FriendlyName), S("Name", $@"\\.\PHYSICALDRIVE{number}"),
                    S("DeviceID", $@"\\.\PHYSICALDRIVE{number}"), S("PNPDeviceID", pnp),
                    U("BytesPerSector", disk.LogicalSectorSize), S("FirmwareRevision", disk.FirmwareVersion),
                    U("Index", number), S("InterfaceType", disk.InterfaceType), S("MediaType", "Fixed hard disk media"),
                    S("Model", disk.Model), S("SerialNumber", disk.SerialNumber), U("Size", disk.Size, "bytes"));
                Link(disk.StableId, driveId, "disk-supplement");

                var rolesId = disk.StableId + ":disk-roles";
                AddObject(rolesId, FactObjectType.HardwareSupplement, NativeNamespace, "Windows.DiskRoles",
                    I("DiskNumber", number), B("IsBoot", disk.IsBoot), B("IsSystem", disk.IsSystem),
                    B("IsPageFile", disk.IsPageFile), B("IsCrashDump", disk.IsCrashDump));
                Link(disk.StableId, rolesId, "disk-supplement");
            }

            foreach (var tier in snapshot.StorageTiers)
                AddObject(tier.StableId, FactObjectType.StorageTier, StorageNamespace, "MSFT_StorageTier",
                    S("ObjectId", tier.StableId), S("UniqueId", tier.StableId), S("FriendlyName", tier.FriendlyName),
                    U("MediaType", Media(tier.MediaType)), S("ResiliencySettingName", tier.ResiliencySettingName),
                    U("Size", tier.Size, "bytes"), U("FootprintOnPool", tier.FootprintOnPool, "bytes"),
                    UN("NumberOfColumns", tier.NumberOfColumns), UN("Interleave", tier.Interleave, "bytes"),
                    UN("NumberOfDataCopies", tier.NumberOfDataCopies), UN("PhysicalDiskRedundancy", tier.PhysicalDiskRedundancy));

            foreach (var disk in snapshot.VirtualDisks)
                AddObject(disk.StableId, FactObjectType.VirtualDisk, StorageNamespace, "MSFT_VirtualDisk",
                    S("ObjectId", disk.StableId), S("UniqueId", disk.StableId), S("FriendlyName", disk.FriendlyName),
                    U("HealthStatus", Health(disk.HealthStatus)), UA("OperationalStatus", Operational(disk.OperationalStatus)),
                    S("ResiliencySettingName", disk.ResiliencySettingName), U("ProvisioningType", Provisioning(disk.ProvisioningType)),
                    UN("NumberOfColumns", disk.NumberOfColumns), UN("Interleave", disk.Interleave, "bytes"),
                    U("Size", disk.Size, "bytes"), U("FootprintOnPool", disk.FootprintOnPool, "bytes"),
                    UN("NumberOfDataCopies", disk.NumberOfDataCopies), UN("PhysicalDiskRedundancy", disk.PhysicalDiskRedundancy),
                    UN("AllocatedSize", disk.AllocatedSize, "bytes"));

            foreach (var disk in snapshot.OsDisks)
            {
                var physical = snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == disk.PhysicalDiskStableId);
                var virtualDisk = snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == disk.VirtualDiskStableId);
                var logicalSector = physical?.LogicalSectorSize ?? 512;
                var physicalSector = physical?.PhysicalSectorSize ?? 4096;
                AddObject(disk.StableId, FactObjectType.Disk, StorageNamespace, "MSFT_Disk",
                    S("ObjectId", disk.StableId), S("UniqueId", disk.StableId), U("AllocatedSize", disk.Size, "bytes"),
                    B("BootFromDisk", disk.IsBoot), U("BusType", physical is null ? 16 : Bus(physical.BusType)),
                    S("FirmwareVersion", physical?.FirmwareVersion ?? ""), S("FriendlyName", disk.FriendlyName),
                    U("HealthStatus", 0), B("IsBoot", disk.IsBoot), B("IsOffline", disk.IsOffline), B("IsReadOnly", false),
                    B("IsSystem", disk.IsSystem), U("LogicalSectorSize", logicalSector, "bytes"), S("Model", physical?.Model ?? virtualDisk?.FriendlyName ?? disk.FriendlyName),
                    U("Number", disk.Number), U("NumberOfPartitions", snapshot.Partitions.Count(x => x.OsDiskStableId == disk.StableId)),
                    U("OfflineReason", 0), UA("OperationalStatus", [2]), U("PartitionStyle", PartitionStyle(disk.PartitionStyle)),
                    U("PhysicalSectorSize", physicalSector, "bytes"), U("ProvisioningType", Provisioning(physical?.ProvisioningType ?? virtualDisk?.ProvisioningType ?? "Fixed")),
                    S("SerialNumber", physical?.SerialNumber ?? ""), U("Size", disk.Size, "bytes"));

            }

            foreach (var partition in snapshot.Partitions)
            {
                var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
                var volume = snapshot.Volumes.FirstOrDefault(x => x.PartitionStableId == partition.StableId);
                var accessPaths = volume?.AccessPaths.ToArray()
                    ?? (string.IsNullOrWhiteSpace(partition.Path) ? Array.Empty<string>() : [partition.Path]);
                AddObject(partition.StableId, FactObjectType.Partition, StorageNamespace, "MSFT_Partition",
                    S("ObjectId", partition.StableId), S("UniqueId", partition.StableId), SA("AccessPaths", accessPaths),
                    S("DiskId", partition.OsDiskStableId ?? ""), U("DiskNumber", partition.DiskNumber),
                    S("DriveLetter", partition.DriveLetter), S("GptType", GptType(partition)), S("Guid", partition.Guid),
                    B("IsActive", false), B("IsBoot", partition.IsBoot), B("IsDAX", false), B("IsHidden", partition.IsHidden),
                    B("IsOffline", disk?.IsOffline == true), B("IsReadOnly", false), B("IsShadowCopy", false), B("IsSystem", partition.IsSystem),
                    U("MbrType", MbrType(partition)), B("NoDefaultDriveLetter", partition.IsHidden || partition.DriveLetter.Length == 0),
                    U("Offset", partition.Offset, "bytes"), U("OperationalStatus", Operational(partition.OperationalStatus).FirstOrDefault()),
                    U("PartitionNumber", partition.PartitionNumber), U("Size", partition.Size, "bytes"));
            }

            foreach (var volume in snapshot.Volumes)
            {
                AddObject(volume.StableId, FactObjectType.Volume, StorageNamespace, "MSFT_Volume",
                    S("ObjectId", volume.StableId), S("UniqueId", volume.VolumeIdentity.Length > 0 ? volume.VolumeIdentity : volume.StableId),
                    UN("AllocationUnitSize", volume.AllocationUnitSize, "bytes"), S("DriveLetter", volume.DriveLetter),
                    U("DriveType", 3), S("FileSystem", volume.FileSystem), S("FileSystemLabel", volume.FileSystemLabel),
                    U("HealthStatus", Health(volume.HealthStatus)), UA("OperationalStatus", Operational(volume.OperationalStatus)),
                    S("Path", volume.AccessPaths.FirstOrDefault() ?? ""), U("Size", volume.Size, "bytes"),
                    U("SizeRemaining", volume.SizeRemaining, "bytes"));
                if (volume.PartitionStableId is not null) Link(volume.PartitionStableId, volume.StableId, "partition-volume");

                if (volume.DriveLetter.Length > 0)
                {
                    var logicalId = volume.StableId + ":logical-disk";
                    AddLogicalDisk(logicalId, FactObjectType.LogicalDisk, volume.DriveLetter, "", volume.FileSystem,
                        volume.FileSystemLabel, volume.Size, volume.SizeRemaining, 3);
                    Link(volume.StableId, logicalId, "same-volume");
                }
            }

            foreach (var disk in snapshot.NetworkDisks)
                AddLogicalDisk(disk.StableId, FactObjectType.NetworkDisk, disk.DriveLetter, disk.ProviderPath,
                    disk.FileSystem, disk.Name, disk.Size, disk.SizeRemaining, 4);

            foreach (var pool in snapshot.StoragePools)
            {
                Link(pool.SubsystemStableId, pool.StableId, "subsystem-pool");
                foreach (var disk in pool.MemberPhysicalDiskIds) Link(pool.StableId, disk, "pool-member");
            }
            foreach (var disk in snapshot.OsDisks)
            {
                Link(disk.PhysicalDiskStableId, disk.StableId, "same-device");
                Link(disk.VirtualDiskStableId, disk.StableId, "same-device");
            }
            foreach (var tier in snapshot.StorageTiers)
            {
                Link(tier.PoolStableId, tier.StableId, "pool-tier");
                Link(tier.VirtualDiskStableId, tier.StableId, "virtual-disk-tier");
                foreach (var disk in tier.MemberPhysicalDiskIds) Link(tier.StableId, disk, "tier-member");
            }
            foreach (var disk in snapshot.VirtualDisks) Link(disk.PoolStableId, disk.StableId, "pool-virtual-disk");
            foreach (var partition in snapshot.Partitions) Link(partition.OsDiskStableId, partition.StableId, "disk-partition");
        }

        private void AddLogicalDisk(string id, FactObjectType type, string letter, string provider, string fileSystem,
            string label, long size, long freeSpace, ulong driveType)
        {
            var device = letter.Length == 1 ? letter + ":" : letter;
            AddObject(id, type, CimV2Namespace, "Win32_LogicalDisk",
                S("Caption", device), S("Name", device), S("DeviceID", device), U("DriveType", driveType),
                S("FileSystem", fileSystem), U("FreeSpace", freeSpace), S("ProviderName", provider),
                U("Size", size, "bytes"), S("VolumeName", label));
        }

        private void AddObject(string id, FactObjectType type, string sourceNamespace, string className, params Field[] fields)
        {
            var sourceRef = Source(sourceNamespace, className);
            var sourceIdentity = WinPoolIdentityRegistry.OpaqueSourceIdentity(sourceNamespace, className, id);
            var values = fields.Select(field => new WinPoolSourceField(field.Name, field.Type,
                JsonSerializer.SerializeToElement(field.Value), FieldReadState.Returned, sourceRef, field.Unit)).ToImmutableArray();
            objects.Add(new(id, type, sourceRef, sourceIdentity, true, values));
            identities.Add(new(type, sourceIdentity, id));
        }

        private string Source(string sourceNamespace, string className)
        {
            var id = $"simulation:{snapshot.SnapshotVersion}:{sourceNamespace}:{className}";
            sources.TryAdd(id, new(id, FactOrigin.Simulation, sourceNamespace, className, snapshot.ScannedAt, CollectionPurpose.Storage));
            return id;
        }

        private void Link(string? from, string? to, string kind)
        {
            if (from is null || to is null || relationships.Any(x => x.FromId == from && x.ToId == to && x.Kind == kind)) return;
            relationships.Add(new(from, to, kind, snapshot.ScannedAt));
        }

        public WinPoolFacts Complete(long revision)
        {
            var result = new WinPoolFacts(WinPoolFacts.CurrentFormatVersion, systemId, revision,
                sources.Values.ToImmutableArray(), objects.ToImmutableArray(), relationships.ToImmutableArray(), identities.ToImmutableArray(),
                [new(CollectionPurpose.Storage, snapshot.ScannedAt, snapshot.ScannedAt, FieldReadState.Returned)])
            {
                IsSimulation = true,
                InventoryVersion = snapshot.SnapshotVersion,
                InventoryCapturedAt = snapshot.ScannedAt
            };
            result.Validate();
            return result;
        }
    }

    private readonly record struct Field(string Name, FactValueType Type, object? Value, string? Unit = null);

    private static Field S(string name, string? value) => new(name, FactValueType.String, value ?? "");
    private static Field B(string name, bool value) => new(name, FactValueType.Boolean, value);
    private static Field I(string name, long value) => new(name, FactValueType.Int64, value);
    private static Field U(string name, long value, string? unit = null) => U(name, checked((ulong)value), unit);
    private static Field U(string name, int value, string? unit = null) => U(name, checked((ulong)value), unit);
    private static Field U(string name, ulong value, string? unit = null) => new(name, FactValueType.UInt64, value, unit);
    private static Field UN(string name, long? value, string? unit = null) => new(name, FactValueType.UInt64,
        value is null ? null : checked((ulong)value.Value), unit);
    private static Field UN(string name, int? value, string? unit = null) => new(name, FactValueType.UInt64,
        value is null ? null : checked((ulong)value.Value), unit);
    private static Field UA(string name, IEnumerable<ulong> values) => new(name, FactValueType.UInt64Array, values.ToArray());
    private static Field SA(string name, IEnumerable<string> values) => new(name, FactValueType.StringArray, values.ToArray());

    private static long ParseInt64(string value) => long.TryParse(value, out var result) ? result : 0;
    private static ulong Health(string value) => value.Trim().ToLowerInvariant() switch
    {
        "healthy" => 0, "warning" => 1, "unhealthy" => 2, _ => 5
    };
    private static ulong[] Operational(string value) => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => item.ToLowerInvariant() switch
        {
            "unknown" => 0UL, "other" => 1UL, "ok" => 2UL, "degraded" => 3UL, "error" => 6UL,
            "stopped" => 10UL, "nocontact" => 12UL, "lostcommunication" => 13UL, _ => 0UL
        }).DefaultIfEmpty(0UL).ToArray();
    private static ulong Media(string value) => value.Trim().ToUpperInvariant() switch
    {
        "HDD" => 3, "SSD" => 4, "SCM" => 5, _ => 0
    };
    private static ulong Bus(string value) => value.Trim().ToUpperInvariant() switch
    {
        "SCSI" => 1, "ATAPI" => 2, "ATA" => 3, "USB" => 7, "RAID" => 8, "SAS" => 10,
        "SATA" => 11, "VIRTUAL" => 14, "FILEBACKEDVIRTUAL" => 15, "STORAGESPACES" => 16,
        "NVME" => 17, "SCM" => 18, _ => 0
    };
    private static ulong Usage(string value)
    {
        var normalized = value.Trim().Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "" or "autoselect" or "datastore" => 1,
            "manualselect" => 2,
            "hotspare" => 3,
            "retired" => 4,
            "journal" => 5,
            _ => 0
        };
    }
    private static ulong Provisioning(string value) => value.Trim().ToLowerInvariant() switch
    {
        "thin" => 1, "fixed" => 2, _ => 0
    };
    private static ulong PartitionStyle(string value) => value.Trim().ToUpperInvariant() switch
    {
        "MBR" => 1, "GPT" => 2, _ => 0
    };
    private static ulong[] CannotPoolReasons(PhysicalDiskInfo disk) => disk.CanPool ? []
        : disk.PoolStableId is not null && !disk.PoolStableId.EndsWith(":primordial", StringComparison.OrdinalIgnoreCase) ? [2]
        : [7];
    private static string GptType(PartitionInfo partition)
    {
        if (partition.GptType.Length > 0) return partition.GptType;
        return partition.Type.Trim().ToLowerInvariant() switch
        {
            "microsoftreserved" => "{e3c9e316-0b5c-4db8-817d-f92df00215ae}",
            "efisystem" => "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}",
            "windowsrecovery" => "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}",
            "primary" or "basicdata" => "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}",
            _ => ""
        };
    }
    private static ulong MbrType(PartitionInfo partition) => ulong.TryParse(partition.MbrType, out var value) ? value : 0;
}
