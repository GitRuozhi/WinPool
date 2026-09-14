using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class WinPoolStorageProjectionTests
{
    [Fact]
    public void EveryBuiltInLayoutRebuildsGeometryNamesCapacityAndReferencesFromFacts()
    {
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            var facts = WinPoolSimulationFacts.Create(layout.Snapshot, SystemId.New());
            var result = WinPoolStorageProjection.Project(WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(facts)));
            Assert.Equal(layout.Snapshot.SnapshotVersion, result.SnapshotVersion);
            Assert.Equal(layout.Snapshot.Computer, result.Computer);
            Assert.Equal(JsonSerializer.Serialize(layout.Snapshot.StoragePools), JsonSerializer.Serialize(result.StoragePools));
            Assert.Equal(JsonSerializer.Serialize(layout.Snapshot.StorageTiers), JsonSerializer.Serialize(result.StorageTiers));
            Assert.Equal(JsonSerializer.Serialize(layout.Snapshot.VirtualDisks), JsonSerializer.Serialize(result.VirtualDisks));
            Assert.Equal(JsonSerializer.Serialize(layout.Snapshot.OsDisks), JsonSerializer.Serialize(result.OsDisks));
            Assert.Equal(JsonSerializer.Serialize(layout.Snapshot.Partitions), JsonSerializer.Serialize(result.Partitions));
            Assert.Equal(layout.Snapshot.PhysicalDisks.Select(x => (x.StableId, x.Size, x.FriendlyName, x.DeviceId, x.PoolStableId)),
                result.PhysicalDisks.Select(x => (x.StableId, x.Size, x.FriendlyName, x.DeviceId, x.PoolStableId)));
            Assert.Empty(result.Warnings);
            Assert.Empty(StorageRelationshipProjector.Validate(result));
        }
    }

    [Fact]
    public void UnsignedCapacityOverflowIsRetainedAndFlaggedForReadOnlyProjection()
    {
        var system = SystemId.New();
        var now = DateTimeOffset.Now;
        var facts = new WinPoolFacts(1, system, 0,
            [new("source", FactOrigin.StorageCim, "root/storage", "MSFT_PhysicalDisk", now, CollectionPurpose.Storage)],
            [new("disk", FactObjectType.PhysicalDisk, "source", "identity", true,
                [WinPoolSourceField.Returned("Size", ulong.MaxValue, FactValueType.UInt64, "source", "bytes")])], [], [], []);
        var view = WinPoolStorageProjection.Project(facts);
        Assert.Contains(view.Warnings, x => x.Code == "facts.numeric-out-of-range" && x.StableId == "disk");
        Assert.Equal(ulong.MaxValue, facts.Objects[0].Field("Size")!.Value!.Value.GetUInt64());
        Assert.Equal(StorageRuleVerdict.Deny, StorageEditRules.Evaluate(view,
            new(SimulationOperationKind.CreateStoragePool, "new-pool", MemberDiskIds: ["disk"])).Verdict);
        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(view,
            new(SimulationOperationKind.Rename, "disk", Name: "Retained disk")).Verdict);
    }

    [Fact]
    public void OutOfRangeRelatedCapacityBlocksEditingButUnrelatedSourceDoesNot()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var disk = snapshot.VirtualDisks.First();
        var partition = snapshot.Partitions.First(x => snapshot.OsDisks.Any(d => d.StableId == x.OsDiskStableId && d.VirtualDiskStableId == disk.StableId));
        var request = new SimulationOperationRequest(SimulationOperationKind.DeletePartition, partition.StableId);
        var related = snapshot with { Warnings = [new("facts.numeric-out-of-range", "Retained raw capacity", disk.StableId)] };
        Assert.Equal("storage.rule.source-value-out-of-range", StorageEditRules.Evaluate(related, request).Code);
        var unrelated = snapshot with { Warnings = [new("facts.numeric-out-of-range", "Another device", "unrelated-device")] };
        Assert.Equal(StorageEditRules.Evaluate(snapshot, request), StorageEditRules.Evaluate(unrelated, request));
    }

    [Fact]
    public void SanitizedFactsStillRebuildDriveLettersDirectoryMountsAndPartitionTypes()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolFactSanitizer.Redact(WinPoolSimulationFacts.Create(snapshot, SystemId.New()));
        var result = WinPoolStorageProjection.Project(WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(facts)));
        Assert.Equal(snapshot.Volumes.Select(x => string.Join(";", x.AccessPaths)), result.Volumes.Select(x => string.Join(";", x.AccessPaths)));
        Assert.Equal(snapshot.Partitions.Select(x => x.PartitionTypeId), result.Partitions.Select(x => x.PartitionTypeId));
        Assert.Equal(snapshot.PhysicalDisks.Select(x => x.DeviceId), result.PhysicalDisks.Select(x => x.DeviceId));
        Assert.Equal(snapshot.Computer.LastBootTime, result.Computer.LastBootTime);
    }
}
