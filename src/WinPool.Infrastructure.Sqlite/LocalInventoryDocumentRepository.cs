using WinPool.Application;
using WinPool.Domain;
using Microsoft.Data.Sqlite;

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
        return await ReadAsync(connection, cancellationToken);
    }

    internal static async Task<PersistedLocalInventoryDocument?> ReadAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    public Task SaveAsync(Guid snapshotId, LocalInventoryDocumentPayload document,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(snapshotId, document, null, null, null, cancellationToken);

    public Task SaveAsync(Guid snapshotId, LocalInventoryDocumentPayload document,
        SystemId systemId, string displayName, string canonicalBinding,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(snapshotId, document, systemId, displayName, canonicalBinding, cancellationToken);

    private async Task SaveCoreAsync(Guid snapshotId, LocalInventoryDocumentPayload document,
        SystemId? systemId, string? displayName, string? canonicalBinding,
        CancellationToken cancellationToken)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A persisted inventory snapshot is required.", nameof(snapshotId));
        }
        Validate(document);
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (systemId is { } identity)
        {
            if (identity.Value == Guid.Empty || string.IsNullOrWhiteSpace(displayName)
                || string.IsNullOrWhiteSpace(canonicalBinding))
                throw new ArgumentException("The local system identity is incomplete.");
            await using var system = connection.CreateCommand();
            system.Transaction = transaction;
            system.CommandText = """
                INSERT INTO systems(system_id,kind,display_name,machine_binding_hash,created_at_utc_ms)
                VALUES($id,0,$name,$binding,$at)
                ON CONFLICT(system_id) DO UPDATE SET kind=0,display_name=excluded.display_name,
                    machine_binding_hash=excluded.machine_binding_hash;
                """;
            system.Parameters.AddWithValue("$id", identity.Value.ToString("N"));
            system.Parameters.AddWithValue("$name", displayName.Trim());
            system.Parameters.AddWithValue("$binding", canonicalBinding);
            system.Parameters.AddWithValue("$at", document.CapturedAtUtc.ToUnixTimeMilliseconds());
            await system.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        command.Parameters.AddWithValue("$json", document.Json);
        command.Parameters.AddWithValue("$sha", document.Sha256);
        command.Parameters.AddWithValue(
            "$captured",
            document.CapturedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void Validate(LocalInventoryDocumentPayload document)
    {
        ArgumentNullException.ThrowIfNull(document);
        DocumentPayloadLimits.EnsureEnvelopeFits(document.Json, document.CapturedAtUtc);
        if (string.IsNullOrWhiteSpace(document.DocumentId)
            || document.DocumentSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(document.DisplayName)
            || string.IsNullOrWhiteSpace(document.Json)
            || System.Text.Encoding.UTF8.GetByteCount(document.Json) > MaximumDocumentBytes
            || document.Sha256.Length != 64
            || !document.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The local inventory document is invalid.");
        }
    }
}
