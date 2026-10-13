using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>Owns the exact frozen stage until its result and related facts have been mapped.
/// This is read-only recovery state, never authority to accept or resume a write.</summary>
public sealed class RealStructureResultSynchronization
{
    public sealed record OutputMapping(StorageObjectKind Kind, string PoolId, string StepId, string? MediaType = null);
    private sealed record Stage(SimulationEditingSession Session, long BindingGeneration,
        long ContentRevision, AgentRealOperationResponse Frozen, RealOperationIntentRequest Proposal,
        OutputMapping? Mapping, RealAutomaticLayoutProgress? Layout);
    private readonly Dictionary<OperationId, Stage> stages = [];

    public bool HasPending(SimulationEditingSession session) => stages.Values.Any(stage =>
        ReferenceEquals(stage.Session, session) && stage.BindingGeneration == session.BindingGeneration);

    public bool Owns(AgentRealOperationResponse response) => stages.TryGetValue(response.Plan.OperationId, out var stage)
        && StringComparer.Ordinal.Equals(stage.Frozen.Plan.PlanHash, response.Plan.PlanHash);

    // Called after exact frozen-plan authorization and before the first Accept attempt.
    // A lost Accept reply, no Query reply, Running reply and polling expiry all retain this same stage.
    public void Register(SimulationEditingSession session, AgentRealOperationResponse frozen,
        RealOperationIntentRequest proposal, OutputMapping? mapping = null, RealAutomaticLayoutProgress? layout = null)
    {
        if (frozen.State != RealOperationState.Prepared || frozen.Plan.SystemId != session.SystemId
            || frozen.Plan.OperationId.Value == Guid.Empty || string.IsNullOrWhiteSpace(frozen.Plan.PlanHash)
            || frozen.Plan.PlanHash != OperationPlanHasher.Compute(frozen.Plan)
            || frozen.Plan.Intent != proposal.Intent || proposal.SystemId != session.SystemId
            || frozen.Plan.RealOperation is not { } real || real.Steps.Count != proposal.Steps.Count
            // Exact parameters (including Agent-added fresh MAX proof) were checked by
            // RealStructureApplyAuthorization.ValidateFrozen immediately before registration.
            || proposal.Steps.Any(step => !real.Steps.Any(item => item.Id == step.Id && item.Command.GetType() == step.Command.GetType())))
            throw new InvalidDataException("The draft stage must match its exact frozen plan.");
        if (HasPending(session)) throw new InvalidOperationException("A draft result is still being synchronized.");
        if (mapping is { Kind: StorageObjectKind.StorageTier } tierMapping)
        {
            if (real.Steps.SingleOrDefault(item => item.Id == tierMapping.StepId)?.Command is not CreateTierCommand tier
                || tierMapping.MediaType is { } requestedMedia && requestedMedia != tier.EffectiveMediaType)
                throw new InvalidDataException("The tier mapping must match the frozen template creation and its exact media.");
            mapping = tierMapping with { MediaType = tier.EffectiveMediaType };
        }
        stages.Add(frozen.Plan.OperationId, new(session, session.BindingGeneration, session.ContentRevision,
            frozen, proposal, mapping, layout));
        session.SetRealResultSynchronizationPending(true);
    }

    public void ObserveUnresolved(AgentRealOperationResponse response)
    {
        if (!Owns(response)) return;
        var stage = stages[response.Plan.OperationId];
        if (stage.BindingGeneration != stage.Session.BindingGeneration) return;
        stage.Layout?.Observe(stage.Proposal, response);
        if (response.RequiresReconciliation)
            stage.Session.MarkOutcomeUnknown(response.Plan.OperationId, response.Plan.PlanHash);
    }

