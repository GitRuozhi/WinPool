using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealStorageBackend
{
    private sealed record MaximumAttemptCandidate(int Ordinal, string SearchTargetKey, MaximumCapacityAttemptPhase Phase,
        long CandidateBytes, long LastSuccessfulBytes, string ExpectedFingerprint, string? VirtualDiskId,
        string? OsDiskId, string? ActualTierId, WindowsRealStorageTopology? LastVerifiedTopology,
        CreateTieredVirtualDiskCommand? MultiMacro = null, IReadOnlyDictionary<string, long>? RequestedTierSizes = null,
        IReadOnlyDictionary<string, string>? PriorActualTierIds = null);

    private sealed record MaximumAttemptState(WindowsRealStorageTopology Topology, RealTargetClosure Closure,
        RealExactPhysicalMemberSetClosure? Members);
    private sealed record MaximumAttemptCall(MaximumAttemptState Before, RealStorageCommand Command,
        WindowsStorageCommandTarget Target);
    private sealed record MaximumAttemptReceipt(WindowsVerifiedStepEvidence? Verified, string Code, string EvidenceJson,
        WindowsRealStorageTopology? ObservedTopology, string? PostFingerprint,
        IReadOnlyDictionary<string, string>? ActualTierIds = null);
    private sealed record MaximumAttemptExecution(MaximumAttemptReceipt? Receipt, string? FailureCode = null,
        string? FailureEvidenceJson = null);
    private sealed record MaximumMapping(WindowsVerifiedStepEvidence? Verified,
        IReadOnlyDictionary<string, string>? ActualTierIds, string? FailedCheck);

    // All MAX schedulers use this one write boundary. Candidate selection and
    // per-tier order stay with their scheduler; the journal owns idempotency.
    private async Task<MaximumAttemptExecution> ExecuteMaximumAttemptAsync(OperationPlan plan, RealOperationStep step,
        RealStepPreflight preflight, MaximumAttemptCandidate candidate, IMaximumCapacityAttemptJournal journal,
        CancellationToken cancellationToken)
    {
        MaximumAttemptExecution Failed(string code, object detail) => new(null, code, JsonSerializer.Serialize(detail));
        if (journal.IsStopRequested || cancellationToken.IsCancellationRequested)
            return Failed("real.maximum.stopped_before_next_attempt", new { candidate.Ordinal, candidate.SearchTargetKey });
        MaximumAttemptCall call;
        try
        {
            var validated = candidate.Ordinal == 1 && preflight.CallContext is CallContext context
                && context.OperationId == plan.OperationId && context.StepId == step.Id ? context : null;
            var before = validated?.Before ?? await CaptureOperationTopologyAsync(plan,
                step.Id + ":max-before:" + candidate.Ordinal, cancellationToken).ConfigureAwait(false);
            var state = ResolveMaximumAttemptState(plan, before, candidate.MultiMacro);
            RequireMaximumInitialIdentity(plan, before, state.Closure, candidate.ExpectedFingerprint);
            RequireMaximumRawComponent(before, state.Closure);
            call = BuildMaximumAttemptCall(step, candidate, state);
            if (validated is null)
                await ValidateMaximumAttemptSafetyAsync(plan, state, call.Command, cancellationToken).ConfigureAwait(false);
            else if (state.Members is null)
                RequirePoolMemberRoleProof(before, state.Closure, validated.Safety);
            if (candidate.MultiMacro is null && candidate.VirtualDiskId is { } vdId
                && (candidate.LastVerifiedTopology is null || !MaximumResizeBeforeMatches(
                    candidate.LastVerifiedTopology, before, vdId, candidate.ActualTierId)))
                throw new InvalidDataException("Last verified MAX mapping changed before the next attempt.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failed("real.maximum.fresh_preflight_failed", new { Error = exception.ToString(), candidate.Ordinal, candidate.SearchTargetKey });
        }
        var attempt = new MaximumCapacityAttempt(candidate.Ordinal, candidate.SearchTargetKey, candidate.Phase,
            candidate.CandidateBytes, candidate.LastSuccessfulBytes, JsonSerializer.Serialize(call.Target),
            call.Before.Closure.Fingerprint, call.Before.Closure.PhysicalMemberFingerprint,
            MaximumBeforeEvidence(plan, call.Before, candidate.MultiMacro is not null));
        if (!await journal.PrepareAsync(attempt, CancellationToken.None).ConfigureAwait(false))
            return Failed("real.maximum.prepare_cas_failed", attempt);
        if (journal.IsStopRequested || cancellationToken.IsCancellationRequested)
        {
            await journal.CompleteAsync(candidate.Ordinal, new(MaximumCapacityAttemptState.FailedWithoutCall,
                candidate.LastSuccessfulBytes, "real.maximum.stopped_before_call",
                JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(true, "real.maximum.stopped_before_call",
                    call.Before.Closure.PhysicalMemberFingerprint))), CancellationToken.None).ConfigureAwait(false);
            return Failed("real.maximum.stopped_before_call", attempt);
        }
        if (!await journal.MarkCallIssuedAsync(candidate.Ordinal, CancellationToken.None).ConfigureAwait(false))
            return Failed("real.maximum.call_boundary_cas_failed", attempt);
        WindowsStorageCommandResult provider;
        try { provider = await adapter.ExecuteAsync(call.Command, call.Target, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var detail = JsonSerializer.Serialize(new { OriginalException = exception.ToString(), Attempt = attempt });
            await journal.CompleteAsync(candidate.Ordinal, new(MaximumCapacityAttemptState.OutcomeUnknown,
                candidate.LastSuccessfulBytes, "real.maximum.adapter_exception", detail), CancellationToken.None).ConfigureAwait(false);
            return new(null, "real.maximum.adapter_exception", detail);
        }
        MaximumAttemptReceipt receipt;
        try { receipt = await ObserveMaximumAttemptAsync(plan, candidate, call, provider, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var detail = JsonSerializer.Serialize(new { OriginalException = exception.ToString(), Attempt = attempt, Provider = provider });
            await journal.CompleteAsync(candidate.Ordinal, new(MaximumCapacityAttemptState.OutcomeUnknown,
                candidate.LastSuccessfulBytes, "real.maximum.observation_exception", detail), CancellationToken.None).ConfigureAwait(false);
            return new(null, "real.maximum.observation_exception", detail);
        }
        var success = receipt.Verified is not null;
        var stateAfter = success ? MaximumCapacityAttemptState.Verified
            : receipt.Code == "real.maximum.capacity_rejected_unchanged" ? MaximumCapacityAttemptState.CapacityRejectedUnchanged
            : MaximumCapacityAttemptState.OutcomeUnknown;
        var result = new MaximumCapacityAttemptResult(stateAfter,
            success ? candidate.CandidateBytes : candidate.LastSuccessfulBytes, receipt.Code, receipt.EvidenceJson,
            success && candidate.Phase == MaximumCapacityAttemptPhase.Seed
                ? new Dictionary<string, long>(candidate.RequestedTierSizes!, StringComparer.Ordinal) : null);
        return await journal.CompleteAsync(candidate.Ordinal, result, CancellationToken.None).ConfigureAwait(false)
            ? new(receipt) : Failed("real.maximum.result_cas_failed", new { attempt, receipt.Code });
    }

    private static MaximumAttemptCall BuildMaximumAttemptCall(RealOperationStep step,
        MaximumAttemptCandidate candidate, MaximumAttemptState state)
    {
        var before = state.Topology;
        if (candidate.MultiMacro is { } macro)
        {
            var creating = candidate.Phase is MaximumCapacityAttemptPhase.Seed or MaximumCapacityAttemptPhase.Create;
            RealStorageCommand command = creating ? ExplicitTierAttempt(macro, candidate.RequestedTierSizes!)
                : new ResizeTierCommand(RealTargetReference.ForExisting(new(before.SystemId, StorageObjectKind.StorageTier,
                    candidate.ActualTierId!)), candidate.CandidateBytes);
            var target = BuildMultiMaximumTarget(before, state.Members!, creating ? macro.Pool.Existing!.Value
                : new(before.SystemId, StorageObjectKind.StorageTier, candidate.ActualTierId!), candidate.VirtualDiskId) with
            {
                MaximumCapacityAttempt = true,
                Layout = creating ? macro.EffectiveLayout
                    : WindowsRealOperationPlanner.LayoutOf(before.Snapshot.StorageTiers.Single(x => x.StableId == candidate.ActualTierId)),
                TierInputs = macro.CapacityTiers!.Select(item =>
                {
                    var template = before.RequireObject(item.Tier.Existing!.Value);
                    var tier = before.Snapshot.StorageTiers.Single(x => x.StableId == template.Id);
                    return new WindowsStorageTierTarget(template.Id, Text(template, "UniqueId"), Text(template, "ObjectId"),
                        tier.MediaType, candidate.RequestedTierSizes![template.Id],
                        item.Layout ?? WindowsRealOperationPlanner.LayoutOf(tier), false);
                }).ToArray()
            };
            if (creating) target = target with { RelatedUniqueId = target.TierInputs![0].UniqueId, RelatedObjectId = target.TierInputs[0].ObjectId };
            return new(state, command, target);
        }
        var single = BuildMaximumAttemptCommand(step.Command, candidate.CandidateBytes,
            candidate.VirtualDiskId, candidate.ActualTierId, before.SystemId);
        var singleTarget = (state.Members is not null
            ? WindowsRealOperationPlanner.BuildExactMemberTarget(before, state.Members, single, new Dictionary<string, string>())
            : WindowsRealStorageTargetBuilder.Build(before, WindowsRealStorageTargetBuilder.GetReference(single), new Dictionary<string, string>()))
            with { MaximumCapacityAttempt = true, Layout = step.Command is CreateVirtualDiskCommand ordinary ? ordinary.EffectiveLayout : null };
        if (single is CreateTieredVirtualDiskCommand tiered)
        {
            var related = WindowsRealStorageTargetBuilder.Build(before, tiered.Tier, new Dictionary<string, string>());
            singleTarget = singleTarget with { RelatedUniqueId = related.UniqueId, RelatedObjectId = related.ObjectId };
        }
        if (single is ResizeTierCommand && candidate.VirtualDiskId is { } ownerId)
        {
            var owner = before.RequireObject(new(before.SystemId, StorageObjectKind.VirtualDisk, ownerId));
            singleTarget = singleTarget with { RelatedUniqueId = Text(owner, "UniqueId"), RelatedObjectId = Text(owner, "ObjectId") };
        }
        return new(state, single, singleTarget);
    }

    private static MaximumAttemptState ResolveMaximumAttemptState(OperationPlan plan, WindowsRealStorageTopology topology,
        CreateTieredVirtualDiskCommand? multiMacro)
    {
        var members = multiMacro is not null ? WindowsRealOperationPlanner.RequireMultiMaximumClosure(topology, plan.Targets, multiMacro)
            : WindowsRealOperationPlanner.UsesExactMemberSet(plan) ? WindowsRealOperationPlanner.ResolveExactMemberSet(topology, plan.Targets) : null;
        return new(topology, members?.AsComponentClosure() ?? MaximumComponentClosure(plan, topology), members);
    }

    private Task ValidateMaximumAttemptSafetyAsync(OperationPlan plan, MaximumAttemptState state, RealStorageCommand command,
        CancellationToken cancellationToken) => state.Members is not null
        ? planner.ValidateMultiMaximumSafetyAsync(state.Topology, state.Members, command, cancellationToken)
        : ValidateMaximumSafetyAsync(plan, state.Topology, state.Closure, command, cancellationToken);

    private static string MaximumBeforeEvidence(OperationPlan plan, MaximumAttemptState state, bool multi) => multi
        ? JsonSerializer.Serialize(MultiMaximumObservation(state.Topology, state.Members!))
        : JsonSerializer.Serialize(MaximumObservation(state.Topology, state.Closure, WindowsRealOperationPlanner.UsesExactMemberSet(plan)));

    private async Task<MaximumAttemptReceipt> ObserveMaximumAttemptAsync(OperationPlan plan, MaximumAttemptCandidate candidate,
        MaximumAttemptCall call, WindowsStorageCommandResult provider, CancellationToken cancellationToken)
    {
        MaximumAttemptReceipt Receipt(string code, MaximumAttemptState? first = null, MaximumAttemptState? second = null,
            IReadOnlyList<WindowsStorageJobAbsenceEvidence>? jobs = null, MaximumMapping? mapping = null, string? diagnostic = null)
        {
            var observed = mapping?.Verified is not null || code == "real.maximum.capacity_rejected_unchanged"
                ? second ?? first : null;
            var json = candidate.MultiMacro is not null
                ? JsonSerializer.Serialize(new WindowsMaximumCapacityMultiAttemptEvidence(provider,
                    MultiMaximumObservation(call.Before.Topology, call.Before.Members!),
                    first is null ? null : MultiMaximumObservation(first.Topology, first.Members!),
                    second is null ? null : MultiMaximumObservation(second.Topology, second.Members!), jobs ?? [],
                    mapping?.Verified, mapping?.ActualTierIds, code) { ObservationDiagnostic = diagnostic, ObservedTopology = observed?.Topology })
                : JsonSerializer.Serialize(new WindowsMaximumCapacityAttemptEvidence(provider,
                    MaximumObservation(call.Before.Topology, call.Before.Closure, WindowsRealOperationPlanner.UsesExactMemberSet(plan)),
                    first is null ? null : MaximumObservation(first.Topology, first.Closure, WindowsRealOperationPlanner.UsesExactMemberSet(plan)),
                    second is null ? null : MaximumObservation(second.Topology, second.Closure, WindowsRealOperationPlanner.UsesExactMemberSet(plan)),
                    jobs ?? [], mapping?.Verified, code) { ObservationDiagnostic = diagnostic, ObservedTopology = observed?.Topology });
            return new(mapping?.Verified, code, json, observed?.Topology, observed?.Closure.Fingerprint, mapping?.ActualTierIds);
        }
        if (!provider.ProviderReturned) return Receipt("real.maximum.adapter_rejected_without_call");
        if ((call.Command is ResizeVirtualDiskCommand or ResizeTierCommand)
            && ((!string.IsNullOrWhiteSpace(provider.UniqueId) && provider.UniqueId != call.Target.UniqueId)
                || (!string.IsNullOrWhiteSpace(provider.ObjectId) && provider.ObjectId != call.Target.ObjectId)))
            return Receipt("real.maximum.provider_output_identity_changed");
        var knownLayout = candidate.MultiMacro is { } macro
            ? macro.CapacityTiers!.All(item => RequestedTierLayoutMatches(call.Before.Topology.Snapshot.StorageTiers
                .Single(value => value.StableId == item.Tier.Existing!.Value.ProviderKey), item.Layout ?? StorageLayoutParameters.LegacyDefault))
            : MaximumKnownLayout(call.Command, call.Before.Topology, candidate.ActualTierId);
        var capacity = IsExplicitMaximumCapacityRejection(call.Command, provider, knownLayout);
        if (provider.Code != "provider.returned" && !capacity)
            return Receipt("real.maximum.provider_uncertain_or_noncapacity_failure");
        var deadline = timeProvider.GetUtcNow().Add(PostCallWindow);
        MaximumAttemptState? lastFresh = null;
        string? lastFailedCheck = null;
        string? lastCaptureException = null;
        do
        {
            try
            {
                var first = await CaptureWithinWindowAsync(plan, "max-postcondition", deadline, cancellationToken).ConfigureAwait(false);
                var current = ResolveMaximumAttemptState(plan, first, candidate.MultiMacro);
                lastFresh = current;
                if (first.MachineBinding != plan.RealOperation!.MachineBinding
                    || current.Closure.PhysicalMemberFingerprint != call.Before.Closure.PhysicalMemberFingerprint)
                    return Receipt("real.maximum.physical_or_machine_changed", current);
                RequireMaximumRawComponent(first, current.Closure);
                if (capacity)
                {
                    if (current.Closure.Fingerprint != call.Before.Closure.Fingerprint)
                        return Receipt("real.maximum.capacity_error_changed_topology", current);
                    await ValidateMaximumAttemptSafetyAsync(plan, current, call.Command, cancellationToken).ConfigureAwait(false);
                    var jobs1 = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (!MaximumTerminalJobs(jobs1)) return Receipt("real.maximum.storage_job_uncertain", current, jobs: [jobs1]);
                    await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken).ConfigureAwait(false);
                    var secondTopology = await CaptureWithinWindowAsync(plan, "max-capacity-unchanged", deadline, cancellationToken).ConfigureAwait(false);
                    var second = ResolveMaximumAttemptState(plan, secondTopology, candidate.MultiMacro);
                    RequireMaximumInitialIdentity(plan, secondTopology, second.Closure, call.Before.Closure.Fingerprint);
                    RequireMaximumRawComponent(secondTopology, second.Closure);
                    await ValidateMaximumAttemptSafetyAsync(plan, second, call.Command, cancellationToken).ConfigureAwait(false);
                    var jobs2 = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (!MaximumTerminalJobs(jobs2)) return Receipt("real.maximum.storage_job_uncertain", current, second, [jobs1, jobs2]);
                    return Receipt("real.maximum.capacity_rejected_unchanged", current, second, [jobs1, jobs2]);
                }
                var mapping = VerifyMaximumAttemptMapping(candidate, call, provider, current);
                lastFailedCheck = mapping.FailedCheck;
                if (mapping.Verified is not null)
                {
                    await ValidateMaximumAttemptSafetyAsync(plan, current, call.Command, cancellationToken).ConfigureAwait(false);
                    return Receipt("real.maximum.attempt_verified", current, mapping: mapping);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            { lastCaptureException = exception.GetType().FullName + ": " + exception.Message; }
            if (timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(PostCallPoll, timeProvider, cancellationToken).ConfigureAwait(false);
        } while (true);
        return Receipt("real.maximum.postcondition_unverified", lastFresh,
            diagnostic: JsonSerializer.Serialize(new { LastFailedCheck = lastFailedCheck, LastCaptureException = lastCaptureException }));
    }

    private static MaximumMapping VerifyMaximumAttemptMapping(MaximumAttemptCandidate candidate, MaximumAttemptCall call,
        WindowsStorageCommandResult provider, MaximumAttemptState current)
    {
        var before = call.Before.Topology;
        var after = current.Topology;
        if (candidate.MultiMacro is { } macro)
        {
            var mapping = MatchMultiMaximumActual(after, current.Members!, macro, candidate.RequestedTierSizes!,
                candidate.PriorActualTierIds ?? new Dictionary<string, string>(), provider, candidate.VirtualDiskId, candidate.OsDiskId);
            var preserved = MaximumOldFactsPreserved(before, after, call.Before.Closure.Objects,
                new[] { candidate.VirtualDiskId, candidate.OsDiskId, candidate.ActualTierId }
                    .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal));
            return mapping is { } actual && preserved
                ? new(new(current.Closure.Fingerprint, current.Closure.PhysicalMemberFingerprint, actual.Vd, provider.Code, actual.Os), actual.Tiers, null)
                : new(null, null, mapping is null ? "MatchMultiMaximumActual" : "MaximumOldFactsPreserved");
        }
        var verified = VerifyAfter(call.Command, call.Target, provider, before, after);
        var resizeMatches = verified is not null && MaximumResizeAfterMatches(call.Command, before, after,
            candidate.VirtualDiskId ?? verified.CreatedObjectId, candidate.ActualTierId);
        var oldFactsPreserved = verified is not null && MaximumOldFactsPreserved(before, after, call.Before.Closure.Objects,
            new[] { candidate.VirtualDiskId ?? verified.CreatedObjectId, candidate.ActualTierId, verified.CreatedOsDiskId,
                after.Snapshot.OsDisks.SingleOrDefault(item => item.VirtualDiskStableId == (candidate.VirtualDiskId ?? verified.CreatedObjectId))?.StableId }
                .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal));
        if (verified is null || !resizeMatches || !oldFactsPreserved)
            return new(null, null, verified is null ? "VerifyAfter" : !resizeMatches ? "MaximumResizeAfterMatches" : "MaximumOldFactsPreserved");
        var vdId = verified.CreatedObjectId ?? candidate.VirtualDiskId;
        var osId = verified.CreatedOsDiskId ?? after.Snapshot.OsDisks.Single(item => item.VirtualDiskStableId == vdId).StableId;
        var tierMapping = verified.TieredCreation;
        if (tierMapping is null && candidate.ActualTierId is { } tierId)
        {
            var actual = after.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
            var template = after.Snapshot.StorageTiers.Single(item => item.PoolStableId == actual.PoolStableId && item.VirtualDiskStableId is null);
            var vd = after.RequireObject(new(after.SystemId, StorageObjectKind.VirtualDisk, vdId!));
            var tier = after.RequireObject(new(after.SystemId, StorageObjectKind.StorageTier, tierId));
            var templateObject = after.RequireObject(new(after.SystemId, StorageObjectKind.StorageTier, template.StableId));
            tierMapping = new(template.StableId, Text(templateObject, "UniqueId"), Text(templateObject, "ObjectId"),
                vd.Id, Text(vd, "UniqueId"), Text(vd, "ObjectId"), tier.Id, Text(tier, "UniqueId"), Text(tier, "ObjectId"),
                actual.PoolStableId ?? throw new InvalidDataException("Actual tier pool identity missing."), current.Closure.PhysicalDiskId,
                actual.Size, actual.MediaType, actual.ResiliencySettingName, "Fixed", 1, 65536,
                after.Facts.Relationships.Where(edge => !edge.IsRetained).ToArray(), vd.Fields, tier.Fields);
        }
        return new(new(current.Closure.Fingerprint, current.Closure.PhysicalMemberFingerprint, vdId, provider.Code, osId,
            tierMapping, provider.LiveCapabilityEvidence, PoolMemberRoleEvidence: current.Closure.PoolMemberRoleEvidence,
            TieredCreationInput: provider.TieredCreationInput), null, null);
    }

    private static RealStepResult MaximumAttemptUnknown(MaximumAttemptExecution result) =>
        new(RealStepOutcome.OutcomeUnknown, result.FailureCode!, result.FailureEvidenceJson ?? "{}");
    private static RealStepResult MaximumAttemptUnknown(MaximumAttemptReceipt receipt) =>
        new(RealStepOutcome.OutcomeUnknown, receipt.Code, receipt.EvidenceJson);
}
