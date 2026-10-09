using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsMaximumCapacityTierBoundary(string TemplateStableId, string ActualTierId, long ActualSizeBytes,
    int LastVerifiedOrdinal, int CapacityBoundaryOrdinal, string BoundaryCode);
public sealed record WindowsMaximumCapacityMultiSearchEvidence(string AlgorithmVersion, long ActualSizeBytes, int AttemptCount,
    string VirtualDiskId, string OsDiskId, IReadOnlyList<WindowsMaximumCapacityTierBoundary> Tiers);
public sealed record WindowsMaximumCapacityMultiObservation(string InventoryVersion, string MachineBinding, string Fingerprint,
    string PhysicalMemberFingerprint, IReadOnlyList<WinPoolSourceObject> Objects, IReadOnlyList<WinPoolFactRelationship> Associations,
    IReadOnlyList<WindowsExactPhysicalMemberRoleEvidence> Members);
public sealed record WindowsMaximumCapacityMultiAttemptEvidence(WindowsStorageCommandResult Provider,
    WindowsMaximumCapacityMultiObservation Before, WindowsMaximumCapacityMultiObservation? First,
    WindowsMaximumCapacityMultiObservation? Second, IReadOnlyList<WindowsStorageJobAbsenceEvidence> StorageJobQueries,
    WindowsVerifiedStepEvidence? Verified, IReadOnlyDictionary<string, string>? ActualTierIds, string Code)
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? ObservationDiagnostic { get; init; }
}

