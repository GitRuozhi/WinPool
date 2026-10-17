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
    IReadOnlyList<WindowsExactPhysicalMemberRoleEvidence> Members, string? SubsystemIdentity = null);
public sealed record WindowsMaximumCapacityMultiAttemptEvidence(WindowsStorageCommandResult Provider,
    WindowsMaximumCapacityMultiObservation Before, WindowsMaximumCapacityMultiObservation? First,
    WindowsMaximumCapacityMultiObservation? Second, IReadOnlyList<WindowsStorageJobAbsenceEvidence> StorageJobQueries,
    WindowsVerifiedStepEvidence? Verified, IReadOnlyDictionary<string, string>? ActualTierIds, string Code)
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? ObservationDiagnostic { get; init; }
    [JsonIgnore]
    internal WindowsRealStorageTopology? ObservedTopology { get; init; }
}

public sealed partial class WindowsRealStorageBackend
{
    private async Task<RealStepPreflight> PreflightMultiMaximumCapacityAsync(OperationPlan plan, RealOperationStep step,
        WindowsRealStorageTopology topology, CancellationToken ct)
    {
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        var closure = WindowsRealOperationPlanner.RequireMultiMaximumClosure(topology, plan.Targets, command);
        RequireMultiMaximumIdentity(plan, topology, closure, plan.RealOperation!.TargetFingerprint);
        var safety = await planner.ValidateMultiMaximumSafetyAsync(topology, closure, command, ct).ConfigureAwait(false);
        RequireMultiMaximumRaw(topology, closure);
        if (topology.Snapshot.VirtualDisks.Any(item => item.PoolStableId == closure.PoolId))
            throw new InvalidDataException("Multi MAX initial pool is no longer empty.");
        foreach (var tier in command.CapacityTiers!.Where(item => item.UsesMaximumSize(command.UseMaximumSize)))
            if (tier.MaximumCapacity is not { AlgorithmVersion: MaximumCapacityAlgorithm.Version } policy
                || policy.SourceFingerprint != closure.Fingerprint || policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes))
                throw new InvalidDataException("Multi MAX policy differs from frozen exact closure.");
        operationScopes[plan.OperationId] = StorageInventoryScopeFactory.Create(topology.Facts, plan.OperationId, step.Id,
            plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray());
        var target = BuildMultiMaximumTarget(topology, closure, command.Pool.Existing!.Value, null);
        return new(JsonSerializer.Serialize(target), closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint)
        { CallContext = new CallContext(plan.OperationId, step.Id, topology, target, safety) };
    }

    private async Task<RealStepResult> ExecuteMultiMaximumCapacityAsync(OperationPlan plan, RealOperationStep step,
        CreateTieredVirtualDiskCommand macro, RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken ct)
    {
        if ((await journal.ReadAsync(ct).ConfigureAwait(false)).Count != 0)
            return MaximumUnknown("real.maximum.existing_attempts_never_replayed", new { plan.OperationId });
        var tiers = macro.CapacityTiers!;
        var seeds = tiers.ToDictionary(item => item.Tier.Existing!.Value.ProviderKey,
            item => item.UsesMaximumSize(macro.UseMaximumSize) ? MaximumCapacityAlgorithm.SeedBytes(item.MaximumCapacity!.InitialCandidateBytes)
                : item.SizeBytes ?? throw new InvalidDataException("Explicit tier bytes absent."), StringComparer.Ordinal);
        var actualIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(seeds, StringComparer.Ordinal);
        var boundaries = new List<WindowsMaximumCapacityTierBoundary>();
        var expected = preflight.TargetFingerprint;
        string? vdId = null, osId = null;
        var ordinal = 0;
        WindowsVerifiedStepEvidence? lastVerified = null;
        WindowsRealStorageTopology? lastObserved = null;
        if (tiers.Count > 1)
        {
            var seedTotal = seeds.Values.Aggregate(0L, (sum, value) => checked(sum + value));
            var seed = await AttemptAsync("seed", MaximumCapacityAttemptPhase.Seed, seedTotal, 0, sizes, null).ConfigureAwait(false);
            if (seed is not { Verified: not null, ActualTierIds: not null }) return MaximumAttemptUnknown(seed);
            lastVerified = seed.Verified; vdId = lastVerified.CreatedObjectId; osId = lastVerified.CreatedOsDiskId;
            foreach (var pair in seed.ActualTierIds) actualIds.Add(pair.Key, pair.Value);
        }
        foreach (var item in tiers.Where(item => item.UsesMaximumSize(macro.UseMaximumSize)))
        {
            var key = item.Tier.Existing!.Value.ProviderKey;
            var state = MaximumCapacitySearchState.Start(item.MaximumCapacity!.InitialCandidateBytes, tiers.Count > 1 ? seeds[key] : 0);
            var verifiedOrdinal = 1; var boundaryOrdinal = 0; long boundaryBytes = 0; var attemptsForTier = 0;
            while (!state.IsComplete)
            {
                if (++attemptsForTier > item.MaximumCapacity.MaximumAttempts)
                    return MaximumUnknown("real.maximum.attempt_budget_without_boundary", new { key, state.LastSuccessfulBytes });
                var requested = new Dictionary<string, long>(sizes, StringComparer.Ordinal) { [key] = state.CandidateBytes };
                var receipt = await AttemptAsync(key, vdId is null ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize, state.CandidateBytes,
                    state.LastSuccessfulBytes, requested, actualIds.GetValueOrDefault(key)).ConfigureAwait(false);
                var success = receipt.Verified is not null;
                if (!success && receipt.Code != "real.maximum.capacity_rejected_unchanged") return MaximumAttemptUnknown(receipt);
                if (success)
                {
                    sizes[key] = state.CandidateBytes; lastVerified = receipt.Verified; verifiedOrdinal = ordinal;
                    vdId ??= lastVerified!.CreatedObjectId; osId ??= lastVerified!.CreatedOsDiskId;
                    foreach (var pair in receipt.ActualTierIds!) actualIds[pair.Key] = pair.Value;
                }
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
            JsonSerializer.Serialize(lastVerified! with { PostFingerprint = expected, MaximumCapacityMulti = summary }), vdId)
        { VerifiedPostState = lastObserved is null ? null : PostState(lastObserved) };

        async Task<MaximumAttemptReceipt> AttemptAsync(string key, MaximumCapacityAttemptPhase phase,
            long bytes, long lastSuccess, IReadOnlyDictionary<string, long> requested, string? actualTier)
        {
            var candidate = new MaximumAttemptCandidate(++ordinal, key, phase, bytes, lastSuccess, expected,
                vdId, osId, actualTier, lastObserved, macro, requested,
                new Dictionary<string, string>(actualIds, StringComparer.Ordinal));
            var execution = await ExecuteMaximumAttemptAsync(plan, step, preflight, candidate, journal, ct).ConfigureAwait(false);
            if (execution.Receipt is not { } receipt)
                return new(null, execution.FailureCode!, execution.FailureEvidenceJson ?? "{}", null, null);
            if (receipt.Verified is not null || receipt.Code == "real.maximum.capacity_rejected_unchanged")
                expected = receipt.PostFingerprint!;
            if (receipt.ObservedTopology is { } observed) lastObserved = observed;
            return receipt;
        }
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
        var layouts = macro.CapacityTiers!.Select(item => item.Layout ?? StorageLayoutParameters.LegacyDefault).Distinct().ToArray();
        if (!layouts.All(layout => MaximumAggregateLayoutMatches(source, layout))) return null;
        var total = sizes.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        var os = topology.Snapshot.OsDisks.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
        if (vd.FriendlyName != macro.Name || os.Length != 1 || os[0].PartitionStyle != "RAW" || os[0].IsBoot || os[0].IsSystem || os[0].IsOffline
            || topology.Snapshot.Partitions.Any(item => item.OsDiskStableId == os[0].StableId) || (osId is not null && os[0].StableId != osId) || os[0].Size != total || vd.Size != total
            || vd.FootprintOnPool < total || vd.TierStableIds.Count != sizes.Count) return null;
        var actual = topology.Snapshot.StorageTiers.Where(item => item.VirtualDiskStableId == vd.StableId).ToArray();
        if (actual.Length != sizes.Count || topology.Snapshot.StorageTiers.Count(item => item.PoolStableId == closure.PoolId) != 2 * sizes.Count) return null;
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        long actualFootprint = 0;
        foreach (var item in macro.CapacityTiers!)
        {
            var template = topology.Snapshot.StorageTiers.Single(value => value.StableId == item.Tier.Existing!.Value.ProviderKey);
            var matches = actual.Where(value => value.MediaType == template.MediaType).ToArray();
            if (matches.Length != 1) return null;
            var tier = matches[0];
            var layout = item.Layout ?? WindowsRealOperationPlanner.LayoutOf(template);
            if (!RequestedTierLayoutMatches(template, layout) || tier.PoolStableId != closure.PoolId || tier.Size != sizes[template.StableId]
                || !FootprintMatchesLayout(tier.Size, tier.FootprintOnPool, layout) || !RequestedTierLayoutMatches(tier, layout)
                || (priorIds.TryGetValue(template.StableId, out var prior) && prior != tier.StableId)) return null;
            var tierSource = topology.RequireObject(new(topology.SystemId, StorageObjectKind.StorageTier, tier.StableId));
            if (!SourceLayoutMatches(tierSource, layout) || !ReturnedNumber(tierSource, "ProvisioningType", 2) || !ReturnedNumber(tierSource, "AllocatedSize", tier.Size)
                || !ReturnedNumber(source, "AllocatedSize", total)) return null;
            var allowed = WindowsRealOperationPlanner.DataMembers(topology, closure, template.MediaType).Select(member => member.StableId).ToHashSet(StringComparer.Ordinal);
            if (tier.MemberPhysicalDiskIds.Count < RequiredAllocatedMembers(layout) || tier.MemberPhysicalDiskIds.Distinct(StringComparer.Ordinal).Count() != tier.MemberPhysicalDiskIds.Count
                || tier.MemberPhysicalDiskIds.Any(id => !allowed.Contains(id))) return null;
            actualFootprint = checked(actualFootprint + tier.FootprintOnPool);
            mapping.Add(template.StableId, tier.StableId);
        }
        if (vd.FootprintOnPool != actualFootprint || !ReturnedNumber(source, "FootprintOnPool", actualFootprint)) return null;
        return (vd.StableId, os[0].StableId, mapping);
    }

    private static long RequiredAllocatedMembers(StorageLayoutParameters layout) =>
        layout.ResiliencySettingName == "Mirror" ? (long)layout.NumberOfColumns * layout.NumberOfDataCopies : layout.NumberOfColumns;

    private static CreateTieredVirtualDiskCommand ExplicitTierAttempt(CreateTieredVirtualDiskCommand macro, IReadOnlyDictionary<string,long> sizes) => macro with {
        SizeBytes = sizes.Values.Aggregate(0L, (sum, bytes) => checked(sum + bytes)), UseMaximumSize = false, MaximumCapacity = null,
        CapacityTiers = macro.CapacityTiers!.Select(item => item with { SizeBytes = sizes[item.Tier.Existing!.Value.ProviderKey], UseMaximumSize = false, MaximumCapacity = null }).ToArray() };

    internal static bool MaximumAggregateLayoutMatches(WinPoolSourceObject source, StorageLayoutParameters? layout = null)
    {
        var provisioning = source.Field("ProvisioningType");
        if (provisioning is not { ReadState: FieldReadState.Returned }
            || provisioning.Value is { } value && value.ValueKind != JsonValueKind.Null && !ReturnedNumber(source, "ProvisioningType", 2)) return false;
        return SourceLayoutMatches(source, layout ?? StorageLayoutParameters.LegacyDefault, aggregate: true);
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
            topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(), closure.Members, "\nsubsystem|" + closure.SubsystemStableId + "|" + closure.SubsystemUniqueId + "|" + closure.SubsystemObjectId);
    }
}
