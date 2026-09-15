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
                    documents.Add(document);
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
        CancellationToken cancellationToken = default,
        string commitId = "");
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
        document.ValidateCurrentFormat();
        if (document.Kind != StorageSystemKind.Simulation)
        {
            throw new InvalidOperationException("Only simulation documents can be encoded.");
        }

        var json = JsonSerializer.Serialize(document, JsonOptions);
        var sha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        var payload = new SimulationDocumentPayload(
            document.Id,
            document.SchemaVersion,
            document.DisplayName,
            json,
            sha256,
            document.Revision,
            document.UpdatedAt);
        StorageDocumentTransportBudget.Validate(payload);
        return payload;
    }

    public static StorageSystemDocument Decode(SimulationDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        StorageDocumentTransportBudget.Validate(payload);
        var json = payload.Json ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(json);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actual, payload.Sha256))
        {
            throw new InvalidDataException("The Agent simulation document hash is invalid.");
        }

        var document = JsonSerializer.Deserialize<StorageSystemDocument>(
                json,
                JsonOptions)
            ?? throw new InvalidDataException("The Agent simulation document is empty.");
        if (document.Kind != StorageSystemKind.Simulation
            || document.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || payload.DocumentSchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || document.Snapshot.SchemaVersion != StorageSnapshot.CurrentSchemaVersion
            || !StringComparer.Ordinal.Equals(document.Id, payload.DocumentId))
        {
            throw new InvalidDataException("The Agent simulation document metadata is inconsistent.");
        }

        if (document.Revision != payload.Revision)
        {
            throw new InvalidDataException("The Agent simulation document revision is inconsistent.");
        }

        document.ValidateCurrentFormat();
        return document;
    }
}

public static class LocalInventoryDocumentCodec
{
    private const int MaximumPayloadBytes = StorageDocumentTransportBudget.MaximumBytes;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LocalInventoryDocumentPayload Encode(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidateCurrentFormat();
        if (document.Kind != StorageSystemKind.Local)
        {
            throw new InvalidOperationException("Only local inventory documents can be encoded.");
        }

        document = document with
        {
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                document.UpdatedAt.ToUnixTimeMilliseconds())
        };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The local inventory document exceeds the IPC limit.");
        }

        var payload = new LocalInventoryDocumentPayload(
            document.Id,
            document.SchemaVersion,
            document.DisplayName,
            json,
            Hash(bytes),
            document.UpdatedAt);
        StorageDocumentTransportBudget.Validate(payload);
        return payload;
    }

    public static StorageSystemDocument Decode(LocalInventoryDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        StorageDocumentTransportBudget.Validate(payload);
        var bytes = Encoding.UTF8.GetBytes(payload.Json ?? string.Empty);
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
            || payload.DocumentSchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || !StringComparer.Ordinal.Equals(document.Id, payload.DocumentId)
            || !StringComparer.Ordinal.Equals(document.DisplayName, payload.DisplayName)
            || document.UpdatedAt.ToUnixTimeMilliseconds() != payload.CapturedAtUtc.ToUnixTimeMilliseconds())
        {
            throw new InvalidDataException("The Agent local inventory metadata is inconsistent.");
        }

        document.ValidateCurrentFormat();
        return document with
        {
            Revision = 0
        };
    }

    /// <summary>
    /// A cache is accepted only through the same integrity checks as a capture.
    /// </summary>
    public static StorageSystemDocument? TryDecodeCached(LocalInventoryDocumentPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return Decode(payload);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal static class StorageDocumentTransportBudget
{
    // Measure the escaped JSON string inside its typed payload, leaving room in the 4 MiB IPC frame.
    public const int MaximumBytes = 3 * 1024 * 1024;
    public static void Validate<T>(T payload)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > MaximumBytes)
            throw new InvalidDataException("The escaped document payload exceeds its IPC budget.");
    }
}

