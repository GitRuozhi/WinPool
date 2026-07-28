using System.Security.Cryptography;
using System.Text;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

internal sealed class RawSnapshot
{
    public DateTimeOffset ScannedAt { get; set; }
    public RawComputer Computer { get; set; } = new();
    public List<RawSubsystem> StorageSubsystems { get; set; } = [];
    public List<RawPhysicalDisk> PhysicalDisks { get; set; } = [];
    public List<RawPool> StoragePools { get; set; } = [];
    public List<RawTier> StorageTiers { get; set; } = [];
    public List<RawVirtualDisk> VirtualDisks { get; set; } = [];
    public List<RawOsDisk> OsDisks { get; set; } = [];
    public List<RawPartition> Partitions { get; set; } = [];
    public List<RawNetworkDisk> NetworkDisks { get; set; } = [];
    public List<RawLogicalVolume> LogicalVolumes { get; set; } = [];
    public List<RawDiskDrive> DiskDrives { get; set; } = [];
    public RawHardware Hardware { get; set; } = new();
    public List<RawWarning> Warnings { get; set; } = [];
}

internal sealed class RawComputer
{
    public string Name { get; set; } = string.Empty;
    public string WindowsProductName { get; set; } = string.Empty;
    public string WindowsVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public DateTimeOffset LastBootTime { get; set; }
}

