using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed class LocalMachineRecordService : IMachineRecordService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RecordPath => Path.Combine(StorageDataLocations.CurrentRoot, "machine.json");

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

    public async Task<StorageSystemDocument?> LoadLocalScanAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(RecordPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(RecordPath);
            var document = await JsonSerializer.DeserializeAsync<StorageSystemDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return document is { IsLocal: true, SchemaVersion: StorageSystemDocument.CurrentSchemaVersion }
                ? document
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class AgentBackedMachineRecordService(IAgentConnection connection)
    : IMachineRecordService
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public Task RecordLocalScanAsync(
        StorageSystemDocument localDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localDocument);
        if (!localDocument.IsLocal)
        {
            throw new InvalidOperationException("Only a local inventory document can be recorded.");
        }

        // AgentBackedHardwareInventoryProvider asks the Agent to persist the
        // same sanitized document before returning it to the workspace.
        return Task.CompletedTask;
    }

    public async Task<StorageSystemDocument?> LoadLocalScanAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await connection.SendAsync(
            new LoadAgentManageInventoryRequest(CorrelationId.New()),
            cancellationToken);
        if (!result.IsSuccess
            || result.Value is not ManageInventoryLoadedResponse response)
        {
            throw new InventoryScanException(
                "The Agent cached inventory request failed.",
                result.Messages.FirstOrDefault()?.Code ?? string.Empty);
        }

        return response.Document is null
            ? null
            : LocalInventoryDocumentCodec.Decode(response.Document);
    }
}