public sealed class AgentBackedHardwareInventoryProvider(IAgentConnection connection)
    : IHardwareInventoryProvider
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Storage, cancellationToken);

    public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Hardware, cancellationToken);

    private async Task<StorageSystemDocument> CollectAsync(CollectionPurpose purpose, CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(
            new CaptureAgentManageInventoryRequest(
                CorrelationId.New(), purpose),
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
            hashes.Clear();
            var documents = new List<StorageSystemDocument>();
            string? afterDocumentId = null;
            do
            {
                var response = await SendAsync(
                    new ListAgentSimulationDocumentsRequest(
                        100,
                        afterDocumentId,
                        CorrelationId.New()),
                    cancellationToken);
                if (response is not SimulationDocumentListResponse list)
                {
                    throw new InvalidDataException("The Agent returned an unexpected simulation list response.");
                }
                foreach (var metadata in list.Documents)
                {
                    var loaded = await SendAsync(
                        new LoadAgentSimulationDocumentRequest(
                            metadata.DocumentId,
                            CorrelationId.New()),
                        cancellationToken);
                    if (loaded is not SimulationDocumentLoadedResponse { Document: { } payload }
                        || payload.DocumentId != metadata.DocumentId
                        || payload.DocumentSchemaVersion != metadata.DocumentSchemaVersion
                        || payload.DisplayName != metadata.DisplayName
                        || payload.Sha256 != metadata.Sha256
                        || payload.Revision != metadata.Revision
                        || payload.UpdatedAtUtc != metadata.UpdatedAtUtc)
                    {
                        throw new InvalidDataException(
                            "The Agent returned simulation content that does not match its metadata.");
                    }
                    var document = SimulationDocumentCodec.Decode(payload);
                    hashes.Add(document.Id, payload.Sha256);
                    documents.Add(document);
                }
                afterDocumentId = list.NextAfterDocumentId;
            }
            while (afterDocumentId is not null);
            return documents
                .OrderBy(document => document.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(document => document.Id, StringComparer.Ordinal)
                .ToArray();
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
        CancellationToken cancellationToken = default,
        string commitId = "")
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!hashes.TryGetValue(document.Id, out var expected))
            {
                throw new InvalidOperationException(
                    "The simulation document must be loaded before an edit can be committed.");
            }

            var id = string.IsNullOrWhiteSpace(commitId)
                ? Guid.NewGuid().ToString("N")
                : commitId.Trim();
            var payload = SimulationDocumentCodec.Encode(document);
            var result = await connection.SendAsync(
                new CommitAgentSimulationEditRequest(
                    payload,
                    expected,
                    plan,
                    events,
                    CorrelationId.New(),
                    id),
                cancellationToken);
            if (result.Status == ApplicationStatus.OutcomeUnknown)
            {
                var lookup = await connection.SendAsync(
                    new LookupAgentSimulationCommitRequest(
                        id,
                        document.Id,
                        expected,
                        payload.Sha256,
                        payload.Revision,
                        plan.OperationId.Value.ToString("N"),
                        plan.PlanHash,
                        CorrelationId.New()),
                    cancellationToken);
                if (lookup.IsSuccess
                    && lookup.Value is SimulationCommitLookupResponse found
                    && found.Found
                    && found.Receipt is { } receipt
                    && receipt.CommitId == id
                    && receipt.BeforeSha256 == expected
                    && CommitDocumentsMatch(receipt.Document, payload)
                    && receipt.OperationId == plan.OperationId.Value.ToString("N")
                    && receipt.PlanHash == plan.PlanHash)
                {
                    hashes[document.Id] = receipt.Document.Sha256;
                    return;
                }

                throw new SimulationCommitOutcomeUnknownException(id);
            }

            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(
                    result.Messages.FirstOrDefault()?.Code
                    ?? "The Agent persistence request failed.");
            }

            UpdateHash(result.Value, document.Id);
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

    private static bool CommitDocumentsMatch(
        SimulationDocumentPayload left,
        SimulationDocumentPayload right) =>
        left.DocumentId == right.DocumentId
        && left.DocumentSchemaVersion == right.DocumentSchemaVersion
        && left.DisplayName == right.DisplayName
        && left.Json == right.Json
        && left.Sha256 == right.Sha256
        && left.Revision == right.Revision
        && left.UpdatedAtUtc.ToUnixTimeMilliseconds()
            == right.UpdatedAtUtc.ToUnixTimeMilliseconds();

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
