using WinPool.Domain;

namespace WinPool.Application;

public static class StorageEditRules
{
    /// <summary>
    /// The simulated partition editor uses the same 1 MiB boundary convention
    /// as its first-partition placement. It is a stable modeling granularity,
    /// not a claim about a Windows disk's physical-sector requirement.
    /// </summary>
    public const long PartitionResizeAlignmentBytes = 1024L * 1024;

    public const string WindowsPhysicalDiskUsage =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk";
    public const string WindowsPartition =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition";
    public const string WindowsVolume =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-volume";
    public const string WindowsTierSupportedSize =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagetier-getsupportedsize";
    public const string WindowsPartitionResize =
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition-resize";
    public const string WindowsExtendBasicVolume =
        "https://learn.microsoft.com/en-us/windows-server/storage/disk-management/extend-a-basic-volume";
    public const string WindowsShrinkBasicVolume =
        "https://learn.microsoft.com/en-us/windows-server/storage/disk-management/shrink-a-basic-volume";

    /// <summary>
    /// Determines whether a simulated partition is eligible for deletion based
    /// only on the selected partition's boot and system flags. Callers retain
    /// their own simulation-mode and online-disk gates.
    /// </summary>
    public static bool CanDeleteSimulatedPartition(PartitionInfo? partition) =>
        partition is { IsBoot: false, IsSystem: false };