public sealed partial class WindowsRealStorageBackend
{
    private async Task<RealStepPreflight> PreflightMultiMaximumCapacityAsync(OperationPlan plan, RealOperationStep step,
        WindowsRealStorageTopology topology, CancellationToken ct)
    {
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        var closure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(topology, plan.Targets, command);
        RequireMultiMaximumIdentity(plan, topology, closure, plan.RealOperation!.TargetFingerprint);
        _ = await planner.ValidateMultiMaximumSafetyAsync(topology, closure, command, ct).ConfigureAwait(false);
        RequireMultiMaximumRaw(topology, closure);
        if (topology.Snapshot.VirtualDisks.Any(item => item.PoolStableId == closure.PoolId))
            throw new InvalidDataException("Multi MAX initial pool is no longer empty.");
        foreach (var tier in command.CapacityTiers!)
            if (tier.MaximumCapacity is not { AlgorithmVersion: MaximumCapacityAlgorithm.Version } policy
                || policy.SourceFingerprint != closure.Fingerprint || policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes))
                throw new InvalidDataException("Multi MAX policy differs from frozen exact closure.");
        operationScopes[plan.OperationId] = StorageInventoryScopeFactory.Create(topology.Facts, plan.OperationId, step.Id,
            plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray());
        var target = BuildMultiMaximumTarget(topology, closure, command.Pool.Existing!.Value, null);
        return new(JsonSerializer.Serialize(target), closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint);
    }

    private async Task<RealStepResult> ExecuteMultiMaximumCapacityAsync(OperationPlan plan, RealOperationStep step,
        CreateTieredVirtualDiskCommand macro, RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken ct)
    {
        if ((await journal.ReadAsync(ct).ConfigureAwait(false)).Count != 0)
            return MaximumUnknown("real.maximum.existing_attempts_never_replayed", new { plan.OperationId });
        var tiers = macro.CapacityTiers!;
        var seeds = tiers.ToDictionary(item => item.Tier.Existing!.Value.ProviderKey,
            item => MaximumCapacityAlgorithm.SeedBytes(item.MaximumCapacity!.InitialCandidateBytes), StringComparer.Ordinal);
        var actualIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(seeds, StringComparer.Ordinal);
        var boundaries = new List<WindowsMaximumCapacityTierBoundary>();
        var expected = preflight.TargetFingerprint;
        string? vdId = null, osId = null;
        var ordinal = 0;
        WindowsVerifiedStepEvidence? lastVerified = null;
        var seedTotal = seeds.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        var seed = await AttemptAsync("seed", MaximumCapacityAttemptPhase.Seed, seedTotal, 0, sizes, null).ConfigureAwait(false);
        if (seed is not { Verified: not null, ActualTierIds: not null }) return MaximumUnknown(seed.Code, seed);
        lastVerified = seed.Verified; vdId = lastVerified.CreatedObjectId; osId = lastVerified.CreatedOsDiskId;
        foreach (var pair in seed.ActualTierIds) actualIds.Add(pair.Key, pair.Value);
        foreach (var item in tiers)
        {
            var key = item.Tier.Existing!.Value.ProviderKey;
            var state = MaximumCapacitySearchState.Start(item.MaximumCapacity!.InitialCandidateBytes, seeds[key]);
            var verifiedOrdinal = 1; var boundaryOrdinal = 0; long boundaryBytes = 0; var attemptsForTier = 0;
            while (!state.IsComplete)
            {
                if (++attemptsForTier > item.MaximumCapacity.MaximumAttempts)
                    return MaximumUnknown("real.maximum.attempt_budget_without_boundary", new { key, state.LastSuccessfulBytes });
                var requested = new Dictionary<string, long>(sizes, StringComparer.Ordinal) { [key] = state.CandidateBytes };
                var receipt = await AttemptAsync(key, MaximumCapacityAttemptPhase.Resize, state.CandidateBytes,
                    state.LastSuccessfulBytes, requested, actualIds[key]).ConfigureAwait(false);
                var success = receipt.Verified is not null;
                if (!success && receipt.Code != "real.maximum.capacity_rejected_unchanged") return MaximumUnknown(receipt.Code, receipt);
                if (success) { sizes[key] = state.CandidateBytes; lastVerified = receipt.Verified; verifiedOrdinal = ordinal; }
                else { boundaryOrdinal = ordinal; boundaryBytes = state.CandidateBytes; }
                state.Observe(success);
            }
            if (!state.HasMaximum || state.LastSuccessfulBytes % MaximumCapacityAlgorithm.GiB != 0
                || boundaryOrdinal == 0 || boundaryBytes - state.LastSuccessfulBytes != MaximumCapacityAlgorithm.GiB)
                return MaximumUnknown("real.maximum.multi_no_integer_gib_boundary", new { key, state.LastSuccessfulBytes });
            boundaries.Add(new(key, actualIds[key], state.LastSuccessfulBytes, verifiedOrdinal, boundaryOrdinal, "real.maximum.capacity_rejected_unchanged"));
        }
        var total = sizes.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        var summary = new WindowsMaximumCapacityMultiSearchEvidence(MaximumCapacityAlgorithm.Version, total, ordinal, vdId!, osId!, boundaries);
        return new(RealStepOutcome.Verified, "real.maximum.multi_integer_gib_boundaries_verified",
            JsonSerializer.Serialize(lastVerified! with { PostFingerprint = expected, MaximumCapacityMulti = summary }), vdId);

        async Task<WindowsMaximumCapacityMultiAttemptEvidence> AttemptAsync(string key, MaximumCapacityAttemptPhase phase,
            long bytes, long lastSuccess, IReadOnlyDictionary<string, long> requested, string? actualTier)
        {
            if (journal.IsStopRequested || ct.IsCancellationRequested) return Empty("real.maximum.stopped_before_next_attempt");
            WindowsRealStorageTopology before;
            RealExactPhysicalMemberSetClosure closure;
            WindowsStorageCommandTarget target;
            RealStorageCommand command;
            try
            {
                before = await CaptureOperationTopologyAsync(plan, step.Id + ":multi-before:" + (ordinal + 1), ct).ConfigureAwait(false);
                closure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(before, plan.Targets, macro);
                RequireMultiMaximumIdentity(plan, before, closure, expected); RequireMultiMaximumRaw(before, closure);
                command = phase == MaximumCapacityAttemptPhase.Seed
                    ? macro with { SizeBytes = bytes, UseMaximumSize = false, MaximumCapacity = null }
                    : new ResizeTierCommand(RealTargetReference.ForExisting(new(before.SystemId, StorageObjectKind.StorageTier, actualTier!)), bytes);
                target = BuildMultiMaximumTarget(before, closure,
                    phase == MaximumCapacityAttemptPhase.Seed ? macro.Pool.Existing!.Value : new(before.SystemId, StorageObjectKind.StorageTier, actualTier!), vdId)
                    with { MaximumCapacityAttempt = true, TierInputs = tiers.Select(item => {
                        var template = before.RequireObject(item.Tier.Existing!.Value);
                        return new WindowsStorageTierTarget(template.Id, Text(template, "UniqueId"), Text(template, "ObjectId"),
                            before.Snapshot.StorageTiers.Single(value => value.StableId == template.Id).MediaType, requested[template.Id]); }).ToArray() };
                if (phase == MaximumCapacityAttemptPhase.Seed)
                    target = target with { RelatedUniqueId = target.TierInputs![0].UniqueId, RelatedObjectId = target.TierInputs[0].ObjectId };
                _ = await planner.ValidateMultiMaximumSafetyAsync(before, closure, command, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return Empty("real.maximum.multi_fresh_preflight_failed", ex.ToString()); }
            var observation = MultiMaximumObservation(before, closure);
            var attempt = new MaximumCapacityAttempt(++ordinal, key, phase, bytes, lastSuccess, JsonSerializer.Serialize(target),
                closure.Fingerprint, closure.PhysicalMemberFingerprint, JsonSerializer.Serialize(observation));
            if (!await journal.PrepareAsync(attempt, CancellationToken.None).ConfigureAwait(false)) return Empty("real.maximum.prepare_cas_failed");
            if (journal.IsStopRequested || ct.IsCancellationRequested)
            {
                await journal.CompleteAsync(ordinal, new(MaximumCapacityAttemptState.FailedWithoutCall, lastSuccess,
                    "real.maximum.stopped_before_call", JsonSerializer.Serialize(observation)), CancellationToken.None).ConfigureAwait(false);
                return Empty("real.maximum.stopped_before_call");
            }
            if (!await journal.MarkCallIssuedAsync(ordinal, CancellationToken.None).ConfigureAwait(false)) return Empty("real.maximum.call_boundary_cas_failed");
            WindowsStorageCommandResult provider;
            try { provider = await adapter.ExecuteAsync(command, target, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { provider = new(true, "adapter.exception", null, null, null, null, null, null, ex.ToString()); }
            var receipt = await ObserveMultiMaximumAttemptAsync(plan, macro, command, provider, before, closure, requested,
                actualIds, vdId, osId, CancellationToken.None).ConfigureAwait(false);
            var success = receipt.Verified is not null;
            var rejected = receipt.Code == "real.maximum.capacity_rejected_unchanged";
            var result = new MaximumCapacityAttemptResult(success ? MaximumCapacityAttemptState.Verified
                : rejected ? MaximumCapacityAttemptState.CapacityRejectedUnchanged : MaximumCapacityAttemptState.OutcomeUnknown,
                success ? bytes : lastSuccess, receipt.Code, JsonSerializer.Serialize(receipt),
                success && phase == MaximumCapacityAttemptPhase.Seed ? seeds : null);
            if (!await journal.CompleteAsync(ordinal, result, CancellationToken.None).ConfigureAwait(false)) return Empty("real.maximum.result_cas_failed");
            if (success) expected = receipt.Verified!.PostFingerprint;
            else if (rejected) expected = receipt.Second!.Fingerprint;
            return receipt;
        }
        WindowsMaximumCapacityMultiAttemptEvidence Empty(string code, string? error = null) => new(
            new(false, code, null, null, null, null, null, null, error),
            new("", "", "", "", [], [], []), null, null, [], null, null, code);
    }

    private async Task<WindowsMaximumCapacityMultiAttemptEvidence> ObserveMultiMaximumAttemptAsync(OperationPlan plan,
        CreateTieredVirtualDiskCommand macro, RealStorageCommand command, WindowsStorageCommandResult provider,
        WindowsRealStorageTopology before, RealExactPhysicalMemberSetClosure beforeClosure, IReadOnlyDictionary<string, long> sizes,
        IReadOnlyDictionary<string, string> priorActualIds, string? vdId, string? osId, CancellationToken ct)
    {
        var origin = MultiMaximumObservation(before, beforeClosure);
        if (!provider.ProviderReturned)
            return new(provider, origin, null, null, [], null, null, "real.maximum.adapter_rejected_without_call");
        if (command is ResizeTierCommand resize)
        {
            var source = before.RequireObject(resize.Tier.Existing!.Value);
            if ((!string.IsNullOrWhiteSpace(provider.UniqueId) && provider.UniqueId != Text(source, "UniqueId"))
                || (!string.IsNullOrWhiteSpace(provider.ObjectId) && provider.ObjectId != Text(source, "ObjectId")))
                return new(provider, origin, null, null, [], null, null, "real.maximum.provider_output_identity_changed");
        }
        var capacity = IsExplicitMaximumCapacityRejection(command, provider,
            macro.CapacityTiers!.All(item => before.Snapshot.StorageTiers.Single(value => value.StableId == item.Tier.Existing!.Value.ProviderKey)
                is { ResiliencySettingName: "Simple", Interleave: 65536, NumberOfColumns: 1 }));
        if (provider.Code != "provider.returned" && !capacity)
            return new(provider, origin, null, null, [], null, null, "real.maximum.provider_uncertain_or_noncapacity_failure");
        var deadline = timeProvider.GetUtcNow().Add(PostCallWindow);
        WindowsMaximumCapacityMultiObservation? lastFresh = null;
        string? lastFailedCheck = null;
        string? lastCaptureException = null;
        do
        {
            try
            {
                var first = await CaptureOperationTopologyAsync(plan, "multi-max-postcondition", ct).ConfigureAwait(false);
                var closure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(first, plan.Targets, macro);
                lastFresh = MultiMaximumObservation(first, closure);
                if (capacity && closure.Fingerprint != beforeClosure.Fingerprint)
                    return new(provider, origin, MultiMaximumObservation(first, closure), null, [], null, null, "real.maximum.capacity_error_changed_topology");
                RequireMultiMaximumIdentity(plan, first, closure, capacity ? beforeClosure.Fingerprint : closure.Fingerprint);
                RequireMultiMaximumRaw(first, closure);
                _ = await planner.ValidateMultiMaximumSafetyAsync(first, closure, command, ct).ConfigureAwait(false);
                if (capacity)
                {
                    var jobs1 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
                    if (!MaximumTerminalJobs(jobs1)) return new(provider, origin, MultiMaximumObservation(first, closure), null, [jobs1], null, null, "real.maximum.storage_job_uncertain");
                    await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, ct).ConfigureAwait(false);
                    var second = await CaptureOperationTopologyAsync(plan, "multi-max-unchanged", ct).ConfigureAwait(false);
                    var secondClosure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(second, plan.Targets, macro);
                    var jobs2 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
                    RequireMultiMaximumIdentity(plan, second, secondClosure, beforeClosure.Fingerprint); RequireMultiMaximumRaw(second, secondClosure);
                    _ = await planner.ValidateMultiMaximumSafetyAsync(second, secondClosure, command, ct).ConfigureAwait(false);
                    if (!MaximumTerminalJobs(jobs2)) return new(provider, origin, MultiMaximumObservation(first, closure), MultiMaximumObservation(second, secondClosure), [jobs1, jobs2], null, null, "real.maximum.storage_job_uncertain");
                    return new(provider, origin, MultiMaximumObservation(first, closure), MultiMaximumObservation(second, secondClosure), [jobs1, jobs2], null, null, "real.maximum.capacity_rejected_unchanged");
                }
                var mapping = MatchMultiMaximumActual(first, closure, macro, sizes, priorActualIds, provider, vdId, osId);
                var oldFactsPreserved = MaximumOldFactsPreserved(before, first, beforeClosure.Objects,
                    new[] { vdId, osId }.Concat(command is ResizeTierCommand resizeTier ? new[] { resizeTier.Tier.Existing!.Value.ProviderKey } : Array.Empty<string>())
                        .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal));
                lastFailedCheck = mapping is null ? "MatchMultiMaximumActual" : !oldFactsPreserved ? "MaximumOldFactsPreserved" : null;
                if (mapping is { } verified && oldFactsPreserved)
                {
                    var evidence = new WindowsVerifiedStepEvidence(closure.Fingerprint, closure.PhysicalMemberFingerprint,
                        verified.Vd, provider.Code, verified.Os);
                    return new(provider, origin, MultiMaximumObservation(first, closure), null, [], evidence, verified.Tiers, "real.maximum.attempt_verified");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
            { lastCaptureException = ex.GetType().FullName + ": " + ex.Message; }
            if (timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(PostCallPoll, timeProvider, ct).ConfigureAwait(false);
        } while (true);
        return new(provider, origin, lastFresh, null, [], null, null, "real.maximum.postcondition_unverified")
        { ObservationDiagnostic = JsonSerializer.Serialize(new { LastFailedCheck = lastFailedCheck, LastCaptureException = lastCaptureException }) };
    }

    private static (string Vd, string Os, IReadOnlyDictionary<string, string> Tiers)? MatchMultiMaximumActual(
        WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure, CreateTieredVirtualDiskCommand macro,
        IReadOnlyDictionary<string, long> sizes, IReadOnlyDictionary<string, string> priorIds,
        WindowsStorageCommandResult provider, string? vdId, string? osId)
    {
        var vds = topology.Snapshot.VirtualDisks.Where(item => item.PoolStableId == closure.PoolId).ToArray();
        if (vds.Length != 1) return null;
        var vd = vds[0]; var source = topology.RequireObject(new(topology.SystemId, StorageObjectKind.VirtualDisk, vd.StableId));
        if (vdId is null ? Text(source, "UniqueId") != provider.UniqueId || Text(source, "ObjectId") != provider.ObjectId : vd.StableId != vdId) return null;
        if (!MaximumAggregateLayoutMatches(source)) return null;
        var total = sizes.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        var os = topology.Snapshot.OsDisks.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
        if (os.Length != 1 || (osId is not null && os[0].StableId != osId) || os[0].Size != total || vd.Size != total
            || vd.FootprintOnPool != total || vd.TierStableIds.Count != sizes.Count) return null;
        var actual = topology.Snapshot.StorageTiers.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
        if (actual.Length != sizes.Count || topology.Snapshot.StorageTiers.Count(item => item.PoolStableId == closure.PoolId) != 2 * sizes.Count) return null;
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in macro.CapacityTiers!)
        {
            var template = topology.Snapshot.StorageTiers.Single(value => value.StableId == item.Tier.Existing!.Value.ProviderKey);
            var matches = actual.Where(value => value.MediaType == template.MediaType).ToArray();
            if (matches.Length != 1) return null;
            var tier = matches[0];
            if (tier.PoolStableId != closure.PoolId || tier.Size != sizes[template.StableId] || tier.FootprintOnPool != tier.Size
                || tier.ResiliencySettingName != "Simple" || tier.NumberOfColumns != 1 || tier.Interleave != 65536
                || tier.NumberOfDataCopies != 1 || tier.PhysicalDiskRedundancy != 0
                || (priorIds.TryGetValue(template.StableId, out var prior) && prior != tier.StableId)) return null;
            var tierSource = topology.RequireObject(new(topology.SystemId, StorageObjectKind.StorageTier, tier.StableId));
            if (!ReturnedNumber(tierSource, "ProvisioningType", 2) || !ReturnedNumber(tierSource, "AllocatedSize", tier.Size)
                || !ReturnedNumber(source, "AllocatedSize", total)) return null;
            var allowed = closure.Members.Where(member => topology.Snapshot.PhysicalDisks.Single(value => value.StableId == member.PhysicalDiskId).MediaType == template.MediaType)
                .Select(member => member.PhysicalDiskId).ToArray();
            if (!SameStringSet(tier.MemberPhysicalDiskIds, allowed)) return null;
            mapping.Add(template.StableId, tier.StableId);
        }
        return (vd.StableId, os[0].StableId, mapping);
    }

    internal static bool MaximumAggregateLayoutMatches(WinPoolSourceObject source)
    {
        bool Matches(string name, object expected)
        {
            if (source.Field(name) is not { ReadState: FieldReadState.Returned, Value: { } value }) return false;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (expected is string text) return value.ValueKind == JsonValueKind.String && value.GetString() == text;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var actual)
                && actual == Convert.ToInt64(expected, System.Globalization.CultureInfo.InvariantCulture);
        }
        return Matches("ResiliencySettingName", "Simple") && Matches("ProvisioningType", 2)
            && Matches("NumberOfColumns", 1) && Matches("Interleave", 65536)
            && Matches("NumberOfDataCopies", 1) && Matches("PhysicalDiskRedundancy", 0);
    }

    private static bool ReturnedNumber(WinPoolSourceObject source, string name, long number) => source.Field(name) is
        { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.Number } value } && value.TryGetInt64(out var actual) && actual == number;

    internal static WindowsStorageCommandTarget BuildMultiMaximumTarget(WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, StorageObjectId id, string? vdId)
    {
        var item = topology.RequireObject(id);
        var pool = topology.Snapshot.StoragePools.Single(value => value.StableId == closure.PoolId);
        var subsystem = topology.Facts.Objects.Single(value => value.Id == pool.SubsystemStableId);
        var first = closure.Members[0];
        var owner = vdId is null ? null : topology.RequireObject(new(topology.SystemId, StorageObjectKind.VirtualDisk, vdId));
        return new(id.Kind, Text(item, "UniqueId"), Text(item, "ObjectId"), first.SerialNumber, "", "", null, null, "", null, null,
            id.Kind == StorageObjectKind.StoragePool ? Text(subsystem, "UniqueId") : closure.PoolUniqueId,
            first.UniqueId, Text(subsystem, "UniqueId"), closure.Fingerprint,
            RelatedUniqueId: owner is null ? "" : Text(owner, "UniqueId"), RelatedObjectId: owner is null ? "" : Text(owner, "ObjectId"),
            StorageSubsystemObjectId: Text(subsystem, "ObjectId"), PhysicalMemberObjectId: first.ObjectId,
            PhysicalMembers: closure.Members.Select(member => new WindowsStoragePhysicalMemberTarget(member.PhysicalDiskId, member.UniqueId, member.ObjectId, member.SerialNumber)).ToArray());
    }

    private static void RequireMultiMaximumIdentity(OperationPlan plan, WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, string fingerprint)
    {
        if (topology.MachineBinding != plan.RealOperation!.MachineBinding || closure.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
            || closure.Fingerprint != fingerprint) throw new InvalidDataException("Multi MAX fresh exact identity/topology changed.");
    }
    private static void RequireMultiMaximumRaw(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure) =>
        RequireMaximumRawComponent(topology, new(closure.Members[0].PhysicalDiskId, closure.Objects, closure.Fingerprint, closure.PhysicalMemberFingerprint));
    private static WindowsMaximumCapacityMultiObservation MultiMaximumObservation(WindowsRealStorageTopology topology, RealExactPhysicalMemberSetClosure closure)
    {
        var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return new(topology.InventoryVersion, topology.MachineBinding, closure.Fingerprint, closure.PhysicalMemberFingerprint, closure.Objects,
            topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(), closure.Members);
    }
}
