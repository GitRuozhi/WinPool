using System.Security.Cryptography;
using System.Text;

namespace WinPool.Application;

public enum WorkspaceCategory
{
    System,
    Pool,
    Tier,
    Disk,
    Partition,
    Volume
}

public enum StorageUnitKind
{
    System,
    StorageSubsystem,
    StoragePool,
    StorageTier,
    PhysicalDisk,
    VirtualDisk,
    NetworkDisk,
    OsDisk,
    Partition,
    NetworkDiskGroup,
    OtherDiskGroup,
    DirectDiskGroup,
    VirtualDiskGroup
}

public sealed record StorageUnitRef(
    string StableId,
    StorageUnitKind Kind,
    string DisplayName,
    bool IsStable = true,
    string? ParentStableId = null);

public sealed record StorageRelationship(
    string FromStableId,
    string ToStableId,
    string RelationshipKind);

public sealed record WorkspaceSelection(
    WorkspaceCategory Category,
    string? StableId,
    StorageUnitKind? ContextKind = null,
    string? ContextStableId = null);

public sealed record InventoryWarning(
    string Code,
    string Message,
    string? StableId = null);

public sealed record ComputerInfo(
    string StableId,
    string Name,
    string WindowsProductName,
    string WindowsVersion,
    string OsBuild,
    DateTimeOffset LastBootTime,
    string DisplayVersion = "",
    string Ubr = "");

public sealed record StorageSubsystemInfo(
    string StableId,
    string FriendlyName,
    string HealthStatus,
    string OperationalStatus);

public sealed record PhysicalDiskInfo(
    string StableId,
    bool IsStable,
    string FriendlyName,
    string Model,
    string MaskedSerialNumber,
    string BusType,
    string MediaType,
    long Size,
    long LogicalSectorSize,
    long PhysicalSectorSize,
    string HealthStatus,
    string OperationalStatus,
    bool CanPool,
    string CannotPoolReason,
    int? DeviceId,
    bool IsBoot,
    bool IsSystem,
    bool IsPageFile,
    bool IsCrashDump,
    string? PoolStableId,
    string FirmwareVersion = "",
    string InterfaceType = "",
    string ProvisioningType = "",
    string PnpDeviceId = "");

public sealed record StoragePoolInfo(
    string StableId,
    bool IsStable,
    string FriendlyName,
    bool IsPrimordial,
    string HealthStatus,
    string OperationalStatus,
    long Size,
    long AllocatedSize,
    string? SubsystemStableId,
    IReadOnlyList<string> MemberPhysicalDiskIds,
    long? LogicalSectorSize = null,
    long? PhysicalSectorSize = null,
    string ProvisioningTypeDefault = "");

public sealed record StorageTierInfo(
    string StableId,
    bool IsStable,
    string FriendlyName,
    string MediaType,
    string ResiliencySettingName,
    long Size,
    long FootprintOnPool,
    string? PoolStableId,
    string? VirtualDiskStableId,
    IReadOnlyList<string> MemberPhysicalDiskIds,
    int? NumberOfColumns = null,
    long? Interleave = null);

public sealed record VirtualDiskInfo(
    string StableId,
    bool IsStable,
    string FriendlyName,
    string HealthStatus,
    string OperationalStatus,
    string ResiliencySettingName,
    string ProvisioningType,
    int? NumberOfColumns,
    long? Interleave,
    long Size,
    long FootprintOnPool,
    string? PoolStableId,
    IReadOnlyList<string> TierStableIds,
    IReadOnlyList<int> OsDiskNumbers);

public sealed record OsDiskInfo(
    string StableId,
    string FriendlyName,
    int Number,
    string PartitionStyle,
    long Size,
    bool IsBoot,
    bool IsSystem,
    bool IsOffline,
    string? PhysicalDiskStableId,
    string? VirtualDiskStableId);

public sealed record PartitionInfo(
    string StableId,
    bool IsStable,
    int DiskNumber,
    int PartitionNumber,
    string Type,
    long Offset,
    long Size,
    bool IsBoot,
    bool IsSystem,
    string DriveLetter,
    string FileSystemLabel,
    string FileSystem,
    long? AllocationUnitSize,
    long SizeRemaining,
    string HealthStatus,
    string OperationalStatus,
    string Path,
    string? OsDiskStableId,
    bool IsHidden = false);

public sealed record NetworkDiskInfo(
    string StableId,
    bool IsStable,
    string Name,
    string DriveLetter,
    string ProviderPath,
    string FileSystem,
    long Size,
    long SizeRemaining);

