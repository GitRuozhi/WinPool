using System.Globalization;

namespace WinPool.Application;

/// <summary>Descriptive step text only. Placeholder CIM objects are never bound or executed.</summary>
public static class SimulationCommandPreview
{
    public static string Quote(string? value)
    {
        var escaped = value ?? string.Empty;
        foreach (var quote in new[] { '\'', '\u2018', '\u2019', '\u201a', '\u201b' })
            escaped = escaped.Replace(quote.ToString(), new string(quote, 2), StringComparison.Ordinal);
        return "'" + escaped + "'";
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private const string Unbound = "# Preview only. $target* and $memberDisks are unbound CIM object placeholders; this is not a standalone script.\n";

    public static IReadOnlyList<string> Build(SimulationEditRequest step, StorageSnapshot before, StorageSnapshot after)
    {
        var lines = new List<string>();
        switch (step.Kind)
        {
            case SimulationEditKind.Rename:
                var command = before.StoragePools.Any(x => x.StableId == step.TargetProviderKey) ? "Set-StoragePool -InputObject $targetPool -NewFriendlyName "
                    : before.StorageTiers.Any(x => x.StableId == step.TargetProviderKey) ? "Set-StorageTier -InputObject $targetTier -NewFriendlyName "
                    : before.PhysicalDisks.Any(x => x.StableId == step.TargetProviderKey) ? "Set-PhysicalDisk -InputObject $targetPhysicalDisk -NewFriendlyName "
                    : before.VirtualDisks.Any(x => x.StableId == step.TargetProviderKey) ? "Set-VirtualDisk -InputObject $targetVirtualDisk -NewFriendlyName "
                    : before.Partitions.Any(x => x.StableId == step.TargetProviderKey) || before.Volumes.Any(x => x.StableId == step.TargetProviderKey)
                        ? "Set-Volume -InputObject $targetVolume -NewFileSystemLabel " : null;
                lines.Add(command is null ? "# This simulated display name has no corresponding Storage cmdlet." : command + Quote(step.Name));
                break;
            case SimulationEditKind.ChangeDriveLetter:
                if (!string.IsNullOrWhiteSpace(step.DriveLetter))
                    lines.Add("Set-Partition -InputObject $targetPartition -NewDriveLetter " + Quote(TopologyProjector.NormalizeDriveLetter(step.DriveLetter)));
                else
                {
                    var letter = before.Partitions.FirstOrDefault(x => x.StableId == step.TargetProviderKey)?.DriveLetter;
                    lines.Add(string.IsNullOrWhiteSpace(letter) ? "# No existing drive-letter access path to remove."
                        : "Remove-PartitionAccessPath -InputObject $targetPartition -AccessPath " + Quote(TopologyProjector.NormalizeDriveLetter(letter) + ":\\"));
                }
                break;
            case SimulationEditKind.FormatPartition:
                var formatted = after.Volumes.FirstOrDefault(x => x.PartitionStableId == step.TargetProviderKey);
                lines.Add(formatted is null ? "# No volume was formatted."
                    : Format(step with { FileSystem = formatted.FileSystem, AllocationUnitSize = formatted.AllocationUnitSize, VolumeName = formatted.FileSystemLabel }, "$targetVolume")); break;
            case SimulationEditKind.DeletePartition:
                lines.Add("Remove-Partition -InputObject $targetPartition -Confirm:$false"); break;
            case SimulationEditKind.InitializeDisk:
            case SimulationEditKind.ConvertDisk:
                lines.Add("Clear-Disk -InputObject $targetDisk -RemoveData -Confirm:$false");
                lines.Add("Initialize-Disk -InputObject $targetDisk -PartitionStyle " + Quote(step.PartitionStyle?.Trim().ToUpperInvariant()));
                if (step.CreateMsr == true) lines.Add("New-Partition -InputObject $targetDisk -Offset 1048576 -Size 16777216 -GptType " + Quote(PartitionTypeId(PartitionKind.MicrosoftReserved)));
                break;
            case SimulationEditKind.SetDiskOffline:
                lines.Add("Set-Disk -InputObject $targetDisk -IsOffline $" + (step.Offline == true ? "true" : "false")); break;
            case SimulationEditKind.CreatePartition:
                foreach (var partition in after.Partitions.Where(x => !before.Partitions.Any(old => old.StableId == x.StableId)))
                    AddPartition(lines, partition, after, step.QuickFormat);
                break;
            case SimulationEditKind.ExtendPartition:
            case SimulationEditKind.ShrinkPartition:
                lines.Add("# Simulated geometry only. Target size is 1 MiB-aligned and not a Windows Get-PartitionSupportedSize result.");
                lines.Add("Resize-Partition -InputObject $targetPartition -Size " + Number(step.SizeBytes ?? 0));
                break;
            case SimulationEditKind.OptimizePool:
            case SimulationEditKind.OptimizeDrive:
                lines.Add("# Simulated no-op. No optimization command or performance improvement is claimed."); break;
            case SimulationEditKind.CreateStoragePool:
            case SimulationEditKind.CreateTieredPool:
                if (step.MemberDiskIds is not { Count: > 0 })
                {
                    lines.Add("# Empty planning pool only. New-StoragePool requires real member disks; no creation command is available.");
                    break;
                }
                lines.Add("$targetPool = New-StoragePool -InputObject $targetStorageSubsystem -FriendlyName " + Quote(step.Name) + " -PhysicalDisks $memberDisks");
                if (step.Kind == SimulationEditKind.CreateTieredPool)
                    foreach (var disk in after.VirtualDisks.Where(x => !before.VirtualDisks.Any(old => old.StableId == x.StableId))) AddVirtualDisk(lines, disk, after);
                break;
            case SimulationEditKind.CreateVirtualDisk:
                var created = after.VirtualDisks.FirstOrDefault(x => !before.VirtualDisks.Any(old => old.StableId == x.StableId));
                if (created is not null) AddVirtualDisk(lines, created, after);
                else lines.Add("# No new virtual disk was produced by this step.");
                break;
            case SimulationEditKind.MovePhysicalDisk:
                var sourcePool = before.PhysicalDisks.FirstOrDefault(x => x.StableId == step.TargetProviderKey)?.PoolStableId;
                if (before.StoragePools.Any(x => x.StableId == sourcePool && !x.IsPrimordial))
                    lines.Add("Remove-PhysicalDisk -InputObject $targetSourcePool -PhysicalDisks $targetPhysicalDisk -Confirm:$false");
                var destination = after.PhysicalDisks.FirstOrDefault(x => x.StableId == step.TargetProviderKey)?.PoolStableId;
                if (after.StoragePools.Any(x => x.StableId == destination && !x.IsPrimordial))
                    lines.Add("Add-PhysicalDisk -InputObject $targetDestinationPool -PhysicalDisks $targetPhysicalDisk");
                break;
            case SimulationEditKind.SetDiskUsage:
                lines.Add("Set-PhysicalDisk -InputObject $targetPhysicalDisk -Usage " + Quote(string.IsNullOrWhiteSpace(step.DiskUsage) ? "AutoSelect" : step.DiskUsage)); break;
            case SimulationEditKind.DeleteVirtualDisk:
                lines.Add("Remove-VirtualDisk -InputObject $targetVirtualDisk -Confirm:$false"); break;
            case SimulationEditKind.DeleteEmptyStoragePool:
                lines.Add("# Delete empty simulated planning pool. It has no bound Windows storage pool."); break;
            case SimulationEditKind.DissolveStoragePool:
                if (before.StoragePools.FirstOrDefault(x => x.StableId == step.TargetProviderKey)?.MemberPhysicalDiskIds.Count == 0)
                {
                    lines.Add("# Empty simulated planning pool. No bound Windows pool exists to remove."); break;
                }
                if (before.VirtualDisks.Any(x => x.PoolStableId == step.TargetProviderKey))
                    lines.Add("Remove-VirtualDisk -InputObject $targetPoolVirtualDisks -Confirm:$false");
                lines.Add("Remove-StoragePool -InputObject $targetPool -Confirm:$false"); break;
            case SimulationEditKind.EvictPhysicalDiskFromTiers:
                lines.Add("# Unallocated is a WinPool display group. Windows has no direct cmdlet to assign a physical disk to this group."); break;
            case SimulationEditKind.UpdateStoragePool:
                lines.Add("# Layout updates must be reviewed as member, tier and virtual-disk changes; there is no Set-StoragePool layout parameter.");
                var oldPool = before.StoragePools.FirstOrDefault(x => x.StableId == step.TargetProviderKey);
                var newPool = after.StoragePools.FirstOrDefault(x => x.StableId == step.TargetProviderKey);
                if (oldPool is not null && newPool is not null)
                {
                    if (oldPool.MemberPhysicalDiskIds.Except(newPool.MemberPhysicalDiskIds).Any())
                        lines.Add("Remove-PhysicalDisk -InputObject $targetPool -PhysicalDisks $targetRemovedDisks -Confirm:$false");
                    if (newPool.MemberPhysicalDiskIds.Except(oldPool.MemberPhysicalDiskIds).Any())
                        lines.Add("Add-PhysicalDisk -InputObject $targetPool -PhysicalDisks $targetAddedDisks");
                }
                var tierIndex = 0;
                foreach (var tier in after.StorageTiers.Where(x => x.PoolStableId == step.TargetProviderKey))
                {
                    tierIndex++;
                    var old = before.StorageTiers.FirstOrDefault(x => x.StableId == tier.StableId);
                    if (old is not null && tier.Size > old.Size && SameLayout(old, tier))
                        lines.Add("Resize-StorageTier -InputObject $targetTier" + Number(tierIndex) + " -Size " + Number(tier.Size));
                    else if (old is not null && !SameLayout(old, tier))
                        lines.Add("# This simulated layout change requires recreation on Windows; no in-place parameter mapping is available.");
                }
                foreach (var disk in after.VirtualDisks.Where(x => x.PoolStableId == step.TargetProviderKey))
                    if (before.VirtualDisks.FirstOrDefault(x => x.StableId == disk.StableId) is { } oldDisk && disk.Size > oldDisk.Size
                        && disk.ResiliencySettingName == oldDisk.ResiliencySettingName && disk.ProvisioningType == oldDisk.ProvisioningType
                        && disk.Interleave == oldDisk.Interleave && disk.NumberOfColumns == oldDisk.NumberOfColumns
                        && disk.NumberOfDataCopies == oldDisk.NumberOfDataCopies && disk.PhysicalDiskRedundancy == oldDisk.PhysicalDiskRedundancy
                        && disk.TierStableIds.Order().SequenceEqual(oldDisk.TierStableIds.Order())
                        && after.StorageTiers.Where(x => disk.TierStableIds.Contains(x.StableId)).All(tier =>
                            before.StorageTiers.FirstOrDefault(x => x.StableId == tier.StableId) is { } oldTier && SameLayout(oldTier, tier)))
                        lines.Add("Resize-VirtualDisk -InputObject $targetVirtualDisk -Size " + Number(disk.Size));
                break;
            default: lines.Add("# Internal simulation action; no corresponding Windows command."); break;
        }
        if (lines.Count == 0) lines.Add("# No Windows command is required for this step.");
        return [Unbound + string.Join("\n", lines)];
    }

    private static bool SameLayout(StorageTierInfo before, StorageTierInfo after) =>
        before.MediaType == after.MediaType && before.ResiliencySettingName == after.ResiliencySettingName
        && before.Interleave == after.Interleave && before.NumberOfColumns == after.NumberOfColumns
        && before.NumberOfDataCopies == after.NumberOfDataCopies && before.PhysicalDiskRedundancy == after.PhysicalDiskRedundancy;

    private static string Format(SimulationEditRequest step, string? target) => "Format-Volume"
        + (target is null ? "" : " -InputObject " + target)
        + " -FileSystem " + Quote(step.FileSystem ?? (step.PartitionKind == PartitionKind.EfiSystem ? "FAT32" : "NTFS"))
        + " -AllocationUnitSize " + Number(step.AllocationUnitSize ?? 4096)
        + (step.QuickFormat == false ? " -Full" : "")
        + (step.VolumeName is not null ? " -NewFileSystemLabel " + Quote(step.VolumeName) : "") + " -Confirm:$false";

    private static string Size(long? size) => size is > 0 ? "-Size " + Number(size.Value) : "-UseMaximumSize";

    private static void AddPartition(
        List<string> lines,
        PartitionInfo partition,
        StorageSnapshot candidate,
        bool? quickFormat)
    {
        var volume = candidate.Volumes.FirstOrDefault(x => x.PartitionStableId == partition.StableId);
        lines.Add("$newPartition = New-Partition -InputObject $targetDisk -Size " + Number(partition.Size)
            + " -Offset " + Number(partition.Offset) + " -GptType " + Quote(partition.PartitionTypeId)
            + (string.IsNullOrWhiteSpace(volume?.DriveLetter) ? "" : " -DriveLetter " + Quote(volume.DriveLetter)));
        if (volume is { FileSystem.Length: > 0 })
            lines.Add("$newPartition | " + Format(new(SimulationEditKind.FormatPartition, partition.StableId,
                FileSystem: volume.FileSystem, AllocationUnitSize: volume.AllocationUnitSize,
                VolumeName: volume.FileSystemLabel, QuickFormat: quickFormat), null));
    }

    private static void AddVirtualDisk(List<string> lines, VirtualDiskInfo disk, StorageSnapshot candidate)
    {
        var tiers = candidate.StorageTiers.Where(x => disk.TierStableIds.Contains(x.StableId)).ToArray();
        for (var i = 0; i < tiers.Length; i++)
        {
            var tier = tiers[i];
            lines.Add("$tier" + Number(i + 1) + " = New-StorageTier -InputObject $targetPool -FriendlyName " + Quote(tier.FriendlyName)
                + " -MediaType " + Quote(tier.MediaType) + " -ResiliencySettingName " + Quote(tier.ResiliencySettingName)
                + (tier.Interleave is { } interleave ? " -Interleave " + Number(interleave) : "")
                + (tier.NumberOfColumns is { } columns ? " -NumberOfColumns " + Number(columns) : "")
                + (tier.NumberOfDataCopies is { } copies ? " -NumberOfDataCopies " + Number(copies) : "")
                + (tier.PhysicalDiskRedundancy is { } redundancy ? " -PhysicalDiskRedundancy " + Number(redundancy) : ""));
        }
        lines.Add("$newVirtualDisk = New-VirtualDisk -InputObject $targetPool -FriendlyName " + Quote(disk.FriendlyName)
            + " -ProvisioningType " + Quote(disk.ProvisioningType)
            + (tiers.Length > 0
                ? " -StorageTiers @(" + string.Join(", ", Enumerable.Range(1, tiers.Length).Select(x => "$tier" + Number(x)))
                    + ") -StorageTierSizes @(" + string.Join(", ", tiers.Select(x => Number(x.Size))) + ")"
                : " -ResiliencySettingName " + Quote(disk.ResiliencySettingName) + " " + Size(disk.Size)
                    + (disk.Interleave is { } diskInterleave ? " -Interleave " + Number(diskInterleave) : "")));
        foreach (var osDisk in candidate.OsDisks.Where(x => x.VirtualDiskStableId == disk.StableId))
        {
            lines.Add("$targetDisk = $newVirtualDisk | Get-Disk");
            if (osDisk.PartitionStyle != "RAW") lines.Add("Initialize-Disk -InputObject $targetDisk -PartitionStyle " + Quote(osDisk.PartitionStyle));
            foreach (var partition in candidate.Partitions.Where(x => x.OsDiskStableId == osDisk.StableId).OrderBy(x => x.Offset))
                AddPartition(lines, partition, candidate, quickFormat: null);
        }
    }

    internal static string PartitionTypeId(PartitionKind kind) => kind switch
    {
        PartitionKind.EfiSystem => "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}",
        PartitionKind.MicrosoftReserved => "{e3c9e316-0b5c-4db8-817d-f92df00215ae}",
        PartitionKind.WindowsRecovery => "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}",
        _ => "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}"
    };
}
