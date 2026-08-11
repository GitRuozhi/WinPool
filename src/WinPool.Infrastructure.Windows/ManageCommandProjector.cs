using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Transitional command-surface projection for the accepted V0.13 document.
/// It exposes command availability and already-resolved local dialog targets;
/// the App retains only labels, icons, dialogs, and process launching.
/// </summary>
public sealed class ManageCommandProjector
    : IManageCommandProjector<StorageSystemDocument>
{
    public ManageCommandSurfaceView Project(
        StorageSystemDocument activeDocument,
        StorageSystemDocument localDocument,
        StorageObjectId objectId,
        ManageObjectRole role,
        ManageWorkspaceCategory category)
    {
        ArgumentNullException.ThrowIfNull(activeDocument);
        ArgumentNullException.ThrowIfNull(localDocument);
        if (!localDocument.IsLocal)
        {
            throw new ArgumentException("The local document must represent the local system.", nameof(localDocument));
        }
        if (objectId.System != activeDocument.SystemId)
        {
            throw new ArgumentException(
                "The command object does not belong to the active document.",
                nameof(objectId));
        }

        var isSimulation = !activeDocument.IsLocal;
        var localConsistent = activeDocument.IsLocal
            || activeDocument.SourceHostName?.Equals(
                Environment.MachineName,
                StringComparison.OrdinalIgnoreCase) == true;
        var commands = new List<ManageCommandView>();
        switch (category)
        {
            case ManageWorkspaceCategory.System:
                Add(commands, ManageCommandKind.RefreshLocal, activeDocument.IsLocal);
                Add(commands, ManageCommandKind.ConvertLocalToSimulation, activeDocument.IsLocal);
                Add(commands, ManageCommandKind.ImportSimulation, true);
                Add(commands, ManageCommandKind.ExportSimulation, true);
                Add(
                    commands,
                    ManageCommandKind.DeleteSimulation,
                    isSimulation
                    && !activeDocument.Id.StartsWith("simulation:builtin", StringComparison.Ordinal));
                break;
            case ManageWorkspaceCategory.Pool:
            {
                var pool = activeDocument.Snapshot.StoragePools.FirstOrDefault(
                    item => item.StableId == objectId.ProviderKey);
                var editable = isSimulation && pool is { IsPrimordial: false };
                Add(commands, ManageCommandKind.RenamePool, editable);
                Add(commands, ManageCommandKind.CreatePool, isSimulation && pool is not null);
                Add(commands, ManageCommandKind.EditPool, editable);
                Add(commands, ManageCommandKind.OptimizePoolUsage, editable);
                break;
            }
            case ManageWorkspaceCategory.Tier:
                Add(commands, ManageCommandKind.RenameTier, isSimulation);
                Add(commands, ManageCommandKind.CreateTier, isSimulation);
                Add(commands, ManageCommandKind.EditTier, isSimulation);
                break;
            case ManageWorkspaceCategory.Disk:
            {
                var osDisk = ResolveOsDisk(activeDocument.Snapshot, objectId.ProviderKey, role);
                var physical = osDisk?.PhysicalDiskStableId is string physicalId
                    ? activeDocument.Snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == physicalId)
                    : null;
                var canTakeOffline = osDisk is { IsOffline: true }
                    || osDisk is { IsBoot: false, IsSystem: false }
                       && physical is not { IsPageFile: true } and not { IsCrashDump: true };
                var hasPartitions = osDisk is not null
                    && activeDocument.Snapshot.Partitions.Any(x => x.OsDiskStableId == osDisk.StableId);
                Add(commands, ManageCommandKind.RenameDisk, isSimulation && role != ManageObjectRole.NetworkDisk);
                Add(commands, ManageCommandKind.InitializeDisk, isSimulation && osDisk is not null);
                Add(commands, ManageCommandKind.CreatePartition, isSimulation && osDisk is not null);
                Add(commands, ManageCommandKind.ConvertDiskStyle, isSimulation && osDisk is not null && !hasPartitions);
                Add(
                    commands,
                    osDisk is { IsOffline: true }
                        ? ManageCommandKind.OnlineDisk
                        : ManageCommandKind.OfflineDisk,
                    isSimulation && osDisk is not null && canTakeOffline);
                Add(commands, ManageCommandKind.ShowSystemProperties, localConsistent);
                break;
            }
            case ManageWorkspaceCategory.Partition:
            {
                var partition = activeDocument.Snapshot.Partitions.FirstOrDefault(
                    item => item.StableId == objectId.ProviderKey);
                var primary = partition?.Type == "Primary";
                var editable = isSimulation && primary;
                Add(commands, ManageCommandKind.OpenExplorer, primary && localConsistent);
                Add(commands, ManageCommandKind.ChangeDriveLetter, editable);
                Add(commands, ManageCommandKind.RenamePartition, editable);
                Add(commands, ManageCommandKind.FormatPartition, editable);
                Add(commands, ManageCommandKind.EditPartition, editable);
                Add(commands, ManageCommandKind.DeletePartition, editable);
                Add(commands, ManageCommandKind.OptimizeDrive, primary && localConsistent);
                Add(
                    commands,
                    ManageCommandKind.ShowSystemProperties,
                    primary && localConsistent);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }
        Add(commands, ManageCommandKind.ExportCategory, true);

        return new ManageCommandSurfaceView(
            objectId,
            commands,
            ResolveSystemDialogTarget(
                activeDocument,
                localDocument,
                objectId.ProviderKey,
                role));
    }

    private static ManageSystemDialogTargetView ResolveSystemDialogTarget(
        StorageSystemDocument activeDocument,
        StorageSystemDocument localDocument,
        string providerKey,
        ManageObjectRole role)
    {
        var activeSnapshot = activeDocument.Snapshot;
        var localSnapshot = localDocument.Snapshot;
        var activePartition = role == ManageObjectRole.Partition
            ? activeSnapshot.Partitions.FirstOrDefault(x => x.StableId == providerKey)
            : null;
        var localPartition = activePartition is null
            ? null
            : activeDocument.IsLocal
                ? activePartition
                : localSnapshot.Partitions.FirstOrDefault(
                    x => x.DiskNumber == activePartition.DiskNumber
                        && x.PartitionNumber == activePartition.PartitionNumber);

        PhysicalDiskInfo? localPhysical = null;
        OsDiskInfo? localOsDisk = null;
        var hasResolvedDisk = false;
        if (activeDocument.IsLocal && role == ManageObjectRole.PhysicalDisk)
        {
            localPhysical = activeSnapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == providerKey);
            hasResolvedDisk = localPhysical is not null;
        }
        else
        {
            var activeOsDisk = ResolveOsDisk(activeSnapshot, providerKey, role);
            localOsDisk = activeOsDisk is null
                ? null
                : activeDocument.IsLocal
                    ? activeOsDisk
                    : localSnapshot.OsDisks.FirstOrDefault(x => x.Number == activeOsDisk.Number);
            hasResolvedDisk = localOsDisk is not null;
            localPhysical = localOsDisk?.PhysicalDiskStableId is string physicalId
                ? localSnapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == physicalId)
                : null;
        }

        return new ManageSystemDialogTargetView(
            localPartition is not null,
            hasResolvedDisk,
            localPartition?.Path ?? string.Empty,
            TopologyProjector.NormalizeDriveLetter(localPartition?.DriveLetter),
            localPhysical?.PnpDeviceId ?? string.Empty,
            role is ManageObjectRole.PhysicalDisk or ManageObjectRole.VirtualDisk or ManageObjectRole.OsDisk,
            role == ManageObjectRole.PhysicalDisk
                ? localPhysical?.DeviceId
                : localOsDisk?.Number);
    }

    private static OsDiskInfo? ResolveOsDisk(
        StorageSnapshot snapshot,
        string providerKey,
        ManageObjectRole role) =>
        role switch
        {
            ManageObjectRole.OsDisk =>
                snapshot.OsDisks.FirstOrDefault(x => x.StableId == providerKey),
            ManageObjectRole.PhysicalDisk =>
                snapshot.OsDisks.FirstOrDefault(x => x.PhysicalDiskStableId == providerKey),
            ManageObjectRole.VirtualDisk =>
                snapshot.OsDisks.FirstOrDefault(x => x.VirtualDiskStableId == providerKey),
            _ => null
        };

    private static void Add(
        ICollection<ManageCommandView> commands,
        ManageCommandKind kind,
        bool enabled) =>
        commands.Add(new ManageCommandView(kind, enabled));
}
