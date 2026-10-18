using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using static WinPool.Infrastructure.Windows.WindowsStorageCommandTarget;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealStorageBackend
{
    private static HashSet<string> VerifiedRemovedTargets(OperationPlan plan, string stepId, IReadOnlyDictionary<string, string> outputs) =>
        plan.RealOperation!.Steps.TakeWhile(item => item.Id != stepId).Where(item => outputs.ContainsKey(item.Id))
            .Select(item => item.Command switch { DeletePoolCommand value => value.Pool.Existing?.ProviderKey,
                DeleteTierCommand value => value.Tier.Existing?.ProviderKey, DeleteVirtualDiskCommand value => value.VirtualDisk.Existing?.ProviderKey,
                DeletePartitionCommand value => value.Partition.Existing?.ProviderKey, _ => null })
            .Where(item => item is not null).Select(item => item!).ToHashSet(StringComparer.Ordinal);

    private async Task<RealStepPreflight> PreflightExactMembersAsync(OperationPlan plan, RealOperationStep step,
        WindowsRealStorageTopology topology, IReadOnlyDictionary<string, string> outputs, CancellationToken ct)
    {
        var closure = WindowsRealOperationPlanner.ResolveExactMemberSet(topology, plan.Targets, VerifiedRemovedTargets(plan, step.Id, outputs));
        var index = plan.RealOperation!.Steps.ToList().FindIndex(item => item.Id == step.Id);
        var expected = plan.RealOperation.TargetFingerprint;
        if (index > 0)
        {
            if (!outputs.TryGetValue(plan.RealOperation.Steps[index - 1].Id, out var json)) throw new InvalidDataException("Previous exact step is not verified.");
            var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(json) ?? throw new InvalidDataException("Previous exact evidence is unreadable.");
            if (evidence.PhysicalMemberFingerprint != closure.PhysicalMemberFingerprint) throw new InvalidDataException("Previous physical member identities changed.");
            expected = evidence.PostFingerprint;
        }
        RequireMultiMaximumIdentity(plan, topology, closure, expected);
        var request = new RealOperationIntentRequest(plan.Intent, plan.SystemId, plan.Targets, plan.RealOperation.Steps, plan.RealOperation.ExpectedFinalState);
        WindowsRealStorageSafetyEvidence? safety = null;
        _ = await planner.ValidateExactMemberStepAsync(topology, closure, request, step, ct, outputs,
            onSafetyVerified: value => safety = value).ConfigureAwait(false);
        var target = WindowsRealOperationPlanner.BuildExactMemberTarget(topology, closure, step.Command, outputs) with
        { CreatedInThisPlan = WindowsRealStorageTargetBuilder.GetReference(step.Command).CreatedByStep is not null };
        operationScopes[plan.OperationId] = StorageInventoryScopeFactory.Create(topology.Facts, plan.OperationId, step.Id,
            plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray());
        return new(JsonSerializer.Serialize(target), closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint)
        { CallContext = new CallContext(plan.OperationId, step.Id, topology, target, safety) };
    }

    private static RealExactPhysicalMemberSetClosure ExactClosureFromTarget(WindowsRealStorageTopology topology, WindowsStorageCommandTarget target)
    {
        if (target.PhysicalMembers is not { Count: > 0 } members) throw new InvalidDataException("Exact member evidence is absent.");
        var ids = members.Select(item => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, item.StableId)).ToArray();
        return WindowsRealOperationPlanner.ResolveExactMemberSet(topology, ids);
    }

    private async Task<RealStepResult> ExecuteExactMembersAsync(OperationPlan plan, RealOperationStep step, RealStepPreflight preflight,
        WindowsStorageCommandTarget target, CancellationToken ct)
    {
        WindowsRealStorageTopology before;
        RealExactPhysicalMemberSetClosure closure;
        WindowsRealStorageSafetyEvidence? safetyEvidence;
        try
        {
            var context = preflight.CallContext as CallContext;
            if (context is null || context.OperationId != plan.OperationId || context.StepId != step.Id)
                throw new InvalidDataException("The exact-member call context is unavailable.");
            before = context.Before;
            closure = ExactClosureFromTarget(before, target);
            RequireMultiMaximumIdentity(plan, before, closure, preflight.TargetFingerprint);
            if (target.ExpectedFingerprint != closure.Fingerprint || preflight.PhysicalMemberFingerprint != closure.PhysicalMemberFingerprint)
                throw new InvalidDataException("Exact pre-call component changed.");
            safetyEvidence = context.Safety;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(RealStepOutcome.FailedWithoutEffect, "real.exact_pre_call_unverified", JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(true,
            "real.exact_pre_call_unverified", plan.RealOperation!.PhysicalMemberFingerprint, BoundedDiagnostic(ex.Message))
            { PlanHash = plan.PlanHash, StepId = step.Id })); }
        WindowsStorageCommandResult provider;
        try
        {
            provider = await StorageOperationTiming.MeasureAsync("real.provider", () => adapter.ExecuteAsync(step.Command,
                target, ((CallContext)preflight.CallContext!).WriteLease, CancellationToken.None), plan.OperationId, step.Id, "exact-members")
                .ConfigureAwait(false);
            provider = provider with { NativeMsrSafetyEvidence = safetyEvidence?.NativePartitionAttributes,
                VolumeSafetyEvidence = safetyEvidence?.VolumeSafetyEvidence, OfflinePartitionAttributes = safetyEvidence?.OfflinePartitionAttributes };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(RealStepOutcome.OutcomeUnknown, "real.provider_return_unknown", JsonSerializer.Serialize(new { Error = ex.Message })); }
        finally { await preflight.CallContext!.DisposeAsync().ConfigureAwait(false); }
        if (!provider.ProviderReturned) return new(RealStepOutcome.FailedWithoutEffect, provider.Code,
            JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(true, provider.Code, closure.PhysicalMemberFingerprint, provider.ProviderError)
            { PlanHash = plan.PlanHash, StepId = step.Id }));
        if (provider.Code != "provider.returned") return new(RealStepOutcome.OutcomeUnknown, "real.provider_reported_uncertain_result", JsonSerializer.Serialize(provider));
        var deadline = timeProvider.GetUtcNow().Add(PostCallWindow);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observation.CancelAfter(PostCallWindow);
        var observationToken = observation.Token;
        var probe = 0;
        var fullFallbackUsed = false;
        var fullNext = false;
        string? diagnostic = null;
        do
        {
            try
            {
                var useFull = fullNext;
                fullNext = false;
                var after = await CaptureWithinWindowAsync(plan, step.Id + ":exact-after", deadline, observationToken, useFull).ConfigureAwait(false);
                var observed = ExactClosureFromTarget(after, target);
                RequireMultiMaximumIdentity(plan, after, observed, observed.Fingerprint);
                var verified = VerifyAfter(step.Command, target, provider, before, after);
                if (verified is not null)
                {
                    var observedSafety = step.Command is SetDiskOnlineCommand diskState
                        ? await planner.ValidateObservedExactMemberSafetyAsync(after, observed, observationToken, diskState).ConfigureAwait(false)
                        : await planner.ValidateObservedExactMemberSafetyAsync(after, observed, observationToken).ConfigureAwait(false);
                    if (step.Command is SetDiskOnlineCommand)
                        RequireOfflinePartitionProof(after, observed.AsComponentClosure(), observedSafety);
                    var evidence = new WindowsVerifiedStepEvidence(observed.Fingerprint, observed.PhysicalMemberFingerprint,
                        verified.CreatedObjectId, provider.Code, verified.CreatedOsDiskId, verified.TieredCreation,
                        provider.LiveCapabilityEvidence, verified.GptInitialization, observedSafety?.NativePartitionAttributes ?? provider.NativeMsrSafetyEvidence,
                        observedSafety?.VolumeSafetyEvidence ?? provider.VolumeSafetyEvidence, observedSafety?.OfflinePartitionAttributes ?? provider.OfflinePartitionAttributes,
                        TieredCreationInput: provider.TieredCreationInput);
                    return new(RealStepOutcome.Verified, "real.step_verified", JsonSerializer.Serialize(evidence), evidence.CreatedObjectId)
                    { VerifiedPostState = PostState(after) };
                }
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostic = ex.Message;
                if (!fullFallbackUsed && !fullNext && topologyReader.SupportsScopedCapture)
                { fullNext = true; fullFallbackUsed = true; }
            }
            if (timeProvider.GetUtcNow() >= deadline) break;
            if (!await DelayObservationProbeAsync(probe++, deadline, observationToken).ConfigureAwait(false)) break;
        } while (true);
        return new(RealStepOutcome.OutcomeUnknown, "real.postcondition_unverified", JsonSerializer.Serialize(new { Provider = provider, Diagnostic = diagnostic }));
    }

    private async Task<RealReconciliationResult> ReconcileExactMembersAsync(OperationPlan plan, IReadOnlyList<RealOperationStepProgress> progress, CancellationToken ct)
    {
        try
        {
            if (plan.RealOperation is not { } frozen || progress.Count != frozen.Steps.Count
                || !progress.Select(item => item.StepId).SequenceEqual(frozen.Steps.Select(item => item.Id), StringComparer.Ordinal)
                || plan.PlanHash != OperationPlanHasher.Compute(plan)) return Unknown(progress, "real.reconciliation_invalid_record");
            var topology = await CaptureOperationTopologyAsync(plan, "exact-reconcile", ct).ConfigureAwait(false);
            var expected = plan.RealOperation!.TargetFingerprint;
            var prefixLength = 0;
            foreach (var item in progress)
            {
                if (item.State != RealOperationStepState.Verified) break;
                var evidence = item.ResultEvidence is null ? null : JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(item.ResultEvidence);
                var target = item.TargetEvidence is null ? null : JsonSerializer.Deserialize<WindowsStorageCommandTarget>(item.TargetEvidence);
                if (evidence is null || evidence.ProviderCode != "provider.returned" || string.IsNullOrEmpty(evidence.PostFingerprint)
                    || evidence.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
                    || target is null || target.ExpectedFingerprint != expected)
                    return Unknown(progress, "real.reconciliation_step_evidence_invalid");
                expected = evidence.PostFingerprint;
                prefixLength++;
            }
            if (progress.Skip(prefixLength).Any(item => item.State == RealOperationStepState.Verified))
                return Unknown(progress, "real.reconciliation_step_order_unknown");
            var outputs = progress.Where(item => item.State == RealOperationStepState.Verified && item.ResultEvidence is not null)
                .ToDictionary(item => item.StepId, item => item.ResultEvidence!, StringComparer.Ordinal);
            var closure = WindowsRealOperationPlanner.ResolveExactMemberSet(topology, plan.Targets,
                VerifiedRemovedTargets(plan, "", outputs));
            var last = progress.LastOrDefault(item => item.State == RealOperationStepState.Verified);
            RequireMultiMaximumIdentity(plan, topology, closure, expected);
            // No issued or uncertain call is ever replayed. Only a completely verified durable prefix can finish here.
            if (progress.All(item => item.State == RealOperationStepState.Verified))
            {
                var diskState = plan.RealOperation!.Steps.SingleOrDefault(step => step.Id == last?.StepId)?.Command as SetDiskOnlineCommand;
                var observedSafety = await planner.ValidateObservedExactMemberSafetyAsync(topology, closure, ct, diskState).ConfigureAwait(false);
                if (diskState is not null) RequireOfflinePartitionProof(topology, closure.AsComponentClosure(), observedSafety);
                return new(RealOperationState.Succeeded, progress, "real.reconciliation_verified", true);
            }
            var failed = progress[prefixLength];
            var frozenStep = plan.RealOperation.Steps[prefixLength];
            if (failed.State is not (RealOperationStepState.Failed or RealOperationStepState.Verifying or RealOperationStepState.OutcomeUnknown)
                || failed.ResultEvidence is null
                || progress.Skip(prefixLength + 1).Any(item => item.State is not (RealOperationStepState.Pending or RealOperationStepState.StoppedBeforeCall)))
                return Unknown(progress, "real.reconciliation_exact_step_outcome_unknown");
            // The Agent records this Failed receipt directly from NotStarted.
            // SQLite requires and retains target_json at PreparingCall/CallIssued,
            // so an absent target also excludes any earlier issued call. Adapter
            // no-call receipts still require their full exact target below.
            var agentPreflightFailed = failed is { State: RealOperationStepState.Failed,
                    Code: "operation.preflight_failed", TargetEvidence: null }
                && IsProvenNoEffect(failed, closure.PhysicalMemberFingerprint, out var preflightReceipt)
                && preflightReceipt is not null && preflightReceipt.Code == failed.Code
                && NoCallBindingMatches(preflightReceipt, plan, frozenStep);
            if (!agentPreflightFailed)
            {
                if (failed.TargetEvidence is null) return Unknown(progress, "real.reconciliation_exact_step_outcome_unknown");
                var noCall = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(failed.ResultEvidence);
                if (noCall is not { NoWindowsCall: true } || noCall.PhysicalMemberFingerprint != closure.PhysicalMemberFingerprint
                    || noCall.Code is not ("adapter.closed-command-or-target-required" or "adapter.maximum-requires-journaled-explicit-attempt"
                        or "adapter.preflight-rejected" or "real.exact_pre_call_unverified")
                    || !NoCallBindingMatches(noCall, plan, frozenStep))
                    return Unknown(progress, "real.reconciliation_no_call_evidence_invalid");
                var recorded = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(failed.TargetEvidence);
                var current = WindowsRealOperationPlanner.BuildExactMemberTarget(topology, closure, frozenStep.Command, outputs) with
                    { CreatedInThisPlan = WindowsRealStorageTargetBuilder.GetReference(frozenStep.Command).CreatedByStep is not null };
                // The historical closed-target rejection was caused by this absent
                // parent identity. It proves no call; every other frozen target fact
                // must still match the fresh exact command target before release.
                if (recorded is { ParentUniqueId.Length: 0 } && frozenStep.Command is SetPhysicalDiskUsageCommand
                    && noCall.Code == "adapter.closed-command-or-target-required" && noCall.PlanHash is null)
                    recorded = recorded with { ParentUniqueId = current.ParentUniqueId };
                if (recorded is null || !SameStorageTarget(recorded, current))
                    return Unknown(progress, "real.reconciliation_no_call_target_invalid");
                _ = await planner.ValidateMultiMaximumSafetyAsync(topology, closure, frozenStep.Command, ct).ConfigureAwait(false);
            }
            var reconciled = progress.Select((item, index) => index < prefixLength ? item : index == prefixLength
                ? item with { State = RealOperationStepState.Failed, Code = "real.reconciliation_proven_no_call" }
                : item with { State = RealOperationStepState.StoppedBeforeCall, Code = "real.reconciliation_never_started" }).ToArray();
            return new(prefixLength == 0 ? RealOperationState.Failed : RealOperationState.PartiallyCompleted,
                reconciled, "real.reconciliation_proven_no_call", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Unknown(progress, "real.reconciliation_exact_component_unverified"); }
    }

    internal static bool SourceLayoutMatches(WinPoolSourceObject source, StorageLayoutParameters layout, bool aggregate = false)
    {
        bool Match(string name, long expected, bool neutral = false)
        {
            var field = source.Field(name);
            if (aggregate && field is { ReadState: FieldReadState.Returned, Value: null }) return true;
            if (!aggregate && layout.ResiliencySettingName == "Simple" && neutral && (field is null || field.ReadState is FieldReadState.Unavailable
                || field.ReadState == FieldReadState.Returned && (field.Value is null || field.Value.Value.ValueKind == JsonValueKind.Null))) return true;
            return field is { ReadState: FieldReadState.Returned, Value: { } value } &&
                (aggregate && value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number == expected);
        }
        var resiliency = source.Field("ResiliencySettingName");
        return (aggregate && resiliency is { ReadState: FieldReadState.Returned, Value: null }
            || resiliency is { ReadState: FieldReadState.Returned, Value: { } value }
                && (aggregate && value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString() == layout.ResiliencySettingName))
            && Match("NumberOfColumns", layout.NumberOfColumns) && Match("Interleave", layout.InterleaveBytes)
            && Match("NumberOfDataCopies", layout.NumberOfDataCopies, true) && Match("PhysicalDiskRedundancy", layout.PhysicalDiskRedundancy, true);
    }

    private static bool RequestedTierLayoutMatches(StorageTierInfo tier, StorageLayoutParameters layout) =>
        tier.ResiliencySettingName == layout.ResiliencySettingName && tier.NumberOfColumns == layout.NumberOfColumns && tier.Interleave == layout.InterleaveBytes
        && (tier.NumberOfDataCopies ?? (layout.ResiliencySettingName == "Simple" ? 1 : -1)) == layout.NumberOfDataCopies
        && (tier.PhysicalDiskRedundancy ?? (layout.ResiliencySettingName == "Simple" ? 0 : -1)) == layout.PhysicalDiskRedundancy;

    private static bool FootprintMatchesLayout(long logical, long physical, StorageLayoutParameters layout)
    {
        if (logical <= 0 || physical <= 0) return false;
        decimal minimum = layout.ResiliencySettingName switch {
            "Mirror" => (decimal)logical * layout.NumberOfDataCopies,
            "Parity" when layout.NumberOfColumns > layout.PhysicalDiskRedundancy =>
                (decimal)logical * layout.NumberOfColumns / (layout.NumberOfColumns - layout.PhysicalDiskRedundancy),
            "Simple" => logical, _ => decimal.MaxValue };
        return physical >= minimum;
    }

    private static VerifiedPostcondition? VerifyExactMemberAfter(RealStorageCommand command, WindowsStorageCommandTarget target,
        WindowsStorageCommandResult result, WindowsRealStorageTopology before, WindowsRealStorageTopology after)
    {
        var snapshot = after.Snapshot;
        var exact = FindExact(after, target.Kind, target.UniqueId, target.ObjectId, target.PartitionGuid);
        var members = target.PhysicalMembers!;
        switch (command)
        {
            case CreatePoolCommand poolCommand:
            {
                var source = FindExact(after, StorageObjectKind.StoragePool, result.UniqueId ?? "", result.ObjectId ?? "", "");
                var pool = source is null ? null : snapshot.StoragePools.SingleOrDefault(item => item.StableId == source.Id);
                return pool is not null && !pool.IsPrimordial && pool.FriendlyName == poolCommand.Name
                    && SameStringSet(pool.MemberPhysicalDiskIds, members.Select(item => item.StableId))
                    && !snapshot.VirtualDisks.Any(item => item.PoolStableId == pool.StableId) && !snapshot.StorageTiers.Any(item => item.PoolStableId == pool.StableId) ? new(source!.Id) : null;
            }
            case SetPhysicalDiskUsageCommand usage:
                return exact is not null && PhysicalDiskUsage.Normalize(snapshot.PhysicalDisks.Single(item => item.StableId == exact.Id).Usage)
                    == PhysicalDiskUsage.Normalize(usage.Usage) ? new() : null;
            case DeletePoolCommand:
            {
                var old = FindExact(before, target.Kind, target.UniqueId, target.ObjectId, "");
                return old is not null && exact is null && !snapshot.VirtualDisks.Any(item => item.PoolStableId == old.Id)
                    && !snapshot.StorageTiers.Any(item => item.PoolStableId == old.Id)
                    && members.All(member => snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == member.StableId) is { } disk
                        && disk.PoolStableId != old.Id && !snapshot.StoragePools.Any(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Contains(member.StableId))) ? new() : null;
            }
            case CreateTierCommand tierCommand:
            {
                var source = FindExact(after, StorageObjectKind.StorageTier, result.UniqueId ?? "", result.ObjectId ?? "", "");
                var tier = source is null ? null : snapshot.StorageTiers.SingleOrDefault(item => item.StableId == source.Id);
                return exact is not null && tier is not null && tier.PoolStableId == exact.Id && tier.FriendlyName == tierCommand.Name
                    && tier.VirtualDiskStableId is null && tier.Size == 0 && tier.FootprintOnPool == 0 && tier.MediaType == tierCommand.EffectiveMediaType
                    && RequestedTierLayoutMatches(tier, tierCommand.EffectiveLayout) && SourceLayoutMatches(source!, tierCommand.EffectiveLayout) ? new(source!.Id) : null;
            }
            case CreateVirtualDiskCommand vdCommand:
            {
                var source = FindExact(after, StorageObjectKind.VirtualDisk, result.UniqueId ?? "", result.ObjectId ?? "", "");
                var vd = source is null ? null : snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == source.Id);
                var disks = vd is null ? [] : snapshot.OsDisks.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
                return exact is not null && vd is not null && vd.PoolStableId == exact.Id && vd.FriendlyName == vdCommand.Name && vd.Size == vdCommand.SizeBytes
                    && vd.TierStableIds.Count == 0 && vd.ProvisioningType == "Fixed" && SourceLayoutMatches(source!, vdCommand.EffectiveLayout)
                    && FootprintMatchesLayout(vd.Size, vd.FootprintOnPool, vdCommand.EffectiveLayout)
                    && disks.Length == 1 && disks[0].Size == vd.Size && disks[0].PartitionStyle == "RAW"
                    && !snapshot.Partitions.Any(item => item.OsDiskStableId == disks[0].StableId) ? new(vd.StableId, disks[0].StableId) : null;
            }
            case CreateTieredVirtualDiskCommand tiered:
            {
                var closure = ExactClosureFromTarget(after, target);
                var inputs = target.TierInputs;
                if (inputs is null || inputs.Count == 0) return null;
                var sizes = inputs.ToDictionary(item => item.TemplateStableId, item => item.SizeBytes, StringComparer.Ordinal);
                var normalized = tiered with { CapacityTiers = inputs.Select(item => new MaximumCapacityTier(
                    RealTargetReference.ForExisting(new(after.SystemId, StorageObjectKind.StorageTier, item.TemplateStableId)), null, item.SizeBytes, false, item.Layout, item.MediaType)).ToArray() };
                var match = MatchMultiMaximumActual(after, closure, normalized, sizes, new Dictionary<string,string>(), result, null, null);
                return match is { } actual ? new(actual.Vd, actual.Os) : null;
            }
            default: return null;
        }
    }
}
