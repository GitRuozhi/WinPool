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
        SimulationOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            SimulationOperationKind.Rename => Allow("storage.rule.rename"),
            SimulationOperationKind.ChangeDriveLetter => EvaluateDriveLetter(snapshot, request),
            SimulationOperationKind.FormatPartition => EvaluateFormat(snapshot, request),
            SimulationOperationKind.DeletePartition => EvaluateDeletePartition(snapshot, request),
            SimulationOperationKind.SetDiskOffline => Deny(
                "storage.rule.offline.transient-only",
                "Online and offline are temporary editor states and cannot be persisted."),
            SimulationOperationKind.InitializeDisk => EvaluateInitialize(snapshot, request),
            SimulationOperationKind.ConvertDisk => EvaluateConvert(snapshot, request),
            SimulationOperationKind.CreatePartition => EvaluateCreatePartition(snapshot, request),
            SimulationOperationKind.ExtendPartition => UnsupportedResize("extend"),
            SimulationOperationKind.ShrinkPartition => UnsupportedResize("shrink"),
            SimulationOperationKind.CreateStoragePool => EvaluateCreatePool(snapshot, request),
            SimulationOperationKind.CreateTieredPool => EvaluateCreateTieredPool(snapshot, request),
            SimulationOperationKind.CreateVirtualDisk => EvaluateCreateVirtualDisk(snapshot, request),
            SimulationOperationKind.DeleteVirtualDisk => EvaluateDeleteVirtualDisk(snapshot, request),
            SimulationOperationKind.UpdateStoragePool => EvaluateUpdatePool(snapshot, request),
            SimulationOperationKind.DissolveStoragePool => EvaluateDissolve(snapshot, request),
            SimulationOperationKind.DeleteEmptyStoragePool => EvaluateDeleteEmptyPool(snapshot, request),
            SimulationOperationKind.MovePhysicalDisk => EvaluateMove(snapshot, request),
            SimulationOperationKind.EvictPhysicalDiskFromTiers => EvaluateEvict(snapshot, request),
            SimulationOperationKind.SetDiskUsage => EvaluateUsage(snapshot, request),
            SimulationOperationKind.OptimizePool => AllowNoOp("storage.rule.optimize-pool.simulated-noop"),
            SimulationOperationKind.OptimizeDrive => AllowNoOp("storage.rule.optimize-drive.simulated-noop"),
            _ => Deny("storage.rule.unknown-operation", $"Operation {request.Kind} is not recognized.")
        };
    }

    public static IReadOnlyList<(SimulationOperationKind Kind, string Support)> OperationMatrix() =>
    [
        (SimulationOperationKind.Rename, "supported: object friendly name / volume label"),
        (SimulationOperationKind.ChangeDriveLetter, "supported: unused letter, volume present"),
        (SimulationOperationKind.FormatPartition, "supported: NTFS, ReFS, exFAT"),
        (SimulationOperationKind.DeletePartition, "supported: any existing partition"),
        (SimulationOperationKind.SetDiskOffline, "not_supported: editor-session state only"),
        (SimulationOperationKind.InitializeDisk, "supported: GPT only; MBR initialize denied"),
        (SimulationOperationKind.ConvertDisk, "supported: destructive MBR data disk to GPT"),
        (SimulationOperationKind.CreatePartition, "supported: four fixed GPT partition kinds"),
        (SimulationOperationKind.ExtendPartition, "not_supported: no Windows supported-size evidence"),
        (SimulationOperationKind.ShrinkPartition, "not_supported: no Windows supported-size evidence"),
        (SimulationOperationKind.CreateStoragePool, "supported: primordial data members"),
        (SimulationOperationKind.CreateTieredPool, "supported: Simple/Mirror×2/Parity with legal disk counts"),
        (SimulationOperationKind.CreateVirtualDisk, "supported: at most one new VD per pool; Fixed estimate"),
        (SimulationOperationKind.DeleteVirtualDisk, "supported: explicit delete"),
        (SimulationOperationKind.UpdateStoragePool, "supported: unused-capacity layout fields"),
        (SimulationOperationKind.DissolveStoragePool, "supported: non-primordial"),
        (SimulationOperationKind.DeleteEmptyStoragePool, "internal: remove an empty pool after ordered dissolve steps"),
        (SimulationOperationKind.MovePhysicalDisk, "supported: primordial or same-media tier"),
        (SimulationOperationKind.EvictPhysicalDiskFromTiers, "supported: keep in pool, drop tier membership"),
        (SimulationOperationKind.SetDiskUsage, "supported: Retired/Hot Spare when pool has remaining data members"),
        (SimulationOperationKind.OptimizePool, "simulated no-op; does not claim a measured result"),
        (SimulationOperationKind.OptimizeDrive, "simulated no-op; does not claim a measured result")
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
        SimulationOperationRequest request)
    {
        if (snapshot.VolumeForPartition(request.TargetStableId) is null)
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
            && snapshot.VolumeForPartition(request.TargetStableId)?.DriveLetter != letter)
        {
            return Deny("storage.rule.drive-letter.conflict", $"Drive letter {letter}: is already in use.");
        }

        return Allow("storage.rule.drive-letter");
    }

    private static StorageRuleDecision EvaluateFormat(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
    {
        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var partition = snapshot.Partitions.FirstOrDefault(item => item.StableId == request.TargetStableId);
        if (partition is null)
        {
            return Deny("storage.rule.delete-partition.missing", "The selected partition was not found.");
        }

        return Allow("storage.rule.delete-partition", WindowsPartition);
    }

    private static StorageRuleDecision EvaluateOffline(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
    {
        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var style = request.Name?.Trim().ToUpperInvariant();
        if (style == "MBR")
        {
            return Deny("storage.rule.initialize.mbr", "New MBR partition tables are outside the product create range.");
        }

        if (style != "GPT")
        {
            return Deny("storage.rule.initialize.style", "Only GPT initialization is supported.");
        }

        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var style = request.Name?.Trim().ToUpperInvariant();
        if (style != "GPT")
        {
            return Deny("storage.rule.convert.gpt-only", "Conversion is limited to GPT. MBR conversion is not offered.");
        }

        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var disk = snapshot.OsDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request) =>
        EvaluateMembers(snapshot, request.MemberDiskIds, requirePrimordial: true);

    private static StorageRuleDecision EvaluateCreateTieredPool(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
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
        SimulationOperationRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var vdisk = snapshot.VirtualDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
        return vdisk is null
            ? Deny("storage.rule.delete-virtual-disk.missing", "The simulated virtual disk was not found.")
            : Allow("storage.rule.delete-virtual-disk");
    }

    private static StorageRuleDecision EvaluateUpdatePool(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetStableId);
        if (pool is null || pool.IsPrimordial)
        {
            return Deny("storage.rule.dissolve.invalid", "The primordial pool cannot be dissolved.");
        }

        return Allow("storage.rule.dissolve");
    }

    private static StorageRuleDecision EvaluateDeleteEmptyPool(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
    {
        var pool = snapshot.StoragePools.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
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
        SimulationOperationRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
        return disk is null
            ? Deny("storage.rule.evict.missing", "The selected physical disk was not found.")
            : Allow("storage.rule.evict", WindowsPhysicalDiskUsage);
    }

    private static StorageRuleDecision EvaluateUsage(
        StorageSnapshot snapshot,
        SimulationOperationRequest request)
    {
        var disk = snapshot.PhysicalDisks.FirstOrDefault(item => item.StableId == request.TargetStableId);
        if (disk is null)
        {
            return Deny("storage.rule.usage.missing", "The selected physical disk was not found.");
        }

        var layer = request.Name?.Trim() ?? string.Empty;
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
        SimulationOperationRequest request)
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

    private static bool TierLayoutChanges(StorageTierInfo tier, SimulationOperationRequest request)
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

    private static bool TierSpecificationChanges(StorageTierInfo tier, SimulationOperationRequest request)
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

    private static bool UsesMaximumCapacity(StorageTierInfo tier, SimulationOperationRequest request) =>
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
