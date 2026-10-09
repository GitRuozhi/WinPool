using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealStorageBackend
{
    public async Task<RealReconciliationResult> ReconcileMaximumCapacitySearchAsync(OperationPlan plan,
        RealOperationStep step, RealOperationStepProgress parent,
        IReadOnlyList<MaximumCapacityAttemptRecord> attempts, CancellationToken cancellationToken)
    {
        RealReconciliationResult Unknown(string code) => new(RealOperationState.OutcomeUnknown, [parent], code, false);
        try
        {
            _ = RequireFrozenStep(plan, step);
            if (step.Command is CreateTieredVirtualDiskCommand { CapacityTiers.Count: > 1 })
                return await ReconcileMultiTierMaximumReadOnlyAsync(plan, step, parent, attempts, cancellationToken).ConfigureAwait(false);
            var policy = step.Command switch
            {
                CreateVirtualDiskCommand { MaximumCapacity: { } value } => value,
                CreateTieredVirtualDiskCommand { MaximumCapacity: { } value } => value,
                _ => throw new InvalidDataException("Frozen MAX policy absent.")
            };
            var physical = plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk);
            var state = MaximumCapacitySearchState.Start(policy.InitialCandidateBytes);
            var searchKey = step.Command is CreateTieredVirtualDiskCommand tiered
                ? tiered.Tier.Existing?.ProviderKey ?? throw new InvalidDataException("Exact template absent.") : "virtual-disk";
            var fingerprint = plan.RealOperation!.TargetFingerprint;
            WindowsVerifiedStepEvidence? lastVerified = null;
            WindowsMaximumCapacityAttemptEvidence? lastReceipt = null;
            var verifiedOrdinal = 0;
            var boundaryOrdinal = 0;
            var ordinal = 0;
            var incomplete = false;
            MaximumCapacityAttempt? pendingCreation = null;
            WindowsMaximumCapacityAttemptEvidence? pendingCreationReceipt = null;
            var capacityReceipts = new List<(RealStorageCommand Command, WindowsStorageCommandResult Provider)>();
            foreach (var record in attempts)
            {
                var attempt = record.Attempt;
                if (++ordinal != attempt.Ordinal || ordinal > policy.MaximumAttempts || state.IsComplete
                    || attempt.SearchTargetKey != searchKey || attempt.CandidateBytes != state.CandidateBytes
                    || attempt.LastSuccessfulBytes != state.LastSuccessfulBytes
                    || attempt.BeforeFingerprint != fingerprint
                    || attempt.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
                    || attempt.Phase != (lastVerified is null ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize))
                    return Unknown("real.maximum.recovery_attempt_chain_invalid");
                if (record.State is MaximumCapacityAttemptState.PreparingCall or MaximumCapacityAttemptState.CallIssued
                    or MaximumCapacityAttemptState.FailedWithoutCall or MaximumCapacityAttemptState.OutcomeUnknown)
                {
                    if (ordinal != attempts.Count) return Unknown("real.maximum.recovery_nonterminal_attempt");
                    if (record.State == MaximumCapacityAttemptState.OutcomeUnknown
                        && attempt.Phase == MaximumCapacityAttemptPhase.Create
                        && step.Command is CreateVirtualDiskCommand && record.Result is { } uncertain
                        && uncertain.State == record.State && uncertain.Code == "real.maximum.postcondition_unverified"
                        && uncertain.LastSuccessfulBytes == attempt.LastSuccessfulBytes)
                    {
                        var uncertainReceipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(uncertain.ResultEvidenceJson)
                            ?? throw new InvalidDataException("Uncertain creation uncertainReceipt absent.");
                        if (uncertainReceipt.Code != uncertain.Code || uncertainReceipt.Verified is not null
                            || !uncertainReceipt.Provider.ProviderReturned || uncertainReceipt.Provider.Code != "provider.returned"
                            || string.IsNullOrWhiteSpace(uncertainReceipt.Provider.UniqueId) || string.IsNullOrWhiteSpace(uncertainReceipt.Provider.ObjectId)
                            || !MaximumRecordedObservationMatches(uncertainReceipt.Before, plan, fingerprint)
                            || !MaximumRecordedObservationMatches(JsonSerializer.Deserialize<WindowsMaximumCapacityObservation>(attempt.BeforeEvidenceJson), plan, fingerprint))
                            return Unknown("real.maximum.recovery_pending_creation_receipt_invalid");
                        pendingCreation = attempt;
                        pendingCreationReceipt = uncertainReceipt;
                        lastReceipt = uncertainReceipt;
                    }
                    // This observes the original pre-call state only. It never
                    // retries the candidate or declares a capacity boundary.
                    incomplete = true;
                    break;
                }
                var result = record.Result ?? throw new InvalidDataException("Attempt result absent.");
                var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(result.ResultEvidenceJson)
                    ?? throw new InvalidDataException("Attempt receipt absent.");
                if (result.State != record.State || result.Code != receipt.Code
                    || !MaximumRecordedObservationMatches(receipt.Before, plan, fingerprint)
                    || !MaximumRecordedObservationMatches(JsonSerializer.Deserialize<WindowsMaximumCapacityObservation>(attempt.BeforeEvidenceJson)!, plan, fingerprint))
                    return Unknown("real.maximum.recovery_origin_invalid");
                var success = record.State == MaximumCapacityAttemptState.Verified;
                if (success)
                {
                    if (receipt.Verified is not { } verified || receipt.Provider.Code != "provider.returned"
                        || receipt.Code != "real.maximum.attempt_verified" || verified.ProviderCode != receipt.Provider.Code
                        || !receipt.Provider.ProviderReturned || receipt.First is null
                        || !MaximumRecordedObservationMatches(receipt.First, plan, verified.PostFingerprint)
                        || verified.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
                        || result.LastSuccessfulBytes != attempt.CandidateBytes
                        || verified.CreatedObjectId is null || verified.CreatedOsDiskId is null
                        || lastVerified is not null && (lastVerified.CreatedObjectId != verified.CreatedObjectId
                            || lastVerified.CreatedOsDiskId != verified.CreatedOsDiskId)
                        || !MaximumRecordedSuccessIdentityMatches(step.Command, attempt, receipt, verified))
                        return Unknown("real.maximum.recovery_success_receipt_invalid");
                    lastVerified = verified;
                    verifiedOrdinal = ordinal;
                    fingerprint = verified.PostFingerprint;
                }
                else
                {
                    if (record.State != MaximumCapacityAttemptState.CapacityRejectedUnchanged
                        || receipt.Code != "real.maximum.capacity_rejected_unchanged" || receipt.Verified is not null
                        || !MaximumRecordedObservationMatches(receipt.First, plan, fingerprint)
                        || !MaximumRecordedObservationMatches(receipt.Second, plan, fingerprint)
                        || receipt.StorageJobQueries.Count != 2 || !receipt.StorageJobQueries.All(item => MaximumRecordedTerminalJobs(item, record.UpdatedAtUtc))
                        || result.LastSuccessfulBytes != attempt.LastSuccessfulBytes)
                        return Unknown("real.maximum.recovery_boundary_receipt_invalid");
                    boundaryOrdinal = ordinal;
                    capacityReceipts.Add((BuildMaximumAttemptCommand(step.Command, attempt.CandidateBytes,
                        lastVerified?.CreatedObjectId, lastVerified?.TieredCreation?.TierInstanceStableId, plan.SystemId), receipt.Provider));
                }
                state.Observe(success);
                lastReceipt = receipt;
            }
            var first = await CaptureOperationTopologyAsync(plan, step.Id + ":max-recovery-first", cancellationToken).ConfigureAwait(false);
            var firstClosure = first.RequireSinglePhysicalClosure([physical]);
            if (pendingCreation is null) RequireMaximumInitialIdentity(plan, first, firstClosure, fingerprint);
            else RequireMaximumPendingOrdinaryCreation(plan, (CreateVirtualDiskCommand)step.Command, pendingCreation,
                pendingCreationReceipt!, first, firstClosure);
            RequireMaximumRawComponent(first, firstClosure);
            var firstSafety = await planner.ValidatePartitionSafetyWithEvidenceAsync(first, firstClosure, step.Command, cancellationToken).ConfigureAwait(false);
            RequirePoolMemberRoleProof(first, firstClosure, firstSafety);
            var jobs1 = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!MaximumTerminalJobs(jobs1)) return Unknown("real.maximum.recovery_jobs_unproven");
            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken).ConfigureAwait(false);
            var second = await CaptureOperationTopologyAsync(plan, step.Id + ":max-recovery-second", cancellationToken).ConfigureAwait(false);
            var secondClosure = second.RequireSinglePhysicalClosure([physical]);
            if (pendingCreation is null) RequireMaximumInitialIdentity(plan, second, secondClosure, fingerprint);
            else
            {
                RequireMaximumPendingOrdinaryCreation(plan, (CreateVirtualDiskCommand)step.Command, pendingCreation,
                    pendingCreationReceipt!, second, secondClosure);
                if (firstClosure.Fingerprint != secondClosure.Fingerprint)
                    return Unknown("real.maximum.recovery_pending_creation_changed");
            }
            RequireMaximumRawComponent(second, secondClosure);
            var secondSafety = await planner.ValidatePartitionSafetyWithEvidenceAsync(second, secondClosure, step.Command, cancellationToken).ConfigureAwait(false);
            RequirePoolMemberRoleProof(second, secondClosure, secondSafety);
            var jobs2 = await storageJobReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!MaximumTerminalJobs(jobs2)) return Unknown("real.maximum.recovery_jobs_unproven");
            if (capacityReceipts.Any(item => !IsExplicitMaximumCapacityRejection(item.Command, item.Provider,
                    MaximumKnownLayout(item.Command, second, lastVerified?.TieredCreation?.TierInstanceStableId))))
                return Unknown("real.maximum.recovery_capacity_reason_unproven");

            if (!incomplete && state.HasMaximum && lastVerified is not null && boundaryOrdinal > 0
                && Math.Abs(verifiedOrdinal - boundaryOrdinal) == 1
                && MaximumRecoveredMappingMatches(second, lastVerified, state.LastSuccessfulBytes, step.Command))
            {
                var search = new WindowsMaximumCapacitySearchEvidence(MaximumCapacityAlgorithm.Version, state.LastSuccessfulBytes,
                    attempts.Count, verifiedOrdinal, boundaryOrdinal, lastVerified.CreatedObjectId!, lastVerified.CreatedOsDiskId!,
                    lastVerified.TieredCreation?.TierInstanceStableId, "real.maximum.capacity_rejected_unchanged");
                var evidence = lastVerified with { PostFingerprint = secondClosure.Fingerprint, MaximumCapacity = search };
                var progress = parent with { State = RealOperationStepState.Verified,
                    Code = "real.maximum.integer_gib_boundary_verified", ResultEvidence = JsonSerializer.Serialize(evidence) };
                return new(RealOperationState.Succeeded, [progress], progress.Code!, true);
            }
            // A stopped or exhausted search is not MAX. Two fresh captures
            // prove the exact known residual without granting replay permission.
            if (lastVerified is not null && !MaximumRecoveredMappingMatches(second, lastVerified, state.LastSuccessfulBytes, step.Command))
                return Unknown("real.maximum.recovery_residual_mapping_invalid");
            var stopped = parent with { State = RealOperationStepState.Failed, Code = "real.maximum.stopped_without_maximum",
                ResultEvidence = JsonSerializer.Serialize(new
                {
                    WindowsCallIssued = attempts.Any(item => item.State != MaximumCapacityAttemptState.PreparingCall
                        && item.State != MaximumCapacityAttemptState.FailedWithoutCall),
                    NotVerified = true, MaximumFound = false, LastVerified = lastVerified, LastReceipt = lastReceipt,
                    Attempts = attempts, First = MaximumObservation(first, firstClosure), Second = MaximumObservation(second, secondClosure),
                    StorageJobQueries = new[] { jobs1, jobs2 }, OriginalResultEvidence = parent.ResultEvidence,
                    AutomaticContinuation = false
                }) };
            return new(RealOperationState.Failed, [stopped], stopped.Code!, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            return Unknown("real.maximum.readonly_recovery_unproven");
        }
    }

    private static bool MaximumRecordedTerminalJobs(WindowsStorageJobAbsenceEvidence value, DateTimeOffset recordedAt) =>
        value.ObservedAtUtc != default && recordedAt != default && value.ObservedAtUtc <= recordedAt.AddSeconds(10)
        && recordedAt - value.ObservedAtUtc <= TimeSpan.FromMinutes(2) && value.Jobs is not null
        && value.Jobs.All(job => !string.IsNullOrWhiteSpace(job.UniqueId) && !string.IsNullOrWhiteSpace(job.ObjectId)
            && job.JobState is 7 or 8 or 9 or 10)
        && value.Jobs.Select(job => job.UniqueId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count
        && value.Jobs.Select(job => job.ObjectId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count;

    private static bool MaximumRecoveredMappingMatches(WindowsRealStorageTopology topology, WindowsVerifiedStepEvidence evidence,
        long bytes, RealStorageCommand command)
    {
        var vd = topology.Snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == evidence.CreatedObjectId);
        var os = topology.Snapshot.OsDisks.SingleOrDefault(item => item.StableId == evidence.CreatedOsDiskId);
        if (vd is null || os is null || bytes <= 0 || bytes % MaximumCapacityAlgorithm.GiB != 0
            || vd.Size != bytes || os.Size != bytes || os.VirtualDiskStableId != vd.StableId) return false;
        if (command is CreateVirtualDiskCommand)
            return vd.TierStableIds.Count == 0 && vd.ResiliencySettingName == "Simple" && vd.ProvisioningType == "Fixed"
                && vd.NumberOfColumns == 1 && vd.Interleave == 65536;
        var mapping = evidence.TieredCreation;
        if (mapping is null || vd.TierStableIds.Count != 1 || vd.TierStableIds[0] != mapping.TierInstanceStableId) return false;
        var tier = topology.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == mapping.TierInstanceStableId);
        var vdObject = topology.RequireObject(new(topology.SystemId, StorageObjectKind.VirtualDisk, vd.StableId));
        var tierObject = tier is null ? null : topology.RequireObject(new(topology.SystemId, StorageObjectKind.StorageTier, tier.StableId));
        var template = topology.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == mapping.TemplateStableId);
        var templateObject = template is null ? null : topology.RequireObject(new(topology.SystemId, StorageObjectKind.StorageTier, template.StableId));
        return tier is not null && tierObject is not null && templateObject is not null
            && command is CreateTieredVirtualDiskCommand macro && macro.Tier.Existing?.ProviderKey == mapping.TemplateStableId
            && Text(vdObject, "UniqueId") == mapping.VirtualDiskUniqueId && Text(vdObject, "ObjectId") == mapping.VirtualDiskObjectId
            && Text(tierObject, "UniqueId") == mapping.TierInstanceUniqueId && Text(tierObject, "ObjectId") == mapping.TierInstanceObjectId
            && Text(templateObject, "UniqueId") == mapping.TemplateUniqueId && Text(templateObject, "ObjectId") == mapping.TemplateObjectId
            && TieredLayoutMatches(vdObject, tierObject, vd, tier, bytes)
            && tier.Size == bytes && tier.VirtualDiskStableId == vd.StableId
            && tier.PoolStableId == vd.PoolStableId && tier.MediaType == "HDD" && tier.ResiliencySettingName == "Simple"
            && tier.NumberOfColumns == 1 && tier.Interleave == 65536;
    }

    private static bool MaximumRecordedObservationMatches(WindowsMaximumCapacityObservation? observation,
        OperationPlan plan, string fingerprint) => observation is not null
        && observation.MachineBinding == plan.RealOperation!.MachineBinding
        && observation.PhysicalMemberFingerprint == plan.RealOperation.PhysicalMemberFingerprint
        && observation.Fingerprint == fingerprint
        && MaximumRecordedFingerprintMatches(observation.MachineBinding, observation.Objects, observation.Associations, fingerprint);

    private static bool MaximumRecordedFingerprintMatches(string machine, IReadOnlyList<WinPoolSourceObject> objects,
        IReadOnlyList<WinPoolFactRelationship> associations, string fingerprint, bool multi = false) =>
        new[] { true, false }.Any(nullAsJson => new[] { false, true }.Any(powerShellStrings =>
            MaximumRecordedComponentFingerprint(machine, objects, associations, multi, nullAsJson, powerShellStrings) == fingerprint));

    private static readonly JsonSerializerOptions MaximumPowerShellStringEncoding = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Recompute the complete canonical component used by fresh preflight. Receipt
    // serialization can change PowerShell's \" string escapes to \u0022 and
    // returned JSON null to nullable null. Try only those known encodings; every
    // field, identity and edge must still hash to the original frozen fingerprint.
    private static string MaximumRecordedComponentFingerprint(string machine,
        IReadOnlyList<WinPoolSourceObject> objects, IReadOnlyList<WinPoolFactRelationship> associations,
        bool multi = false, bool returnedNullAsJsonNull = true, bool powerShellStrings = false)
    {
        var builder = new StringBuilder();
        if (multi) builder.Append("real-exact-physical-member-set-v1\n");
        builder.Append("real-component-v2\n").Append(machine).Append('\n');
        var ids = objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != objects.Count) return "";
        foreach (var item in objects.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            builder.Append("object|").Append((int)item.ObjectType).Append('|').Append(item.Id).Append('|').Append(item.SourceIdentity).Append('\n');
            foreach (var field in item.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                if (field.Name.Equals("SizeRemaining", StringComparison.OrdinalIgnoreCase)) continue;
                builder.Append("field|").Append(field.Name).Append('|').Append((int)field.ReadState).Append('|')
                    .Append(field.Value is { } value ? powerShellStrings && value.ValueKind == JsonValueKind.String
                            ? JsonSerializer.Serialize(value.GetString(), MaximumPowerShellStringEncoding) : value.GetRawText()
                        : returnedNullAsJsonNull && field.ReadState == FieldReadState.Returned ? "null" : "<missing>").Append('\n');
            }
        }
        foreach (var edge in associations.Where(item => !item.IsRetained && ids.Contains(item.FromId) && ids.Contains(item.ToId))
                     .OrderBy(item => item.FromId, StringComparer.Ordinal).ThenBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.ToId, StringComparer.Ordinal))
            builder.Append("edge|").Append(edge.FromId).Append('|').Append(edge.Kind).Append('|').Append(edge.ToId).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static bool MaximumRecordedSuccessIdentityMatches(RealStorageCommand macro, MaximumCapacityAttempt attempt,
        WindowsMaximumCapacityAttemptEvidence receipt, WindowsVerifiedStepEvidence verified)
    {
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(attempt.TargetEvidenceJson);
        if (target is null || target.ExpectedFingerprint != attempt.BeforeFingerprint || !target.MaximumCapacityAttempt) return false;
        var physical = receipt.Before.Objects.Single(item => item.ObjectType == FactObjectType.PhysicalDisk);
        if (target.PhysicalMemberUniqueId != Text(physical, "UniqueId") || target.PhysicalMemberObjectId != Text(physical, "ObjectId")
            || target.SerialNumber != Text(physical, "SerialNumber")) return false;
        var first = receipt.First!;
        var vd = first.Objects.SingleOrDefault(item => item.Id == verified.CreatedObjectId && item.ObjectType == FactObjectType.VirtualDisk);
        var os = first.Objects.SingleOrDefault(item => item.Id == verified.CreatedOsDiskId && item.ObjectType == FactObjectType.Disk);
        if (vd is null || os is null || !first.Associations.Any(edge => edge.FromId == vd.Id && edge.ToId == os.Id
                || edge.FromId == os.Id && edge.ToId == vd.Id)) return false;
        var output = attempt.Phase == MaximumCapacityAttemptPhase.Create ? vd
            : macro is CreateTieredVirtualDiskCommand ? first.Objects.SingleOrDefault(item => item.Id == verified.TieredCreation?.TierInstanceStableId) : vd;
        if (output is null || receipt.Provider.UniqueId != Text(output, "UniqueId") || receipt.Provider.ObjectId != Text(output, "ObjectId")) return false;
        var targetId = attempt.Phase == MaximumCapacityAttemptPhase.Create
            ? macro switch { CreateVirtualDiskCommand ordinary => ordinary.Pool.Existing?.ProviderKey,
                CreateTieredVirtualDiskCommand tier => tier.Pool.Existing?.ProviderKey, _ => null } : output.Id;
        var beforeTarget = receipt.Before.Objects.SingleOrDefault(item => item.Id == targetId);
        if (beforeTarget is null || target.UniqueId != Text(beforeTarget, "UniqueId") || target.ObjectId != Text(beforeTarget, "ObjectId")) return false;
        if (attempt.Phase == MaximumCapacityAttemptPhase.Resize
            && (target.UniqueId != receipt.Provider.UniqueId || target.ObjectId != receipt.Provider.ObjectId)) return false;
        if (macro is not CreateTieredVirtualDiskCommand tiered) return verified.TieredCreation is null;
        if (verified.TieredCreation is not { } mapping || mapping.TemplateStableId != tiered.Tier.Existing?.ProviderKey
            || mapping.VirtualDiskStableId != vd.Id || mapping.PoolStableId != tiered.Pool.Existing?.ProviderKey
            || mapping.PhysicalDiskStableId != first.Objects.Single(item => item.ObjectType == FactObjectType.PhysicalDisk).Id
            || mapping.SizeBytes != attempt.CandidateBytes) return false;
        var template = first.Objects.SingleOrDefault(item => item.Id == mapping.TemplateStableId);
        var actual = first.Objects.SingleOrDefault(item => item.Id == mapping.TierInstanceStableId);
        if (template is null || actual is null || mapping.TemplateUniqueId != Text(template, "UniqueId")
            || mapping.TemplateObjectId != Text(template, "ObjectId") || mapping.VirtualDiskUniqueId != Text(vd, "UniqueId")
            || mapping.VirtualDiskObjectId != Text(vd, "ObjectId") || mapping.TierInstanceUniqueId != Text(actual, "UniqueId")
            || mapping.TierInstanceObjectId != Text(actual, "ObjectId")) return false;
        if (attempt.Phase != MaximumCapacityAttemptPhase.Create) return true;
        return receipt.Provider.TieredCreationInput is { } input
            && TieredCreationInputMatches(tiered, target, input)
            && verified.TieredCreationInput == input && input.TemplateUniqueId == mapping.TemplateUniqueId
            && input.TemplateObjectId == mapping.TemplateObjectId && input.PoolUniqueId == target.UniqueId
            && input.PhysicalMemberUniqueId == target.PhysicalMemberUniqueId && input.SizeBytes == attempt.CandidateBytes
            && !input.UseMaximumSize && input.CreationMechanism == TieredVirtualDiskCreationMechanism.ExactTemplate
            && input.MediaType == "HDD" && input.ResiliencySettingName == "Simple" && input.ProvisioningType == "Fixed"
            && input.NumberOfColumns == 1 && input.Interleave == 65536;
    }
}