    public bool Reconcile(AgentRealOperationResponse response, StorageSystemDocument fresh, bool inventoryRefreshed)
    {
        if (!stages.TryGetValue(response.Plan.OperationId, out var stage)) return true;
        if (!Owns(response) || response.Plan.PlanHash != OperationPlanHasher.Compute(response.Plan)
            || response.Plan.SystemId != stage.Frozen.Plan.SystemId
            || response.Steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != response.Steps.Count
            || response.Steps.Any(step => !stage.Proposal.Steps.Any(item => item.Id == step.StepId))
            || response.State == RealOperationState.Succeeded && (response.Steps.Count != stage.Proposal.Steps.Count
                || response.Steps.Any(step => step.State != RealOperationStepState.Verified))
            || !RealOperationSubmissionState.IsReconciledTerminal(response)
            || response.Steps.Any(step => step.State == RealOperationStepState.OutcomeUnknown)) return false;
        var session = stage.Session;
        // Rebinding/discarding explicitly abandons that local draft, never changes the newer draft.
        if (session.BindingGeneration != stage.BindingGeneration)
        {
            if (!inventoryRefreshed || !MatchesFreshScope()) return false;
            stages.Remove(response.Plan.OperationId);
            return true;
        }
        // Cancel/reject with only pre-call step states is affirmative no-write proof.
        // Failed is deliberately excluded: a Failed step can follow CallIssued and
        // UnexpectedEffect even when no step was verified.
        var provedNoCall = response.State is RealOperationState.Cancelled or RealOperationState.Rejected
            && response.Steps.Count == stage.Proposal.Steps.Count
            && response.Steps.All(step => step.State is RealOperationStepState.Pending or RealOperationStepState.StoppedBeforeCall);
        if (provedNoCall)
        {
            // Immediate pre-call cancellation may close without an inventory read.
            // Background recovery has already refreshed: advance that exact baseline
            // before notifying the page, preserving the still-unexecuted draft goal.
            if (inventoryRefreshed && !MatchesFreshScope()) return false;
            stage.Layout?.ReconcileTerminal(stage.Proposal, response);
            if (inventoryRefreshed) RebaseRemainingDraft(session, fresh);
            Close();
            return true;
        }
        if (!inventoryRefreshed || !MatchesFreshScope()) return false;
        var createdId = stage.Mapping is { } mapping && response.Steps.Any(step =>
            step.StepId == mapping.StepId && step.State == RealOperationStepState.Verified)
            ? RequireVerifiedCreatedId(response, fresh, mapping) : null;
        // Validate every output before mutating the remaining draft. Merge only stage output/state;
        // ContentRevision is deliberately distinct from BindingGeneration, and is never used as
        // proof that a captured whole PoolEditIntent can safely replace current user parameters.
        if (createdId is not null && stage.Mapping is { Kind: StorageObjectKind.VirtualDisk } vdMapping
            && !session.PoolIntents.ContainsKey(vdMapping.PoolId))
            throw new InvalidDataException("The verified virtual disk no longer has a matching draft goal.");
        if (createdId is not null && stage.Mapping is { Kind: StorageObjectKind.StoragePool } poolMapping
            && !session.PoolIntents.ContainsKey(poolMapping.PoolId))
            throw new InvalidDataException("The verified pool no longer has a matching draft goal.");
        if (createdId is not null && stage.Mapping is { Kind: StorageObjectKind.StorageTier } tierMapping
            && (!session.PoolIntents.TryGetValue(tierMapping.PoolId, out var tierIntent)
                || tierIntent.Tiers?.Count(item => item.MediaType == tierMapping.MediaType) != 1))
            throw new InvalidDataException("The verified template no longer has one matching media draft goal.");
        if (stage.Layout is { } layout)
        {
            layout.ReconcileTerminal(stage.Proposal, response);
            var current = session.PoolIntents.Values.SingleOrDefault(intent => intent.VerifiedVirtualDiskId == layout.VirtualDiskId)
                ?? throw new InvalidDataException("The automatic layout no longer has its exact draft goal.");
            // Validate the verified prefix against fresh identities/geometry/format.
            // Building a remaining proposal is read-only; it is discarded and never accepted here.
            _ = layout.BuildNext(fresh, current);
        }
        if (createdId is not null && stage.Mapping is { } output)
        {
            if (output.Kind == StorageObjectKind.StoragePool) RemapCreatedPool(session, output.PoolId, createdId);
            else if (output.Kind == StorageObjectKind.StorageTier)
            {
                var current = session.PoolIntents[output.PoolId];
                session.PoolIntents[output.PoolId] = current with
                { Tiers = current.Tiers!.Select(item => item.MediaType == output.MediaType ? item with { VerifiedTemplateId = createdId } : item).ToArray() };
            }
            else
            {
                RemapCreatedVirtualDisk(session, output.PoolId, createdId);
                var current = session.PoolIntents[output.PoolId];
                session.PoolIntents[output.PoolId] = current with
                { VerifiedVirtualDiskId = createdId, PendingAutomaticLayout = current.AutoCreatePartition };
            }
        }
        RebaseRemainingDraft(session, fresh);
        Close();
        return true;

        bool MatchesFreshScope() => fresh.SystemId == stage.Frozen.Plan.SystemId
            && fresh.SourceFacts?.ScopedCollection is { Complete: true } collection
            && collection.Scope.SystemId == stage.Frozen.Plan.SystemId
            && collection.Scope.OperationId == response.Plan.OperationId;

        void Close()
        {
            if (session.OutcomeUnknown) session.ClearOutcomeUnknown(response.Plan.OperationId, response.Plan.PlanHash);
            stages.Remove(response.Plan.OperationId);
            session.SetRealResultSynchronizationPending(false);
        }
    }