internal class RawIdentity
{
    public string UniqueId { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string AssociationKey { get; set; } = string.Empty;
}

internal sealed class RawSubsystem : RawIdentity
{
    public string FriendlyName { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
}

internal sealed class RawPhysicalDisk : RawIdentity
{
    public string FriendlyName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long Size { get; set; }
    public long LogicalSectorSize { get; set; }
    public long PhysicalSectorSize { get; set; }
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
    public bool CanPool { get; set; }
    public string CannotPoolReason { get; set; } = string.Empty;
    public int? DeviceId { get; set; }
    public string FirmwareVersion { get; set; } = string.Empty;
    public string ProvisioningType { get; set; } = string.Empty;
    public string PhysicalLocation { get; set; } = string.Empty;
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public bool IsPageFile { get; set; }
    public bool IsCrashDump { get; set; }
    public string PoolAssociationKey { get; set; } = string.Empty;
}

internal sealed class RawPool : RawIdentity
{
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsPrimordial { get; set; }
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public long? LogicalSectorSize { get; set; }
    public long? PhysicalSectorSize { get; set; }
    public string ProvisioningTypeDefault { get; set; } = string.Empty;
    public string SubsystemAssociationKey { get; set; } = string.Empty;
    public List<string> MemberPhysicalDiskKeys { get; set; } = [];
}

internal sealed class RawTier : RawIdentity
{
    public string FriendlyName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string ResiliencySettingName { get; set; } = string.Empty;
    public long Size { get; set; }
    public long FootprintOnPool { get; set; }
    public int? NumberOfColumns { get; set; }
    public long? Interleave { get; set; }
    public string PoolAssociationKey { get; set; } = string.Empty;
    public string VirtualDiskAssociationKey { get; set; } = string.Empty;
    public List<string> MemberPhysicalDiskKeys { get; set; } = [];
}

internal sealed class RawVirtualDisk : RawIdentity
{
    public string FriendlyName { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
    public string ResiliencySettingName { get; set; } = string.Empty;
    public string ProvisioningType { get; set; } = string.Empty;
    public int? NumberOfColumns { get; set; }
    public long? Interleave { get; set; }
    public long Size { get; set; }
    public long FootprintOnPool { get; set; }
    public string PoolAssociationKey { get; set; } = string.Empty;
    public List<string> TierAssociationKeys { get; set; } = [];
    public List<int> OsDiskNumbers { get; set; } = [];
}

internal sealed class RawOsDisk
{
    public int Number { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string PartitionStyle { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public bool IsOffline { get; set; }
    public int? NumberOfPartitions { get; set; }
    public string Path { get; set; } = string.Empty;
    public string PhysicalDiskAssociationKey { get; set; } = string.Empty;
    public string VirtualDiskAssociationKey { get; set; } = string.Empty;
}

internal sealed class RawPartition
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }
    public string Guid { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string MbrType { get; set; } = string.Empty;
    public string GptType { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long Size { get; set; }
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public string DriveLetter { get; set; } = string.Empty;
    public string FileSystemLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long? AllocationUnitSize { get; set; }
    public long SizeRemaining { get; set; }
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsHidden { get; set; }
    public string DriveType { get; set; } = string.Empty;
    public string VolumeUniqueId { get; set; } = string.Empty;
    public string VolumeObjectId { get; set; } = string.Empty;
}

internal sealed class RawLogicalVolume
{
    public string DeviceID { get; set; } = string.Empty;
    public int? DriveType { get; set; }
    public string VolumeSerialNumber { get; set; } = string.Empty;
    public bool? Compressed { get; set; }
    public string ProviderName { get; set; } = string.Empty;
}

internal sealed class RawDiskDrive
{
    public int? Index { get; set; }
    public string InterfaceType { get; set; } = string.Empty;
    public string PNPDeviceID { get; set; } = string.Empty;
    public int? SCSIBus { get; set; }
    public int? SCSILogicalUnit { get; set; }
    public int? SCSIPort { get; set; }
    public int? SCSITargetId { get; set; }
}

internal sealed class RawNetworkDisk
{
    public string DeviceId { get; set; } = string.Empty;
    public string VolumeName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long Size { get; set; }
    public long FreeSpace { get; set; }
}

internal sealed class RawWarning
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AssociationKey { get; set; } = string.Empty;
}

internal static class RawSnapshotProjector
{
    public static StorageSnapshot Project(RawSnapshot raw, string sourceJson)
    {
        var physicalMap = Map(raw.PhysicalDisks, "physical", x => [x.DeviceId, x.FriendlyName, x.Model, x.Size]);
        var poolMap = Map(raw.StoragePools, "pool", x => [x.FriendlyName, x.Size]);
        var tierMap = Map(raw.StorageTiers, "tier", x => [x.FriendlyName, x.MediaType, x.Size]);
        var virtualMap = Map(raw.VirtualDisks, "virtual", x => [x.FriendlyName, x.Size]);
        var subsystemMap = Map(raw.StorageSubsystems, "subsystem", x => [x.FriendlyName]);
        var osDiskMap = raw.OsDisks.ToDictionary(
            x => x.Number,
            x => StableId.Create("osdisk", x.UniqueId, null, x.Number, x.FriendlyName, x.Size).Value);
        var partitionMap = raw.Partitions.ToDictionary(
            x => (x.DiskNumber, x.PartitionNumber),
            x => StableId.Create("partition", x.Guid, null, x.DiskNumber, x.PartitionNumber, x.Offset, x.Size).Value);

        var partitions = raw.Partitions.Select(x =>
        {
            var driveLetter = TopologyProjector.NormalizeDriveLetter(x.DriveLetter);
            return new PartitionInfo(
            partitionMap[(x.DiskNumber, x.PartitionNumber)],
            !string.IsNullOrWhiteSpace(x.Guid),
            x.DiskNumber,
            x.PartitionNumber,
            ClassifyPartition(x),
            x.Offset,
            x.Size,
            x.IsBoot,
            x.IsSystem,
            driveLetter,
            x.FileSystemLabel.Replace('\0', ' ').Trim(),
            x.FileSystem,
            x.AllocationUnitSize,
            x.SizeRemaining,
            x.HealthStatus,
            x.OperationalStatus,
            string.IsNullOrWhiteSpace(driveLetter) ? x.Path : $"{driveLetter}:\\",
            osDiskMap.GetValueOrDefault(x.DiskNumber),
            x.IsHidden);
        }).ToList();

        var physicalDisks = raw.PhysicalDisks.Select(x =>
        {
            var id = physicalMap[x.AssociationKey];
            var drive = raw.DiskDrives.FirstOrDefault(candidate => candidate.Index == x.DeviceId);
            return new PhysicalDiskInfo(
                id.Value, id.IsStable, First(x.FriendlyName, x.Model, $"Physical disk {x.DeviceId}"), x.Model,
                string.IsNullOrWhiteSpace(x.SerialNumber) ? "—" : x.SerialNumber.Trim(), x.BusType, x.MediaType, x.Size, x.LogicalSectorSize,
                x.PhysicalSectorSize, x.HealthStatus, x.OperationalStatus, x.CanPool, x.CannotPoolReason,
                x.DeviceId, x.IsBoot, x.IsSystem, x.IsPageFile, x.IsCrashDump,
                Resolve(poolMap, x.PoolAssociationKey),
                x.FirmwareVersion.Trim(),
                (drive?.InterfaceType ?? string.Empty).Trim(),
                x.ProvisioningType.Trim(),
                (drive?.PNPDeviceID ?? string.Empty).Trim());
        }).ToList();

        var pools = raw.StoragePools.Select(x =>
        {
            var id = poolMap[x.AssociationKey];
            return new StoragePoolInfo(
                id.Value, id.IsStable, x.IsPrimordial ? "Primordial" : x.FriendlyName, x.IsPrimordial,
                x.HealthStatus, x.OperationalStatus, x.Size,
                x.AllocatedSize, Resolve(subsystemMap, x.SubsystemAssociationKey),
                ResolveMany(physicalMap, x.MemberPhysicalDiskKeys),
                x.LogicalSectorSize,
                x.PhysicalSectorSize,
                x.ProvisioningTypeDefault.Trim());
        }).ToList();

        if (!pools.Any(x => x.IsPrimordial))
        {
            var fallbackComputerName = First(raw.Computer.Name, Environment.MachineName);
            var primordialId = $"pool:primordial:{fallbackComputerName.ToLowerInvariant()}";
            var memberIds = physicalDisks
                .Where(x => string.IsNullOrWhiteSpace(x.PoolStableId))
                .Select(x => x.StableId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (memberIds.Count > 0)
            {
                pools.Insert(0, new StoragePoolInfo(
                    primordialId,
                    true,
                    "Primordial",
                    true,
                    "Healthy",
                    "OK",
                    physicalDisks.Where(x => memberIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)).Sum(x => x.Size),
                    0,
                    null,
                    memberIds));
                physicalDisks = physicalDisks
                    .Select(x => memberIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)
                        ? x with { PoolStableId = primordialId }
                        : x)
                    .ToList();
            }
        }

