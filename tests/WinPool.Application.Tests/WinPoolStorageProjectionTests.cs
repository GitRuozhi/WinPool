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
            Assert.Equal(layout.Snapshot.Partitions.Select(x => (x.StableId, x.DiskNumber, x.PartitionNumber,
                    Type: x.Type == "Primary" ? "BasicData" : x.Type, x.Offset, x.Size, x.IsBoot, x.IsSystem, x.IsHidden)),
                result.Partitions.Select(x => (x.StableId, x.DiskNumber, x.PartitionNumber, x.Type, x.Offset, x.Size, x.IsBoot, x.IsSystem, x.IsHidden)));
            Assert.Equal(layout.Snapshot.PhysicalDisks.Select(x => (x.StableId, x.Size, x.FriendlyName, x.DeviceId, x.PoolStableId)),
                result.PhysicalDisks.Select(x => (x.StableId, x.Size, x.FriendlyName, x.DeviceId, x.PoolStableId)));
            Assert.Empty(result.Warnings);
            Assert.Empty(StorageRelationshipProjector.Validate(result));
        }
    }

    [Fact]
    public void BuiltInSimulationFactsUseWindowsStorageSourceShapes()
    {
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            var facts = WinPoolSimulationFacts.Create(layout.Snapshot, SystemId.New());
            var sources = facts.Sources.ToDictionary(x => x.Id);
            Assert.All(sources.Values, source => Assert.Equal(FactOrigin.Simulation, source.Origin));
            Assert.DoesNotContain(sources.Values, source => source.ClassName == "Simulation");
            Assert.Contains(sources.Values, source => source.ClassName == "Win32_ComputerSystem" && source.Namespace == "root/cimv2");
            Assert.Contains(sources.Values, source => source.ClassName == "Win32_OperatingSystem" && source.Namespace == "root/cimv2");
            Assert.Contains(sources.Values, source => source.ClassName == "Registry.CurrentVersion" && source.Namespace == "winpool/native");
            Assert.Contains(sources.Values, source => source.ClassName == "MSFT_PhysicalDisk" && source.Namespace == "root/microsoft/windows/storage");
            Assert.Contains(sources.Values, source => source.ClassName == "MSFT_Disk" && source.Namespace == "root/microsoft/windows/storage");
            Assert.Contains(sources.Values, source => source.ClassName == "MSFT_Partition" && source.Namespace == "root/microsoft/windows/storage");
            Assert.Contains(sources.Values, source => source.ClassName == "MSFT_Volume" && source.Namespace == "root/microsoft/windows/storage");

            var computer = Assert.Single(facts.Objects, x => x.ObjectType == FactObjectType.Computer);
            Assert.Null(computer.Field("WindowsVersion"));
            var operatingSystem = Assert.Single(facts.Objects, x => x.ObjectType == FactObjectType.OperatingSystem);
            Assert.Equal(layout.Snapshot.Computer.WindowsVersion, operatingSystem.Field("Version")!.DisplayValue());

            foreach (var disk in facts.Objects.Where(x => x.ObjectType == FactObjectType.PhysicalDisk))
            {
                Assert.Equal(FactValueType.UInt64, disk.Field("BusType")!.ValueType);
                Assert.Equal(FactValueType.UInt64, disk.Field("MediaType")!.ValueType);
                Assert.Equal(FactValueType.UInt64Array, disk.Field("OperationalStatus")!.ValueType);
                Assert.Equal((FactValueType.UInt64, "bytes"),
                    (disk.Field("LogicalSectorSize")!.ValueType, disk.Field("LogicalSectorSize")!.Unit));
                Assert.Equal((FactValueType.UInt64, "bytes"),
                    (disk.Field("PhysicalSectorSize")!.ValueType, disk.Field("PhysicalSectorSize")!.Unit));
            }

            Assert.All(facts.Objects.Where(x => x.ObjectType == FactObjectType.Partition), partition =>
            {
                Assert.Equal("MSFT_Partition", sources[partition.SourceRef].ClassName);
                Assert.Null(partition.Field("FileSystem"));
                Assert.Equal(FactValueType.UInt64, partition.Field("PartitionNumber")!.ValueType);
            });
            Assert.All(facts.Objects.Where(x => x.ObjectType == FactObjectType.Volume), volume =>
            {
                Assert.Equal("MSFT_Volume", sources[volume.SourceRef].ClassName);
                Assert.Equal(FactValueType.UInt64, volume.Field("AllocationUnitSize")!.ValueType);
                Assert.Equal("bytes", volume.Field("AllocationUnitSize")!.Unit);
            });

            var projected = WinPoolStorageProjection.Project(facts);
            Assert.Equal(4096, projected.Partitions.Single(x => x.IsBoot).AllocationUnitSize);
            Assert.DoesNotContain(projected.FieldIssues, x => x.FieldName is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump");
        }
    }

    [Fact]
    public void CuratedLayoutsUseDistinctSupportedWindowsVersionProfiles()
    {
        var versions = SimulationLayouts.CreateAll()
            .Select(layout => layout.Snapshot.Computer.WindowsVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["10.0.19045", "10.0.26100", "10.0.26200"],
            versions);
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
            new(SimulationEditKind.CreateStoragePool, "new-pool", MemberDiskIds: ["disk"])).Verdict);
        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(view,
            new(SimulationEditKind.Rename, "disk", Name: "Retained disk")).Verdict);
    }

    [Fact]
    public void OutOfRangeRelatedCapacityBlocksEditingButUnrelatedSourceDoesNot()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var disk = snapshot.VirtualDisks.First();
        var partition = snapshot.Partitions.First(x => snapshot.OsDisks.Any(d => d.StableId == x.OsDiskStableId && d.VirtualDiskStableId == disk.StableId));
        var request = new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId);
        var related = snapshot with { Warnings = [new("facts.numeric-out-of-range", "Retained raw capacity", disk.StableId)] };
        Assert.Equal("storage.rule.source-value-out-of-range", StorageEditRules.Evaluate(related, request).Code);
        var unrelated = snapshot with { Warnings = [new("facts.numeric-out-of-range", "Another device", "unrelated-device")] };
        Assert.Equal(StorageEditRules.Evaluate(snapshot, request), StorageEditRules.Evaluate(unrelated, request));
    }

    [Fact]
    public void FactsRoundTripRebuildsDriveLettersDirectoryMountsAndPartitionTypes()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        var result = WinPoolStorageProjection.Project(WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(facts)));
        Assert.Equal(snapshot.Volumes.Select(x => string.Join(";", x.AccessPaths)), result.Volumes.Select(x => string.Join(";", x.AccessPaths)));
        Assert.All(result.Partitions, partition => Assert.False(string.IsNullOrWhiteSpace(partition.PartitionTypeId)));
        Assert.Equal(snapshot.PhysicalDisks.Select(x => x.DeviceId), result.PhysicalDisks.Select(x => x.DeviceId));
        Assert.Equal(snapshot.Computer.LastBootTime, result.Computer.LastBootTime);
    }
}
