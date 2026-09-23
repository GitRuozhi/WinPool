using System.Text.Json;

namespace WinPool.Application;

/// <summary>
/// Checks the nullable JSON shape of an imported system before deserializing it
/// into model constructors that assume non-null collection entries.
/// </summary>
public static class StorageSystemImportJsonValidator
{
    private static readonly string[] FactCollections =
    [
        "Sources",
        "Objects",
        "Relationships",
        "Identities",
        "Collections"
    ];

    public static void Validate(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDocument("The import root must be a JSON object.");
        }

        var system = RequireObject(envelope, "System", "The WinPool import is missing its system document.");
        RequireNonEmptyString(
            system,
            "Id",
            "The imported system is missing its document ID.");
        RequireNonEmptyString(
            system,
            "DisplayName",
            "The imported system is missing its display name.");
        var jobs = RequireArray(system, "Jobs", "The imported system is missing its jobs collection.");
        RequireObjectEntries(jobs, "System.Jobs");

        var facts = RequireObject(system, "SourceFacts", "The imported system is missing its source facts.");
        foreach (var collectionName in FactCollections)
        {
            var collection = RequireArray(
                facts,
                collectionName,
                $"The imported system is missing its source facts {collectionName} collection.");
            RequireObjectEntries(collection, $"System.SourceFacts.{collectionName}");

            if (collectionName == "Objects")
            {
                var index = 0;
                foreach (var item in collection.EnumerateArray())
                {
                    var fields = RequireArray(
                        item,
                        "Fields",
                        $"System.SourceFacts.Objects[{index}] is missing its fields collection.");
                    RequireObjectEntries(fields, $"System.SourceFacts.Objects[{index}].Fields");
                    index++;
                }
            }
        }
    }

    private static JsonElement RequireObject(JsonElement owner, string propertyName, string message)
    {
        if (!TryGetProperty(owner, propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDocument(message);
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement owner, string propertyName, string message)
    {
        if (!TryGetProperty(owner, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidDocument(message);
        }

        return value;
    }

    private static void RequireNonEmptyString(JsonElement owner, string propertyName, string message)
    {
        if (!TryGetProperty(owner, propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw InvalidDocument(message);
        }
    }

    private static void RequireObjectEntries(JsonElement collection, string path)
    {
        var index = 0;
        foreach (var item in collection.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDocument($"The imported system contains an invalid {path}[{index}] entry.");
            }

            index++;
        }
    }

    private static bool TryGetProperty(JsonElement owner, string propertyName, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in owner.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static InvalidDataException InvalidDocument(string message) => new(message);
}
