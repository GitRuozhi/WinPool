using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class ScopedFactRefreshTests
{
    private static readonly SystemId System = SystemId.New();
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScopedPoolDeletionRestoresObservedPrimordialMemberAndPreservesUnqueriedSiblings()
    {
        WinPoolFacts StorageCapture(int second, bool scoped)
        {
            var time = Start.AddSeconds(second);
            var covered = new[] { "primordial", "concrete", "wdc" }.ToImmutableArray();
            WinPoolSource Source(string name) => new(name + second, FactOrigin.StorageCim,
                "root/microsoft/windows/storage", name, time, CollectionPurpose.Storage)
            { CaptureGeneration = second, Coverage = scoped ? new("cleanup:result", covered, true, second) : null };
            var pools = Source("MSFT_StoragePool");
            var physicals = Source("MSFT_PhysicalDisk");
            WinPoolSourceObject Pool(string id, bool primordial) => new(id, FactObjectType.StoragePool, pools.Id, id, true,
                [WinPoolSourceField.Returned("UniqueId", id, FactValueType.String, pools.Id),
                 WinPoolSourceField.Returned("IsPrimordial", primordial, FactValueType.Boolean, pools.Id)]);
            WinPoolSourceObject Physical(string id) => new(id, FactObjectType.PhysicalDisk, physicals.Id, id, true,
                [WinPoolSourceField.Returned("UniqueId", id, FactValueType.String, physicals.Id),
                 WinPoolSourceField.Returned("CanPool", scoped || id != "wdc", FactValueType.Boolean, physicals.Id)]);
            return new(WinPoolFacts.CurrentFormatVersion, System, second, [pools, physicals],
                scoped ? [Pool("primordial", true), Physical("wdc")]
                    : [Pool("primordial", true), Pool("concrete", false), Physical("wdc"), Physical("samsung-a"), Physical("samsung-b")],
                scoped ? [new("primordial", "wdc", "pool-member", time)]
                    : [new("primordial", "samsung-a", "pool-member", time), new("primordial", "samsung-b", "pool-member", time),
                       new("concrete", "wdc", "pool-member", time)], [], [])
            { InventoryCapturedAt = time, InventoryVersion = "capture-" + second };
        }

        var baseline = StorageCapture(0, false);
        var incoming = StorageCapture(2, true);
        var merged = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(WinPoolFactRefresh.Merge(baseline, incoming)));
        Assert.DoesNotContain(merged.Objects, item => item.Id == "concrete");
        var fresh = Assert.Single(merged.Relationships, edge => edge.ToId == "wdc");
        Assert.Equal("primordial", fresh.FromId);
        Assert.False(fresh.IsRetained);
        Assert.Equal(Start.AddSeconds(2), fresh.ObservedAt);
        foreach (var sibling in new[] { "samsung-a", "samsung-b" })
        {
            Assert.DoesNotContain(incoming.Objects, item => item.Id == sibling);
            Assert.True(Assert.Single(merged.Relationships, edge => edge.ToId == sibling).IsRetained);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(baseline.Objects.Single(item => item.Id == sibling)),
                JsonSerializer.SerializeToElement(merged.Objects.Single(item => item.Id == sibling))));
        }
        var snapshot = WinPoolStorageProjection.Project(merged);
        Assert.Equal("primordial", snapshot.PhysicalDisks.Single(disk => disk.StableId == "wdc").PoolStableId);
        var node = Assert.Single(EditWorkspace.ProjectPoolWorkspace(snapshot), item => item.Unit.StableId == "primordial");
        Assert.Equal(new[] { "samsung-a", "samsung-b", "wdc" }, node.Children.Select(child => child.Unit.StableId).Order(StringComparer.Ordinal));
        Assert.True(merged.IsMerged); // Display cache remains distinct from a fresh execution oracle.
    }

    [Fact]
    public void CompleteLocalAbsenceRemovesOnlyCoveredObjectsAndRoundTripsTheProof()
    {
        var baseline = Capture(0, ["a", "b", "c"]);
        var local = Capture(2, [], ["a"], complete: true);
        var merged = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(WinPoolFactRefresh.Merge(baseline, local)));
        Assert.Equal(new[] { "b", "c" }, merged.Objects.Select(x => x.Id));
        Assert.True(merged.IsMerged);
        Assert.Contains(merged.Sources, x => x.Coverage is { Complete: true } scope && scope.ObjectIds.Contains("a"));
        Assert.Equal(Start, merged.Sources.Single(x => x.Id == merged.Objects[0].SourceRef).CapturedAt);
        // A delayed whole-class capture must not resurrect absence established by a newer scope.
        var lateFull = WinPoolFactRefresh.Merge(merged, Capture(1, ["a", "b", "c"]));
        Assert.DoesNotContain(lateFull.Objects, x => x.Id == "a");
        Assert.Equal(Start.AddSeconds(1), lateFull.Sources.Single(x => x.Id == lateFull.Objects.Single(x => x.Id == "b").SourceRef).CapturedAt);
    }

    [Fact]
    public void IncompleteAndFailedScopesPreserveOldObjectsAndTheirObservationTime()
    {
        var baseline = Capture(0, ["a", "b"]);
        foreach (var incoming in new[] { Capture(2, [], ["a"], complete: false), Capture(2, [], ["a"], state: FieldReadState.Failed) })
        {
            var merged = WinPoolFactRefresh.Merge(baseline, incoming);
            Assert.Equal(baseline.Objects.Select(x => (x.Id, x.ObjectType, x.SourceRef, x.SourceIdentity, x.HasReliableIdentity)),
                merged.Objects.Select(x => (x.Id, x.ObjectType, x.SourceRef, x.SourceIdentity, x.HasReliableIdentity)));
            Assert.All(merged.Objects, item => Assert.Equal(
                baseline.Objects.Single(x => x.Id == item.Id).Fields.ToArray(), item.Fields.ToArray()));
            Assert.All(merged.Objects, item => Assert.Equal(Start, merged.Sources.Single(x => x.Id == item.SourceRef).CapturedAt));
        }
    }

    [Fact]
    public void LaterFullPreservesNewerLocalObjectAndCrossBoundaryRelations()
    {
        var baseline = Capture(0, ["a", "b", "c"]) with { Relationships = [new("a", "b", "same-device", Start)] };
        var local = Capture(3, ["a"], ["a"]);
        var merged = WinPoolFactRefresh.Merge(baseline, local);
        var retained = Assert.Single(merged.Relationships);
        Assert.True(retained.IsRetained);
        Assert.Equal(Start, retained.ObservedAt);
        Assert.Equal("PartialCollection", retained.ReasonCode);
        var late = WinPoolFactRefresh.Merge(merged, Capture(2, ["a", "b", "c"]));
        Assert.Equal(Start.AddSeconds(3), late.Sources.Single(x => x.Id == late.Objects.Single(x => x.Id == "a").SourceRef).CapturedAt);
        Assert.Equal(Start.AddSeconds(2), late.Sources.Single(x => x.Id == late.Objects.Single(x => x.Id == "b").SourceRef).CapturedAt);
        Assert.Single(late.Relationships);
    }

    [Fact]
    public void InvertedGenerationAndLateScopeCannotOverrideNewerEvidence()
    {
        var current = Capture(3, ["a"], ["a"], generation: 9);
        Assert.Same(current, WinPoolFactRefresh.Merge(current, Capture(4, [], ["a"], generation: 8)));
        Assert.Same(current, WinPoolFactRefresh.Merge(current, Capture(2, [], ["a"], generation: 10)));
    }

    [Fact]
    public void ScopeFactoryUsesProviderIdentityAndIncludesSiblingObjectsWithoutTraversingPrimordialPools()
    {
        var baseline = Capture(0, ["disk", "sibling", "unrelated"]);
        baseline = baseline with
        {
            Objects = baseline.Objects.Select(item => item with
            {
                ObjectType = item.Id == "disk" ? FactObjectType.PhysicalDisk : FactObjectType.Partition,
                Fields = [WinPoolSourceField.Returned("UniqueId", "provider-" + item.Id, FactValueType.String, item.SourceRef)]
            }).ToImmutableArray(),
            Relationships = [new("disk", "sibling", "disk-partition")]
        };
        var target = new StorageObjectId(System, StorageObjectKind.PhysicalDisk, "disk");
        var scope = StorageInventoryScopeFactory.Create(baseline, OperationId.New(), "step-1", [target]);
        Assert.Equal(new[] { "disk", "sibling" }, scope.BeforeObjectIds);
        var locator = Assert.Single(scope.BeforeLocators);
        Assert.Equal("UniqueId", locator.IdentityProperty);
        Assert.Equal("provider-disk", locator.IdentityValue);
        Assert.Equal("MSFT_PhysicalDisk", locator.ClassName);
        Assert.Throws<InvalidDataException>(() => StorageInventoryScopeFactory.Create(baseline, OperationId.New(), null,
            [new StorageObjectId(SystemId.New(), target.Kind, target.ProviderKey)]));
    }

    private static WinPoolFacts Capture(int second, string[] objectIds, string[]? coverage = null, bool complete = true,
        FieldReadState state = FieldReadState.Returned, long generation = 0)
    {
        var time = Start.AddSeconds(second);
        var source = new WinPoolSource("source-" + second, FactOrigin.StorageCim, "root/microsoft/windows/storage",
            "MSFT_PhysicalDisk", time, CollectionPurpose.Storage, state)
        {
            CaptureGeneration = generation,
            Coverage = coverage is null ? null : new("operation:result", coverage.ToImmutableArray(), complete, generation)
        };
        return new(WinPoolFacts.CurrentFormatVersion, System, 0, [source], objectIds.Select(id =>
            new WinPoolSourceObject(id, FactObjectType.PhysicalDisk, source.Id, "identity-" + id, true, [])).ToImmutableArray(), [], [], []);
    }
}
