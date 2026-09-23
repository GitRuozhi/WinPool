using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class StorageSystemImportJsonValidatorTests
{
    [Fact]
    public void RejectsNullFactObjectEntriesAsInvalidDataBeforeModelDeserialization()
    {
        using var import = JsonDocument.Parse(
            """
            {
              "System": {
                "Id": "simulation:malformed",
                "DisplayName": "Malformed system",
                "Jobs": [],
                "SourceFacts": {
                  "Sources": [],
                  "Objects": [null],
                  "Relationships": [],
                  "Identities": [],
                  "Collections": []
                }
              }
            }
            """);

        var error = Assert.Throws<InvalidDataException>(
            () => StorageSystemImportJsonValidator.Validate(import.RootElement));

        Assert.Contains("System.SourceFacts.Objects[0]", error.Message);
    }

    [Fact]
    public void RejectsNullDisplayNameBeforeModelConstructorCanUseIt()
    {
        using var import = JsonDocument.Parse(
            """
            {
              "System": {
                "Id": "simulation:malformed",
                "DisplayName": null
              }
            }
            """);

        var error = Assert.Throws<InvalidDataException>(
            () => StorageSystemImportJsonValidator.Validate(import.RootElement));

        Assert.Contains("display name", error.Message);
    }

    [Fact]
    public void AcceptsCurrentSerializedSystemDocumentShape()
    {
        var system = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:import-shape-test",
            StorageSystemKind.Simulation,
            "Import shape test",
            TestSnapshotFactory.Create(),
            [],
            DateTimeOffset.UtcNow);
        using var import = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Product = "WinPool",
            SchemaVersion = StorageSystemDocument.CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            System = system
        }));

        StorageSystemImportJsonValidator.Validate(import.RootElement);
    }
}
