using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealOperationPlanner
{
    internal async Task<WindowsRealStorageSafetyEvidence?> ValidateObservedExactMemberSafetyAsync(WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, CancellationToken ct, SetDiskOnlineCommand? diskState = null)
    {
        var evidence = diskState is null
            ? await safetyInspector.InspectObservedAsync(topology, closure, ct).ConfigureAwait(false)
            : await safetyInspector.InspectObservedDiskStateAsync(topology, closure, diskState, ct).ConfigureAwait(false);
        if (evidence?.PhysicalMemberRoleEvidence is not { } roles || roles.Count != closure.Members.Count
            || closure.Members.Any(member => roles.SingleOrDefault(role => role.PhysicalStableId == member.PhysicalDiskId) is not { } role
                || role.InventoryVersion != topology.InventoryVersion || role.PoolStableId != member.PoolMemberRoleEvidence.PoolStableId
                || role.IsBoot || role.IsSystem || role.IsPageFile || role.IsCrashDump))
            throw new InvalidDataException("Observed storage state lacks the complete current exact-member role proof.");
        return evidence;
    }
    private static RealExactPhysicalMemberSetClosure ResolveSupportMemberSet(WindowsRealStorageTopology topology, StorageObjectId physical)
    {
        var pool = topology.Snapshot.StoragePools.SingleOrDefault(item => !item.IsPrimordial && item.MemberPhysicalDiskIds.Contains(physical.ProviderKey));
        return pool is null ? topology.RequireSelectedPhysicalMemberSet([physical], requirePoolable: false)
            : topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, pool.StableId),
                pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray());
    }

    internal static RealExactPhysicalMemberSetClosure? ResolveCurrentPoolMemberSet(WindowsRealStorageTopology topology, StorageObjectId target)
    {
        var pool = topology.FindConcretePoolForTarget(target);
        return pool is null ? null : topology.RequireExactPhysicalMemberSet(
            new(topology.SystemId, StorageObjectKind.StoragePool, pool.StableId),
            pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray());
    }
    internal static bool UsesExactMemberSet(RealOperationIntentRequest request) =>
        !IsLegacyCombinedPoolCreation(request) && (
        request.Targets.Any(item => item.Kind == StorageObjectKind.PhysicalDisk) && OperationSecurityCatalog.IsStorageStructureMutation(request.Intent)
        || request.Steps.Any(item => item.Command is
            CreatePoolCommand { Members: not null } or SetPhysicalDiskUsageCommand or CreateVirtualDiskCommand { Layout: not null }
            or CreateTierCommand { Layout: not null } or CreateTierCommand { MediaType: not null }
            or CreateTieredVirtualDiskCommand { Layout: not null } or CreateTieredVirtualDiskCommand { CapacityTiers: not null }));

    private static bool IsLegacyCombinedPoolCreation(RealOperationIntentRequest request) =>
        request.Steps.Count > 1 && request.Targets.Count(item => item.Kind == StorageObjectKind.PhysicalDisk) == 1
        && request.Steps.Any(item => item.Command is CreatePoolCommand { Members: null })
        && !request.Steps.Any(item => item.Command is CreatePoolCommand { Members: not null } or SetPhysicalDiskUsageCommand
            or CreateVirtualDiskCommand { Layout: not null } or CreateTierCommand { Layout: not null } or CreateTierCommand { MediaType: not null }
            or CreateTieredVirtualDiskCommand { Layout: not null } or CreateTieredVirtualDiskCommand { CapacityTiers: not null });

    internal static bool UsesExactMemberSet(OperationPlan plan) => plan.RealOperation?.SupportEvidence.StartsWith(
        "Windows exact selected physical-member set;", StringComparison.Ordinal) == true;

    internal static RealExactPhysicalMemberSetClosure ResolveExactMemberSet(WindowsRealStorageTopology topology,
        IReadOnlyList<StorageObjectId> targets, IReadOnlySet<string>? verifiedRemovedTargetIds = null)
    {
        var members = targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (members.Length == 0 || targets.Any(item => item.System != topology.SystemId))
            throw new InvalidDataException("A real structure plan must declare its complete exact physical member set.");
        var memberIds = members.Select(item => item.ProviderKey).ToHashSet(StringComparer.Ordinal);
        var pools = topology.Snapshot.StoragePools.Where(item => !item.IsPrimordial && item.MemberPhysicalDiskIds.Any(memberIds.Contains)).ToArray();
        if (pools.Length > 1) throw new InvalidDataException("A real structure operation cannot span concrete pools.");
        var closure = pools.Length == 1 ? topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, pools[0].StableId), members)
            : topology.RequireSelectedPhysicalMemberSet(members, requirePoolable: false);
        if (targets.Any(item => closure.Objects.All(value => value.Id != item.ProviderKey)
            && !(verifiedRemovedTargetIds?.Contains(item.ProviderKey) == true && item.Kind != StorageObjectKind.PhysicalDisk)))
            throw new InvalidDataException("A declared existing target is outside the exact selected storage component.");
        return closure;
    }

    private async Task<OperationPlan> PrepareExactMemberSetAsync(RealOperationIntentRequest proposal, TrustedRealSession session,
        OperationId operationId, WindowsRealStorageTopology topology, CancellationToken ct)
    {
        var closure = ResolveExactMemberSet(topology, proposal.Targets);
        var steps = new List<RealOperationStep>();
        foreach (var input in proposal.Steps)
        {
            var step = await FreezeExactMemberMaximumAsync(topology, closure, input, ct).ConfigureAwait(false);
            var support = await ValidateExactMemberStepAsync(topology, closure, proposal, step, ct).ConfigureAwait(false);
            steps.Add(step with { BeforeCondition = "Fresh exact selected storage component " + closure.Fingerprint + "; members "
                    + JsonSerializer.Serialize(closure.Members.Select(item => new { item.PhysicalDiskId, item.UniqueId, item.ObjectId, item.SerialNumber })),
                AfterCondition = DescribeExactMemberAfter(step.Command), DataLoss = DescribeLoss(step.Command,
                    closure.AsComponentClosure(step.Command is ClearDiskCommand clear ? ExistingDisk(topology.Snapshot, clear.Disk).PhysicalDiskStableId : null), topology.Snapshot),
                SupportEvidence = support });
        }
        var trusted = proposal with { Steps = steps, ExpectedFinalState = string.Join("; ", steps.Select(item => item.AfterCondition)) };
        RealOperationValidator.Validate(trusted);
        var now = timeProvider.GetUtcNow();
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, topology.MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        return RealOperationPlanFactory.Create(trusted, operationId, environment, session, closure.Fingerprint, closure.Fingerprint,
            closure.PhysicalMemberFingerprint, "Windows exact selected physical-member set; fresh requested layout and provider facts", now,
            now.Add(InMemoryOperationAuthority.DefaultLifetime));
    }

    internal async Task<string> ValidateExactMemberStepAsync(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure,
        RealOperationIntentRequest proposal, RealOperationStep step, CancellationToken ct,
        IReadOnlyDictionary<string, string>? outputs = null,
        Action<WindowsRealStorageSafetyEvidence?>? onSafetyVerified = null)
    {
        outputs ??= new Dictionary<string, string>();
        var safety = await safetyInspector.InspectAsync(topology, closure, step.Command, ct).ConfigureAwait(false);
        onSafetyVerified?.Invoke(safety);
        if (safety?.PhysicalMemberRoleEvidence is not { } roles || roles.Count != closure.Members.Count)
            throw new InvalidDataException("Every physical member requires current Windows safety evidence.");
        foreach (var member in closure.Members)
        {
            var role = roles.SingleOrDefault(item => item.PhysicalStableId == member.PhysicalDiskId);
            if (role is null || role.InventoryVersion != topology.InventoryVersion || role.PoolStableId != member.PoolMemberRoleEvidence.PoolStableId
                || role.VerificationMethod != "CompleteCurrentPoolAndOsDiskAssociations"
                || !role.AssociatedOsDiskIds.SequenceEqual(member.PoolMemberRoleEvidence.AssociatedOsDiskIds, StringComparer.Ordinal)
                || role.IsBoot || role.IsSystem || role.IsPageFile || role.IsCrashDump)
                throw new InvalidDataException("Per-member safety evidence differs from the current exact closure.");
        }
        var snapshot = topology.Snapshot;
        var evidence = "exact-member-safety:" + JsonSerializer.Serialize(safety);
        var pool = closure.IsSelectedFreeSet ? null : snapshot.StoragePools.Single(item => item.StableId == closure.PoolId);
        switch (step.Command)
        {
            case CreatePoolCommand create:
                if (!closure.IsSelectedFreeSet || !create.EffectiveMembers.Select(item => item.Existing!.Value.ProviderKey).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(closure.Members.Select(item => item.PhysicalDiskId)))
                    throw new InvalidDataException("Pool creation must use exactly the selected free member set.");
                foreach (var member in closure.Members)
                {
                    var physical = snapshot.PhysicalDisks.Single(item => item.StableId == member.PhysicalDiskId);
                    var disks = snapshot.OsDisks.Where(item => item.PhysicalDiskStableId == physical.StableId).ToArray();
                    if (!physical.CanPool || disks.Any(item => item.PartitionStyle != "RAW" || snapshot.Partitions.Any(part => part.OsDiskStableId == item.StableId)))
                        throw new InvalidDataException("Pool creation requires poolable, partition-free RAW members.");
                }
                return evidence + await RequireExactMemberCapabilityAsync(topology, closure, "SupportsStoragePoolCreation", ct).ConfigureAwait(false);
            case SetPhysicalDiskUsageCommand usage:
                if (pool is null || usage.PhysicalDisk.Existing is not { } usageId || !pool.MemberPhysicalDiskIds.Contains(usageId.ProviderKey)
                    || PhysicalDiskUsage.Normalize(usage.Usage) is not (PhysicalDiskUsage.AutoSelect or PhysicalDiskUsage.HotSpare or PhysicalDiskUsage.Retired))
                    throw new InvalidDataException("Usage requires one exact current pool member and a canonical editor role.");
                if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId))
                    throw new NotSupportedException("Member role setup is limited to the new pool before virtual-disk allocation.");
                return evidence;
            case CreateTierCommand tier:
                RequireConcretePool(pool);
                if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool!.StableId)
                    || snapshot.StorageTiers.Any(item => item.PoolStableId == pool!.StableId && item.MediaType == tier.EffectiveMediaType))
                    throw new InvalidDataException("A new media template needs no virtual disk and no duplicate template for its media.");
                ValidateDataLayout(topology, closure, tier.EffectiveLayout, tier.EffectiveMediaType);
                return evidence + await RequireExactMemberCapabilityAsync(topology, closure, "SupportsStorageTierCreation", ct).ConfigureAwait(false);
            case CreateVirtualDiskCommand ordinary:
                RequireConcretePool(pool);
                if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool!.StableId) || snapshot.StorageTiers.Any(item => item.PoolStableId == pool!.StableId))
                    throw new InvalidDataException("Ordinary creation requires an empty concrete pool without templates.");
                ValidateDataLayout(topology, closure, ordinary.EffectiveLayout);
                var ordinaryTarget = BuildExactMemberTarget(topology, closure, ordinary, outputs);
                if (!ordinary.UseMaximumSize && !(await virtualDiskSizes.ReadAsync(ordinaryTarget, ct).ConfigureAwait(false)).Supports(ordinary.SizeBytes))
                    throw new InvalidDataException("Requested ordinary size is outside the current requested-resiliency provider range.");
                if (ordinary.UseMaximumSize) RequireExactPolicy(ordinary.MaximumCapacity, closure);
                return evidence;
            case CreateTieredVirtualDiskCommand tiered:
                RequireConcretePool(pool);
                if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool!.StableId)) throw new InvalidDataException("The new virtual disk requires an empty pool.");
                var inputs = TierInputs(tiered);
                var templates = snapshot.StorageTiers.Where(item => item.PoolStableId == pool!.StableId && item.VirtualDiskStableId is null).ToArray();
                if (templates.Length != inputs.Count || inputs.Select(item => item.Tier.Existing?.ProviderKey).Distinct().Count() != inputs.Count)
                    throw new InvalidDataException("The complete unique unused template set is required.");
                foreach (var input in inputs)
                {
                    var template = RequireTemplate(topology, closure, input.Tier);
                    var layout = input.Layout ?? LayoutOf(template);
                    if (layout != LayoutOf(template) || input.MediaType is { } media && media != template.MediaType)
                        throw new InvalidDataException("Requested tier parameters differ from the exact provider template.");
                    ValidateDataLayout(topology, closure, layout, template.MediaType);
                    if (input.UsesMaximumSize(tiered.UseMaximumSize)) RequireExactPolicy(input.MaximumCapacity ?? tiered.MaximumCapacity, closure);
                    else if (input.SizeBytes is not > 0 || !(await capabilities.ReadTierCreationSizeAsync(topology, input.Tier.Existing!.Value, ct).ConfigureAwait(false)).Supports(input.SizeBytes.Value))
                        throw new InvalidDataException("Requested explicit tier size is outside its exact provider sequence.");
                }
                return evidence + await RequireExactMemberCapabilityAsync(topology, closure, "SupportsStorageTieredVirtualDiskCreation", ct).ConfigureAwait(false);
            case DeletePoolCommand:
                RequireConcretePool(pool);
                var prior = proposal.Steps.TakeWhile(item => item.Id != step.Id).Select(item => item.Command).ToArray();
                if (snapshot.VirtualDisks.Any(item => item.PoolStableId == pool!.StableId && !prior.OfType<DeleteVirtualDiskCommand>().Any(command => command.VirtualDisk.Existing?.ProviderKey == item.StableId))
                    || snapshot.StorageTiers.Any(item => item.PoolStableId == pool!.StableId && item.VirtualDiskStableId is null && !prior.OfType<DeleteTierCommand>().Any(command => command.Tier.Existing?.ProviderKey == item.StableId)))
                    throw new InvalidDataException("Delete every exact pool child before deleting the pool.");
                return evidence;
            case DeleteTierCommand delete:
                var selectedTier = snapshot.StorageTiers.Single(item => item.StableId == delete.Tier.Existing!.Value.ProviderKey);
                if (selectedTier.VirtualDiskStableId is not null) throw new InvalidDataException("Only an unused template may be deleted separately.");
                return evidence + await RequireExactMemberCapabilityAsync(topology, closure, "SupportsStorageTierDeletion", ct).ConfigureAwait(false);
            case DeleteVirtualDiskCommand deleteVirtual:
                RequireConcretePool(pool);
                if (snapshot.VirtualDisks.Count(item => item.PoolStableId == pool!.StableId) != 1
                    || snapshot.OsDisks.Count(item => item.VirtualDiskStableId == deleteVirtual.VirtualDisk.Existing!.Value.ProviderKey) != 1)
                    throw new InvalidDataException("Delete requires the pool's sole exact virtual disk and one observed OS-disk association.");
                return evidence;
            case RenameTierCommand renameTier:
                RequireConcretePool(pool);
                var renamedTier = snapshot.StorageTiers.Single(item => item.StableId == renameTier.Tier.Existing!.Value.ProviderKey);
                if (renamedTier.FriendlyName == renameTier.Name) throw new InvalidDataException("The requested tier name is unchanged.");
                return evidence + await RequireExactMemberCapabilityAsync(topology, closure, "SupportsStorageTierFriendlyNameModification", ct).ConfigureAwait(false);
            case RenamePoolCommand:
            case RenameVirtualDiskCommand:
                RequireConcretePool(pool);
                return evidence;
            default:
                return await ValidateCurrentStepAsync(topology, closure.AsComponentClosure(), proposal, step, ct, outputs,
                    exactMemberSet: closure, verifiedSafety: safety).ConfigureAwait(false);
        }
    }

    internal static WindowsStorageCommandTarget BuildExactMemberTarget(WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, RealStorageCommand command, IReadOnlyDictionary<string, string> outputs)
    {
        var reference = WindowsRealStorageTargetBuilder.GetReference(command);
        var target = WindowsRealStorageTargetBuilder.BuildForMemberSet(topology, reference, outputs, closure)
            with { CreatedInThisPlan = reference.CreatedByStep is not null };
        if (command is FormatVolumeCommand format && (format.FileSystem == RealFileSystem.ReFs
                || format.ClusterBytes != 65536 && Guid.TryParse(target.PartitionTypeGuid, out var partitionRole)
                && partitionRole == Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"))
            || command is ResizePartitionCommand && target.Kind == StorageObjectKind.Partition
                && topology.Snapshot.Volumes.Any(item => item.PartitionStableId == WindowsRealStorageTargetBuilder.ResolveId(topology, reference, outputs).ProviderKey
                    && item.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
        {
            var partition = WindowsRealStorageTargetBuilder.ResolveId(topology, reference, outputs);
            var volume = topology.Snapshot.Volumes.Single(item => item.PartitionStableId == partition.ProviderKey);
            var source = topology.RequireObject(new(topology.SystemId, StorageObjectKind.Volume, volume.StableId));
            target = target with { RelatedUniqueId = RequireFactText(source, "UniqueId"), RelatedObjectId = RequireFactText(source, "ObjectId") };
        }
        if (command is RenameTierCommand rename)
        {
            var tierId = WindowsRealStorageTargetBuilder.ResolveId(topology, rename.Tier, outputs);
            var tier = topology.Snapshot.StorageTiers.Single(item => item.StableId == tierId.ProviderKey);
            if (tier.VirtualDiskStableId is { } ownerId)
            {
                var owner = topology.RequireObject(new(topology.SystemId, StorageObjectKind.VirtualDisk, ownerId));
                target = target with { RelatedUniqueId = RequireFactText(owner, "UniqueId"), RelatedObjectId = RequireFactText(owner, "ObjectId") };
            }
        }
        return command switch
        {
            SetPhysicalDiskUsageCommand => target with { ParentUniqueId = closure.PoolUniqueId },
            CreateVirtualDiskCommand ordinary => target with { Layout = ordinary.EffectiveLayout },
            CreateTierCommand tier => target with { Layout = tier.EffectiveLayout },
            CreateTieredVirtualDiskCommand tiered => AddTierTargets(topology, closure, target, tiered),
            _ => target
        };
    }

    private static WindowsStorageCommandTarget AddTierTargets(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure,
        WindowsStorageCommandTarget target, CreateTieredVirtualDiskCommand command)
    {
        var inputs = TierInputs(command).Select(input => {
            var template = RequireTemplate(topology, closure, input.Tier);
            var source = topology.RequireObject(input.Tier.Existing!.Value);
            return new WindowsStorageTierTarget(template.StableId, RequireFactText(source, "UniqueId"), RequireFactText(source, "ObjectId"), template.MediaType,
                input.SizeBytes ?? command.SizeBytes, input.Layout ?? LayoutOf(template), input.UsesMaximumSize(command.UseMaximumSize));
        }).ToArray();
        return target with { Layout = command.EffectiveLayout, TierInputs = inputs, RelatedUniqueId = inputs[0].UniqueId, RelatedObjectId = inputs[0].ObjectId };
    }

    internal static IReadOnlyList<MaximumCapacityTier> TierInputs(CreateTieredVirtualDiskCommand command) => command.CapacityTiers
        ?? [new(command.Tier, command.MaximumCapacity, command.UseMaximumSize ? null : command.SizeBytes, command.UseMaximumSize, command.Layout)];

    internal static StorageLayoutParameters LayoutOf(StorageTierInfo tier)
    {
        if (tier.NumberOfColumns is null || tier.Interleave is null
            || tier.ResiliencySettingName != "Simple" && (tier.NumberOfDataCopies is null || tier.PhysicalDiskRedundancy is null))
            throw new InvalidDataException("The exact provider tier layout is incomplete.");
        var layout = new StorageLayoutParameters(tier.ResiliencySettingName, tier.NumberOfColumns.Value,
            checked((int)tier.Interleave.Value), tier.NumberOfDataCopies ?? 1, tier.PhysicalDiskRedundancy ?? 0);
        layout.Validate();
        return layout;
    }

    private static StorageTierInfo RequireTemplate(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure, RealTargetReference reference)
    {
        if (reference.Existing is not { } id || reference.Kind != StorageObjectKind.StorageTier || id.System != topology.SystemId)
            throw new InvalidDataException("An exact existing provider template is required.");
        _ = topology.RequireObject(id);
        return topology.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == id.ProviderKey && item.PoolStableId == closure.PoolId
            && item.VirtualDiskStableId is null && item.Size == 0 && item.FootprintOnPool == 0)
            ?? throw new InvalidDataException("The exact unused zero-allocation provider template is absent.");
    }

    internal static IReadOnlyList<PhysicalDiskInfo> DataMembers(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure, string? media = null) =>
        topology.Snapshot.PhysicalDisks.Where(item => closure.Members.Any(member => member.PhysicalDiskId == item.StableId)
            && PhysicalDiskUsage.ContributesDataCapacity(item.Usage) && (media is null || item.MediaType == media)).ToArray();

    private static void ValidateDataLayout(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure, StorageLayoutParameters layout, string? media = null)
    {
        var members = DataMembers(topology, closure, media);
        if (members.Count == 0) throw new InvalidDataException("The requested layer has no data-capacity members.");
        _ = layout.EstimateCapacity(members.Select(item => item.Size).ToArray());
    }

    private static void RequireConcretePool(StoragePoolInfo? pool)
    {
        if (pool is null || pool.IsPrimordial) throw new InvalidDataException("Observe the concrete pool before preparing its next structure operation.");
    }

    private async Task<string> RequireExactMemberCapabilityAsync(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure, string field, CancellationToken ct)
    {
        var proofs = new List<WindowsTierCapability>();
        foreach (var member in closure.Members)
        {
            var capability = await capabilities.ReadTierAsync(topology, new(topology.SystemId, StorageObjectKind.PhysicalDisk, member.PhysicalDiskId), ct).ConfigureAwait(false);
            var subsystem = RequireCreationSubsystem(topology, topology.RequireObject(new(topology.SystemId, StorageObjectKind.PhysicalDisk, member.PhysicalDiskId)),
                topology.Snapshot.PhysicalDisks.Single(item => item.StableId == member.PhysicalDiskId));
            if (capability.Status != "queried" || capability.PhysicalDiskStableId != member.PhysicalDiskId
                || capability.ObservedAtUtc == default || capability.ObservedAtUtc > timeProvider.GetUtcNow().AddSeconds(10)
                || timeProvider.GetUtcNow() - capability.ObservedAtUtc > TimeSpan.FromMinutes(2)
                || !capability.Associations.Select(RelationshipKey).Order(StringComparer.Ordinal).SequenceEqual(
                    subsystem.Associations.Select(RelationshipKey).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || capability.SubsystemStableId != closure.SubsystemStableId
                || capability.UniqueId != closure.SubsystemUniqueId || capability.ObjectId != closure.SubsystemObjectId
                || capability.Fields.SingleOrDefault(item => item.Name == field) is not { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.True } }
                || capability.Fields.SingleOrDefault(item => item.Name == "PhysicalDisksPerStoragePoolMin") is not { ReadState: FieldReadState.Returned, Value: { } minimum }
                || !minimum.TryGetUInt64(out var count) || count == 0 || count > (ulong)closure.Members.Count)
                throw new NotSupportedException("The exact subsystem does not verify the requested capability/member count: " + field);
            proofs.Add(capability);
        }
        return ";live-subsystem-capabilities:" + JsonSerializer.Serialize(proofs);
    }

    private static void RequireExactPolicy(MaximumCapacityPolicy? policy, RealExactPhysicalMemberSetClosure closure)
    {
        if (policy is null || policy.AlgorithmVersion != MaximumCapacityAlgorithm.Version || policy.SourceFingerprint != closure.Fingerprint
            || policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes) || policy.PhysicalCapacityBytes <= 0)
            throw new InvalidDataException("The MAX policy must be freshly frozen against the exact selected component.");
    }

    private async Task<RealOperationStep> FreezeExactMemberMaximumAsync(WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, RealOperationStep step, CancellationToken ct)
    {
        if (step.Command is CreateVirtualDiskCommand { UseMaximumSize: true } ordinary)
        {
            var target = BuildExactMemberTarget(topology, closure, ordinary, new Dictionary<string, string>());
            var range = await virtualDiskSizes.ReadAsync(target, ct).ConfigureAwait(false);
            var members = DataMembers(topology, closure);
            var raw = members.Aggregate(0L, (sum, item) => checked(sum + item.Size));
            _ = ordinary.EffectiveLayout.EstimateCapacity(members.Select(item => item.Size).ToArray());
            return step with { Command = ordinary with { MaximumCapacity = FreezeExactPolicy(range, raw, closure,
                "MSFT_StoragePool.GetSupportedSize(" + ordinary.EffectiveLayout.ResiliencySettingName + ")") } };
        }
        if (step.Command is not CreateTieredVirtualDiskCommand tiered) return step;
        var tiers = new List<MaximumCapacityTier>();
        foreach (var input in TierInputs(tiered))
        {
            var template = RequireTemplate(topology, closure, input.Tier);
            var layout = input.Layout ?? LayoutOf(template);
            var normalized = input with { Layout = layout, MediaType = template.MediaType, UseMaximumSize = input.UsesMaximumSize(tiered.UseMaximumSize) };
            if (normalized.UsesMaximumSize(tiered.UseMaximumSize))
            {
                var range = await capabilities.ReadTierCreationSizeAsync(topology, input.Tier.Existing!.Value, ct).ConfigureAwait(false);
                var members = DataMembers(topology, closure, template.MediaType);
                _ = layout.EstimateCapacity(members.Select(item => item.Size).ToArray());
                var raw = members.Aggregate(0L, (sum, item) => checked(sum + item.Size));
                normalized = normalized with { MaximumCapacity = FreezeExactPolicy(range, raw, closure,
                    "MSFT_StorageTier.GetSupportedSize(" + layout.ResiliencySettingName + "); exact data-media-member-set"), SizeBytes = null };
            }
            else normalized = normalized with { MaximumCapacity = null };
            tiers.Add(normalized);
        }
        return step with { Command = tiered with { CapacityTiers = tiers, MaximumCapacity = tiers.FirstOrDefault(item => item.UsesMaximumSize(tiered.UseMaximumSize))?.MaximumCapacity } };
    }

    private MaximumCapacityPolicy FreezeExactPolicy(VirtualDiskCreationSize range, long physicalBytes,
        RealExactPhysicalMemberSetClosure closure, string source)
    {
        const long gib = MaximumCapacityAlgorithm.GiB;
        if (physicalBytes <= gib || range.MaximumBytes <= MaximumCapacityAlgorithm.ReserveBytes
            || range.DivisorBytes <= 0 || gib % range.DivisorBytes != 0 || range.RangeOriginBytes % range.DivisorBytes != 0)
            throw new NotSupportedException("No positive integer-GiB MAX search origin exists for the requested layer.");
        var upper = Math.Min(range.MaximumBytes, physicalBytes);
        var initial = MaximumCapacityAlgorithm.InitialCandidateBytes(upper);
        if (initial <= 0) throw new NotSupportedException("No positive integer-GiB MAX candidate exists.");
        var units = checked(physicalBytes / gib + (physicalBytes % gib == 0 ? 0 : 1));
        return new(MaximumCapacityAlgorithm.Version, upper, initial, physicalBytes, checked((int)(2 * units + 4)),
            JsonSerializer.Serialize(new { Method = source, ProviderRange = range, PhysicalCapacityBytes = physicalBytes }),
            closure.Fingerprint, timeProvider.GetUtcNow());
    }

    private static string DescribeExactMemberAfter(RealStorageCommand command) => command switch
    {
        CreatePoolCommand pool => $"Pool {pool.Name}; exactly {pool.EffectiveMembers.Count} selected members",
        SetPhysicalDiskUsageCommand usage => "Exact physical member usage " + usage.Usage,
        CreateTierCommand tier => "Unused " + tier.EffectiveMediaType + " template " + tier.Name + "; " + JsonSerializer.Serialize(tier.EffectiveLayout),
        CreateVirtualDiskCommand vd => "One Fixed virtual disk " + vd.Name + "; " + (vd.UseMaximumSize ? "integer-GiB MAX search" : vd.SizeBytes + " bytes")
            + "; " + JsonSerializer.Serialize(vd.EffectiveLayout),
        CreateTieredVirtualDiskCommand vd => "One Fixed virtual disk with exact requested per-tier layout and capacity intents; " + JsonSerializer.Serialize(TierInputs(vd)),
        DeleteTierCommand => "Only the exact unused tier template is absent",
        RenameTierCommand tier => "Exact tier name " + tier.Name + "; identity and associations retained",
        _ => DescribeAfter(command)
    };
}
