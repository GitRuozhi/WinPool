using WinPool.Domain;

namespace WinPool.Application;

public static class StorageEditRules
{
    public const string WindowsPhysicalDiskUsage =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk";
    public const string WindowsPartition =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition";
    public const string WindowsVolume =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-volume";
    public const string WindowsTierSupportedSize =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagetier-getsupportedsize";

    public static StorageRuleDecision Evaluate(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (SimulationEditKind.Rename or SimulationEditKind.OptimizePool or SimulationEditKind.OptimizeDrive)
            && HasUnsupportedRelatedValue(snapshot, request))
            return Deny("storage.rule.source-value-out-of-range",
                "A related source value exceeds the supported editing range. Inspect its original value in source details.");
        var unavailable = RequiredSourceIssue(snapshot, request);
        if (unavailable is not null)
            return new(StorageRuleVerdict.InsufficientInfo, "storage.rule.source-field-unavailable",
                $"{unavailable.FieldName} is unavailable or conflicting ({unavailable.State}, {unavailable.Reason}); this operation requires reliable source information.", unavailable.ObjectId);
        return request.Kind switch
        {
            SimulationEditKind.Rename => Allow("storage.rule.rename"),
            SimulationEditKind.ChangeDriveLetter => EvaluateDriveLetter(snapshot, request),
            SimulationEditKind.FormatPartition => EvaluateFormat(snapshot, request),
            SimulationEditKind.DeletePartition => EvaluateDeletePartition(snapshot, request),
            SimulationEditKind.SetDiskOffline => EvaluateOffline(snapshot, request),
            SimulationEditKind.InitializeDisk => EvaluateInitialize(snapshot, request),
            SimulationEditKind.ConvertDisk => EvaluateConvert(snapshot, request),
            SimulationEditKind.CreatePartition => EvaluateCreatePartition(snapshot, request),
            SimulationEditKind.ExtendPartition => UnsupportedResize("extend"),
            SimulationEditKind.ShrinkPartition => UnsupportedResize("shrink"),
            SimulationEditKind.CreateStoragePool => EvaluateCreatePool(snapshot, request),
            SimulationEditKind.CreateTieredPool => EvaluateCreateTieredPool(snapshot, request),
            SimulationEditKind.CreateVirtualDisk => EvaluateCreateVirtualDisk(snapshot, request),
            SimulationEditKind.DeleteVirtualDisk => EvaluateDeleteVirtualDisk(snapshot, request),
            SimulationEditKind.UpdateStoragePool => EvaluateUpdatePool(snapshot, request),
            SimulationEditKind.DissolveStoragePool => EvaluateDissolve(snapshot, request),
            SimulationEditKind.DeleteEmptyStoragePool => EvaluateDeleteEmptyPool(snapshot, request),
            SimulationEditKind.MovePhysicalDisk => EvaluateMove(snapshot, request),
            SimulationEditKind.EvictPhysicalDiskFromTiers => EvaluateEvict(snapshot, request),
            SimulationEditKind.SetDiskUsage => EvaluateUsage(snapshot, request),
            SimulationEditKind.OptimizePool => AllowNoOp("storage.rule.optimize-pool.simulated-noop"),
            SimulationEditKind.OptimizeDrive => AllowNoOp("storage.rule.optimize-drive.simulated-noop"),
            _ => Deny("storage.rule.unknown-operation", $"Operation {request.Kind} is not recognized.")
        };
    }

    private static StorageFieldIssue? RequiredSourceIssue(StorageSnapshot snapshot, SimulationEditRequest request)
    {
        if (snapshot.FieldIssues.Count == 0 || request.Kind is SimulationEditKind.Rename
            or SimulationEditKind.OptimizeDrive or SimulationEditKind.OptimizePool) return null;
        var required = new HashSet<(string, string)>();
        void Need(string? id, params string[] fields) { if (id is not null) foreach (var field in fields) required.Add((id, field)); }
        var target = request.TargetProviderKey;
        var partition = snapshot.Partitions.FirstOrDefault(x => x.StableId == target);
        var disk = snapshot.OsDisks.FirstOrDefault(x => x.StableId == (partition?.OsDiskStableId ?? target));
        var partitionAction = request.Kind is SimulationEditKind.FormatPartition or SimulationEditKind.DeletePartition
            or SimulationEditKind.ChangeDriveLetter or SimulationEditKind.CreatePartition or SimulationEditKind.InitializeDisk
            or SimulationEditKind.ConvertDisk or SimulationEditKind.SetDiskOffline;
        if (partitionAction)
        {
            Need(disk?.StableId, "IsOffline");
            if (request.Kind is SimulationEditKind.FormatPartition or SimulationEditKind.DeletePartition)
                Need(target, "IsBoot", "IsSystem");
            if (request.Kind == SimulationEditKind.FormatPartition) Need(target, "Type");
            if (request.Kind is SimulationEditKind.InitializeDisk or SimulationEditKind.ConvertDisk or SimulationEditKind.SetDiskOffline)
                Need(disk?.StableId, "IsBoot", "IsSystem", "PartitionStyle");
            if (request.Kind == SimulationEditKind.SetDiskOffline && request.Offline == true)
                Need(disk?.PhysicalDiskStableId, "IsPageFile", "IsCrashDump");
            if (request.Kind == SimulationEditKind.CreatePartition)
            {
                Need(disk?.StableId, "Size", "PartitionStyle");
                foreach (var item in snapshot.Partitions.Where(x => x.OsDiskStableId == disk?.StableId)) Need(item.StableId, "Size", "Offset");
            }
        }
        else
        {
            var poolId = snapshot.StoragePools.Any(x => x.StableId == target) ? target
                : snapshot.VirtualDisks.FirstOrDefault(x => x.StableId == target)?.PoolStableId
                    ?? snapshot.PhysicalDisks.FirstOrDefault(x => x.StableId == target)?.PoolStableId;
            var ids = new HashSet<string>(request.MemberDiskIds ?? []) { target };
            foreach (var pool in snapshot.StoragePools.Where(x => x.StableId == poolId && !x.IsPrimordial))
                foreach (var member in pool.MemberPhysicalDiskIds) ids.Add(member);
            foreach (var physical in snapshot.PhysicalDisks.Where(x => ids.Contains(x.StableId)))
            {
                Need(physical.StableId, "Size", "Usage", "MediaType");
                if (physical.StableId == target || request.Kind is not (SimulationEditKind.MovePhysicalDisk or SimulationEditKind.SetDiskUsage))
                    Need(physical.StableId, "IsBoot", "IsSystem", "IsPageFile", "IsCrashDump");
            }
            foreach (var os in snapshot.OsDisks.Where(x => ids.Contains(x.PhysicalDiskStableId ?? "")
                || snapshot.VirtualDisks.Any(v => v.StableId == x.VirtualDiskStableId && v.PoolStableId == poolId))) Need(os.StableId, "IsOffline");
            foreach (var tier in snapshot.StorageTiers.Where(x => x.PoolStableId == poolId)) Need(tier.StableId, "Size", "AllocatedSize");
            foreach (var volume in snapshot.Volumes.Where(x => snapshot.Partitions.Any(p => p.StableId == x.PartitionStableId
                && snapshot.OsDisks.Any(d => d.StableId == p.OsDiskStableId && (ids.Contains(d.PhysicalDiskStableId ?? "")
                    || snapshot.VirtualDisks.Any(v => v.StableId == d.VirtualDiskStableId && v.PoolStableId == poolId))))))
                Need(volume.StableId, "Size", "SizeRemaining");
        }
        return snapshot.FieldIssues.FirstOrDefault(x => required.Contains((x.ObjectId, x.FieldName)));
    }

    private static bool HasUnsupportedRelatedValue(StorageSnapshot snapshot, SimulationEditRequest request)
    {
        var invalid = snapshot.Warnings.Where(x => x.Code == "facts.numeric-out-of-range")
            .Select(x => x.StableId).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (invalid.Count == 0) return false;
        var related = new HashSet<string>(request.MemberDiskIds ?? [], StringComparer.OrdinalIgnoreCase) { request.TargetProviderKey };
        if (request.Kind == SimulationEditKind.MovePhysicalDisk && request.DestinationGroupId is not null) related.Add(request.DestinationGroupId);
        var relationships = StorageRelationshipProjector.Project(snapshot);
        bool changed;
        do
        {
            changed = false;
            foreach (var link in relationships)
                if (related.Contains(link.FromStableId) || related.Contains(link.ToStableId))
                {
                    changed |= related.Add(link.FromStableId);
                    changed |= related.Add(link.ToStableId);
                }
        } while (changed);
        return invalid.Any(x => related.Contains(x!));
    }

    public static IReadOnlyList<(SimulationEditKind Kind, string Support)> OperationMatrix() =>
    [
        (SimulationEditKind.ResetDocument, "internal: built-in document reset adapter only"),
        (SimulationEditKind.Rename, "supported: object friendly name / volume label"),
        (SimulationEditKind.ChangeDriveLetter, "supported: unused letter, volume present"),
        (SimulationEditKind.FormatPartition, "supported: NTFS, ReFS, exFAT"),
        (SimulationEditKind.DeletePartition, "supported: existing non-boot/system partition"),
        (SimulationEditKind.SetDiskOffline, "supported: persisted simulation disk state"),
        (SimulationEditKind.InitializeDisk, "supported: GPT only; MBR initialize denied"),
        (SimulationEditKind.ConvertDisk, "supported: destructive MBR data disk to GPT"),
        (SimulationEditKind.CreatePartition, "supported: four fixed GPT partition kinds"),
        (SimulationEditKind.ExtendPartition, "not_supported: no Windows supported-size evidence"),
        (SimulationEditKind.ShrinkPartition, "not_supported: no Windows supported-size evidence"),
        (SimulationEditKind.CreateStoragePool, "supported: primordial data members"),
        (SimulationEditKind.CreateTieredPool, "supported: Simple/Mirror×2/Parity with legal disk counts"),
        (SimulationEditKind.CreateVirtualDisk, "supported: at most one new VD per pool; Fixed estimate"),
        (SimulationEditKind.DeleteVirtualDisk, "supported: explicit delete"),
        (SimulationEditKind.UpdateStoragePool, "supported: unused-capacity layout fields"),
        (SimulationEditKind.DissolveStoragePool, "supported: non-primordial"),
        (SimulationEditKind.DeleteEmptyStoragePool, "internal: remove an empty pool after ordered dissolve steps"),
        (SimulationEditKind.MovePhysicalDisk, "supported: primordial or same-media tier"),
        (SimulationEditKind.EvictPhysicalDiskFromTiers, "supported: keep in pool, drop tier membership"),
        (SimulationEditKind.SetDiskUsage, "supported: Retired/Hot Spare when pool has remaining data members"),
        (SimulationEditKind.OptimizePool, "simulated no-op; does not claim a measured result"),
        (SimulationEditKind.OptimizeDrive, "simulated no-op; does not claim a measured result")
    ];

    public static bool TouchesOfflineDisk(
        StorageSnapshot snapshot,
        IEnumerable<string> targetStableIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetStableIds);
        var targets = targetStableIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetedPools = snapshot.StoragePools.Where(pool => targets.Contains(pool.StableId))
            .Select(pool => pool.StableId)
            .Concat(snapshot.StorageTiers.Where(tier => targets.Contains(tier.StableId))
                .Select(tier => tier.PoolStableId))
            .Where(poolId => poolId is not null)
            .Select(poolId => poolId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var osDisk in snapshot.OsDisks.Where(item => item.IsOffline))
        {
            var virtualPoolId = osDisk.VirtualDiskStableId is string virtualDiskId
                ? snapshot.VirtualDisks.FirstOrDefault(item =>
                    item.StableId.Equals(virtualDiskId, StringComparison.OrdinalIgnoreCase))?.PoolStableId
                : null;
            if (targets.Contains(osDisk.StableId)
                || osDisk.PhysicalDiskStableId is not null && targets.Contains(osDisk.PhysicalDiskStableId)
                || osDisk.VirtualDiskStableId is not null && targets.Contains(osDisk.VirtualDiskStableId)
                || virtualPoolId is not null && targetedPools.Contains(virtualPoolId)
                || osDisk.PhysicalDiskStableId is not null && snapshot.StoragePools.Any(pool =>
                    targetedPools.Contains(pool.StableId)
                    && pool.MemberPhysicalDiskIds.Contains(osDisk.PhysicalDiskStableId, StringComparer.OrdinalIgnoreCase))
                || snapshot.Partitions.Any(partition =>
                    partition.OsDiskStableId == osDisk.StableId
                    && (targets.Contains(partition.StableId)
                        || snapshot.Volumes.Any(volume =>
                            volume.PartitionStableId == partition.StableId && targets.Contains(volume.StableId)))))
            {
                return true;
            }
        }

        return false;
    }

    private static StorageRuleDecision EvaluateDriveLetter(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        if (snapshot.VolumeForPartition(request.TargetProviderKey) is null)
        {
            return Deny("storage.rule.drive-letter.missing-volume", "A volume is required before a drive letter can be assigned.");
        }

        var letter = TopologyProjector.NormalizeDriveLetter(request.DriveLetter);
        if (letter.Length == 0)
        {
            return Allow("storage.rule.drive-letter.clear");
        }

        if (letter.Length != 1)
        {
            return Deny("storage.rule.drive-letter.invalid", "A drive letter must be a single unused letter A–Z.");
        }

        var used = snapshot.Volumes.Select(item => item.DriveLetter)
            .Concat(snapshot.NetworkDisks.Select(item => TopologyProjector.NormalizeDriveLetter(item.DriveLetter)))
            .Where(item => item.Length == 1);
        if (used.Contains(letter, StringComparer.OrdinalIgnoreCase)
            && snapshot.VolumeForPartition(request.TargetProviderKey)?.DriveLetter != letter)
        {
            return Deny("storage.rule.drive-letter.conflict", $"Drive letter {letter}: is already in use.");
        }

        return Allow("storage.rule.drive-letter");
    }

    private static StorageRuleDecision EvaluateFormat(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (partition is null)
        {
            return Deny("storage.rule.format.missing", "The selected partition was not found.");
        }

        if (partition.IsBoot || partition.IsSystem || partition.Type is "EfiSystem" or "MicrosoftReserved" or "WindowsRecovery")
        {
            return Deny("storage.rule.format.system", "System, EFI, MSR, and recovery partitions cannot be formatted.");
        }

        var fileSystem = (request.FileSystem ?? "NTFS").Trim().ToUpperInvariant();
        if (!IsCreateFileSystem(fileSystem))
        {
            return Deny(
                "storage.rule.format.filesystem",
                $"File system {fileSystem} is outside the create range (NTFS, ReFS, exFAT).");
        }

        return Allow("storage.rule.format", WindowsVolume);
    }

    private static StorageRuleDecision EvaluateDeletePartition(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (partition is null)
        {
            return Deny("storage.rule.delete-partition.missing", "The selected partition was not found.");
        }

        if (partition.IsBoot || partition.IsSystem)
        {
            return Deny(
                "storage.rule.delete-partition.system",
                "The boot or system partition cannot be deleted.");
        }

        return Allow("storage.rule.delete-partition", WindowsPartition);
    }

    private static StorageRuleDecision EvaluateOffline(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.offline.missing", "The selected disk was not found.");
        }

        var physical = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == disk.PhysicalDiskStableId);
        if (request.Offline == true
            && (disk.IsBoot || disk.IsSystem || physical?.IsPageFile == true || physical?.IsCrashDump == true))
        {
            return Deny(
                "storage.rule.offline.role",
                "Boot, system, page-file, and crash-dump disks cannot be taken offline.");
        }

        return Allow("storage.rule.offline");
    }

    private static StorageRuleDecision EvaluateInitialize(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var style = request.PartitionStyle?.Trim().ToUpperInvariant();
        if (style == "MBR")
        {
            return Deny("storage.rule.initialize.mbr", "New MBR partition tables are outside the product create range.");
        }

        if (style != "GPT")
        {
            return Deny("storage.rule.initialize.style", "Only GPT initialization is supported.");
        }

        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.initialize.missing", "The selected disk was not found.");
        }

        if (disk.IsBoot || disk.IsSystem)
        {
            return Deny("storage.rule.initialize.system", "The boot or system disk cannot be initialized.");
        }

        if (!string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("storage.rule.initialize.raw-only", "Only a RAW disk can be initialized.");
        }

        return Allow("storage.rule.initialize", WindowsPartition);
    }

    private static StorageRuleDecision EvaluateConvert(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var style = request.PartitionStyle?.Trim().ToUpperInvariant();
        if (style != "GPT")
        {
            return Deny("storage.rule.convert.gpt-only", "Conversion is limited to GPT. MBR conversion is not offered.");
        }

        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.convert.missing", "The selected disk was not found.");
        }

        if (disk.IsBoot || disk.IsSystem)
        {
            return Deny("storage.rule.convert.system", "The boot or system disk cannot use the destructive conversion path.");
        }

        if (!string.Equals(disk.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("storage.rule.convert.mbr-only", "Only an MBR disk can be converted to GPT.");
        }

        return Allow("storage.rule.convert", WindowsPartition);
    }

    private static StorageRuleDecision EvaluateCreatePartition(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.create-partition.missing", "The selected disk was not found.");
        }

        if (disk.IsOffline)
        {
            return Deny("storage.rule.create-partition.offline", "An offline disk cannot accept a new partition.");
        }

        if (!string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase))
        {
            return Deny(
                "storage.rule.create-partition.gpt",
                "Creating partitions is limited to GPT disks in this stage.");
        }

        if (request.SizeBytes is < 0)
        {
            return Deny("storage.rule.create-partition.size", "Partition size cannot be negative.");
        }

        var kind = request.PartitionKind ?? PartitionKind.BasicData;
        var fileSystem = request.FileSystem?.Trim().ToUpperInvariant() ?? string.Empty;
        if (kind == PartitionKind.MicrosoftReserved && fileSystem.Length > 0)
        {
            return Deny("storage.rule.create-partition.msr-format", "An MSR partition cannot be formatted.");
        }
        if (kind == PartitionKind.EfiSystem && fileSystem != "FAT32")
        {
            return Deny("storage.rule.create-partition.efi-filesystem", "An EFI system partition must use FAT32.");
        }
        if (kind == PartitionKind.WindowsRecovery && fileSystem != "NTFS")
        {
            return Deny("storage.rule.create-partition.recovery-filesystem", "A Windows recovery partition created here must use NTFS.");
        }
        if (kind == PartitionKind.BasicData && fileSystem.Length > 0 && !IsCreateFileSystem(fileSystem))
        {
            return Deny(
                "storage.rule.create-partition.filesystem",
                "A formatted basic data partition must use NTFS, ReFS, or exFAT.");
        }

        return Allow("storage.rule.create-partition", WindowsPartition);
    }

    private static StorageRuleDecision UnsupportedResize(string action) =>
        new(
            StorageRuleVerdict.InsufficientInfo,
            $"storage.rule.{action}.unsupported",
            $"Partition {action} is not offered without a Windows supported-size or file-system limit for this configuration.",
            Source: WindowsVolume);

    private static StorageRuleDecision EvaluateCreatePool(
        StorageSnapshot snapshot,
        SimulationEditRequest request) =>
        EvaluateMembers(snapshot, request.MemberDiskIds, requirePrimordial: true);

    private static StorageRuleDecision EvaluateCreateTieredPool(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var members = EvaluateMembers(snapshot, request.MemberDiskIds, requirePrimordial: true);
        if (members.Verdict != StorageRuleVerdict.Allow)
        {
            return members;
        }

        if (request.MemberDiskIds is null || request.MemberDiskIds.Count == 0)
        {
            return request.CreateVirtualDisk == true
                ? Deny("storage.rule.empty-pool.virtual-disk", "An empty pool cannot create a virtual disk.")
                : Allow("storage.rule.create-empty-pool", WindowsPhysicalDiskUsage);
        }

        var disks = snapshot.PhysicalDisks
            .Where(item => (request.MemberDiskIds ?? []).Contains(item.StableId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        foreach (var group in disks.GroupBy(item => EditWorkspace.NormalizeMedia(item.MediaType)))
        {
            var ids = group.Select(item => item.StableId).ToArray();
            var setting = group.Key switch
            {
                "HDD" => request.CapacityResiliency,
                "SCM" => request.ScmResiliency,
                _ => request.PerformanceResiliency ?? request.Resiliency
            };
            setting ??= EditWorkspace.RecommendedResiliency(group.Key, ids.Length);
            var copies = group.Key == "SCM" ? request.ScmDataCopies : request.PerformanceDataCopies;
            copies ??= EditWorkspace.RecommendedDataCopies(setting, ids.Length);
            var decision = EvaluateLayout(
                snapshot,
                ids,
                setting,
                copies,
                group.Key == "HDD" ? request.CapacityColumns : null,
                group.Key == "HDD" ? request.CapacityToleratedFailures : null);
            if (decision.Verdict != StorageRuleVerdict.Allow)
            {
                return decision;
            }

            var requestedSize = group.Key switch
            {
                "HDD" => request.CapacitySizeBytes,
                "SCM" => request.ScmSizeBytes,
                _ => request.PerformanceSizeBytes
            };
            var capacity = EvaluateCapacity(
                snapshot, ids, setting, copies,
                group.Key == "HDD" ? request.CapacityColumns : null,
                group.Key == "HDD" ? request.CapacityToleratedFailures : null,
                requestedSize);
            if (capacity.Verdict != StorageRuleVerdict.Allow)
            {
                return capacity;
            }
        }

        return Allow("storage.rule.create-tiered-pool", WindowsTierSupportedSize);
    }

    private static StorageRuleDecision EvaluateCreateVirtualDisk(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (pool is null || pool.IsPrimordial)
        {
            return Deny("storage.rule.virtual-disk.pool", "A non-primordial pool is required.");
        }

        if (snapshot.VirtualDisks.Count(item => item.PoolStableId == pool.StableId) >= 1)
        {
            return Deny(
                "storage.rule.virtual-disk.one",
                "This editor creates at most one virtual disk per pool. Existing extra virtual disks are read-only.");
        }

        var copies = request.PerformanceDataCopies
            ?? CopiesFor(request.Resiliency ?? request.PerformanceResiliency ?? "Mirror");
        return EvaluateLayout(
            snapshot,
            pool.MemberPhysicalDiskIds,
            request.Resiliency ?? request.PerformanceResiliency,
            copies,
            request.CapacityColumns,
            request.CapacityToleratedFailures);
    }

    private static StorageRuleDecision EvaluateDeleteVirtualDisk(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var vdisk = snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        return vdisk is null
            ? Deny("storage.rule.delete-virtual-disk.missing", "The simulated virtual disk was not found.")
            : Allow("storage.rule.delete-virtual-disk");
    }

    private static StorageRuleDecision EvaluateUpdatePool(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (pool is null || pool.IsPrimordial)
        {
            return Deny("storage.rule.update-pool.invalid", "A non-primordial pool is required.");
        }

        if (snapshot.VirtualDisks.Count(item => item.PoolStableId == pool.StableId) > 1)
        {
            return Deny(
                "storage.rule.update-pool.multiple-vdisks",
                "A pool with more than one virtual disk cannot be modified in this editor.");
        }

        var tiers = snapshot.StorageTiers.Where(item => item.PoolStableId == pool.StableId).ToArray();
        var changesLayout = tiers.Any(tier => TierLayoutChanges(tier, request));
        if (changesLayout && EditWorkspace.PoolHoldsStoredData(snapshot, pool.StableId))
        {
            try
            {
                var hasUnsafeChange = tiers.Any(tier =>
                    TierLayoutChanges(tier, request)
                    && (TierSpecificationChanges(tier, request)
                        || !UsesMaximumCapacity(tier, request)
                        || RequestedTierSize(snapshot, tier, request) < tier.Size));
                if (hasUnsafeChange)
                {
                    return Deny(
                        "storage.rule.update-pool.existing-data",
                        "A pool containing data can only expand a tier with Use maximum size; tier settings and capacity reductions remain blocked.");
                }
            }
            catch (ArgumentException)
            {
                return new(
                    StorageRuleVerdict.InsufficientInfo,
                    "storage.rule.update-pool.maximum-unknown",
                    "The simulated maximum cannot be calculated safely for this tier layout.",
                    pool.StableId,
                    WindowsTierSupportedSize);
            }
        }

        foreach (var tier in tiers)
        {
            if (!TierLayoutChanges(tier, request))
            {
                continue;
            }

            var media = EditWorkspace.NormalizeMedia(tier.MediaType);
            var setting = media switch
            {
                "HDD" => request.CapacityResiliency ?? tier.ResiliencySettingName,
                "SCM" => request.ScmResiliency ?? tier.ResiliencySettingName,
                _ => request.PerformanceResiliency ?? tier.ResiliencySettingName
            };
            var copies = media switch
            {
                "HDD" => tier.NumberOfDataCopies,
                "SCM" => request.ScmDataCopies ?? tier.NumberOfDataCopies,
                _ => request.PerformanceDataCopies ?? tier.NumberOfDataCopies
            };
            var columns = media == "HDD" ? request.CapacityColumns ?? tier.NumberOfColumns : tier.NumberOfColumns;
            var tolerated = media == "HDD"
                ? request.CapacityToleratedFailures ?? tier.PhysicalDiskRedundancy
                : tier.PhysicalDiskRedundancy;
            var layout = EvaluateLayout(snapshot, tier.MemberPhysicalDiskIds, setting, copies, columns, tolerated);
            if (layout.Verdict != StorageRuleVerdict.Allow)
            {
                return layout;
            }

            var size = media switch
            {
                "HDD" => request.CapacitySizeBytes ?? tier.Size,
                "SCM" => request.ScmSizeBytes ?? tier.Size,
                _ => request.PerformanceSizeBytes ?? tier.Size
            };
            var capacity = EvaluateCapacity(
                snapshot, tier.MemberPhysicalDiskIds, setting, copies, columns, tolerated, size);
            if (capacity.Verdict != StorageRuleVerdict.Allow)
            {
                return capacity;
            }
        }

        if (changesLayout)
        {
            long requestedLogicalSize;
            try
            {
                requestedLogicalSize = tiers.Sum(tier => RequestedTierSize(snapshot, tier, request));
            }
            catch (ArgumentException)
            {
                return new(
                    StorageRuleVerdict.InsufficientInfo,
                    "storage.rule.update-pool.partition-bounds-unknown",
                    "The requested layout does not provide enough information to validate partition boundaries.",
                    pool.StableId,
                    WindowsTierSupportedSize);
            }

            var vdiskIds = snapshot.VirtualDisks.Where(item => item.PoolStableId == pool.StableId)
                .Select(item => item.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var osDisk in snapshot.OsDisks.Where(item =>
                         item.VirtualDiskStableId is not null && vdiskIds.Contains(item.VirtualDiskStableId)))
            {
                foreach (var partition in snapshot.Partitions.Where(item => item.OsDiskStableId == osDisk.StableId))
                {
                    long required;
                    try
                    {
                        required = checked(partition.Offset + partition.Size);
                    }
                    catch (OverflowException)
                    {
                        return Deny(
                            "storage.rule.update-pool.partition-range",
                            $"Disk {partition.DiskNumber} partition {partition.PartitionNumber} has an invalid byte range.",
                            partition.StableId);
                    }

                    if (required > requestedLogicalSize)
                    {
                        return Deny(
                            "storage.rule.update-pool.partition-bounds",
                            $"Disk {partition.DiskNumber} partition {partition.PartitionNumber} requires at least {required} bytes, but the requested OS disk size is {requestedLogicalSize} bytes.",
                            partition.StableId);
                    }
                }
            }
        }

        return Allow("storage.rule.update-pool");
    }

    private static StorageRuleDecision EvaluateDissolve(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (pool is null || pool.IsPrimordial)
        {
            return Deny("storage.rule.dissolve.invalid", "The primordial pool cannot be dissolved.");
        }

        return Allow("storage.rule.dissolve");
    }

    private static StorageRuleDecision EvaluateDeleteEmptyPool(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (pool is null || pool.IsPrimordial)
        {
            return Deny("storage.rule.delete-empty-pool.invalid", "A non-primordial pool is required.");
        }

        if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
            || pool.MemberPhysicalDiskIds.Count > 0)
        {
            return Deny("storage.rule.delete-empty-pool.not-empty", "The pool must have no virtual disks or member disks before it is removed.");
        }

        return Allow("storage.rule.delete-empty-pool");
    }

    private static StorageRuleDecision EvaluateMove(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.move.missing", "The selected physical disk was not found.");
        }

        if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
        {
            return Deny("storage.rule.move.role", "Boot, system, page-file, and crash-dump disks cannot change pool membership.");
        }

        if (PhysicalDiskUsage.IsUnknown(disk.Usage) && !string.IsNullOrEmpty(disk.PoolStableId))
        {
            return new(
                StorageRuleVerdict.InsufficientInfo,
                "storage.rule.move.unknown-usage",
                "Disk usage is unknown, so membership changes are not applied.",
                disk.StableId,
                WindowsPhysicalDiskUsage);
        }

        return Allow("storage.rule.move", WindowsPhysicalDiskUsage);
    }

    private static StorageRuleDecision EvaluateEvict(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        return disk is null
            ? Deny("storage.rule.evict.missing", "The selected physical disk was not found.")
            : Allow("storage.rule.evict", WindowsPhysicalDiskUsage);
    }

    private static StorageRuleDecision EvaluateUsage(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetProviderKey);
        if (disk is null)
        {
            return Deny("storage.rule.usage.missing", "The selected physical disk was not found.");
        }

        var layer = request.DiskUsage?.Trim() ?? string.Empty;
        if (layer is not ("" or "Retired" or "HotSpare"))
        {
            return Deny("storage.rule.usage.value", "Usage must be data, Retired, or Hot Spare.");
        }

        if (disk.IsBoot || disk.IsSystem)
        {
            return Deny("storage.rule.usage.role", "Boot and system disks cannot change usage.");
        }

        if (string.IsNullOrEmpty(disk.PoolStableId))
        {
            return Deny("storage.rule.usage.unpooled", "Only pooled disks can enter Retired or Hot Spare.");
        }

        if (layer.Length == 0)
        {
            return Allow("storage.rule.usage.clear", WindowsPhysicalDiskUsage);
        }

        var pool = snapshot.StoragePools.First(item => item.StableId == disk.PoolStableId);
        var remainingData = snapshot.PhysicalDisks.Count(item =>
            pool.MemberPhysicalDiskIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
            && item.StableId != disk.StableId
            && PhysicalDiskUsage.ContributesDataCapacity(item.Usage));
        if (remainingData < 1)
        {
            return Deny(
                "storage.rule.usage.last-data-member",
                "The last data member cannot be retired or converted to a hot spare.");
        }

        return Allow("storage.rule.usage", WindowsPhysicalDiskUsage);
    }

    private static StorageRuleDecision EvaluateMembers(
        StorageSnapshot snapshot,
        IReadOnlyList<string>? memberIds,
        bool requirePrimordial)
    {
        if (memberIds is null || memberIds.Count == 0)
        {
            return Allow("storage.rule.members.empty-pool", WindowsPhysicalDiskUsage);
        }

        var primordial = snapshot.StoragePools.FirstOrDefault(item => item.IsPrimordial);
        foreach (var id in memberIds)
        {
            var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
                item.StableId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (disk is null)
            {
                return Deny("storage.rule.members.missing", "A selected physical disk was not found.");
            }

            if (!disk.CanPool && requirePrimordial)
            {
                return Deny("storage.rule.members.cannot-pool", $"'{disk.FriendlyName}' cannot join a pool: {disk.CannotPoolReason}");
            }

            if (disk.IsBoot || disk.IsSystem || disk.IsPageFile || disk.IsCrashDump)
            {
                return Deny("storage.rule.members.role", "Boot, system, page-file, and crash-dump disks cannot join a pool.");
            }

            if (PhysicalDiskUsage.IsUnknown(disk.Usage) && !string.IsNullOrEmpty(disk.Usage))
            {
                return new(
                    StorageRuleVerdict.InsufficientInfo,
                    "storage.rule.members.unknown-usage",
                    "A selected disk has unknown usage, so it is not treated as safe to pool.",
                    disk.StableId,
                    WindowsPhysicalDiskUsage);
            }

            if (requirePrimordial && primordial is not null && disk.PoolStableId != primordial.StableId)
            {
                return Deny("storage.rule.members.not-primordial", "Only primordial disks can join a new pool.");
            }
        }

        return Allow("storage.rule.members", WindowsPhysicalDiskUsage);
    }

    private static StorageRuleDecision EvaluateLayout(
        StorageSnapshot snapshot,
        IReadOnlyList<string> memberIds,
        string? resiliency,
        int? dataCopies,
        int? columns,
        int? tolerated)
    {
        var dataMembers = snapshot.PhysicalDisks
            .Where(item => memberIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
                && PhysicalDiskUsage.ContributesDataCapacity(item.Usage))
            .ToArray();
        var setting = string.IsNullOrWhiteSpace(resiliency)
            ? (dataMembers.Length < 2 ? "Simple" : "Mirror")
            : resiliency.Trim();
        var copies = dataCopies ?? CopiesFor(setting);
        if (string.Equals(setting, "Mirror", StringComparison.OrdinalIgnoreCase))
        {
            if (copies < 2)
            {
                return Deny("storage.rule.layout.mirror-copies", "Mirror requires at least two data copies.");
            }

            if (dataMembers.Length < copies)
            {
                return Deny(
                    "storage.rule.layout.mirror-disks",
                    $"A {copies}-copy mirror needs at least {copies} data disks. A single-disk mirror is not legal.");
            }
        }
        else if (string.Equals(setting, "Simple", StringComparison.OrdinalIgnoreCase))
        {
            if (dataMembers.Length < 1)
            {
                return Deny("storage.rule.layout.simple-disks", "Simple layout needs at least one data disk.");
            }
        }
        else if (string.Equals(setting, "Parity", StringComparison.OrdinalIgnoreCase))
        {
            var redundancy = tolerated ?? 1;
            var columnCount = columns ?? dataMembers.Length;
            if (columnCount < redundancy + 2)
            {
                return Deny(
                    "storage.rule.layout.parity-columns",
                    "Parity needs enough columns for data plus redundancy.");
            }

            if (dataMembers.Length < columnCount)
            {
                return Deny("storage.rule.layout.parity-disks", "Parity column count cannot exceed the data disk count.");
            }
        }
        else
        {
            return new(
                StorageRuleVerdict.InsufficientInfo,
                "storage.rule.layout.unknown",
                $"Resiliency '{setting}' is not a covered Windows layout.",
                Source: WindowsTierSupportedSize);
        }

        return Allow("storage.rule.layout", WindowsTierSupportedSize);
    }

    private static StorageRuleDecision EvaluateCapacity(
        StorageSnapshot snapshot,
        IReadOnlyList<string> memberIds,
        string? resiliency,
        int? dataCopies,
        int? columns,
        int? tolerated,
        long? requestedBytes)
    {
        if (requestedBytes is <= 0)
        {
            return Deny("storage.rule.capacity.zero", "A positive logical capacity is required.");
        }

        var dataMembers = snapshot.PhysicalDisks
            .Where(item => memberIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase)
                && PhysicalDiskUsage.ContributesDataCapacity(item.Usage))
            .ToArray();
        var setting = string.IsNullOrWhiteSpace(resiliency)
            ? (dataMembers.Length < 2 ? "Simple" : "Mirror")
            : resiliency.Trim();
        try
        {
            var estimate = ConservativeCapacity.PlanLogicalUpperBound(
                dataMembers.Select(item => item.Size).ToArray(),
                setting,
                dataCopies ?? CopiesFor(setting),
                columns,
                setting.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? tolerated ?? 1 : 0);
            if (estimate.AlignedLogicalBytes <= 0)
            {
                return Deny("storage.rule.capacity.none", "The selected layout has less than 4 GiB of createable logical capacity.");
            }

            return requestedBytes > estimate.AlignedLogicalBytes
                ? Deny(
                    "storage.rule.capacity.exceeds-maximum",
                    $"Requested capacity {requestedBytes} bytes exceeds the simulated maximum {estimate.AlignedLogicalBytes} bytes.")
                : Allow("storage.rule.capacity");
        }
        catch (ArgumentException)
        {
            return new(
                StorageRuleVerdict.InsufficientInfo,
                "storage.rule.capacity.layout-insufficient",
                "The selected layout does not provide enough information to calculate a safe simulated maximum.",
                Source: WindowsTierSupportedSize);
        }
    }

    private static long RequestedTierSize(
        StorageSnapshot snapshot,
        StorageTierInfo tier,
        SimulationEditRequest request)
    {
        var media = EditWorkspace.NormalizeMedia(tier.MediaType);
        var setting = media switch
        {
            "HDD" => request.CapacityResiliency ?? tier.ResiliencySettingName,
            "SCM" => request.ScmResiliency ?? tier.ResiliencySettingName,
            _ => request.PerformanceResiliency ?? tier.ResiliencySettingName
        };
        var copies = media switch
        {
            "HDD" => tier.NumberOfDataCopies ?? 1,
            "SCM" => request.ScmDataCopies ?? tier.NumberOfDataCopies ?? 1,
            _ => request.PerformanceDataCopies ?? tier.NumberOfDataCopies ?? 1
        };
        var columns = media == "HDD" ? request.CapacityColumns ?? tier.NumberOfColumns : tier.NumberOfColumns;
        var tolerated = media == "HDD"
            ? request.CapacityToleratedFailures ?? tier.PhysicalDiskRedundancy ?? 1
            : tier.PhysicalDiskRedundancy ?? 0;
        var useMaximum = media switch
        {
            "HDD" => request.CapacityUseMaximum,
            "SCM" => request.ScmUseMaximum,
            _ => request.PerformanceUseMaximum
        };
        if (useMaximum)
        {
            return ConservativeCapacity.PlanLogicalUpperBound(
                tier.MemberPhysicalDiskIds
                    .Select(id => snapshot.PhysicalDisks.First(item => item.StableId == id).Size)
                    .ToArray(),
                setting,
                copies,
                columns,
                setting.Equals("Parity", StringComparison.OrdinalIgnoreCase) ? tolerated : 0,
                media switch
                {
                    "HDD" => request.CapacityInterleaveBytes ?? tier.Interleave ?? 65536,
                    "SCM" => request.ScmInterleaveBytes ?? tier.Interleave ?? 65536,
                    _ => request.PerformanceInterleaveBytes ?? tier.Interleave ?? 65536
                }).AlignedLogicalBytes;
        }

        return media switch
        {
            "HDD" => request.CapacitySizeBytes ?? tier.Size,
            "SCM" => request.ScmSizeBytes ?? tier.Size,
            _ => request.PerformanceSizeBytes ?? tier.Size
        };
    }

    private static bool TierLayoutChanges(StorageTierInfo tier, SimulationEditRequest request)
    {
        var media = EditWorkspace.NormalizeMedia(tier.MediaType);
        return media switch
        {
            "HDD" => Different(request.CapacityResiliency, tier.ResiliencySettingName)
                || Different(request.CapacityInterleaveBytes, tier.Interleave)
                || Different(request.CapacitySizeBytes, tier.Size)
                || Different(request.CapacityColumns, tier.NumberOfColumns)
                || Different(request.CapacityToleratedFailures, tier.PhysicalDiskRedundancy),
            "SCM" => Different(request.ScmResiliency, tier.ResiliencySettingName)
                || Different(request.ScmInterleaveBytes, tier.Interleave)
                || Different(request.ScmSizeBytes, tier.Size)
                || Different(request.ScmDataCopies, tier.NumberOfDataCopies),
            _ => Different(request.PerformanceResiliency, tier.ResiliencySettingName)
                || Different(request.PerformanceInterleaveBytes, tier.Interleave)
                || Different(request.PerformanceSizeBytes, tier.Size)
                || Different(request.PerformanceDataCopies, tier.NumberOfDataCopies)
        };
    }

    private static bool TierSpecificationChanges(StorageTierInfo tier, SimulationEditRequest request)
    {
        var media = EditWorkspace.NormalizeMedia(tier.MediaType);
        return media switch
        {
            "HDD" => Different(request.CapacityResiliency, tier.ResiliencySettingName)
                || Different(request.CapacityInterleaveBytes, tier.Interleave)
                || Different(request.CapacityColumns, tier.NumberOfColumns)
                || Different(request.CapacityToleratedFailures, tier.PhysicalDiskRedundancy),
            "SCM" => Different(request.ScmResiliency, tier.ResiliencySettingName)
                || Different(request.ScmInterleaveBytes, tier.Interleave)
                || Different(request.ScmDataCopies, tier.NumberOfDataCopies),
            _ => Different(request.PerformanceResiliency, tier.ResiliencySettingName)
                || Different(request.PerformanceInterleaveBytes, tier.Interleave)
                || Different(request.PerformanceDataCopies, tier.NumberOfDataCopies)
        };
    }

    private static bool UsesMaximumCapacity(StorageTierInfo tier, SimulationEditRequest request) =>
        EditWorkspace.NormalizeMedia(tier.MediaType) switch
        {
            "HDD" => request.CapacityUseMaximum,
            "SCM" => request.ScmUseMaximum,
            _ => request.PerformanceUseMaximum
        };

    private static bool Different<T>(T? requested, T? current) where T : struct =>
        requested.HasValue && !EqualityComparer<T>.Default.Equals(requested.Value, current.GetValueOrDefault());

    private static bool Different(string? requested, string current) =>
        requested is not null && !requested.Equals(current, StringComparison.OrdinalIgnoreCase);

    private static int CopiesFor(string resiliency) =>
        string.Equals(resiliency, "Simple", StringComparison.OrdinalIgnoreCase) ? 1
        : string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase) ? 1
        : 2;

    private static bool IsCreateFileSystem(string fileSystem) =>
        fileSystem is "NTFS" or "REFS" or "EXFAT";

    private static StorageRuleDecision Allow(string code, string? source = null) =>
        new(StorageRuleVerdict.Allow, code, string.Empty, Source: source);

    private static StorageRuleDecision AllowNoOp(string code) =>
        new(
            StorageRuleVerdict.Allow,
            code,
            "Simulated no-op. No rearrangement or performance improvement is claimed.");

    private static StorageRuleDecision Deny(string code, string message, string? objectId = null) =>
        new(StorageRuleVerdict.Deny, code, message, objectId);
}
