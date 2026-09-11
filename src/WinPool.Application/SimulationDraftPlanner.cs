using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var draftPoolIds = working.StoragePools
            .Where(item => EditWorkspace.IsDraftPool(item.StableId))
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var draftMembersMovedToPrimordial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                var finalTarget = working.PhysicalDisks.FirstOrDefault(item =>
                    item.StableId.Equals(diskId, StringComparison.OrdinalIgnoreCase))?.PoolStableId;
                if (string.IsNullOrWhiteSpace(finalTarget)
                    || finalTarget.Equals(pool.StableId, StringComparison.OrdinalIgnoreCase))
                {
                    finalTarget = primordialId;
                }

                // A draft pool does not exist yet. Return the disk to the
                // primordial pool first; the later create step consumes it
                // as a member of the new pool.
                if (draftPoolIds.Contains(finalTarget))
                {
                    finalTarget = primordialId;
                    draftMembersMovedToPrimordial.Add(diskId);
                }

                steps.Add(new SimulationEditRequest(SimulationEditKind.MovePhysicalDisk, diskId, Name: finalTarget));
            }

            steps.Add(new SimulationEditRequest(SimulationEditKind.DeleteEmptyStoragePool, pool.StableId));
        }

        foreach (var draft in working.StoragePools.Where(item => EditWorkspace.IsDraftPool(item.StableId)))
        {
            // Members dragged from an existing pool are still associated
            // with that pool in the committed snapshot used by precheck.
            // Materialize the exit before validating creation so the create
            // operation sees the same primordial membership that it will
            // consume during execution.
            foreach (var memberId in draft.MemberPhysicalDiskIds)
            {
                var original = committed.PhysicalDisks.FirstOrDefault(item =>
                    item.StableId.Equals(memberId, StringComparison.OrdinalIgnoreCase));
                if (original is null
                    || string.IsNullOrWhiteSpace(original.PoolStableId)
                    || original.PoolStableId.Equals(primordialId, StringComparison.OrdinalIgnoreCase)
                    || draftMembersMovedToPrimordial.Contains(memberId))
                {
                    continue;
                }

                steps.Add(new SimulationEditRequest(
                    SimulationEditKind.MovePhysicalDisk,
                    memberId,
                    Name: primordialId));
                draftMembersMovedToPrimordial.Add(memberId);
            }

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
                CapacitySizeBytes: hdd?.Size,
                CapacityColumns: hdd?.NumberOfColumns,
                CapacityToleratedFailures: hdd?.PhysicalDiskRedundancy,
                ScmResiliency: scm?.ResiliencySettingName,
                ScmInterleaveBytes: scm?.Interleave,
                ScmSizeBytes: scm?.Size,
                ScmDataCopies: scm?.NumberOfDataCopies,
                SizeBytes: vdisk is null
                    ? null
                    : new[] { ssd, hdd, scm }
                        .Where(item => item is { MemberPhysicalDiskIds.Count: > 0 })
                        .Sum(item => item!.Size),
                CreateVirtualDisk: createVirtual,
                CreatePartition: partition is not null,
                AllocatedPoolId: poolId,
                AllocatedVirtualDiskId: vdiskId,
                AllocatedOsDiskId: osDiskId,
                AllocatedPartitionId: partitionId,
                AllocatedVolumeId: volumeId,
                DraftSourceId: draft.StableId,
                VolumeName: volume?.FileSystemLabel ?? partition?.FileSystemLabel));
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
            var finalTierSize = working.StorageTiers
                .Where(item => item.PoolStableId == draftVdisk.PoolStableId)
                .Sum(item => item.Size);
            steps.Add(new SimulationEditRequest(
                SimulationEditKind.CreateVirtualDisk,
                draftVdisk.PoolStableId ?? string.Empty,
                Name: draftVdisk.FriendlyName,
                Resiliency: draftVdisk.ResiliencySettingName,
                InterleaveBytes: draftVdisk.Interleave,
                SizeBytes: finalTierSize > 0 ? finalTierSize : draftVdisk.Size,
                AllocatedVirtualDiskId: vdiskId,
                VolumeName: null));
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

        // An existing empty pool may receive tier edits and its first virtual
        // disk in one batch. Apply the final tier settings before sizing and
        // creating that virtual disk.
        foreach (var create in steps.Where(item => item.Kind == SimulationEditKind.CreateVirtualDisk).ToArray())
        {
            var createIndex = steps.IndexOf(create);
            var updateIndex = steps.FindIndex(item =>
                item.Kind == SimulationEditKind.UpdateStoragePool
                && item.TargetProviderKey.Equals(create.TargetProviderKey, StringComparison.OrdinalIgnoreCase));
            if (updateIndex > createIndex)
            {
                var update = steps[updateIndex];
                steps.RemoveAt(updateIndex);
                steps.Insert(createIndex, update);
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
        var failedDissolveSteps = new List<(string PoolId, SimulationEditRequest Step, string Reason)>();
        var index = 0;
        foreach (var step in steps)
        {
            var parentPool = ParentDissolvedPool(committed, step, dissolvedPoolIds);
            var operation = ToOperation(step);
            StorageRuleDecision decision;
            var failedPrerequisite = parentPool is null
                ? default
                : failedDissolveSteps.FirstOrDefault(failure =>
                    failure.PoolId.Equals(parentPool, StringComparison.OrdinalIgnoreCase)
                    && DependsOn(step, failure.Step, committed));
            if (failedPrerequisite.Step is not null)
            {
                decision = new StorageRuleDecision(
                    StorageRuleVerdict.Deny,
                    "storage.rule.plan-prerequisite",
                    $"A prerequisite action did not pass: {failedPrerequisite.Reason}",
                    step.TargetProviderKey);
            }
            else
            {
                decision = StorageEditRules.Evaluate(document.Snapshot, operation);
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
            }

            if (parentPool is not null && decision.Verdict != StorageRuleVerdict.Allow)
            {
                failedDissolveSteps.Add((parentPool, step, decision.Message));
            }
            childItems.Add(new SimulationPlanItem(
                $"step:{index++}",
                ActionTitle(committed, step),
                step,
                parentPool is null ? null : $"dissolve:{parentPool}",
                decision,
                CausesDataLoss(committed, step)));
        }

        // Zero-member pools are useful while arranging a draft, but they
        // are not a valid applied result. Mark the move that emptied each
        // surviving pool so the action panel explains the final-state
        // failure before submission. A dissolved pool is absent here and
        // therefore remains valid.
        foreach (var emptyPool in document.Snapshot.StoragePools.Where(item =>
                     !item.IsPrimordial
                     && item.MemberPhysicalDiskIds.Count == 0
                     && !dissolvedPoolIds.Contains(item.StableId)))
        {
            var originalMembers = committed.StoragePools.FirstOrDefault(item =>
                    item.StableId.Equals(emptyPool.StableId, StringComparison.OrdinalIgnoreCase))?
                .MemberPhysicalDiskIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemIndex = childItems.FindLastIndex(item =>
                item.Request.Kind == SimulationEditKind.MovePhysicalDisk
                && originalMembers.Contains(item.Request.TargetProviderKey));
            var decision = new StorageRuleDecision(
                StorageRuleVerdict.Deny,
                "storage.rule.pool.empty-final",
                $"Storage pool '{emptyPool.FriendlyName}' must retain at least one physical disk when changes are applied.",
                emptyPool.StableId);
            if (itemIndex >= 0)
            {
                childItems[itemIndex] = childItems[itemIndex] with { Decision = decision };
            }
            else
            {
                childItems.Add(new SimulationPlanItem(
                    $"validation:pool-not-empty:{emptyPool.StableId}",
                    $"Validate storage pool {emptyPool.FriendlyName}",
                    new SimulationEditRequest(SimulationEditKind.UpdateStoragePool, emptyPool.StableId),
                    Decision: decision));
            }
        }

        var items = new List<SimulationPlanItem>();
        foreach (var item in childItems)
        {
            if (item.ParentId is not null && items.All(existing => existing.Id != item.ParentId))
            {
                var poolId = item.ParentId["dissolve:".Length..];
                var children = childItems.Where(candidate => candidate.ParentId == item.ParentId).ToArray();
                var blocked = children.Where(candidate => candidate.Decision?.Verdict != StorageRuleVerdict.Allow).ToArray();
                var parentDecision = blocked.Length == 0
                    ? new StorageRuleDecision(StorageRuleVerdict.Allow, "storage.rule.dissolve-plan", string.Empty, poolId)
                    : new StorageRuleDecision(
                        blocked.Any(candidate => candidate.Decision?.Verdict == StorageRuleVerdict.Deny)
                            ? StorageRuleVerdict.Deny
                            : StorageRuleVerdict.InsufficientInfo,
                        "storage.rule.dissolve-plan.blocked",
                        string.Join("\n", blocked
                            .Select(candidate => $"{candidate.Title}: {candidate.Decision!.Message}")
                            .Distinct(StringComparer.Ordinal)),
                        poolId);
                items.Add(new SimulationPlanItem(
                    item.ParentId,
                    $"Dissolve pool {committed.StoragePools.First(pool => pool.StableId == poolId).FriendlyName}",
                    new SimulationEditRequest(SimulationEditKind.DissolveStoragePool, poolId),
                    Decision: parentDecision,
                    CausesDataLoss: children.Any(candidate => candidate.CausesDataLoss)));
            }

            items.Add(item);
        }

        var planBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(steps));
        var planId = Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant()[..32];
        return new SimulationDraftPlan(planId, steps, items);
    }

    private static bool DependsOn(
        SimulationEditRequest step,
        SimulationEditRequest failed,
        StorageSnapshot snapshot)
    {
        if (step.Kind == SimulationEditKind.DeleteEmptyStoragePool)
        {
            return true;
        }

        if (step.Kind != SimulationEditKind.DeleteVirtualDisk
            || failed.Kind != SimulationEditKind.DeletePartition)
        {
            return false;
        }

        var partition = snapshot.Partitions.FirstOrDefault(item =>
            item.StableId.Equals(failed.TargetProviderKey, StringComparison.OrdinalIgnoreCase));
        var osDisk = partition is null ? null : snapshot.OsDisks.FirstOrDefault(item =>
            item.StableId.Equals(partition.OsDiskStableId, StringComparison.OrdinalIgnoreCase));
        return osDisk?.VirtualDiskStableId?.Equals(
            step.TargetProviderKey,
            StringComparison.OrdinalIgnoreCase) == true;
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

    private static string ActionTitle(StorageSnapshot snapshot, SimulationEditRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        var vdisk = snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        var targetPool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.Name);
        return request.Kind switch
        {
            SimulationEditKind.DeletePartition => partition is null
                ? $"Delete partition {request.TargetProviderKey}"
                : $"Delete disk {partition.DiskNumber} partition {partition.PartitionNumber} ({partition.DriveLetter})",
            SimulationEditKind.DeleteVirtualDisk => $"Delete virtual disk {vdisk?.FriendlyName ?? request.TargetProviderKey}",
            SimulationEditKind.MovePhysicalDisk =>
                $"Move {disk?.FriendlyName ?? request.TargetProviderKey}: {snapshot.StoragePools.FirstOrDefault(item => item.StableId == disk?.PoolStableId)?.FriendlyName ?? disk?.PoolStableId ?? "unpooled"} → {targetPool?.FriendlyName ?? request.Name}",
            SimulationEditKind.DeleteEmptyStoragePool => $"Remove empty pool {pool?.FriendlyName ?? request.TargetProviderKey}",
            SimulationEditKind.CreateTieredPool => $"Create storage pool {request.Name}",
            SimulationEditKind.CreateVirtualDisk => $"Create virtual disk {request.Name}",
            SimulationEditKind.UpdateStoragePool => UpdatePoolTitle(snapshot, request, pool),
            SimulationEditKind.FormatPartition => partition is null
                ? $"Format partition {request.TargetProviderKey} as {request.FileSystem}"
                : $"Format disk {partition.DiskNumber} partition {partition.PartitionNumber} ({partition.DriveLetter}) as {request.FileSystem}",
            SimulationEditKind.Rename => $"Rename {request.TargetProviderKey} to {request.Name}",
            _ => $"{request.Kind}: {request.TargetProviderKey}"
        };
    }

    private static string UpdatePoolTitle(
        StorageSnapshot snapshot,
        SimulationEditRequest request,
        StoragePoolInfo? pool)
    {
        var changes = new List<string>();
        AddSizeChange("SSD", request.PerformanceSizeBytes);
        AddSizeChange("HDD", request.CapacitySizeBytes);
        AddSizeChange("SCM", request.ScmSizeBytes);
        return changes.Count == 0
            ? $"Update storage pool {pool?.FriendlyName ?? request.TargetProviderKey}"
            : $"Update storage pool {pool?.FriendlyName ?? request.TargetProviderKey}: {string.Join(", ", changes)}";

        void AddSizeChange(string media, long? requested)
        {
            var current = snapshot.StorageTiers.FirstOrDefault(item =>
                item.PoolStableId == pool?.StableId && EditWorkspace.NormalizeMedia(item.MediaType) == media)?.Size;
            if (requested is not null && requested != current)
            {
                changes.Add($"{media} {TopologyProjector.FormatBytes(current ?? 0)} → {TopologyProjector.FormatBytes(requested.Value)}");
            }
        }
    }

    private static bool CausesDataLoss(StorageSnapshot snapshot, SimulationEditRequest request)
    {
        if (request.Kind == SimulationEditKind.DeletePartition)
        {
            var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
            return partition is not null && EditWorkspace.PartitionHoldsStoredData(partition);
        }

        if (request.Kind != SimulationEditKind.DeleteVirtualDisk)
        {
            return false;
        }

        return EditWorkspace.DiskHoldsStoredData(
            snapshot,
            request.TargetProviderKey,
            isVirtualDisk: true);
    }

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
                    ? StableAllocatedId("sim:partition", partition.StableId)
                    : partition.StableId,
                AllocatedVolumeId: volume?.StableId.StartsWith("edit:", StringComparison.Ordinal) == true
                    ? StableAllocatedId("sim:volume", volume.StableId)
                    : volume?.StableId,
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

        var suffix = draftId[(draftId.LastIndexOf(':') + 1)..];
        var created = $"{prefix}:{suffix}";
        allocated[draftId] = created;
        return created;
    }

    private static string StableAllocatedId(string prefix, string draftId)
    {
        var suffix = draftId[(draftId.LastIndexOf(':') + 1)..];
        return $"{prefix}:{suffix}";
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
            request.AccessPaths,
            request.VolumeName);
}
