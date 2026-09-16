using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional details projection for the accepted V0.13 snapshot model.
/// It preserves the frozen row order while keeping localization and display
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
        if (role is ManageObjectRole.Partition or ManageObjectRole.Volume or ManageObjectRole.NetworkDisk
            && snapshot.ResolvePartitionUnion(objectId.ProviderKey) is { } union)
            return new ManageObjectDetailsView(objectId, ManageObjectRole.Partition, union.DisplayName,
                ManagePartitionProjector.Properties(snapshot, union));
        var synthetic = snapshot.FindSyntheticStorageObject(objectId.ProviderKey);
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
                rows.Add(P("Partition", snapshot.PartitionUnions.Count.ToString()));
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
                rows.Add(P("Capacity", TopologyProjector.TierCapacityText(snapshot, tier)));
                rows.Add(P("Members", tier.MemberPhysicalDiskIds.Count.ToString()));
                break;
            }
            case ManageObjectRole.SyntheticStoragePool when synthetic is { Kind: SyntheticStorageObjectKind.Pool }:
                rows.Add(P("Type", "SyntheticStoragePool", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Health", string.Empty));
                rows.Add(P("Capacity", string.Empty));
                rows.Add(P("Allocated", string.Empty));
                rows.Add(P("Available", string.Empty));
                rows.Add(P("Members", synthetic.MemberStableIds.Count.ToString()));
                rows.Add(P("ProvisioningType", string.Empty));
                rows.Add(P("Resiliency", string.Empty));
                rows.Add(P("FaultTolerance", string.Empty));
                rows.Add(P("Columns", string.Empty));
                rows.Add(P("Interleave", string.Empty));
                break;
            case ManageObjectRole.SyntheticStorageTier when synthetic is { Kind: SyntheticStorageObjectKind.Tier }:
                rows.Add(P(
                    "PoolOwner",
                    snapshot.StoragePools.FirstOrDefault(pool => pool.StableId == synthetic.ParentStableId)?.FriendlyName
                    ?? string.Empty));
                rows.Add(P("Media", string.Empty));
                rows.Add(P("Type", "SyntheticStorageTier", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", string.Empty));
                rows.Add(P("ProvisioningType", string.Empty));
                rows.Add(P("Resiliency", string.Empty));
                rows.Add(P("FaultTolerance", string.Empty));
                rows.Add(P("Members", synthetic.MemberStableIds.Count.ToString()));
                rows.Add(P("Columns", string.Empty));
                rows.Add(P("Interleave", string.Empty));
                rows.Add(P("AllocationUnit", string.Empty));
                rows.Add(P(
                    "Membership",
                    synthetic.UnknownMemberStableIds.Count == 0 ? string.Empty : "MembershipUnknown",
                    synthetic.UnknownMemberStableIds.Count == 0
                        ? ManageValuePresentation.Plain
                        : ManageValuePresentation.LocalizationKey));
                break;
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
                var direct = snapshot.DirectPoolMembers(pool.StableId);
                rows.Add(P("Type", "SyntheticStorageTier", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Capacity", string.Empty));
                rows.Add(P("Members", direct.Count.ToString()));
                rows.Add(P("Health", string.Empty));
                break;
            }
            case ManageObjectRole.PhysicalDisk:
            {
                var disk = snapshot.PhysicalDisks.First(x => x.StableId == objectId.ProviderKey);
                rows.Add(P("Model", disk.Model));
                rows.Add(P("Serial", disk.SerialNumber, ManageValuePresentation.SerialNumber));
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
                rows.Add(P("Columns", virtualDisk.NumberOfColumns?.ToString() ?? string.Empty));
                rows.Add(P(
                    "Interleave",
                    virtualDisk.Interleave is null
                        ? string.Empty
                        : TopologyProjector.FormatBytes(virtualDisk.Interleave.Value)));
                break;
            }
            case ManageObjectRole.NetworkGroup:
                rows.Add(P("Type", "SyntheticStoragePool", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Members", snapshot.NetworkDisks.Count.ToString()));
                rows.Add(P("Capacity", string.Empty));
                rows.Add(P("Available", string.Empty));
                break;
            case ManageObjectRole.OtherGroup:
            {
                var otherDisks = TopologyProjector.GetOtherOsDisks(snapshot);
                rows.Add(P("Type", "SyntheticStoragePool", ManageValuePresentation.LocalizationKey));
                rows.Add(P("Members", otherDisks.Count.ToString()));
                rows.Add(P("Capacity", string.Empty));
                rows.Add(P("Available", string.Empty));
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
        return new ManageObjectDetailsView(objectId, role, title, WinPoolHardwarePresentation.MarkUnavailable(snapshot, objectId.ProviderKey, rows));
    }

    private static ManagePropertyView P(
        string key,
        string value,
        ManageValuePresentation presentation = ManageValuePresentation.Plain) =>
        new(key, value, presentation);
}
