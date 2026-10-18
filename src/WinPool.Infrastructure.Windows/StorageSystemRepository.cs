using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public interface IStructuredSimulationEditRepository
{
    Task<StorageSystemDocument> SaveEditAsync(
        StorageSystemDocument candidate, SimulationCommitIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class SimulationCommitConflictException()
    : InvalidOperationException("The simulation document changed before the edit was saved. Refresh it before retrying.")
{
}

public sealed class SimulationCommitFailureException()
    : IOException("The simulation edit could not be saved. Check storage diagnostics before retrying.")
{
}

public sealed class SimulationDocumentSaveOutcomeUnknownException(string documentId, string sha256)
    : IOException("The simulation document save outcome has not been confirmed.")
{
    public string DocumentId { get; } = documentId;
    public string Sha256 { get; } = sha256;
}

public static class SimulationDocumentCodec
{
    private const int MaximumDocumentBytes = DocumentPayloadLimits.MaximumDocumentBytes;
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

        var json = DocumentPayloadLimits.EncodeBody(document);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The simulation document exceeds its supported size.");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var payload = new SimulationDocumentPayload(
            document.Id,
            document.SchemaVersion,
            document.DisplayName,
            json,
            sha256,
            document.Revision,
            document.UpdatedAt);
        return payload;
    }

    public static StorageSystemDocument Decode(SimulationDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = payload.Json ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length == 0 || bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The simulation document size is invalid.");
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actual, payload.Sha256))
        {
            throw new InvalidDataException("The Agent simulation document hash is invalid.");
        }

        var document = JsonSerializer.Deserialize<StorageSystemDocument>(
                bytes,
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
        DocumentPayloadLimits.EnsureEnvelopeFits(json, document.UpdatedAt);
        return document;
    }
}

public static class LocalInventoryDocumentCodec
{
    private const int MaximumPayloadBytes = DocumentPayloadLimits.MaximumDocumentBytes;
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
        var json = DocumentPayloadLimits.EncodeBody(document);
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
        return payload;
    }

    public static StorageSystemDocument Decode(LocalInventoryDocumentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
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
        DocumentPayloadLimits.EnsureEnvelopeFits(payload.Json, document.UpdatedAt);
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
        return await AgentInventoryReader.CaptureAsync(connection,
            new CaptureAgentManageInventoryRequest(
                CorrelationId.New(), purpose),
            cancellationToken);
    }
}