    public static StorageRuleDecision Evaluate(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        var sourceDecision = SourceSafetyDecision(snapshot, request);
        if (sourceDecision is not null)
        {
            return sourceDecision;
        }
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
            SimulationEditKind.ExtendPartition => TryBuildPartitionResizePlan(snapshot, request, extend: true, out _),
            SimulationEditKind.ShrinkPartition => TryBuildPartitionResizePlan(snapshot, request, extend: false, out _),
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

    /// <summary>
    /// Reports whether the selected simulated partition has enough reliable,
    /// modeled geometry to accept a target capacity. This does not claim a
    /// Windows Get-PartitionSupportedSize result.
    /// </summary>
    public static PartitionResizeCapability GetPartitionResizeCapability(
        StorageSnapshot snapshot,
        string targetProviderKey,
        SimulationEditKind kind)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetProviderKey);
        if (kind is not (SimulationEditKind.ExtendPartition or SimulationEditKind.ShrinkPartition))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A partition resize operation is required.");
        }

        var request = new SimulationEditRequest(kind, targetProviderKey);
        var sourceDecision = SourceSafetyDecision(snapshot, request);
        if (sourceDecision is not null)
        {
            return new(sourceDecision, null, null, null);
        }

        var capability = GetPartitionResizeCapabilityCore(snapshot, targetProviderKey, out var context);
        if (capability.Decision.Verdict != StorageRuleVerdict.Allow || context is null)
        {
            return capability;
        }

        var extend = kind == SimulationEditKind.ExtendPartition;
        var fileSystemDecision = EvaluateResizeFileSystem(context, extend);
        if (fileSystemDecision.Verdict != StorageRuleVerdict.Allow)
        {
            return capability with { Decision = fileSystemDecision };
        }

        if (!HasAlignedResizeTarget(context, extend))
        {
            return capability with
            {
                Decision = Deny(
                    extend
                        ? "storage.rule.resize.extend-no-aligned-target"
                        : "storage.rule.resize.shrink-no-aligned-target",
                    extend
                        ? "No larger 1 MiB-aligned target capacity fits the modeled partition geometry."
                        : "No smaller 1 MiB-aligned target capacity preserves the modeled data and geometry.",
                    context.Partition.StableId)
            };
        }

        return capability;
    }

    private static StorageRuleDecision? SourceSafetyDecision(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        if (request.Kind is not (SimulationEditKind.Rename or SimulationEditKind.OptimizePool or SimulationEditKind.OptimizeDrive)
            && HasUnsupportedRelatedValue(snapshot, request))
        {
            return Deny(
                "storage.rule.source-value-out-of-range",
                "A related source value exceeds the supported editing range. Inspect its original value in source details.");
        }

        var unavailable = RequiredSourceIssue(snapshot, request);
        return unavailable is null
            ? null
            : new StorageRuleDecision(
                StorageRuleVerdict.InsufficientInfo,
                "storage.rule.source-field-unavailable",
                $"{unavailable.FieldName} is unavailable or conflicting ({unavailable.State}, {unavailable.Reason}); this operation requires reliable source information.",
                unavailable.ObjectId);
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
            or SimulationEditKind.ConvertDisk or SimulationEditKind.SetDiskOffline or SimulationEditKind.ExtendPartition
            or SimulationEditKind.ShrinkPartition;
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
            if (request.Kind is SimulationEditKind.ExtendPartition or SimulationEditKind.ShrinkPartition)
            {
                Need(target, "Type", "Size", "Offset", "SizeRemaining", "FileSystem");
                Need(disk?.StableId, "Size");
                foreach (var volume in snapshot.Volumes.Where(item => item.PartitionStableId == target))
                {
                    Need(volume.StableId, "Size", "SizeRemaining", "FileSystem");
                }
                foreach (var item in snapshot.Partitions.Where(item => item.OsDiskStableId == disk?.StableId))
                {
                    Need(item.StableId, "Size", "Offset");
                }
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
        (SimulationEditKind.CreatePartition, "supported: four fixed GPT partition kinds; new starts and lengths use the simulated 1 MiB grid"),
        (SimulationEditKind.ExtendPartition, "supported: simulated Primary/BasicData, 1 MiB-aligned target capacity, modeled geometry and NTFS/ReFS/RAW direction rules; not a Windows supported-size result"),
        (SimulationEditKind.ShrinkPartition, "supported: simulated Primary/BasicData, 1 MiB-aligned target capacity, modeled geometry and NTFS/RAW direction rules; not a Windows supported-size result"),
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

        if (!CanDeleteSimulatedPartition(partition))
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

        if (request.CreateMsr == true && disk.Size < 17L * 1024 * 1024)
        {
            return Deny(
                "storage.rule.initialize.msr-capacity",
                "The disk is too small for the simulated 16 MiB MSR at the 1 MiB offset.",
                disk.StableId);
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

        if (request.OffsetBytes is < 0)
        {
            return Deny("storage.rule.create-partition.offset", "The selected unallocated region was not found.");
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

        var geometry = EditWorkspace.GetPartitionCreateGeometry(
            snapshot,
            disk.StableId,
            request.OffsetBytes);
        if (!geometry.CanCreate
            || geometry.MaximumSizeBytes is not long maximumSize)
        {
            return Deny(
                "storage.rule.create-partition.no-aligned-space",
                geometry.UnavailableReason ?? "No unallocated region can hold a 1 MiB-aligned partition.",
                disk.StableId);
        }

        if (request.SizeBytes is long requestedSize && requestedSize > 0)
        {
            if (requestedSize % EditWorkspace.PartitionCreateAlignmentBytes != 0)
            {
                return Deny(
                    "storage.rule.create-partition.size-alignment",
                    "A new partition length must be a whole number of MiB in the simulation.",
                    disk.StableId);
            }

            if (requestedSize > maximumSize)
            {
                return Deny(
                    "storage.rule.create-partition.size-boundary",
                    "The requested partition length exceeds the aligned capacity available in the selected unallocated region.",
                    disk.StableId);
            }
        }

        return Allow("storage.rule.create-partition", WindowsPartition);
    }

    internal static StorageRuleDecision TryBuildPartitionResizePlan(
        StorageSnapshot snapshot,
        SimulationEditRequest request,
        bool extend,
        out PartitionResizePlan? plan)
    {
        plan = null;
        var sourceDecision = SourceSafetyDecision(snapshot, request);
        if (sourceDecision is not null)
        {
            return sourceDecision;
        }

        var capability = GetPartitionResizeCapabilityCore(snapshot, request.TargetProviderKey, out var context);
        if (capability.Decision.Verdict != StorageRuleVerdict.Allow || context is null)
        {
            return capability.Decision;
        }

        var fileSystemDecision = EvaluateResizeFileSystem(context, extend);
        if (fileSystemDecision.Verdict != StorageRuleVerdict.Allow)
        {
            return fileSystemDecision;
        }

        if (request.SizeBytes is not long targetSize || targetSize <= 0)
        {
            return Deny(
                "storage.rule.resize.target-size",
                "A positive target partition capacity is required. The value is a total target size, not an increment.",
                context.Partition.StableId);
        }

        if (targetSize % PartitionResizeAlignmentBytes != 0)
        {
            return Deny(
                "storage.rule.resize.target-alignment",
                "The target partition capacity must be aligned to 1 MiB in the simulation.",
                context.Partition.StableId);
        }

        if (extend && targetSize <= context.Partition.Size)
        {
            return Deny(
                "storage.rule.resize.extend-target",
                "Extending requires a target capacity larger than the current partition size.",
                context.Partition.StableId);
        }

        if (!extend && targetSize >= context.Partition.Size)
        {
            return Deny(
                "storage.rule.resize.shrink-target",
                "Shrinking requires a target capacity smaller than the current partition size.",
                context.Partition.StableId);
        }

        if (targetSize < context.MinimumTargetSizeBytes)
        {
            return Deny(
                "storage.rule.resize.used-space",
                "The target capacity is smaller than the modeled used data or file-system reserve.",
                context.Partition.StableId);
        }

        if (targetSize > context.MaximumTargetSizeBytes)
        {
            return Deny(
                "storage.rule.resize.geometry-boundary",
                "The target capacity would exceed the modeled disk boundary or overlap the next partition.",
                context.Partition.StableId);
        }

        try
        {
            var change = checked(targetSize - context.Partition.Size);
            // Partition free space is projected from the linked volume. An
            // unformatted partition has no filesystem free-space fact, so its
            // synthetic zero must not be treated as occupied data.
            var targetPartitionRemaining = context.Volume is null
                ? 0
                : checked(context.Volume.SizeRemaining + change);

            long? targetVolumeSize = null;
            long? targetVolumeRemaining = null;
            if (context.Volume is { } volume)
            {
                targetVolumeSize = checked(volume.Size + change);
                targetVolumeRemaining = checked(volume.SizeRemaining + change);
                if (targetVolumeSize <= 0
                    || targetVolumeRemaining < 0
                    || targetVolumeRemaining > targetVolumeSize)
                {
                    return Deny(
                        "storage.rule.resize.volume-free-space",
                        "The target capacity cannot preserve the modeled volume used and free space.",
                        context.Partition.StableId);
                }
            }

            plan = new(
                context.Partition,
                context.Volume,
                targetSize,
                targetPartitionRemaining,
                targetVolumeSize,
                targetVolumeRemaining);
            return capability.Decision;
        }
        catch (OverflowException)
        {
            return Deny(
                "storage.rule.resize.numeric-overflow",
                "The target capacity cannot be represented safely by the simulated partition geometry.",
                context.Partition.StableId);
        }
    }

    private static PartitionResizeCapability GetPartitionResizeCapabilityCore(
        StorageSnapshot snapshot,
        string targetProviderKey,
        out PartitionResizeContext? context)
    {
        context = null;
        var partition = snapshot.Partitions.FirstOrDefault(item =>
            item.StableId.Equals(targetProviderKey, StringComparison.OrdinalIgnoreCase));
        if (partition is null)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.missing-partition",
                "The selected partition was not found."));
        }

        if (IsReservedPartitionType(partition.Type))
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.reserved-partition",
                "EFI, Microsoft Reserved, and recovery partitions are outside the simulated resize range.",
                partition.StableId));
        }

        if (!IsResizablePartitionType(partition.Type))
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.partition-type",
                "Only Primary or BasicData partitions can be resized in the simulation.",
                partition.StableId));
        }

        var disk = snapshot.OsDisks.FirstOrDefault(item =>
            item.StableId.Equals(partition.OsDiskStableId, StringComparison.OrdinalIgnoreCase));
        if (disk is null)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.missing-disk",
                "The selected partition has no modeled OS disk, so its resize boundary is unknown.",
                partition.StableId));
        }

        if (disk.IsOffline)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.offline",
                "An offline simulated disk cannot resize a partition.",
                partition.StableId));
        }

        var linkedVolumes = snapshot.Volumes
            .Where(item => item.PartitionStableId is not null
                && item.PartitionStableId.Equals(partition.StableId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (linkedVolumes.Length > 1)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.ambiguous-volume",
                "More than one volume is linked to the selected partition, so its file-system capacity is ambiguous.",
                partition.StableId));
        }

        var volume = linkedVolumes.SingleOrDefault();
        var fileSystem = (volume?.FileSystem ?? partition.FileSystem ?? string.Empty).Trim();

        if (disk.Size <= 0
            || !TryRangeEnd(partition.Offset, partition.Size, out var partitionEnd)
            || partitionEnd > disk.Size
            || (volume is null && !HasValidRemaining(partition.Size, partition.SizeRemaining)))
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.partition-geometry",
                "The selected partition or disk has invalid modeled capacity, offset, or free-space data.",
                partition.StableId));
        }

        if (volume is not null
            && (volume.Size <= 0
                || volume.Size > partition.Size
                || !HasValidRemaining(volume.Size, volume.SizeRemaining)))
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.volume-geometry",
                "The linked volume has invalid modeled capacity or free-space data.",
                partition.StableId));
        }

        var maximumEnd = disk.Size;
        foreach (var sibling in snapshot.Partitions.Where(item =>
                     item.OsDiskStableId is not null
                     && item.OsDiskStableId.Equals(disk.StableId, StringComparison.OrdinalIgnoreCase)
                     && !item.StableId.Equals(partition.StableId, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryRangeEnd(sibling.Offset, sibling.Size, out var siblingEnd) || siblingEnd > disk.Size)
            {
                return ResizeCapability(Deny(
                    "storage.rule.resize.sibling-geometry",
                    "A neighboring partition has invalid modeled capacity or offset, so resize bounds are unknown.",
                    partition.StableId));
            }

            if (RangesOverlap(partition.Offset, partitionEnd, sibling.Offset, siblingEnd))
            {
                return ResizeCapability(Deny(
                    "storage.rule.resize.overlapping-layout",
                    "The existing modeled partition layout overlaps, so resize is not applied.",
                    partition.StableId));
            }

            if (sibling.Offset > partition.Offset)
            {
                maximumEnd = Math.Min(maximumEnd, sibling.Offset);
            }
        }

        long minimum;
        long maximum;
        try
        {
            // Filesystem capacity may legitimately be smaller than its
            // containing partition. Both are moved by the same delta, retaining
            // that difference and modeled used bytes instead of rejecting
            // imported source facts merely because the two capacities are not
            // equal.
            var volumeMinimum = volume is null
                ? 1
                : Math.Max(
                    checked(partition.Size - volume.SizeRemaining),
                    checked(partition.Size - volume.Size + 1));
            minimum = Math.Max(1, volumeMinimum);
            maximum = checked(maximumEnd - partition.Offset);
        }
        catch (OverflowException)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.numeric-overflow",
                "The simulated partition geometry cannot be calculated safely.",
                partition.StableId));
        }

        if (minimum > partition.Size || maximum < partition.Size || maximum < minimum)
        {
            return ResizeCapability(Deny(
                "storage.rule.resize.geometry-range",
                "The modeled partition has no safe target capacity range.",
                partition.StableId));
        }

        context = new(partition, disk, volume, fileSystem, minimum, maximum);
        return new(Allow("storage.rule.resize.simulated-geometry", WindowsPartitionResize), partition.Size, minimum, maximum);
    }

    private static PartitionResizeCapability ResizeCapability(StorageRuleDecision decision) =>
        new(decision, null, null, null);

    private static bool IsResizablePartitionType(string? value) =>
        string.Equals(value, "Primary", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "BasicData", StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedPartitionType(string? value) =>
        string.Equals(value, "EfiSystem", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MicrosoftReserved", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "WindowsRecovery", StringComparison.OrdinalIgnoreCase);

    private static StorageRuleDecision EvaluateResizeFileSystem(
        PartitionResizeContext context,
        bool extend)
    {
        var rawOrUnformatted = IsRawOrUnformattedFileSystem(context.FileSystem);
        var ntfs = context.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        var refs = context.FileSystem.Equals("REFS", StringComparison.OrdinalIgnoreCase);
        var supported = extend
            ? rawOrUnformatted || ntfs || refs
            : rawOrUnformatted || ntfs;
        if (supported)
        {
            var source = rawOrUnformatted
                ? WindowsPartitionResize
                : extend
                    ? WindowsExtendBasicVolume
                    : WindowsShrinkBasicVolume;
            return Allow(
                extend
                    ? "storage.rule.resize.extend-filesystem"
                    : "storage.rule.resize.shrink-filesystem",
                source);
        }

        return Deny(
            extend
                ? "storage.rule.resize.extend-filesystem"
                : "storage.rule.resize.shrink-filesystem",
            extend
                ? "Only NTFS, ReFS, or RAW/unformatted normal data partitions can be extended in the simulation."
                : "Only NTFS or RAW/unformatted normal data partitions can be shrunk in the simulation.",
            context.Partition.StableId);
    }

    private static bool IsRawOrUnformattedFileSystem(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Equals("RAW", StringComparison.OrdinalIgnoreCase);

    private static bool HasAlignedResizeTarget(PartitionResizeContext context, bool extend)
    {
        if (extend)
        {
            if (context.Partition.Size == long.MaxValue
                || !TryAlignUp(context.Partition.Size + 1, out var minimumLargerTarget))
            {
                return false;
            }

            return minimumLargerTarget <= context.MaximumTargetSizeBytes;
        }

        if (context.Partition.Size <= 1)
        {
            return false;
        }

        var maximumSmallerTarget = AlignDown(context.Partition.Size - 1);
        return maximumSmallerTarget > 0 && maximumSmallerTarget >= context.MinimumTargetSizeBytes;
    }

    private static bool TryAlignUp(long value, out long aligned)
    {
        aligned = 0;
        if (value < 0)
        {
            return false;
        }

        var remainder = value % PartitionResizeAlignmentBytes;
        if (remainder == 0)
        {
            aligned = value;
            return true;
        }

        try
        {
            aligned = checked(value + PartitionResizeAlignmentBytes - remainder);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long AlignDown(long value) =>
        value - value % PartitionResizeAlignmentBytes;

    private static bool HasValidRemaining(long size, long remaining) =>
        size > 0 && remaining >= 0 && remaining <= size;

    private static bool TryRangeEnd(long offset, long size, out long end)
    {
        end = 0;
        if (offset < 0 || size <= 0)
        {
            return false;
        }

        try
        {
            end = checked(offset + size);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool RangesOverlap(long firstStart, long firstEnd, long secondStart, long secondEnd) =>
        firstStart < secondEnd && secondStart < firstEnd;

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

/// <summary>
/// A modeled target-capacity range for a simulated partition. The range is
/// derived from the persisted geometry and volume accounting only; it is not a
/// Windows supported-size probe.
/// </summary>
public sealed record PartitionResizeCapability(
    StorageRuleDecision Decision,
    long? CurrentSizeBytes,
    long? MinimumTargetSizeBytes,
    long? MaximumTargetSizeBytes);

internal sealed record PartitionResizeContext(
    PartitionInfo Partition,
    OsDiskInfo Disk,
    VolumeInfo? Volume,
    string FileSystem,
    long MinimumTargetSizeBytes,
    long MaximumTargetSizeBytes);

internal sealed record PartitionResizePlan(
    PartitionInfo Partition,
    VolumeInfo? Volume,
    long TargetPartitionSizeBytes,
    long TargetPartitionSizeRemainingBytes,
    long? TargetVolumeSizeBytes,
    long? TargetVolumeSizeRemainingBytes);
