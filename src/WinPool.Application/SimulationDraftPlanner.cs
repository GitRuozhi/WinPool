using WinPool.Domain;

namespace WinPool.Application;

public static class SimulationDraftPlanner
{
    public static SimulationDraftPlan Build(StorageSnapshot committed, StorageSnapshot working)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(working);
        var steps = new List<SimulationEditRequest>();
        var allocated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pool in committed.StoragePools.Where(item => !item.IsPrimordial))
        {
            if (!working.StoragePools.Any(item =>
                    item.StableId.Equals(pool.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(new SimulationEditRequest(SimulationEditKind.DissolveStoragePool, pool.StableId));
            }
        }

        foreach (var draft in working.StoragePools.Where(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            var poolId = NewId("sim:pool", allocated, draft.StableId);
            var vdisk = working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == draft.StableId);
            string? vdiskId = null;
            string? osDiskId = null;
            string? partitionId = null;
            string? volumeId = null;
            var createVirtual = vdisk is not null;
            if (vdisk is not null)
            {
                vdiskId = NewId("sim:vdisk", allocated, vdisk.StableId);
                var osDisk = working.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
                if (osDisk is not null)
                {
                    osDiskId = NewId("sim:osdisk", allocated, osDisk.StableId);
                    var nestedPartition = working.Partitions.FirstOrDefault(item =>
                        item.OsDiskStableId == osDisk.StableId && item.Type == "Primary");
                    if (nestedPartition is not null)
                    {
                        partitionId = NewId("sim:partition", allocated, nestedPartition.StableId);
                        var nestedVolume = working.VolumeForPartition(nestedPartition.StableId);
                        if (nestedVolume is not null)
                        {
                            volumeId = NewId("sim:volume", allocated, nestedVolume.StableId);
                        }
                    }
                }
            }

            var ssd = working.StorageTiers.FirstOrDefault(item =>
                item.PoolStableId == draft.StableId
                && EditWorkspace.NormalizeMedia(item.MediaType) == "SSD");
            var hdd = working.StorageTiers.FirstOrDefault(item =>
                item.PoolStableId == draft.StableId
                && EditWorkspace.NormalizeMedia(item.MediaType) == "HDD");
            var scm = working.StorageTiers.FirstOrDefault(item =>
                item.PoolStableId == draft.StableId
                && EditWorkspace.NormalizeMedia(item.MediaType) == "SCM");
            var partition = vdisk is null
                ? null
                : working.Partitions.FirstOrDefault(item =>
                    working.OsDisks.Any(os =>
                        os.VirtualDiskStableId == vdisk.StableId && os.StableId == item.OsDiskStableId)
                    && item.Type == "Primary");
            var volume = partition is null ? null : working.VolumeForPartition(partition.StableId);
            steps.Add(new SimulationEditRequest(
                SimulationEditKind.CreateTieredPool,
                "primordial",
                Name: draft.FriendlyName,
                MemberDiskIds: draft.MemberPhysicalDiskIds.ToArray(),
                VirtualDiskName: vdisk?.FriendlyName ?? draft.FriendlyName,
                FileSystem: volume?.FileSystem ?? partition?.FileSystem,
                AllocationUnitSize: volume?.AllocationUnitSize ?? partition?.AllocationUnitSize,
                PerformanceResiliency: ssd?.ResiliencySettingName,
                PerformanceInterleaveBytes: ssd?.Interleave,
                PerformanceDataCopies: ssd?.NumberOfDataCopies,
                CapacityResiliency: hdd?.ResiliencySettingName,
                CapacityInterleaveBytes: hdd?.Interleave,
                CapacityColumns: hdd?.NumberOfColumns,
                CapacityToleratedFailures: hdd?.PhysicalDiskRedundancy,
                ScmResiliency: scm?.ResiliencySettingName,
                ScmInterleaveBytes: scm?.Interleave,
                ScmDataCopies: scm?.NumberOfDataCopies,
                CreateVirtualDisk: createVirtual,
                CreatePartition: partition is not null,
                AllocatedPoolId: poolId,
                AllocatedVirtualDiskId: vdiskId,
                AllocatedOsDiskId: osDiskId,
                AllocatedPartitionId: partitionId,
                AllocatedVolumeId: volumeId));
        }

        foreach (var vdisk in committed.VirtualDisks)
        {
            if (working.VirtualDisks.Any(item =>
                    item.StableId.Equals(vdisk.StableId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(SimulationEditKind.DeleteVirtualDisk, vdisk.StableId));
        }

        foreach (var draftVdisk in working.VirtualDisks.Where(item =>
                     EditWorkspace.IsDraftVirtualDisk(item.StableId)
                     && !EditWorkspace.IsDraftPool(item.PoolStableId ?? string.Empty)))
        {
            var vdiskId = NewId("sim:vdisk", allocated, draftVdisk.StableId);
            steps.Add(new SimulationEditRequest(
                SimulationEditKind.CreateVirtualDisk,
                draftVdisk.PoolStableId ?? string.Empty,
                Name: draftVdisk.FriendlyName,
                Resiliency: draftVdisk.ResiliencySettingName,
                InterleaveBytes: draftVdisk.Interleave,
                SizeBytes: draftVdisk.Size,
                AllocatedVirtualDiskId: vdiskId));
        }

        foreach (var disk in working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(
                SimulationEditKind.MovePhysicalDisk,
                disk.StableId,
                Name: disk.PoolStableId ?? string.Empty));
        }

        foreach (var disk in working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)
                || disk.IsRetired
                || disk.IsHotSpare
                || EditWorkspace.DiskIsAssignedToTier(working, disk.StableId)
                || !EditWorkspace.DiskIsAssignedToTier(committed, disk.StableId))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(
                SimulationEditKind.EvictPhysicalDiskFromTiers,
                disk.StableId));
        }

        foreach (var disk in working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)
                || disk.IsRetired
                || disk.IsHotSpare
                || string.IsNullOrEmpty(disk.PoolStableId)
                || !EditWorkspace.DiskNeedsSamePoolTierAssignment(working, committed, disk.StableId))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(
                SimulationEditKind.MovePhysicalDisk,
                disk.StableId,
                Name: disk.PoolStableId));
        }

        foreach (var disk in working.PhysicalDisks)
        {
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty))
            {
                continue;
            }

            var original = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId);
            if (original is null
                || !string.Equals(original.PoolStableId, disk.PoolStableId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var usage = EditWorkspace.DiskUsage(disk);
            if (usage == EditWorkspace.DiskUsage(original))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(
                SimulationEditKind.SetDiskUsage,
                disk.StableId,
                Name: usage));
        }

        foreach (var pool in working.StoragePools.Where(item =>
                     !item.IsPrimordial && !EditWorkspace.IsDraftPool(item.StableId)))
        {
            if (EditWorkspace.HasPoolPropertyChanges(working, committed, pool.StableId))
            {
                var vdisk = working.VirtualDisks.FirstOrDefault(item => item.PoolStableId == pool.StableId);
                var ssd = working.StorageTiers.FirstOrDefault(item =>
                    item.PoolStableId == pool.StableId
                    && EditWorkspace.NormalizeMedia(item.MediaType) == "SSD");
                var hdd = working.StorageTiers.FirstOrDefault(item =>
                    item.PoolStableId == pool.StableId
                    && EditWorkspace.NormalizeMedia(item.MediaType) == "HDD");
                var scm = working.StorageTiers.FirstOrDefault(item =>
                    item.PoolStableId == pool.StableId
                    && EditWorkspace.NormalizeMedia(item.MediaType) == "SCM");
                steps.Add(new SimulationEditRequest(
                    SimulationEditKind.UpdateStoragePool,
                    pool.StableId,
                    Name: pool.FriendlyName,
                    VirtualDiskName: vdisk?.FriendlyName,
                    PerformanceResiliency: ssd?.ResiliencySettingName,
                    PerformanceInterleaveBytes: ssd?.Interleave,
                    PerformanceSizeBytes: ssd?.Size,
                    PerformanceDataCopies: ssd?.NumberOfDataCopies,
                    CapacityResiliency: hdd?.ResiliencySettingName,
                    CapacityInterleaveBytes: hdd?.Interleave,
                    CapacitySizeBytes: hdd?.Size,
                    CapacityColumns: hdd?.NumberOfColumns,
                    CapacityToleratedFailures: hdd?.PhysicalDiskRedundancy,
                    ScmResiliency: scm?.ResiliencySettingName,
                    ScmInterleaveBytes: scm?.Interleave,
                    ScmDataCopies: scm?.NumberOfDataCopies));
            }
        }

        AppendPartitionAndVolumeSteps(committed, working, steps);
        return new SimulationDraftPlan(Guid.NewGuid().ToString("N"), steps);
    }

    private static void AppendPartitionAndVolumeSteps(
        StorageSnapshot committed,
        StorageSnapshot working,
        List<SimulationEditRequest> steps)
    {
        foreach (var partition in committed.Partitions)
        {
            if (working.Partitions.Any(item => item.StableId == partition.StableId))
            {
                continue;
            }

            steps.Add(new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId));
        }

        foreach (var partition in working.Partitions)
        {
            if (committed.Partitions.Any(item => item.StableId == partition.StableId)
                || EditWorkspace.IsDraftPool(
                    working.OsDisks.FirstOrDefault(item => item.StableId == partition.OsDiskStableId)
                        ?.VirtualDiskStableId is string vdiskId
                        ? working.VirtualDisks.FirstOrDefault(item => item.StableId == vdiskId)?.PoolStableId
                        : null)
                || string.IsNullOrEmpty(partition.OsDiskStableId))
            {
                continue;
            }

            var volume = working.VolumeForPartition(partition.StableId);
            steps.Add(new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                partition.OsDiskStableId,
                Name: volume?.FileSystemLabel ?? partition.FileSystemLabel,
                DriveLetter: working.DriveLetterOf(partition),
                FileSystem: working.FileSystemOf(partition),
                AllocationUnitSize: working.AllocationUnitOf(partition),
                SizeBytes: partition.Size,
                OffsetBytes: partition.Offset,
                AllocatedPartitionId: partition.StableId.StartsWith("edit:", StringComparison.Ordinal)
                    ? $"sim:partition:{Guid.NewGuid():N}"
                    : partition.StableId,
                AllocatedVolumeId: volume?.StableId,
                AccessPaths: volume?.AccessPaths));
        }

        foreach (var partition in working.Partitions)
        {
            var committedPartition = committed.Partitions.FirstOrDefault(item =>
                item.StableId == partition.StableId);
            if (committedPartition is null)
            {
                continue;
            }

            var workingVolume = working.VolumeForPartition(partition.StableId);
            var committedVolume = committed.VolumeForPartition(partition.StableId);
            var workingLetter = working.DriveLetterOf(partition);
            var committedLetter = committed.DriveLetterOf(committedPartition);
            if (!string.Equals(workingLetter, committedLetter, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(workingLetter))
            {
                steps.Add(new SimulationEditRequest(
                    SimulationEditKind.ChangeDriveLetter,
                    partition.StableId,
                    DriveLetter: workingLetter));
            }

            var workingFs = working.FileSystemOf(partition);
            var committedFs = committed.FileSystemOf(committedPartition);
            var workingLabel = working.FileSystemLabelOf(partition);
            var committedLabel = committed.FileSystemLabelOf(committedPartition);
            var workingCluster = working.AllocationUnitOf(partition);
            var committedCluster = committed.AllocationUnitOf(committedPartition);
            if (!string.Equals(workingFs, committedFs, StringComparison.OrdinalIgnoreCase)
                || !Equals(workingCluster, committedCluster)
                || (!string.Equals(workingLabel, committedLabel, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(workingFs)))
            {
                steps.Add(new SimulationEditRequest(
                    SimulationEditKind.FormatPartition,
                    partition.StableId,
                    Name: workingLabel,
                    FileSystem: string.IsNullOrWhiteSpace(workingFs) ? "NTFS" : workingFs,
                    AllocationUnitSize: workingCluster,
                    AccessPaths: workingVolume?.AccessPaths ?? committedVolume?.AccessPaths));
            }
            else if (!string.Equals(workingLabel, committedLabel, StringComparison.Ordinal)
                     && workingVolume is not null)
            {
                steps.Add(new SimulationEditRequest(
                    SimulationEditKind.Rename,
                    workingVolume.StableId,
                    Name: workingLabel));
            }
        }
    }

    private static string NewId(string prefix, Dictionary<string, string> allocated, string draftId)
    {
        if (allocated.TryGetValue(draftId, out var existing))
        {
            return existing;
        }

        var created = $"{prefix}:{Guid.NewGuid():N}";
        allocated[draftId] = created;
        return created;
    }

    public static SimulationOperationRequest ToOperation(SimulationEditRequest request) =>
        new(
            Enum.Parse<SimulationOperationKind>(request.Kind.ToString()),
            request.TargetProviderKey,
            request.Name,
            request.DriveLetter,
            request.FileSystem,
            request.AllocationUnitSize,
            request.Offline,
            request.SizeBytes,
            request.CreateMsr,
            request.InterleaveBytes,
            request.Resiliency,
            request.MemberDiskIds,
            request.VirtualDiskName,
            request.PerformanceResiliency,
            request.PerformanceInterleaveBytes,
            request.PerformanceSizeBytes,
            request.PerformanceDataCopies,
            request.CapacityResiliency,
            request.CapacityInterleaveBytes,
            request.CapacitySizeBytes,
            request.CapacityColumns,
            request.CapacityToleratedFailures,
            request.ScmResiliency,
            request.ScmInterleaveBytes,
            request.ScmDataCopies,
            request.OffsetBytes,
            request.CreatePartition,
            request.CreateVirtualDisk,
            request.AllocatedPoolId,
            request.AllocatedVirtualDiskId,
            request.AllocatedOsDiskId,
            request.AllocatedPartitionId,
            request.AllocatedVolumeId,
            request.AccessPaths);
}
