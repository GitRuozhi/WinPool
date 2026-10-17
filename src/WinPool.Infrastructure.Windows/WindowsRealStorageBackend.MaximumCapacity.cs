using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsMaximumCapacityObservation(string InventoryVersion, string MachineBinding,
    string Fingerprint, string PhysicalMemberFingerprint, IReadOnlyList<WinPoolSourceObject> Objects,
    IReadOnlyList<WinPoolFactRelationship> Associations, WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence, string? SubsystemIdentity = null);

public sealed record WindowsMaximumCapacityAttemptEvidence(WindowsStorageCommandResult Provider,
    WindowsMaximumCapacityObservation Before, WindowsMaximumCapacityObservation? First,
    WindowsMaximumCapacityObservation? Second, IReadOnlyList<WindowsStorageJobAbsenceEvidence> StorageJobQueries,
    WindowsVerifiedStepEvidence? Verified, string Code)
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? ObservationDiagnostic { get; init; }
    [JsonIgnore]
    internal WindowsRealStorageTopology? ObservedTopology { get; init; }
}

public sealed record WindowsMaximumCapacitySearchEvidence(string AlgorithmVersion, long ActualSizeBytes,
    int AttemptCount, int LastVerifiedOrdinal, int CapacityBoundaryOrdinal,
    string VirtualDiskId, string OsDiskId, string? ActualTierId, string BoundaryCode);

public sealed partial class WindowsRealStorageBackend
{
    internal static bool IsMaximumCapacityMacro(RealStorageCommand command) => command is CreateVirtualDiskCommand { UseMaximumSize: true }
        || command is CreateTieredVirtualDiskCommand tiered && (tiered.UseMaximumSize
            || tiered.CapacityTiers?.Any(item => item.UsesMaximumSize(tiered.UseMaximumSize)) == true);