        var tiers = raw.StorageTiers.Select(x =>
        {
            var id = tierMap[x.AssociationKey];
            return new StorageTierInfo(
                id.Value, id.IsStable, x.FriendlyName, x.MediaType, x.ResiliencySettingName, x.Size,
                x.FootprintOnPool, Resolve(poolMap, x.PoolAssociationKey),
                Resolve(virtualMap, x.VirtualDiskAssociationKey),
                ResolveMany(physicalMap, x.MemberPhysicalDiskKeys),
                x.NumberOfColumns,
                x.Interleave);
        }).ToList();

        var virtualDisks = raw.VirtualDisks.Select(x =>
        {
            var id = virtualMap[x.AssociationKey];
            return new VirtualDiskInfo(
                id.Value, id.IsStable, x.FriendlyName, x.HealthStatus, x.OperationalStatus,
                x.ResiliencySettingName, x.ProvisioningType, x.NumberOfColumns, x.Interleave, x.Size,
                x.FootprintOnPool, Resolve(poolMap, x.PoolAssociationKey),
                ResolveMany(tierMap, x.TierAssociationKeys), x.OsDiskNumbers);
        }).ToList();

        var osDisks = raw.OsDisks.Select(x => new OsDiskInfo(
            osDiskMap[x.Number], x.FriendlyName, x.Number, x.PartitionStyle, x.Size, x.IsBoot,
            x.IsSystem, x.IsOffline, Resolve(physicalMap, x.PhysicalDiskAssociationKey),
            Resolve(virtualMap, x.VirtualDiskAssociationKey))).ToList();

        var subsystems = raw.StorageSubsystems.Select(x =>
        {
            var id = subsystemMap[x.AssociationKey];
            return new StorageSubsystemInfo(id.Value, x.FriendlyName, x.HealthStatus, x.OperationalStatus);
        }).ToList();

        var networkDisks = raw.NetworkDisks.Select(x =>
        {
            var id = StableId.Create("network", x.ProviderName, null, x.DeviceId, x.VolumeName, x.Size);
            var drive = x.DeviceId.Trim().TrimEnd(':');
            var name = string.IsNullOrWhiteSpace(x.VolumeName)
                ? (string.IsNullOrWhiteSpace(drive) ? x.ProviderName : $"{drive}:")
                : (string.IsNullOrWhiteSpace(drive) ? x.VolumeName : $"{drive}: {x.VolumeName}");
            return new NetworkDiskInfo(
                id.Value,
                id.IsStable,
                name,
                drive,
                x.ProviderName,
                x.FileSystem,
                x.Size,
                x.FreeSpace);
        }).ToList();

        var relationships = BuildRelationships(pools, tiers, virtualDisks, osDisks, partitions);
        var warnings = raw.Warnings.Select(x => new InventoryWarning(
            x.Code,
            x.Message,
            Resolve(physicalMap, x.AssociationKey)
            ?? Resolve(poolMap, x.AssociationKey)
            ?? Resolve(tierMap, x.AssociationKey)
            ?? Resolve(virtualMap, x.AssociationKey))).ToList();

