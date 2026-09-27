using System.Globalization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Rebuilds an App proposal from a fresh local topology. The App supplies a
/// requested operation and numeric values, never Windows identities, trusted
/// conditions, impact text or provider capability evidence.
/// </summary>
public sealed class WindowsRealOperationPlanner
{
    private readonly WindowsRealStorageTopologyReader topologyReader;
    private readonly IPartitionSupportedSizeReader partitionSizes;
    private readonly IVirtualDiskCreationSizeReader virtualDiskSizes;
    private readonly IWindowsRealStorageSafetyInspector safetyInspector;
    private readonly IPrivilegeService privilege;
    private readonly TimeProvider timeProvider;

    public WindowsRealOperationPlanner(
        WindowsRealStorageTopologyReader? topologyReader = null,
        IPartitionSupportedSizeReader? partitionSizes = null,
        IPrivilegeService? privilege = null,
        TimeProvider? timeProvider = null,
        IWindowsRealStorageSafetyInspector? safetyInspector = null,
        IVirtualDiskCreationSizeReader? virtualDiskSizes = null)
    {
        this.topologyReader = topologyReader ?? new WindowsRealStorageTopologyReader();
        this.partitionSizes = partitionSizes ?? new WindowsPartitionSupportedSizeReader();
        this.virtualDiskSizes = virtualDiskSizes ?? new WindowsVirtualDiskCreationSizeReader();
        this.safetyInspector = safetyInspector ?? new WindowsRealStorageSafetyInspector();
        this.privilege = privilege ?? new WindowsPrivilegeService();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationPlan> PrepareAsync(
        RealOperationIntentRequest proposal,
        TrustedRealSession session,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(session);
        if (privilege.Current != PrivilegeState.Administrator || !session.IsArmed)
            throw new UnauthorizedAccessException("An armed elevated Agent is required.");

        if (proposal.Intent == OperationIntent.ClearDisk &&
            (proposal.Targets is null || proposal.Steps is null ||
             proposal.Targets.Count != 1 ||
             proposal.Targets[0].Kind != StorageObjectKind.OsDisk ||
             proposal.Steps.Count != 1 ||
             proposal.Steps[0].Command is not ClearDiskCommand clear ||
             clear.Disk.Existing != proposal.Targets[0] ||
             proposal.ExpectedFinalState != RealOperationValidator.ClearDiskExpectedFinalState))
            throw new ArgumentException("A standalone clear requires one exact existing OS disk and the fixed RAW end state.");

        // Static validation is necessary, but not sufficient: every target and
        // mutable condition is resolved again from this newly collected report.
        RealOperationValidator.Validate(proposal);
        var topology = await topologyReader.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (proposal.SystemId != topology.SystemId)
            throw new InvalidDataException("The proposal does not name the current local system.");

        var proposedClosure = topology.RequireSinglePhysicalClosure(proposal.Targets);
        var physicalTarget = new StorageObjectId(
            topology.SystemId, StorageObjectKind.PhysicalDisk,
            proposedClosure.PhysicalDiskId);
        var trustedTargets = proposal.Targets.Contains(physicalTarget)
            ? proposal.Targets.ToArray()
            : proposal.Targets.Append(physicalTarget).ToArray();
        var closure = topology.RequireSinglePhysicalClosure(trustedTargets);
        var normalized = new List<RealOperationStep>(proposal.Steps.Count);
        foreach (var step in proposal.Steps)
        {
            var target = WindowsRealStorageTargetBuilder.GetReference(step.Command);
            if (target.Existing is { } existing)
            {
                // Resolve and verify the exact fresh source even when its
                // opaque UI ID happens to match a prior report.
                _ = WindowsRealStorageTargetBuilder.Build(
                    topology, target, new Dictionary<string, string>());
            }
            await ValidateCurrentStepAsync(
                topology, closure, proposal, step, cancellationToken)
                .ConfigureAwait(false);
            normalized.Add(step with
            {
                BeforeCondition = DescribeBefore(step.Command, closure),
                AfterCondition = DescribeAfter(step.Command),
                DataLoss = DescribeLoss(step.Command, closure, topology.Snapshot),
                SupportEvidence = "fresh-msft-storage:" + closure.Fingerprint
            });
        }

        var trusted = new RealOperationIntentRequest(
            proposal.Intent, topology.SystemId, trustedTargets,
            normalized, proposal.Intent == OperationIntent.ClearDisk
                ? RealOperationValidator.ClearDiskExpectedFinalState
                : string.Join("; ", normalized.Select(item => item.AfterCondition)));
        RealOperationValidator.Validate(trusted);
        var now = timeProvider.GetUtcNow();
        var environment = new EnvironmentProfile(
            EnvironmentId.New(), EnvironmentKind.LocalMachine,
            topology.MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
            IsUserProvidedDisposableEnvironment: false, now);
        return RealOperationPlanFactory.Create(
            trusted, operationId, environment, session,
            closure.Fingerprint, closure.Fingerprint,
            closure.PhysicalMemberFingerprint,
            "Windows Storage Management local provider; OS "
                + topology.Snapshot.Computer.OsBuild + "; physical member "
                + closure.PhysicalDiskId,
            now, now.Add(InMemoryOperationAuthority.DefaultLifetime));
    }

    internal async Task ValidateCurrentStepAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealOperationIntentRequest proposal,
        RealOperationStep step,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? verifiedStepOutputs = null)
    {
        var snapshot = topology.Snapshot;
        verifiedStepOutputs ??= new Dictionary<string, string>();
        await safetyInspector.ValidateAsync(topology, closure, step.Command, cancellationToken)
            .ConfigureAwait(false);
        switch (step.Command)
        {
            case SetDiskOnlineCommand value:
            {
                var disk = ExistingDisk(snapshot, value.Disk);
                if (disk.IsBoot || disk.IsSystem ||
                    snapshot.PhysicalDisks.Single(item => item.StableId == closure.PhysicalDiskId)
                        is { IsPageFile: true } or { IsCrashDump: true })
                    throw new InvalidDataException("The selected disk has a running-system dependency.");
                if (disk.IsOffline == !value.Online)
                    throw new InvalidDataException("The selected disk is already in the requested online state.");
                break;
            }
            case InitializeGptCommand value:
            {
                var disk = CurrentDisk(topology, value.Disk, verifiedStepOutputs);
                if (disk is not null &&
                    (!disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    || snapshot.Partitions.Any(item => item.OsDiskStableId == disk.StableId)))
                    throw new InvalidDataException("GPT initialization requires a partition-free RAW disk.");
                break;
            }
            case ClearDiskCommand value:
            {
                if (value.RemoveOem)
                    throw new NotSupportedException("Removing OEM partitions is outside this stage.");
                var disk = ExistingDisk(snapshot, value.Disk);
                var physical = snapshot.PhysicalDisks.Single(item =>
                    item.StableId == closure.PhysicalDiskId);
                if (disk.PhysicalDiskStableId != physical.StableId
                    || disk.VirtualDiskStableId is not null || disk.IsOffline
                    || disk.IsBoot || disk.IsSystem
                    || disk.PartitionStyle is not ("GPT" or "MBR")
                    || snapshot.StoragePools.Any(pool => !pool.IsPrimordial
                        && pool.MemberPhysicalDiskIds.Contains(physical.StableId)))
                    throw new InvalidDataException("Clear requires an online, directly attached, non-system basic disk outside every real pool.");
                var partitions = snapshot.Partitions.Where(item =>
                    item.OsDiskStableId == disk.StableId).ToArray();
                if (partitions.Length == 0 || partitions.Any(item =>
                        item.IsBoot || item.IsSystem
                        || Guid.TryParse(item.PartitionTypeId, out var kind)
                        && kind == Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac")))
                    throw new InvalidDataException("Clear requires known non-protected partitions; OEM/recovery removal is outside this stage.");
                break;
            }
            case CreatePartitionCommand value:
                ValidatePartitionGeometry(topology, proposal, step, value,
                    verifiedStepOutputs);
                break;
            case DeletePartitionCommand value:
            {
                var partition = ExistingPartition(snapshot, value.Partition);
                if (partition.IsBoot || partition.IsSystem)
                    throw new InvalidDataException("The selected partition is Boot or System.");
                break;
            }
            case ResizePartitionCommand value:
            {
                var partition = ExistingPartition(snapshot, value.Partition);
                if (partition.IsBoot || partition.IsSystem || !IsBasicData(partition)
                    || partition.FileSystem is not ("NTFS" or "RAW" or ""))
                    throw new InvalidDataException("Only an ordinary NTFS or unformatted data partition may be resized.");
                var target = WindowsRealStorageTargetBuilder.Build(
                    topology, value.Partition, new Dictionary<string, string>());
                var range = await partitionSizes.ReadAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (value.SizeBytes < range.MinimumBytes || value.SizeBytes > range.MaximumBytes)
                    throw new InvalidDataException("The requested size is outside the provider's current supported range.");
                if (value.SizeBytes == partition.Size)
                    throw new InvalidDataException("The requested partition size is unchanged.");
                break;
            }
            case FormatVolumeCommand value:
            {
                var partition = CurrentPartition(topology, value.Partition,
                    verifiedStepOutputs);
                if (partition is not null && (partition.IsBoot || partition.IsSystem
                    || !IsValidFormatRole(partition, value)))
                    throw new InvalidDataException("This existing partition/file-system combination is not enabled.");
                ValidateLabel(value.Label, value.FileSystem);
                break;
            }
            case SetDriveLetterCommand value:
            {
                var partition = CurrentPartition(topology, value.Partition,
                    verifiedStepOutputs);
                var current = string.IsNullOrWhiteSpace(partition?.DriveLetter)
                    ? (char?)null : char.ToUpperInvariant(partition.DriveLetter[0]);
                if (partition is null && value.PreviousLetter is not null)
                    throw new InvalidDataException("A newly created partition has no previous drive letter.");
                if (partition is not null && current != value.PreviousLetter)
                    throw new InvalidDataException("The selected partition's drive letter changed.");
                if (partition is not null && !IsBasicData(partition))
                    throw new InvalidDataException("Only BasicData partitions can receive a drive letter.");
                if (value.NewLetter is { } next && IsLetterUsed(snapshot, next))
                    throw new InvalidDataException("The requested drive letter is already in use.");
                break;
            }
            case RenameVolumeCommand value:
            {
                if (value.Volume.Existing is not null ||
                    verifiedStepOutputs.ContainsKey(value.Volume.CreatedByStep ?? string.Empty))
                {
                    var id = WindowsRealStorageTargetBuilder.ResolveId(topology,
                        value.Volume, verifiedStepOutputs);
                    var volume = snapshot.Volumes.SingleOrDefault(item =>
                        item.StableId == id.ProviderKey)
                        ?? throw new InvalidDataException("The selected volume is absent.");
                    ValidateLabel(value.Label, ParseFileSystem(volume.FileSystem));
                    if (volume.FileSystemLabel == value.Label)
                        throw new InvalidDataException("The requested volume label is unchanged.");
                }
                break;
            }
            case CreatePoolCommand value:
            {
                var physical = snapshot.PhysicalDisks.Single(item =>
                    item.StableId == closure.PhysicalDiskId);
                var nonPrimordialPool = snapshot.StoragePools.Any(pool =>
                    pool.StableId == physical.PoolStableId && !pool.IsPrimordial);
                if (physical.IsBoot || physical.IsSystem || physical.IsPageFile
                    || physical.IsCrashDump)
                    throw new InvalidDataException("The identified physical disk is not safely poolable.");
                if (!physical.CanPool || nonPrimordialPool)
                {
                    var oldPool = snapshot.StoragePools.SingleOrDefault(pool =>
                        !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Contains(physical.StableId));
                    var priorRemoval = oldPool is not null &&
                        proposal.Steps.TakeWhile(item => item.Id != step.Id)
                            .Any(item => item.Command is DeletePoolCommand deletion
                                && deletion.Pool.Existing is { } old
                                && old.ProviderKey == oldPool.StableId);
                    if (verifiedStepOutputs.Count == 0 && priorRemoval)
                        break;
                    throw new InvalidDataException("The identified physical disk is not safely poolable.");
                }
                var directDisk = snapshot.OsDisks.SingleOrDefault(item =>
                    item.PhysicalDiskStableId == physical.StableId);
                if (directDisk is not null &&
                    (!directDisk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                     || snapshot.Partitions.Any(item => item.OsDiskStableId == directDisk.StableId)))
                    throw new InvalidDataException("Pool creation requires a partition-free RAW member.");
                break;
            }
            case DeletePoolCommand value:
            {
                var pool = ExistingPool(snapshot, value.Pool);
                if (pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                    || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId)
                    throw new InvalidDataException("Only the exact single-member non-primordial pool may be deleted.");
                var prior = proposal.Steps.TakeWhile(item => item.Id != step.Id).ToArray();
                if (snapshot.VirtualDisks.Any(child => child.PoolStableId == pool.StableId
                    && !prior.Any(item => item.Command is DeleteVirtualDiskCommand deletion
                        && deletion.VirtualDisk.Existing is { } id
                        && id.ProviderKey == child.StableId))
                    || snapshot.StorageTiers.Any(child => child.PoolStableId == pool.StableId
                    && !prior.Any(item => item.Command is DeleteTierCommand deletion
                        && deletion.Tier.Existing is { } id
                        && id.ProviderKey == child.StableId)))
                    throw new InvalidDataException("Every existing pool child must have an earlier explicit removal step.");
                if (verifiedStepOutputs.Count > 0 &&
                    (snapshot.VirtualDisks.Any(child => child.PoolStableId == pool.StableId)
                     || snapshot.StorageTiers.Any(child => child.PoolStableId == pool.StableId)))
                    throw new InvalidDataException("Pool children remain after the earlier removal steps.");
                break;
            }
            case RenamePoolCommand value:
            {
                var pool = CurrentPool(topology, value.Pool, verifiedStepOutputs);
                if (pool is not null && (pool.IsPrimordial
                    || pool.MemberPhysicalDiskIds.Count != 1
                    || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId
                    || pool.FriendlyName == value.Name))
                    throw new InvalidDataException("The selected pool cannot receive this rename.");
                break;
            }
            case DeleteVirtualDiskCommand value:
            {
                var virtualDisk = ExistingVirtualDisk(snapshot, value.VirtualDisk);
                var pool = snapshot.StoragePools.SingleOrDefault(item =>
                    item.StableId == virtualDisk.PoolStableId)
                    ?? throw new InvalidDataException("The selected virtual disk has no concrete pool.");
                if (pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                    || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId
                    || snapshot.VirtualDisks.Count(item => item.PoolStableId == pool.StableId) != 1)
                    throw new InvalidDataException("Only the sole virtual disk of the exact single-member pool may be deleted.");
                break;
            }
            case RenameVirtualDiskCommand value:
            {
                var virtualDisk = CurrentVirtualDisk(topology, value.VirtualDisk,
                    verifiedStepOutputs);
                if (virtualDisk is not null && (virtualDisk.FriendlyName == value.Name
                    || snapshot.StoragePools.SingleOrDefault(item =>
                        item.StableId == virtualDisk.PoolStableId) is not { } pool
                    || pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                    || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId))
                    throw new InvalidDataException("The selected virtual disk cannot receive this rename.");
                break;
            }
            case CreateVirtualDiskCommand value:
            {
                if (value.Pool.Existing is null &&
                    !verifiedStepOutputs.ContainsKey(value.Pool.CreatedByStep ?? string.Empty))
                {
                    // This pool does not exist until an earlier approved step.
                    // Freeze the requested bytes now; the provider range is a
                    // mandatory preflight after that step is verified.
                    if (!proposal.Steps.TakeWhile(item => item.Id != step.Id)
                            .Any(item => item.Id == value.Pool.CreatedByStep
                                && item.Command is CreatePoolCommand))
                        throw new InvalidDataException("The planned virtual disk lacks a prior concrete pool creation.");
                    break;
                }
                var poolId = WindowsRealStorageTargetBuilder.ResolveId(
                    topology, value.Pool, verifiedStepOutputs);
                var pool = snapshot.StoragePools.SingleOrDefault(item =>
                    item.StableId == poolId.ProviderKey)
                    ?? throw new InvalidDataException("The selected concrete pool is absent.");
                if (pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                    || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId
                    || snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId))
                    throw new InvalidDataException("A first virtual disk requires an empty, exact single-member pool.");
                var target = WindowsRealStorageTargetBuilder.Build(
                    topology, value.Pool, verifiedStepOutputs);
                var range = await virtualDiskSizes.ReadAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (!range.Supports(value.SizeBytes))
                    throw new InvalidDataException("The requested Simple/Fixed virtual-disk size is outside the current pool-supported creation range.");
                break;
            }
            case ResizeVirtualDiskCommand:
            case ResizeTierCommand:
                throw new NotSupportedException("Existing-object MAX expansion lacks verified provider bounds.");
            case CreateTierCommand:
            case CreateTieredVirtualDiskCommand:
                throw new NotSupportedException("Single-HDD tier capability has not been proven on this host.");
            default:
                throw new NotSupportedException("This real command is not enabled by the current Windows planner.");
        }
    }

    private static OsDiskInfo ExistingDisk(StorageSnapshot snapshot, RealTargetReference reference) =>
        reference.Existing is { } existing
            ? snapshot.OsDisks.SingleOrDefault(item => item.StableId == existing.ProviderKey)
                ?? throw new InvalidDataException("The selected OS disk is absent.")
            : throw new InvalidDataException("This step requires an existing OS disk.");

    private static PartitionInfo ExistingPartition(StorageSnapshot snapshot, RealTargetReference reference) =>
        reference.Existing is { } existing
            ? snapshot.Partitions.SingleOrDefault(item => item.StableId == existing.ProviderKey)
                ?? throw new InvalidDataException("The selected partition is absent.")
            : throw new InvalidDataException("This step requires an existing partition.");

    private static StoragePoolInfo ExistingPool(StorageSnapshot snapshot, RealTargetReference reference) =>
        reference.Existing is { } existing
            ? snapshot.StoragePools.SingleOrDefault(item => item.StableId == existing.ProviderKey)
                ?? throw new InvalidDataException("The selected pool is absent.")
            : throw new InvalidDataException("Pool removal requires an existing exact pool.");

    private static VirtualDiskInfo ExistingVirtualDisk(StorageSnapshot snapshot, RealTargetReference reference) =>
        reference.Existing is { } existing
            ? snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == existing.ProviderKey)
                ?? throw new InvalidDataException("The selected virtual disk is absent.")
            : throw new InvalidDataException("Virtual-disk removal requires an existing exact virtual disk.");

