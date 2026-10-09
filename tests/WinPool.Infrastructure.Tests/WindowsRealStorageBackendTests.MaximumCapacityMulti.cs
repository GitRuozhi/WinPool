using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Fact]
    public async Task MultiMaximumCreatesOneExactHalfGibSeedThenSearchesEachActualTierWithoutShrinking()
    {
        var setup = await MultiMaximumFixtureAsync(false);
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.True(result.Outcome == RealStepOutcome.Verified, result.ResultEvidenceJson);
        var records = setup.Journal.Records;
        Assert.Equal(MaximumCapacityAttemptPhase.Seed, records[0].Attempt.Phase);
        Assert.Equal("seed", records[0].Attempt.SearchTargetKey);
        Assert.Equal(5 * MaximumCapacityAlgorithm.GiB, records[0].Attempt.CandidateBytes);
        Assert.All(records.Skip(1), item => Assert.Equal(MaximumCapacityAttemptPhase.Resize, item.Attempt.Phase));
        Assert.Equal(new long[] { 5, 5, 6, 7, 5, 4, 3 }, records.Select(item => item.Attempt.CandidateBytes / MaximumCapacityAlgorithm.GiB));
        Assert.Equal(2, records[0].Result!.SeedSuccessfulBytes!.Count);
        Assert.All(records[0].Result!.SeedSuccessfulBytes!.Values, value => Assert.Equal(5 * MaximumCapacityAlgorithm.GiB / 2, value));
        var summary = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!.MaximumCapacityMulti!;
        Assert.Equal(9 * MaximumCapacityAlgorithm.GiB, summary.ActualSizeBytes);
        Assert.Equal(new long[] { 6, 3 }, summary.Tiers.Select(item => item.ActualSizeBytes / MaximumCapacityAlgorithm.GiB));
        Assert.Equal(records.Count, setup.Fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("stale")]
    [InlineData("wrong-pool")]
    [InlineData("missing-os-association")]
    public async Task MultiMaximumRejectsNonexactMemberRoleProofBeforeAnyCall(string corruption)
    {
        var setup = await MultiMaximumFixtureAsync(false);
        setup.Fixture.Safety.MultiRolesTransform = roles => roles.Select((item, index) => index != 0 ? item : corruption switch
        {
            "protected" => item with { IsSystem = true },
            "stale" => item with { InventoryVersion = "stale" },
            "wrong-pool" => item with { PoolStableId = "other" },
            _ => item with { AssociatedOsDiskIds = ["invented-os"] }
        }).ToArray();
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(0, setup.Fixture.Adapter.CallCount);
        Assert.Empty(setup.Journal.Records);
    }

    [Theory]
    [InlineData("NumberOfColumns")]
    [InlineData("Interleave")]
    [InlineData("ProvisioningType")]
    [InlineData("ResiliencySettingName")]
    [InlineData("NumberOfDataCopies")]
    [InlineData("PhysicalDiskRedundancy")]
    public async Task MultiMaximumAggregateLayoutAllowsReturnedNullButRejectsMissingOrIncompatibleValues(string fieldName)
    {
        var setup = await MultiMaximumFixtureAsync(false);
        // Reuse the exact production-layout fact builder, without invoking a device.
        var topology = await setup.Fixture.Reader.CaptureAsync(default);
        var template = topology.Facts.Objects.Single(item => item.Id == "tier:template");
        var fields = new Dictionary<string, object?> { ["ResiliencySettingName"] = "Simple", ["ProvisioningType"] = 2,
            ["NumberOfColumns"] = 1, ["Interleave"] = 65536, ["NumberOfDataCopies"] = 1, ["PhysicalDiskRedundancy"] = 0 };
        var source = template with { Fields = fields.Select(pair => WinPoolSourceField.Returned(pair.Key, pair.Value,
            pair.Value is string ? FactValueType.String : FactValueType.UInt64, template.SourceRef)).ToImmutableArray() };
        Assert.True(WindowsRealStorageBackend.MaximumAggregateLayoutMatches(source));
        var nullSource = source with { Fields = source.Fields.Select(field => field.Name == fieldName ? field with { Value = JsonSerializer.SerializeToElement<object?>(null) } : field).ToImmutableArray() };
        Assert.True(WindowsRealStorageBackend.MaximumAggregateLayoutMatches(nullSource));
        var missing = source with { Fields = source.Fields.Where(field => field.Name != fieldName).ToImmutableArray() };
        Assert.False(WindowsRealStorageBackend.MaximumAggregateLayoutMatches(missing));
        var incompatible = source with { Fields = source.Fields.Select(field => field.Name == fieldName ? field with {
            Value = fieldName == "ResiliencySettingName" ? JsonSerializer.SerializeToElement("Mirror") : JsonSerializer.SerializeToElement(99) } : field).ToImmutableArray() };
        Assert.False(WindowsRealStorageBackend.MaximumAggregateLayoutMatches(incompatible));
    }

    [Fact]
    public async Task MultiMaximumProviderNotInvokedCannotVerifyAnObservedResize()
    {
        var setup = await MultiMaximumFixtureAsync(false);
        var original = setup.Fixture.Adapter.ResultTransform!;
        setup.Fixture.Adapter.ResultTransform = value =>
        {
            var result = original(value);
            return setup.Journal.Records[^1].Attempt.Phase == MaximumCapacityAttemptPhase.Resize
                ? result with { ProviderReturned = false } : result;
        };
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(2, setup.Fixture.Adapter.CallCount);
        Assert.Equal(MaximumCapacityAttemptState.OutcomeUnknown, setup.Journal.Records[^1].State);
        Assert.Contains("real.maximum.adapter_rejected_without_call", result.ResultEvidenceJson);
    }

    [Fact]
    public async Task MultiMaximumMayRetainAnIntegerSeedAfterSeveralRejectedLargerCandidates()
    {
        var setup = await MultiMaximumFixtureAsync(false, initialGib: 4, hddMaximumGib: 2, ssdMaximumGib: 2);
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        var summary = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!.MaximumCapacityMulti!;
        Assert.Equal(4 * MaximumCapacityAlgorithm.GiB, summary.ActualSizeBytes);
        Assert.All(summary.Tiers, item => { Assert.Equal(1, item.LastVerifiedOrdinal); Assert.Equal(2 * MaximumCapacityAlgorithm.GiB, item.ActualSizeBytes); });
        Assert.Equal(new long[] { 4, 4, 3, 4, 3 }, setup.Journal.Records.Select(item => item.Attempt.CandidateBytes / MaximumCapacityAlgorithm.GiB));
    }

    [Fact]
    public async Task MultiMaximumCapacityErrorWithPartialAllocationNeverAdvancesToAnotherCandidate()
    {
        var setup = await MultiMaximumFixtureAsync(true);
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step, setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(4, setup.Fixture.Adapter.CallCount);
        Assert.Equal(MaximumCapacityAttemptState.OutcomeUnknown, setup.Journal.Records[^1].State);
        Assert.DoesNotContain(setup.Journal.Records, item => item.Attempt.SearchTargetKey == "tier:ssd");
    }

    private static async Task<(Fixture Fixture, OperationPlan Plan, RealOperationStep Step, RealStepPreflight Preflight, MaximumMemoryJournal Journal)>
        MultiMaximumFixtureAsync(bool partial, int initialGib = 5, int hddMaximumGib = 6, int ssdMaximumGib = 3)
    {
        var fixture = new Fixture(secondDisk: true, storageJobs: new SyntheticStorageJobReader(), maximumCapacityClock: true);
        var allocated = new Dictionary<string, long>(StringComparer.Ordinal);
        StorageSnapshot Snapshot(StorageSnapshot snapshot)
        {
            var first = TieredCreationSnapshot(snapshot, false);
            var hdd = first.StorageTiers.Single();
            var ssd = hdd with { StableId = "tier:ssd", FriendlyName = "SSD template", MediaType = "SSD", MemberPhysicalDiskIds = [OtherPhysicalId] };
            var templates = new[] { hdd, ssd };
            var total = allocated.Values.Sum();
            return first with
            {
                PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { Size = 100 * MaximumCapacityAlgorithm.GiB,
                    MediaType = item.StableId == OtherPhysicalId ? "SSD" : "HDD", PoolStableId = "pool:returned", CanPool = false }).ToArray(),
                StoragePools = [first.StoragePools.Single(item => item.IsPrimordial) with { MemberPhysicalDiskIds = [] },
                    first.StoragePools.Single(item => !item.IsPrimordial) with { MemberPhysicalDiskIds = [PhysicalId, OtherPhysicalId], Size = 200 * MaximumCapacityAlgorithm.GiB }],
                StorageTiers = allocated.Count == 0 ? templates : templates.Concat(templates.Select(item => item with {
                    StableId = item.StableId + ":actual", FriendlyName = "Provider allocated instance", VirtualDiskStableId = "vd:tiered",
                    Size = allocated[item.StableId], FootprintOnPool = allocated[item.StableId] })).ToArray(),
                VirtualDisks = allocated.Count == 0 ? [] : [new("vd:tiered", true, "Tiered VD", "Healthy", "OK", "Simple", "Fixed", 1, 65536,
                    total, total, "pool:returned", templates.Select(item => item.StableId + ":actual").ToArray(), [9], NumberOfDataCopies: 1,
                    PhysicalDiskRedundancy: 0, AllocatedSize: total)],
                OsDisks = allocated.Count == 0 ? [] : [new("disk:tiered", "Tiered VD", 9, "RAW", total, false, false, false, null, "vd:tiered")]
            };
        }
        fixture.SnapshotTransform = Snapshot;
        fixture.FactsTransform = item => MaximumPhysicalFootprint(TieredCreationFacts(item),
            allocated.GetValueOrDefault(item.Id == OtherPhysicalId ? "tier:ssd" : "tier:template"));
        fixture.CompleteFactsTransform = facts =>
        {
            var roleSource = facts.Sources.First(item => item.ClassName == "Windows.DiskRoles");
            foreach (var disk in facts.Objects.Where(item => item.ObjectType == FactObjectType.Disk))
            {
                var roleId = disk.Id + ":native-roles";
                var role = new WinPoolSourceObject(roleId, FactObjectType.HardwareSupplement, roleSource.Id, roleId, true,
                    [WinPoolSourceField.Returned("DiskNumber", disk.Field("Number")!.Value!.Value.GetInt32(), FactValueType.Int64, roleSource.Id),
                     WinPoolSourceField.Returned("IsPageFile", false, FactValueType.Boolean, roleSource.Id),
                     WinPoolSourceField.Returned("IsCrashDump", false, FactValueType.Boolean, roleSource.Id)]);
                facts = facts with { Objects = facts.Objects.Add(role),
                    Relationships = facts.Relationships.Add(new(disk.Id, roleId, "disk-supplement", facts.InventoryCapturedAt)) };
            }
            return facts;
        };
        var topology = await fixture.Reader.CaptureAsync(default);
        var pool = fixture.Id(StorageObjectKind.StoragePool, "pool:returned");
        var members = new[] { fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId), fixture.Id(StorageObjectKind.PhysicalDisk, OtherPhysicalId) };
        var closure = topology.RequireExactPhysicalMemberSet(pool, members);
        var templates = new[] { fixture.Id(StorageObjectKind.StorageTier, "tier:template"), fixture.Id(StorageObjectKind.StorageTier, "tier:ssd") };
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, (initialGib + 1L) * MaximumCapacityAlgorithm.GiB, initialGib * MaximumCapacityAlgorithm.GiB,
            100 * MaximumCapacityAlgorithm.GiB, 204, "fixture provider", closure.Fingerprint, topology.Facts.InventoryCapturedAt);
        var command = new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), RealTargetReference.ForExisting(templates[0]),
            "Tiered VD", 0, true, MaximumCapacity: policy, CapacityTiers: templates.Select(id => new MaximumCapacityTier(RealTargetReference.ForExisting(id), policy)).ToArray());
        var step = new RealOperationStep("max-multi", command, [], "Exact pool", "Per-tier boundaries", "RAW allocation", "fixture");
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, topology.Facts.InventoryCapturedAt);
        var plan = RealOperationPlanFactory.Create(new(OperationIntent.CreateVirtualDisk, pool.System, new[] { pool }.Concat(templates).Concat(members).ToArray(),
            [step], "Per-tier boundaries"), OperationId.New(), environment, fixture.Session, closure.Fingerprint, closure.Fingerprint,
            closure.PhysicalMemberFingerprint, "fixture", topology.Facts.InventoryCapturedAt, topology.Facts.InventoryCapturedAt.AddMinutes(5));
        var preflight = new RealStepPreflight("{}", closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint);
        var journal = new MaximumMemoryJournal(topology.Facts.InventoryCapturedAt);
        var rejected = false;
        fixture.Adapter.OnExecute = () =>
        {
            var attempt = journal.Records[^1].Attempt;
            rejected = false;
            if (attempt.Phase == MaximumCapacityAttemptPhase.Seed)
                foreach (var template in templates) allocated[template.ProviderKey] = initialGib * MaximumCapacityAlgorithm.GiB / 2;
            else
            {
                var bound = attempt.SearchTargetKey == "tier:template" ? (long)hddMaximumGib : ssdMaximumGib;
                rejected = attempt.CandidateBytes > bound * MaximumCapacityAlgorithm.GiB;
                if (!rejected || partial) allocated[attempt.SearchTargetKey] = attempt.CandidateBytes;
            }
        };
        fixture.Adapter.ResultTransform = _ =>
        {
            var attempt = journal.Records[^1].Attempt;
            if (rejected) return new(true, "provider.error-outcome-unknown", null, null, null, null, null, null, "capacity",
                ProviderFailure: new(40000, "cdxml-storagewmi-error-id", "StorageWMI 40000,Resize-StorageTier", null, null, null, null));
            var id = attempt.Phase == MaximumCapacityAttemptPhase.Seed ? "vd:tiered" : attempt.SearchTargetKey + ":actual";
            var provider = new WindowsStorageCommandResult(true, "provider.returned", id, id, null, null, null, null, null);
            return provider;
        };
        return (fixture, plan, step, preflight, journal);
    }
}

