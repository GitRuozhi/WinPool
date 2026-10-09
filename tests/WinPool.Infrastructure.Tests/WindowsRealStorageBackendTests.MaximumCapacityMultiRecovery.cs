using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Theory]
    [InlineData(MaximumCapacityAttemptState.CallIssued)]
    [InlineData(MaximumCapacityAttemptState.OutcomeUnknown)]
    public async Task RejectedMultiSeedCannotHideALaterUncertainAttempt(MaximumCapacityAttemptState laterState)
    {
        var setup = await MultiMaximumFixtureAsync(false);
        var macro = (CreateTieredVirtualDiskCommand)setup.Step.Command;
        var topology = await setup.Fixture.Reader.CaptureAsync(default);
        var closure = topology.RequireExactPhysicalMemberSet(macro.Pool.Existing!.Value,
            setup.Plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray());
        var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var observation = new WindowsMaximumCapacityMultiObservation(topology.InventoryVersion, topology.MachineBinding,
            closure.Fingerprint, closure.PhysicalMemberFingerprint, closure.Objects,
            topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(), closure.Members);
        var inputs = macro.CapacityTiers!.Select(item =>
        {
            var source = topology.RequireObject(item.Tier.Existing!.Value);
            var tier = topology.Snapshot.StorageTiers.Single(value => value.StableId == source.Id);
            return new WindowsStorageTierTarget(source.Id, source.Field("UniqueId")!.Value!.Value.GetString()!,
                source.Field("ObjectId")!.Value!.Value.GetString()!, tier.MediaType,
                MaximumCapacityAlgorithm.SeedBytes(item.MaximumCapacity!.InitialCandidateBytes));
        }).ToArray();
        var target = WindowsRealStorageBackend.BuildMultiMaximumTarget(topology, closure, macro.Pool.Existing!.Value, null)
            with { MaximumCapacityAttempt = true, TierInputs = inputs,
                RelatedUniqueId = inputs[0].UniqueId, RelatedObjectId = inputs[0].ObjectId };
        var attempt = new MaximumCapacityAttempt(1, "seed", MaximumCapacityAttemptPhase.Seed, inputs.Sum(item => item.SizeBytes), 0,
            JsonSerializer.Serialize(target), closure.Fingerprint, closure.PhysicalMemberFingerprint, JsonSerializer.Serialize(observation));
        var provider = new WindowsStorageCommandResult(true, "provider.error-outcome-unknown", null, null, null, null, null, null, "capacity",
            ProviderFailure: new(40000, "cdxml-storagewmi-error-id", "StorageWMI 40000,New-VirtualDisk", null, null, null, null));
        var jobs = new WindowsStorageJobAbsenceEvidence(topology.Facts.InventoryCapturedAt, []);
        var receipt = new WindowsMaximumCapacityMultiAttemptEvidence(provider, observation, observation, observation,
            [jobs, jobs], null, null, "real.maximum.capacity_rejected_unchanged");
        var rejected = new MaximumCapacityAttemptResult(MaximumCapacityAttemptState.CapacityRejectedUnchanged, 0,
            receipt.Code, JsonSerializer.Serialize(receipt));
        var later = attempt with { Ordinal = 2, CandidateBytes = attempt.CandidateBytes - MaximumCapacityAlgorithm.GiB };
        MaximumCapacityAttemptResult? uncertain = laterState == MaximumCapacityAttemptState.OutcomeUnknown
            ? new(laterState, 0, "lost_provider_receipt", "{\"NotVerified\":true}") : null;
        MaximumCapacityAttemptRecord[] records =
        [new(attempt, MaximumCapacityAttemptState.CapacityRejectedUnchanged, rejected, topology.Facts.InventoryCapturedAt),
         new(later, laterState, uncertain, topology.Facts.InventoryCapturedAt)];
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", null, null), records, default);
        Assert.Equal(RealOperationState.OutcomeUnknown, recovered.State);
        Assert.False(recovered.CanReleaseWriteBarrier);
        Assert.Equal("real.maximum.multi_recovery_attempt_after_rejected_seed", recovered.Code);
        Assert.Equal(0, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(5, 6, 3)]
    [InlineData(4, 2, 2)]
    public async Task MultiMaximumReadOnlyRecoveryVerifiesEachBoundaryIncludingTheOriginalIntegerSeed(int initial, int hdd, int ssd)
    {
        var setup = await MultiMaximumFixtureAsync(false, initial, hdd, ssd);
        var execution = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.Verified, execution.Outcome);
        var calls = setup.Fixture.Adapter.CallCount;
        var result = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", setup.Preflight.TargetEvidenceJson, null),
            setup.Journal.Records, default);
        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(Assert.Single(result.Steps).ResultEvidence!)!;
        Assert.Equal((hdd + ssd) * MaximumCapacityAlgorithm.GiB, evidence.MaximumCapacityMulti!.ActualSizeBytes);
        Assert.Equal(new long[] { hdd, ssd }, evidence.MaximumCapacityMulti.Tiers.Select(item => item.ActualSizeBytes / MaximumCapacityAlgorithm.GiB));
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("provider-identity")]
    [InlineData("template-target")]
    [InlineData("seed-success-map")]
    [InlineData("facts-with-copied-fingerprint")]
    public async Task MultiMaximumReadOnlyRecoveryRejectsAlteredDurableSeedEvidence(string alteration)
    {
        var setup = await MultiMaximumFixtureAsync(false, initialGib: 2, hddMaximumGib: 2, ssdMaximumGib: 2);
        var execution = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.Verified, execution.Outcome);
        var calls = setup.Fixture.Adapter.CallCount;
        var row = setup.Journal.Records[0];
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityMultiAttemptEvidence>(row.Result!.ResultEvidenceJson)!;
        if (alteration == "provider-identity") receipt = receipt with { Provider = receipt.Provider with { ObjectId = "other-provider-object" } };
        if (alteration == "facts-with-copied-fingerprint") receipt = receipt with { First = receipt.First! with
            { Objects = receipt.First!.Objects.Select(item => item.Id == receipt.Verified!.CreatedObjectId
                ? item with { SourceIdentity = "another-source-identity" } : item).ToArray() } };
        if (alteration == "template-target")
        {
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(row.Attempt.TargetEvidenceJson)!;
            row = row with { Attempt = row.Attempt with { TargetEvidenceJson = JsonSerializer.Serialize(target with
                { TierInputs = target.TierInputs!.Select((tier, index) => index == 0 ? tier with { ObjectId = "other-template" } : tier).ToArray() }) } };
        }
        var result = row.Result! with { ResultEvidenceJson = JsonSerializer.Serialize(receipt) };
        if (alteration == "seed-success-map") result = result with { SeedSuccessfulBytes = result.SeedSuccessfulBytes!
            .ToDictionary(pair => pair.Key, pair => pair.Value + MaximumCapacityAlgorithm.GiB, StringComparer.Ordinal) };
        setup.Journal.Records[0] = row with { Result = result };
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", null, null), setup.Journal.Records, default);
        Assert.Equal(RealOperationState.OutcomeUnknown, recovered.State);
        Assert.False(recovered.CanReleaseWriteBarrier);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiMaximumStoppedAfterSeedOnlyReleasesAnExactlyUnchangedResidual(bool residualChanged)
    {
        var setup = await MultiMaximumFixtureAsync(false);
        var journal = new StopAfterVerifiedSeedJournal(setup.Journal);
        var execution = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, execution.Outcome);
        Assert.Single(setup.Journal.Records);
        Assert.Equal(1, setup.Fixture.Adapter.CallCount);
        if (residualChanged)
        {
            var original = setup.Fixture.SnapshotTransform!;
            setup.Fixture.SnapshotTransform = snapshot =>
            {
                var current = original(snapshot);
                return current with { VirtualDisks = current.VirtualDisks.Select(item => item with
                    { FriendlyName = "changed outside the search" }).ToArray() };
            };
        }
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "stopped_after_seed", null, execution.ResultEvidenceJson),
            setup.Journal.Records, default);
        Assert.Equal(residualChanged ? RealOperationState.OutcomeUnknown : RealOperationState.Failed, recovered.State);
        Assert.Equal(!residualChanged, recovered.CanReleaseWriteBarrier);
        if (!residualChanged)
        {
            using var evidence = JsonDocument.Parse(Assert.Single(recovered.Steps).ResultEvidence!);
            Assert.True(evidence.RootElement.GetProperty("NotVerified").GetBoolean());
            Assert.False(evidence.RootElement.GetProperty("MaximumFound").GetBoolean());
            Assert.False(evidence.RootElement.GetProperty("AutomaticContinuation").GetBoolean());
        }
        Assert.Equal(1, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("physical-footprint", true)]
    [InlineData("pool-footprint", false)]
    [InlineData("physical-uid", false)]
    [InlineData("physical-object-id", false)]
    public async Task MultiMaximumRecoveryAllowsPhysicalAllocationFootprintButPreservesPoolAndMemberIdentity(
        string alteration, bool canRelease)
    {
        var setup = await MultiMaximumFixtureAsync(false);
        var execution = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, new StopAfterVerifiedSeedJournal(setup.Journal), default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, execution.Outcome);
        var row = Assert.Single(setup.Journal.Records);
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityMultiAttemptEvidence>(row.Result!.ResultEvidenceJson)!;
        Assert.Contains(receipt.Before.Objects, item => item.ObjectType == FactObjectType.PhysicalDisk
            && item.Field("VirtualDiskFootprint")!.Value!.Value.GetInt64() == 0);
        Assert.Contains(receipt.First!.Objects, item => item.ObjectType == FactObjectType.PhysicalDisk
            && item.Field("VirtualDiskFootprint")!.Value!.Value.GetInt64() > 0);
        var calls = setup.Fixture.Adapter.CallCount;
        if (!canRelease)
        {
            var original = setup.Fixture.FactsTransform!;
            setup.Fixture.FactsTransform = item =>
            {
                item = original(item);
                var selected = alteration == "pool-footprint"
                    ? item.ObjectType == FactObjectType.StoragePool && item.Id == "pool:returned"
                    : item.ObjectType == FactObjectType.PhysicalDisk && item.Id == PhysicalId;
                if (!selected) return item;
                var name = alteration == "pool-footprint" ? "VirtualDiskFootprint"
                    : alteration == "physical-uid" ? "UniqueId" : "ObjectId";
                var field = alteration == "pool-footprint"
                    ? WinPoolSourceField.Returned(name, MaximumCapacityAlgorithm.GiB, FactValueType.UInt64, item.SourceRef)
                    : WinPoolSourceField.Returned(name, "changed-provider-identity", FactValueType.String, item.SourceRef);
                return item with { Fields = item.Fields.Where(value => value.Name != name).Append(field).ToImmutableArray() };
            };
            // Recompute the observation through the production reader/closure, so
            // rejection cannot rely on a copied fingerprint for changed facts.
            var topology = await setup.Fixture.Reader.CaptureAsync(default);
            var macro = (CreateTieredVirtualDiskCommand)setup.Step.Command;
            var closure = topology.RequireExactPhysicalMemberSet(macro.Pool.Existing!.Value,
                setup.Plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray());
            var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var changed = new WindowsMaximumCapacityMultiObservation(topology.InventoryVersion, topology.MachineBinding,
                closure.Fingerprint, closure.PhysicalMemberFingerprint, closure.Objects,
                topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId)).ToArray(),
                closure.Members);
            receipt = receipt with { First = changed,
                Verified = receipt.Verified! with { PostFingerprint = closure.Fingerprint } };
            setup.Journal.Records[0] = row with { Result = row.Result! with { ResultEvidenceJson = JsonSerializer.Serialize(receipt) } };
        }
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "stopped_after_seed", null, execution.ResultEvidenceJson),
            setup.Journal.Records, default);
        Assert.Equal(canRelease ? RealOperationState.Failed : RealOperationState.OutcomeUnknown, recovered.State);
        Assert.Equal(canRelease, recovered.CanReleaseWriteBarrier);
        Assert.Equal(canRelease ? "real.maximum.multi_stopped_without_maximum"
            : "real.maximum.multi_recovery_verified_receipt_invalid", recovered.Code);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    private sealed class StopAfterVerifiedSeedJournal(IMaximumCapacityAttemptJournal inner) : IMaximumCapacityAttemptJournal
    {
        public bool IsStopRequested { get; private set; }
        public Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task<bool> PrepareAsync(MaximumCapacityAttempt value, CancellationToken ct) => inner.PrepareAsync(value, ct);
        public Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken ct) => inner.MarkCallIssuedAsync(ordinal, ct);
        public async Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult value, CancellationToken ct)
        {
            var accepted = await inner.CompleteAsync(ordinal, value, ct);
            if (accepted && ordinal == 1 && value.State == MaximumCapacityAttemptState.Verified) IsStopRequested = true;
            return accepted;
        }
    }
}
