using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class SimulationDocumentConflictException(string message)
    : InvalidOperationException(message);

public sealed class SimulationDocumentRepository
{
    private const int MaximumDocumentBytes = 3 * 1024 * 1024;
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;

    public SimulationDocumentRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<SimulationDocumentListResponse> ListMetadataAsync(
        int pageSize,
        string? afterDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (afterDocumentId is not null)
        {
            ValidateId(afterDocumentId);
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_id, document_schema_version, display_name,
                   sha256, revision, updated_at_utc_ms
            FROM simulation_documents
            WHERE $after IS NULL OR document_id > $after
            ORDER BY document_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", (object?)afterDocumentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var documents = new List<SimulationDocumentMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new SimulationDocumentMetadata(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }

        var hasMore = documents.Count > pageSize;
        if (hasMore)
        {
            documents.RemoveAt(documents.Count - 1);
        }
        return new SimulationDocumentListResponse(
            documents,
            hasMore ? documents[^1].DocumentId : null);
    }

    public async Task<SimulationDocumentPayload?> LoadAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(documentId);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_id, document_schema_version, display_name,
                   sanitized_json, sha256, revision, updated_at_utc_ms
            FROM simulation_documents
            WHERE document_id = $id;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<SimulationDocumentPayload> SaveAsync(
        SimulationDocumentPayload document,
        string? expectedPreviousSha256,
        CancellationToken cancellationToken = default)
    {
        Validate(document);
        ValidateExpectedHash(expectedPreviousSha256, allowNull: true);
        if (expectedPreviousSha256 is null && document.Revision != 1)
        {
            throw new ArgumentException(
                "A new simulation document must start at revision 1.",
                nameof(document));
        }
        if (expectedPreviousSha256 is not null && document.Revision < 2)
        {
            throw new ArgumentException(
                "A simulation update must advance its revision.",
                nameof(document));
        }
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var saved = await UpsertAsync(
            connection,
            transaction,
            document,
            expectedPreviousSha256,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<bool> DeleteAsync(
        string documentId,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateId(documentId);
        ValidateExpectedHash(expectedSha256, allowNull: false);
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM simulation_documents
            WHERE document_id = $id AND sha256 = $expected;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$expected", expectedSha256);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0 && await ExistsAsync(connection, documentId, cancellationToken))
        {
            throw new SimulationDocumentConflictException(
                "The simulation document changed before it could be deleted.");
        }

        return changed == 1;
    }

    public async Task<SimulationCommitReceipt?> FindByCommitIdAsync(
        string commitId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commitId)) return null;
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT commit_id, document_id, before_revision, document_revision,
                   before_sha256, after_sha256, intent_hash, committed_at_utc_ms
            FROM simulation_edit_commits WHERE commit_id=$commit;
            """;
        command.Parameters.AddWithValue("$commit", commitId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SimulationCommitReceipt(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)))
            : null;
    }

    public async Task<SimulationCommitReceipt> CommitEditAsync(
        SimulationDocumentPayload document, string expectedPreviousSha256,
        SimulationCommitIntent intent, CancellationToken cancellationToken = default)
    {
        Validate(document);
        ValidateExpectedHash(expectedPreviousSha256, allowNull: false);
        if (string.IsNullOrWhiteSpace(intent.CommitId) || intent.CommitId.Length > 128
            || intent.DocumentId != document.DocumentId
            || intent.ExpectedRevision < 1 || document.Revision != checked(intent.ExpectedRevision + 1)
            || intent.Edits.Count == 0)
            throw new ArgumentException("The simulation commit intent is inconsistent.");
        var existing = await FindByCommitIdAsync(intent.CommitId, cancellationToken);
        if (existing is not null)
        {
            if (existing.DocumentId == intent.DocumentId && existing.IntentHash == intent.IntentHash)
                return existing;
            throw new SimulationDocumentConflictException("The commit ID is already bound to a different intent.");
        }
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var saved = await UpsertAsync(connection, transaction, document, expectedPreviousSha256, cancellationToken);
        var receipt = new SimulationCommitReceipt(intent.CommitId, saved.DocumentId,
            intent.ExpectedRevision, saved.Revision, expectedPreviousSha256, saved.Sha256,
            intent.IntentHash, DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO simulation_edit_commits(commit_id,document_id,before_revision,document_revision,
                before_sha256,after_sha256,intent_hash,committed_at_utc_ms)
            VALUES($commit,$document,$beforeRevision,$revision,$before,$after,$intent,$at);
            """;
        command.Parameters.AddWithValue("$commit", receipt.CommitId);
        command.Parameters.AddWithValue("$document", receipt.DocumentId);
        command.Parameters.AddWithValue("$beforeRevision", receipt.BeforeRevision);
        command.Parameters.AddWithValue("$revision", receipt.AfterRevision);
        command.Parameters.AddWithValue("$before", receipt.BeforeSha256);
        command.Parameters.AddWithValue("$after", receipt.AfterSha256);
        command.Parameters.AddWithValue("$intent", receipt.IntentHash);
        command.Parameters.AddWithValue("$at", receipt.CommittedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<SimulationDocumentPayload> UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulationDocumentPayload document,
        string? expectedPreviousSha256,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedPreviousSha256 is null)
        {
            command.CommandText = """
                INSERT INTO simulation_documents(
                    document_id, document_schema_version, display_name,
                    sanitized_json, sha256, revision,
                    created_at_utc_ms, updated_at_utc_ms)
                VALUES($id, $schema, $name, $json, $sha, $targetRevision, $now, $updated)
                ON CONFLICT(document_id) DO NOTHING;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE simulation_documents SET
                    document_schema_version = $schema,
                    display_name = $name,
                    sanitized_json = $json,
                    sha256 = $sha,
                    revision = $targetRevision,
                    updated_at_utc_ms = $updated
                WHERE document_id = $id
                  AND sha256 = $expected
                  AND revision = $previousRevision;
                """;
            command.Parameters.AddWithValue("$expected", expectedPreviousSha256);
            command.Parameters.AddWithValue(
                "$previousRevision",
                checked(document.Revision - 1));
        }

        command.Parameters.AddWithValue("$id", document.DocumentId);
        command.Parameters.AddWithValue("$schema", document.DocumentSchemaVersion);
        command.Parameters.AddWithValue("$name", document.DisplayName);
        command.Parameters.AddWithValue("$json", document.Json);
        command.Parameters.AddWithValue("$sha", document.Sha256);
        command.Parameters.AddWithValue("$targetRevision", document.Revision);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", document.UpdatedAtUtc.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SimulationDocumentConflictException(
                "The simulation document was created or changed by another client.");
        }