public sealed record StorageSnapshot(
    int SchemaVersion,
    string SnapshotVersion,
    DateTimeOffset ScannedAt,
    ComputerInfo Computer,
    IReadOnlyList<StorageSubsystemInfo> StorageSubsystems,
    IReadOnlyList<PhysicalDiskInfo> PhysicalDisks,
    IReadOnlyList<StoragePoolInfo> StoragePools,
    IReadOnlyList<StorageTierInfo> StorageTiers,
    IReadOnlyList<VirtualDiskInfo> VirtualDisks,
    IReadOnlyList<OsDiskInfo> OsDisks,
    IReadOnlyList<PartitionInfo> Partitions,
    IReadOnlyList<NetworkDiskInfo> NetworkDisks,
    IReadOnlyList<StorageRelationship> Relationships,
    IReadOnlyList<InventoryWarning> Warnings)
{
    public static StorageSnapshot Empty(string computerName) =>
        new(
            2,
            "empty",
            DateTimeOffset.MinValue,
            new ComputerInfo(
                $"system:{computerName.ToLowerInvariant()}",
                computerName,
                string.Empty,
                string.Empty,
                string.Empty,
                DateTimeOffset.MinValue),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

    public StorageUnitRef? FindUnit(string? stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
        {
            return null;
        }

        if (Computer.StableId == stableId)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.System, Computer.Name);
        }

        if (NetworkDisks.Count > 0 && TopologyProjector.NetworkGroupStableId(this) == stableId)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.NetworkDiskGroup, "Network");
        }

        var virtualDiskGroupPool = StoragePools.FirstOrDefault(
            candidate => stableId == $"group:vdisk:{candidate.StableId}"
                && VirtualDisks.Count(disk => disk.PoolStableId == candidate.StableId) > 1);
        if (virtualDiskGroupPool is not null)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.VirtualDiskGroup, string.Empty, true, virtualDiskGroupPool.StableId);
        }

        if (TopologyProjector.GetOtherOsDisks(this).Count > 0
            && TopologyProjector.OtherGroupStableId(this) == stableId)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.OtherDiskGroup, "Other");
        }

        var pool = StoragePools.FirstOrDefault(x => x.StableId == stableId);
        if (pool is not null)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.StoragePool, pool.FriendlyName, pool.IsStable);
        }

        var tier = StorageTiers.FirstOrDefault(x => x.StableId == stableId);
        if (tier is not null)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.StorageTier, tier.FriendlyName, tier.IsStable, tier.PoolStableId);
        }

        var disk = PhysicalDisks.FirstOrDefault(x => x.StableId == stableId);
        if (disk is not null)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.PhysicalDisk, disk.FriendlyName, disk.IsStable, disk.PoolStableId);
        }

        var virtualDisk = VirtualDisks.FirstOrDefault(x => x.StableId == stableId);
        if (virtualDisk is not null)
        {
            return new StorageUnitRef(stableId, StorageUnitKind.VirtualDisk, virtualDisk.FriendlyName, virtualDisk.IsStable, virtualDisk.PoolStableId);
        }

        var partition = Partitions.FirstOrDefault(x => x.StableId == stableId);
        if (partition is not null)
        {
            return new StorageUnitRef(
                stableId,
                StorageUnitKind.Partition,
                TopologyProjector.PartitionDisplayName(partition),
                partition.IsStable,
                partition.OsDiskStableId);
        }

        var networkDisk = NetworkDisks.FirstOrDefault(x => x.StableId == stableId);
        if (networkDisk is not null)
        {
            return new StorageUnitRef(
                stableId,
                StorageUnitKind.NetworkDisk,
                networkDisk.Name,
                networkDisk.IsStable);
        }

        var osDisk = OsDisks.FirstOrDefault(x => x.StableId == stableId);
        return osDisk is null
            ? null
            : new StorageUnitRef(stableId, StorageUnitKind.OsDisk, $"Disk {osDisk.Number}", true, osDisk.PhysicalDiskStableId ?? osDisk.VirtualDiskStableId);
    }

}

public static class StableId
{
    public static (string Value, bool IsStable) Create(
        string kind,
        string? uniqueId,
        string? objectId,
        params object?[] fallbackParts)
    {
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            return ($"{kind}:uid:{Normalize(uniqueId)}", true);
        }

        if (!string.IsNullOrWhiteSpace(objectId))
        {
            return ($"{kind}:oid:{Normalize(objectId)}", true);
        }

        var material = string.Join(
            "|",
            fallbackParts.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return ($"{kind}:unstable:{hash[..24]}", false);
    }

    public static string MaskSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var normalized = value.Trim();
        if (normalized.Length <= 4)
        {
            return new string('•', normalized.Length);
        }

        return $"{normalized[..2]}{new string('•', Math.Min(10, normalized.Length - 4))}{normalized[^2..]}";
    }

    private static string Normalize(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..24].ToLowerInvariant();
}
