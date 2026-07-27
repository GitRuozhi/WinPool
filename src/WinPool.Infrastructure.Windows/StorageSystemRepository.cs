using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

public sealed class LocalStorageSystemRepository : IStorageSystemRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LocalStorageSystemRepository(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPool",
            "Systems");
    }

    public string DirectoryPath { get; }

    public async Task<IReadOnlyList<StorageSystemDocument>> LoadSimulationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var documents = new List<StorageSystemDocument>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var document = await JsonSerializer.DeserializeAsync<StorageSystemDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken);
                if (document is not null
                    && document.SchemaVersion == StorageSystemDocument.CurrentSchemaVersion
                    && document.Kind == StorageSystemKind.Simulation
                    && !string.IsNullOrWhiteSpace(document.Id))
                {
                    documents.Add(StorageSystemDocumentSanitizer.RedactSensitiveData(document));
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged optional simulation must not prevent WinPool startup.
            }
        }

        return documents;
    }

    public async Task SaveSimulationAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document.Kind != StorageSystemKind.Simulation)
        {
            throw new InvalidOperationException("Only simulated systems can be persisted.");
        }
        document = StorageSystemDocumentSanitizer.RedactSensitiveData(document);

        Directory.CreateDirectory(DirectoryPath);
        var safeId = string.Concat(document.Id.Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var path = Path.Combine(DirectoryPath, $"{safeId}.json");
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, path, true);
    }
}
