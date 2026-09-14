using System.Security.Cryptography;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WinPoolFactsPersistenceTests
{
    [Fact]
    public void DocumentPersistsFactsAndApplicationMetadataWithoutWritableProjectionOrReport()
    {
        var time = DateTimeOffset.UtcNow;
        var snapshot = SimulationLayouts.StandardTiered();
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:canonical",
            StorageSystemKind.Simulation, "Canonical", snapshot, HardwareInventoryReport.Empty(time), [], time);
        var payload = SimulationDocumentCodec.Encode(document);
        using var parsed = System.Text.Json.JsonDocument.Parse(payload.SanitizedJson);
        Assert.False(parsed.RootElement.TryGetProperty("Snapshot", out _));
        Assert.False(parsed.RootElement.TryGetProperty("HardwareReport", out _));
        Assert.True(parsed.RootElement.TryGetProperty("SourceFacts", out _));
        Assert.Null(typeof(StorageSystemDocument).GetProperty(nameof(StorageSystemDocument.Snapshot))!.SetMethod);
        var restored = SimulationDocumentCodec.Decode(payload);
        Assert.Equal(snapshot.Volumes.Select(x => x.DriveLetter), restored.Snapshot.Volumes.Select(x => x.DriveLetter));
        Assert.Equal(snapshot.StoragePools.Select(x => x.Size), restored.Snapshot.StoragePools.Select(x => x.Size));
        var oldJson = payload.SanitizedJson.Replace("\"SchemaVersion\":3", "\"SchemaVersion\":2", StringComparison.Ordinal);
        Assert.NotEqual(payload.SanitizedJson, oldJson);
        var old = payload with { DocumentSchemaVersion = 2, SanitizedJson = oldJson,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson))).ToLowerInvariant() };
        Assert.Throws<InvalidDataException>(() => SimulationDocumentCodec.Decode(old));
        Assert.Equal(oldJson, old.SanitizedJson);
    }

    [Fact]
    public void ActualDocumentCodecPreservesOrdinaryStringsAndRedactsHardwareIdentifiers()
    {
        var system = SystemId.New();
        var time = DateTimeOffset.UtcNow;
        var source = new WinPoolSource("source", FactOrigin.Simulation, "WinPool", "PhysicalDisk", time, CollectionPurpose.Storage);
        var facts = new WinPoolFacts(1, system, 1, [source],
            [new("disk", FactObjectType.PhysicalDisk, "source", "opaque", true,
                   [WinPoolSourceField.Returned("Size", ulong.MaxValue, FactValueType.UInt64, "source", "bytes"),
                    WinPoolSourceField.Returned("SerialNumber", "secret-serial", FactValueType.String, "source"),
                    WinPoolSourceField.Returned("ObjectId", "ordinary-storage-id", FactValueType.String, "source"),
                    WinPoolSourceField.Returned("UnrecognizedVendorProperty", "ordinary-extension", FactValueType.String, "source")])],
            [], [new(FactObjectType.PhysicalDisk, "opaque", "disk")], []) { IsSimulation = true };
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:roundtrip",
            StorageSystemKind.Simulation, "Roundtrip", StorageSnapshot.Empty("Example"), HardwareInventoryReport.Empty(time), [], time)
        { SystemId = system, SourceFacts = facts };
        var payload = SimulationDocumentCodec.Encode(document);
        Assert.DoesNotContain("secret-serial", payload.SanitizedJson);
        Assert.Contains("ordinary-storage-id", payload.SanitizedJson);
        Assert.Contains("ordinary-extension", payload.SanitizedJson);
        var restored = SimulationDocumentCodec.Decode(payload);
        var disk = restored.Unified!.Objects.Single();
        Assert.Equal(ulong.MaxValue, disk.Field("Size")!.Value!.Value.GetUInt64());
        Assert.True(disk.Field("SerialNumber")!.IsRedacted);
        Assert.Equal(FieldReadState.Returned, disk.Field("SerialNumber")!.ReadState);
        Assert.False(disk.Field("ObjectId")!.IsRedacted);
        Assert.Equal("ordinary-storage-id", disk.Field("ObjectId")!.DisplayValue());
        Assert.False(disk.Field("UnrecognizedVendorProperty")!.IsRedacted);
        Assert.Equal("ordinary-extension", disk.Field("UnrecognizedVendorProperty")!.DisplayValue());
        Assert.False(facts.Objects[0].Field("SerialNumber")!.IsRedacted);
    }
}