    /// <summary>Production recovery ordering: complete scoped facts, map owned draft, notify views,
    /// then release the exact global identity. No Accept or write callback is available here.</summary>
    public async Task<bool> RecoverAsync(AgentRealOperationResponse status, Func<Task<bool>> refresh,
        Func<StorageSystemDocument> document, Func<AgentRealOperationResponse, bool, bool> notify,
        Action<AgentRealOperationResponse> release, CancellationToken cancellationToken)
    {
        if (!RealOperationSubmissionState.IsReconciledTerminal(status)) return false;
        var refreshed = await refresh();
        cancellationToken.ThrowIfCancellationRequested();
        // A failed background refresh must retain the owned stage, including
        // a no-call terminal. Otherwise the retry loses its draft/baseline owner.
        if (!refreshed) return false;
        if (!Reconcile(status, document(), true)) return false;
        if (!notify(status, true)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        release(status);
        return true;
    }

    private static string RequireVerifiedCreatedId(AgentRealOperationResponse response, StorageSystemDocument fresh, OutputMapping mapping)
    {
        var step = response.Steps.Single(item => item.StepId == mapping.StepId && item.State == RealOperationStepState.Verified);
        using var evidence = JsonDocument.Parse(step.ResultEvidence ?? "{}");
        if (!evidence.RootElement.TryGetProperty("CreatedObjectId", out var value)
            || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("The verified creation step has no exact output identity.");
        var key = value.GetString()!;
        var expectedType = mapping.Kind switch { StorageObjectKind.StoragePool => FactObjectType.StoragePool,
            StorageObjectKind.VirtualDisk => FactObjectType.VirtualDisk, StorageObjectKind.StorageTier => FactObjectType.StorageTier,
            _ => throw new InvalidDataException("This creation output mapping kind is not supported.") };
        if (fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == key) is not { HasReliableIdentity: true } source
            || source.ObjectType != expectedType)
            throw new InvalidDataException("The verified output is absent from the fresh related inventory.");
        var exists = mapping.Kind switch
        {
            StorageObjectKind.StoragePool => fresh.Snapshot.StoragePools.Any(item => item.StableId == key),
            StorageObjectKind.VirtualDisk => fresh.Snapshot.VirtualDisks.Any(item => item.StableId == key && item.PoolStableId == mapping.PoolId),
            StorageObjectKind.StorageTier => fresh.Snapshot.StorageTiers.Any(item => item.StableId == key && item.PoolStableId == mapping.PoolId
                && item.MediaType == mapping.MediaType && item.VirtualDiskStableId is null),
            _ => false
        };
        if (!exists) throw new InvalidDataException("The verified output has the wrong kind or parent.");
        if (mapping.Kind == StorageObjectKind.VirtualDisk)
        {
            if (!evidence.RootElement.TryGetProperty("CreatedOsDiskId", out var osValue)
                || osValue.ValueKind != JsonValueKind.String || osValue.GetString() is not { Length: > 0 } osId
                || fresh.SourceFacts!.Objects.SingleOrDefault(item => item.Id == osId) is not { HasReliableIdentity: true }
                || fresh.Snapshot.OsDisks.Count(item => item.VirtualDiskStableId == key) != 1
                || !fresh.Snapshot.OsDisks.Any(item => item.StableId == osId && item.VirtualDiskStableId == key))
                throw new InvalidDataException("The verified VD has no unique reliable created OS disk in fresh facts.");
        }
        else if (mapping.Kind == StorageObjectKind.StoragePool)
        {
            var members = response.Plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
            var pool = fresh.Snapshot.StoragePools.Single(item => item.StableId == key);
            if (pool.IsPrimordial || members.Length == 0 || members.Select(item => item.ProviderKey).Distinct(StringComparer.Ordinal).Count() != members.Length
                || pool.MemberPhysicalDiskIds.Count != members.Length
                || !pool.MemberPhysicalDiskIds.ToHashSet(StringComparer.Ordinal).SetEquals(members.Select(item => item.ProviderKey))
                || members.Any(member => fresh.SourceFacts!.Objects.SingleOrDefault(item => item.Id == member.ProviderKey)
                    is not { HasReliableIdentity: true, ObjectType: FactObjectType.PhysicalDisk }
                    || !fresh.Snapshot.PhysicalDisks.Any(item => item.StableId == member.ProviderKey && item.PoolStableId == key)))
                throw new InvalidDataException("The verified pool belongs to a different complete physical member set.");
        }
        return key;
    }

    private static void RemapCreatedPool(SimulationEditingSession session, string temporaryId, string realId)
    {
        session.Working = session.Working with
        {
            StoragePools = session.Working.StoragePools.Where(pool => pool.StableId != temporaryId).ToArray(),
            StorageTiers = session.Working.StorageTiers.Where(tier => tier.PoolStableId != temporaryId).ToArray(),
            VirtualDisks = session.Working.VirtualDisks.Select(vd => vd.PoolStableId == temporaryId ? vd with { PoolStableId = realId } : vd).ToArray(),
            PhysicalDisks = session.Working.PhysicalDisks.Select(disk => disk.PoolStableId == temporaryId ? disk with { PoolStableId = realId } : disk).ToArray()
        };
        if (session.PoolIntents.Remove(temporaryId, out var intent)) session.PoolIntents[realId] = intent;
        foreach (var key in session.MaximumSizeFields.Where(key => key.StartsWith(temporaryId + "|", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            session.MaximumSizeFields.Remove(key);
            session.MaximumSizeFields.Add(realId + key[temporaryId.Length..]);
        }
    }

    private static void RemapCreatedVirtualDisk(SimulationEditingSession session, string poolId, string realId)
    {
        var temporaryVds = session.Working.VirtualDisks.Where(vd => vd.PoolStableId == poolId
            && EditWorkspace.IsDraftVirtualDisk(vd.StableId)).Select(vd => vd.StableId).ToHashSet();
        var temporaryDisks = session.Working.OsDisks.Where(disk => temporaryVds.Contains(disk.VirtualDiskStableId ?? ""))
            .Select(disk => disk.StableId).ToHashSet();
        var temporaryPartitions = session.Working.Partitions.Where(partition => temporaryDisks.Contains(partition.OsDiskStableId ?? ""))
            .Select(partition => partition.StableId).ToHashSet();
        session.Working = session.Working with
        {
            VirtualDisks = session.Working.VirtualDisks.Where(vd => !temporaryVds.Contains(vd.StableId)).ToArray(),
            OsDisks = session.Working.OsDisks.Where(disk => !temporaryDisks.Contains(disk.StableId)).ToArray(),
            Partitions = session.Working.Partitions.Where(partition => !temporaryPartitions.Contains(partition.StableId)).ToArray(),
            Volumes = session.Working.Volumes.Where(volume => !temporaryPartitions.Contains(volume.PartitionStableId ?? "")).ToArray()
        };
    }

    private static void RebaseRemainingDraft(SimulationEditingSession session, StorageSystemDocument fresh)
    {
        var old = session.Baseline;
        var target = session.Working;
        var actual = fresh.Snapshot;
        var desiredUsages = session.PoolIntents.Values.SelectMany(intent => intent.MemberUsages ?? [])
            .GroupBy(item => item.PhysicalDiskId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                group => group.Select(item => PhysicalDiskUsage.Normalize(item.Usage)).Distinct(StringComparer.Ordinal).Single(), StringComparer.OrdinalIgnoreCase);
        static T[] Merge<T>(IReadOnlyList<T> baseline, IReadOnlyList<T> desired, IReadOnlyList<T> live, Func<T, string> id)
        {
            var oldIds = baseline.Select(id).ToHashSet();
            var desiredIds = desired.Select(id).ToHashSet();
            var liveIds = live.Select(id).ToHashSet();
            return live.Where(item => !oldIds.Contains(id(item)) || desiredIds.Contains(id(item)))
                .Concat(desired.Where(item => !oldIds.Contains(id(item)) && !liveIds.Contains(id(item)))).ToArray();
        }
        var remaining = target with
        {
            SnapshotVersion = actual.SnapshotVersion, ScannedAt = actual.ScannedAt,
            PhysicalDisks = actual.PhysicalDisks.Select(live =>
            {
                var desired = target.PhysicalDisks.SingleOrDefault(item => item.StableId == live.StableId);
                var prior = old.PhysicalDisks.SingleOrDefault(item => item.StableId == live.StableId);
                var poolId = desired is not null && prior is not null && desired.PoolStableId != prior.PoolStableId ? desired.PoolStableId : live.PoolStableId;
                var usage = desiredUsages.TryGetValue(live.StableId, out var requestedUsage) ? requestedUsage
                    : desired is not null && prior is not null && desired.Usage != prior.Usage ? desired.Usage : live.Usage;
                return live with { PoolStableId = poolId, Usage = usage };
            }).ToArray(),
            StoragePools = Merge(old.StoragePools, target.StoragePools, actual.StoragePools, item => item.StableId),
            StorageTiers = Merge(old.StorageTiers, target.StorageTiers, actual.StorageTiers, item => item.StableId),
            VirtualDisks = Merge(old.VirtualDisks, target.VirtualDisks, actual.VirtualDisks, item => item.StableId),
            OsDisks = Merge(old.OsDisks, target.OsDisks, actual.OsDisks, item => item.StableId),
            Partitions = Merge(old.Partitions, target.Partitions, actual.Partitions, item => item.StableId),
            Volumes = Merge(old.Volumes, target.Volumes, actual.Volumes, item => item.StableId)
        };
        session.RebaseVerified(fresh, session.Capture() with { Snapshot = remaining });
    }
}
