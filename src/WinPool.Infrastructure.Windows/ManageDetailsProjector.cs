using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional details projection for the accepted V0.13 snapshot model.
/// It preserves the frozen row order while keeping localization and privacy
/// presentation in the App.
/// </summary>
public sealed class ManageDetailsProjector
    : IManageDetailsProjector<StorageSystemDocument>
{
    public ManageObjectDetailsView Project(
        StorageSystemDocument document,
        StorageObjectId objectId,
        ManageObjectRole role,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (objectId.System != document.SystemId)
        {
            throw new ArgumentException(
                "The details object does not belong to the supplied document.",
                nameof(objectId));
        }

        var snapshot = document.Snapshot;
        var title = displayName;
        var rows = new List<ManagePropertyView>();
        switch (role)
        {
            case ManageObjectRole.System:
                rows.Add(P("Windows", $"{snapshot.Computer.WindowsProductName} {snapshot.Computer.WindowsVersion} ({snapshot.Computer.OsBuild})"));
                rows.Add(P("PhysicalDisk", snapshot.PhysicalDisks.Count.ToString()));
                rows.Add(P("StoragePool", snapshot.StoragePools.Count.ToString()));
                rows.Add(P("StorageTier", snapshot.StorageTiers.Count.ToString()));
                rows.Add(P("VirtualDisk", snapshot.VirtualDisks.Count.ToString()));
                rows.Add(P("NetworkDisk", snapshot.NetworkDisks.Count.ToString()));
                rows.Add(P("Partition", snapshot.Partitions.Count.ToString()));
                break;
            case ManageObjectRole.StoragePool:
            {
                var pool = snapshot.StoragePools.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P(
                    "Type",
                    pool.IsPrimordial ? "OriginalPool" : "StoragePool",
                    ManageValuePresentation.LocalizationKey));
                rows.Add(P("Health", TopologyProjector.JoinSummary(pool.HealthStatus, pool.OperationalStatus)));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(pool.Size)));
                rows.Add(P("Allocated", TopologyProjector.FormatBytes(pool.AllocatedSize)));
                rows.Add(P("Members", pool.MemberPhysicalDiskIds.Count.ToString()));
                break;
            }
            case ManageObjectRole.StorageTier:
            {
                var tier = snapshot.StorageTiers.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("Media", tier.MediaType));
                rows.Add(P("Role", tier.ResiliencySettingName));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(tier.Size)));
                rows.Add(P("Members", tier.MemberPhysicalDiskIds.Count.ToString()));
                break;
            }
            case ManageObjectRole.DirectDiskGroup:
            {
                var pool = snapshot.StoragePools.FirstOrDefault(
                    x => $"group:direct:{x.StableId}".Equals(
                        objectId.ProviderKey,
                        StringComparison.OrdinalIgnoreCase));
                if (pool is null)
                {
                    break;
                }
                var tierMemberIds = snapshot.StorageTiers
                    .Where(x => x.PoolStableId == pool.StableId)
                    .SelectMany(x => x.MemberPhysicalDiskIds)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var direct = snapshot.PhysicalDisks
                    .Where(x => pool.MemberPhysicalDiskIds.Contains(x.StableId, StringComparer.OrdinalIgnoreCase)
                        && !tierMemberIds.Contains(x.StableId))
                    .ToList();
                rows.Add(P("Type", "UnallocatedLayer", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(direct.Sum(x => x.Size))));
                rows.Add(P("Members", direct.Count.ToString()));
                rows.Add(P("Health", TopologyProjector.JoinSummary(
                    direct.Select(x => x.HealthStatus)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray())));
                break;
            }
            case ManageObjectRole.PhysicalDisk:
            {
                var disk = snapshot.PhysicalDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("Model", disk.Model));
                rows.Add(P("Serial", disk.MaskedSerialNumber, ManageValuePresentation.MaskedSerial));
                rows.Add(P("Bus", disk.BusType));
                rows.Add(P("Media", disk.MediaType));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(disk.Size)));
                rows.Add(P("Health", TopologyProjector.JoinSummary(disk.HealthStatus, disk.OperationalStatus)));
                rows.Add(P(
                    "CanPool",
                    disk.CanPool ? "Yes" : "No",
                    ManageValuePresentation.LocalizationKey));
                if (!disk.CanPool && !string.IsNullOrWhiteSpace(disk.CannotPoolReason))
                {
                    rows.Add(P("CannotPoolReason", disk.CannotPoolReason));
                }
                break;
            }
            case ManageObjectRole.VirtualDisk:
            {
                var virtualDisk = snapshot.VirtualDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("Health", TopologyProjector.JoinSummary(virtualDisk.HealthStatus, virtualDisk.OperationalStatus)));
                rows.Add(P("Role", virtualDisk.ResiliencySettingName));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(virtualDisk.Size)));
                rows.Add(P("Columns", virtualDisk.NumberOfColumns?.ToString() ?? "—"));
                rows.Add(P(
                    "Interleave",
                    virtualDisk.Interleave is null
                        ? "—"
                        : TopologyProjector.FormatBytes(virtualDisk.Interleave.Value)));
                break;
            }
            case ManageObjectRole.Partition:
            case ManageObjectRole.Volume:
            {
                var partition = snapshot.Partitions.First(x => x.StableId == objectId.ProviderKey);
                title = TopologyProjector.PartitionDisplayName(partition);
                rows.Add(P("Type", partition.Type, ManageValuePresentation.PartitionType));
                rows.Add(P(
                    "FileSystem",
                    string.IsNullOrWhiteSpace(partition.FileSystem) ? "Unknown" : partition.FileSystem,
                    string.IsNullOrWhiteSpace(partition.FileSystem)
                        ? ManageValuePresentation.LocalizationKey
                        : ManageValuePresentation.Plain));
                rows.Add(P(
                    "AllocationUnit",
                    partition.AllocationUnitSize is null
                        ? "Unknown"
                        : TopologyProjector.FormatBytes(partition.AllocationUnitSize.Value),
                    partition.AllocationUnitSize is null
                        ? ManageValuePresentation.LocalizationKey
                        : ManageValuePresentation.Plain));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(partition.Size)));
                rows.Add(P("Available", TopologyProjector.FormatBytes(partition.SizeRemaining)));
                rows.Add(P("Health", TopologyProjector.JoinSummary(partition.HealthStatus, partition.OperationalStatus)));
                rows.Add(P("Path", string.IsNullOrWhiteSpace(partition.Path) ? "—" : partition.Path));
                break;
            }
            case ManageObjectRole.NetworkDisk:
            {
                var network = snapshot.NetworkDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("FileSystem", network.FileSystem));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(network.Size)));
                rows.Add(P("Available", TopologyProjector.FormatBytes(network.SizeRemaining)));
                rows.Add(P("Path", network.ProviderPath));
                break;
            }
            case ManageObjectRole.NetworkGroup:
                rows.Add(P("Type", "NetworkStorageGroup", ManageValuePresentation.LocalizationKey));
                rows.Add(P("NetworkDisk", snapshot.NetworkDisks.Count.ToString()));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size))));
                rows.Add(P("Available", TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.SizeRemaining))));
                break;
            case ManageObjectRole.OtherGroup:
            {
                var otherDisks = TopologyProjector.GetOtherOsDisks(snapshot);
                var otherIds = otherDisks.Select(x => x.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var otherPartitions = snapshot.Partitions
                    .Where(x => x.OsDiskStableId is not null && otherIds.Contains(x.OsDiskStableId))
                    .ToList();
                rows.Add(P("Type", "OtherStorageGroup", ManageValuePresentation.LocalizationKey));
                rows.Add(P("OtherDisk", otherDisks.Count.ToString()));
                rows.Add(P("Partition", otherPartitions.Count.ToString()));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(otherDisks.Sum(x => x.Size))));
                rows.Add(P("Available", TopologyProjector.FormatBytes(otherPartitions.Sum(x => x.SizeRemaining))));
                break;
            }
            case ManageObjectRole.OsDisk:
            {
                var osDisk = snapshot.OsDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("Type", osDisk.PartitionStyle));
                rows.Add(P("Capacity", TopologyProjector.FormatBytes(osDisk.Size)));
                break;
            }
        }

        rows.Add(P(
            "LastScan",
            snapshot.ScannedAt == DateTimeOffset.MinValue
                ? string.Empty
                : snapshot.ScannedAt.ToString("O"),
            ManageValuePresentation.LocalDateTime));
        return new ManageObjectDetailsView(objectId, role, title, rows);
    }

    private static ManagePropertyView P(
        string key,
        string value,
        ManageValuePresentation presentation = ManageValuePresentation.Plain) =>
        new(key, value, presentation);
}
