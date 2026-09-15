using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional comparison projection for the accepted V0.13 snapshot model.
/// Values stay UI-neutral; localization and display-only normalization are
/// applied by the App from the presentation hint.
/// </summary>
public sealed class ManageComparisonProjector
    : IManageComparisonProjector<StorageSystemDocument>
{
    public ManageObjectComparisonView Project(
        StorageSystemDocument document,
        StorageObjectId objectId,
        ManageObjectRole role)
    {
        ArgumentNullException.ThrowIfNull(document);
        var expectedSystem = document.SystemId;
        if (objectId.System != expectedSystem)
        {
            return new ManageObjectComparisonView(objectId, []);
        }

        var snapshot = document.Snapshot;
        if (role is ManageObjectRole.Partition or ManageObjectRole.Volume or ManageObjectRole.NetworkDisk
            && snapshot.ResolvePartitionUnion(objectId.ProviderKey) is { } union)
            return new ManageObjectComparisonView(objectId, ManagePartitionProjector.Properties(snapshot, union));
        var rows = new List<ManagePropertyView>();
        switch (role)
        {
            case ManageObjectRole.System:
            {
                var uniquePhysical = snapshot.PhysicalDisks
                    .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rows.Add(P("HostName", snapshot.Computer.Name));
                rows.Add(P("Version", snapshot.Computer.WindowsProductName, ManageValuePresentation.ProductName));
                rows.Add(P("VersionNumber", snapshot.Computer.DisplayVersion));
                rows.Add(P(
                    "OsBuild",
                    string.IsNullOrWhiteSpace(snapshot.Computer.Ubr)
                        ? snapshot.Computer.OsBuild
                        : $"{snapshot.Computer.OsBuild}.{snapshot.Computer.Ubr}"));
                rows.Add(P("LocalStorage", TopologyProjector.FormatBytes(uniquePhysical.Sum(x => x.Size))));
                if (snapshot.NetworkDisks.Count > 0)
                {
                    rows.Add(P("ExternalStorage", TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size))));
                }
                rows.Add(P("StoragePool", snapshot.StoragePools.Count.ToString()));
                rows.Add(P("PhysicalDisk", uniquePhysical.Count.ToString()));
                if (snapshot.VirtualDisks.Count > 0)
                {
                    rows.Add(P("VirtualDisk", snapshot.VirtualDisks.Count.ToString()));
                }
                rows.Add(P("Partition", snapshot.PartitionUnions.Count.ToString()));
                rows.Add(P(
                    "AccessibleVolumes",
                    snapshot.PartitionUnions.Count(x => x.AccessPaths.Count > 0 || !string.IsNullOrWhiteSpace(x.DriveLetter)).ToString()));
                break;
            }
            case ManageObjectRole.StoragePool:
            {
                var pool = snapshot.StoragePools.First(x => x.StableId == objectId.ProviderKey);
                var poolVirtualDisks = snapshot.VirtualDisks
                    .Where(x => x.PoolStableId == pool.StableId)
                    .ToList();
                var poolTiers = snapshot.StorageTiers
                    .Where(x => x.PoolStableId == pool.StableId)
                    .ToList();
                var members = snapshot.PhysicalDisks
                    .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase))
                    .DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rows.Add(P(
                    "Type",
                    pool.IsPrimordial ? "OriginalPool" : "StoragePool",
                    ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(pool.Size)));
                rows.Add(P("PhysicalDisk", members.Count.ToString()));
                rows.Add(P("VirtualDisk", poolVirtualDisks.Count.ToString()));
                rows.Add(P("RunningStatus", Empty(pool.OperationalStatus)));
                rows.Add(P("Health", Empty(pool.HealthStatus)));
                rows.Add(P(
                    "ProvisioningType",
                    FirstNonEmpty(
                        pool.ProvisioningTypeDefault,
                        string.Join(", ", poolVirtualDisks.Select(x => x.ProvisioningType).Distinct()))));
                rows.Add(P(
                    "Resiliency",
                    FirstNonEmpty(string.Join(", ", poolVirtualDisks.Select(x => x.ResiliencySettingName).Distinct()))));
                rows.Add(P(
                    "PhysicalSector",
                    pool.PhysicalSectorSize is > 0
                        ? TopologyProjector.FormatBytes(pool.PhysicalSectorSize.Value)
                        : FirstNonEmpty(string.Join(", ", members.Select(x => TopologyProjector.FormatBytes(x.PhysicalSectorSize)).Distinct()))));
                rows.Add(P(
                    "LogicalSector",
                    pool.LogicalSectorSize is > 0
                        ? TopologyProjector.FormatBytes(pool.LogicalSectorSize.Value)
                        : FirstNonEmpty(string.Join(", ", members.Select(x => TopologyProjector.FormatBytes(x.LogicalSectorSize)).Distinct()))));
                rows.Add(P("PerformanceTier", TierNames(poolTiers, media => media is "SSD" or "SCM")));
                rows.Add(P("CapacityTier", TierNames(poolTiers, media => media == "HDD")));
                break;
            }
            case ManageObjectRole.StorageTier:
            {
                var tier = snapshot.StorageTiers.First(x => x.StableId == objectId.ProviderKey);
                var virtualDisk = snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == tier.VirtualDiskStableId);
                rows.Add(P(
                    "PoolOwner",
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == tier.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(P("Media", Empty(tier.MediaType)));
                rows.Add(P(
                    "Type",
                    tier.MediaType is "SSD" or "SCM"
                        ? "PerformanceTier"
                        : tier.MediaType == "HDD" ? "CapacityTier" : "StorageTier",
                    ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(tier.Size)));
                rows.Add(P("ProvisioningType", FirstNonEmpty(virtualDisk?.ProvisioningType ?? string.Empty)));
                rows.Add(P("Resiliency", Empty(tier.ResiliencySettingName)));
                rows.Add(P(
                    "FaultTolerance",
                    tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
                        ? "0"
                        : tier.ResiliencySettingName.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? "1" : string.Empty));
                rows.Add(P("PhysicalDisk", tier.MemberPhysicalDiskIds.Count.ToString()));
                rows.Add(P("Columns", (tier.NumberOfColumns ?? virtualDisk?.NumberOfColumns)?.ToString() ?? string.Empty));
                rows.Add(P(
                    "Interleave",
                    (tier.Interleave ?? virtualDisk?.Interleave) is { } interleave
                        ? TopologyProjector.FormatBytes(interleave)
                        : string.Empty));
                rows.Add(P("AllocationUnit", string.Empty));
                break;
            }
            case ManageObjectRole.DirectDiskGroup:
            {
                var pool = snapshot.StoragePools.FirstOrDefault(
                    x => $"group:direct:{x.StableId}".Equals(
                        objectId.ProviderKey,
                        StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<PhysicalDiskInfo> direct = pool is null ? [] : snapshot.DirectPoolMembers(pool.StableId);
                rows.Add(P(
                    "PoolOwner",
                    pool?.FriendlyName ?? string.Empty));
                rows.Add(P("Media", string.Empty));
                rows.Add(P("Type", pool is not null && snapshot.UnknownTierMembershipPools.Contains(pool.StableId) ? "Membership unknown" : "UnallocatedLayer", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(direct.Sum(x => x.Size))));
                rows.Add(P("PhysicalDisk", direct.Count.ToString()));
                rows.Add(P(
                    "Health",
                    direct.Count == 0
                        ? string.Empty
                        : string.Join(", ", direct.Select(x => x.HealthStatus)
                            .Distinct(StringComparer.OrdinalIgnoreCase))));
                rows.Add(P("RunningStatus", string.Empty));
                break;
            }
            case ManageObjectRole.PhysicalDisk:
            {
                var physical = snapshot.PhysicalDisks.First(x => x.StableId == objectId.ProviderKey);
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.PhysicalDiskStableId == physical.StableId)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var partitionStyle = snapshot.OsDisks
                    .FirstOrDefault(x => x.PhysicalDiskStableId == physical.StableId)
                    ?.PartitionStyle;
                rows.Add(P("DiskNumber", physical.DeviceId?.ToString() ?? string.Empty));
                rows.Add(P(
                    "PoolOwner",
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == physical.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(P("Media", Empty(physical.MediaType)));
                rows.Add(P("PartitionTable", Empty(partitionStyle ?? string.Empty)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(physical.Size)));
                rows.Add(P(
                    "Partition",
                    snapshot.Partitions.Count(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId)).ToString()));
                rows.Add(P("RunningStatus", Empty(physical.OperationalStatus)));
                rows.Add(P("Health", Empty(physical.HealthStatus)));
                rows.Add(P("LogicalSector", TopologyProjector.FormatBytes(physical.LogicalSectorSize)));
                rows.Add(P("PhysicalSector", TopologyProjector.FormatBytes(physical.PhysicalSectorSize)));
                rows.Add(P("Model", Empty(physical.Model)));
                rows.Add(P(
                    "Serial",
                    string.IsNullOrWhiteSpace(physical.SerialNumber) || physical.SerialNumber == "—"
                        ? string.Empty
                        : physical.SerialNumber,
                    ManageValuePresentation.SerialNumber));
                rows.Add(P("Firmware", Empty(physical.FirmwareVersion)));
                rows.Add(P("Bus", Empty(physical.BusType)));
                rows.Add(P("InterfaceType", Empty(physical.InterfaceType)));
                rows.Add(P("ProvisioningType", Empty(physical.ProvisioningType)));
                break;
            }
            case ManageObjectRole.VirtualDisk:
            {
                var virtualDisk = snapshot.VirtualDisks.First(x => x.StableId == objectId.ProviderKey);
                var osDiskIds = snapshot.OsDisks
                    .Where(x => x.VirtualDiskStableId == virtualDisk.StableId)
                    .Select(x => x.StableId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.Add(P(
                    "DiskNumber",
                    virtualDisk.OsDiskNumbers.Count > 0 ? virtualDisk.OsDiskNumbers[0].ToString() : string.Empty));
                rows.Add(P(
                    "PoolOwner",
                    snapshot.StoragePools.FirstOrDefault(x => x.StableId == virtualDisk.PoolStableId)?.FriendlyName ?? string.Empty));
                rows.Add(P(
                    "PartitionTable",
                    Empty(snapshot.OsDisks.FirstOrDefault(x => x.VirtualDiskStableId == virtualDisk.StableId)?.PartitionStyle ?? string.Empty)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(virtualDisk.Size)));
                rows.Add(P(
                    "Partition",
                    snapshot.Partitions.Count(x => x.OsDiskStableId is not null && osDiskIds.Contains(x.OsDiskStableId)).ToString()));
                rows.Add(P("RunningStatus", Empty(virtualDisk.OperationalStatus)));
                rows.Add(P("Health", Empty(virtualDisk.HealthStatus)));
                rows.Add(P("ProvisioningType", Empty(virtualDisk.ProvisioningType)));
                break;
            }
            case ManageObjectRole.OsDisk:
            {
                var osDisk = snapshot.OsDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("DiskNumber", osDisk.Number.ToString()));
                rows.Add(P("PartitionTable", Empty(osDisk.PartitionStyle)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(osDisk.Size)));
                rows.Add(P("Partition", snapshot.Partitions.Count(x => x.OsDiskStableId == osDisk.StableId).ToString()));
                rows.Add(P(
                    "RunningStatus",
                    osDisk.IsOffline ? "Offline" : "Online",
                    ManageValuePresentation.LocalizationKey));
                break;
            }
            case ManageObjectRole.NetworkGroup:
                rows.Add(P("Type", "NetworkStorageGroup", ManageValuePresentation.LocalizationKey));
                break;
            case ManageObjectRole.OtherGroup:
                rows.Add(P("Type", "OtherStorageGroup", ManageValuePresentation.LocalizationKey));
                break;
            default:
                rows.Add(P("Type", role.ToString()));
                break;
        }

        return new ManageObjectComparisonView(objectId, WinPoolHardwarePresentation.MarkUnavailable(snapshot, objectId.ProviderKey, rows));
    }

    private static ManagePropertyView P(
        string key,
        string value,
        ManageValuePresentation presentation = ManageValuePresentation.Plain) =>
        new(key, value, presentation);

    private static string TierNames(
        IReadOnlyList<StorageTierInfo> tiers,
        Func<string, bool> mediaPredicate)
    {
        var names = tiers
            .Where(x => mediaPredicate(x.MediaType))
            .Select(x => x.FriendlyName)
            .ToList();
        return names.Count == 0 ? string.Empty : string.Join(", ", names);
    }

    private static string PartitionOwnerName(StorageSnapshot snapshot, PartitionInfo partition)
    {
        var osDisk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == partition.OsDiskStableId);
        if (osDisk is null)
        {
            return string.Empty;
        }
        if (osDisk.VirtualDiskStableId is not null)
        {
            return snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == osDisk.VirtualDiskStableId)?.FriendlyName ?? string.Empty;
        }
        if (osDisk.PhysicalDiskStableId is not null)
        {
            return snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == osDisk.PhysicalDiskStableId)?.FriendlyName ?? string.Empty;
        }
        return osDisk.FriendlyName;
    }

    private static string Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string ReportMemory(StorageSystemDocument document) => document.Unified?.TotalMemoryBytes switch
    {
        null => string.Empty,
        <= long.MaxValue and var bytes => TopologyProjector.FormatBytes((long)bytes),
        var bytes => bytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes"
    };
}
