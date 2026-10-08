using System.Text.Json;
using System.Collections.Concurrent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsVerifiedStepEvidence(
    string PostFingerprint,
    string PhysicalMemberFingerprint,
    string? CreatedObjectId,
    string ProviderCode,
    string? CreatedOsDiskId = null,
    WindowsTieredCreationMapping? TieredCreation = null,
    JsonElement? LiveCapabilityEvidence = null,
    WindowsGptInitializationEvidence? GptInitialization = null,
    WindowsNativeMsrSafetyEvidence? NativeMsrSafetyEvidence = null,
    IReadOnlyList<WindowsVolumeSafetyEvidence>? VolumeSafetyEvidence = null,
    IReadOnlyList<WindowsNativeMsrSafetyEvidence>? OfflinePartitionAttributes = null,
    WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence = null);

public sealed record WindowsGptInitializationEvidence(
    string OsDiskStableId, string OsDiskUniqueId, string OsDiskObjectId,
    string? ProviderMsrStableId, string? ProviderMsrGuid,
    long? ProviderMsrOffsetBytes, long? ProviderMsrSizeBytes);

public sealed record WindowsTieredCreationMapping(
    string TemplateStableId, string TemplateUniqueId, string TemplateObjectId,
    string VirtualDiskStableId, string VirtualDiskUniqueId, string VirtualDiskObjectId,
    string TierInstanceStableId, string TierInstanceUniqueId, string TierInstanceObjectId,
    string PoolStableId, string PhysicalDiskStableId,
    long SizeBytes, string MediaType, string ResiliencySettingName, string ProvisioningType,
    int NumberOfColumns, long Interleave,
    IReadOnlyList<WinPoolFactRelationship> Associations,
    IReadOnlyList<WinPoolSourceField>? VirtualDiskLayoutFacts = null,
    IReadOnlyList<WinPoolSourceField>? TierInstanceLayoutFacts = null);

public sealed record WindowsNoEffectStepEvidence(
    bool NoWindowsCall,
    string Code,
    string PhysicalMemberFingerprint,
    string? Diagnostic = null);

public sealed record WindowsObservedNoEffectStepEvidence(
    bool WindowsCallIssued, string Code, string PhysicalMemberFingerprint,
    string BeforeFingerprint, string FirstObservedFingerprint, string SecondObservedFingerprint,
    WindowsStorageCommandResult ProviderResult,
    IReadOnlyList<WindowsStorageJobAbsenceEvidence> StorageJobQueries);

/// <summary>
/// The only Windows implementation of the Agent's real storage boundary.
/// An IPC reply, a provider return, and a verified postcondition are distinct.
/// </summary>
public sealed class WindowsRealStorageBackend : IRealStorageBackend
{
    private static readonly TimeSpan PostCallWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PostCallPoll = TimeSpan.FromSeconds(2);
    private readonly WindowsRealOperationPlanner planner;
    private readonly WindowsRealStorageTopologyReader topologyReader;
    private readonly IWindowsRealStorageCommandAdapter adapter;
    private readonly TimeProvider timeProvider;
    private readonly IWindowsStorageJobReader storageJobReader;
    // Only provider selectors are retained. Every use collects new unmerged facts.
    private readonly ConcurrentDictionary<OperationId, StorageInventoryScope> operationScopes = new();

    public WindowsRealStorageBackend(
        IWindowsRealStorageCommandAdapter adapter,
        WindowsRealOperationPlanner? planner = null,
        WindowsRealStorageTopologyReader? topologyReader = null,
        TimeProvider? timeProvider = null,
        IWindowsStorageJobReader? storageJobReader = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.topologyReader = topologyReader ?? new WindowsRealStorageTopologyReader();
        this.planner = planner ?? new WindowsRealOperationPlanner(this.topologyReader);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.storageJobReader = storageJobReader ?? new WindowsStorageJobReader();
    }

    public Task<OperationPlan> PrepareAsync(
        RealOperationIntentRequest proposal,
        TrustedRealSession session,
        OperationId operationId,
        CancellationToken cancellationToken) =>
        planner.PrepareAsync(proposal, session, operationId, cancellationToken);

    public Task<RealPartitionResizeRange> ReadPartitionResizeRangeAsync(
        StorageObjectId partition,
        TrustedRealSession session,
        CancellationToken cancellationToken) =>
        planner.ReadPartitionResizeRangeAsync(partition, session, cancellationToken);

    public Task<RealVirtualDiskCreationRange> ReadVirtualDiskCreationRangeAsync(
        StorageObjectId target, TrustedRealSession session, CancellationToken cancellationToken) =>
        planner.ReadVirtualDiskCreationRangeAsync(target, session, cancellationToken);

    public Task<RealStructureCreationSupport> ReadStructureCreationSupportAsync(StorageObjectId physicalTarget,
        bool tiered, TrustedRealSession session, CancellationToken cancellationToken) =>
        planner.ReadStructureCreationSupportAsync(physicalTarget, tiered, session, cancellationToken);

    public async Task<RealStepPreflight> PreflightStepAsync(
        OperationPlan plan,
        RealOperationStep step,
        IReadOnlyDictionary<string, string> verifiedStepOutputs,
        CancellationToken cancellationToken)
    {
        var stored = RequireFrozenStep(plan, step);
        var topology = await CaptureOperationTopologyAsync(plan, step.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(topology.MachineBinding,
                plan.RealOperation!.MachineBinding))
            throw new InvalidDataException("The machine identity changed after preparation.");
        var physicalId = plan.Targets.SingleOrDefault(target =>
            target.Kind == StorageObjectKind.PhysicalDisk);
        if (physicalId.Kind != StorageObjectKind.PhysicalDisk)
            throw new InvalidDataException("The real plan lacks its frozen physical member.");
        var closure = topology.RequireSinglePhysicalClosure([physicalId]);
        operationScopes[plan.OperationId] = StorageInventoryScopeFactory.Create(
            topology.Facts, plan.OperationId, step.Id, [physicalId]);
        if (!StringComparer.Ordinal.Equals(
                closure.PhysicalMemberFingerprint,
                plan.RealOperation.PhysicalMemberFingerprint))
            throw new InvalidDataException("The physical member changed after preparation.");

        var stepIndex = plan.RealOperation.Steps.ToList().FindIndex(item => item.Id == stored.Id);
        var previousFingerprint = plan.RealOperation.TargetFingerprint;
        if (stepIndex > 0)
        {
            var previous = plan.RealOperation.Steps[stepIndex - 1];
            if (!verifiedStepOutputs.TryGetValue(previous.Id, out var previousJson))
                throw new InvalidDataException("The preceding real step has no verified output.");
            var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(previousJson)
                ?? throw new InvalidDataException("The preceding step evidence is unreadable.");
            previousFingerprint = evidence.PostFingerprint;
            if (!StringComparer.Ordinal.Equals(evidence.PhysicalMemberFingerprint,
                    closure.PhysicalMemberFingerprint))
                throw new InvalidDataException("A previous step did not preserve the physical member.");
        }
        if (!StringComparer.Ordinal.Equals(closure.Fingerprint, previousFingerprint))
            throw new InvalidDataException("The relevant storage topology changed outside verified steps.");

        var request = new RealOperationIntentRequest(
            plan.Intent, plan.SystemId, plan.Targets,
            plan.RealOperation.Steps, plan.RealOperation.ExpectedFinalState);
        await planner.ValidateCurrentStepAsync(
            topology, closure, request, stored, cancellationToken,
            verifiedStepOutputs).ConfigureAwait(false);

        var target = WindowsRealStorageTargetBuilder.Build(
            topology, WindowsRealStorageTargetBuilder.GetReference(stored.Command),
            verifiedStepOutputs);
        target = target with
        {
            CreatedInThisPlan = WindowsRealStorageTargetBuilder
                .GetReference(stored.Command).CreatedByStep is not null
        };
        if (stored.Command is CreateTieredVirtualDiskCommand tiered)
        {
            var related = WindowsRealStorageTargetBuilder.Build(
                topology, tiered.Tier, verifiedStepOutputs);
            target = target with
            {
                RelatedUniqueId = related.UniqueId,
                RelatedObjectId = related.ObjectId
            };
        }
        if (stored.Command is RenameTierCommand renameTier)
        {
            var tierId = WindowsRealStorageTargetBuilder.ResolveId(topology, renameTier.Tier, verifiedStepOutputs);
            var tier = topology.Snapshot.StorageTiers.Single(item => item.StableId == tierId.ProviderKey);
            if (tier.VirtualDiskStableId is { } ownerId)
            {
                var owner = topology.RequireObject(new StorageObjectId(topology.SystemId,
                    StorageObjectKind.VirtualDisk, ownerId));
                target = target with { RelatedUniqueId = Text(owner, "UniqueId"), RelatedObjectId = Text(owner, "ObjectId") };
            }
        }
        if (stored.Command is FormatVolumeCommand { FileSystem: RealFileSystem.ReFs }
            || stored.Command is ResizePartitionCommand && target.Kind == StorageObjectKind.Partition
                && topology.Snapshot.Volumes.Any(volume => volume.PartitionStableId ==
                    WindowsRealStorageTargetBuilder.ResolveId(topology,
                        WindowsRealStorageTargetBuilder.GetReference(stored.Command), verifiedStepOutputs).ProviderKey
                    && volume.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
        {
            var partitionId = WindowsRealStorageTargetBuilder.ResolveId(topology,
                WindowsRealStorageTargetBuilder.GetReference(stored.Command), verifiedStepOutputs);
            var volume = topology.Snapshot.Volumes.Single(item => item.PartitionStableId == partitionId.ProviderKey);
            var source = topology.RequireObject(new StorageObjectId(topology.SystemId,
                StorageObjectKind.Volume, volume.StableId));
            target = target with
            {
                RelatedUniqueId = source.Field("UniqueId")!.DisplayValue(),
                RelatedObjectId = source.Field("ObjectId")!.DisplayValue()
            };
        }
        if (!StringComparer.Ordinal.Equals(target.ExpectedFingerprint, closure.Fingerprint))
            throw new InvalidDataException("The exact step target no longer belongs to the frozen physical member.");
        return new RealStepPreflight(
            JsonSerializer.Serialize(target), closure.Fingerprint,
            closure.Fingerprint, closure.PhysicalMemberFingerprint);
    }

    public async Task<RealStepResult> ExecuteStepAsync(
        OperationPlan plan,
        RealOperationStep step,
        RealStepPreflight preflight,
        CancellationToken cancellationToken)
    {
        var stored = RequireFrozenStep(plan, step);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(
            preflight.TargetEvidenceJson)
            ?? throw new InvalidDataException("The persisted exact target is unreadable.");
        var before = await CaptureOperationTopologyAsync(plan, step.Id, cancellationToken)
            .ConfigureAwait(false);
        var physical = plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk);
        var beforeClosure = before.RequireSinglePhysicalClosure([physical]);
        if (!StringComparer.Ordinal.Equals(beforeClosure.Fingerprint, preflight.TargetFingerprint)
            || !StringComparer.Ordinal.Equals(beforeClosure.PhysicalMemberFingerprint,
                preflight.PhysicalMemberFingerprint)
            || !StringComparer.Ordinal.Equals(target.ExpectedFingerprint, preflight.TargetFingerprint))
            return new RealStepResult(RealStepOutcome.FailedWithoutEffect,
                "real.pre_call_facts_changed",
                JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(
                    true, "real.pre_call_facts_changed",
                    beforeClosure.PhysicalMemberFingerprint)));

        WindowsRealStorageSafetyEvidence? safetyEvidence;
        try
        {
            if (stored.Command is FormatVolumeCommand createdFormat
                && (createdFormat.FileSystem == RealFileSystem.Fat32
                    || GuidEquals(target.PartitionTypeGuid, WindowsNativeMsrAttributesReader.EfiRole.ToString())))
            {
                var createdPartition = RequireCreatedEfiFormatTarget(plan, stored, target, before);
                safetyEvidence = await planner.ValidateCreatedPartitionFormatWithEvidenceAsync(
                    before, beforeClosure, createdFormat, createdPartition, cancellationToken).ConfigureAwait(false);
                if (safetyEvidence?.NativePartitionAttributes is not { } native
                    || native.PartitionStableId != createdPartition.ProviderKey
                    || native.InventoryVersion != before.InventoryVersion
                    || native.PartitionType != WindowsNativeMsrAttributesReader.EfiRole
                    || native.DiskPath != target.OsDiskPath || native.DiskUniqueId != target.OsDiskUniqueId
                    || native.PhysicalUniqueId != target.PhysicalMemberUniqueId
                    || native.PhysicalObjectId != target.PhysicalMemberObjectId
                    || native.SerialNumber != target.SerialNumber
                    || !GuidEquals(target.PartitionGuid, native.PartitionGuid.ToString())
                    || native.Offset != target.OffsetBytes || native.Length != target.SizeBytes
                    || native.DiskIsOffline
                    || (native.Attributes & (WindowsNativeMsrAttributes.PlatformRequired
                        | WindowsNativeMsrAttributes.ReadOnly | WindowsNativeMsrAttributes.ShadowCopy)) != 0)
                    throw new InvalidDataException("The newly created EFI format lacks current exact native safety evidence.");
            }
            else
                safetyEvidence = await planner.ValidatePartitionSafetyWithEvidenceAsync(
                    before, beforeClosure, stored.Command, cancellationToken).ConfigureAwait(false);
            RequirePoolMemberRoleProof(before, beforeClosure, safetyEvidence);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No adapter or Windows mutation has started at this point. Probe
            // failures, including COM/WMI errors, are a proven no-call failure.
            return new RealStepResult(RealStepOutcome.FailedWithoutEffect,
                "real.pre_call_safety_unverified",
                JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(
                    true, "real.pre_call_safety_unverified",
                    beforeClosure.PhysicalMemberFingerprint,
                    BoundedDiagnostic(exception.GetType().Name + ": " + exception.Message))));
        }

