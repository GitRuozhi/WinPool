using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealStorageBackend
{
    // Recovery receives immutable journal snapshots only. It cannot continue a
    // search, repeat an issued candidate, or acquire an attempt-journal writer.
    private async Task<RealReconciliationResult> ReconcileMultiTierMaximumReadOnlyAsync(OperationPlan plan,
        RealOperationStep step, RealOperationStepProgress parent, IReadOnlyList<MaximumCapacityAttemptRecord> attempts,
        CancellationToken ct)
    {
        RealReconciliationResult Unknown(string code) => new(RealOperationState.OutcomeUnknown, [parent], code, false);
        var macro = (CreateTieredVirtualDiskCommand)step.Command;
        var tiers = macro.CapacityTiers!;
        var seeds = tiers.ToDictionary(item => item.Tier.Existing!.Value.ProviderKey,
            item => MaximumCapacityAlgorithm.SeedBytes(item.MaximumCapacity!.InitialCandidateBytes), StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(seeds, StringComparer.Ordinal);
        var actualIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundaries = new List<WindowsMaximumCapacityTierBoundary>();
        var fingerprint = plan.RealOperation!.TargetFingerprint;
        WindowsVerifiedStepEvidence? lastVerified = null;
        WindowsMaximumCapacityMultiAttemptEvidence? lastReceipt = null;
        string? vdId = null, osId = null;
        var slot = 0;
        var slotAttempts = 0;
        var verifiedOrdinal = 1;
        var boundaryOrdinal = 0;
        long boundaryBytes = 0;
        MaximumCapacitySearchState? state = null;
        var incomplete = attempts.Count == 0;
        for (var index = 0; index < attempts.Count; index++)
        {
            var row = attempts[index];
            var attempt = row.Attempt;
            var seed = index == 0;
            if (attempt.Ordinal != index + 1 || attempt.BeforeFingerprint != fingerprint
                || attempt.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
                || !MultiMaximumRecordedObservationMatches(JsonSerializer.Deserialize<WindowsMaximumCapacityMultiObservation>(attempt.BeforeEvidenceJson), plan, fingerprint))
                return Unknown("real.maximum.multi_recovery_attempt_origin_invalid");
            if (seed)
            {
                if (attempt.Phase != MaximumCapacityAttemptPhase.Seed || attempt.SearchTargetKey != "seed"
                    || attempt.CandidateBytes != seeds.Values.Aggregate(0L, (sum, bytes) => checked(sum + bytes))
                    || attempt.LastSuccessfulBytes != 0) return Unknown("real.maximum.multi_recovery_seed_invalid");
            }
            else
            {
                if (slot >= tiers.Count || state is null || state.IsComplete
                    || attempt.Phase != MaximumCapacityAttemptPhase.Resize
                    || attempt.SearchTargetKey != tiers[slot].Tier.Existing!.Value.ProviderKey
                    || attempt.CandidateBytes != state.CandidateBytes || attempt.LastSuccessfulBytes != state.LastSuccessfulBytes
                    || ++slotAttempts > tiers[slot].MaximumCapacity!.MaximumAttempts)
                    return Unknown("real.maximum.multi_recovery_search_chain_invalid");
            }
            if (row.State is MaximumCapacityAttemptState.PreparingCall or MaximumCapacityAttemptState.CallIssued
                or MaximumCapacityAttemptState.FailedWithoutCall or MaximumCapacityAttemptState.OutcomeUnknown)
            {
                if (index != attempts.Count - 1) return Unknown("real.maximum.multi_recovery_nonterminal_attempt");
                incomplete = true;
                break;
            }
            var result = row.Result ?? throw new InvalidDataException("MAX attempt result absent.");
            var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityMultiAttemptEvidence>(result.ResultEvidenceJson)
                ?? throw new InvalidDataException("MAX multi receipt absent.");
            var requested = new Dictionary<string, long>(sizes, StringComparer.Ordinal);
            if (!seed) requested[attempt.SearchTargetKey] = attempt.CandidateBytes;
            if (result.State != row.State || result.Code != receipt.Code
                || !MultiMaximumRecordedObservationMatches(receipt.Before, plan, fingerprint)
                || !MultiMaximumRecordedTargetMatches(attempt, receipt.Before, macro, requested, actualIds, vdId))
                return Unknown("real.maximum.multi_recovery_receipt_origin_invalid");
            var success = row.State == MaximumCapacityAttemptState.Verified;
            if (success)
            {
                if (receipt.Verified is not { } verified || receipt.Code != "real.maximum.attempt_verified"
                    || !receipt.Provider.ProviderReturned || receipt.Provider.Code != "provider.returned"
                    || verified.ProviderCode != receipt.Provider.Code
                    || verified.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
                    || !MultiMaximumRecordedObservationMatches(receipt.First, plan, verified.PostFingerprint)
                    || result.LastSuccessfulBytes != attempt.CandidateBytes
                    || receipt.ActualTierIds is not { } mapping || mapping.Count != tiers.Count
                    || !MultiMaximumRecordedMappingMatches(receipt.First!, macro, requested, mapping,
                        receipt.Provider, verified.CreatedObjectId, verified.CreatedOsDiskId, seed,
                        seed ? null : actualIds[attempt.SearchTargetKey])
                    || !MultiMaximumRecordedOldFactsPreserved(receipt.Before, receipt.First!,
                        seed ? new HashSet<string>(StringComparer.Ordinal)
                            : new HashSet<string>([vdId!, osId!, actualIds[attempt.SearchTargetKey]], StringComparer.Ordinal))
                    || !seed && (verified.CreatedObjectId != vdId || verified.CreatedOsDiskId != osId
                        || actualIds.Any(pair => !mapping.TryGetValue(pair.Key, out var id) || id != pair.Value)))
                    return Unknown("real.maximum.multi_recovery_verified_receipt_invalid");
                if (seed)
                {
                    if (result.SeedSuccessfulBytes is not { } frozenSeeds || frozenSeeds.Count != seeds.Count
                        || seeds.Any(pair => !frozenSeeds.TryGetValue(pair.Key, out var bytes) || bytes != pair.Value))
                        return Unknown("real.maximum.multi_recovery_seed_mapping_invalid");
                    foreach (var pair in mapping) actualIds.Add(pair.Key, pair.Value);
                    vdId = verified.CreatedObjectId; osId = verified.CreatedOsDiskId;
                    state = MaximumCapacitySearchState.Start(tiers[0].MaximumCapacity!.InitialCandidateBytes, seeds[tiers[0].Tier.Existing!.Value.ProviderKey]);
                }
                else { sizes[attempt.SearchTargetKey] = attempt.CandidateBytes; verifiedOrdinal = attempt.Ordinal; }
                lastVerified = verified;
                fingerprint = verified.PostFingerprint;
            }
            else
            {
                var candidate = seed ? macro with { SizeBytes = attempt.CandidateBytes, UseMaximumSize = false, MaximumCapacity = null }
                    : (RealStorageCommand)new ResizeTierCommand(RealTargetReference.ForExisting(new(plan.SystemId,
                        StorageObjectKind.StorageTier, actualIds[attempt.SearchTargetKey])), attempt.CandidateBytes);
                if (row.State != MaximumCapacityAttemptState.CapacityRejectedUnchanged || receipt.Verified is not null
                    || receipt.Code != "real.maximum.capacity_rejected_unchanged"
                    || result.LastSuccessfulBytes != attempt.LastSuccessfulBytes
                    || !MultiMaximumRecordedObservationMatches(receipt.First, plan, fingerprint)
                    || !MultiMaximumRecordedObservationMatches(receipt.Second, plan, fingerprint)
                    || receipt.StorageJobQueries.Count != 2
                    || !receipt.StorageJobQueries.All(job => MaximumRecordedTerminalJobs(job, row.UpdatedAtUtc))
                    || !IsExplicitMaximumCapacityRejection(candidate, receipt.Provider,
                        MultiMaximumRecordedTemplatesMatch(receipt.Before, macro)))
                    return Unknown("real.maximum.multi_recovery_boundary_unproven");
                if (seed)
                {
                    if (index != attempts.Count - 1) return Unknown("real.maximum.multi_recovery_attempt_after_rejected_seed");
                    incomplete = true; lastReceipt = receipt; break;
                }
                boundaryOrdinal = attempt.Ordinal; boundaryBytes = attempt.CandidateBytes;
            }
            lastReceipt = receipt;
            if (!seed)
            {
                state!.Observe(success);
                if (state.IsComplete)
                {
                    if (!state.HasMaximum || boundaryOrdinal == 0
                        || boundaryBytes - state.LastSuccessfulBytes != MaximumCapacityAlgorithm.GiB)
                        incomplete = true;
                    else boundaries.Add(new(attempt.SearchTargetKey, actualIds[attempt.SearchTargetKey], state.LastSuccessfulBytes,
                        verifiedOrdinal, boundaryOrdinal, "real.maximum.capacity_rejected_unchanged"));
                    slot++; slotAttempts = 0; verifiedOrdinal = 1; boundaryOrdinal = 0; boundaryBytes = 0;
                    state = slot < tiers.Count ? MaximumCapacitySearchState.Start(tiers[slot].MaximumCapacity!.InitialCandidateBytes,
                        seeds[tiers[slot].Tier.Existing!.Value.ProviderKey]) : null;
                }
            }
        }
        var first = await CaptureOperationTopologyAsync(plan, step.Id + ":multi-recovery-first", ct).ConfigureAwait(false);
        var firstClosure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(first, plan.Targets, macro);
        RequireMultiMaximumIdentity(plan, first, firstClosure, fingerprint); RequireMultiMaximumRaw(first, firstClosure);
        _ = await planner.ValidateMultiMaximumSafetyAsync(first, firstClosure, macro, ct).ConfigureAwait(false);
        var jobs1 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
        if (!MaximumTerminalJobs(jobs1)) return Unknown("real.maximum.multi_recovery_jobs_unproven");
        await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, ct).ConfigureAwait(false);
        var second = await CaptureOperationTopologyAsync(plan, step.Id + ":multi-recovery-second", ct).ConfigureAwait(false);
        var closure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(second, plan.Targets, macro);
        RequireMultiMaximumIdentity(plan, second, closure, fingerprint); RequireMultiMaximumRaw(second, closure);
        _ = await planner.ValidateMultiMaximumSafetyAsync(second, closure, macro, ct).ConfigureAwait(false);
        var jobs2 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
        if (!MaximumTerminalJobs(jobs2)) return Unknown("real.maximum.multi_recovery_jobs_unproven");
        if (lastVerified is not null && (MatchMultiMaximumActual(second, closure, macro, sizes, actualIds,
                lastReceipt!.Provider, vdId, osId) is null
            || !MultiMaximumRecordedMappingMatches(MultiMaximumObservation(second, closure), macro, sizes, actualIds,
                lastReceipt.Provider, vdId, osId, false, null)))
            return Unknown("real.maximum.multi_recovery_residual_mapping_invalid");
        if (!incomplete && slot == tiers.Count && boundaries.Count == tiers.Count && lastVerified is not null)
        {
            var summary = new WindowsMaximumCapacityMultiSearchEvidence(MaximumCapacityAlgorithm.Version,
                sizes.Values.Aggregate(0L, (sum, bytes) => checked(sum + bytes)), attempts.Count, vdId!, osId!, boundaries);
            var verified = parent with { State = RealOperationStepState.Verified, Code = "real.maximum.multi_integer_gib_boundaries_verified",
                ResultEvidence = JsonSerializer.Serialize(lastVerified with { PostFingerprint = closure.Fingerprint, MaximumCapacityMulti = summary }) };
            return new(RealOperationState.Succeeded, [verified], verified.Code!, true);
        }
        var failed = parent with { State = RealOperationStepState.Failed, Code = "real.maximum.multi_stopped_without_maximum",
            ResultEvidence = JsonSerializer.Serialize(new { NotVerified = true, MaximumFound = false, AutomaticContinuation = false,
                WindowsCallIssued = attempts.Any(row => row.State is not (MaximumCapacityAttemptState.PreparingCall or MaximumCapacityAttemptState.FailedWithoutCall)),
                Attempts = attempts, LastVerified = lastVerified, OriginalResultEvidence = parent.ResultEvidence,
                First = MultiMaximumObservation(first, firstClosure), Second = MultiMaximumObservation(second, closure),
                StorageJobQueries = new[] { jobs1, jobs2 } }) };
        return new(RealOperationState.Failed, [failed], failed.Code!, true);
    }

    private static bool MultiMaximumRecordedObservationMatches(WindowsMaximumCapacityMultiObservation? value, OperationPlan plan, string fingerprint) =>
        value is not null && value.MachineBinding == plan.RealOperation!.MachineBinding
        && value.PhysicalMemberFingerprint == plan.RealOperation.PhysicalMemberFingerprint && value.Fingerprint == fingerprint
        && value.Objects.All(item => item.HasReliableIdentity)
        && MaximumRecordedFingerprintMatches(value.MachineBinding, value.Objects, value.Associations, fingerprint, multi: true);

    private static bool MultiMaximumRecordedTargetMatches(MaximumCapacityAttempt attempt, WindowsMaximumCapacityMultiObservation before,
        CreateTieredVirtualDiskCommand macro, IReadOnlyDictionary<string, long> sizes, IReadOnlyDictionary<string, string> actualIds, string? vdId)
    {
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(attempt.TargetEvidenceJson);
        if (target is null || !target.MaximumCapacityAttempt || target.ExpectedFingerprint != attempt.BeforeFingerprint
            || target.PhysicalMembers is not { } members || target.TierInputs is not { } inputs
            || inputs.Count != sizes.Count || members.Count != before.Objects.Count(item => item.ObjectType == FactObjectType.PhysicalDisk)
            || members.Select(item => item.StableId).Distinct(StringComparer.Ordinal).Count() != members.Count
            || inputs.Select(item => item.TemplateStableId).Distinct(StringComparer.Ordinal).Count() != inputs.Count) return false;
        foreach (var member in members)
        {
            var source = before.Objects.SingleOrDefault(item => item.Id == member.StableId && item.ObjectType == FactObjectType.PhysicalDisk);
            if (source is null || Text(source, "UniqueId") != member.UniqueId || Text(source, "ObjectId") != member.ObjectId
                || Text(source, "SerialNumber") != member.SerialNumber) return false;
        }
        foreach (var input in inputs)
        {
            var template = before.Objects.SingleOrDefault(item => item.Id == input.TemplateStableId && item.ObjectType == FactObjectType.StorageTier);
            if (template is null || !sizes.TryGetValue(input.TemplateStableId, out var bytes) || input.SizeBytes != bytes
                || input.UniqueId != Text(template, "UniqueId") || input.ObjectId != Text(template, "ObjectId")
                || !MultiMaximumMediaMatches(template, input.MediaType)) return false;
        }
        var id = attempt.Phase == MaximumCapacityAttemptPhase.Seed ? macro.Pool.Existing!.Value.ProviderKey : actualIds[attempt.SearchTargetKey];
        var exact = before.Objects.SingleOrDefault(item => item.Id == id);
        if (exact is null || target.UniqueId != Text(exact, "UniqueId") || target.ObjectId != Text(exact, "ObjectId")) return false;
        if (attempt.Phase == MaximumCapacityAttemptPhase.Seed)
            return target.Kind == StorageObjectKind.StoragePool && target.RelatedUniqueId == inputs[0].UniqueId && target.RelatedObjectId == inputs[0].ObjectId;
        var vd = before.Objects.SingleOrDefault(item => item.Id == vdId && item.ObjectType == FactObjectType.VirtualDisk);
        return target.Kind == StorageObjectKind.StorageTier && vd is not null
            && target.RelatedUniqueId == Text(vd, "UniqueId") && target.RelatedObjectId == Text(vd, "ObjectId");
    }

    private static bool MultiMaximumRecordedMappingMatches(WindowsMaximumCapacityMultiObservation value, CreateTieredVirtualDiskCommand macro,
        IReadOnlyDictionary<string, long> sizes, IReadOnlyDictionary<string, string> ids, WindowsStorageCommandResult provider,
        string? vdId, string? osId, bool seed, string? resizedId)
    {
        var vd = value.Objects.SingleOrDefault(item => item.Id == vdId && item.ObjectType == FactObjectType.VirtualDisk);
        var os = value.Objects.SingleOrDefault(item => item.Id == osId && item.ObjectType == FactObjectType.Disk);
        if (vd is null || os is null || value.Objects.Count(item => item.ObjectType == FactObjectType.VirtualDisk) != 1
            || ids.Count != sizes.Count || ids.Values.Distinct(StringComparer.Ordinal).Count() != sizes.Count
            || !MultiMaximumRecordedTemplatesMatch(value, macro)) return false;
        var total = sizes.Values.Aggregate(0L, (sum, bytes) => checked(sum + bytes));
        if (!ReturnedNumber(vd, "Size", total) || !ReturnedNumber(os, "Size", total)
            || !ReturnedNumber(vd, "AllocatedSize", total) || !ReturnedNumber(vd, "FootprintOnPool", total)
            || value.Associations.Count(edge => !edge.IsRetained && edge.Kind == "same-device" && edge.FromId == vd.Id && edge.ToId == os.Id) != 1
            || value.Associations.Count(edge => !edge.IsRetained && edge.Kind == "pool-virtual-disk" && edge.FromId == macro.Pool.Existing!.Value.ProviderKey && edge.ToId == vd.Id) != 1
            || value.Objects.Count(item => item.ObjectType == FactObjectType.StorageTier) != 2 * sizes.Count) return false;
        var output = seed ? vd : resizedId is null ? null : value.Objects.SingleOrDefault(item => item.Id == resizedId);
        if (output is not null && (provider.UniqueId != Text(output, "UniqueId") || provider.ObjectId != Text(output, "ObjectId"))) return false;
        foreach (var (key, bytes) in sizes)
        {
            if (!ids.TryGetValue(key, out var id)) return false;
            var template = value.Objects.Single(item => item.Id == key);
            var tier = value.Objects.SingleOrDefault(item => item.Id == id && item.ObjectType == FactObjectType.StorageTier);
            if (tier is null || !ReturnedNumber(tier, "Size", bytes) || !ReturnedNumber(tier, "AllocatedSize", bytes)
                || !ReturnedNumber(tier, "FootprintOnPool", bytes) || !ReturnedNumber(tier, "ProvisioningType", 2)
                || Text(tier, "ResiliencySettingName") != "Simple" || !ReturnedNumber(tier, "NumberOfColumns", 1)
                || !ReturnedNumber(tier, "Interleave", 65536) || !ReturnedNumber(tier, "NumberOfDataCopies", 1)
                || !ReturnedNumber(tier, "PhysicalDiskRedundancy", 0)
                || !MultiMaximumMediaMatches(tier, MultiMaximumMedia(template))
                || value.Associations.Count(edge => !edge.IsRetained && edge.Kind == "virtual-disk-tier" && edge.FromId == vd.Id && edge.ToId == tier.Id) != 1
                || value.Associations.Count(edge => !edge.IsRetained && edge.Kind == "pool-tier" && edge.FromId == macro.Pool.Existing!.Value.ProviderKey && edge.ToId == tier.Id) != 1) return false;
            var eligible = value.Associations.Where(edge => !edge.IsRetained && edge.Kind == "template-pool-member" && edge.FromId == key).Select(edge => edge.ToId).Order(StringComparer.Ordinal);
            var allocated = value.Associations.Where(edge => !edge.IsRetained && edge.Kind == "tier-member" && edge.FromId == tier.Id).Select(edge => edge.ToId).Order(StringComparer.Ordinal);
            if (!eligible.SequenceEqual(allocated, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool MultiMaximumRecordedTemplatesMatch(WindowsMaximumCapacityMultiObservation value, CreateTieredVirtualDiskCommand macro) =>
        macro.CapacityTiers!.All(item => value.Objects.SingleOrDefault(source => source.Id == item.Tier.Existing!.Value.ProviderKey) is { } template
            && ReturnedNumber(template, "Size", 0) && ReturnedNumber(template, "AllocatedSize", 0)
            && ReturnedNumber(template, "ProvisioningType", 2) && Text(template, "ResiliencySettingName") == "Simple"
            && ReturnedNumber(template, "NumberOfColumns", 1) && ReturnedNumber(template, "Interleave", 65536)
            && !value.Associations.Any(edge => !edge.IsRetained && edge.Kind is "virtual-disk-tier" or "tier-member" && (edge.FromId == template.Id || edge.ToId == template.Id)));

    private static string MultiMaximumMedia(WinPoolSourceObject source) => source.Field("MediaType")?.Value is { } value
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.TryGetInt64(out var code) ? code switch { 3 => "HDD", 4 => "SSD", _ => "" } : "" : "";
    private static bool MultiMaximumMediaMatches(WinPoolSourceObject source, string media) => media is "HDD" or "SSD"
        && source.Field("MediaType")?.ReadState == FieldReadState.Returned && MultiMaximumMedia(source) == media;

    private static bool MultiMaximumRecordedOldFactsPreserved(WindowsMaximumCapacityMultiObservation before,
        WindowsMaximumCapacityMultiObservation after, IReadOnlySet<string> changedIds)
    {
        foreach (var old in before.Objects)
        {
            var current = after.Objects.SingleOrDefault(item => item.Id == old.Id);
            if (current is null || current.SourceIdentity != old.SourceIdentity || current.ObjectType != old.ObjectType) return false;
            var capacities = old.ObjectType is FactObjectType.StoragePool or FactObjectType.PhysicalDisk || changedIds.Contains(old.Id);
            bool Skip(string name) => name == "SizeRemaining"
                || old.ObjectType == FactObjectType.PhysicalDisk && name == "VirtualDiskFootprint"
                || capacities && name is "Size" or "AllocatedSize" or "FootprintOnPool" or "LargestFreeExtent" or "FreeSpace" or "PhysicalExtents";
            var left = old.Fields.Where(field => !Skip(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            var right = current.Fields.Where(field => !Skip(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (left.Length != right.Length || left.Where((field, index) => field.Name != right[index].Name
                || field.ReadState != right[index].ReadState || field.Value?.GetRawText() != right[index].Value?.GetRawText()).Any()) return false;
        }
        var ids = before.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        string[] Edges(WindowsMaximumCapacityMultiObservation observation) => observation.Associations.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId))
            .Select(edge => edge.FromId + "|" + edge.Kind + "|" + edge.ToId).Order(StringComparer.Ordinal).ToArray();
        return Edges(before).SequenceEqual(Edges(after), StringComparer.Ordinal);
    }
}