public sealed class AgentBackedStorageSystemRepository(IAgentConnection connection)
    : IStorageSystemRepository, IStructuredSimulationEditRepository
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly SemaphoreSlim listGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> documentGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> hashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommit> uncertainCommits = new(StringComparer.Ordinal);
    private sealed record PendingCommit(string IntentHash, string BeforeHash);
    private SemaphoreSlim Gate(string id) => documentGates.GetOrAdd(id, _ => new(1, 1));

    public async Task<IReadOnlyList<SimulationDocumentMetadata>> LoadSimulationsAsync(
        CancellationToken cancellationToken = default)
    {
        var gate = listGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var documents = new List<SimulationDocumentMetadata>();
            string? after = null;
            do
            {
                var response = await SendAsync(new ListAgentSimulationDocumentsRequest(100, after, CorrelationId.New()), cancellationToken);
                if (response is not SimulationDocumentListResponse list)
                    throw new InvalidDataException("The Agent returned an unexpected simulation list response.");
                foreach (var metadata in list.Documents)
                {
                    // Catalog refreshes must not replace the base of an open draft.
                    hashes.TryAdd(metadata.DocumentId, metadata.Sha256);
                    documents.Add(metadata);
                }
                after = list.NextAfterDocumentId;
            } while (after is not null);
            return documents.OrderBy(document => document.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(document => document.DocumentId, StringComparer.Ordinal).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<StorageSystemDocument?> LoadSimulationAsync(string documentId,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(documentId);
        await gate.WaitAsync(cancellationToken);
        try { return await LoadDocumentCoreAsync(documentId, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<StorageSystemDocument?> LoadDocumentCoreAsync(string documentId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new LoadAgentSimulationDocumentRequest(documentId, CorrelationId.New()), cancellationToken);
        if (response is not SimulationDocumentLoadedResponse loaded)
            throw new InvalidDataException("The Agent returned an unexpected simulation load response.");
        if (loaded.Document is null) return null;
        if (loaded.Document.DocumentId != documentId)
            throw new InvalidDataException("The Agent returned a different simulation document.");
        var document = await DocumentPayloadLimits.RunLargeAsync(() => SimulationDocumentCodec.Decode(loaded.Document), cancellationToken);
        hashes[documentId] = loaded.Document.Sha256;
        return document;
    }

    public async Task SaveSimulationAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(document.Id);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var payload = await DocumentPayloadLimits.RunLargeAsync(() => SimulationDocumentCodec.Encode(document), cancellationToken);
            hashes.TryGetValue(document.Id, out var expected);
            var result = await connection.SendAsync(
                new SaveAgentSimulationDocumentRequest(
                    payload,
                    expected,
                    CorrelationId.New()),
                cancellationToken);
            if (result.Status == ApplicationStatus.Cancelled)
                throw new OperationCanceledException("The simulation save was cancelled before it was sent.", cancellationToken);
            if (result.Status == ApplicationStatus.OutcomeUnknown)
                throw new SimulationDocumentSaveOutcomeUnknownException(document.Id, payload.Sha256);
            if (!result.IsSuccess || result.Value is null)
                throw new InvalidOperationException(result.Messages.FirstOrDefault()?.Code ?? "The Agent simulation save failed.");
            UpdateHash(result.Value, document.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StorageSystemDocument> SaveEditAsync(
        StorageSystemDocument candidate, SimulationCommitIntent intent,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(intent.DocumentId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!hashes.TryGetValue(intent.DocumentId, out var beforeHash))
                throw new InvalidOperationException("The simulation document must be loaded before an edit can be committed.");
            var expected = await DocumentPayloadLimits.RunLargeAsync(() => SimulationDocumentCodec.Encode(candidate), cancellationToken);
            SimulationCommitReceipt? receipt = null;
            if (!uncertainCommits.TryGetValue(intent.CommitId, out var uncertain))
            {
                uncertain = new(intent.IntentHash, beforeHash);
                uncertainCommits[intent.CommitId] = uncertain;
                var result = await connection.SendAsync(new CommitAgentSimulationEditRequest(intent, CorrelationId.New()), cancellationToken);
                receipt = (result.Value as SimulationEditCommittedResponse)?.Receipt;
                if (result.Status != ApplicationStatus.OutcomeUnknown && (!result.IsSuccess || receipt is null))
                {
                    uncertainCommits.TryRemove(intent.CommitId, out _);
                    var code = result.Messages.FirstOrDefault()?.Code;
                    if (result.Status == ApplicationStatus.Rejected && code == "agent.persistence.simulation_commit_conflict")
                        throw new SimulationCommitConflictException();
                    if (result.Status == ApplicationStatus.Cancelled)
                        throw new OperationCanceledException("The simulation commit was cancelled before completion.", cancellationToken);
                    if (result.Status == ApplicationStatus.Failed && code == "agent.persistence.simulation_commit_failed")
                        throw new SimulationCommitFailureException();
                    throw new InvalidOperationException(code ?? "The Agent simulation commit failed.");
                }
            }
            else if (uncertain.IntentHash != intent.IntentHash)
                throw new InvalidOperationException("A commit identity cannot be reused for another intent.");
            if (receipt is null)
            {
                var lookup = await connection.SendAsync(new LookupAgentSimulationCommitRequest(
                    intent.CommitId, intent.DocumentId, intent.IntentHash, CorrelationId.New()), cancellationToken);
                if (lookup.IsSuccess && lookup.Value is SimulationCommitLookupResponse { Found: true } found)
                    receipt = found.Receipt;
                if (receipt is null) throw new SimulationCommitOutcomeUnknownException(intent.CommitId);
            }
            if (receipt.CommitId != intent.CommitId || receipt.DocumentId != intent.DocumentId
                || receipt.IntentHash != intent.IntentHash || receipt.BeforeRevision != intent.ExpectedRevision
                || receipt.AfterRevision != checked(intent.ExpectedRevision + 1) || receipt.BeforeSha256 != uncertain.BeforeHash)
                throw new SimulationCommitOutcomeUnknownException(intent.CommitId);
            if (receipt.AfterRevision != candidate.Revision || receipt.AfterSha256 != expected.Sha256)
                throw new SimulationCommitConflictException();
            if (beforeHash != uncertain.BeforeHash && beforeHash != receipt.AfterSha256)
                throw new SimulationCommitConflictException();
            hashes[intent.DocumentId] = receipt.AfterSha256;
            uncertainCommits.TryRemove(intent.CommitId, out _);
            return candidate;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteSimulationAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(id);
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
                hashes.TryRemove(id, out _);
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
        hashes[documentId] = saved.Document.Sha256;
    }
}