        return document with
        {
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(document.UpdatedAtUtc.ToUnixTimeMilliseconds())
        };
    }

    private static void Validate(SimulationDocumentPayload document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateId(document.DocumentId);
        if (document.DocumentSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(document.DisplayName)
            || document.DisplayName.Length > 512)
        {
            throw new ArgumentException("The simulation document metadata is invalid.", nameof(document));
        }
        var bytes = Encoding.UTF8.GetBytes(document.Json ?? string.Empty);
        if (bytes.Length == 0 || bytes.Length > MaximumDocumentBytes)
        {
            throw new ArgumentException("The simulation document payload size is invalid.", nameof(document));
        }
        using var parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The simulation document must be a JSON object.", nameof(document));
        }
        var root = parsed.RootElement;
        if (!root.TryGetProperty("Id", out var id)
            || !StringComparer.Ordinal.Equals(id.GetString(), document.DocumentId)
            || !root.TryGetProperty("SchemaVersion", out var schema)
            || schema.GetInt32() != document.DocumentSchemaVersion
            || !root.TryGetProperty("DisplayName", out var displayName)
            || !StringComparer.Ordinal.Equals(displayName.GetString(), document.DisplayName)
            || !root.TryGetProperty("Kind", out var kind)
            || !StringComparer.OrdinalIgnoreCase.Equals(kind.GetString(), "Simulation")
            || !root.TryGetProperty("Revision", out var revision)
            || revision.GetInt64() != document.Revision
            || document.Revision < 1)
        {
            throw new ArgumentException(
                "The simulation document envelope does not match its JSON payload.",
                nameof(document));
        }
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actual, document.Sha256))
        {
            throw new ArgumentException("The simulation document hash does not match its payload.", nameof(document));
        }
    }

    private static void ValidateId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 256)
        {
            throw new ArgumentException("The simulation document ID is invalid.", nameof(documentId));
        }
    }

    private static void ValidateExpectedHash(string? hash, bool allowNull)
    {
        if (allowNull && hash is null)
        {
            return;
        }
        if (hash is null || hash.Length != 64
            || hash.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The expected SHA-256 value is invalid.", nameof(hash));
        }
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM simulation_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    internal static SimulationDocumentPayload Read(SqliteDataReader reader)
    {
        var document = new SimulationDocumentPayload(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
        Validate(document);
        return document;
    }

}
