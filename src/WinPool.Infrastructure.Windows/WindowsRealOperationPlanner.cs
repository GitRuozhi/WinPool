using System.Globalization;
using System.Text.Json;
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
    private readonly Func<IReadOnlyList<string>> logicalDriveRoots;
    private readonly IWindowsRealStorageCapabilityReader capabilities;

    public WindowsRealOperationPlanner(
        WindowsRealStorageTopologyReader? topologyReader = null,
        IPartitionSupportedSizeReader? partitionSizes = null,
        IPrivilegeService? privilege = null,
        TimeProvider? timeProvider = null,
        IWindowsRealStorageSafetyInspector? safetyInspector = null,
        IVirtualDiskCreationSizeReader? virtualDiskSizes = null,
        Func<IReadOnlyList<string>>? logicalDriveRoots = null,
        IWindowsRealStorageCapabilityReader? capabilities = null)
    {
        this.topologyReader = topologyReader ?? new WindowsRealStorageTopologyReader();
        this.partitionSizes = partitionSizes ?? new WindowsPartitionSupportedSizeReader();
        this.virtualDiskSizes = virtualDiskSizes ?? new WindowsVirtualDiskCreationSizeReader();
        this.safetyInspector = safetyInspector ?? new WindowsRealStorageSafetyInspector();
        this.privilege = privilege ?? new WindowsPrivilegeService();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logicalDriveRoots = logicalDriveRoots ?? Environment.GetLogicalDrives;
        this.capabilities = capabilities ?? new WindowsRealStorageCapabilityReader();
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
        if (proposal.Intent == OperationIntent.InitializeDisk
            && proposal.Steps.Any(item => item.Command is InitializeGptCommand)
            && proposal.Steps.Count != 1)
            throw new ArgumentException("Initialize GPT separately, observe the provider MSR, then confirm a new normalization plan.");
        var topology = await topologyReader.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (proposal.SystemId != topology.SystemId)
            throw new InvalidDataException("The proposal does not name the current local system.");

        if (proposal.Intent == OperationIntent.InitializeDisk
            && !proposal.Steps.Any(item => item.Command is InitializeGptCommand))
            ValidateInitializationContinuation(topology, proposal);

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
            var support = await ValidateCurrentStepAsync(
                topology, closure, proposal, step, cancellationToken)
                .ConfigureAwait(false);
            normalized.Add(step with
            {
                BeforeCondition = DescribeBefore(step.Command, closure, topology),
                AfterCondition = DescribeAfter(step.Command),
                DataLoss = DescribeLoss(step.Command, closure, topology.Snapshot),
                SupportEvidence = support
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

    public async Task<RealVirtualDiskCreationRange> ReadVirtualDiskCreationRangeAsync(
        StorageObjectId targetId, TrustedRealSession session, CancellationToken cancellationToken)
    {
        if (privilege.Current != PrivilegeState.Administrator || !session.IsArmed)
            throw new UnauthorizedAccessException("An armed elevated Agent is required.");
        if (targetId.Kind is not (StorageObjectKind.StoragePool or StorageObjectKind.StorageTier))
            throw new InvalidDataException("An exact pool or unused HDD template is required.");
        var topology = await topologyReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var closure = topology.RequireSinglePhysicalClosure([targetId]);
        var target = WindowsRealStorageTargetBuilder.Build(topology,
            RealTargetReference.ForExisting(targetId), new Dictionary<string, string>());
        VirtualDiskCreationSize range;
        if (targetId.Kind == StorageObjectKind.StoragePool)
        {
            var pool = ExistingPool(topology.Snapshot, RealTargetReference.ForExisting(targetId));
            if (pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId
                || topology.Snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
                || topology.Snapshot.StorageTiers.Any(item => item.PoolStableId == pool.StableId))
                throw new InvalidDataException("Ordinary creation requires an empty exact single-member pool.");
            range = await virtualDiskSizes.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var tier = topology.Snapshot.StorageTiers.Single(item => item.StableId == targetId.ProviderKey);
            var pool = RequireTierPool(topology.Snapshot, closure, tier.PoolStableId);
            _ = RequireHddTier(topology.Snapshot, pool, tier.StableId);
            if (tier.VirtualDiskStableId is not null
                || topology.Snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
                || topology.Snapshot.StorageTiers.Count(item => item.PoolStableId == pool.StableId) != 1)
                throw new InvalidDataException("The sole unused HDD template is required.");
            await RequireTierCapabilityAsync(topology, closure,
                "SupportsStorageTieredVirtualDiskCreation", cancellationToken).ConfigureAwait(false);
            var tierRange = await capabilities.ReadTierCreationSizeAsync(topology, targetId,
                cancellationToken).ConfigureAwait(false);
            var poolTarget = WindowsRealStorageTargetBuilder.Build(topology,
                RealTargetReference.ForExisting(new StorageObjectId(topology.SystemId,
                    StorageObjectKind.StoragePool, pool.StableId)), new Dictionary<string, string>());
            var poolRange = await virtualDiskSizes.ReadAsync(poolTarget, cancellationToken).ConfigureAwait(false);
            range = VirtualDiskCreationSize.Intersect(poolRange, tierRange);
        }
        var result = new RealVirtualDiskCreationRange(targetId, range.MinimumBytes,
            range.MaximumBytes, range.DivisorBytes, range.RangeOriginBytes, range.EnumeratedSizes,
            closure.Fingerprint, timeProvider.GetUtcNow());
        _ = result.ResolveMaximum();
        return result;
    }

    public async Task<RealStructureCreationSupport> ReadStructureCreationSupportAsync(
        StorageObjectId physicalTarget,
        bool tiered,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (privilege.Current != PrivilegeState.Administrator || !session.IsArmed)
            throw new UnauthorizedAccessException("An armed elevated Agent is required.");
        if (physicalTarget.Kind != StorageObjectKind.PhysicalDisk)
            throw new InvalidDataException("Creation support requires one exact physical disk.");

        var topology = await topologyReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (physicalTarget.System != topology.SystemId)
            throw new InvalidDataException("The physical disk does not belong to the current local system.");
        var physicalSource = topology.RequireObject(physicalTarget);
        var closure = topology.RequireSinglePhysicalClosure([physicalTarget]);
        var physical = topology.Snapshot.PhysicalDisks.SingleOrDefault(item =>
            item.StableId == closure.PhysicalDiskId)
            ?? throw new InvalidDataException("The exact physical member is absent from the fresh snapshot.");
        if (!StringComparer.Ordinal.Equals(physicalTarget.ProviderKey, closure.PhysicalDiskId))
            throw new InvalidDataException("The selected identity is not the exact physical member in its closure.");

        var subsystem = RequireCreationSubsystem(topology, physicalSource, physical);
        var currentPool = RequireCurrentCreationPool(topology, physical);
        if (tiered && (!physical.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
            || RequireFactUInt64(physicalSource, "MediaType") != 3))
            throw new NotSupportedException("Tiered creation is enabled only for one exact HDD member.");

        VirtualDiskCreationSupportProbe? ordinaryProbe = null;
        WindowsTierCapability capabilityEvidence;
        if (tiered)
        {
            capabilityEvidence = await RequireTierCapabilitiesAsync(topology, closure,
                ["SupportsStoragePoolCreation", "SupportsStorageTierCreation",
                    "SupportsStorageTieredVirtualDiskCreation"],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            capabilityEvidence = await RequireTierCapabilitiesAsync(topology, closure,
                ["SupportsStoragePoolCreation"], cancellationToken).ConfigureAwait(false);
            if (currentPool is not null)
            {
                var poolId = new StorageObjectId(topology.SystemId,
                    StorageObjectKind.StoragePool, currentPool.Id);
                var target = WindowsRealStorageTargetBuilder.Build(topology,
                    RealTargetReference.ForExisting(poolId), new Dictionary<string, string>());
                if (target.Kind != StorageObjectKind.StoragePool
                    || target.UniqueId != currentPool.UniqueId
                    || target.ObjectId != currentPool.ObjectId
                    || target.PhysicalMemberUniqueId != subsystem.PhysicalUniqueId
                    || target.PhysicalMemberObjectId != subsystem.PhysicalObjectId
                    || target.StorageSubsystemUniqueId != subsystem.UniqueId
                    || target.StorageSubsystemObjectId != subsystem.ObjectId)
                    throw new InvalidDataException("The current pool target does not preserve the exact physical/subsystem identities.");
                ordinaryProbe = await virtualDiskSizes.ProbeAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (ordinaryProbe.ReturnValue != 0
                    || ordinaryProbe.ObservedAtUtc == default
                    || ordinaryProbe.ResiliencySettingName != "Simple"
                    || ordinaryProbe.PoolUniqueId != currentPool.UniqueId
                    || ordinaryProbe.PoolObjectId != currentPool.ObjectId)
                    throw new NotSupportedException("The exact current pool did not verify Simple virtual-disk creation support.");
            }
        }

        var after = await topologyReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(after.MachineBinding, topology.MachineBinding))
            throw new InvalidDataException("The local machine binding changed while creation support was being checked.");
        var afterClosure = after.RequireSinglePhysicalClosure([physicalTarget]);
        var afterPhysical = after.RequireObject(physicalTarget);
        var afterSubsystem = RequireCreationSubsystem(after, afterPhysical,
            after.Snapshot.PhysicalDisks.Single(item => item.StableId == afterClosure.PhysicalDiskId));
        var afterPool = RequireCurrentCreationPool(after,
            after.Snapshot.PhysicalDisks.Single(item => item.StableId == afterClosure.PhysicalDiskId));
        if (!StringComparer.Ordinal.Equals(afterClosure.Fingerprint, closure.Fingerprint)
            || afterClosure.PhysicalDiskId != closure.PhysicalDiskId
            || afterSubsystem.StableId != subsystem.StableId
            || afterSubsystem.UniqueId != subsystem.UniqueId
            || afterSubsystem.ObjectId != subsystem.ObjectId
            || afterPool?.Id != currentPool?.Id
            || afterPool?.UniqueId != currentPool?.UniqueId
            || afterPool?.ObjectId != currentPool?.ObjectId)
            throw new InvalidDataException("The physical member, pool or subsystem changed while creation support was being checked.");

        var providerEvidence = JsonSerializer.Serialize(new
        {
            topology.InventoryVersion,
            PhysicalTarget = physicalTarget,
            PhysicalMemberFingerprint = closure.PhysicalMemberFingerprint,
            Subsystem = new { subsystem.StableId, subsystem.UniqueId, subsystem.ObjectId },
            CurrentPool = currentPool is null ? null : new
            {
                currentPool.Id, currentPool.UniqueId, currentPool.ObjectId
            },
            OrdinaryProviderMethodProbe = ordinaryProbe,
            Capabilities = capabilityEvidence,
            closure.PoolMemberRoleEvidence
        });
        ulong? minimum = null;
        bool? supportsTierCreation = null;
        bool? supportsTieredVirtualDiskCreation = null;
        if (tiered)
        {
            var minimumField = capabilityEvidence.Fields.Single(item =>
                item.Name == "PhysicalDisksPerStoragePoolMin");
            minimum = minimumField.Value!.Value.GetUInt64();
            supportsTierCreation = true;
            supportsTieredVirtualDiskCreation = true;
        }
        return new RealStructureCreationSupport(physicalTarget, tiered,
            closure.Fingerprint, timeProvider.GetUtcNow(), closure.PhysicalMemberFingerprint,
            subsystem.StableId, subsystem.UniqueId, subsystem.ObjectId,
            currentPool?.Id, currentPool?.UniqueId, currentPool?.ObjectId,
            ordinaryProbe is not null, true, supportsTierCreation,
            supportsTieredVirtualDiskCreation, minimum, providerEvidence);
    }

    public async Task<RealPartitionResizeRange> ReadPartitionResizeRangeAsync(
        StorageObjectId partitionId,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (privilege.Current != PrivilegeState.Administrator || !session.IsArmed)
            throw new UnauthorizedAccessException("An armed elevated Agent is required.");
        if (partitionId.Kind != StorageObjectKind.Partition)
            throw new InvalidDataException("A specific partition is required for a resize range.");

        var topology = await topologyReader.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (partitionId.System != topology.SystemId)
            throw new InvalidDataException("The partition does not belong to the current local system.");
        var closure = topology.RequireSinglePhysicalClosure([partitionId]);
        var partition = ExistingPartition(topology.Snapshot,
            RealTargetReference.ForExisting(partitionId));
        var disk = topology.Snapshot.OsDisks.SingleOrDefault(item =>
            item.StableId == partition.OsDiskStableId)
            ?? throw new InvalidDataException("The partition has no exact OS disk.");
        if (disk.IsBoot || disk.IsSystem || disk.IsOffline
            || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
            || partition.IsBoot || partition.IsSystem || !IsBasicData(partition)
            || !IsSupportedResizeFileSystem(topology.Snapshot, partition))
            throw new NotSupportedException("Only an ordinary online GPT NTFS or RAW data partition can be resized.");

        var reference = RealTargetReference.ForExisting(partitionId);
        await safetyInspector.ValidateAsync(topology, closure,
            new ResizePartitionCommand(reference, partition.Size), cancellationToken)
            .ConfigureAwait(false);
        if (IsRefsPartition(topology.Snapshot, partition))
            await RequireRefsCapabilityAsync(topology, partition, cancellationToken).ConfigureAwait(false);
        var target = WindowsRealStorageTargetBuilder.Build(topology, reference,
            new Dictionary<string, string>());
        var provider = await partitionSizes.ReadAsync(target, cancellationToken)
            .ConfigureAwait(false);
        var afterLookup = await topologyReader.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(afterLookup.MachineBinding, topology.MachineBinding)
            || !StringComparer.Ordinal.Equals(
                afterLookup.RequireSinglePhysicalClosure([partitionId]).Fingerprint,
                closure.Fingerprint))
            throw new InvalidDataException("The partition topology changed while reading supported sizes.");

        var (minimum, maximum) = AllowedResizeRange(topology.Snapshot,
            partition, provider);
        if (IsRefsPartition(topology.Snapshot, partition))
            minimum = Math.Max(minimum, partition.Size);
        return new RealPartitionResizeRange(partitionId, partition.Size,
            provider.MinimumBytes, provider.MaximumBytes, minimum, maximum,
            closure.Fingerprint, timeProvider.GetUtcNow(), "real.resize_range_verified");
    }

    internal Task ValidateObservedInitializationAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        InitializeGptCommand command, CancellationToken cancellationToken) =>
        safetyInspector.ValidateAsync(topology, closure, command, cancellationToken);

    internal Task<WindowsRealStorageSafetyEvidence?> ValidatePartitionSafetyWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        RealStorageCommand command, CancellationToken cancellationToken) =>
        safetyInspector.ValidateWithEvidenceAsync(topology, closure, command, cancellationToken);

    internal Task<WindowsRealStorageSafetyEvidence?> ValidateObservedDiskStateWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        SetDiskOnlineCommand command, CancellationToken cancellationToken) =>
        safetyInspector.ValidateObservedDiskStateWithEvidenceAsync(topology, closure, command, cancellationToken);

    internal Task<WindowsRealStorageSafetyEvidence?> ValidateCreatedPartitionFormatWithEvidenceAsync(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        FormatVolumeCommand command, StorageObjectId exactCreatedPartition,
        CancellationToken cancellationToken) =>
        safetyInspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology, closure,
            command, exactCreatedPartition, cancellationToken);

    internal async Task<string> ValidateCurrentStepAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        RealOperationIntentRequest proposal,
        RealOperationStep step,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? verifiedStepOutputs = null,
        bool readOnlyRenameReconciliation = false)
    {
        var snapshot = topology.Snapshot;
        var supportEvidence = "fresh-msft-storage:" + closure.Fingerprint;
        verifiedStepOutputs ??= new Dictionary<string, string>();
        WindowsRealStorageSafetyEvidence? safetyEvidence;
        if (step.Command is FormatVolumeCommand { Partition.Existing: null,
                Partition.CreatedByStep: { Length: > 0 } createdBy } createdFormat
            && verifiedStepOutputs.ContainsKey(createdBy)
            && CurrentPartition(topology, createdFormat.Partition, verifiedStepOutputs) is { } createdPartition
            && Guid.TryParse(createdPartition.PartitionTypeId, out var createdRole)
            && createdRole == WindowsNativeMsrAttributesReader.EfiRole)
        {
            var exactId = WindowsRealStorageTargetBuilder.ResolveId(topology,
                createdFormat.Partition, verifiedStepOutputs);
            safetyEvidence = await safetyInspector.ValidateCreatedPartitionFormatWithEvidenceAsync(
                topology, closure, createdFormat, exactId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            safetyEvidence = await safetyInspector.ValidateWithEvidenceAsync(
                topology, closure, step.Command, cancellationToken).ConfigureAwait(false);
        }
        if (safetyEvidence?.NativePartitionAttributes is { } nativeAttributes)
            supportEvidence += ";native-msr-attributes:" + JsonSerializer.Serialize(nativeAttributes);
        if (safetyEvidence?.VolumeSafetyEvidence is { Count: > 0 } volumeSafety)
            supportEvidence += ";volume-safety:" + JsonSerializer.Serialize(volumeSafety);
        if (safetyEvidence?.OfflinePartitionAttributes is { Count: > 0 } offlineAttributes)
            supportEvidence += ";offline-partition-attributes:" + JsonSerializer.Serialize(offlineAttributes);
        if (safetyEvidence?.PoolMemberRoleEvidence is { } poolMemberRoles)
            supportEvidence += ";pool-member-role-evidence:" + JsonSerializer.Serialize(poolMemberRoles);
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
                var msrRole = Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae");
                var clearableGptRoles = new HashSet<Guid>
                {
                    Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
                    msrRole
                };
                var diskSource = topology.RequireObject(new StorageObjectId(topology.SystemId,
                    StorageObjectKind.OsDisk, disk.StableId));
                if (diskSource.Field("NumberOfPartitions") is not { } partitionCount
                    || !partitionCount.TryGetInt64(out var observedCount)
                    || observedCount != partitions.Length
                    || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                    || snapshot.UnattachedPartitions.Any(item => item.OsDiskId == disk.StableId)
                    || partitions.Any(item =>
                        item.IsBoot || item.IsSystem
                        || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                        || !Guid.TryParse(item.PartitionTypeId, out var kind)
                        || !clearableGptRoles.Contains(kind)
                        || item.IsHidden && (kind != msrRole
                            || !Guid.TryParse(item.GptType, out var gptKind)
                            || gptKind != msrRole)))
                    throw new InvalidDataException("Clear requires ordinary GPT data/MSR partitions only; OEM, recovery, boot, hidden non-MSR and unknown roles are outside this stage.");
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
                if (proposal.Intent == OperationIntent.InitializeDisk
                    && !IsProviderInitializationMsr(topology, partition))
                    throw new InvalidDataException("Initialization continuation can remove only the exact unformatted provider-created MSR.");
                break;
            }
            case ResizePartitionCommand value:
            {
                var partition = ExistingPartition(snapshot, value.Partition);
                if (partition.IsBoot || partition.IsSystem || !IsBasicData(partition)
                    || !IsSupportedResizeFileSystem(snapshot, partition))
                    throw new InvalidDataException("Only an ordinary NTFS or unformatted data partition may be resized.");
                if (IsRefsPartition(snapshot, partition))
                {
                    if (value.SizeBytes <= partition.Size)
                        throw new InvalidDataException("ReFS supports only separately verified extension in this stage.");
                    supportEvidence = await RequireRefsCapabilityAsync(topology, partition, cancellationToken).ConfigureAwait(false);
                }
                var target = WindowsRealStorageTargetBuilder.Build(
                    topology, value.Partition, new Dictionary<string, string>());
                var range = await partitionSizes.ReadAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                var (minimum, maximum) = AllowedResizeRange(snapshot, partition, range);
                if (value.SizeBytes < minimum || value.SizeBytes > maximum)
                    throw new InvalidDataException("The requested size is outside the current provider and geometric range.");
                if (value.SizeBytes == partition.Size)
                    throw new InvalidDataException("The requested partition size is unchanged.");
                break;
            }
            case FormatVolumeCommand value:
            {
                var partition = CurrentPartition(topology, value.Partition,
                    verifiedStepOutputs);
                if (value.FileSystem == RealFileSystem.ReFs)
                {
                    if (value.Partition.Existing is null || partition is null || value.Full
                        || value.ClusterBytes != 65536)
                        throw new NotSupportedException("ReFS requires a separately prepared existing BasicData volume and 64 KiB quick format.");
                    supportEvidence = await RequireRefsCapabilityAsync(topology, partition, cancellationToken).ConfigureAwait(false);
                }
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
                    if (!readOnlyRenameReconciliation && volume.FileSystemLabel == value.Label)
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
                    && (child.VirtualDiskStableId is { } owner
                        ? !prior.Any(item => item.Command is DeleteVirtualDiskCommand deletion
                            && deletion.VirtualDisk.Existing?.ProviderKey == owner)
                        : !prior.Any(item => item.Command is DeleteTierCommand deletion
                            && deletion.Tier.Existing?.ProviderKey == child.StableId))))
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
            {
                var value = (CreateTierCommand)step.Command;
                if (value.Pool.Existing is null)
                    throw new NotSupportedException("Create the concrete single-member pool in a separate plan first.");
                var pool = RequireTierPool(snapshot, closure, value.Pool.Existing.Value.ProviderKey);
                if (snapshot.StorageTiers.Any(item => item.PoolStableId == pool.StableId)
                    || snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId))
                    throw new InvalidDataException("A first HDD tier requires an empty pool with no existing templates.");
                supportEvidence = await RequireTierCapabilityAsync(topology, closure, "SupportsStorageTierCreation", cancellationToken).ConfigureAwait(false);
                break;
            }
            case CreateTieredVirtualDiskCommand value:
            {
                if (value.Pool.Existing is null || value.Tier.Existing is null)
                    throw new NotSupportedException("Tiered creation requires an existing pool and verified template from a separate plan.");
                var pool = RequireTierPool(snapshot, closure, value.Pool.Existing.Value.ProviderKey);
                var tier = RequireHddTier(snapshot, pool, value.Tier.Existing.Value.ProviderKey);
                if (tier.VirtualDiskStableId is not null
                    || snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
                    || snapshot.StorageTiers.Count(item => item.PoolStableId == pool.StableId) != 1)
                    throw new InvalidDataException("Only the sole unused HDD template can create the first virtual disk.");
                supportEvidence = await RequireTierCapabilityAsync(topology, closure, "SupportsStorageTieredVirtualDiskCreation", cancellationToken).ConfigureAwait(false);
                var range = await capabilities.ReadTierCreationSizeAsync(topology,
                    value.Tier.Existing.Value, cancellationToken).ConfigureAwait(false);
                var poolTarget = WindowsRealStorageTargetBuilder.Build(topology, value.Pool, verifiedStepOutputs);
                var poolRange = await virtualDiskSizes.ReadAsync(poolTarget, cancellationToken).ConfigureAwait(false);
                if (!VirtualDiskCreationSize.Intersect(poolRange, range).Supports(value.SizeBytes))
                    throw new InvalidDataException("The frozen size is outside the intersection of the exact pool and HDD template Simple creation ranges.");
                supportEvidence += "; exact-template-new-size:" + System.Text.Json.JsonSerializer.Serialize(range)
                    + "; exact-pool-new-size:" + System.Text.Json.JsonSerializer.Serialize(poolRange);
                break;
            }
            case DeleteTierCommand value:
            {
                var tierId = WindowsRealStorageTargetBuilder.ResolveId(topology, value.Tier, verifiedStepOutputs);
                var tier = snapshot.StorageTiers.Single(item => item.StableId == tierId.ProviderKey);
                var pool = RequireTierPool(snapshot, closure, tier.PoolStableId);
                _ = RequireHddTier(snapshot, pool, tier.StableId);
                if (tier.VirtualDiskStableId is not null)
                    throw new InvalidDataException("A VD tier instance is removed with its owning VD; only a pool template has an explicit tier-deletion step.");
                var users = snapshot.VirtualDisks.Where(item => item.PoolStableId == pool.StableId).ToArray();
                if (users.Any(item => !proposal.Steps.TakeWhile(prior => prior.Id != step.Id)
                        .Any(prior => prior.Command is DeleteVirtualDiskCommand removal
                            && removal.VirtualDisk.Existing?.ProviderKey == item.StableId))
                    || verifiedStepOutputs.Count > 0 && users.Length > 0)
                    throw new InvalidDataException("Every tier user must be explicitly removed before deleting its template.");
                supportEvidence = await RequireTierCapabilityAsync(topology, closure, "SupportsStorageTierDeletion", cancellationToken).ConfigureAwait(false);
                break;
            }
            case RenameTierCommand value:
            {
                var tierId = WindowsRealStorageTargetBuilder.ResolveId(topology, value.Tier, verifiedStepOutputs);
                var tier = snapshot.StorageTiers.Single(item => item.StableId == tierId.ProviderKey);
                var pool = RequireTierPool(snapshot, closure, tier.PoolStableId);
                _ = RequireHddTier(snapshot, pool, tier.StableId);
                if (!readOnlyRenameReconciliation && tier.FriendlyName == value.Name)
                    throw new InvalidDataException("The requested tier name is unchanged.");
                supportEvidence = await RequireTierCapabilityAsync(topology, closure, "SupportsStorageTierFriendlyNameModification", cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new NotSupportedException("This real command is not enabled by the current Windows planner.");
        }
        return supportEvidence;
    }

    private async Task<string> RequireRefsCapabilityAsync(
        WindowsRealStorageTopology topology, PartitionInfo partition, CancellationToken token)
    {
        if (partition.IsBoot || partition.IsSystem || partition.IsHidden || !IsBasicData(partition))
            throw new InvalidDataException("ReFS requires an ordinary unprotected BasicData partition.");
        var product = topology.Snapshot.Computer.WindowsProductName;
        if (!product.Contains("Pro for Workstations", StringComparison.OrdinalIgnoreCase)
            && !product.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            && !product.Contains("Server", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The current Windows SKU has no verified ReFS creation applicability.");
        var volumes = topology.Snapshot.Volumes.Where(item => item.PartitionStableId == partition.StableId).ToArray();
        if (volumes.Length != 1)
            throw new NotSupportedException("A uniquely associated current volume is required for ReFS capability queries.");
        var volume = new StorageObjectId(topology.SystemId, StorageObjectKind.Volume, volumes[0].StableId);
        var closure = topology.RequireSinglePhysicalClosure([volume]);
        var evidence = await capabilities.ReadVolumeFormatAsync(topology, volume, token).ConfigureAwait(false);
        var fs = evidence.FileSystems;
        var clusters = evidence.ReFsClusterSizes;
        if (evidence.Status != "queried" || evidence.VolumeStableId != volume.ProviderKey
            || evidence.PartitionStableId != partition.StableId
            || evidence.PhysicalDiskStableId != closure.PhysicalDiskId
            || evidence.PhysicalMemberFingerprint != closure.PhysicalMemberFingerprint
            || fs is not { ReturnValue: 0, Status: "returned" }
            || clusters is not { ReturnValue: 0, Status: "returned" }
            || !fs.Output.TryGetValue("SupportedFileSystems", out var supportedFs)
            || supportedFs is not { ValueKind: System.Text.Json.JsonValueKind.Array } names
            || !names.EnumerateArray().Any(item => item.ValueKind == System.Text.Json.JsonValueKind.String
                && item.GetString()!.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            || !clusters.Output.TryGetValue("SupportedClusterSizes", out var supportedClusters)
            || supportedClusters is not { ValueKind: System.Text.Json.JsonValueKind.Array } sizes
            || !sizes.EnumerateArray().Any(item => item.ValueKind == System.Text.Json.JsonValueKind.Number
                && item.TryGetInt32(out var bytes) && bytes == 65536))
            throw new NotSupportedException("The exact current volume did not verify ReFS and 65536-byte clusters: " + evidence.Error);
        return "live-refs-capability; SKU=" + product + "; " + System.Text.Json.JsonSerializer.Serialize(evidence);
    }

    private async Task<string> RequireTierCapabilityAsync(WindowsRealStorageTopology topology,
        RealTargetClosure closure, string field, CancellationToken token)
    {
        var physical = new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, closure.PhysicalDiskId);
        var evidence = await capabilities.ReadTierAsync(topology, physical, token).ConfigureAwait(false);
        var supported = evidence.Fields.SingleOrDefault(item => item.Name == field);
        var minimum = evidence.Fields.SingleOrDefault(item => item.Name == "PhysicalDisksPerStoragePoolMin");
        if (evidence.Status != "queried" || evidence.PhysicalDiskStableId != closure.PhysicalDiskId
            || supported is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: System.Text.Json.JsonValueKind.True } }
            || minimum is not { ReadState: FieldReadState.Returned, Value: { } min }
            || !min.TryGetUInt64(out var count) || count != 1)
            throw new NotSupportedException("The exact subsystem has not verified the single-HDD capability " + field + ": " + evidence.Error);
        return "live-tier-capability:" + System.Text.Json.JsonSerializer.Serialize(evidence);
    }

    private async Task<WindowsTierCapability> RequireTierCapabilitiesAsync(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        IReadOnlyList<string> requiredFields,
        CancellationToken token)
    {
        var physicalTarget = new StorageObjectId(topology.SystemId,
            StorageObjectKind.PhysicalDisk, closure.PhysicalDiskId);
        var physicalSource = topology.RequireObject(physicalTarget);
        var physical = topology.Snapshot.PhysicalDisks.Single(item =>
            item.StableId == closure.PhysicalDiskId);
        var subsystem = RequireCreationSubsystem(topology, physicalSource, physical);
        var evidence = await capabilities.ReadTierAsync(topology, physicalTarget, token)
            .ConfigureAwait(false);
        var associationKeys = evidence.Associations.Select(RelationshipKey)
            .Order(StringComparer.Ordinal).ToArray();
        var expectedAssociationKeys = subsystem.Associations.Select(RelationshipKey)
            .Order(StringComparer.Ordinal).ToArray();
        if (evidence.Status != "queried"
            || evidence.PhysicalDiskStableId != closure.PhysicalDiskId
            || evidence.SubsystemStableId != subsystem.StableId
            || evidence.UniqueId != subsystem.UniqueId
            || evidence.ObjectId != subsystem.ObjectId
            || evidence.ObservedAtUtc == default
            || associationKeys.Length != associationKeys.Distinct(StringComparer.Ordinal).Count()
            || !associationKeys.SequenceEqual(expectedAssociationKeys, StringComparer.Ordinal))
            throw new NotSupportedException("The current physical disk and subsystem capability identity is not exact: " + evidence.Error);

        foreach (var fieldName in requiredFields)
        {
            var fields = evidence.Fields.Where(item => item.Name == fieldName).ToArray();
            if (fields.Length != 1 || fields[0] is not
                { ValueType: FactValueType.Boolean, ReadState: FieldReadState.Returned,
                    Value: { ValueKind: JsonValueKind.True } }
                || fields[0].SourceRef != subsystem.StableId)
                throw new NotSupportedException("The exact subsystem has not verified the single-HDD capability "
                    + fieldName + ": " + evidence.Error);
        }

        var minimumFields = evidence.Fields.Where(item =>
            item.Name == "PhysicalDisksPerStoragePoolMin").ToArray();
        if (minimumFields.Length != 1
            || minimumFields[0] is not
                { ValueType: FactValueType.UInt64, ReadState: FieldReadState.Returned,
                    Value: { } minimumValue }
            || minimumFields[0].SourceRef != subsystem.StableId
            || !minimumValue.TryGetUInt64(out var minimum) || minimum != 1)
            throw new NotSupportedException("The exact subsystem has not verified one physical disk per storage pool: "
                + evidence.Error);
        return evidence;
    }

    private static CreationSubsystemEvidence RequireCreationSubsystem(
        WindowsRealStorageTopology topology,
        WinPoolSourceObject physicalSource,
        PhysicalDiskInfo physical)
    {
        if (physicalSource.ObjectType != FactObjectType.PhysicalDisk
            || physicalSource.Id != physical.StableId
            || !StringComparer.Ordinal.Equals(RequireFactText(physicalSource, "SerialNumber"),
                physical.SerialNumber.Trim()))
            throw new InvalidDataException("The physical target has no exact current provider identity.");
        var physicalUniqueId = RequireFactText(physicalSource, "UniqueId");
        var physicalObjectId = RequireFactText(physicalSource, "ObjectId");
        if (RequireFactBoolean(physicalSource, "CanPool") != physical.CanPool)
            throw new InvalidDataException("The physical member's current poolability fact disagrees with its projection.");

        var relations = topology.Facts.Relationships.Where(item => !item.IsRetained).ToArray();
        var memberships = relations.Where(item => item.Kind == "pool-member"
            && item.ToId == physicalSource.Id).ToArray();
        if (memberships.Length == 0
            || memberships.Select(item => item.FromId).Distinct(StringComparer.Ordinal).Count() != memberships.Length)
            throw new InvalidDataException("The physical member has no unique current pool association for its subsystem.");

        var subsystemIds = new HashSet<string>(StringComparer.Ordinal);
        var associationEvidence = new List<WinPoolFactRelationship>();
        var factConcretePools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var membership in memberships)
        {
            var poolSource = topology.Facts.Objects.SingleOrDefault(item =>
                item.Id == membership.FromId && item.ObjectType == FactObjectType.StoragePool
                && item.HasReliableIdentity)
                ?? throw new InvalidDataException("A physical pool association has no exact current pool object.");
            var poolSourceState = topology.Facts.Sources.Single(item => item.Id == poolSource.SourceRef);
            if (poolSourceState.ClassName != "MSFT_StoragePool"
                || poolSourceState.ReadState != FieldReadState.Returned)
                throw new InvalidDataException("The physical pool association is not from a complete current provider query.");
            var primordial = RequireFactBoolean(poolSource, "IsPrimordial");
            _ = RequireFactText(poolSource, "UniqueId");
            _ = RequireFactText(poolSource, "ObjectId");
            var projectedPool = topology.Snapshot.StoragePools.SingleOrDefault(item =>
                item.StableId == poolSource.Id)
                ?? throw new InvalidDataException("A current pool association has no unique projected pool.");
            if (projectedPool.IsPrimordial != primordial
                || !projectedPool.MemberPhysicalDiskIds.Contains(physical.StableId))
                throw new InvalidDataException("The exact pool association disagrees with the current storage projection.");
            if (!primordial)
            {
                factConcretePools.Add(poolSource.Id);
                if (projectedPool.MemberPhysicalDiskIds.Count != 1
                    || projectedPool.MemberPhysicalDiskIds[0] != physical.StableId)
                    throw new NotSupportedException("Creation support requires a single-member concrete pool.");
            }

            var subsystemParents = relations.Where(item => item.Kind == "subsystem-pool"
                && item.ToId == poolSource.Id).ToArray();
            if (subsystemParents.Length != 1)
                throw new InvalidDataException("A current pool lacks one exact storage subsystem association.");
            var subsystemSource = topology.Facts.Objects.SingleOrDefault(item =>
                item.Id == subsystemParents[0].FromId
                && item.ObjectType == FactObjectType.StorageSubsystem
                && item.HasReliableIdentity)
                ?? throw new InvalidDataException("The pool subsystem has no exact current provider identity.");
            var subsystemSourceState = topology.Facts.Sources.Single(item =>
                item.Id == subsystemSource.SourceRef);
            if (subsystemSourceState.ClassName != "MSFT_StorageSubSystem"
                || subsystemSourceState.ReadState != FieldReadState.Returned)
                throw new InvalidDataException("The exact storage subsystem query is incomplete.");
            _ = RequireFactText(subsystemSource, "UniqueId");
            _ = RequireFactText(subsystemSource, "ObjectId");
            if (projectedPool.SubsystemStableId != subsystemSource.Id)
                throw new InvalidDataException("The pool's projected subsystem differs from its exact current association.");
            subsystemIds.Add(subsystemSource.Id);
            associationEvidence.Add(membership);
            associationEvidence.Add(subsystemParents[0]);
        }

        var projectedConcretePools = topology.Snapshot.StoragePools
            .Where(item => !item.IsPrimordial
                && item.MemberPhysicalDiskIds.Contains(physical.StableId))
            .Select(item => item.StableId).Order(StringComparer.Ordinal).ToArray();
        if (!projectedConcretePools.SequenceEqual(factConcretePools.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidDataException("The projected and fact-level concrete pool memberships disagree.");
        if (subsystemIds.Count != 1)
            throw new InvalidDataException("The physical member is associated with more than one storage subsystem.");

        var subsystem = topology.Facts.Objects.Single(item => item.Id == subsystemIds.Single());
        var subsystemUnique = RequireFactText(subsystem, "UniqueId");
        var subsystemObject = RequireFactText(subsystem, "ObjectId");
        if (!topology.Snapshot.StorageSubsystems.Any(item => item.StableId == subsystem.Id))
            throw new InvalidDataException("The exact current subsystem is absent from the projected snapshot.");
        return new CreationSubsystemEvidence(subsystem.Id, subsystemUnique,
            subsystemObject, physicalUniqueId, physicalObjectId,
            associationEvidence.ToArray());
    }

    private static CreationPoolEvidence? RequireCurrentCreationPool(
        WindowsRealStorageTopology topology,
        PhysicalDiskInfo physical)
    {
        var pools = topology.Snapshot.StoragePools.Where(item =>
            !item.IsPrimordial && item.MemberPhysicalDiskIds.Contains(physical.StableId)).ToArray();
        if (pools.Length > 1)
            throw new NotSupportedException("Creation support requires at most one current concrete pool.");
        if (pools.Length == 0)
            return null;

        var pool = pools[0];
        var target = new StorageObjectId(topology.SystemId,
            StorageObjectKind.StoragePool, pool.StableId);
        var source = topology.RequireObject(target);
        var uniqueId = RequireFactText(source, "UniqueId");
        var objectId = RequireFactText(source, "ObjectId");
        if (RequireFactBoolean(source, "IsPrimordial")
            || pool.MemberPhysicalDiskIds.Count != 1
            || pool.MemberPhysicalDiskIds[0] != physical.StableId)
            throw new InvalidDataException("The exact current concrete pool identity or physical member is incomplete.");
        return new CreationPoolEvidence(pool.StableId, uniqueId, objectId);
    }

    private static string RequireFactText(WinPoolSourceObject source, string name) =>
        source.Field(name) is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
        && value.GetString() is { Length: > 0 } result && !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new InvalidDataException("The exact current storage identity field is unavailable: " + name);

    private static bool RequireFactBoolean(WinPoolSourceObject source, string name) =>
        source.Field(name) is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.True or JsonValueKind.False } value }
            ? value.GetBoolean()
            : throw new InvalidDataException("The exact current storage safety field is unavailable: " + name);

    private static ulong RequireFactUInt64(WinPoolSourceObject source, string name) =>
        source.Field(name) is { ValueType: FactValueType.UInt64,
            ReadState: FieldReadState.Returned, Value: { } value }
        && value.TryGetUInt64(out var result)
            ? result
            : throw new InvalidDataException("The exact current storage field is unavailable: " + name);

    private static string RelationshipKey(WinPoolFactRelationship relation) =>
        relation.FromId + "\0" + relation.Kind + "\0" + relation.ToId;

    private static StoragePoolInfo RequireTierPool(StorageSnapshot snapshot,
        RealTargetClosure closure, string? poolId)
    {
        var pool = snapshot.StoragePools.SingleOrDefault(item => item.StableId == poolId);
        var physical = snapshot.PhysicalDisks.Single(item => item.StableId == closure.PhysicalDiskId);
        if (pool is null || pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
            || pool.MemberPhysicalDiskIds[0] != physical.StableId
            || !physical.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
            || snapshot.VirtualDisks.Count(item => item.PoolStableId == pool.StableId) > 1)
            throw new InvalidDataException("The tier operation requires an exact single-HDD, single-VD pool.");
        return pool;
    }

    private static StorageTierInfo RequireHddTier(StorageSnapshot snapshot,
        StoragePoolInfo pool, string tierId)
    {
        var tier = snapshot.StorageTiers.SingleOrDefault(item => item.StableId == tierId);
        if (tier is null || tier.PoolStableId != pool.StableId
            || !tier.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
            || !tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
            || tier.Interleave != 65536 || tier.NumberOfColumns != 1)
            throw new InvalidDataException("The exact HDD tier does not retain the enabled Simple/65536/one-column layout.");
        return tier;
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

        if (proposal.Intent == OperationIntent.InitializeDisk)
        {
            var removed = proposal.Steps.TakeWhile(item => item.Id != step.Id)
                .Select(item => item.Command).OfType<DeletePartitionCommand>()
                .Where(item => item.Partition.Existing.HasValue)
                .Select(item => item.Partition.Existing!.Value.ProviderKey)
                .ToHashSet(StringComparer.Ordinal);
            current = current.Where(item => !removed.Contains(item.StableId)).ToArray();
        }

        var end = checked(command.OffsetBytes + command.SizeBytes);
        var geometry = EditWorkspace.GetRealPartitionCreateGeometry(diskSize,
            command.OffsetBytes, diskSize - command.OffsetBytes);
        if (geometry is not { CanCreate: true, StartOffsetBytes: long start, MaximumSizeBytes: long maximum }
            || start != command.OffsetBytes || command.SizeBytes > maximum
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

    private static void ValidateInitializationContinuation(
        WindowsRealStorageTopology topology, RealOperationIntentRequest proposal)
    {
        var diskId = proposal.Targets.Single(item => item.Kind == StorageObjectKind.OsDisk);
        var disk = topology.Snapshot.OsDisks.Single(item => item.StableId == diskId.ProviderKey);
        if (disk.PartitionStyle != "GPT" || disk.IsOffline || disk.IsBoot || disk.IsSystem)
            throw new InvalidDataException("Initialization continuation requires the exact online non-system GPT disk.");
        var partitions = topology.Snapshot.Partitions.Where(item => item.OsDiskStableId == disk.StableId).ToArray();
        var removals = proposal.Steps.Select(item => item.Command).OfType<DeletePartitionCommand>().ToArray();
        if (partitions.Length > 1
            || partitions.Length == 1 && (!IsProviderInitializationMsr(topology, partitions[0])
                || removals.Length != 1 || removals[0].Partition.Existing?.ProviderKey != partitions[0].StableId)
            || partitions.Length == 0 && removals.Length != 0)
            throw new InvalidDataException("Initialization continuation needs zero partitions or its explicitly listed sole provider MSR.");
    }

    private static bool IsProviderInitializationMsr(WindowsRealStorageTopology topology, PartitionInfo partition) =>
        !partition.IsBoot && !partition.IsSystem
        && Guid.TryParse(partition.PartitionTypeId, out var role)
        && role == Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae")
        && Guid.TryParse(partition.GptType, out var gptRole) && gptRole == role
        && Guid.TryParse(partition.Guid, out _)
        && partition.Offset == 17408 && partition.Size == 16759808
        && string.IsNullOrWhiteSpace(partition.DriveLetter)
        && partition.FileSystem is "" or "RAW"
        && !topology.Snapshot.Volumes.Any(item => item.PartitionStableId == partition.StableId)
        && HasEmptyPartitionMountFacts(topology, partition);

    internal static bool HasEmptyPartitionMountFacts(
        WindowsRealStorageTopology topology, PartitionInfo partition)
    {
        var raw = topology.RequireObject(new StorageObjectId(topology.SystemId,
            StorageObjectKind.Partition, partition.StableId));
        var letter = raw.Field("DriveLetter");
        var paths = raw.Field("AccessPaths");
        return letter is { ReadState: FieldReadState.Returned }
            && (letter.Value is null || letter.Value.Value.ValueKind == JsonValueKind.Null
                || letter.Value.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(letter.Value.Value.GetString()))
            && paths is { ReadState: FieldReadState.Returned }
            && (paths.Value is null || paths.Value.Value.ValueKind == JsonValueKind.Null
                || paths.Value.Value.ValueKind == JsonValueKind.Array && paths.Value.Value.GetArrayLength() == 0);
    }

    private static (long Minimum, long Maximum) AllowedResizeRange(
        StorageSnapshot snapshot,
        PartitionInfo partition,
        PartitionSupportedSize provider)
    {
        var disk = snapshot.OsDisks.SingleOrDefault(item =>
            item.StableId == partition.OsDiskStableId)
            ?? throw new InvalidDataException("The partition has no exact OS disk.");
        const long mib = 1024L * 1024;
        var currentEnd = checked(partition.Offset + partition.Size);
        var conservativeDiskEnd = checked(disk.Size - mib);
        var nextOffset = snapshot.Partitions.Where(item =>
                item.OsDiskStableId == disk.StableId
                && item.StableId != partition.StableId
                && item.Offset >= currentEnd)
            .Select(item => item.Offset).DefaultIfEmpty(conservativeDiskEnd).Min();
        var geometryMaximum = checked(Math.Max(currentEnd, nextOffset) - partition.Offset);
        var unalignedMinimum = Math.Max(mib, provider.MinimumBytes);
        var unalignedMaximum = Math.Min(provider.MaximumBytes, geometryMaximum);
        var minimum = checked((unalignedMinimum + mib - 1) / mib * mib);
        var maximum = unalignedMaximum / mib * mib;
        if (minimum > maximum)
            throw new InvalidDataException("The provider and current geometry have no whole-MiB resize range.");
        return (minimum, maximum);
    }

    private static bool IsSupportedResizeFileSystem(
        StorageSnapshot snapshot, PartitionInfo partition)
    {
        var volumes = snapshot.Volumes.Where(item =>
            StringComparer.Ordinal.Equals(item.PartitionStableId, partition.StableId)).ToArray();
        if (volumes.Length > 1) return false;
        var formats = new[] { partition.FileSystem }
            .Concat(volumes.Select(item => item.FileSystem))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return formats.All(item => item is "NTFS" or "RAW" or "REFS") && formats.Length <= 1;
    }

    private static bool IsRefsPartition(StorageSnapshot snapshot, PartitionInfo partition) =>
        partition.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
        || snapshot.Volumes.Any(item => item.PartitionStableId == partition.StableId
            && item.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

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
            return (command.FileSystem is RealFileSystem.Ntfs or RealFileSystem.ExFat or RealFileSystem.ReFs)
                && command.ClusterBytes == 65536 && !(command.FileSystem == RealFileSystem.ReFs && command.Full);
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

    private bool IsLetterUsed(StorageSnapshot snapshot, char letter) =>
        snapshot.Partitions.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
        || snapshot.Volumes.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
        || snapshot.NetworkDisks.Any(item => item.DriveLetter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
        || logicalDriveRoots().Any(root => root.Length >= 2 && root[1] == ':'
            && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(letter));

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

    private static string DescribeBefore(
        RealStorageCommand command,
        RealTargetClosure closure,
        WindowsRealStorageTopology topology)
    {
        var physical = topology.Snapshot.PhysicalDisks.Single(item =>
            item.StableId == closure.PhysicalDiskId);
        var physicalReference = RealTargetReference.ForExisting(new StorageObjectId(
            topology.SystemId, StorageObjectKind.PhysicalDisk, physical.StableId));
        var physicalTarget = WindowsRealStorageTargetBuilder.Build(topology,
            physicalReference, new Dictionary<string, string>());
        var reference = WindowsRealStorageTargetBuilder.GetReference(command);
        var source = reference.Existing is null ? null
            : WindowsRealStorageTargetBuilder.Build(topology, reference,
                new Dictionary<string, string>());
        var identity = reference.Existing is { } existing
            ? reference.Kind + " ID " + existing.ProviderKey
                + ", UniqueId " + source!.UniqueId + ", ObjectId " + source.ObjectId
                + (source.PartitionGuid.Length == 0 ? string.Empty
                    : ", partition GUID " + source.PartitionGuid)
                + (source.PartitionNumber is null ? string.Empty
                    : ", partition " + source.PartitionNumber.Value.ToString(CultureInfo.InvariantCulture))
                + (source.OffsetBytes is null ? string.Empty
                    : ", offset " + source.OffsetBytes.Value.ToString(CultureInfo.InvariantCulture)
                        + " bytes")
                + (source.SizeBytes is null ? string.Empty
                    : ", size " + source.SizeBytes.Value.ToString(CultureInfo.InvariantCulture)
                        + " bytes")
            : "new " + reference.Kind + " from prior step " + reference.CreatedByStep;
        var virtualOsDisks = reference.Kind == StorageObjectKind.VirtualDisk
            && reference.Existing is { } virtualId
            ? topology.Snapshot.OsDisks.Where(item =>
                item.VirtualDiskStableId == virtualId.ProviderKey).ToArray()
            : [];
        if (command is DeleteVirtualDiskCommand && virtualOsDisks.Length != 1)
            throw new InvalidDataException("The selected virtual disk must have one exact OS disk before removal.");
        var osDisk = source?.DiskNumber is { } number
            ? topology.Snapshot.OsDisks.SingleOrDefault(item => item.Number == number)
            : virtualOsDisks.Length == 1 ? virtualOsDisks[0]
            : topology.Snapshot.OsDisks.SingleOrDefault(item =>
                item.PhysicalDiskStableId == physical.StableId);
        var osDiskTarget = osDisk is null ? null
            : WindowsRealStorageTargetBuilder.Build(topology,
                RealTargetReference.ForExisting(new StorageObjectId(topology.SystemId,
                    StorageObjectKind.OsDisk, osDisk.StableId)),
                new Dictionary<string, string>());
        return "Physical disk " + physical.Model + ", serial " + physical.SerialNumber
            + ", PhysicalDisk ID " + physical.StableId
            + ", PhysicalDisk UniqueId " + physicalTarget.PhysicalMemberUniqueId
            + "; OS disk " + (osDisk?.Number.ToString(CultureInfo.InvariantCulture)
                ?? "created by prior step")
            + ", OS disk ID " + (osDisk?.StableId ?? "pending")
            + ", OS disk UniqueId " + (osDiskTarget?.OsDiskUniqueId ?? "pending")
            + "; step target " + identity
            + "; command " + command.GetType().Name
            + "; topology fingerprint " + closure.Fingerprint;
    }

    private static string DescribeAfter(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand value => value.Online ? "Exact disk online" : "Exact disk offline",
        ClearDiskCommand => RealOperationValidator.ClearDiskExpectedFinalState,
        InitializeGptCommand => "Exact disk GPT; Windows may create its own unformatted MSR; physical identity unchanged; any MSR normalization needs a separately confirmed plan",
        CreatePartitionCommand value => "Partition " + value.Role + " at "
            + value.OffsetBytes.ToString(CultureInfo.InvariantCulture) + " bytes, "
            + value.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        DeletePartitionCommand => "Only selected partition and attached volume absent",
        ResizePartitionCommand value => "Selected partition total size "
            + value.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        FormatVolumeCommand value => "Selected partition formatted " + value.FileSystem
            + " with " + value.ClusterBytes.ToString(CultureInfo.InvariantCulture)
            + " byte clusters; " + (value.Full ? "full format" : "quick format")
            + "; label " + (string.IsNullOrEmpty(value.Label) ? "(empty)" : value.Label),
        SetDriveLetterCommand value => "Selected partition drive letter "
            + (value.PreviousLetter?.ToString() ?? "none") + " -> "
            + (value.NewLetter?.ToString() ?? "none")
            + "; other mount paths preserved",
        RenameVolumeCommand value => "Selected volume label " + value.Label,
        CreatePoolCommand value => "Single-member pool " + value.Name,
        CreateVirtualDiskCommand value => "Single-column Simple/Fixed virtual disk "
            + value.Name + ", " + value.SizeBytes.ToString(CultureInfo.InvariantCulture)
            + " bytes, 65536-byte interleave",
        DeleteVirtualDiskCommand => "Selected virtual disk and its OS disk, partitions and volumes absent",
        DeletePoolCommand => "Selected pool absent; physical member released as observed",
        RenamePoolCommand value => "Selected pool name " + value.Name,
        RenameVirtualDiskCommand value => "Selected virtual disk name " + value.Name,
        CreateTierCommand value => "Unused HDD tier template " + value.Name + "; Simple, 65536-byte interleave, one column; no allocated size",
        CreateTieredVirtualDiskCommand value => "Single-HDD Simple/Fixed tiered virtual disk " + value.Name
            + ", " + value.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes; sole exact template associated",
        DeleteTierCommand => "Only the exact unused HDD tier template absent",
        RenameTierCommand value => "Exact HDD tier name " + value.Name + "; identity and associations retained",
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
            + value.VirtualDisk.Existing?.ProviderKey + "; OS disk "
            + string.Join(", ", snapshot.OsDisks.Where(disk =>
                disk.VirtualDiskStableId == value.VirtualDisk.Existing?.ProviderKey)
                .Select(disk => disk.StableId + " (#"
                    + disk.Number.ToString(CultureInfo.InvariantCulture) + ")")) + ": "
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
                    + ", volume " + (volume?.StableId ?? "none")
                    + ", access paths " + (volume is null ? "none" : string.Join(", ", volume.AccessPaths));
            }).ToArray();
        return items.Length == 0 ? "no existing partitions" : string.Join(" | ", items);
    }

    private sealed record CreationSubsystemEvidence(
        string StableId,
        string UniqueId,
        string ObjectId,
        string PhysicalUniqueId,
        string PhysicalObjectId,
        IReadOnlyList<WinPoolFactRelationship> Associations);

    private sealed record CreationPoolEvidence(string Id, string UniqueId, string ObjectId);
}
