using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

public sealed class LocalMachineRecordService : IMachineRecordService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RecordPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPool",
        "machine.json");

    public async Task RecordLocalScanAsync(
        StorageSystemDocument localDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localDocument);
        if (!localDocument.IsLocal)
        {
            return;
        }

        var redacted = StorageSystemDocumentSanitizer.RedactSensitiveData(localDocument);
        var directory = Path.GetDirectoryName(RecordPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = RecordPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, redacted, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, RecordPath, true);
    }
}