        var snapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson)))[..16].ToLowerInvariant();
        var computerName = First(raw.Computer.Name, Environment.MachineName);
        return new StorageSnapshot(
            2,
            snapshotHash,
            raw.ScannedAt,
            new ComputerInfo(
                $"system:{computerName.ToLowerInvariant()}",
                computerName,
                raw.Computer.WindowsProductName,
                raw.Computer.WindowsVersion,
                raw.Computer.OsBuild,
                raw.Computer.LastBootTime,
                raw.Hardware?.OperatingSystem.DisplayVersion ?? string.Empty,
                raw.Hardware?.OperatingSystem.UBR ?? string.Empty),
            subsystems,
            physicalDisks,
            pools,
            tiers,
            virtualDisks,
            osDisks,
            partitions,
            networkDisks,
            relationships,
            warnings);
    }

    private static Dictionary<string, (string Value, bool IsStable)> Map<T>(
        IEnumerable<T> source,
        string kind,
        Func<T, object?[]> fallback)
        where T : RawIdentity =>
        source.ToDictionary(
            x => x.AssociationKey,
            x => StableId.Create(kind, x.UniqueId, x.ObjectId, fallback(x)),
            StringComparer.OrdinalIgnoreCase);

    private static List<StorageRelationship> BuildRelationships(
        IEnumerable<StoragePoolInfo> pools,
        IEnumerable<StorageTierInfo> tiers,
        IEnumerable<VirtualDiskInfo> virtualDisks,
        IEnumerable<OsDiskInfo> osDisks,
        IEnumerable<PartitionInfo> partitions)
    {
        var result = new List<StorageRelationship>();
        foreach (var pool in pools)
        {
            result.AddRange(pool.MemberPhysicalDiskIds.Select(id => new StorageRelationship(pool.StableId, id, "PoolMember")));
        }
        foreach (var tier in tiers)
        {
            if (tier.PoolStableId is not null)
            {
                result.Add(new StorageRelationship(tier.PoolStableId, tier.StableId, "ContainsTier"));
            }
            result.AddRange(tier.MemberPhysicalDiskIds.Select(id => new StorageRelationship(tier.StableId, id, "TierDiskReference")));
        }
        foreach (var virtualDisk in virtualDisks)
        {
            if (virtualDisk.PoolStableId is not null)
            {
                result.Add(new StorageRelationship(virtualDisk.PoolStableId, virtualDisk.StableId, "ContainsVirtualDisk"));
            }
        }
        foreach (var osDisk in osDisks)
        {
            var parent = osDisk.VirtualDiskStableId ?? osDisk.PhysicalDiskStableId;
            if (parent is not null)
            {
                result.Add(new StorageRelationship(parent, osDisk.StableId, "MapsToOsDisk"));
            }
        }
        foreach (var partition in partitions)
        {
            if (partition.OsDiskStableId is not null)
            {
                result.Add(new StorageRelationship(partition.OsDiskStableId, partition.StableId, "ContainsPartition"));
            }
        }
        return result;
    }

    internal static string ClassifyPartition(RawPartition partition)
    {
        var raw = First(partition.Type, partition.GptType, partition.MbrType);
        var normalized = raw.Trim().Trim('{', '}').ToLowerInvariant();
        if (partition.IsSystem && normalized is not "c12a7328-f81f-11d2-ba4b-00a0c93ec93b")
        {
            return "SystemReserved";
        }

        return normalized switch
        {
            "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" or "system" => "EfiSystem",
            "e3c9e316-0b5c-4db8-817d-f92df00215ae" or "reserved" or "msr" => "MicrosoftReserved",
            "de94bba4-06d1-4d40-a16a-bfd50179d6ac" or "recovery" => "WindowsRecovery",
            "extended" or "0x05" or "0x0f" => "Extended",
            "basic" or "primary" or "ifs" or "fat12" or "fat16" or "fat32"
                or "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Primary",
            "simple" => "Simple",
            "spanned" => "Spanned",
            "striped" => "Striped",
            _ => "Unknown"
        };
    }

    private static string? Resolve(
        IReadOnlyDictionary<string, (string Value, bool IsStable)> map,
        string? key) =>
        string.IsNullOrWhiteSpace(key) || !map.TryGetValue(key, out var value) ? null : value.Value;

    private static List<string> ResolveMany(
        IReadOnlyDictionary<string, (string Value, bool IsStable)> map,
        IEnumerable<string> keys) =>
        keys.Select(key => Resolve(map, key))
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string First(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
