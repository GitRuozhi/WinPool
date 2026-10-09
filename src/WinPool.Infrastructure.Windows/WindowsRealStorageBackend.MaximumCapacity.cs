using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsMaximumCapacityObservation(string InventoryVersion, string MachineBinding,
    string Fingerprint, string PhysicalMemberFingerprint, IReadOnlyList<WinPoolSourceObject> Objects,
    IReadOnlyList<WinPoolFactRelationship> Associations, WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence);

public sealed record WindowsMaximumCapacityAttemptEvidence(WindowsStorageCommandResult Provider,
    WindowsMaximumCapacityObservation Before, WindowsMaximumCapacityObservation? First,
    WindowsMaximumCapacityObservation? Second, IReadOnlyList<WindowsStorageJobAbsenceEvidence> StorageJobQueries,
    WindowsVerifiedStepEvidence? Verified, string Code)
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? ObservationDiagnostic { get; init; }
}

public sealed record WindowsMaximumCapacitySearchEvidence(string AlgorithmVersion, long ActualSizeBytes,
    int AttemptCount, int LastVerifiedOrdinal, int CapacityBoundaryOrdinal,
    string VirtualDiskId, string OsDiskId, string? ActualTierId, string BoundaryCode);

public sealed partial class WindowsRealStorageBackend
{
    internal static bool IsMaximumCapacityMacro(RealStorageCommand command) => command is
        CreateVirtualDiskCommand { UseMaximumSize: true } or CreateTieredVirtualDiskCommand { UseMaximumSize: true };

