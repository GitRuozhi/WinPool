using System.Text.Json;
using WinPool.Application;
using WinPool.Core;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional comparison projection for the accepted V0.13 snapshot model.
/// Values stay UI-neutral; localization, privacy masking, and display-only
/// normalization are applied by the App from the presentation hint.
/// </summary>
public sealed class LegacyManageComparisonProjector
    : IManageComparisonProjector<StorageSystemDocument>
{
    public ManageObjectComparisonView Project(
        StorageSystemDocument document,
        StorageObjectId objectId,
        ManageObjectRole role)
    {
        ArgumentNullException.ThrowIfNull(document);
        var expectedSystem = InternalStableIdentity.SystemFromDocumentId(document.Id);
        if (objectId.System != expectedSystem)
        {
            throw new ArgumentException(
                "The comparison object does not belong to the supplied document.",
                nameof(objectId));
        }

        var snapshot = document.Snapshot;
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
                rows.Add(P("Cpu", ReportValue(document, "0401") ?? string.Empty));
                rows.Add(P("Memory", ReportMemory(document)));
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
                rows.Add(P("Partition", snapshot.Partitions.Count.ToString()));
                rows.Add(P(
                    "AccessibleVolumes",
                    (snapshot.Partitions.Count(x => !string.IsNullOrWhiteSpace(x.Path))
                        + snapshot.NetworkDisks.Count(x => !string.IsNullOrWhiteSpace(x.DriveLetter))).ToString()));
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
                    string.IsNullOrWhiteSpace(physical.MaskedSerialNumber) || physical.MaskedSerialNumber == "—"
                        ? string.Empty
                        : physical.MaskedSerialNumber,
                    ManageValuePresentation.MaskedSerial));
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
            case ManageObjectRole.Partition:
            {
                var partition = snapshot.Partitions.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("OwningDisk", PartitionOwnerName(snapshot, partition)));
                rows.Add(P("Type", partition.Type, ManageValuePresentation.PartitionType));
                rows.Add(P("FileSystem", string.IsNullOrWhiteSpace(partition.FileSystem) ? string.Empty : partition.FileSystem));
                rows.Add(P(
                    "AllocationUnit",
                    partition.AllocationUnitSize is null
                        ? string.Empty
                        : TopologyProjector.FormatBytes(partition.AllocationUnitSize.Value)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(partition.Size)));
                rows.Add(P(
                    "Available",
                    string.IsNullOrWhiteSpace(partition.FileSystem)
                        ? string.Empty
                        : TopologyProjector.FormatBytes(partition.SizeRemaining)));
                rows.Add(P("SystemPartition", partition.IsBoot || partition.IsSystem ? "✓" : string.Empty));
                rows.Add(P("PartitionStatus", Empty(partition.OperationalStatus)));
                rows.Add(P("StartOffset", TopologyProjector.FormatBytes(partition.Offset)));
                rows.Add(P("DriveLetter", Empty(TopologyProjector.NormalizeDriveLetter(partition.DriveLetter))));
                rows.Add(P("VolumeLabel", Empty(partition.FileSystemLabel.Replace('\0', ' ').Trim())));
                rows.Add(P("Path", string.IsNullOrWhiteSpace(partition.Path) ? string.Empty : partition.Path));
                break;
            }
            case ManageObjectRole.NetworkDisk:
            {
                var network = snapshot.NetworkDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("FileSystem", Empty(network.FileSystem)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(network.Size)));
                rows.Add(P("Available", TopologyProjector.FormatBytes(network.SizeRemaining)));
                rows.Add(P("DriveLetter", Empty(TopologyProjector.NormalizeDriveLetter(network.DriveLetter))));
                rows.Add(P("Path", Empty(network.ProviderPath)));
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

        return new ManageObjectComparisonView(objectId, rows);
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

    private static string? ReportValue(StorageSystemDocument document, string itemId)
    {
        var item = document.HardwareReport.Items.FirstOrDefault(x => x.Id == itemId);
        if (item?.FinalValue is not { } element || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return element.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string ReportMemory(StorageSystemDocument document)
    {
        var item = document.HardwareReport.Items.FirstOrDefault(x => x.Id == "0504");
        if (item?.FinalValue is not { } element || element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        var values = element.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (values.Count == 0)
        {
            return string.Empty;
        }
        long total = 0;
        var parsed = 0;
        foreach (var value in values)
        {
            if (TryParseByteSize(value!, out var bytes))
            {
                total += bytes;
                parsed++;
            }
        }
        return parsed == values.Count && parsed > 0
            ? TopologyProjector.FormatBytes(total)
            : string.Join(" + ", values);
    }

    private static bool TryParseByteSize(string text, out long bytes)
    {
        bytes = 0;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], out var amount))
        {
            return false;
        }
        var multiplier = parts[1] switch
        {
            "B" => 1L,
            "KiB" => 1L << 10,
            "MiB" => 1L << 20,
            "GiB" => 1L << 30,
            "TiB" => 1L << 40,
            "PiB" => 1L << 50,
            _ => 0L
        };
        if (multiplier == 0)
        {
            return false;
        }
        bytes = (long)(amount * multiplier);
        return true;
    }
}