    public async Task<RealStepResult> ExecuteMaximumCapacitySearchAsync(OperationPlan plan, RealOperationStep step,
        RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var stored = RequireFrozenStep(plan, step);
        if (!IsMaximumCapacityMacro(stored.Command)) throw new InvalidDataException("A frozen MAX search is required.");
        if (stored.Command is CreateTieredVirtualDiskCommand tieredMacro
            && (tieredMacro.CapacityTiers is not null || WindowsRealOperationPlanner.UsesExactMemberSet(plan)))
        {
            var normalized = tieredMacro with { CapacityTiers = WindowsRealOperationPlanner.TierInputs(tieredMacro) };
            return await ExecuteMultiMaximumCapacityAsync(plan, stored, normalized, preflight, journal, cancellationToken).ConfigureAwait(false);
        }
        var policy = stored.Command switch
        {
            CreateVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: { } value } => value,
            CreateTieredVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: { } value } => value,
            _ => throw new InvalidDataException("An Agent-frozen MAX macro is required.")
        };
        if ((await journal.ReadAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
            return MaximumUnknown("real.maximum.existing_attempts_never_replayed", new { plan.OperationId, step.Id });
        var expectedFingerprint = preflight.TargetFingerprint;
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
            var candidate = new MaximumAttemptCandidate(++ordinal,
                stored.Command is CreateTieredVirtualDiskCommand tiered ? tiered.Tier.Existing!.Value.ProviderKey : "virtual-disk",
                vdId is null ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize,
                state.CandidateBytes, state.LastSuccessfulBytes, expectedFingerprint, vdId, osId, actualTierId, lastTopology);
            var execution = await ExecuteMaximumAttemptAsync(plan, stored, preflight, candidate, journal, cancellationToken).ConfigureAwait(false);
            if (execution.Receipt is not { } result) return MaximumAttemptUnknown(execution);
            var success = result.Verified is not null;
            var rejected = result.Code == "real.maximum.capacity_rejected_unchanged";
            if (!success && !rejected) return MaximumAttemptUnknown(result);
            if (success)
            {
                lastVerified = result.Verified!; lastVerifiedOrdinal = ordinal;
                vdId ??= lastVerified.CreatedObjectId;
                osId ??= lastVerified.CreatedOsDiskId;
                actualTierId ??= lastVerified.TieredCreation?.TierInstanceStableId;
                if (vdId is null || osId is null) return new(RealStepOutcome.OutcomeUnknown,
                    "real.maximum.actual_mapping_missing", result.EvidenceJson);
                expectedFingerprint = lastVerified.PostFingerprint;
                lastTopology = result.ObservedTopology
                    ?? throw new InvalidDataException("Verified MAX observation has no Agent-local facts.");
                RequireMaximumInitialIdentity(plan, lastTopology, MaximumComponentClosure(plan, lastTopology), expectedFingerprint);
            }
            else { boundaryOrdinal = ordinal; expectedFingerprint = result.PostFingerprint!; lastTopology = result.ObservedTopology; }
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
        return new(RealStepOutcome.Verified, "real.maximum.integer_gib_boundary_verified", JsonSerializer.Serialize(finalEvidence), vdId)
        { VerifiedPostState = lastTopology is null ? null : PostState(lastTopology) };
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
        CreateVirtualDiskCommand value => ValidMaximumLayout(value.EffectiveLayout),
        CreateTieredVirtualDiskCommand value => value.CreationMechanism == TieredVirtualDiskCreationMechanism.ExactTemplate
            && before.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == value.Tier.Existing?.ProviderKey)
                is { MediaType: "HDD", ResiliencySettingName: "Simple", NumberOfColumns: 1, Interleave: 65536 },
        ResizeVirtualDiskCommand value => before.Snapshot.VirtualDisks.SingleOrDefault(item => item.StableId == value.VirtualDisk.Existing?.ProviderKey)
            is { ResiliencySettingName: "Simple", ProvisioningType: "Fixed", NumberOfColumns: 1, Interleave: 65536 },
        ResizeTierCommand => before.Snapshot.StorageTiers.SingleOrDefault(item => item.StableId == tierId)
            is { MediaType: "HDD", ResiliencySettingName: "Simple", NumberOfColumns: 1, Interleave: 65536 },
        _ => false
    };

    private static bool ValidMaximumLayout(StorageLayoutParameters layout)
    {
        try { layout.Validate(); return true; } catch (ArgumentException) { return false; }
    }

    private static RealTargetClosure MaximumComponentClosure(OperationPlan plan, WindowsRealStorageTopology topology) =>
        WindowsRealOperationPlanner.UsesExactMemberSet(plan)
            ? WindowsRealOperationPlanner.ResolveExactMemberSet(topology, plan.Targets).AsComponentClosure()
            : topology.RequireSinglePhysicalClosure([plan.Targets.Single(item => item.Kind == StorageObjectKind.PhysicalDisk)]);

    private async Task ValidateMaximumSafetyAsync(OperationPlan plan, WindowsRealStorageTopology topology, RealTargetClosure closure,
        RealStorageCommand command, CancellationToken ct)
    {
        if (WindowsRealOperationPlanner.UsesExactMemberSet(plan))
            _ = await planner.ValidateMultiMaximumSafetyAsync(topology, WindowsRealOperationPlanner.ResolveExactMemberSet(topology, plan.Targets), command, ct).ConfigureAwait(false);
        else
            RequirePoolMemberRoleProof(topology, closure, await planner.ValidatePartitionSafetyWithEvidenceAsync(topology, closure, command, ct).ConfigureAwait(false));
    }

    private bool MaximumTerminalJobs(WindowsStorageJobAbsenceEvidence value) => value.ObservedAtUtc != default
        && value.ObservedAtUtc <= timeProvider.GetUtcNow().AddSeconds(10) && timeProvider.GetUtcNow() - value.ObservedAtUtc <= TimeSpan.FromMinutes(2)
        && value.Jobs is not null && value.Jobs.All(job => !string.IsNullOrWhiteSpace(job.UniqueId) && !string.IsNullOrWhiteSpace(job.ObjectId) && job.JobState is 7 or 8 or 9 or 10)
        && value.Jobs.Select(job => job.UniqueId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count
        && value.Jobs.Select(job => job.ObjectId).Distinct(StringComparer.Ordinal).Count() == value.Jobs.Count;

    private static WindowsMaximumCapacityObservation MaximumObservation(WindowsRealStorageTopology topology, RealTargetClosure closure, bool exact = false)
    {
        var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return new(topology.InventoryVersion, topology.MachineBinding, closure.Fingerprint, closure.PhysicalMemberFingerprint,
            closure.Objects, topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(), closure.PoolMemberRoleEvidence, exact ? MaximumSubsystemIdentity(topology, closure) : null);
    }

    private static string MaximumSubsystemIdentity(WindowsRealStorageTopology topology, RealTargetClosure closure)
    {
        var memberIds = closure.Objects.Where(item => item.ObjectType == FactObjectType.PhysicalDisk).Select(item => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, item.Id)).ToArray();
        var exact = WindowsRealOperationPlanner.ResolveExactMemberSet(topology, memberIds);
        return "\nsubsystem|" + exact.SubsystemStableId + "|" + exact.SubsystemUniqueId + "|" + exact.SubsystemObjectId;
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
            return vd.TierStableIds.Count == 0 && vd.Size == ordinary.SizeBytes && vd.ProvisioningType == "Fixed"
                && vd.ResiliencySettingName == old.ResiliencySettingName && vd.NumberOfColumns == old.NumberOfColumns && vd.Interleave == old.Interleave
                && new[] { "ResiliencySettingName", "NumberOfColumns", "Interleave", "NumberOfDataCopies", "PhysicalDiskRedundancy" }.All(name =>
                    before.RequireObject(new(before.SystemId, StorageObjectKind.VirtualDisk, old.StableId)).Field(name)?.Value?.ToString()
                    == after.RequireObject(new(after.SystemId, StorageObjectKind.VirtualDisk, vd.StableId)).Field(name)?.Value?.ToString());
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
