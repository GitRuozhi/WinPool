using System.Security.Cryptography;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WinPoolFactsPersistenceTests
{
    [Fact]
    public void ActualDocumentCodecPreservesSourceValuesAndRedactsUnknownExtensionStrings()
    {
        var system = SystemId.New();
        var time = DateTimeOffset.UtcNow;
        var source = new WinPoolSource("source", FactOrigin.Simulation, "WinPool", "PhysicalDisk", time, CollectionPurpose.Storage);
        var facts = new WinPoolFacts(1, system, 1, [source],
            [new("disk", FactObjectType.PhysicalDisk, "source", "opaque", true,
                [WinPoolSourceField.Returned("Size", ulong.MaxValue, FactValueType.UInt64, "source", "bytes"),
                 WinPoolSourceField.Returned("SerialNumber", "secret-serial", FactValueType.String, "source"),
                 WinPoolSourceField.Returned("UnrecognizedVendorProperty", "secret-extension", FactValueType.String, "source")])],
            [], [new(FactObjectType.PhysicalDisk, "opaque", "disk")], []) { IsSimulation = true };
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:roundtrip",
            StorageSystemKind.Simulation, "Roundtrip", StorageSnapshot.Empty("Example"), HardwareInventoryReport.Empty(time), [], time)
        { SystemId = system, SourceFacts = facts };
        var payload = SimulationDocumentCodec.Encode(document);
        Assert.DoesNotContain("secret-", payload.SanitizedJson);
        var restored = SimulationDocumentCodec.Decode(payload);
        var disk = restored.Unified!.Objects.Single();
        Assert.Equal(ulong.MaxValue, disk.Field("Size")!.Value!.Value.GetUInt64());
        Assert.True(disk.Field("SerialNumber")!.IsRedacted);
        Assert.Equal(FieldReadState.Returned, disk.Field("SerialNumber")!.ReadState);
        Assert.True(disk.Field("UnrecognizedVendorProperty")!.IsRedacted);
        Assert.False(facts.Objects[0].Field("SerialNumber")!.IsRedacted);
    }
}
