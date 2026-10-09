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
    [InlineData(3, new long[] { 5, 4, 3 })]
    [InlineData(6, new long[] { 5, 6, 7 })]
    public async Task MaximumMacroSearchesExactWholeGibAndReadOnlyRecoveryNeverReplays(int supportedGib, long[] expectedGib)
    {
        var setup = await MaximumFixtureAsync(supportedGib);
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(expectedGib, setup.Journal.Records.Select(item => item.Attempt.CandidateBytes / MaximumCapacityAlgorithm.GiB));
        Assert.Equal(expectedGib.Length, setup.Fixture.Adapter.CallCount);
        Assert.All(setup.Journal.Records, record => Assert.Contains(record.State,
            new[] { MaximumCapacityAttemptState.Verified, MaximumCapacityAttemptState.CapacityRejectedUnchanged }));
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!;
        Assert.Equal(supportedGib * MaximumCapacityAlgorithm.GiB, evidence.MaximumCapacity!.ActualSizeBytes);
        var parent = new RealOperationStepProgress(setup.Step.Id, RealOperationStepState.OutcomeUnknown,
            "lost_parent_receipt", setup.Preflight.TargetEvidenceJson, null);
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            parent, setup.Journal.Records, default);
        Assert.True(recovered.State == RealOperationState.Succeeded, recovered.Code);
        Assert.True(recovered.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, Assert.Single(recovered.Steps).State);
        Assert.Equal(expectedGib.Length, setup.Fixture.Adapter.CallCount);
        var replay = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, replay.Outcome);
        Assert.Equal(expectedGib.Length, setup.Fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task MaximumMacroUnknownFailureStopsAtTheOriginalCandidate()
    {
        var setup = await MaximumFixtureAsync(0);
        setup.Fixture.Adapter.ResultTransform = result => result with
        {
            Code = "provider.error-outcome-unknown", UniqueId = null, ObjectId = null,
            DiskNumber = null, ProviderFailure = null
        };
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, setup.Fixture.Adapter.CallCount);
        Assert.Equal(MaximumCapacityAttemptState.OutcomeUnknown, Assert.Single(setup.Journal.Records).State);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("template")]
    [InlineData("machine")]
    [InlineData("facts")]
    [InlineData("result-state")]
    public async Task MaximumRecoveryRejectsAlteredHistoricalProofWithoutCallingWindows(string alteration)
    {
        var setup = await MaximumFixtureAsync(3);
        Assert.Equal(RealStepOutcome.Verified, (await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(
            setup.Plan, setup.Step, setup.Preflight, setup.Journal, default)).Outcome);
        var index = setup.Journal.Records.FindIndex(item => item.State == MaximumCapacityAttemptState.Verified);
        var record = setup.Journal.Records[index];
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(record.Result!.ResultEvidenceJson)!;
        receipt = alteration switch
        {
            "provider" => receipt with { Provider = receipt.Provider with { UniqueId = "foreign" } },
            "template" => receipt with { Verified = receipt.Verified! with { TieredCreation = receipt.Verified.TieredCreation! with { TemplateUniqueId = "foreign" } } },
            "machine" => receipt with { First = receipt.First! with { MachineBinding = "foreign" } },
            "facts" => receipt with { First = receipt.First! with { Objects = receipt.First.Objects.Skip(1).ToArray() } },
            _ => receipt
        };
        setup.Journal.Records[index] = record with { Result = record.Result with
        {
            State = alteration == "result-state" ? MaximumCapacityAttemptState.OutcomeUnknown : record.Result.State,
            ResultEvidenceJson = JsonSerializer.Serialize(receipt)
        } };
        var calls = setup.Fixture.Adapter.CallCount;
        var parent = new RealOperationStepProgress(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost", setup.Preflight.TargetEvidenceJson, null);
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step, parent, setup.Journal.Records, default);
        Assert.Equal(RealOperationState.OutcomeUnknown, recovered.State);
        Assert.False(recovered.CanReleaseWriteBarrier);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(FactObjectType.PhysicalDisk, true)]
    [InlineData(FactObjectType.StoragePool, false)]
    public async Task MaximumOldFactsAllowsVirtualDiskFootprintChangesOnlyOnPhysicalDisks(FactObjectType objectType, bool expected)
    {
        var setup = await MaximumFixtureAsync(3);
        var original = setup.Fixture.FactsTransform!;
        long footprint = 0;
        setup.Fixture.FactsTransform = item =>
        {
            item = original(item);
            return item.ObjectType != objectType ? item : item with
            {
                Fields = item.Fields.Where(field => field.Name != "VirtualDiskFootprint").Append(
                    WinPoolSourceField.Returned("VirtualDiskFootprint", footprint, FactValueType.UInt64, item.SourceRef)).ToImmutableArray()
            };
        };
        var before = await setup.Fixture.Reader.CaptureAsync(default);
        var closure = before.RequireSinglePhysicalClosure([setup.Fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]);
        footprint = MaximumCapacityAlgorithm.GiB;
        var after = await setup.Fixture.Reader.CaptureAsync(default);
        Assert.Equal(expected, WindowsRealStorageBackend.MaximumOldFactsPreserved(before, after, closure.Objects, new HashSet<string>()));
    }

    [Theory]
    [InlineData("SerialNumber")]
    [InlineData("UniqueId")]
    [InlineData("ObjectId")]
    public async Task MaximumOldFactsStillRejectsChangedPhysicalIdentityWithNewFootprint(string identityField)
    {
        var setup = await MaximumFixtureAsync(3);
        var before = await setup.Fixture.Reader.CaptureAsync(default);
        var closure = before.RequireSinglePhysicalClosure([setup.Fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]);
        var original = setup.Fixture.FactsTransform!;
        setup.Fixture.FactsTransform = item =>
        {
            item = MaximumPhysicalFootprint(original(item), MaximumCapacityAlgorithm.GiB);
            return item.ObjectType != FactObjectType.PhysicalDisk ? item : item with
            {
                Fields = item.Fields.Select(field => field.Name == identityField
                    ? WinPoolSourceField.Returned(identityField, "foreign-identity", FactValueType.String, item.SourceRef) : field).ToImmutableArray()
            };
        };
        var after = await setup.Fixture.Reader.CaptureAsync(default);
        Assert.False(WindowsRealStorageBackend.MaximumOldFactsPreserved(before, after, closure.Objects, new HashSet<string>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaximumPostconditionTimeoutRetainsFreshFactsAndFailureReason(bool captureException)
    {
        var setup = await MaximumFixtureAsync(6, expirePostCallWindow: true);
        if (captureException)
            setup.Fixture.CompleteFactsTransform = facts => setup.Journal.Records.Count == 0 ? facts
                : throw new InvalidDataException("fixture fresh capture failed");
        var original = setup.Fixture.FactsTransform!;
        setup.Fixture.FactsTransform = item =>
        {
            item = original(item);
            return setup.Journal.Records.Count == 0 || item.ObjectType != FactObjectType.PhysicalDisk ? item : item with
            {
                Fields = item.Fields.Select(field => field.Name == "FirmwareVersion"
                    ? WinPoolSourceField.Returned(field.Name, "changed-firmware", FactValueType.String, item.SourceRef) : field).ToImmutableArray()
            };
        };
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, setup.Fixture.Adapter.CallCount);
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(Assert.Single(setup.Journal.Records).Result!.ResultEvidenceJson)!;
        Assert.Equal("real.maximum.postcondition_unverified", receipt.Code);
        if (captureException)
        {
            Assert.Null(receipt.First);
            Assert.Contains("System.IO.InvalidDataException: fixture fresh capture failed", receipt.ObservationDiagnostic);
        }
        else
        {
            Assert.NotNull(receipt.First);
            Assert.Contains(receipt.First.Objects, item => item.Id == "vd:tiered");
            Assert.Contains("MaximumOldFactsPreserved", receipt.ObservationDiagnostic);
        }
        Assert.Null(receipt.Verified);
    }

    private static WinPoolSourceObject MaximumPhysicalFootprint(WinPoolSourceObject item, long footprint) =>
        item.ObjectType != FactObjectType.PhysicalDisk ? item : item with
        {
            Fields = item.Fields.Where(field => field.Name != "VirtualDiskFootprint").Append(
                WinPoolSourceField.Returned("VirtualDiskFootprint", footprint, FactValueType.UInt64, item.SourceRef)).ToImmutableArray()
        };

    private static async Task<(Fixture Fixture, OperationPlan Plan, RealOperationStep Step, RealStepPreflight Preflight,
        MaximumMemoryJournal Journal)> MaximumFixtureAsync(int supportedGib, bool expirePostCallWindow = false)
    {
        var fixture = new Fixture(storageJobs: new SyntheticStorageJobReader(), maximumCapacityClock: true, expirePostCallWindow: expirePostCallWindow);
        long footprint = 0;
        fixture.Safety.ReturnFreshPoolRoles = true;
        StorageSnapshot Snapshot(StorageSnapshot snapshot, bool created, long size)
        {
            var pooled = TieredCreationSnapshot(snapshot, created, size);
            return pooled with { PhysicalDisks = pooled.PhysicalDisks.Select(item => item with { Size = 100 * MaximumCapacityAlgorithm.GiB }).ToArray() };
        }
        fixture.SnapshotTransform = snapshot => Snapshot(snapshot, false, 0);
        fixture.FactsTransform = item => MaximumPhysicalFootprint(TieredCreationFacts(item), footprint);
        var topology = await fixture.Reader.CaptureAsync(default);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var pool = fixture.Id(StorageObjectKind.StoragePool, "pool:returned");
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:template");
        var closure = topology.RequireSinglePhysicalClosure([physical]);
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, 6 * MaximumCapacityAlgorithm.GiB,
            5 * MaximumCapacityAlgorithm.GiB, 100 * MaximumCapacityAlgorithm.GiB, 204, "fixture provider", closure.Fingerprint,
            topology.Facts.InventoryCapturedAt);
        var step = new RealOperationStep("max", new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool),
            RealTargetReference.ForExisting(tier), "Tiered VD", 0, true, MaximumCapacity: policy), [],
            "Exact fixture template", "Whole GiB MAX", "Allocations remain", "Fixture provider proof");
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, topology.Facts.InventoryCapturedAt);
        var plan = RealOperationPlanFactory.Create(new(OperationIntent.CreateVirtualDisk, pool.System,
            [pool, tier, physical], [step], "Whole GiB MAX"), OperationId.New(), environment, fixture.Session,
            closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint, "Fixture provider proof",
            topology.Facts.InventoryCapturedAt, topology.Facts.InventoryCapturedAt.AddMinutes(5));
        var target = WindowsRealStorageTargetBuilder.Build(topology, RealTargetReference.ForExisting(pool), new Dictionary<string, string>())
            with { RelatedUniqueId = tier.ProviderKey, RelatedObjectId = tier.ProviderKey };
        var preflight = new RealStepPreflight(JsonSerializer.Serialize(target), topology.InventoryVersion, closure.Fingerprint, closure.PhysicalMemberFingerprint);
        var journal = new MaximumMemoryJournal(topology.Facts.InventoryCapturedAt);
        fixture.Adapter.OnExecute = () =>
        {
            var candidate = journal.Records[^1].Attempt.CandidateBytes;
            if (candidate <= supportedGib * MaximumCapacityAlgorithm.GiB)
            {
                footprint = candidate;
                fixture.SnapshotTransform = snapshot => Snapshot(snapshot, true, candidate);
            }
        };
        fixture.Adapter.ResultTransform = result =>
        {
            var candidate = journal.Records[^1].Attempt.CandidateBytes;
            if (candidate > supportedGib * MaximumCapacityAlgorithm.GiB)
                return new(true, "provider.error-outcome-unknown", null, null, null, null, null, null, "capacity",
                    ProviderFailure: new(40000, "cdxml-storagewmi-error-id", "StorageWMI 40000,New-VirtualDisk", null, null, null, null));
            var output = journal.Records[^1].Attempt.Phase == MaximumCapacityAttemptPhase.Resize ? "tier:instance" : "vd:tiered";
            return new(true, "provider.returned", output, output, "", null, null, null, null,
                new(tier.ProviderKey, tier.ProviderKey, pool.ProviderKey, PhysicalId, "HDD", "Simple", "Fixed", 1, 65536, candidate),
                TieredLiveCapability(target));
        };
        return (fixture, plan, step, preflight, journal);
    }

    private sealed class MaximumMemoryJournal(DateTimeOffset timestamp) : IMaximumCapacityAttemptJournal
    {
        public List<MaximumCapacityAttemptRecord> Records { get; } = [];
        public bool IsStopRequested => false;
        public Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MaximumCapacityAttemptRecord>>(Records.ToArray());
        public Task<bool> PrepareAsync(MaximumCapacityAttempt attempt, CancellationToken ct)
        {
            Records.Add(new(attempt, MaximumCapacityAttemptState.PreparingCall, null, timestamp));
            return Task.FromResult(true);
        }
        public Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken ct)
        {
            Records[ordinal - 1] = Records[ordinal - 1] with { State = MaximumCapacityAttemptState.CallIssued };
            return Task.FromResult(true);
        }
        public Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult result, CancellationToken ct)
        {
            Records[ordinal - 1] = Records[ordinal - 1] with { State = result.State, Result = result };
            return Task.FromResult(true);
        }
    }
}