    public async Task<RealStepResult> ExecuteMaximumCapacitySearchAsync(OperationPlan plan, RealOperationStep step,
        RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var stored = RequireFrozenStep(plan, step);
        var policy = stored.Command switch
        {
            CreateVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: { } value } => value,
            CreateTieredVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: { } value } => value,
            _ => throw new InvalidDataException("An Agent-frozen MAX macro is required.")
        };
        if (stored.Command is CreateTieredVirtualDiskCommand { CapacityTiers.Count: > 1 } multi)
            return await ExecuteMultiMaximumCapacityAsync(plan, stored, multi, preflight, journal, cancellationToken).ConfigureAwait(false);
        if ((await journal.ReadAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
            return MaximumUnknown("real.maximum.existing_attempts_never_replayed", new { plan.OperationId, step.Id });
        var physical = plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk);
        var expectedFingerprint = preflight.TargetFingerprint;
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)
            ?? throw new InvalidDataException("MAX preflight target missing.");
        if (policy.AlgorithmVersion != MaximumCapacityAlgorithm.Version || policy.SourceFingerprint != expectedFingerprint
            || policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes))
            throw new InvalidDataException("MAX policy differs from frozen preflight.");
        var state = MaximumCapacitySearchState.Start(policy.InitialCandidateBytes);
        WindowsVerifiedStepEvidence? lastVerified = null;
        WindowsRealStorageTopology? lastTopology = null;
        string? vdId = null, osId = null, actualTierId = null;
        var ordinal = 0; var lastVerifiedOrdinal = 0; var boundaryOrdinal = 0;
        while (!state.IsComplete)
        {
            if (journal.IsStopRequested || cancellationToken.IsCancellationRequested)
                return MaximumUnknown("real.maximum.stopped_before_next_attempt", new { LastVerified = lastVerified, LastSuccessfulBytes = state.LastSuccessfulBytes });
            if (ordinal >= policy.MaximumAttempts)
                return MaximumUnknown("real.maximum.attempt_budget_without_boundary", new { LastVerified = lastVerified, LastSuccessfulBytes = state.LastSuccessfulBytes });
            WindowsRealStorageTopology before;
            RealTargetClosure closure;
            RealStorageCommand command;
            WindowsStorageCommandTarget attemptTarget;
            try
            {
                before = await CaptureOperationTopologyAsync(plan, step.Id + ":max-before:" + (ordinal + 1), cancellationToken).ConfigureAwait(false);
                closure = before.RequireSinglePhysicalClosure([physical]);
                RequireMaximumInitialIdentity(plan, before, closure, expectedFingerprint);
                command = BuildMaximumAttemptCommand(stored.Command, state.CandidateBytes, vdId, actualTierId, before.SystemId);
                var reference = WindowsRealStorageTargetBuilder.GetReference(command);
                attemptTarget = WindowsRealStorageTargetBuilder.Build(before, reference, new Dictionary<string, string>())
                    with { MaximumCapacityAttempt = true };
                if (command is CreateTieredVirtualDiskCommand tiered)
                {
                    var related = WindowsRealStorageTargetBuilder.Build(before, tiered.Tier, new Dictionary<string, string>());
                    attemptTarget = attemptTarget with { RelatedUniqueId = related.UniqueId, RelatedObjectId = related.ObjectId };
                }
                if (command is ResizeTierCommand && vdId is not null)
                {
                    var owner = before.RequireObject(new(before.SystemId, StorageObjectKind.VirtualDisk, vdId));
                    attemptTarget = attemptTarget with { RelatedUniqueId = Text(owner, "UniqueId"), RelatedObjectId = Text(owner, "ObjectId") };
                }
                var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(before, closure, command, cancellationToken).ConfigureAwait(false);
                RequirePoolMemberRoleProof(before, closure, safety);
                RequireMaximumRawComponent(before, closure);
                if (vdId is not null && (lastTopology is null || !MaximumResizeBeforeMatches(lastTopology, before, vdId, actualTierId)))
                    throw new InvalidDataException("Last verified MAX mapping changed before the next attempt.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return MaximumUnknown("real.maximum.fresh_preflight_failed", new { Error = exception.ToString(), LastVerified = lastVerified });
            }
            var observation = MaximumObservation(before, closure);
            var attempt = new MaximumCapacityAttempt(++ordinal, stored.Command is CreateTieredVirtualDiskCommand tc ? tc.Tier.Existing!.Value.ProviderKey : "virtual-disk", vdId is null ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize,
                state.CandidateBytes, state.LastSuccessfulBytes, JsonSerializer.Serialize(attemptTarget), closure.Fingerprint,
                closure.PhysicalMemberFingerprint, JsonSerializer.Serialize(observation));
            if (!await journal.PrepareAsync(attempt, CancellationToken.None).ConfigureAwait(false))
                return MaximumUnknown("real.maximum.prepare_cas_failed", attempt);
            if (journal.IsStopRequested || cancellationToken.IsCancellationRequested)
            {
                await journal.CompleteAsync(ordinal, new(MaximumCapacityAttemptState.FailedWithoutCall, state.LastSuccessfulBytes,
                    "real.maximum.stopped_before_call", JsonSerializer.Serialize(new WindowsNoEffectStepEvidence(true,
                        "real.maximum.stopped_before_call", closure.PhysicalMemberFingerprint))), CancellationToken.None).ConfigureAwait(false);
                return MaximumUnknown("real.maximum.stopped_before_call", attempt);
            }
            if (!await journal.MarkCallIssuedAsync(ordinal, CancellationToken.None).ConfigureAwait(false))
                return MaximumUnknown("real.maximum.call_boundary_cas_failed", attempt);
            WindowsStorageCommandResult provider;
            try { provider = await adapter.ExecuteAsync(command, attemptTarget, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var unknown = new MaximumCapacityAttemptResult(MaximumCapacityAttemptState.OutcomeUnknown, state.LastSuccessfulBytes,
                    "real.maximum.adapter_exception", JsonSerializer.Serialize(new { OriginalException = exception.ToString(), Attempt = attempt }));
                await journal.CompleteAsync(ordinal, unknown, CancellationToken.None).ConfigureAwait(false);
                return MaximumUnknown(unknown.Code, unknown);
            }
            var result = await ObserveMaximumAttemptAsync(plan, command, attemptTarget, provider, before, closure,
                vdId, actualTierId, CancellationToken.None).ConfigureAwait(false);
            var success = result.Verified is not null;
            var rejected = result.Code == "real.maximum.capacity_rejected_unchanged";
            var attemptState = success ? MaximumCapacityAttemptState.Verified
                : rejected ? MaximumCapacityAttemptState.CapacityRejectedUnchanged
                : MaximumCapacityAttemptState.OutcomeUnknown;
            var attemptResult = new MaximumCapacityAttemptResult(attemptState, success ? attempt.CandidateBytes : attempt.LastSuccessfulBytes,
                result.Code, JsonSerializer.Serialize(result));
            if (!await journal.CompleteAsync(ordinal, attemptResult, CancellationToken.None).ConfigureAwait(false))
                return MaximumUnknown("real.maximum.result_cas_failed", result);
            if (!success && !rejected) return MaximumUnknown(result.Code, result);
            if (success)
            {
                lastVerified = result.Verified!; lastVerifiedOrdinal = ordinal;
                vdId ??= lastVerified.CreatedObjectId;
                osId ??= lastVerified.CreatedOsDiskId;
                actualTierId ??= lastVerified.TieredCreation?.TierInstanceStableId;
                if (vdId is null || osId is null) return MaximumUnknown("real.maximum.actual_mapping_missing", result);
                expectedFingerprint = lastVerified.PostFingerprint;
                lastTopology = await CaptureOperationTopologyAsync(plan, step.Id + ":max-verified:" + ordinal, CancellationToken.None).ConfigureAwait(false);
                RequireMaximumInitialIdentity(plan, lastTopology, lastTopology.RequireSinglePhysicalClosure([physical]), expectedFingerprint);
            }
            else { boundaryOrdinal = ordinal; expectedFingerprint = result.Second!.Fingerprint; }
            state.Observe(success);
        }
        if (!state.HasMaximum || state.LastSuccessfulBytes <= 0 || lastVerified is null || vdId is null || osId is null
            || boundaryOrdinal == 0 || Math.Abs(lastVerifiedOrdinal - boundaryOrdinal) != 1)
            return MaximumUnknown("real.maximum.no_complete_integer_gib_boundary", new { state.LastSuccessfulBytes, LastVerified = lastVerified });
        // The boundary observation is unchanged from last success. No shrink,
        // format, partition or implicit cleanup is performed by this macro.
        var search = new WindowsMaximumCapacitySearchEvidence(MaximumCapacityAlgorithm.Version, state.LastSuccessfulBytes,
            ordinal, lastVerifiedOrdinal, boundaryOrdinal, vdId, osId, actualTierId, "real.maximum.capacity_rejected_unchanged");
        var finalEvidence = lastVerified with { PostFingerprint = expectedFingerprint, MaximumCapacity = search };
        return new(RealStepOutcome.Verified, "real.maximum.integer_gib_boundary_verified", JsonSerializer.Serialize(finalEvidence), vdId);
    }

    private static RealStorageCommand BuildMaximumAttemptCommand(RealStorageCommand macro, long bytes,
        string? vdId, string? actualTierId, SystemId system)
    {
        if (bytes <= 0 || bytes % MaximumCapacityAlgorithm.GiB != 0) throw new InvalidDataException("MAX candidate is not a positive whole GiB.");
        if (vdId is null) return macro switch
        {
            CreateVirtualDiskCommand value => value with { SizeBytes = bytes, UseMaximumSize = false, MaximumCapacity = null },
            CreateTieredVirtualDiskCommand value => value with { SizeBytes = bytes, UseMaximumSize = false, MaximumCapacity = null, CapacityTiers = null },
            _ => throw new InvalidDataException("Unsupported maximum macro.")
        };
        return macro is CreateTieredVirtualDiskCommand
            ? new ResizeTierCommand(RealTargetReference.ForExisting(new(system, StorageObjectKind.StorageTier,
                actualTierId ?? throw new InvalidDataException("Actual tier mapping absent."))), bytes)
            : new ResizeVirtualDiskCommand(RealTargetReference.ForExisting(new(system, StorageObjectKind.VirtualDisk, vdId)), bytes);
    }

    private async Task<WindowsMaximumCapacityAttemptEvidence> ObserveMaximumAttemptAsync(OperationPlan plan,
        RealStorageCommand command, WindowsStorageCommandTarget target, WindowsStorageCommandResult provider,
        WindowsRealStorageTopology before, RealTargetClosure beforeClosure, string? vdId, string? tierId, CancellationToken ct)
    {
        var origin = MaximumObservation(before, beforeClosure);
        if (command is ResizeVirtualDiskCommand or ResizeTierCommand)
        {
            if ((!string.IsNullOrWhiteSpace(provider.UniqueId) && provider.UniqueId != target.UniqueId)
                || (!string.IsNullOrWhiteSpace(provider.ObjectId) && provider.ObjectId != target.ObjectId))
                return new(provider, origin, null, null, [], null, "real.maximum.provider_output_identity_changed");
        }
        if (!provider.ProviderReturned)
            return new(provider, origin, null, null, [], null, "real.maximum.adapter_rejected_without_call");
        var capacity = IsExplicitMaximumCapacityRejection(command, provider, MaximumKnownLayout(command, before, tierId));
        if (provider.Code != "provider.returned" && !capacity)
            return new(provider, origin, null, null, [], null, "real.maximum.provider_uncertain_or_noncapacity_failure");
        var physical = plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk);
        var deadline = timeProvider.GetUtcNow().Add(PostCallWindow);
        WindowsMaximumCapacityObservation? lastFresh = null;
        string? lastFailedCheck = null;
        string? lastCaptureException = null;
        do
        {
            try
            {
                var first = await CaptureOperationTopologyAsync(plan, "max-postcondition", ct).ConfigureAwait(false);
                var firstClosure = first.RequireSinglePhysicalClosure([physical]);
                lastFresh = MaximumObservation(first, firstClosure);
                if (first.MachineBinding != plan.RealOperation!.MachineBinding || firstClosure.PhysicalMemberFingerprint != beforeClosure.PhysicalMemberFingerprint)
                    return new(provider, origin, MaximumObservation(first, firstClosure), null, [], null, "real.maximum.physical_or_machine_changed");
                RequireMaximumRawComponent(first, firstClosure);
                if (capacity)
                {
                    if (firstClosure.Fingerprint != beforeClosure.Fingerprint)
                        return new(provider, origin, MaximumObservation(first, firstClosure), null, [], null, "real.maximum.capacity_error_changed_topology");
                    var jobs1 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
                    if (!MaximumTerminalJobs(jobs1)) return new(provider, origin, MaximumObservation(first, firstClosure), null, [jobs1], null, "real.maximum.storage_job_uncertain");
                    await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, ct).ConfigureAwait(false);
                    var second = await CaptureOperationTopologyAsync(plan, "max-capacity-unchanged", ct).ConfigureAwait(false);
                    var secondClosure = second.RequireSinglePhysicalClosure([physical]);
                    var jobs2 = await storageJobReader.ReadAsync(ct).ConfigureAwait(false);
                    if (second.MachineBinding != before.MachineBinding || secondClosure.PhysicalMemberFingerprint != beforeClosure.PhysicalMemberFingerprint
                        || secondClosure.Fingerprint != beforeClosure.Fingerprint || !MaximumTerminalJobs(jobs2))
                        return new(provider, origin, MaximumObservation(first, firstClosure), MaximumObservation(second, secondClosure), [jobs1, jobs2], null, "real.maximum.capacity_boundary_unproven");
                    var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(second, secondClosure, command, ct).ConfigureAwait(false);
                    RequirePoolMemberRoleProof(second, secondClosure, safety);
                    return new(provider, origin, MaximumObservation(first, firstClosure), MaximumObservation(second, secondClosure), [jobs1, jobs2], null, "real.maximum.capacity_rejected_unchanged");
                }
                var verified = VerifyAfter(command, target, provider, before, first);
                var resizeMatches = verified is not null && MaximumResizeAfterMatches(command, before, first, vdId ?? verified.CreatedObjectId, tierId);
                var oldFactsPreserved = verified is not null && MaximumOldFactsPreserved(before, first, beforeClosure.Objects,
                    new[] { vdId ?? verified.CreatedObjectId, tierId, verified.CreatedOsDiskId,
                        first.Snapshot.OsDisks.SingleOrDefault(item => item.VirtualDiskStableId == (vdId ?? verified.CreatedObjectId))?.StableId }.Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal));
                lastFailedCheck = verified is null ? "VerifyAfter" : !resizeMatches ? "MaximumResizeAfterMatches"
                    : !oldFactsPreserved ? "MaximumOldFactsPreserved" : "SafetyAndMapping";
                if (verified is not null && resizeMatches && oldFactsPreserved)
                {
                    var safety = await planner.ValidatePartitionSafetyWithEvidenceAsync(first, firstClosure, command, ct).ConfigureAwait(false);
                    RequirePoolMemberRoleProof(first, firstClosure, safety);
                    var createdVd = verified.CreatedObjectId ?? vdId;
                    var createdOs = verified.CreatedOsDiskId ?? first.Snapshot.OsDisks.Single(item => item.VirtualDiskStableId == createdVd).StableId;
                    var tierMapping = verified.TieredCreation;
                    if (tierMapping is null && tierId is not null)
                    {
                        var old = before.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
                        var actual = first.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
                        var template = first.Snapshot.StorageTiers.Single(item => item.PoolStableId == actual.PoolStableId && item.VirtualDiskStableId is null);
                        var vdObject = first.RequireObject(new(first.SystemId, StorageObjectKind.VirtualDisk, createdVd!));
                        var tierObject = first.RequireObject(new(first.SystemId, StorageObjectKind.StorageTier, tierId));
                        var templateObject = first.RequireObject(new(first.SystemId, StorageObjectKind.StorageTier, template.StableId));
                        tierMapping = new(template.StableId, Text(templateObject, "UniqueId"), Text(templateObject, "ObjectId"),
                            vdObject.Id, Text(vdObject, "UniqueId"), Text(vdObject, "ObjectId"), tierObject.Id, Text(tierObject, "UniqueId"), Text(tierObject, "ObjectId"),
                            actual.PoolStableId ?? throw new InvalidDataException("Actual tier pool identity missing."), firstClosure.PhysicalDiskId, actual.Size, actual.MediaType, actual.ResiliencySettingName, "Fixed", 1, 65536,
                            first.Facts.Relationships.Where(edge => !edge.IsRetained).ToArray(), vdObject.Fields, tierObject.Fields);
                    }
                    var evidence = new WindowsVerifiedStepEvidence(firstClosure.Fingerprint, firstClosure.PhysicalMemberFingerprint,
                        createdVd, provider.Code, createdOs, tierMapping, provider.LiveCapabilityEvidence,
                        PoolMemberRoleEvidence: firstClosure.PoolMemberRoleEvidence, TieredCreationInput: provider.TieredCreationInput);
                    return new(provider, origin, MaximumObservation(first, firstClosure), null, [], evidence, "real.maximum.attempt_verified");
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            { lastCaptureException = exception.GetType().FullName + ": " + exception.Message; }
            if (timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(PostCallPoll, timeProvider, ct).ConfigureAwait(false);
        } while (true);
        return new(provider, origin, lastFresh, null, [], null, "real.maximum.postcondition_unverified")
        { ObservationDiagnostic = JsonSerializer.Serialize(new { LastFailedCheck = lastFailedCheck, LastCaptureException = lastCaptureException }) };
    }

    internal const string EligibleResourceCapacityReason = "The storage pool does not have sufficient eligible resources for the creation of the specified virtual disk.";

    internal static bool IsExplicitMaximumCapacityRejection(RealStorageCommand command, WindowsStorageCommandResult provider,
        bool knownSupportedLayout = false)
    {
        if (provider is not { ProviderReturned: true, Code: "provider.error-outcome-unknown", ProviderFailure: { CodeSource: "cdxml-storagewmi-error-id" } failure }
            || command is not (CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or ResizeVirtualDiskCommand or ResizeTierCommand)
            || !string.IsNullOrWhiteSpace(provider.UniqueId) || !string.IsNullOrWhiteSpace(provider.ObjectId)
            || !string.IsNullOrEmpty(provider.PartitionGuid) || provider.PartitionNumber is not null || provider.DiskNumber is not null || provider.ProviderJobId is not null)
            return false;
        if (failure.StorageReturnCode == 40000) return true;
        // This closed local-provider reason is narrower than generic NotSupported
        // or InsufficientResources. The original structured error is retained and
        // still needs two unchanged fresh closures plus complete terminal jobs.
        return knownSupportedLayout && failure is { StorageReturnCode: 1, ErrorDataClass: "MSFT_WmiError",
            ErrorCategory: 7, ErrorCode: 1, ErrorType: "StorageWMI" }
            && failure.FullyQualifiedErrorId == "StorageWMI 1,New-VirtualDisk"
            && failure.ExtendedMessage is { } message
            && message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Any(line => line.Trim().Equals(EligibleResourceCapacityReason, StringComparison.Ordinal));
    }

    private static bool MaximumKnownLayout(RealStorageCommand command, WindowsRealStorageTopology before, string? tierId) => command switch
    {
        CreateVirtualDiskCommand value => value.DataColumns == 1 && value.InterleaveBytes == 65536,
        CreateTieredVirtualDiskCommand value => value.CreationMechanism == TieredVirtualDiskCreationMechanism.ExactTemplate
            && before.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == value.Tier.Existing?.ProviderKey)
                is { MediaType: "HDD", ResiliencySettingName: "Simple", NumberOfColumns: 1, Interleave: 65536 },
        ResizeVirtualDiskCommand value => before.Snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == value.VirtualDisk.Existing?.ProviderKey)
            is { ResiliencySettingName: "Simple", ProvisioningType: "Fixed", NumberOfColumns: 1, Interleave: 65536 },
        ResizeTierCommand => before.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == tierId)
            is { MediaType: "HDD", ResiliencySettingName: "Simple", NumberOfColumns: 1, Interleave: 65536 },
        _ => false
    };

    private bool MaximumTerminalJobs(WindowsStorageJobAbsenceEvidence value) => value.ObservedAtUtc != default
        && value.ObservedAtUtc <= timeProvider.GetUtcNow().AddSeconds(10) && timeProvider.GetUtcNow() - value.ObservedAtUtc <= TimeSpan.FromMinutes(2)
        && value.Jobs is not null && value.Jobs.All(job => !string.IsNullOrWhiteSpace(job.UniqueId) && !string.IsNullOrWhiteSpace(job.ObjectId) && job.JobState is 7 or 8 or 9 or 10)
        && value.Jobs.Select(job => job.UniqueId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count
        && value.Jobs.Select(job => job.ObjectId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count;

    private static WindowsMaximumCapacityObservation MaximumObservation(WindowsRealStorageTopology topology, RealTargetClosure closure)
    {
        var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return new(topology.InventoryVersion, topology.MachineBinding, closure.Fingerprint, closure.PhysicalMemberFingerprint,
            closure.Objects, topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(), closure.PoolMemberRoleEvidence);
    }

    private static void RequireMaximumInitialIdentity(OperationPlan plan, WindowsRealStorageTopology topology, RealTargetClosure closure, string fingerprint)
    {
        if (topology.MachineBinding != plan.RealOperation!.MachineBinding || closure.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
            || closure.Fingerprint != fingerprint) throw new InvalidDataException("Fresh MAX identity or topology differs from last durable observation.");
    }

    private static void RequireMaximumRawComponent(WindowsRealStorageTopology topology, RealTargetClosure closure)
    {
        if (closure.Objects.Any(item => item.ObjectType is FactObjectType.Partition or FactObjectType.Volume))
            throw new InvalidDataException("MAX search only operates before partition/volume creation.");
        foreach (var item in closure.Objects.Where(item => item.ObjectType == FactObjectType.Disk))
            if (!ReturnedFalse(item, "IsReadOnly") || !ReturnedFalse(item, "IsClustered") || !ReturnedFalse(item, "IsOffline")
                || !ReturnedFalse(item, "IsBoot") || !ReturnedFalse(item, "IsSystem")
                || item.Field("PartitionStyle")?.Value is not { ValueKind: JsonValueKind.Number } style || !style.TryGetInt32(out var number) || number != 0
                || !ReturnedZero(item, "NumberOfPartitions")) throw new InvalidDataException("MAX requires exact safe online RAW OS disks.");
    }

    private static bool MaximumResizeBeforeMatches(WindowsRealStorageTopology previous, WindowsRealStorageTopology current, string vdId, string? tierId) =>
        previous.RequireObject(new(previous.SystemId, StorageObjectKind.VirtualDisk, vdId)).SourceIdentity
            == current.RequireObject(new(current.SystemId, StorageObjectKind.VirtualDisk, vdId)).SourceIdentity
        && (tierId is null || previous.RequireObject(new(previous.SystemId, StorageObjectKind.StorageTier, tierId)).SourceIdentity
            == current.RequireObject(new(current.SystemId, StorageObjectKind.StorageTier, tierId)).SourceIdentity);

    private static bool MaximumResizeAfterMatches(RealStorageCommand command, WindowsRealStorageTopology before, WindowsRealStorageTopology after,
        string? vdId, string? tierId)
    {
        var id = vdId;
        if (id is null) return false;
        var vd = after.Snapshot.VirtualDisks.Single(item => item.StableId == id);
        var os = after.Snapshot.OsDisks.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
        if (os.Length != 1 || os[0].Size != vd.Size || os[0].PartitionStyle != "RAW" || os[0].IsOffline || os[0].IsBoot || os[0].IsSystem) return false;
        if (command is CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand) return true;
        var old = before.Snapshot.VirtualDisks.Single(item => item.StableId == vd.StableId);
        if (vd.Size <= old.Size || vd.PoolStableId != old.PoolStableId || vd.FriendlyName != old.FriendlyName
            || !SameStringSet(vd.TierStableIds, old.TierStableIds)) return false;
        if (command is ResizeVirtualDiskCommand ordinary)
            return vd.TierStableIds.Count == 0 && vd.Size == ordinary.SizeBytes && vd.ResiliencySettingName == "Simple"
                && vd.ProvisioningType == "Fixed" && vd.NumberOfColumns == 1 && vd.Interleave == 65536;
        if (command is not ResizeTierCommand tierResize || tierId is null) return false;
        var actual = after.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
        var prior = before.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
        var vdObject = after.RequireObject(new(after.SystemId, StorageObjectKind.VirtualDisk, vd.StableId));
        var actualObject = after.RequireObject(new(after.SystemId, StorageObjectKind.StorageTier, actual.StableId));
        return actual.Size == tierResize.SizeBytes && actual.Size > prior.Size && vd.Size == actual.Size
            && actual.PoolStableId == prior.PoolStableId && actual.VirtualDiskStableId == prior.VirtualDiskStableId
            && actual.MediaType == prior.MediaType && actual.MediaType == "HDD"
            && SameStringSet(actual.MemberPhysicalDiskIds, prior.MemberPhysicalDiskIds)
            && SameTierAssociations(before, after, tierId, tierId)
            && TieredLayoutMatches(vdObject, actualObject, vd, actual, actual.Size)
            && before.Snapshot.StorageTiers.Count == after.Snapshot.StorageTiers.Count
            && before.Snapshot.StorageTiers.Where(item => item.StableId != tierId).All(priorTier =>
                after.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == priorTier.StableId) is { } next
                    && MaximumTierUnchanged(priorTier, next)
                    && SameTierAssociations(before, after, priorTier.StableId, next.StableId));
    }

    private static bool MaximumTierUnchanged(StorageTierInfo before, StorageTierInfo after) =>
        before.StableId == after.StableId && before.PoolStableId == after.PoolStableId
        && before.VirtualDiskStableId == after.VirtualDiskStableId && before.FriendlyName == after.FriendlyName
        && before.MediaType == after.MediaType && before.ResiliencySettingName == after.ResiliencySettingName
        && before.Size == after.Size && before.FootprintOnPool == after.FootprintOnPool
        && before.Interleave == after.Interleave && before.NumberOfColumns == after.NumberOfColumns
        && before.NumberOfDataCopies == after.NumberOfDataCopies && before.PhysicalDiskRedundancy == after.PhysicalDiskRedundancy
        && SameStringSet(before.MemberPhysicalDiskIds, after.MemberPhysicalDiskIds);

    internal static bool MaximumOldFactsPreserved(WindowsRealStorageTopology before, WindowsRealStorageTopology after,
        IReadOnlyList<WinPoolSourceObject> previousObjects, IReadOnlySet<string> changedIds)
    {
        var previousIds = previousObjects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in previousObjects)
        {
            var current = after.Facts.Objects.SingleOrDefault(item => item.Id == old.Id);
            if (current is null || current.SourceIdentity != old.SourceIdentity || current.ObjectType != old.ObjectType) return false;
            var capacityChanges = old.ObjectType is FactObjectType.StoragePool or FactObjectType.PhysicalDisk || changedIds.Contains(old.Id);
            bool Skip(string name) => name == "SizeRemaining"
                || old.ObjectType == FactObjectType.PhysicalDisk && name == "VirtualDiskFootprint"
                || capacityChanges && name is
                "Size" or "AllocatedSize" or "FootprintOnPool" or "LargestFreeExtent" or "FreeSpace" or "PhysicalExtents";
            var oldFields = old.Fields.Where(field => !Skip(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            var newFields = current.Fields.Where(field => !Skip(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (oldFields.Length != newFields.Length || oldFields.Where((field, index) => field.Name != newFields[index].Name
                || field.ReadState != newFields[index].ReadState
                || field.Value?.GetRawText() != newFields[index].Value?.GetRawText()).Any()) return false;
        }
        var oldEdges = before.Facts.Relationships.Where(edge => !edge.IsRetained && previousIds.Contains(edge.FromId) && previousIds.Contains(edge.ToId))
            .Select(edge => edge.FromId + "|" + edge.Kind + "|" + edge.ToId).Order(StringComparer.Ordinal).ToArray();
        var newEdges = after.Facts.Relationships.Where(edge => !edge.IsRetained && previousIds.Contains(edge.FromId) && previousIds.Contains(edge.ToId))
            .Select(edge => edge.FromId + "|" + edge.Kind + "|" + edge.ToId).Order(StringComparer.Ordinal).ToArray();
        return oldEdges.SequenceEqual(newEdges, StringComparer.Ordinal);
    }

    private static RealStepResult MaximumUnknown(string code, object evidence) => new(RealStepOutcome.OutcomeUnknown, code, JsonSerializer.Serialize(evidence));
}