        WindowsStorageCommandResult provider;
        try
        {
            // The caller has already persisted CallIssued. Once this call starts,
            // transport cancellation may not terminate the storage provider.
            provider = await StorageOperationTiming.MeasureAsync("real.provider", () => adapter.ExecuteAsync(
                stored.Command, target, CancellationToken.None), plan.OperationId, step.Id, "exact-provider")
                .ConfigureAwait(false);
            provider = provider with
            {
                NativeMsrSafetyEvidence = safetyEvidence?.NativePartitionAttributes,
                VolumeSafetyEvidence = safetyEvidence?.VolumeSafetyEvidence,
                OfflinePartitionAttributes = safetyEvidence?.OfflinePartitionAttributes,
                PoolMemberRoleEvidence = safetyEvidence?.PoolMemberRoleEvidence
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new RealStepResult(RealStepOutcome.OutcomeUnknown,
                "real.provider_return_unknown",
                JsonSerializer.Serialize(new { exception = exception.GetType().Name }));
        }

        if (!provider.ProviderReturned)
        {
            if (provider.Code is "adapter.preflight-rejected"
                or "adapter.closed-command-or-target-required")
                return new RealStepResult(RealStepOutcome.FailedWithoutEffect,
                    provider.Code,
                    JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(
                        true, provider.Code, beforeClosure.PhysicalMemberFingerprint,
                        BoundedDiagnostic(provider.ProviderError))));
            return new RealStepResult(RealStepOutcome.OutcomeUnknown,
                provider.Code, JsonSerializer.Serialize(provider));
        }
        if (!StringComparer.Ordinal.Equals(provider.Code, "provider.returned"))
            return new RealStepResult(RealStepOutcome.OutcomeUnknown,
                "real.provider_reported_uncertain_result", JsonSerializer.Serialize(provider));

