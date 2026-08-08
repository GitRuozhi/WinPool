using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedLocalInventoryDocument(
    Guid SnapshotId,
    LocalInventoryDocumentPayload Document);

public sealed class LocalInventoryDocumentRepository
{
    private const int MaximumDocumentBytes = 64 * 1024 * 1024;
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;

    public LocalInventoryDocumentRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<PersistedLocalInventoryDocument?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, document_id, document_schema_version,
                   display_name, sanitized_json, sha256, captured_at_utc_ms
            FROM local_inventory_document
            WHERE singleton = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PersistedLocalInventoryDocument(
            Guid.Parse(reader.GetString(0)),
            new LocalInventoryDocumentPayload(
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
    }

    public async Task SaveAsync(
        Guid snapshotId,
        LocalInventoryDocumentPayload document,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A persisted inventory snapshot is required.", nameof(snapshotId));
        }
        Validate(document);
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_inventory_document(
                singleton, snapshot_id, document_id, document_schema_version,
                display_name, sanitized_json, sha256, captured_at_utc_ms)
            VALUES(1, $snapshot, $document, $schema, $display, $json, $sha, $captured)
            ON CONFLICT(singleton) DO UPDATE SET
                snapshot_id = excluded.snapshot_id,
                document_id = excluded.document_id,
                document_schema_version = excluded.document_schema_version,
                display_name = excluded.display_name,
                sanitized_json = excluded.sanitized_json,
                sha256 = excluded.sha256,
                captured_at_utc_ms = excluded.captured_at_utc_ms;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("N"));
        command.Parameters.AddWithValue("$document", document.DocumentId);
        command.Parameters.AddWithValue("$schema", document.DocumentSchemaVersion);
        command.Parameters.AddWithValue("$display", document.DisplayName);
        command.Parameters.AddWithValue("$json", document.SanitizedJson);
        command.Parameters.AddWithValue("$sha", document.Sha256);
        command.Parameters.AddWithValue(
            "$captured",
            document.CapturedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(LocalInventoryDocumentPayload document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.DocumentId)
            || document.DocumentSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(document.DisplayName)
            || string.IsNullOrWhiteSpace(document.SanitizedJson)
            || System.Text.Encoding.UTF8.GetByteCount(document.SanitizedJson) > MaximumDocumentBytes
            || document.Sha256.Length != 64
            || !document.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The local inventory document is invalid.");
        }
    }
}
