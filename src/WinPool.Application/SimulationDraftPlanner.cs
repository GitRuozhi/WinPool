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
        var dissolvedPoolIds = committed.StoragePools
            .Where(item => !item.IsPrimordial
                && !working.StoragePools.Any(candidate => candidate.StableId.Equals(item.StableId, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dissolvedVdiskIds = committed.VirtualDisks
            .Where(item => item.PoolStableId is not null && dissolvedPoolIds.Contains(item.PoolStableId))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dissolvedOsDiskIds = committed.OsDisks
            .Where(item => item.VirtualDiskStableId is not null && dissolvedVdiskIds.Contains(item.VirtualDiskStableId))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dissolvedPartitionIds = committed.Partitions
            .Where(item => item.OsDiskStableId is not null && dissolvedOsDiskIds.Contains(item.OsDiskStableId))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primordialId = committed.StoragePools.FirstOrDefault(item => item.IsPrimordial)?.StableId ?? string.Empty;

        foreach (var pool in committed.StoragePools.Where(item => dissolvedPoolIds.Contains(item.StableId)))
        {
            foreach (var partition in committed.Partitions.Where(item => dissolvedPartitionIds.Contains(item.StableId)))
            {
                var osDisk = committed.OsDisks.FirstOrDefault(item => item.StableId == partition.OsDiskStableId);
                if (osDisk?.VirtualDiskStableId is not null && dissolvedVdiskIds.Contains(osDisk.VirtualDiskStableId)
                    && committed.VirtualDisks.Any(item => item.StableId == osDisk.VirtualDiskStableId && item.PoolStableId == pool.StableId))
                {
                    steps.Add(new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId));
                }
            }

            foreach (var vdisk in committed.VirtualDisks.Where(item => item.PoolStableId == pool.StableId))
            {
                steps.Add(new SimulationEditRequest(SimulationEditKind.DeleteVirtualDisk, vdisk.StableId));
            }

            foreach (var diskId in pool.MemberPhysicalDiskIds)
            {
                steps.Add(new SimulationEditRequest(SimulationEditKind.MovePhysicalDisk, diskId, Name: primordialId));
            }

            steps.Add(new SimulationEditRequest(SimulationEditKind.DeleteEmptyStoragePool, pool.StableId));
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
                PerformanceSizeBytes: ssd?.Size,
                PerformanceDataCopies: ssd?.NumberOfDataCopies,
                CapacityResiliency: hdd?.ResiliencySettingName,
                CapacityInterleaveBytes: hdd?.Interleave,
                CapacityColumns: hdd?.NumberOfColumns,
                CapacityToleratedFailures: hdd?.PhysicalDiskRedundancy,
                ScmResiliency: scm?.ResiliencySettingName,
                ScmInterleaveBytes: scm?.Interleave,
                ScmSizeBytes: scm?.Size,
                ScmDataCopies: scm?.NumberOfDataCopies,
                SizeBytes: vdisk?.Size,
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
            if (dissolvedVdiskIds.Contains(vdisk.StableId))
            {
                continue;
            }

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
                || original.PoolStableId is not null && dissolvedPoolIds.Contains(original.PoolStableId)
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
            var originalPool = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId)?.PoolStableId;
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)
                || originalPool is not null && dissolvedPoolIds.Contains(originalPool)
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
            var originalPool = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.StableId)?.PoolStableId;
            if (EditWorkspace.IsDraftPool(disk.PoolStableId ?? string.Empty)
                || originalPool is not null && dissolvedPoolIds.Contains(originalPool)
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
                || original.PoolStableId is not null && dissolvedPoolIds.Contains(original.PoolStableId)
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
                    ScmSizeBytes: scm?.Size,
                    ScmDataCopies: scm?.NumberOfDataCopies));
            }
        }

        AppendPartitionAndVolumeSteps(committed, working, steps, dissolvedPartitionIds);
        return BuildPlanWithItems(committed, steps, dissolvedPoolIds);
    }

    public static SimulationDraftPlan Precheck(
        StorageSnapshot committed,
        IReadOnlyList<SimulationEditRequest> steps)
    {
        var dissolvedPoolIds = steps
            .Where(item => item.Kind == SimulationEditKind.DeleteEmptyStoragePool)
            .Select(item => item.TargetProviderKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuildPlanWithItems(committed, steps, dissolvedPoolIds);
    }

    private static SimulationDraftPlan BuildPlanWithItems(
        StorageSnapshot committed,
        IReadOnlyList<SimulationEditRequest> steps,
        IReadOnlySet<string> dissolvedPoolIds)
    {
        var document = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:plan-preview",
            StorageSystemKind.Simulation,
            committed.Computer.Name,
            committed,
            HardwareInventoryReport.Empty(DateTimeOffset.UtcNow),
            [],
            DateTimeOffset.UtcNow);
        var service = new SimulationOperationService();
        var childItems = new List<SimulationPlanItem>();
        var index = 0;
        foreach (var step in steps)
        {
            var operation = ToOperation(step);
            var decision = StorageEditRules.Evaluate(document.Snapshot, operation);
            if (decision.Verdict == StorageRuleVerdict.Allow)
            {
                var applied = service.Apply(document, operation);
                if (applied.Succeeded)
                {
                    document = applied.Document;
                }
                else
                {
                    decision = new StorageRuleDecision(
                        StorageRuleVerdict.Deny,
                        "storage.rule.plan-apply",
                        applied.Error,
                        step.TargetProviderKey);
                }
            }

            var parentPool = ParentDissolvedPool(committed, step, dissolvedPoolIds);
            childItems.Add(new SimulationPlanItem(
                $"step:{index++}",
                ActionTitle(step),
                step,
                parentPool is null ? null : $"dissolve:{parentPool}",
                decision,
                step.Kind is SimulationEditKind.DeletePartition or SimulationEditKind.DeleteVirtualDisk));
        }

        var items = new List<SimulationPlanItem>();
        foreach (var item in childItems)
        {
            if (item.ParentId is not null && items.All(existing => existing.Id != item.ParentId))
            {
                var poolId = item.ParentId["dissolve:".Length..];
                var children = childItems.Where(candidate => candidate.ParentId == item.ParentId).ToArray();
                var blocked = children.FirstOrDefault(candidate => candidate.Decision?.Verdict != StorageRuleVerdict.Allow);
                var parentDecision = blocked?.Decision
                    ?? new StorageRuleDecision(StorageRuleVerdict.Allow, "storage.rule.dissolve-plan", string.Empty, poolId);
                items.Add(new SimulationPlanItem(
                    item.ParentId,
                    $"Dissolve pool {committed.StoragePools.First(pool => pool.StableId == poolId).FriendlyName}",
                    new SimulationEditRequest(SimulationEditKind.DissolveStoragePool, poolId),
                    Decision: parentDecision,
                    CausesDataLoss: children.Any(candidate => candidate.CausesDataLoss)));
            }

            items.Add(item);
        }

        return new SimulationDraftPlan(Guid.NewGuid().ToString("N"), steps, items);
    }

    private static string? ParentDissolvedPool(
        StorageSnapshot snapshot,
        SimulationEditRequest step,
        IReadOnlySet<string> dissolvedPoolIds)
    {
        if (step.Kind == SimulationEditKind.DeleteEmptyStoragePool
            && dissolvedPoolIds.Contains(step.TargetProviderKey))
        {
            return step.TargetProviderKey;
        }

        var vdisk = snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == step.TargetProviderKey);
        if (vdisk?.PoolStableId is not null && dissolvedPoolIds.Contains(vdisk.PoolStableId))
        {
            return vdisk.PoolStableId;
        }

        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == step.TargetProviderKey);
        var osDisk = partition is null ? null : snapshot.OsDisks.FirstOrDefault(item => item.StableId == partition.OsDiskStableId);
        var parentVdisk = osDisk?.VirtualDiskStableId is null
            ? null
            : snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == osDisk.VirtualDiskStableId);
        if (parentVdisk?.PoolStableId is not null && dissolvedPoolIds.Contains(parentVdisk.PoolStableId))
        {
            return parentVdisk.PoolStableId;
        }

        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == step.TargetProviderKey);
        return disk?.PoolStableId is not null && dissolvedPoolIds.Contains(disk.PoolStableId)
            ? disk.PoolStableId
            : null;
    }

    private static string ActionTitle(SimulationEditRequest request) => request.Kind switch
    {
        SimulationEditKind.DeletePartition => $"Delete partition {request.TargetProviderKey}",
        SimulationEditKind.DeleteVirtualDisk => $"Delete virtual disk {request.TargetProviderKey}",
        SimulationEditKind.MovePhysicalDisk => $"Move physical disk {request.TargetProviderKey}",
        SimulationEditKind.DeleteEmptyStoragePool => $"Remove empty pool {request.TargetProviderKey}",
        SimulationEditKind.CreateTieredPool => $"Create storage pool {request.Name}",
        SimulationEditKind.CreateVirtualDisk => $"Create virtual disk {request.Name}",
        SimulationEditKind.UpdateStoragePool => $"Update storage pool {request.Name}",
        _ => $"{request.Kind}: {request.TargetProviderKey}"
    };

    private static void AppendPartitionAndVolumeSteps(
        StorageSnapshot committed,
        StorageSnapshot working,
        List<SimulationEditRequest> steps,
        IReadOnlySet<string> dissolvedPartitionIds)
    {
        foreach (var partition in committed.Partitions)
        {
            if (dissolvedPartitionIds.Contains(partition.StableId))
            {
                continue;
            }
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
            request.ScmSizeBytes,
            request.ScmDataCopies,
            request.PerformanceUseMaximum,
            request.CapacityUseMaximum,
            request.ScmUseMaximum,
            request.ProvisioningType,
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
