using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class WinPoolFactsTests
{
    [Fact]
    public void SystemCopyRemapsStorageAndFactsTogetherAndEditsStayInTheCopy()
    {
        var snapshot = TestSnapshotFactory.Create();
        var original = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:original",
            StorageSystemKind.Simulation, "Original", snapshot, HardwareInventoryReport.Empty(DateTimeOffset.Now), [], DateTimeOffset.Now);
        var copy = original.AsImportedSimulation("Copy");
        var sourceIds = original.SourceFacts!.Objects.Select(x => x.Id).ToHashSet();
        Assert.DoesNotContain(copy.SourceFacts!.Objects, x => sourceIds.Contains(x.Id));
        Assert.All(copy.Snapshot.StoragePools, pool =>
        {
            Assert.Contains(copy.SourceFacts.Objects, x => x.Id == pool.StableId);
            Assert.All(pool.MemberPhysicalDiskIds, id => Assert.Contains(copy.Snapshot.PhysicalDisks, disk => disk.StableId == id));
        });
        var target = copy.Snapshot.StoragePools[0].StableId;
        var edited = new SimulationOperationService().Apply(copy, new(SimulationOperationKind.Rename, target, Name: "Copy changed"));
        Assert.True(edited.Succeeded);
        Assert.Equal("Copy changed", edited.Document.Unified!.Objects.Single(x => x.Id == target).DisplayName);
        Assert.NotEqual("Copy changed", original.Snapshot.StoragePools[0].FriendlyName);
        Assert.NotSame(original.Snapshot.StoragePools, copy.Snapshot.StoragePools);
        Assert.NotSame(original.Snapshot.Volumes[0].AccessPaths, copy.Snapshot.Volumes[0].AccessPaths);
    }

    private static readonly SystemId System = SystemId.New();
    private static WinPoolFacts Example(DateTimeOffset time, FieldReadState state = FieldReadState.Returned)
    {
        var source = new WinPoolSource(time.Ticks.ToString(), FactOrigin.StorageCim, "root/Microsoft/Windows/Storage",
            "MSFT_PhysicalDisk", time, CollectionPurpose.Storage, state);
        return new(1, System, 1, [source], state != FieldReadState.Returned ? [] :
            [new("disk", FactObjectType.PhysicalDisk, source.Id, "opaque", true,
                [WinPoolSourceField.Returned("Size", ulong.MaxValue, FactValueType.UInt64, source.Id, "bytes"),
                 WinPoolSourceField.Returned("CanPool", false, FactValueType.Boolean, source.Id),
                 WinPoolSourceField.Returned<string?>("Null", null, FactValueType.String, source.Id),
                 WinPoolSourceField.Returned("Codes", new ulong[] { 0, ulong.MaxValue }, FactValueType.UInt64Array, source.Id),
                 WinPoolSourceField.Missing("Offline", FactValueType.Boolean, source.Id, FieldReadState.Failed, "AccessDenied")])],
            [], [new(FactObjectType.PhysicalDisk, "opaque", "disk")],
            [new(CollectionPurpose.Storage, time, time, state)]);
    }

    [Fact]
    public void JsonRoundTripPreservesUnsignedBoundaryNullFalseArrayAndFailure()
    {
        var facts = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(Example(DateTimeOffset.UtcNow)));
        var disk = new WinPoolSystem(facts).Objects.Single();
        Assert.Equal(ulong.MaxValue, disk.Field("Size")!.Value!.Value.GetUInt64());
        Assert.False(disk.Field("Size")!.TryGetInt64(out _));
        Assert.False(disk.Field("CanPool")!.Value!.Value.GetBoolean());
        Assert.Null(disk.Field("Null")!.Value);
        Assert.Equal(FieldReadState.Returned, disk.Field("Null")!.ReadState);
        Assert.Equal(ulong.MaxValue, disk.Field("Codes")!.Value!.Value[1].GetUInt64());
        Assert.Null(disk.Field("Offline")!.Value);
        Assert.Equal("AccessDenied", disk.Field("Offline")!.ReasonCode);
    }

    [Fact]
    public void StableIdentitySurvivesRegistryReloadButUnknownIdentityIsNeverGuessed()
    {
        var identities = new WinPoolIdentityRegistry(System);
        var id = identities.Resolve(FactObjectType.PhysicalDisk, "reliable");
        var restored = new WinPoolIdentityRegistry(System, identities.Snapshot());
        Assert.Equal(id, restored.Resolve(FactObjectType.PhysicalDisk, "reliable"));
        Assert.NotEqual(id, restored.Resolve(FactObjectType.VirtualDisk, "reliable"));
        Assert.NotEqual(restored.Resolve(FactObjectType.PhysicalDisk, null), restored.Resolve(FactObjectType.PhysicalDisk, null));
        Assert.NotEqual(id, new WinPoolIdentityRegistry(SystemId.New()).Resolve(FactObjectType.PhysicalDisk, "reliable"));
    }

    [Fact]
    public void CopyRemapsObjectsRelationshipsAndPersistedIdentityBindings()
    {
        var original = Example(DateTimeOffset.UtcNow);
        var copy = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(original.CopyTo(SystemId.New())));
        Assert.NotEqual(original.SystemId, copy.SystemId);
        Assert.NotEqual(original.Objects[0].Id, copy.Objects[0].Id);
        Assert.Equal(copy.Objects[0].Id, copy.Identities[0].ObjectId);
        Assert.Equal("disk", original.Objects[0].Id);
    }

    [Fact]
    public void FailedOrLateCollectionDoesNotRemoveCachedDevices()
    {
        var now = DateTimeOffset.UtcNow;
        var current = Example(now);
        var failed = WinPoolFactRefresh.Merge(current, Example(now.AddSeconds(1), FieldReadState.Failed));
        Assert.Single(failed.Objects);
        Assert.Equal(FieldReadState.Failed, failed.Collections.Single().State);
        Assert.Same(failed, WinPoolFactRefresh.Merge(failed, Example(now.AddSeconds(-1))));
        var emptySuccess = Example(now.AddSeconds(2)) with { Objects = [] };
        Assert.Empty(WinPoolFactRefresh.Merge(failed, emptySuccess).Objects);
    }

    [Fact]
    public void RejectsForeignSystemUnknownTypeInvalidNumbersAndDanglingReferences()
    {
        var facts = Example(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => WinPoolFactRefresh.Merge(facts, facts with { SystemId = SystemId.New() }));
        Assert.Throws<InvalidDataException>(() => WinPoolFactsCodec.Encode(facts with { FormatVersion = 0 }));
        Assert.Throws<InvalidDataException>(() => WinPoolFactsCodec.Encode(facts with { Relationships = [new("disk", "missing", "same-device")] }));
        Assert.Throws<InvalidDataException>(() => WinPoolFactsCodec.Encode(facts with { Objects = [facts.Objects[0] with { ObjectType = (FactObjectType)999 }] }));
        Assert.Throws<InvalidDataException>(() => WinPoolFactsCodec.Encode(facts with
        {
            Objects = [facts.Objects[0] with { Fields = [WinPoolSourceField.Returned("Size", -1, FactValueType.UInt64, facts.Sources[0].Id)] }]
        }));
    }

    [Fact]
    public void UnknownUsageDoesNotCreateAHotSpareGroup()
    {
        Assert.Empty(new WinPoolSystem(Example(DateTimeOffset.UtcNow)).DisplayGroups);
    }
}