        var deadline = timeProvider.GetUtcNow().Add(PostCallWindow);
        do
        {
            try
            {
                var after = await CaptureOperationTopologyAsync(plan, step.Id, cancellationToken)
                    .ConfigureAwait(false);
                var afterClosure = after.RequireSinglePhysicalClosure([physical]);
                if (!StringComparer.Ordinal.Equals(afterClosure.PhysicalMemberFingerprint,
                        plan.RealOperation!.PhysicalMemberFingerprint))
                    return new RealStepResult(RealStepOutcome.OutcomeUnknown,
                        "real.physical_member_changed", JsonSerializer.Serialize(provider));
                var verified = stored.Command is CreateTieredVirtualDiskCommand
                    && !TieredCreationCapabilityMatches(stored, target, provider, afterClosure.PhysicalDiskId)
                    ? null : VerifyAfter(stored.Command, target, provider, before, after);
                if (verified is not null)
                {
                    var observedSafety = stored.Command is SetDiskOnlineCommand diskState
                        ? await planner.ValidateObservedDiskStateWithEvidenceAsync(
                            after, afterClosure, diskState, cancellationToken).ConfigureAwait(false)
                        : stored.Command is CreateTieredVirtualDiskCommand
                            ? await planner.ValidatePartitionSafetyWithEvidenceAsync(
                                after, afterClosure, stored.Command, cancellationToken).ConfigureAwait(false)
                            : null;
                    if (stored.Command is SetDiskOnlineCommand)
                        RequireOfflinePartitionProof(after, afterClosure, observedSafety);
                    if (stored.Command is CreateTieredVirtualDiskCommand)
                        RequirePoolMemberRoleProof(after, afterClosure, observedSafety);
                    var evidence = new WindowsVerifiedStepEvidence(
                        afterClosure.Fingerprint,
                        afterClosure.PhysicalMemberFingerprint,
                        verified.CreatedObjectId,
                        provider.Code,
                        verified.CreatedOsDiskId,
                        verified.TieredCreation,
                        provider.LiveCapabilityEvidence,
                        verified.GptInitialization,
                        observedSafety?.NativePartitionAttributes ?? provider.NativeMsrSafetyEvidence,
                        observedSafety?.VolumeSafetyEvidence ?? provider.VolumeSafetyEvidence,
                        observedSafety?.OfflinePartitionAttributes,
                        afterClosure.PoolMemberRoleEvidence);
                    return new RealStepResult(RealStepOutcome.Verified,
                        "real.step_verified", JsonSerializer.Serialize(evidence),
                        evidence.CreatedObjectId);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException
                or IOException or UnauthorizedAccessException or InvalidOperationException
                or JsonException or NotSupportedException or ArgumentException
                or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                // A short-lived provider lag is possible. Failure to observe a
                // complete post-state by the deadline becomes OutcomeUnknown.
            }
            if (timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(PostCallPoll, cancellationToken).ConfigureAwait(false);
        } while (true);

        return new RealStepResult(RealStepOutcome.OutcomeUnknown,
            "real.postcondition_unverified", JsonSerializer.Serialize(provider));
    }

    private static StorageObjectId RequireCreatedEfiFormatTarget(
        OperationPlan plan, RealOperationStep step, WindowsStorageCommandTarget target,
        WindowsRealStorageTopology topology)
    {
        if (step.Command is not FormatVolumeCommand
                { Partition.Existing: null, Partition.CreatedByStep: { } createdBy,
                    FileSystem: RealFileSystem.Fat32, ClusterBytes: 4096, Full: false }
            || !target.CreatedInThisPlan || target.Kind != StorageObjectKind.Partition
            || string.IsNullOrWhiteSpace(target.ObjectId)
            || !Guid.TryParse(target.PartitionGuid, out var guid) || guid == Guid.Empty)
            throw new InvalidDataException("EFI format safety requires this plan's exact newly created partition.");
        var creator = plan.RealOperation!.Steps.TakeWhile(item => item.Id != step.Id)
            .SingleOrDefault(item => item.Id == createdBy)?.Command as CreatePartitionCommand;
        if (creator is not { Role: RealPartitionRole.Efi, Disk.Existing: { } parentDisk }
            || creator.Disk.CreatedByStep is not null)
            throw new InvalidDataException("EFI format safety lacks a prior frozen EFI creation on the exact existing disk.");
        var current = FindExact(topology, StorageObjectKind.Partition,
            target.UniqueId, target.ObjectId, target.PartitionGuid)
            ?? throw new InvalidDataException("The newly created EFI provider identity changed before format.");
        var id = new StorageObjectId(topology.SystemId, StorageObjectKind.Partition, current.Id);
        var partition = topology.Snapshot.Partitions.Single(item => item.StableId == current.Id);
        var currentTarget = WindowsRealStorageTargetBuilder.Build(topology,
            RealTargetReference.ForExisting(id), new Dictionary<string, string>()) with { CreatedInThisPlan = true };
        if (currentTarget != target || parentDisk.System != topology.SystemId
            || parentDisk.Kind != StorageObjectKind.OsDisk
            || partition.OsDiskStableId != parentDisk.ProviderKey
            || partition.Offset != creator.OffsetBytes || partition.Size != creator.SizeBytes
            || !GuidEquals(partition.PartitionTypeId, WindowsNativeMsrAttributesReader.EfiRole.ToString()))
            throw new InvalidDataException("The newly created EFI identity, parent, role, or geometry changed before format.");
        return id;
    }

    public async Task<RealReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        CancellationToken cancellationToken)
    {
        // This routine only reads. In particular, an uncertain CallIssued step
        // is never replayed to make the state appear complete.
        var frozen = plan.RealOperation;
        if (!RealOperationValidator.IsValid(plan)
            || frozen is null
            || persistedSteps.Count != frozen.Steps.Count
            || !persistedSteps.Select(item => item.StepId)
                .SequenceEqual(frozen.Steps.Select(item => item.Id), StringComparer.Ordinal))
            return Unknown(persistedSteps, "real.reconciliation_invalid_record");

        WindowsRealStorageTopology topology;
        RealTargetClosure closure;
        try
        {
            if (!operationScopes.ContainsKey(plan.OperationId))
            {
                var evidence = persistedSteps.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.TargetEvidence))?.TargetEvidence;
                var exact = evidence is null ? null : JsonSerializer.Deserialize<WindowsStorageCommandTarget>(evidence);
                if (exact is not null && !string.IsNullOrWhiteSpace(exact.PhysicalMemberUniqueId))
                {
                    var anchor = plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk);
                    operationScopes[plan.OperationId] = new(plan.SystemId, plan.OperationId, "reconcile", [anchor],
                        plan.Targets.Select(item => item.ProviderKey).ToArray(),
                        [new(anchor, "MSFT_PhysicalDisk", "UniqueId", exact.PhysicalMemberUniqueId)]);
                }
            }
            topology = await CaptureOperationTopologyAsync(plan, "reconcile", cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(topology.MachineBinding, frozen.MachineBinding))
                return Unknown(persistedSteps, "real.reconciliation_machine_changed");
            var physical = plan.Targets.Single(item =>
                item.Kind == StorageObjectKind.PhysicalDisk);
            closure = topology.RequireSinglePhysicalClosure([physical]);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return Unknown(persistedSteps, "real.reconciliation_capture_failed");
        }

        if (!StringComparer.Ordinal.Equals(closure.PhysicalMemberFingerprint,
                frozen.PhysicalMemberFingerprint))
            return Unknown(persistedSteps, "real.reconciliation_physical_changed");

        if (plan.Intent == OperationIntent.CreateStoragePool
            && frozen.Steps.Count == 1
            && frozen.Steps[0].Command is CreatePoolCommand
            && persistedSteps[0].State == RealOperationStepState.OutcomeUnknown)
            return await ReconcileSinglePoolCreationAsync(plan, persistedSteps,
                topology, closure, cancellationToken).ConfigureAwait(false);

        if (frozen.Steps.Count == 1
            && frozen.Steps[0].Command is CreateTieredVirtualDiskCommand
            && persistedSteps[0].State == RealOperationStepState.OutcomeUnknown)
            return await ReconcileSingleTieredCreationAsync(plan, persistedSteps,
                topology, closure, cancellationToken).ConfigureAwait(false);

        if (frozen.Steps.Count == 1
            && frozen.Steps[0].Command is RenameVolumeCommand
            && persistedSteps[0].State == RealOperationStepState.OutcomeUnknown)
            return await ReconcileSingleRenameAsync(plan, persistedSteps,
                topology, closure, cancellationToken).ConfigureAwait(false);

        if (plan.Intent == OperationIntent.SetDiskOnlineState
            && frozen.Steps.Count == 1
            && frozen.Steps[0].Command is SetDiskOnlineCommand
            && persistedSteps[0].State == RealOperationStepState.OutcomeUnknown)
            return await ReconcileSingleDiskOnlineStateAsync(plan, persistedSteps,
                topology, closure, cancellationToken).ConfigureAwait(false);

        if (plan.Intent == OperationIntent.InitializeDisk
            && frozen.Steps.Count == 2
            && frozen.Steps[0].Command is InitializeGptCommand
            && frozen.Steps[1].Command is CreatePartitionCommand
                { Role: RealPartitionRole.Msr, OffsetBytes: 1048576, SizeBytes: 16777216 }
            && persistedSteps[0].State == RealOperationStepState.OutcomeUnknown
            && persistedSteps[1].State == RealOperationStepState.StoppedBeforeCall)
            return await ReconcileInitializedGptPrefixAsync(plan, persistedSteps,
                topology, closure, cancellationToken).ConfigureAwait(false);

        var verifiedCount = 0;
        WindowsVerifiedStepEvidence? lastVerified = null;
        var reachedNonVerified = false;
        var reconciledSteps = new List<RealOperationStepProgress>(persistedSteps.Count);
        foreach (var step in persistedSteps)
        {
            if (step.State == RealOperationStepState.Verifying
                && IsProvenNoEffect(step, frozen.PhysicalMemberFingerprint))
            {
                reachedNonVerified = true;
                reconciledSteps.Add(step with
                {
                    State = RealOperationStepState.Failed,
                    Code = "real.reconciliation_proven_no_call"
                });
                continue;
            }
            if (step.State is RealOperationStepState.Verified
                or RealOperationStepState.Verifying)
            {
                if (reachedNonVerified || string.IsNullOrWhiteSpace(step.ResultEvidence))
                    return Unknown(persistedSteps, "real.reconciliation_step_order_unknown");
                try
                {
                    var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(
                        step.ResultEvidence);
                    if (evidence is null
                        || string.IsNullOrWhiteSpace(evidence.PostFingerprint)
                        || !StringComparer.Ordinal.Equals(
                            evidence.ProviderCode, "provider.returned")
                        || !StringComparer.Ordinal.Equals(
                            evidence.PhysicalMemberFingerprint,
                            frozen.PhysicalMemberFingerprint))
                        return Unknown(persistedSteps, "real.reconciliation_step_evidence_invalid");
                    lastVerified = evidence;
                }
                catch (JsonException)
                {
                    return Unknown(persistedSteps, "real.reconciliation_step_evidence_invalid");
                }
                verifiedCount++;
                if (step.State == RealOperationStepState.Verifying)
                    reachedNonVerified = true;
                reconciledSteps.Add(step.State == RealOperationStepState.Verifying
                    ? step with
                    {
                        State = RealOperationStepState.Verified,
                        Code = "real.reconciliation_verified_persisted_result"
                    }
                    : step);
                continue;
            }

            reachedNonVerified = true;
            if (step.State is RealOperationStepState.Failed)
            {
                if (!IsProvenNoEffect(step, frozen.PhysicalMemberFingerprint))
                    return Unknown(persistedSteps, "real.reconciliation_failed_step_uncertain");
                reconciledSteps.Add(step);
            }
            else if (step.State is RealOperationStepState.StoppedBeforeCall)
            {
                reconciledSteps.Add(step);
            }
            else if (step.State is RealOperationStepState.Pending)
            {
                // NotStarted is durably before both PreparingCall and CallIssued.
                // Recovery may stop that step once the whole observed prefix is
                // shown unchanged. PreparingCall itself stays uncertain by Plan 7.2.
                reconciledSteps.Add(step with
                {
                    State = RealOperationStepState.StoppedBeforeCall,
                    Code = "real.reconciliation_never_started"
                });
            }
            else
            {
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            }
        }

        var expectedFingerprint = lastVerified?.PostFingerprint
            ?? frozen.TargetFingerprint;
        if (!StringComparer.Ordinal.Equals(closure.Fingerprint, expectedFingerprint))
            return Unknown(persistedSteps, "real.reconciliation_topology_changed");

        if (verifiedCount == 0)
        {
            // A requested stop before the first call is cancellation. A
            // preflight or no-call failure remains a failure. Both are proven
            // to have had no Windows storage side effect.
            var stoppedBeforeCall = reconciledSteps.All(item =>
                item.State == RealOperationStepState.StoppedBeforeCall);
            return new RealReconciliationResult(
                stoppedBeforeCall ? RealOperationState.Cancelled : RealOperationState.Failed,
                reconciledSteps,
                stoppedBeforeCall ? "real.reconciliation_cancelled_before_call"
                    : "real.reconciliation_no_effect", true);
        }

        if (verifiedCount == persistedSteps.Count)
        {
            return new RealReconciliationResult(
                RealOperationState.Succeeded, reconciledSteps,
                "real.reconciliation_verified", true);
        }
        return new RealReconciliationResult(
            RealOperationState.PartiallyCompleted,
            reconciledSteps,
            "real.reconciliation_partial_verified",
            true);
    }

    private async Task<RealReconciliationResult> ReconcileSingleTieredCreationAsync(
        OperationPlan plan, IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology, RealTargetClosure closure, CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var step = frozen.Steps[0];
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        var progress = persistedSteps[0];
        try
        {
            if (plan.PlanHash != OperationPlanHasher.Compute(plan)
                || command.Pool.Existing is not { Kind: StorageObjectKind.StoragePool } poolId
                || command.Tier.Existing is not { Kind: StorageObjectKind.StorageTier } tierId
                || command.Pool.CreatedByStep is not null || command.Tier.CreatedByStep is not null
                || poolId.System != topology.SystemId || tierId.System != topology.SystemId
                || string.IsNullOrWhiteSpace(progress.TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.ResultEvidence))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence);
            var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence);
            if (target is not null && provider is { ProviderReturned: true, Code: "provider.error-outcome-unknown" })
                return await ReconcileFailedTieredCreationAsync(plan, persistedSteps, topology, closure,
                    target, provider, cancellationToken).ConfigureAwait(false);
            if (target is not { Kind: StorageObjectKind.StoragePool, CreatedInThisPlan: false }
                || provider is not { ProviderReturned: true, Code: "provider.returned" }
                || provider.ProviderError is not null || provider.ProviderJobId is not null
                || provider.DiskNumber is not null || provider.PartitionNumber is not null
                || !string.IsNullOrEmpty(provider.PartitionGuid)
                || string.IsNullOrWhiteSpace(provider.UniqueId) || string.IsNullOrWhiteSpace(provider.ObjectId)
                || target.ExpectedFingerprint != frozen.TargetFingerprint)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var currentTarget = WindowsRealStorageTargetBuilder.Build(topology, command.Pool,
                new Dictionary<string, string>());
            var currentTier = topology.RequireObject(tierId);
            var pool = topology.Snapshot.StoragePools.Single(item => item.StableId == poolId.ProviderKey);
            var subsystem = topology.Facts.Objects.SingleOrDefault(item => item.Id == pool.SubsystemStableId
                && item.ObjectType == FactObjectType.StorageSubsystem && item.HasReliableIdentity);
            if (subsystem is null || topology.Facts.Sources.Single(item => item.Id == subsystem.SourceRef) is not
                { ClassName: "MSFT_StorageSubSystem", ReadState: FieldReadState.Returned })
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            currentTarget = currentTarget with { ExpectedFingerprint = target.ExpectedFingerprint,
                RelatedUniqueId = Text(currentTier, "UniqueId"), RelatedObjectId = Text(currentTier, "ObjectId") };
            if (target != currentTarget || !TieredCreationCapabilityMatches(step, target, provider, closure.PhysicalDiskId))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            // The exact pool-level template persists after cloning. It supplies
            // current identity/layout evidence, not a synthetic pre-creation inventory.
            var verified = VerifyAfter(command, target, provider, topology, topology);
            if (verified?.TieredCreation is null
                || topology.Snapshot.VirtualDisks.Count(item => item.PoolStableId == poolId.ProviderKey) != 1
                || topology.Snapshot.StorageTiers.Count(item => item.PoolStableId == poolId.ProviderKey) != 2
                || closure.Objects.Count(item => item.ObjectType == FactObjectType.Disk) != 1
                || closure.Objects.Any(item => item.ObjectType is FactObjectType.Partition or FactObjectType.Volume))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(
                topology, closure, command, cancellationToken).ConfigureAwait(false);
            RequirePoolMemberRoleProof(topology, closure, safety);
            var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint, closure.PhysicalMemberFingerprint,
                verified.CreatedObjectId, provider.Code, verified.CreatedOsDiskId, verified.TieredCreation,
                provider.LiveCapabilityEvidence, NativeMsrSafetyEvidence: safety?.NativePartitionAttributes,
                VolumeSafetyEvidence: safety?.VolumeSafetyEvidence,
                OfflinePartitionAttributes: safety?.OfflinePartitionAttributes,
                PoolMemberRoleEvidence: closure.PoolMemberRoleEvidence);
            return new RealReconciliationResult(RealOperationState.Succeeded,
                [progress with { State = RealOperationStepState.Verified,
                    Code = "real.reconciliation_verified_observed_tiered_creation",
                    ResultEvidence = JsonSerializer.Serialize(evidence) }], "real.reconciliation_verified", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        }
    }

    private async Task<RealReconciliationResult> ReconcileFailedTieredCreationAsync(
        OperationPlan plan, IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        WindowsStorageCommandTarget target, WindowsStorageCommandResult provider,
        CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var step = frozen.Steps[0];
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        // A transport failure, timeout, missing error, returned identity or job
        // cannot prove absence. Keep the original Windows-call evidence intact.
        if (target is not { Kind: StorageObjectKind.StoragePool, CreatedInThisPlan: false }
            || string.IsNullOrWhiteSpace(provider.ProviderError) || provider.ProviderJobId is not null
            || !string.IsNullOrEmpty(provider.UniqueId) || !string.IsNullOrEmpty(provider.ObjectId)
            || !string.IsNullOrEmpty(provider.PartitionGuid) || provider.DiskNumber is not null
            || provider.PartitionNumber is not null || target.ExpectedFingerprint != frozen.TargetFingerprint)
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");

        bool Unchanged(WindowsRealStorageTopology current, RealTargetClosure currentClosure)
        {
            if (current.MachineBinding != frozen.MachineBinding
                || currentClosure.PhysicalMemberFingerprint != frozen.PhysicalMemberFingerprint
                || currentClosure.Fingerprint != frozen.TargetFingerprint
                || currentClosure.Objects.Any(item => item.ObjectType is FactObjectType.VirtualDisk
                    or FactObjectType.Disk or FactObjectType.Partition or FactObjectType.Volume)
                || currentClosure.Objects.Count(item => item.ObjectType == FactObjectType.StorageTier) != 1)
                return false;
            var currentTier = current.RequireObject(command.Tier.Existing!.Value);
            var currentTarget = WindowsRealStorageTargetBuilder.Build(current, command.Pool, new Dictionary<string, string>())
                with { ExpectedFingerprint = target.ExpectedFingerprint,
                    RelatedUniqueId = Text(currentTier, "UniqueId"), RelatedObjectId = Text(currentTier, "ObjectId") };
            return currentTarget == target;
        }
        var expectedInput = new WindowsTieredCreationInput(target.RelatedUniqueId, target.RelatedObjectId,
            target.UniqueId, target.PhysicalMemberUniqueId, "HDD", "Simple", "Fixed", 1, 65536, command.SizeBytes);
        if (!Unchanged(topology, closure)
            || !TieredCreationCapabilityMatches(step, target, provider, closure.PhysicalDiskId, expectedInput))
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        var jobsBefore = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        bool EmptyFresh(WindowsStorageJobAbsenceEvidence value) => value.Jobs is not null
            && value.Jobs.All(job => !string.IsNullOrWhiteSpace(job.UniqueId) && !string.IsNullOrWhiteSpace(job.ObjectId)
                && job.JobState is 7 or 8 or 9 or 10)
            && value.Jobs.Select(job => job.UniqueId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count
            && value.Jobs.Select(job => job.ObjectId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count
            && value.ObservedAtUtc != default && value.ObservedAtUtc <= timeProvider.GetUtcNow().AddSeconds(10)
            && timeProvider.GetUtcNow() - value.ObservedAtUtc <= TimeSpan.FromMinutes(2);
        if (!EmptyFresh(jobsBefore)) return Unknown(persistedSteps, "real.reconciliation_storage_job_uncertain");
        await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken).ConfigureAwait(false);
        var second = await CaptureOperationTopologyAsync(plan, "reconcile-no-effect", cancellationToken).ConfigureAwait(false);
        var secondClosure = second.RequireSinglePhysicalClosure([plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk)]);
        var jobsAfter = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!Unchanged(second, secondClosure) || !EmptyFresh(jobsAfter))
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(second, secondClosure, command, cancellationToken).ConfigureAwait(false);
        RequirePoolMemberRoleProof(second, secondClosure, safety);
        var evidence = new WindowsObservedNoEffectStepEvidence(true,
            "real.reconciliation_observed_tiered_creation_no_effect", frozen.PhysicalMemberFingerprint,
            frozen.TargetFingerprint, closure.Fingerprint, secondClosure.Fingerprint, provider, [jobsBefore, jobsAfter]);
        return new RealReconciliationResult(RealOperationState.Failed,
            [persistedSteps[0] with { State = RealOperationStepState.Failed, Code = evidence.Code,
                ResultEvidence = JsonSerializer.Serialize(evidence) }], evidence.Code, true);
    }

    private static bool TieredCreationCapabilityMatches(RealOperationStep step,
        WindowsStorageCommandTarget target, WindowsStorageCommandResult provider, string physicalId,
        WindowsTieredCreationInput? expectedInput = null)
    {
        const string prefix = "live-tier-capability:";
        const string separator = "; exact-template-new-size:";
        const string poolSeparator = "; exact-pool-new-size:";
        var offset = step.SupportEvidence.IndexOf(separator, StringComparison.Ordinal);
        if (!step.SupportEvidence.StartsWith(prefix, StringComparison.Ordinal) || offset <= prefix.Length
            || provider.LiveCapabilityEvidence is not { ValueKind: JsonValueKind.Object } live)
            return false;
        var frozenCapability = JsonSerializer.Deserialize<WindowsTierCapability>(
            step.SupportEvidence[prefix.Length..offset]);
        var poolOffset = step.SupportEvidence.IndexOf(poolSeparator, offset + separator.Length, StringComparison.Ordinal);
        var tierRangeJson = poolOffset < 0 ? step.SupportEvidence[(offset + separator.Length)..]
            : step.SupportEvidence[(offset + separator.Length)..poolOffset];
        var frozenRange = JsonSerializer.Deserialize<VirtualDiskCreationSize>(tierRangeJson);
        var frozenPoolRange = poolOffset < 0 ? null : JsonSerializer.Deserialize<VirtualDiskCreationSize>(
            step.SupportEvidence[(poolOffset + poolSeparator.Length)..]);
        if (frozenCapability is not { Status: "queried", Error: null }
            || frozenCapability.PhysicalDiskStableId != physicalId
            || frozenCapability.UniqueId != target.StorageSubsystemUniqueId
            || frozenCapability.ObjectId != target.StorageSubsystemObjectId
            || frozenCapability.Fields is null || frozenCapability.Associations is null
            || frozenRange?.EnumeratedSizes is null || (expectedInput ?? provider.TieredCreationInput) is not { } input
            || (expectedInput is not null && provider.TieredCreationInput is not null && provider.TieredCreationInput != expectedInput)
            || !frozenRange.Supports(input.SizeBytes)) return false;
        var support = frozenCapability.Fields.SingleOrDefault(item => item.Name == "SupportsStorageTieredVirtualDiskCreation");
        var minimum = frozenCapability.Fields.SingleOrDefault(item => item.Name == "PhysicalDisksPerStoragePoolMin");
        if (support is not { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.True } }
            || minimum is not { ReadState: FieldReadState.Returned, Value: { } count } || !count.TryGetInt64(out var min) || min != 1)
            return false;
        bool TextMatches(string name, string expected) => live.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        if (!TextMatches("SubsystemUniqueId", target.StorageSubsystemUniqueId)
            || !TextMatches("SubsystemObjectId", target.StorageSubsystemObjectId)
            || !TextMatches("PhysicalMemberUniqueId", target.PhysicalMemberUniqueId)
            || !TextMatches("PhysicalMemberObjectId", target.PhysicalMemberObjectId)
            || !TextMatches("RequiredField", "SupportsStorageTieredVirtualDiskCreation")
            || !live.TryGetProperty("RequiredValue", out var required) || required.ValueKind != JsonValueKind.True
            || !live.TryGetProperty("PhysicalDisksPerStoragePoolMin", out var liveMin) || !liveMin.TryGetInt64(out var liveCount) || liveCount != 1
            || !live.TryGetProperty("CreationSize", out var size) || size.ValueKind != JsonValueKind.Object)
            return false;
        if (!size.TryGetProperty("ReturnValue", out var returned) || !returned.TryGetUInt32(out var code) || code != 0
            || !size.TryGetProperty("SupportedSizes", out var sizes) || sizes.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null)) return false;
        object? UInt64Value(string name) => size.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null
            ? null : size.TryGetProperty(name, out value) && value.TryGetUInt64(out var number)
                ? number : throw new InvalidDataException("Missing or malformed tier creation range output.");
        var values = sizes.ValueKind == JsonValueKind.Null ? null : sizes.EnumerateArray().Select(value =>
            value.TryGetUInt64(out var number) ? number : throw new InvalidDataException("Malformed tier size enumeration.")).ToArray();
        if (!WindowsRealStorageCapabilityReader.ParseTierCreationSize(code, values,
            UInt64Value("TierSizeMin"), UInt64Value("TierSizeMax"), UInt64Value("TierSizeDivisor")).Supports(input.SizeBytes))
            return false;
        // Historical frozen records retain their original template-only proof
        // for read-only recovery. New plans require both frozen/live constraints.
        if (poolOffset < 0) return true;
        if (frozenPoolRange?.EnumeratedSizes is null || !frozenPoolRange.Supports(input.SizeBytes)
            || !live.TryGetProperty("PoolCreationSize", out var poolSize) || poolSize.ValueKind != JsonValueKind.Object
            || !poolSize.TryGetProperty("ReturnValue", out var poolReturned) || !poolReturned.TryGetUInt32(out var poolCode) || poolCode != 0
            || !poolSize.TryGetProperty("SupportedSizes", out var poolSizes) || poolSizes.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
            return false;
        long PoolNumber(string name) => poolSize.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null
            ? 0 : poolSize.TryGetProperty(name, out value) && value.TryGetInt64(out var number) && number >= 0
                ? number : throw new InvalidDataException("Missing or malformed pool creation range output.");
        var poolValues = poolSizes.ValueKind == JsonValueKind.Null ? [] : poolSizes.EnumerateArray().Select(value =>
            value.TryGetInt64(out var number) && number > 0 ? number
                : throw new InvalidDataException("Malformed pool size enumeration.")).ToArray();
        var livePoolRange = new VirtualDiskCreationSize(PoolNumber("VirtualDiskSizeMin"), PoolNumber("VirtualDiskSizeMax"),
            PoolNumber("VirtualDiskSizeDivisor"), poolValues);
        if (poolValues.Length > 0 && (livePoolRange.DivisorBytes <= 0
            || poolValues.Distinct().Count() != poolValues.Length
            || poolValues.Any(value => value % livePoolRange.DivisorBytes != 0))) return false;
        return VirtualDiskCreationSize.Intersect(frozenRange, frozenPoolRange).Supports(input.SizeBytes)
            && livePoolRange.Supports(input.SizeBytes);
    }

    private async Task<RealReconciliationResult> ReconcileSinglePoolCreationAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var command = (CreatePoolCommand)frozen.Steps[0].Command;
        var progress = persistedSteps[0];
        try
        {
            // Only prove the successfully returned single creation. CanPool is
            // a pre-creation condition; never retry creation on its pooled member.
            if (!StringComparer.Ordinal.Equals(plan.PlanHash, OperationPlanHasher.Compute(plan))
                || command.PhysicalDisk.Existing is not { Kind: StorageObjectKind.PhysicalDisk } physical
                || command.PhysicalDisk.CreatedByStep is not null
                || physical.System != topology.SystemId || physical.ProviderKey != closure.PhysicalDiskId
                || string.IsNullOrWhiteSpace(progress.TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.ResultEvidence))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence);
            var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence);
            if (target is not { Kind: StorageObjectKind.PhysicalDisk, CreatedInThisPlan: false }
                || provider is not { ProviderReturned: true, Code: "provider.returned" }
                || provider.ProviderError is not null || provider.ProviderJobId is not null
                || provider.DiskNumber is not null || provider.PartitionNumber is not null
                || !string.IsNullOrEmpty(provider.PartitionGuid) || provider.TieredCreationInput is not null
                || string.IsNullOrWhiteSpace(provider.UniqueId) || string.IsNullOrWhiteSpace(provider.ObjectId)
                || !StringComparer.Ordinal.Equals(target.ExpectedFingerprint, frozen.TargetFingerprint))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var currentTarget = WindowsRealStorageTargetBuilder.Build(topology,
                command.PhysicalDisk, new Dictionary<string, string>());
            if (target != (currentTarget with { ExpectedFingerprint = target.ExpectedFingerprint }))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");

            var created = FindExact(topology, StorageObjectKind.StoragePool,
                provider.UniqueId, provider.ObjectId, string.Empty);
            if (created is null)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var snapshot = topology.Snapshot;
            var pool = snapshot.StoragePools.Single(item => item.StableId == created.Id);
            var member = snapshot.PhysicalDisks.Single(item => item.StableId == physical.ProviderKey);
            if (pool.SubsystemStableId is not { Length: > 0 } subsystemId)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var subsystem = topology.Facts.Objects.SingleOrDefault(item => item.Id == subsystemId
                && item.ObjectType == FactObjectType.StorageSubsystem && item.HasReliableIdentity);
            if (subsystem is null || topology.Facts.Sources.Single(item => item.Id == subsystem.SourceRef) is not
                { ClassName: "MSFT_StorageSubSystem", ReadState: FieldReadState.Returned })
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            if (pool.IsPrimordial || !StringComparer.Ordinal.Equals(pool.FriendlyName, command.Name)
                || !pool.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                || !pool.OperationalStatus.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || pool.MemberPhysicalDiskIds.Count != 1 || pool.MemberPhysicalDiskIds[0] != physical.ProviderKey
                || member.PoolStableId != pool.StableId || member.CanPool
                || Text(subsystem, "UniqueId") != target.StorageSubsystemUniqueId
                || Text(subsystem, "ObjectId") != target.StorageSubsystemObjectId
                || snapshot.StoragePools.Count(item => !item.IsPrimordial
                    && item.MemberPhysicalDiskIds.Contains(physical.ProviderKey)) != 1
                || snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
                || snapshot.StorageTiers.Any(item => item.PoolStableId == pool.StableId)
                || snapshot.OsDisks.Any(item => item.PhysicalDiskStableId == physical.ProviderKey)
                || closure.Objects.Any(item => item.ObjectType is FactObjectType.Disk
                    or FactObjectType.Partition or FactObjectType.Volume
                    or FactObjectType.VirtualDisk or FactObjectType.StorageTier))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");

            var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(
                topology, closure, command, cancellationToken).ConfigureAwait(false);
            RequirePoolMemberRoleProof(topology, closure, safety);
            var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint,
                closure.PhysicalMemberFingerprint, created.Id, provider.Code,
                LiveCapabilityEvidence: provider.LiveCapabilityEvidence,
                NativeMsrSafetyEvidence: safety?.NativePartitionAttributes,
                VolumeSafetyEvidence: safety?.VolumeSafetyEvidence,
                OfflinePartitionAttributes: safety?.OfflinePartitionAttributes,
                PoolMemberRoleEvidence: closure.PoolMemberRoleEvidence);
            var verified = progress with
            {
                State = RealOperationStepState.Verified,
                Code = "real.reconciliation_verified_observed_pool_creation",
                ResultEvidence = JsonSerializer.Serialize(evidence)
            };
            return new RealReconciliationResult(RealOperationState.Succeeded, [verified],
                "real.reconciliation_verified", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException
            or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        }
    }

    private async Task<RealReconciliationResult> ReconcileInitializedGptPrefixAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var command = (InitializeGptCommand)frozen.Steps[0].Command;
        var progress = persistedSteps[0];
        try
        {
            // The historical second step never reached its pre-call record.
            // Prove the returned initialization only; never resume MSR creation.
            if (!StringComparer.Ordinal.Equals(plan.PlanHash, OperationPlanHasher.Compute(plan))
                || command.Disk.Existing is not { } existing
                || command.Disk.CreatedByStep is not null
                || ((CreatePartitionCommand)frozen.Steps[1].Command).Disk != command.Disk
                || !string.IsNullOrWhiteSpace(persistedSteps[1].TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.ResultEvidence))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence);
            var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence);
            if (target is null || provider is not { ProviderReturned: true, Code: "provider.returned" }
                || provider.ProviderError is not null || provider.ProviderJobId is not null
                || target.Kind != StorageObjectKind.OsDisk
                || !StringComparer.Ordinal.Equals(target.ExpectedFingerprint, frozen.TargetFingerprint)
                || !StringComparer.Ordinal.Equals(provider.UniqueId, target.UniqueId)
                || !StringComparer.Ordinal.Equals(provider.ObjectId, target.ObjectId)
                || provider.DiskNumber != target.DiskNumber)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var currentTarget = WindowsRealStorageTargetBuilder.Build(topology,
                command.Disk, new Dictionary<string, string>());
            // These auxiliary C04 fields were added after the recorded call.
            // Existing disk/provider identities and frozen physical fingerprint
            // remain mandatory; populated auxiliary identities must still match.
            if (string.IsNullOrEmpty(target.StorageSubsystemObjectId))
                currentTarget = currentTarget with { StorageSubsystemObjectId = string.Empty };
            if (string.IsNullOrEmpty(target.PhysicalMemberObjectId))
                currentTarget = currentTarget with { PhysicalMemberObjectId = string.Empty };
            if (topology.RequireObject(existing).Id != existing.ProviderKey
                || target != (currentTarget with { ExpectedFingerprint = target.ExpectedFingerprint }))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var observed = ObserveInitializedGpt(topology, target);
            if (observed?.GptInitialization?.ProviderMsrStableId is null)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            await planner.ValidateObservedInitializationAsync(topology, closure,
                command, cancellationToken).ConfigureAwait(false);
            var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint,
                closure.PhysicalMemberFingerprint, observed.CreatedObjectId,
                provider.Code, observed.CreatedOsDiskId,
                GptInitialization: observed.GptInitialization,
                PoolMemberRoleEvidence: closure.PoolMemberRoleEvidence);
            var reconciled = new[]
            {
                progress with
                {
                    State = RealOperationStepState.Verified,
                    Code = "real.reconciliation_verified_observed_gpt",
                    ResultEvidence = JsonSerializer.Serialize(evidence)
                },
                persistedSteps[1]
            };
            return new RealReconciliationResult(RealOperationState.PartiallyCompleted,
                reconciled, "real.reconciliation_initialized_gpt_prefix", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException)
        {
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        }
    }

    private async Task<RealReconciliationResult> ReconcileSingleDiskOnlineStateAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var command = (SetDiskOnlineCommand)frozen.Steps[0].Command;
        var progress = persistedSteps[0];
        try
        {
            // Prove one completed, successfully returned GPT disk state change.
            // No write adapter is called and no following step can be resumed.
            if (!StringComparer.Ordinal.Equals(plan.PlanHash, OperationPlanHasher.Compute(plan))
                || command.Disk.Existing is not { Kind: StorageObjectKind.OsDisk } existing
                || command.Disk.CreatedByStep is not null
                || string.IsNullOrWhiteSpace(progress.TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.ResultEvidence))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence);
            var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence);
            if (target is not { Kind: StorageObjectKind.OsDisk }
                || provider is not { ProviderReturned: true, Code: "provider.returned" }
                || provider.ProviderError is not null || provider.ProviderJobId is not null
                || provider.PartitionNumber is not null || provider.TieredCreationInput is not null
                || !StringComparer.Ordinal.Equals(target.ExpectedFingerprint, frozen.TargetFingerprint)
                || !StringComparer.Ordinal.Equals(provider.UniqueId, target.UniqueId)
                || !StringComparer.Ordinal.Equals(provider.ObjectId, target.ObjectId)
                || provider.DiskNumber != target.DiskNumber
                || !Guid.TryParse(provider.PartitionGuid, out var returnedDiskGuid)
                || returnedDiskGuid == Guid.Empty)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var currentObject = topology.RequireObject(existing);
            var currentTarget = WindowsRealStorageTargetBuilder.Build(topology,
                command.Disk, new Dictionary<string, string>());
            var disk = topology.Snapshot.OsDisks.Single(item => item.StableId == currentObject.Id);
            if (currentObject.Id != existing.ProviderKey
                || target != (currentTarget with { ExpectedFingerprint = target.ExpectedFingerprint })
                || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                || disk.IsOffline != !command.Online
                || !Guid.TryParse(Text(currentObject, "Guid"), out var observedDiskGuid)
                || observedDiskGuid != returnedDiskGuid)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            // Validate the observed state's safety directly. Preparation's
            // already-in-requested-state guard is deliberately not a recovery test.
            var safety = await planner.ValidateObservedDiskStateWithEvidenceAsync(
                topology, closure, command, cancellationToken).ConfigureAwait(false);
            RequireOfflinePartitionProof(topology, closure, safety);
            RequirePoolMemberRoleProof(topology, closure, safety);
            var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint,
                closure.PhysicalMemberFingerprint, null, provider.Code,
                NativeMsrSafetyEvidence: safety?.NativePartitionAttributes,
                VolumeSafetyEvidence: safety?.VolumeSafetyEvidence,
                OfflinePartitionAttributes: safety?.OfflinePartitionAttributes,
                PoolMemberRoleEvidence: closure.PoolMemberRoleEvidence);
            var verified = progress with
            {
                State = RealOperationStepState.Verified,
                Code = "real.reconciliation_verified_observed_disk_online_state",
                ResultEvidence = JsonSerializer.Serialize(evidence)
            };
            return new RealReconciliationResult(RealOperationState.Succeeded, [verified],
                "real.reconciliation_verified", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException
            or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        }
    }

    private static void RequirePoolMemberRoleProof(
        WindowsRealStorageTopology topology, RealTargetClosure closure,
        WindowsRealStorageSafetyEvidence? safety)
    {
        if (closure.PoolMemberRoleEvidence is not { } expected) return;
        var actual = safety?.PoolMemberRoleEvidence;
        if (actual is null || actual.InventoryVersion != topology.InventoryVersion
            || actual.InventoryVersion != expected.InventoryVersion
            || actual.PhysicalStableId != closure.PhysicalDiskId
            || actual.PhysicalStableId != expected.PhysicalStableId || actual.PoolStableId != expected.PoolStableId
            || actual.VerificationMethod != "CompleteCurrentPoolAndOsDiskAssociations"
            || actual.IsBoot || actual.IsSystem || actual.IsPageFile || actual.IsCrashDump
            || !actual.AssociatedOsDiskIds.SequenceEqual(expected.AssociatedOsDiskIds, StringComparer.Ordinal))
            throw new InvalidDataException("The pooled physical member lacks its exact current Windows role proof.");
    }

    private static void RequireOfflinePartitionProof(
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        WindowsRealStorageSafetyEvidence? safety)
    {
        foreach (var partitionId in closure.OfflinePartitionIdsNeedingNativeProof)
        {
            var proofs = safety?.OfflinePartitionAttributes?.Where(item =>
                StringComparer.Ordinal.Equals(item.PartitionStableId, partitionId)).ToArray();
            if (proofs is not { Length: 1 }
                || !proofs[0].DiskIsOffline
                || !StringComparer.Ordinal.Equals(proofs[0].InventoryVersion, topology.InventoryVersion))
                throw new InvalidDataException("An offline partition lacks its unique current native safety proof.");
        }
    }

    private async Task<RealReconciliationResult> ReconcileSingleRenameAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        WindowsRealStorageTopology topology,
        RealTargetClosure closure,
        CancellationToken cancellationToken)
    {
        var frozen = plan.RealOperation!;
        var step = frozen.Steps[0];
        var progress = persistedSteps[0];
        var command = (RenameVolumeCommand)step.Command;
        // This recovery proves the affected volume and its frozen parent chain.
        // It does not assert that every old field in the closure is unchanged,
        // and it never invokes the write adapter or resumes another step.
        try
        {
            if (!StringComparer.Ordinal.Equals(plan.PlanHash, OperationPlanHasher.Compute(plan))
                || command.Volume.Existing is not { } existing
                || command.Volume.CreatedByStep is not null
                || string.IsNullOrWhiteSpace(progress.TargetEvidence)
                || string.IsNullOrWhiteSpace(progress.ResultEvidence))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence);
            var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence);
            if (target is null || provider is not { ProviderReturned: true, Code: "provider.returned" }
                || !StringComparer.Ordinal.Equals(target.ExpectedFingerprint, frozen.TargetFingerprint)
                || !StringComparer.Ordinal.Equals(provider.UniqueId, target.UniqueId)
                || !StringComparer.Ordinal.Equals(provider.ObjectId, target.ObjectId))
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");

            var currentObject = topology.RequireObject(existing);
            var currentTarget = WindowsRealStorageTargetBuilder.Build(topology,
                command.Volume, new Dictionary<string, string>());
            if (currentObject.Id != existing.ProviderKey
                || target != (currentTarget with { ExpectedFingerprint = target.ExpectedFingerprint })
                || VerifyAfter(command, target, provider, topology, topology) is null)
                return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");

            var proposal = new RealOperationIntentRequest(plan.Intent, plan.SystemId,
                plan.Targets, frozen.Steps, frozen.ExpectedFinalState);
            await planner.ValidateCurrentStepAsync(topology, closure, proposal, step,
                cancellationToken, readOnlyRenameReconciliation: true).ConfigureAwait(false);

            var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint,
                closure.PhysicalMemberFingerprint, null, provider.Code,
                PoolMemberRoleEvidence: closure.PoolMemberRoleEvidence);
            var verified = progress with
            {
                State = RealOperationStepState.Verified,
                Code = "real.reconciliation_verified_observed_rename",
                ResultEvidence = JsonSerializer.Serialize(evidence)
            };
            return new RealReconciliationResult(RealOperationState.Succeeded, [verified],
                "real.reconciliation_verified", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException)
        {
            return Unknown(persistedSteps, "real.reconciliation_step_outcome_unknown");
        }
    }

    private static string? BoundedDiagnostic(string? value) =>
        value is { Length: > 2048 } ? value[..2048] : value;

    private static bool IsProvenNoEffect(
        RealOperationStepProgress step,
        string physicalFingerprint)
    {
        if (step.ResultEvidence == "preflight_failed") return true;
        if (string.IsNullOrWhiteSpace(step.ResultEvidence)) return false;
        try
        {
            var evidence = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(
                step.ResultEvidence);
            return evidence is { NoWindowsCall: true }
                && StringComparer.Ordinal.Equals(
                    evidence.PhysicalMemberFingerprint, physicalFingerprint);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static RealReconciliationResult Unknown(
        IReadOnlyList<RealOperationStepProgress> steps,
        string code) => new(
            RealOperationState.OutcomeUnknown, steps, code, false);

    private static RealOperationStep RequireFrozenStep(
        OperationPlan plan, RealOperationStep step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        if (!RealOperationValidator.IsValid(plan)
            || !StringComparer.Ordinal.Equals(plan.PlanHash, OperationPlanHasher.Compute(plan)))
            throw new InvalidDataException("The persisted real plan is invalid or changed.");
        var matches = plan.RealOperation!.Steps.Where(item => item.Id == step.Id).ToArray();
        if (matches.Length != 1
            || !StringComparer.Ordinal.Equals(
                JsonSerializer.Serialize(matches[0]), JsonSerializer.Serialize(step)))
            throw new InvalidDataException("The requested real step differs from the frozen plan.");
        return matches[0];
    }

    private Task<WindowsRealStorageTopology> CaptureOperationTopologyAsync(
        OperationPlan plan, string stepId, CancellationToken cancellationToken)
    {
        if (topologyReader.SupportsScopedCapture && operationScopes.TryGetValue(plan.OperationId, out var scope))
        {
            var currentScope = scope with { StepId = stepId };
            return StorageOperationTiming.MeasureAsync("real.capture.scoped",
                () => topologyReader.CaptureScopedAsync(currentScope, cancellationToken),
                plan.OperationId, stepId, currentScope.Key, 1);
        }
        return StorageOperationTiming.MeasureAsync("real.capture.full",
            () => topologyReader.CaptureAsync(cancellationToken), plan.OperationId, stepId,
            topologyReader.SupportsScopedCapture ? "initial-safe-locator" : "source-scoped-unavailable", 1);
    }

    private static readonly string[] TieredLayoutFieldNames =
        ["ResiliencySettingName", "ProvisioningType", "NumberOfColumns", "Interleave",
            "NumberOfDataCopies", "PhysicalDiskRedundancy", "AllocatedSize", "Size", "FootprintOnPool", "MediaType"];

    private static bool ReturnedFalse(WinPoolSourceObject source, string name) => source.Field(name) is
        { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.False } };

    private static bool ReturnedZero(WinPoolSourceObject source, string name) => source.Field(name) is
        { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.Number } value }
        && value.TryGetUInt64(out var number) && number == 0;

    private static bool TieredLayoutMatches(WinPoolSourceObject virtualDisk, WinPoolSourceObject tier,
        VirtualDiskInfo vd, StorageTierInfo instance, long bytes)
    {
        // The installed Microsoft StorageWMI MOF defines layout on MSFT_StorageTier.
        // Tiered Spaces on this provider return the aggregate VD layout as null.
        // Preserve those observations; prove the layout on its exact allocated
        // instance's Fixed property and allocation, matching the frozen adapter input.
        bool Matches(WinPoolSourceObject source, string name, object expected, bool allowNull = false)
        {
            var field = source.Field(name);
            if (field is not { ReadState: FieldReadState.Returned, Value: { } value }) return false;
            if (value.ValueKind == JsonValueKind.Null) return allowNull;
            if (expected is string text)
                return value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), text, StringComparison.OrdinalIgnoreCase);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                && number == Convert.ToInt64(expected, System.Globalization.CultureInfo.InvariantCulture);
        }
        return Matches(virtualDisk, "ResiliencySettingName", "Simple", true)
            && Matches(virtualDisk, "ProvisioningType", 2, true)
            && Matches(virtualDisk, "NumberOfColumns", 1, true)
            && Matches(virtualDisk, "Interleave", 65536, true)
            && Matches(virtualDisk, "NumberOfDataCopies", 1, true)
            && Matches(virtualDisk, "PhysicalDiskRedundancy", 0, true)
            && Matches(virtualDisk, "AllocatedSize", bytes)
            && vd.FootprintOnPool == bytes
            && Matches(tier, "ResiliencySettingName", "Simple")
            && Matches(tier, "ProvisioningType", 2)
            && Matches(tier, "NumberOfColumns", 1)
            && Matches(tier, "Interleave", 65536)
            && Matches(tier, "NumberOfDataCopies", 1)
            && Matches(tier, "PhysicalDiskRedundancy", 0)
            && Matches(tier, "AllocatedSize", bytes)
            && instance.FootprintOnPool == bytes;
    }

    internal sealed record VerifiedPostcondition(
        string? CreatedObjectId = null,
        string? CreatedOsDiskId = null,
        WindowsTieredCreationMapping? TieredCreation = null,
        WindowsGptInitializationEvidence? GptInitialization = null);

    internal static VerifiedPostcondition? VerifyAfter(
        RealStorageCommand command,
        WindowsStorageCommandTarget target,
        WindowsStorageCommandResult result,
        WindowsRealStorageTopology before,
        WindowsRealStorageTopology after)
    {
        var snapshot = after.Snapshot;
        var exact = FindExact(after, target.Kind, target.UniqueId, target.ObjectId,
            target.PartitionGuid);
        switch (command)
        {
            case SetDiskOnlineCommand value:
                return exact is not null && snapshot.OsDisks
                    .Single(item => item.StableId == exact.Id).IsOffline == !value.Online
                    && SiblingPartitionsUnchanged(before, after, target.DiskNumber, null)
                    ? new() : null;
            case InitializeGptCommand:
            {
                var original = FindExact(before, StorageObjectKind.OsDisk,
                    target.UniqueId, target.ObjectId, string.Empty);
                if (original is null
                    || !before.Snapshot.OsDisks.Single(item => item.StableId == original.Id)
                        .PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    || before.Snapshot.Partitions.Any(item => item.OsDiskStableId == original.Id)
                    || result.UniqueId != target.UniqueId || result.ObjectId != target.ObjectId
                    || result.DiskNumber != target.DiskNumber)
                    return null;
                return ObserveInitializedGpt(after, target);
            }
            case ClearDiskCommand:
            {
                var disk = DiskForSamePhysical(snapshot, before, target);
                return disk is not null
                    && disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    && !snapshot.Partitions.Any(item => item.OsDiskStableId == disk.StableId)
                    && !snapshot.StoragePools.Any(pool => !pool.IsPrimordial
                        && pool.MemberPhysicalDiskIds.Contains(disk.PhysicalDiskStableId ?? string.Empty))
                    ? new(CreatedOsDiskId: disk.StableId) : null;
            }
            case CreatePartitionCommand value:
            {
                if (string.IsNullOrWhiteSpace(result.PartitionGuid)) return null;
                var created = FindExact(after, StorageObjectKind.Partition,
                    string.Empty, string.Empty, result.PartitionGuid);
                if (created is null) return null;
                var part = snapshot.Partitions.Single(item => item.StableId == created.Id);
                return part.Offset == value.OffsetBytes && part.Size == value.SizeBytes
                    && part.DiskNumber == target.DiskNumber
                    && exact is not null && part.OsDiskStableId == exact.Id
                    && Guid.TryParse(part.PartitionTypeId, out var actualType)
                    && actualType == PartitionRoleGuid(value.Role)
                    && CreatedPartitionHasNoImplicitFormatOrLetter(snapshot, part)
                    && SiblingPartitionsUnchanged(before, after, target.DiskNumber, part.Guid)
                    ? new(created.Id) : null;
            }
            case DeletePartitionCommand:
                return exact is null
                    && !snapshot.Partitions.Any(item =>
                        GuidEquals(item.Guid, target.PartitionGuid))
                    && !snapshot.Volumes.Any(item => item.PartitionStableId
                        == FindExact(before, StorageObjectKind.Partition,
                            target.UniqueId, target.ObjectId,
                            target.PartitionGuid)?.Id)
                    && SiblingPartitionsUnchanged(before, after, target.DiskNumber,
                        target.PartitionGuid) ? new() : null;
            case ResizePartitionCommand value:
                return exact is not null && snapshot.Partitions
                    .Single(item => item.StableId == exact.Id) is { } resized
                    && resized.Size == value.SizeBytes
                    && resized.Offset == target.OffsetBytes
                    && ResizedPartitionContentIdentityUnchanged(before, after, target, resized)
                    && SiblingPartitionsUnchanged(before, after, target.DiskNumber,
                        target.PartitionGuid) ? new() : null;
            case FormatVolumeCommand value:
            {
                if (exact is null || result.Code != "provider.returned") return null;
                var part = snapshot.Partitions.Single(item => item.StableId == exact.Id);
                var oldPart = FindExact(before, StorageObjectKind.Partition,
                    target.UniqueId, target.ObjectId, target.PartitionGuid);
                var volume = snapshot.Volumes.SingleOrDefault(item =>
                    item.PartitionStableId == part.StableId);
                return oldPart is not null && volume is not null
                    && part.FileSystem.Equals(value.FileSystem.ToString(), StringComparison.OrdinalIgnoreCase)
                    && part.AllocationUnitSize == value.ClusterBytes
                    && volume.FileSystem.Equals(value.FileSystem.ToString(), StringComparison.OrdinalIgnoreCase)
                    && volume.AllocationUnitSize == value.ClusterBytes
                    && string.Equals(part.DriveLetter,
                        before.Snapshot.Partitions.Single(item => item.StableId == oldPart.Id).DriveLetter,
                        StringComparison.OrdinalIgnoreCase)
                    && StringComparer.Ordinal.Equals(part.FileSystemLabel,
                        value.Label ?? string.Empty)
                    && StringComparer.Ordinal.Equals(volume.FileSystemLabel,
                        value.Label ?? string.Empty)
                    && SiblingPartitionsUnchanged(before, after, target.DiskNumber,
                        target.PartitionGuid)
                    ? new(volume.StableId)
                    : null;
            }
            case SetDriveLetterCommand value:
                return exact is not null && string.Equals(snapshot.Partitions
                        .Single(item => item.StableId == exact.Id).DriveLetter,
                        value.NewLetter?.ToString() ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase)
                    && MountPathsMatchPlannedLetterChange(before, after, exact.Id,
                        value.PreviousLetter, value.NewLetter)
                    ? new() : null;
            case RenameVolumeCommand value:
            {
                if (exact is null || target.Kind != StorageObjectKind.Volume
                    || string.IsNullOrWhiteSpace(target.UniqueId)
                    || string.IsNullOrWhiteSpace(target.ObjectId)) return null;
                var volume = snapshot.Volumes.Single(item => item.StableId == exact.Id);
                var partition = snapshot.Partitions.SingleOrDefault(item =>
                    item.StableId == volume.PartitionStableId);
                var diskObject = FindExact(after, StorageObjectKind.OsDisk,
                    target.OsDiskUniqueId, string.Empty, string.Empty);
                // A volume has its own UniqueId/ObjectId. PartitionGuid belongs
                // to its parent partition, not to the MSFT_Volume object.
                return partition is not null && diskObject is not null
                    && GuidEquals(partition.Guid, target.PartitionGuid)
                    && GuidEquals(partition.Guid, target.ParentUniqueId)
                    && partition.OsDiskStableId == diskObject.Id
                    && snapshot.OsDisks.Single(item => item.StableId == diskObject.Id)
                        .Number == target.DiskNumber
                    && StringComparer.Ordinal.Equals(Text(diskObject, "Path"), target.OsDiskPath)
                    && partition.DiskNumber == target.DiskNumber
                    && partition.PartitionNumber == target.PartitionNumber
                    && partition.Offset == target.OffsetBytes
                    && partition.Size == target.SizeBytes
                    && volume.FileSystemLabel == value.Label
                    ? new() : null;
            }
            case CreatePoolCommand value:
            {
                var created = FindExact(after, StorageObjectKind.StoragePool,
                    result.UniqueId ?? string.Empty,
                    result.ObjectId ?? string.Empty, string.Empty);
                if (created is null) return null;
                var pool = snapshot.StoragePools.Single(item => item.StableId == created.Id);
                return exact is not null && !pool.IsPrimordial
                    && pool.FriendlyName == value.Name
                    && pool.MemberPhysicalDiskIds.Count == 1
                    && pool.MemberPhysicalDiskIds[0] == exact.Id
                    && !snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
                    && !snapshot.StorageTiers.Any(item => item.PoolStableId == pool.StableId)
                    ? new(created.Id) : null;
            }
            case DeletePoolCommand:
            {
                var old = FindExact(before, target.Kind, target.UniqueId,
                    target.ObjectId, target.PartitionGuid);
                return old is not null && exact is null
                    && !snapshot.VirtualDisks.Any(item => item.PoolStableId == old.Id)
                    && !snapshot.StorageTiers.Any(item => item.PoolStableId == old.Id)
                    && snapshot.PhysicalDisks.SingleOrDefault(item =>
                        item.StableId == before.Snapshot.StoragePools.Single(pool =>
                            pool.StableId == old.Id).MemberPhysicalDiskIds.Single())
                        is { } released
                    && released.PoolStableId != old.Id
                    && !snapshot.StoragePools.Any(pool => !pool.IsPrimordial
                        && pool.MemberPhysicalDiskIds.Contains(released.StableId))
                    ? new() : null;
            }
            case RenamePoolCommand value:
                return exact is not null && snapshot.StoragePools.Single(item =>
                    item.StableId == exact.Id).FriendlyName == value.Name ? new() : null;
            case CreateVirtualDiskCommand value:
            {
                var created = FindExact(after, StorageObjectKind.VirtualDisk,
                    result.UniqueId ?? string.Empty, result.ObjectId ?? string.Empty, string.Empty);
                if (created is null || exact is null) return null;
                var virtualDisk = snapshot.VirtualDisks.Single(item => item.StableId == created.Id);
                var osDisks = snapshot.OsDisks.Where(item =>
                    item.VirtualDiskStableId == virtualDisk.StableId).ToArray();
                return virtualDisk.PoolStableId == exact.Id
                    && virtualDisk.FriendlyName == value.Name
                    && virtualDisk.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
                    && virtualDisk.ProvisioningType.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
                    && virtualDisk.NumberOfColumns == value.DataColumns
                    && virtualDisk.Interleave == value.InterleaveBytes
                    && virtualDisk.Size == value.SizeBytes
                    && virtualDisk.TierStableIds.Count == 0
                    && osDisks.Length == 1
                    && osDisks[0].PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    && !snapshot.Partitions.Any(item => item.OsDiskStableId == osDisks[0].StableId)
                    ? new(created.Id, osDisks[0].StableId) : null;
            }
            case DeleteVirtualDiskCommand:
            {
                var old = FindExact(before, target.Kind, target.UniqueId,
                    target.ObjectId, target.PartitionGuid);
                if (old is null) return null;
                var oldOsDisks = before.Snapshot.OsDisks.Where(item =>
                    item.VirtualDiskStableId == old.Id).Select(item => item.StableId)
                    .ToHashSet(StringComparer.Ordinal);
                var oldPartitions = before.Snapshot.Partitions.Where(item =>
                    item.OsDiskStableId is { } diskId && oldOsDisks.Contains(diskId)).ToArray();
                var oldPartitionIds = oldPartitions.Select(item => item.StableId)
                    .ToHashSet(StringComparer.Ordinal);
                var oldVolumes = before.Snapshot.Volumes.Where(item =>
                    item.PartitionStableId is { } partitionId
                    && oldPartitionIds.Contains(partitionId)).ToArray();
                var oldVolumeIds = oldVolumes.Select(item => item.StableId)
                    .ToHashSet(StringComparer.Ordinal);
                var oldVolumeIdentities = oldVolumes.Select(item => item.VolumeIdentity)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var oldInstanceIds = before.Snapshot.StorageTiers.Where(item =>
                    item.VirtualDiskStableId == old.Id).Select(item => item.StableId)
                    .ToHashSet(StringComparer.Ordinal);
                var oldPoolId = before.Snapshot.VirtualDisks.Single(item => item.StableId == old.Id).PoolStableId;
                var oldTemplates = before.Snapshot.StorageTiers.Where(item =>
                    item.PoolStableId == oldPoolId && item.VirtualDiskStableId is null).ToArray();
                return exact is null && !snapshot.OsDisks.Any(item =>
                    item.VirtualDiskStableId == old.Id || oldOsDisks.Contains(item.StableId))
                    && !snapshot.Partitions.Any(item =>
                        oldPartitionIds.Contains(item.StableId)
                        || item.OsDiskStableId is { } diskId && oldOsDisks.Contains(diskId)
                        || oldPartitions.Any(oldPart => GuidEquals(item.Guid, oldPart.Guid)))
                    && !snapshot.Volumes.Any(item =>
                        oldVolumeIds.Contains(item.StableId)
                        || item.PartitionStableId is { } partitionId
                            && oldPartitionIds.Contains(partitionId)
                        || oldVolumeIdentities.Contains(item.VolumeIdentity))
                    && !snapshot.StorageTiers.Any(item => item.VirtualDiskStableId == old.Id
                        || oldInstanceIds.Contains(item.StableId))
                    && oldTemplates.All(template => snapshot.StorageTiers.Any(item =>
                        item.StableId == template.StableId && item.VirtualDiskStableId is null
                        && item.PoolStableId == template.PoolStableId
                        && item.MediaType == template.MediaType
                        && item.ResiliencySettingName == template.ResiliencySettingName
                        && item.NumberOfColumns == template.NumberOfColumns
                        && item.Interleave == template.Interleave))
                    ? new() : null;
            }
            case ResizeVirtualDiskCommand value:
                return exact is not null && snapshot.VirtualDisks.Single(item =>
                    item.StableId == exact.Id).Size == value.SizeBytes ? new() : null;
            case RenameVirtualDiskCommand value:
                return exact is not null && snapshot.VirtualDisks.Single(item =>
                    item.StableId == exact.Id).FriendlyName == value.Name ? new() : null;
            case CreateTierCommand value:
            {
                var created = FindExact(after, StorageObjectKind.StorageTier,
                    result.UniqueId ?? string.Empty, result.ObjectId ?? string.Empty, string.Empty);
                if (created is null || exact is null) return null;
                var tier = snapshot.StorageTiers.Single(item => item.StableId == created.Id);
                return tier.PoolStableId == exact.Id
                    && StringComparer.Ordinal.Equals(tier.FriendlyName, value.Name)
                    && tier.VirtualDiskStableId is null
                    && tier.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
                    && tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
                    && tier.Interleave == value.InterleaveBytes
                    && tier.NumberOfColumns == value.DataColumns
                    ? new(created.Id) : null;
            }
            case CreateTieredVirtualDiskCommand value:
            {
                var created = FindExact(after, StorageObjectKind.VirtualDisk,
                    result.UniqueId ?? string.Empty, result.ObjectId ?? string.Empty, string.Empty);
                if (created is null || exact is null) return null;
                var virtualDisk = snapshot.VirtualDisks.Single(item => item.StableId == created.Id);
                // A pool template is cloned into a VD tier instance. Bind the
                // input from the frozen pre-state and the output by the exact
                // returned VD's fresh association, never by name or same-ID.
                var templateObject = FindExact(before, StorageObjectKind.StorageTier,
                    target.RelatedUniqueId, target.RelatedObjectId, string.Empty);
                if (templateObject is null || value.Tier.Existing?.ProviderKey != templateObject.Id)
                    return null;
                var template = before.Snapshot.StorageTiers.Single(item => item.StableId == templateObject.Id);
                var closure = after.RequireSinglePhysicalClosure([new StorageObjectId(after.SystemId,
                    StorageObjectKind.VirtualDisk, created.Id)]);
                var input = result.TieredCreationInput;
                if (template.VirtualDiskStableId is not null || template.PoolStableId != exact.Id
                    || template.Size != 0 || template.FootprintOnPool != 0
                    || !ReturnedZero(templateObject, "AllocatedSize")
                    || template.MediaType != "HDD" || template.ResiliencySettingName != "Simple"
                    || template.NumberOfColumns != 1 || template.Interleave != 65536
                    || before.Facts.Relationships.Count(item => !item.IsRetained
                        && item.Kind == "template-pool-member" && item.FromId == template.StableId
                        && item.ToId == closure.PhysicalDiskId) != 1
                    || input is null || input.TemplateUniqueId != target.RelatedUniqueId
                    || input.TemplateObjectId != target.RelatedObjectId || input.PoolUniqueId != target.UniqueId
                    || input.PhysicalMemberUniqueId != target.PhysicalMemberUniqueId
                    || input.MediaType != "HDD" || input.ResiliencySettingName != "Simple"
                    || input.ProvisioningType != "Fixed" || input.NumberOfColumns != 1
                    || input.Interleave != 65536 || input.SizeBytes != value.SizeBytes)
                    return null;
                var associations = after.Facts.Relationships.Where(item => !item.IsRetained
                    && item.Kind == "virtual-disk-tier" && item.FromId == created.Id).ToArray();
                if (associations.Length != 1 || virtualDisk.TierStableIds.Count != 1
                    || virtualDisk.TierStableIds[0] != associations[0].ToId) return null;
                var tier = snapshot.StorageTiers.SingleOrDefault(item => item.StableId == associations[0].ToId);
                if (tier is null) return null;
                var tierObject = after.RequireObject(new StorageObjectId(after.SystemId,
                    StorageObjectKind.StorageTier, tier.StableId));
                var pool = snapshot.StoragePools.Single(item => item.StableId == exact.Id);
                var osDisks = snapshot.OsDisks.Where(item =>
                    item.VirtualDiskStableId == created.Id).ToArray();
                return tier.VirtualDiskStableId == created.Id && tier.PoolStableId == exact.Id
                    && virtualDisk.PoolStableId == exact.Id
                    && virtualDisk.FriendlyName == value.Name
                    && virtualDisk.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                    && virtualDisk.OperationalStatus.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    && !pool.IsPrimordial
                    && pool.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                    && pool.OperationalStatus.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    && pool.MemberPhysicalDiskIds.Count == 1
                    && pool.MemberPhysicalDiskIds[0] == closure.PhysicalDiskId
                    && tier.MemberPhysicalDiskIds.Count == 1
                    && tier.MemberPhysicalDiskIds[0] == closure.PhysicalDiskId
                    && virtualDisk.Size == value.SizeBytes && tier.Size == value.SizeBytes
                    && TieredLayoutMatches(created, tierObject, virtualDisk, tier, value.SizeBytes)
                    && tier.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
                    && tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
                    && tier.Interleave == 65536 && tier.NumberOfColumns == 1
                    && osDisks.Length == 1
                    && osDisks[0].Size == value.SizeBytes
                    && !osDisks[0].IsBoot && !osDisks[0].IsSystem && !osDisks[0].IsOffline
                    && ReturnedFalse(after.RequireObject(new StorageObjectId(after.SystemId,
                        StorageObjectKind.OsDisk, osDisks[0].StableId)), "IsReadOnly")
                    && ReturnedFalse(after.RequireObject(new StorageObjectId(after.SystemId,
                        StorageObjectKind.OsDisk, osDisks[0].StableId)), "IsClustered")
                    && osDisks[0].PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    && !snapshot.Partitions.Any(item => item.OsDiskStableId == osDisks[0].StableId)
                    ? new(created.Id, osDisks[0].StableId, new WindowsTieredCreationMapping(
                        templateObject.Id, target.RelatedUniqueId, target.RelatedObjectId,
                        created.Id, Text(created, "UniqueId"), Text(created, "ObjectId"),
                        tierObject.Id, Text(tierObject, "UniqueId"), Text(tierObject, "ObjectId"),
                        pool.StableId, closure.PhysicalDiskId, value.SizeBytes,
                        tier.MediaType, tier.ResiliencySettingName, input.ProvisioningType,
                        1, 65536, after.Facts.Relationships.Where(item => !item.IsRetained
                            && (item.FromId == created.Id || item.ToId == created.Id
                                || item.FromId == tier.StableId || item.ToId == tier.StableId
                                || item.Kind == "pool-member" && item.FromId == pool.StableId)).ToArray(),
                        created.Fields.Where(item => TieredLayoutFieldNames.Contains(item.Name, StringComparer.Ordinal)).ToArray(),
                        tierObject.Fields.Where(item => TieredLayoutFieldNames.Contains(item.Name, StringComparer.Ordinal)).ToArray())) : null;
            }
            case DeleteTierCommand:
            {
                var old = FindExact(before, StorageObjectKind.StorageTier,
                    target.UniqueId, target.ObjectId, string.Empty);
                var oldTier = old is null ? null : before.Snapshot.StorageTiers
                    .SingleOrDefault(item => item.StableId == old.Id);
                return oldTier is { VirtualDiskStableId: null } && exact is null
                    && !snapshot.VirtualDisks.Any(item => item.TierStableIds.Contains(oldTier.StableId))
                    ? new() : null;
            }
            case ResizeTierCommand value:
                return exact is not null && snapshot.StorageTiers.Single(item =>
                    item.StableId == exact.Id).Size == value.SizeBytes ? new() : null;
            case RenameTierCommand value:
            {
                var old = FindExact(before, StorageObjectKind.StorageTier,
                    target.UniqueId, target.ObjectId, string.Empty);
                var oldTier = old is null ? null : before.Snapshot.StorageTiers
                    .SingleOrDefault(item => item.StableId == old.Id);
                var renamed = exact is null ? null : snapshot.StorageTiers
                    .SingleOrDefault(item => item.StableId == exact.Id);
                return oldTier is not null && renamed is not null
                    && renamed.FriendlyName == value.Name
                    && renamed.PoolStableId == oldTier.PoolStableId
                    && renamed.VirtualDiskStableId == oldTier.VirtualDiskStableId
                    && renamed.MediaType == oldTier.MediaType
                    && renamed.ResiliencySettingName == oldTier.ResiliencySettingName
                    && renamed.NumberOfColumns == oldTier.NumberOfColumns
                    && renamed.Interleave == oldTier.Interleave
                    && renamed.NumberOfDataCopies == oldTier.NumberOfDataCopies
                    && renamed.PhysicalDiskRedundancy == oldTier.PhysicalDiskRedundancy
                    && renamed.Size == oldTier.Size && renamed.FootprintOnPool == oldTier.FootprintOnPool
                    && SameStringSet(renamed.MemberPhysicalDiskIds, oldTier.MemberPhysicalDiskIds)
                    && SameTierAssociations(before, after, old!.Id, exact!.Id)
                    ? new() : null;
            }
            default: return null;
        }
    }

    private static VerifiedPostcondition? ObserveInitializedGpt(
        WindowsRealStorageTopology topology, WindowsStorageCommandTarget target)
    {
        var exact = FindExact(topology, StorageObjectKind.OsDisk,
            target.UniqueId, target.ObjectId, string.Empty);
        if (exact is null) return null;
        var snapshot = topology.Snapshot;
        var disk = snapshot.OsDisks.Single(item => item.StableId == exact.Id);
        if (disk.PartitionStyle != "GPT" || disk.IsOffline || disk.IsBoot || disk.IsSystem
            || disk.Number != target.DiskNumber || disk.Size != target.SizeBytes)
            return null;
        var partitions = snapshot.Partitions.Where(item => item.OsDiskStableId == disk.StableId).ToArray();
        if (partitions.Length > 1) return null;
        var msr = partitions.SingleOrDefault();
        // This is the provider layout observed on the admitted Windows host.
        // Other layouts need explicit evidence rather than a broad MSR wildcard.
        if (msr is not null && (msr.IsBoot || msr.IsSystem
            || !GuidEquals(msr.PartitionTypeId, "e3c9e316-0b5c-4db8-817d-f92df00215ae")
            || !GuidEquals(msr.GptType, "e3c9e316-0b5c-4db8-817d-f92df00215ae")
            || !Guid.TryParse(msr.Guid, out _)
            || msr.Offset != 17408 || msr.Size != 16759808
            || !CreatedPartitionHasNoImplicitFormatOrLetter(snapshot, msr)
            || snapshot.Volumes.Any(item => item.PartitionStableId == msr.StableId)
            || !WindowsRealOperationPlanner.HasEmptyPartitionMountFacts(topology, msr)))
            return null;
        return new(CreatedOsDiskId: disk.StableId,
            GptInitialization: new(disk.StableId, target.UniqueId, target.ObjectId,
                msr?.StableId, msr?.Guid, msr?.Offset, msr?.Size));
    }

    private static OsDiskInfo? DiskForSamePhysical(
        StorageSnapshot snapshot,
        WindowsRealStorageTopology before,
        WindowsStorageCommandTarget target)
    {
        var old = FindExact(before, StorageObjectKind.OsDisk,
            target.UniqueId, target.ObjectId, string.Empty);
        if (old is null) return null;
        var previous = before.Snapshot.OsDisks.Single(item => item.StableId == old.Id);
        return snapshot.OsDisks.SingleOrDefault(item =>
            item.PhysicalDiskStableId == previous.PhysicalDiskStableId
            && item.Number == target.DiskNumber);
    }

    private static Guid PartitionRoleGuid(RealPartitionRole role) => role switch
    {
        RealPartitionRole.BasicData => Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
        RealPartitionRole.Efi => Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"),
        RealPartitionRole.Msr => Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae"),
        RealPartitionRole.Recovery => Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac"),
        _ => throw new InvalidDataException("Unsupported partition role.")
    };

    private static bool SameStringSet(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool SameTierAssociations(WindowsRealStorageTopology before,
        WindowsRealStorageTopology after, string oldId, string newId)
    {
        static string[] Associations(WindowsRealStorageTopology topology, string id) =>
            topology.Facts.Relationships.Where(item => !item.IsRetained
                && (item.FromId == id || item.ToId == id)
                && item.Kind is "pool-tier" or "virtual-disk-tier" or "tier-member" or "template-pool-member")
                .Select(item => item.Kind + "|" + item.FromId + "|" + item.ToId)
                .Order(StringComparer.Ordinal).ToArray();
        return oldId == newId && Associations(before, oldId).SequenceEqual(Associations(after, newId), StringComparer.Ordinal);
    }

    private static bool ResizedPartitionContentIdentityUnchanged(
        WindowsRealStorageTopology before, WindowsRealStorageTopology after,
        WindowsStorageCommandTarget target, PartitionInfo resized)
    {
        var oldObject = FindExact(before, StorageObjectKind.Partition,
            target.UniqueId, target.ObjectId, target.PartitionGuid);
        var original = oldObject is null ? null : before.Snapshot.Partitions
            .SingleOrDefault(item => item.StableId == oldObject.Id);
        if (original is null || original.StableId != resized.StableId
            || original.OsDiskStableId != resized.OsDiskStableId
            || original.PartitionTypeId != resized.PartitionTypeId
            || original.IsBoot != resized.IsBoot || original.IsSystem != resized.IsSystem
            || original.IsHidden != resized.IsHidden
            || !StringComparer.OrdinalIgnoreCase.Equals(original.FileSystem, resized.FileSystem)
            || original.AllocationUnitSize != resized.AllocationUnitSize
            || original.FileSystemLabel != resized.FileSystemLabel
            || !StringComparer.OrdinalIgnoreCase.Equals(original.DriveLetter, resized.DriveLetter)
            || !StringComparer.OrdinalIgnoreCase.Equals(original.Path, resized.Path)) return false;

        var oldVolumes = before.Snapshot.Volumes.Where(item => item.PartitionStableId == original.StableId).ToArray();
        var newVolumes = after.Snapshot.Volumes.Where(item => item.PartitionStableId == resized.StableId).ToArray();
        string[] preservedFields = ["FileSystem", "FileSystemLabel", "AllocationUnitSize", "Path", "DriveLetter", "AccessPaths"];
        if (before.Snapshot.FieldIssues.Concat(after.Snapshot.FieldIssues).Any(issue =>
            (issue.ObjectId == original.StableId || issue.ObjectId == resized.StableId)
            && preservedFields.Contains(issue.FieldName, StringComparer.Ordinal)
            && issue.State != FieldReadState.Returned)) return false;
        if (oldVolumes.Length == 0)
            return newVolumes.Length == 0 && (original.FileSystem is "" or "RAW");
        if (oldVolumes.Length != 1 || newVolumes.Length != 1) return false;
        var previous = oldVolumes[0];
        var current = newVolumes[0];
        var oldVolumeObject = before.RequireObject(new(before.SystemId, StorageObjectKind.Volume, previous.StableId));
        var uniqueId = Text(oldVolumeObject, "UniqueId");
        var objectId = Text(oldVolumeObject, "ObjectId");
        var currentVolumeObject = FindExact(after, StorageObjectKind.Volume, uniqueId, objectId, string.Empty);
        if (string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(objectId)
            || currentVolumeObject is null || currentVolumeObject.Id != current.StableId
            || !ReturnedVolumeContentMetadata(oldVolumeObject)
            || !ReturnedVolumeContentMetadata(currentVolumeObject)
            || !StringComparer.OrdinalIgnoreCase.Equals(oldVolumeObject.Field("Path")!.DisplayValue(),
                currentVolumeObject.Field("Path")!.DisplayValue())
            || !StringComparer.OrdinalIgnoreCase.Equals(
                ReturnedVolumeLetter(oldVolumeObject), ReturnedVolumeLetter(currentVolumeObject)))
            return false;
        if (before.Snapshot.FieldIssues.Concat(after.Snapshot.FieldIssues).Any(issue =>
            (issue.ObjectId == previous.StableId || issue.ObjectId == current.StableId)
            && preservedFields.Contains(issue.FieldName, StringComparer.Ordinal)
            && issue.State != FieldReadState.Returned)) return false;
        return previous.StableId == current.StableId && previous.VolumeIdentity == current.VolumeIdentity
            && previous.PartitionStableId == current.PartitionStableId
            && StringComparer.OrdinalIgnoreCase.Equals(previous.FileSystem, current.FileSystem)
            && previous.FileSystemLabel == current.FileSystemLabel
            && previous.AllocationUnitSize == current.AllocationUnitSize
            && SameStringSet(previous.AccessPaths, current.AccessPaths);
    }

    private static bool ReturnedVolumeContentMetadata(WinPoolSourceObject source)
    {
        // Projection may display a successful Win32_LogicalDisk fallback.
        // Verification still requires the exact MSFT volume's own read receipt.
        foreach (var name in new[] { "FileSystem", "FileSystemLabel", "Path" })
        {
            if (source.Field(name) is not { ReadState: FieldReadState.Returned,
                    Value: { ValueKind: JsonValueKind.String } value }
                || name == "Path" && string.IsNullOrWhiteSpace(value.GetString())) return false;
        }
        var fileSystem = Text(source, "FileSystem");
        if (source.Field("AllocationUnitSize") is not { ReadState: FieldReadState.Returned } cluster)
            return false;
        if (cluster.Value is null) return false;
        if (cluster.Value is { ValueKind: JsonValueKind.Null })
        {
            if (fileSystem is not "" && !fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if (cluster.Value is not { ValueKind: JsonValueKind.Number } bytes || !bytes.TryGetUInt64(out var size)
            || size == 0 && fileSystem is not "" && !fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase))
            return false;
        return ReturnedVolumeLetter(source) is not null;
    }

    private static string? ReturnedVolumeLetter(WinPoolSourceObject source)
    {
        if (source.Field("DriveLetter") is not { ReadState: FieldReadState.Returned } field) return null;
        if (field.Value is { ValueKind: JsonValueKind.Null }) return string.Empty;
        if (field.Value is not { ValueKind: JsonValueKind.String } value) return null;
        var text = value.GetString();
        if (text is "" or "\0") return string.Empty;
        return text is { Length: 1 } && char.IsAsciiLetter(text[0]) ? text.ToUpperInvariant() : null;
    }

    private static bool SiblingPartitionsUnchanged(
        WindowsRealStorageTopology before,
        WindowsRealStorageTopology after,
        int? diskNumber,
        string? excludedGuid)
    {
        if (diskNumber is null) return false;
        static string[] Signatures(StorageSnapshot snapshot, int number, string? excluded) =>
            snapshot.Partitions.Where(item => item.DiskNumber == number
                && !GuidEquals(item.Guid, excluded))
                .Select(item => string.Join('|', item.Guid, item.Offset, item.Size,
                    item.PartitionTypeId, item.IsBoot, item.IsSystem))
                .Order(StringComparer.Ordinal).ToArray();
        return Signatures(before.Snapshot, diskNumber.Value, excludedGuid)
            .SequenceEqual(Signatures(after.Snapshot, diskNumber.Value, excludedGuid),
                StringComparer.Ordinal);
    }

    private static bool GuidEquals(string value, string? other) =>
        other is not null && Guid.TryParse(value, out var actual)
            && Guid.TryParse(other, out var expected) && actual == expected;

    private static bool CreatedPartitionHasNoImplicitFormatOrLetter(
        StorageSnapshot snapshot, PartitionInfo partition)
    {
        var volume = snapshot.Volumes.SingleOrDefault(item =>
            item.PartitionStableId == partition.StableId);
        return (partition.FileSystem is "" or "RAW")
            && string.IsNullOrWhiteSpace(partition.DriveLetter)
            && (volume is null || (volume.FileSystem is "" or "RAW")
                && !volume.AccessPaths.Any(StorageAccessPath.IsDriveLetter));
    }

    private static bool MountPathsMatchPlannedLetterChange(
        WindowsRealStorageTopology before,
        WindowsRealStorageTopology after,
        string partitionId,
        char? previousLetter,
        char? newLetter)
    {
        var oldPartition = before.Snapshot.Partitions.SingleOrDefault(item =>
            item.StableId == partitionId);
        var newPartition = after.Snapshot.Partitions.SingleOrDefault(item =>
            item.StableId == partitionId);
        var oldVolume = before.Snapshot.Volumes.SingleOrDefault(item =>
            item.PartitionStableId == partitionId);
        var newVolume = after.Snapshot.Volumes.SingleOrDefault(item =>
            item.PartitionStableId == partitionId);
        if (oldPartition is null || newPartition is null
            || oldVolume is null || newVolume is null
            || !string.Equals(oldPartition.DriveLetter,
                previousLetter?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(newPartition.DriveLetter,
                newLetter?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || oldVolume.StableId != newVolume.StableId) return false;
        static string[] NonLetterPaths(VolumeInfo volume) => volume.AccessPaths
            .Where(path => !StorageAccessPath.IsDriveLetter(path))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        static HashSet<string> LetterPaths(VolumeInfo volume) => volume.AccessPaths
            .Select(path => StorageAccessPath.TryGetDriveLetter(path, out var letter)
                ? letter : string.Empty)
            .Where(letter => letter.Length != 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedLetters = LetterPaths(oldVolume);
        if (previousLetter is { } previous)
            expectedLetters.Remove(previous.ToString());
        if (newLetter is { } next)
            expectedLetters.Add(next.ToString());
        return NonLetterPaths(oldVolume).SequenceEqual(
                NonLetterPaths(newVolume), StringComparer.OrdinalIgnoreCase)
            && expectedLetters.SetEquals(LetterPaths(newVolume));
    }

    private static WinPoolSourceObject? FindExact(
        WindowsRealStorageTopology topology,
        StorageObjectKind kind,
        string uniqueId,
        string objectId,
        string partitionGuid)
    {
        var type = kind switch
        {
            StorageObjectKind.StoragePool => FactObjectType.StoragePool,
            StorageObjectKind.PhysicalDisk => FactObjectType.PhysicalDisk,
            StorageObjectKind.VirtualDisk => FactObjectType.VirtualDisk,
            StorageObjectKind.StorageTier => FactObjectType.StorageTier,
            StorageObjectKind.OsDisk => FactObjectType.Disk,
            StorageObjectKind.Partition => FactObjectType.Partition,
            StorageObjectKind.Volume => FactObjectType.Volume,
            _ => throw new InvalidDataException("Unsupported real object kind.")
        };
        if (string.IsNullOrWhiteSpace(uniqueId)
            && string.IsNullOrWhiteSpace(objectId)
            && string.IsNullOrWhiteSpace(partitionGuid)) return null;
        var found = topology.Facts.Objects.Where(item => item.ObjectType == type
            && (string.IsNullOrWhiteSpace(uniqueId)
                || StringComparer.Ordinal.Equals(Text(item, "UniqueId"), uniqueId))
            && (string.IsNullOrWhiteSpace(objectId)
                || StringComparer.Ordinal.Equals(Text(item, "ObjectId"), objectId))
            && (kind != StorageObjectKind.Partition || string.IsNullOrWhiteSpace(partitionGuid)
                || Guid.TryParse(Text(item, "Guid"), out var observed)
                    && Guid.TryParse(partitionGuid, out var expected)
                    && observed == expected)).ToArray();
        if (found.Length > 1)
            throw new InvalidDataException("A provider identity maps to multiple current objects.");
        return found.SingleOrDefault();
    }

    private static string Text(WinPoolSourceObject item, string name)
    {
        var field = item.Field(name);
        return field is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
            ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
    }
}
