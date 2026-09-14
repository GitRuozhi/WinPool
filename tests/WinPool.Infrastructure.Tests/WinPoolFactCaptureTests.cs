using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WinPoolFactCaptureTests
{
    [Fact]
    public void LogicalDriveClassificationUsesDriveTypeAndKeepsLocalObservationWithItsVolume()
    {
        object Field(string name, object? value, string type = "String") => new { Name = name, Value = value, CimType = type, ReadState = "Returned" };
        object Logical(string letter, object? driveType) => new { ClassName = "Win32_LogicalDisk", Namespace = "root/cimv2", Identity = letter,
            Fields = new[] { Field("DeviceID", letter), Field("DriveType", driveType, "UInt32") } };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { SourceObservations = new object[] {
            new { ClassName = "MSFT_Volume", Namespace = "root/microsoft/windows/storage", Identity = "volume-id",
                Fields = new[] { Field("UniqueId", "volume-id"), Field("DriveLetter", "C") } },
            Logical("C:", 3), Logical("D:", 3), Logical("Z:", 4), Logical("Q:", null) } }));
        var facts = WinPoolFactCapture.Read(json.RootElement, StorageSnapshot.Empty("test"), SystemId.New(), CollectionPurpose.Hardware);
        var system = new WinPoolSystem(facts);
        Assert.Single(system.Objects.Where(x => x.ObjectType == FactObjectType.NetworkDisk));
        var volume = Assert.Single(system.Objects.Where(x => x.ObjectType == FactObjectType.Volume));
        Assert.Equal(2, volume.Sources.Length);
        Assert.Equal(5, facts.Objects.Length);
        Assert.Single(WinPoolStorageProjection.Project(facts).NetworkDisks);
    }

    [Fact]
    public void SuccessfulEmptyClassRemovesOldDevicesButFailedClassKeepsCachedFacts()
    {
        var system = SystemId.New();
        var time = DateTimeOffset.UtcNow;
        const string populated = """
            {"SourceObservations":[{"ClassName":"Win32_Processor","Namespace":"root\\cimv2","Identity":"DeviceID:CPU0",
            "Fields":[{"Name":"Name","CimType":"String","Value":"CPU","ReadState":"Returned"}]}]}
            """;
        WinPoolFacts Capture(string json, int seconds)
        {
            using var parsed = JsonDocument.Parse(json);
            return WinPoolFactCapture.Read(parsed.RootElement, StorageSnapshot.Empty("test") with { ScannedAt = time.AddSeconds(seconds) }, system, CollectionPurpose.Hardware);
        }
        var first = Capture(populated, -3);
        var second = Capture(populated, -2);
        Assert.Equal(first.Objects[0].Id, second.Objects[0].Id);
        var empty = Capture("""{"SourceQuerySuccesses":[{"ClassName":"Win32_Processor","Namespace":"root/cimv2"}]}""", -1);
        Assert.Empty(WinPoolFactRefresh.Merge(first, empty).Objects);
        var failed = Capture("""{"SourceQueryFailures":[{"ClassName":"Win32_Processor","Namespace":"root/cimv2"}]}""", 0);
        var retained = WinPoolFactRefresh.Merge(first, failed);
        Assert.Equal(first.Objects[0], Assert.Single(retained.Objects));
        Assert.Contains(retained.Sources, x => x.ReadState == FieldReadState.Failed);
    }

    [Fact]
    public async Task StorageRefreshOmitsHardwareAndFullRefreshReturnsTypedProcessorFacts()
    {
        var provider = new WindowsHardwareInventoryProvider();
        var storage = await provider.CollectLocalAsync(CancellationToken.None);
        Assert.NotNull(storage.SourceFacts);
        Assert.DoesNotContain(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.Processor);
        Assert.Equal(CollectionPurpose.Storage, Assert.Single(storage.SourceFacts.Collections).Purpose);
        Assert.Contains(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.PhysicalDisk);
        var projected = WinPoolStorageProjection.Project(storage.SourceFacts);
        Assert.Equal(storage.Snapshot.PhysicalDisks.Select(x => (x.StableId, x.Size, x.DeviceId)).OrderBy(x => x.StableId),
            projected.PhysicalDisks.Select(x => (x.StableId, x.Size, x.DeviceId)).OrderBy(x => x.StableId));
        Assert.Equal(storage.Snapshot.Partitions.Select(x => (x.StableId, x.Size, x.Offset, x.GptType)).OrderBy(x => x.StableId),
            projected.Partitions.Select(x => (x.StableId, x.Size, x.Offset, x.GptType)).OrderBy(x => x.StableId));
        Assert.All(projected.Volumes, volume => Assert.Contains(storage.SourceFacts.Objects, x => x.ObjectType == FactObjectType.Volume && x.Id == volume.StableId));
        var hardware = await provider.CollectHardwareAsync(CancellationToken.None);
        var cpu = Assert.Single(hardware.SourceFacts!.Objects.Where(x => x.ObjectType == FactObjectType.Processor));
        Assert.Equal(FieldReadState.Returned, cpu.Field("Name")!.ReadState);
        Assert.Equal(FactValueType.UInt64, cpu.Field("NumberOfCores")!.ValueType);
        var merged = WinPoolFactRefresh.Merge(storage.SourceFacts, hardware.SourceFacts);
        Assert.Equal(2, merged.Collections.Length);
        Assert.Equal(merged.Objects.Length, merged.Objects.Select(x => x.Id).Distinct().Count());
    }
}
