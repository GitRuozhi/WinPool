using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Execution;

namespace WinPool.Infrastructure.Sqlite;

public sealed class SimulationDocumentConflictException(string message)
    : InvalidOperationException(message);

public sealed class SimulationDocumentRepository
{
    private const int MaximumDocumentBytes = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
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

    public async Task<IReadOnlyList<SimulationDocumentPayload>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_id, document_schema_version, display_name,
                   sanitized_json, sha256, revision, updated_at_utc_ms
            FROM simulation_documents
            ORDER BY display_name COLLATE NOCASE, document_id;
            """;
        var documents = new List<SimulationDocumentPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(Read(reader));
        }

        return documents;
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

    public async Task<SimulationDocumentPayload> CommitEditAsync(
        SimulationDocumentPayload document,
        string expectedPreviousSha256,
        OperationPlan plan,
        IReadOnlyList<ExecutionEvent> events,
        CancellationToken cancellationToken = default)
    {
        Validate(document);
        ValidateExpectedHash(expectedPreviousSha256, allowNull: false);
        if (document.Revision < 2)
        {
            throw new ArgumentException(
                "A simulation edit must advance its document revision.",
                nameof(document));
        }
        ValidatePlan(plan, events);
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
        await InsertPlanAsync(connection, transaction, plan, cancellationToken);
        await InsertEventsAsync(connection, transaction, events, cancellationToken);
        await using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = """
            INSERT INTO simulation_edit_commits(
                operation_id, document_id, before_sha256, after_sha256,
                document_revision, committed_at_utc_ms)
            VALUES($operation, $document, $before, $after, $revision, $committed);
            """;
        link.Parameters.AddWithValue("$operation", Id(plan.OperationId.Value));
        link.Parameters.AddWithValue("$document", saved.DocumentId);
        link.Parameters.AddWithValue("$before", expectedPreviousSha256);
        link.Parameters.AddWithValue("$after", saved.Sha256);
        link.Parameters.AddWithValue("$revision", saved.Revision);
        link.Parameters.AddWithValue("$committed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await link.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
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
        command.Parameters.AddWithValue("$name", document.DisplayName.Trim());
        command.Parameters.AddWithValue("$json", document.SanitizedJson);
        command.Parameters.AddWithValue("$sha", document.Sha256);
        command.Parameters.AddWithValue("$targetRevision", document.Revision);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", document.UpdatedAtUtc.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SimulationDocumentConflictException(
                "The simulation document was created or changed by another client.");
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT document_id, document_schema_version, display_name,
                   sanitized_json, sha256, revision, updated_at_utc_ms
            FROM simulation_documents WHERE document_id = $id;
            """;
        read.Parameters.AddWithValue("$id", document.DocumentId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The saved simulation document could not be read.");
        }

        return Read(reader);
    }

    private static async Task InsertPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_plans(
                operation_id, plan_hash, environment_id, risk, state,
                sanitized_json, created_at_utc_ms)
            VALUES($operation, $hash, $environment, $risk, $state, $json, $created);
            """;
        command.Parameters.AddWithValue("$operation", Id(plan.OperationId.Value));
        command.Parameters.AddWithValue("$hash", plan.PlanHash);
        command.Parameters.AddWithValue("$environment", Id(plan.EnvironmentId.Value));
        command.Parameters.AddWithValue("$risk", (int)plan.Risk);
        command.Parameters.AddWithValue("$state", (int)PersistedOperationState.Completed);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, JsonOptions));
        command.Parameters.AddWithValue("$created", plan.CreatedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            await using var step = connection.CreateCommand();
            step.Transaction = transaction;
            step.CommandText = """
                INSERT INTO operation_steps(
                    operation_id, step_id, sequence_no, state, sanitized_json)
                VALUES($operation, $step, $sequence, $state, $json);
                """;
            step.Parameters.AddWithValue("$operation", Id(plan.OperationId.Value));
            step.Parameters.AddWithValue("$step", plan.Steps[index].Id);
            step.Parameters.AddWithValue("$sequence", index);
            step.Parameters.AddWithValue("$state", (int)ApplicationTaskState.Succeeded);
            step.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan.Steps[index], JsonOptions));
            await step.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ExecutionEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var item in events)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution_events(
                    operation_id, timestamp_utc_ms, kind, code, sanitized_message)
                VALUES($operation, $timestamp, $kind, $code, $message);
                """;
            command.Parameters.AddWithValue("$operation", Id(item.OperationId.Value));
            command.Parameters.AddWithValue("$timestamp", item.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$kind", (int)item.Kind);
            command.Parameters.AddWithValue("$code", item.Code.Trim());
            command.Parameters.AddWithValue("$message", item.Message ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidatePlan(OperationPlan plan, IReadOnlyList<ExecutionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(events);
        if (plan.OperationId.Value == Guid.Empty || string.IsNullOrWhiteSpace(plan.PlanHash))
        {
            throw new ArgumentException("The operation plan identity is incomplete.", nameof(plan));
        }
        if (events.Count == 0
            || events.Any(item => item.OperationId != plan.OperationId)
            || events[^1].Kind != ExecutionEventKind.Completed)
        {
            throw new ArgumentException("A completed, operation-matched event stream is required.", nameof(events));
        }
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
        var bytes = Encoding.UTF8.GetBytes(document.SanitizedJson ?? string.Empty);
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

    private static SimulationDocumentPayload Read(SqliteDataReader reader)
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

    private static string Id(Guid value) => value.ToString("N");
}
