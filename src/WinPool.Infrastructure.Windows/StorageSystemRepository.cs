using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed class LocalStorageSystemRepository : IStorageSystemRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string? _directoryPath;

    public LocalStorageSystemRepository(string? directoryPath = null) => _directoryPath = directoryPath;

    public string DirectoryPath => _directoryPath ?? Path.Combine(StorageDataLocations.CurrentRoot, "Systems");

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

    public Task DeleteSimulationAsync(string id, CancellationToken cancellationToken = default)
    {
        var safeId = string.Concat(id.Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var path = Path.Combine(DirectoryPath, $"{safeId}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

public interface IStructuredSimulationEditRepository
{
    Task SaveEditAsync(
        StorageSystemDocument document,
        OperationPlan plan,
        IReadOnlyList<ExecutionEvent> events,
        CancellationToken cancellationToken = default);
}

public static class SimulationDocumentCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SimulationDocumentPayload Encode(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Kind != StorageSystemKind.Simulation)
        {
            throw new InvalidOperationException("Only simulation documents can be encoded.");
        }

        var sanitized = StorageSystemDocumentSanitizer.RedactSensitiveData(document);
        var json = JsonSerializer.Serialize(sanitized, JsonOptions);
        var sha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return new(
            sanitized.Id,
            sanitized.SchemaVersion,
            sanitized.DisplayName,
            json,
            sha256,
            sanitized.Revision,
            sanitized.UpdatedAt);
    }

    public static StorageSystemDocument Decode(SimulationDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var sanitizedJson = payload.SanitizedJson ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(sanitizedJson);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actual, payload.Sha256))
        {
            throw new InvalidDataException("The Agent simulation document hash is invalid.");
        }

        var document = JsonSerializer.Deserialize<StorageSystemDocument>(
                sanitizedJson,
                JsonOptions)
            ?? throw new InvalidDataException("The Agent simulation document is empty.");
        if (document.Kind != StorageSystemKind.Simulation
            || document.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || !StringComparer.Ordinal.Equals(document.Id, payload.DocumentId))
        {
            throw new InvalidDataException("The Agent simulation document metadata is inconsistent.");
        }

        return StorageSystemDocumentSanitizer.RedactSensitiveData(document) with
        {
            Revision = payload.Revision
        };
    }
}

public static class LocalInventoryDocumentCodec
{
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LocalInventoryDocumentPayload Encode(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Kind != StorageSystemKind.Local)
        {
            throw new InvalidOperationException("Only local inventory documents can be encoded.");
        }

        var sanitized = StorageSystemDocumentSanitizer.RedactSensitiveData(document) with
        {
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                document.UpdatedAt.ToUnixTimeMilliseconds())
        };
        var json = JsonSerializer.Serialize(sanitized, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The local inventory document exceeds the IPC limit.");
        }

        return new(
            sanitized.Id,
            sanitized.SchemaVersion,
            sanitized.DisplayName,
            json,
            Hash(bytes),
            sanitized.UpdatedAt);
    }

    public static StorageSystemDocument Decode(LocalInventoryDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = Encoding.UTF8.GetBytes(payload.SanitizedJson ?? string.Empty);
        if (bytes.Length == 0
            || bytes.Length > MaximumPayloadBytes
            || !StringComparer.Ordinal.Equals(Hash(bytes), payload.Sha256))
        {
            throw new InvalidDataException("The Agent local inventory document hash is invalid.");
        }

        var document = JsonSerializer.Deserialize<StorageSystemDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The Agent local inventory document is empty.");
        if (document.Kind != StorageSystemKind.Local
            || document.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || !StringComparer.Ordinal.Equals(document.Id, payload.DocumentId)
            || !StringComparer.Ordinal.Equals(document.DisplayName, payload.DisplayName)
            || document.UpdatedAt != payload.CapturedAtUtc)
        {
            throw new InvalidDataException("The Agent local inventory metadata is inconsistent.");
        }

        return StorageSystemDocumentSanitizer.RedactSensitiveData(document) with
        {
            Revision = 0
        };
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class AgentBackedHardwareInventoryProvider(IAgentConnection connection)
    : IHardwareInventoryProvider
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<StorageSystemDocument> CollectLocalAsync(
        CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(
            new CaptureAgentManageInventoryRequest(
                SystemId.New(),
                CorrelationId.New()),
            cancellationToken);
        if (!result.IsSuccess
            || result.Value is not ManageInventoryCaptureResponse response)
        {
            throw new InventoryScanException(
                "The Agent local inventory request failed.",
                result.Messages.FirstOrDefault()?.Code ?? string.Empty);
        }

        return LocalInventoryDocumentCodec.Decode(response.Document);
    }
}

public sealed class AgentBackedStorageSystemRepository(IAgentConnection connection)
    : IStorageSystemRepository, IStructuredSimulationEditRepository
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<StorageSystemDocument>> LoadSimulationsAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var response = await SendAsync(
                new ListAgentSimulationDocumentsRequest(CorrelationId.New()),
                cancellationToken);
            if (response is not SimulationDocumentListResponse list)
            {
                throw new InvalidDataException("The Agent returned an unexpected simulation list response.");
            }

            hashes.Clear();
            var documents = new List<StorageSystemDocument>(list.Documents.Count);
            foreach (var payload in list.Documents)
            {
                var document = SimulationDocumentCodec.Decode(payload);
                hashes.Add(document.Id, payload.Sha256);
                documents.Add(document);
            }
            return documents;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveSimulationAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var payload = SimulationDocumentCodec.Encode(document);
            hashes.TryGetValue(document.Id, out var expected);
            var response = await SendAsync(
                new SaveAgentSimulationDocumentRequest(
                    payload,
                    expected,
                    CorrelationId.New()),
                cancellationToken);
            UpdateHash(response, document.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveEditAsync(
        StorageSystemDocument document,
        OperationPlan plan,
        IReadOnlyList<ExecutionEvent> events,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!hashes.TryGetValue(document.Id, out var expected))
            {
                throw new InvalidOperationException(
                    "The simulation document must be loaded before an edit can be committed.");
            }
            var response = await SendAsync(
                new CommitAgentSimulationEditRequest(
                    SimulationDocumentCodec.Encode(document),
                    expected,
                    plan,
                    events,
                    CorrelationId.New()),
                cancellationToken);
            UpdateHash(response, document.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteSimulationAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!hashes.TryGetValue(id, out var expected))
            {
                return;
            }
            var response = await SendAsync(
                new DeleteAgentSimulationDocumentRequest(
                    id,
                    expected,
                    CorrelationId.New()),
                cancellationToken);
            if (response is not SimulationDocumentDeletedResponse deleted
                || !StringComparer.Ordinal.Equals(deleted.DocumentId, id))
            {
                throw new InvalidDataException("The Agent returned an unexpected delete response.");
            }
            if (deleted.Deleted)
            {
                hashes.Remove(id);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AgentResponse> SendAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            throw new InvalidOperationException(
                result.Messages.FirstOrDefault()?.Code
                ?? "The Agent persistence request failed.");
        }
        return result.Value;
    }

    private void UpdateHash(AgentResponse response, string documentId)
    {
        if (response is not SimulationDocumentSavedResponse saved
            || !StringComparer.Ordinal.Equals(saved.Document.DocumentId, documentId))
        {
            throw new InvalidDataException("The Agent returned an unexpected save response.");
        }
        SimulationDocumentCodec.Decode(saved.Document);
        hashes[documentId] = saved.Document.Sha256;
    }
}
