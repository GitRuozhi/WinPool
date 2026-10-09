using System.Text.Json;
using System.Text.Encodings.Web;
using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("object-id")]
    [InlineData("unique-id")]
    public async Task OrdinaryMaximumResidualRecoveryRoundTripsQuotedProviderIdentitiesAndReturnedNullWithoutWeakeningIdentity(string alteration)
    {
        var setup = await OrdinaryMaximumResidualFixtureAsync(quotedIdentities: true);
        var wire = JsonSerializer.Serialize(setup.Journal.Records);
        Assert.Contains("\\u0022", wire);
        var rows = JsonSerializer.Deserialize<MaximumCapacityAttemptRecord[]>(wire)!;
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(rows[0].Result!.ResultEvidenceJson)!;
        Assert.Equal(2, receipt.Before.Objects.Count);
        Assert.All(receipt.Before.Objects, item =>
        {
            Assert.Contains('"', item.Field("ObjectId")!.Value!.Value.GetString()!);
            var returnedNull = item.Field("ReturnedNullFixture")!;
            Assert.Equal(FieldReadState.Returned, returnedNull.ReadState);
            Assert.Null(returnedNull.Value);
        });
        if (alteration != "none")
        {
            var fieldName = alteration == "object-id" ? "ObjectId" : "UniqueId";
            receipt = receipt with { Before = receipt.Before with { Objects = receipt.Before.Objects.Select((item, index) =>
                index != 0 ? item : item with { Fields = item.Fields.Select(field => field.Name != fieldName ? field
                    : field with { Value = JsonSerializer.SerializeToElement("foreign-provider-identity") }).ToImmutableArray() }).ToArray() } };
            rows[0] = rows[0] with { Result = rows[0].Result! with { ResultEvidenceJson = JsonSerializer.Serialize(receipt) } };
        }
        var calls = setup.Fixture.Adapter.CallCount;
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", null, null), rows, default);
        Assert.True(recovered.State == (alteration == "none" ? RealOperationState.Failed : RealOperationState.OutcomeUnknown), recovered.Code);
        Assert.Equal(alteration == "none", recovered.CanReleaseWriteBarrier);
        if (alteration == "none") Assert.Equal("real.maximum.stopped_without_maximum", Assert.Single(recovered.Steps).Code);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task OrdinaryMaximumUnknownCreationCanOnlyRecoverAsAnExactFailedResidualWithoutReplay()
    {
        var setup = await OrdinaryMaximumResidualFixtureAsync();
        var calls = setup.Fixture.Adapter.CallCount;
        var original = JsonSerializer.Serialize(setup.Journal.Records);
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", null, "original_parent_receipt"),
            setup.Journal.Records, default);
        Assert.True(recovered.State == RealOperationState.Failed, recovered.Code);
        Assert.True(recovered.CanReleaseWriteBarrier);
        var progress = Assert.Single(recovered.Steps);
        Assert.Equal(RealOperationStepState.Failed, progress.State);
        Assert.Equal("real.maximum.stopped_without_maximum", progress.Code);
        using var evidence = JsonDocument.Parse(progress.ResultEvidence!);
        Assert.True(evidence.RootElement.GetProperty("NotVerified").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("MaximumFound").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("AutomaticContinuation").GetBoolean());
        Assert.Equal(original, JsonSerializer.Serialize(setup.Journal.Records));
        Assert.Equal(1, calls);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("provider-uid")]
    [InlineData("provider-oid")]
    [InlineData("residual-capacity")]
    [InlineData("residual-layout")]
    [InlineData("missing-child")]
    [InlineData("extra-issued-child")]
    public async Task OrdinaryMaximumUnknownResidualRequiresTheOriginalExactProviderAndCompleteChildChain(string alteration)
    {
        var setup = await OrdinaryMaximumResidualFixtureAsync();
        var row = Assert.Single(setup.Journal.Records);
        if (alteration is "provider-uid" or "provider-oid")
        {
            var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(row.Result!.ResultEvidenceJson)!;
            receipt = receipt with { Provider = alteration == "provider-uid"
                ? receipt.Provider with { UniqueId = "foreign-provider-uid" }
                : receipt.Provider with { ObjectId = "foreign-provider-object" } };
            setup.Journal.Records[0] = row with { Result = row.Result! with { ResultEvidenceJson = JsonSerializer.Serialize(receipt) } };
        }
        if (alteration is "residual-capacity" or "residual-layout")
        {
            var original = setup.Fixture.SnapshotTransform!;
            setup.Fixture.SnapshotTransform = snapshot =>
            {
                var actual = original(snapshot);
                return actual with { VirtualDisks = actual.VirtualDisks.Select(item => alteration == "residual-capacity"
                    ? item with { Size = item.Size + MaximumCapacityAlgorithm.GiB }
                    : item with { NumberOfColumns = 2 }).ToArray() };
            };
        }
        if (alteration == "missing-child") setup.Journal.Records.Clear();
        if (alteration == "extra-issued-child") setup.Journal.Records.Add(new(row.Attempt with { Ordinal = 2 },
            MaximumCapacityAttemptState.CallIssued, null, row.UpdatedAtUtc));
        var calls = setup.Fixture.Adapter.CallCount;
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.OutcomeUnknown, "lost_parent_receipt", null, null), setup.Journal.Records, default);
        Assert.Equal(RealOperationState.OutcomeUnknown, recovered.State);
        Assert.False(recovered.CanReleaseWriteBarrier);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
    }

    private static async Task<(Fixture Fixture, OperationPlan Plan, RealOperationStep Step, MaximumMemoryJournal Journal)>
        OrdinaryMaximumResidualFixtureAsync(bool quotedIdentities = false)
    {
        var fixture = new Fixture(storageJobs: new SyntheticStorageJobReader(), maximumCapacityClock: true);
        fixture.Safety.ReturnFreshPoolRoles = true;
        const string vdId = "vd:ordinary-max-residual", osId = "disk:ordinary-max-residual";
        const long bytes = 5 * MaximumCapacityAlgorithm.GiB;
        StorageSnapshot Snapshot(StorageSnapshot snapshot, bool created)
        {
            var pooled = CreatedPoolSnapshot(snapshot);
            return pooled with
            {
                PhysicalDisks = pooled.PhysicalDisks.Select(item => item with { Size = 100 * MaximumCapacityAlgorithm.GiB }).ToArray(),
                StoragePools = pooled.StoragePools.Select(item => item.IsPrimordial ? item
                    : item with { Size = 100 * MaximumCapacityAlgorithm.GiB, AllocatedSize = created ? bytes : 0 }).ToArray(),
                VirtualDisks = created ? [new(vdId, true, "Ordinary MAX", "Healthy", "OK", "Simple", "Fixed", 1, 65536,
                    bytes, bytes, "pool:returned", [], [9], NumberOfDataCopies: 1, PhysicalDiskRedundancy: 0, AllocatedSize: bytes)] : [],
                OsDisks = created ? [new(osId, "Ordinary MAX", 9, "RAW", bytes, false, false, false, null, vdId)] : []
            };
        }
        fixture.SnapshotTransform = snapshot => Snapshot(snapshot, false);
        fixture.FactsTransform = item =>
        {
            if (item.ObjectType == FactObjectType.Disk)
                item = item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("IsClustered", false, FactValueType.Boolean, item.SourceRef)) };
            if (quotedIdentities && item.ObjectType is FactObjectType.PhysicalDisk or FactObjectType.StoragePool)
            {
                // Match PowerShell's raw token (\") before the default .NET
                // receipt writer escapes those same quotes as \u0022.
                var text = "provider \"" + item.Id + "\"";
                using var raw = JsonDocument.Parse(JsonSerializer.Serialize(text,
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                var value = raw.RootElement.Clone();
                item = item with { Fields = item.Fields.Select(field => field.Name == "ObjectId" ? field with { Value = value } : field)
                    .ToImmutableArray().Add(WinPoolSourceField.Returned<object?>("ReturnedNullFixture", null, FactValueType.String, item.SourceRef)) };
            }
            return item;
        };
        fixture.CompleteFactsTransform = facts =>
        {
            var source = facts.Sources.First(item => item.ClassName == "Windows.DiskRoles");
            foreach (var disk in facts.Objects.Where(item => item.ObjectType == FactObjectType.Disk))
            {
                var id = disk.Id + ":native-roles";
                var roles = new WinPoolSourceObject(id, FactObjectType.HardwareSupplement, source.Id, id, true,
                    [WinPoolSourceField.Returned("DiskNumber", disk.Field("Number")!.Value!.Value.GetInt32(), FactValueType.Int64, source.Id),
                     WinPoolSourceField.Returned("IsPageFile", false, FactValueType.Boolean, source.Id),
                     WinPoolSourceField.Returned("IsCrashDump", false, FactValueType.Boolean, source.Id)]);
                facts = facts with { Objects = facts.Objects.Add(roles),
                    Relationships = facts.Relationships.Add(new(disk.Id, id, "disk-supplement", facts.InventoryCapturedAt)) };
            }
            return facts;
        };
        var before = await fixture.Reader.CaptureAsync(default);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var pool = fixture.Id(StorageObjectKind.StoragePool, "pool:returned");
        var closure = before.RequireSinglePhysicalClosure([physical]);
        if (quotedIdentities) Assert.All(closure.Objects, item => Assert.Contains("\\\"", item.Field("ObjectId")!.Value!.Value.GetRawText()));
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, 6 * MaximumCapacityAlgorithm.GiB,
            bytes, 100 * MaximumCapacityAlgorithm.GiB, 204, "fixture provider", closure.Fingerprint, before.Facts.InventoryCapturedAt);
        var step = new RealOperationStep("ordinary-max", new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool),
            "Ordinary MAX", 0, 65536, 1, true, policy), [], "Empty exact ordinary pool", "Observed integer-GiB boundary", "RAW allocation", "fixture");
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, before.Facts.InventoryCapturedAt);
        var plan = RealOperationPlanFactory.Create(new(OperationIntent.CreateVirtualDisk, pool.System, [physical, pool], [step], "Ordinary MAX"),
            OperationId.New(), environment, fixture.Session, closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint,
            "fixture", before.Facts.InventoryCapturedAt, before.Facts.InventoryCapturedAt.AddMinutes(5));
        var target = WindowsRealStorageTargetBuilder.Build(before, RealTargetReference.ForExisting(pool), new Dictionary<string, string>());
        var preflight = new RealStepPreflight(JsonSerializer.Serialize(target), before.InventoryVersion, closure.Fingerprint, closure.PhysicalMemberFingerprint);
        var journal = new MaximumMemoryJournal(before.Facts.InventoryCapturedAt);
        fixture.Adapter.OnExecute = () => fixture.SnapshotTransform = snapshot => Snapshot(snapshot, true);
        fixture.Adapter.ResultTransform = _ => new(true, "provider.returned", vdId, vdId, null, null, null, null, null);
        // Let the real macro verifier observe a successful ordinary creation,
        // then stop before any Resize. Only the receipt is made uncertain below.
        var stopped = await fixture.Backend.ExecuteMaximumCapacitySearchAsync(plan, step, preflight,
            new StopAfterFirstResidualAttemptJournal(journal), default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, stopped.Outcome);
        var row = Assert.Single(journal.Records);
        Assert.Equal(MaximumCapacityAttemptState.Verified, row.State);
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(row.Result!.ResultEvidenceJson)!;
        receipt = receipt with { First = null, Second = null, Verified = null, Code = "real.maximum.postcondition_unverified" };
        journal.Records[0] = row with { State = MaximumCapacityAttemptState.OutcomeUnknown,
            Result = new(MaximumCapacityAttemptState.OutcomeUnknown, 0, receipt.Code, JsonSerializer.Serialize(receipt)) };
        return (fixture, plan, step, journal);
    }

    private sealed class StopAfterFirstResidualAttemptJournal(IMaximumCapacityAttemptJournal inner) : IMaximumCapacityAttemptJournal
    {
        public bool IsStopRequested { get; private set; }
        public Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task<bool> PrepareAsync(MaximumCapacityAttempt value, CancellationToken ct) => inner.PrepareAsync(value, ct);
        public Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken ct) => inner.MarkCallIssuedAsync(ordinal, ct);
        public async Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult value, CancellationToken ct)
        {
            var accepted = await inner.CompleteAsync(ordinal, value, ct);
            if (accepted) IsStopRequested = true;
            return accepted;
        }
    }
}
