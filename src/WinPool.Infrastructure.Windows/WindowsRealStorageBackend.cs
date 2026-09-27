using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsVerifiedStepEvidence(
    string PostFingerprint,
    string PhysicalMemberFingerprint,
    string? CreatedObjectId,
    string ProviderCode,
    string? CreatedOsDiskId = null);

public sealed record WindowsNoEffectStepEvidence(
    bool NoWindowsCall,
    string Code,
    string PhysicalMemberFingerprint);

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

    public WindowsRealStorageBackend(
        IWindowsRealStorageCommandAdapter adapter,
        WindowsRealOperationPlanner? planner = null,
        WindowsRealStorageTopologyReader? topologyReader = null,
        TimeProvider? timeProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.topologyReader = topologyReader ?? new WindowsRealStorageTopologyReader();
        this.planner = planner ?? new WindowsRealOperationPlanner(this.topologyReader);
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

    public async Task<RealStepPreflight> PreflightStepAsync(
        OperationPlan plan,
        RealOperationStep step,
        IReadOnlyDictionary<string, string> verifiedStepOutputs,
        CancellationToken cancellationToken)
    {
        var stored = RequireFrozenStep(plan, step);
        var topology = await topologyReader.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(topology.MachineBinding,
                plan.RealOperation!.MachineBinding))
            throw new InvalidDataException("The machine identity changed after preparation.");
        var physicalId = plan.Targets.SingleOrDefault(target =>
            target.Kind == StorageObjectKind.PhysicalDisk);
        if (physicalId.Kind != StorageObjectKind.PhysicalDisk)
            throw new InvalidDataException("The real plan lacks its frozen physical member.");
        var closure = topology.RequireSinglePhysicalClosure([physicalId]);
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
        var before = await topologyReader.CaptureAsync(cancellationToken)
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

        WindowsStorageCommandResult provider;
        try
        {
            // The caller has already persisted CallIssued. Once this call starts,
            // transport cancellation may not terminate the storage provider.
            provider = await adapter.ExecuteAsync(
                stored.Command, target, CancellationToken.None)
                .ConfigureAwait(false);
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
                        true, provider.Code, beforeClosure.PhysicalMemberFingerprint)));
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
                var after = await topologyReader.CaptureAsync(cancellationToken)
                    .ConfigureAwait(false);
                var afterClosure = after.RequireSinglePhysicalClosure([physical]);
                if (!StringComparer.Ordinal.Equals(afterClosure.PhysicalMemberFingerprint,
                        plan.RealOperation!.PhysicalMemberFingerprint))
                    return new RealStepResult(RealStepOutcome.OutcomeUnknown,
                        "real.physical_member_changed", JsonSerializer.Serialize(provider));
                var verified = VerifyAfter(stored.Command, target, provider, before, after);
                if (verified is not null)
                {
                    var evidence = new WindowsVerifiedStepEvidence(
                        afterClosure.Fingerprint,
                        afterClosure.PhysicalMemberFingerprint,
                        verified.CreatedObjectId,
                        provider.Code,
                        verified.CreatedOsDiskId);
                    return new RealStepResult(RealStepOutcome.Verified,
                        "real.step_verified", JsonSerializer.Serialize(evidence),
                        evidence.CreatedObjectId);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException
                or IOException or UnauthorizedAccessException or InvalidOperationException)
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
            topology = await topologyReader.CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(topology.MachineBinding, frozen.MachineBinding))
                return Unknown(persistedSteps, "real.reconciliation_machine_changed");
            var physical = plan.Targets.Single(item =>
                item.Kind == StorageObjectKind.PhysicalDisk);
            closure = topology.RequireSinglePhysicalClosure([physical]);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Unknown(persistedSteps, "real.reconciliation_capture_failed");
        }

        if (!StringComparer.Ordinal.Equals(closure.PhysicalMemberFingerprint,
                frozen.PhysicalMemberFingerprint))
            return Unknown(persistedSteps, "real.reconciliation_physical_changed");

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

    internal sealed record VerifiedPostcondition(
        string? CreatedObjectId = null,
        string? CreatedOsDiskId = null);

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
                var disk = DiskForSamePhysical(snapshot, before, target);
                return disk is not null
                    && disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                    && !snapshot.Partitions.Any(item => item.OsDiskStableId == disk.StableId)
                    ? new(CreatedOsDiskId: disk.StableId) : null;
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
                return exact is not null && snapshot.Volumes
                    .Single(item => item.StableId == exact.Id).FileSystemLabel == value.Label
                    ? new() : null;
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
                var tierObject = FindExact(after, StorageObjectKind.StorageTier,
                    target.RelatedUniqueId, target.RelatedObjectId, string.Empty);
                var tier = tierObject is null ? null : snapshot.StorageTiers
                    .SingleOrDefault(item => item.StableId == tierObject.Id);
                var osDisks = snapshot.OsDisks.Where(item =>
                    item.VirtualDiskStableId == created.Id).ToArray();
                return tier is not null && tier.VirtualDiskStableId == created.Id
                    && virtualDisk.PoolStableId == exact.Id
                    && virtualDisk.TierStableIds.Contains(tier.StableId)
                    && virtualDisk.Size == value.SizeBytes && osDisks.Length == 1
                    ? new(created.Id, osDisks[0].StableId) : null;
            }
            case DeleteTierCommand:
                return exact is null ? new() : null;
            case ResizeTierCommand value:
                return exact is not null && snapshot.StorageTiers.Single(item =>
                    item.StableId == exact.Id).Size == value.SizeBytes ? new() : null;
            case RenameTierCommand value:
                return exact is not null && snapshot.StorageTiers.Single(item =>
                    item.StableId == exact.Id).FriendlyName == value.Name ? new() : null;
            default: return null;
        }
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
            && (string.IsNullOrWhiteSpace(partitionGuid)
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