    private static StoragePoolInfo? CurrentPool(WindowsRealStorageTopology topology,
        RealTargetReference reference, IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        if (reference.Existing is null &&
            !verifiedStepOutputs.ContainsKey(reference.CreatedByStep ?? string.Empty))
            return null;
        var id = WindowsRealStorageTargetBuilder.ResolveId(topology, reference,
            verifiedStepOutputs);
        return topology.Snapshot.StoragePools.SingleOrDefault(item =>
            item.StableId == id.ProviderKey)
            ?? throw new InvalidDataException("The selected pool is absent.");
    }

    private static VirtualDiskInfo? CurrentVirtualDisk(WindowsRealStorageTopology topology,
        RealTargetReference reference, IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        if (reference.Existing is null &&
            !verifiedStepOutputs.ContainsKey(reference.CreatedByStep ?? string.Empty))
            return null;
        var id = WindowsRealStorageTargetBuilder.ResolveId(topology, reference,
            verifiedStepOutputs);
        return topology.Snapshot.VirtualDisks.SingleOrDefault(item =>
            item.StableId == id.ProviderKey)
            ?? throw new InvalidDataException("The selected virtual disk is absent.");
    }

    private static void ValidatePartitionGeometry(
        WindowsRealStorageTopology topology,
        RealOperationIntentRequest proposal,
        RealOperationStep step,
        CreatePartitionCommand command,
        IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        var snapshot = topology.Snapshot;
        long diskSize;
        var current = Array.Empty<PartitionInfo>();
        if (command.Disk.Existing is { } id)
        {
            var disk = ExistingDisk(snapshot, command.Disk);
            var initializedEarlier = proposal.Steps.TakeWhile(item => item.Id != step.Id)
                .Any(item => item.Command is InitializeGptCommand initialization
                    && initialization.Disk.Existing == id);
            if (!initializedEarlier && !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Partition creation requires an online GPT disk.");
            if (disk.IsOffline) throw new InvalidDataException("Partition creation requires an online disk.");
            diskSize = disk.Size;
            current = snapshot.Partitions.Where(item => item.OsDiskStableId == id.ProviderKey).ToArray();
        }
        else
        {
            var observed = CurrentDisk(topology, command.Disk,
                verifiedStepOutputs);
            diskSize = observed?.Size ?? PlannedDiskSize(snapshot, proposal,
                command.Disk);
            if (observed is not null)
                current = snapshot.Partitions.Where(item =>
                    item.OsDiskStableId == observed.StableId).ToArray();
        }

        const long mebibyte = 1024L * 1024;
        var end = checked(command.OffsetBytes + command.SizeBytes);
        if (command.OffsetBytes < mebibyte || end > diskSize - mebibyte
            || current.Any(item => item.Offset < end && item.Offset + item.Size > command.OffsetBytes))
            throw new InvalidDataException("The partition does not fit an observed usable GPT gap.");
        foreach (var earlier in proposal.Steps.TakeWhile(item => item.Id != step.Id))
        {
            if (earlier.Command is CreatePartitionCommand created
                && created.Disk == command.Disk
                && created.OffsetBytes < end
                && created.OffsetBytes + created.SizeBytes > command.OffsetBytes)
                throw new InvalidDataException("Planned partitions overlap.");
        }
    }

    private static long PlannedDiskSize(StorageSnapshot snapshot,
        RealOperationIntentRequest proposal, RealTargetReference reference)
    {
        if (reference.Existing is not null)
            return ExistingDisk(snapshot, reference).Size;
        var source = proposal.Steps.SingleOrDefault(item =>
            item.Id == reference.CreatedByStep)?.Command;
        return source switch
        {
            CreateVirtualDiskCommand value => value.SizeBytes,
            CreateTieredVirtualDiskCommand value => value.SizeBytes,
            InitializeGptCommand value => PlannedDiskSize(snapshot, proposal, value.Disk),
            ClearDiskCommand value => PlannedDiskSize(snapshot, proposal, value.Disk),
            _ => throw new InvalidDataException("The new OS disk has no frozen capacity.")
        };
    }

    private static OsDiskInfo? CurrentDisk(
        WindowsRealStorageTopology topology,
        RealTargetReference reference,
        IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        if (reference.Existing is null &&
            !verifiedStepOutputs.ContainsKey(reference.CreatedByStep ?? string.Empty))
            return null;
        var id = WindowsRealStorageTargetBuilder.ResolveId(topology,
            reference, verifiedStepOutputs);
        return topology.Snapshot.OsDisks.SingleOrDefault(item =>
            item.StableId == id.ProviderKey)
            ?? throw new InvalidDataException("The selected OS disk is absent.");
    }

    private static PartitionInfo? CurrentPartition(
        WindowsRealStorageTopology topology,
        RealTargetReference reference,
        IReadOnlyDictionary<string, string> verifiedStepOutputs)
    {
        if (reference.Existing is null &&
            !verifiedStepOutputs.ContainsKey(reference.CreatedByStep ?? string.Empty))
            return null;
        var id = WindowsRealStorageTargetBuilder.ResolveId(topology,
            reference, verifiedStepOutputs);
        return topology.Snapshot.Partitions.SingleOrDefault(item =>
            item.StableId == id.ProviderKey)
            ?? throw new InvalidDataException("The selected partition is absent.");
    }

    private static bool IsValidFormatRole(
        PartitionInfo partition,
        FormatVolumeCommand command)
    {
        if (!Guid.TryParse(partition.PartitionTypeId, out var kind)) return false;
        if (IsBasicData(partition))
            return (command.FileSystem is RealFileSystem.Ntfs or RealFileSystem.ExFat)
                && command.ClusterBytes == 65536;
        if (command.Partition.CreatedByStep is null || command.Full)
            return false;
        return kind == Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")
                && command.FileSystem == RealFileSystem.Fat32
                && command.ClusterBytes == 4096
            || kind == Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac")
                && command.FileSystem == RealFileSystem.Ntfs
                && command.ClusterBytes == 4096;
    }

    private static bool IsBasicData(PartitionInfo partition) =>
        Guid.TryParse(partition.PartitionTypeId, out var actual)
        && actual == Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    private static bool IsLetterUsed(StorageSnapshot snapshot, char letter) =>
        snapshot.Partitions.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
        || snapshot.Volumes.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
        || snapshot.NetworkDisks.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase));

    private static RealFileSystem ParseFileSystem(string fileSystem) =>
        Enum.TryParse<RealFileSystem>(fileSystem, true, out var value)
            ? value : throw new InvalidDataException("The selected volume has an unknown file system.");

    private static void ValidateLabel(string? label, RealFileSystem fileSystem)
    {
        if (label is null) return;
        var maximum = fileSystem is RealFileSystem.ExFat or RealFileSystem.Fat32 ? 11 : 32;
        if (label.Length > maximum || label.Any(character =>
                character < ' ' || "\\/:*?\"<>|".Contains(character)))
            throw new InvalidDataException("The volume label is invalid for the selected file system.");
    }

    private static string DescribeBefore(RealStorageCommand command, RealTargetClosure closure) =>
        "Current single-member Windows topology verified: " + closure.Fingerprint
        + "; exact command target: " + command.GetType().Name;

    private static string DescribeAfter(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand value => value.Online ? "Exact disk online" : "Exact disk offline",
        ClearDiskCommand => RealOperationValidator.ClearDiskExpectedFinalState,
        InitializeGptCommand => "Exact disk GPT; physical identity unchanged",
        CreatePartitionCommand value => "Partition " + value.Role + " at "
            + value.OffsetBytes.ToString(CultureInfo.InvariantCulture) + " bytes, "
            + value.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        DeletePartitionCommand => "Only selected partition and attached volume absent",
        ResizePartitionCommand value => "Selected partition total size "
            + value.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        FormatVolumeCommand value => "Selected partition formatted " + value.FileSystem
            + " with " + value.ClusterBytes.ToString(CultureInfo.InvariantCulture) + " byte clusters",
        SetDriveLetterCommand value => "Selected partition drive letter "
            + (value.NewLetter?.ToString() ?? "removed"),
        RenameVolumeCommand value => "Selected volume label " + value.Label,
        CreatePoolCommand value => "Single-member pool " + value.Name,
        CreateVirtualDiskCommand value => "Single-column Simple/Fixed virtual disk "
            + value.Name + ", " + value.SizeBytes.ToString(CultureInfo.InvariantCulture)
            + " bytes, 65536-byte interleave",
        DeleteVirtualDiskCommand => "Selected virtual disk and its OS disk, partitions and volumes absent",
        DeletePoolCommand => "Selected pool absent; physical member released as observed",
        RenamePoolCommand value => "Selected pool name " + value.Name,
        RenameVirtualDiskCommand value => "Selected virtual disk name " + value.Name,
        _ => "Exact selected object reflects the listed change and retains its expected associations"
    };

    private static string DescribeLoss(RealStorageCommand command,
        RealTargetClosure closure, StorageSnapshot snapshot) => command switch
    {
        ClearDiskCommand value => "All existing partition and volume data on physical member "
            + closure.PhysicalDiskId + ": " + DescribePartitions(snapshot,
                snapshot.Partitions.Where(partition => partition.OsDiskStableId
                    == value.Disk.Existing?.ProviderKey)),
        DeletePartitionCommand value => "All data on partition "
            + value.Partition.Existing?.ProviderKey + ": "
            + DescribePartitions(snapshot, snapshot.Partitions.Where(partition =>
                partition.StableId == value.Partition.Existing?.ProviderKey)),
        FormatVolumeCommand value => "All existing data on partition "
            + (value.Partition.Existing?.ProviderKey ?? "created by "
                + value.Partition.CreatedByStep),
        DeleteVirtualDiskCommand value => "All partitions and volume data on virtual disk "
            + value.VirtualDisk.Existing?.ProviderKey + ": "
            + DescribePartitions(snapshot, snapshot.Partitions.Where(partition =>
                snapshot.OsDisks.Any(disk => disk.StableId == partition.OsDiskStableId
                    && disk.VirtualDiskStableId == value.VirtualDisk.Existing?.ProviderKey))),
        DeletePoolCommand value => "All layouts and data in pool "
            + value.Pool.Existing?.ProviderKey + ": virtual disks "
            + string.Join(", ", snapshot.VirtualDisks.Where(disk => disk.PoolStableId
                    == value.Pool.Existing?.ProviderKey).Select(disk => disk.StableId))
            + "; partitions " + DescribePartitions(snapshot,
                snapshot.Partitions.Where(partition => snapshot.OsDisks.Any(disk =>
                    disk.StableId == partition.OsDiskStableId &&
                    snapshot.VirtualDisks.Any(virtualDisk =>
                        virtualDisk.StableId == disk.VirtualDiskStableId
                        && virtualDisk.PoolStableId == value.Pool.Existing?.ProviderKey)))),
        _ => string.Empty
    };

    private static string DescribePartitions(StorageSnapshot snapshot,
        IEnumerable<PartitionInfo> partitions)
    {
        var items = partitions.OrderBy(item => item.DiskNumber)
            .ThenBy(item => item.Offset).Select(partition =>
            {
                var volume = snapshot.Volumes.SingleOrDefault(item =>
                    item.PartitionStableId == partition.StableId);
                return "partition " + partition.Guid + " at "
                    + partition.Offset.ToString(CultureInfo.InvariantCulture)
                    + " bytes, " + partition.Size.ToString(CultureInfo.InvariantCulture)
                    + " bytes, " + partition.FileSystem + ", letter "
                    + (partition.DriveLetter.Length == 0 ? "none" : partition.DriveLetter)
                    + ", label " + partition.FileSystemLabel
                    + ", volume " + (volume?.StableId ?? "none");
            }).ToArray();
        return items.Length == 0 ? "no existing partitions" : string.Join(" | ", items);
    }
}
