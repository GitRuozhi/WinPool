using System.Data;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// Reads the last committed workspace selection and its active document before
/// the Agent finishes connecting. It never initializes or writes the database.
/// </summary>
public sealed class ReadOnlyWorkspaceStartupReader(string databasePath)
{
    public async Task<ReadOnlyWorkspaceStartupPreview?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);

        await using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "SELECT schema_version FROM schema_info WHERE singleton = 1;";
        var rawVersion = await version.ExecuteScalarAsync(cancellationToken);
        if (rawVersion is null)
        {
            throw new InvalidOperationException("The WinPool database has no schema version.");
        }

        var actualVersion = Convert.ToInt32(rawVersion, System.Globalization.CultureInfo.InvariantCulture);
        if (actualVersion < WinPoolSqliteStore.CurrentSchemaVersion)
        {
            throw new LegacySqliteSchemaNotSupportedException(actualVersion);
        }
        if (actualVersion > WinPoolSqliteStore.CurrentSchemaVersion)
        {
            throw new UnsupportedSqliteSchemaVersionException(
                actualVersion,
                WinPoolSqliteStore.CurrentSchemaVersion);
        }

        var state = await WorkspaceSessionStateRepository.ReadAsync(
            connection,
            transaction,
            cancellationToken);
        if (state is null || string.IsNullOrWhiteSpace(state.ActiveDocumentId))
        {
            return null;
        }

        await using (var simulation = connection.CreateCommand())
        {
            simulation.Transaction = transaction;
            simulation.CommandText = """
                SELECT document_id, document_schema_version, display_name,
                       sanitized_json, sha256, revision, updated_at_utc_ms
                FROM simulation_documents
                WHERE document_id = $id;
                """;
            simulation.Parameters.AddWithValue("$id", state.ActiveDocumentId);
            await using var reader = await simulation.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    return new ReadOnlyWorkspaceStartupPreview(
                        state,
                        null,
                        SimulationDocumentRepository.Read(reader));
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or InvalidDataException
                        or System.Text.Json.JsonException
                        or FormatException)
                {
                    // Missing or corrupt active documents use the normal
                    // startup fallback after the Agent validates its catalog.
                    return null;
                }
            }
        }

        // Avoid parsing the potentially large local inventory when the saved
        // selection is a simulation. The same deferred transaction keeps this
        // identity probe and subsequent payload read on one SQLite snapshot.
        await using var localIdentity = connection.CreateCommand();
        localIdentity.Transaction = transaction;
        localIdentity.CommandText = "SELECT document_id FROM local_inventory_document WHERE singleton = 1;";
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                await localIdentity.ExecuteScalarAsync(cancellationToken) as string,
                state.ActiveDocumentId))
        {
            return null;
        }

        PersistedLocalInventoryDocument? local;
        try
        {
            local = await LocalInventoryDocumentRepository.ReadAsync(
                connection,
                cancellationToken,
                transaction);
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidDataException or SqliteException)
        {
            // A damaged local cache is ignored; normal Agent startup will use
            // its existing cached-inventory fallback.
            return null;
        }

        return local is null
            ? null
            : new ReadOnlyWorkspaceStartupPreview(state, local.Document, null);
    }
}

public sealed record ReadOnlyWorkspaceStartupPreview(
    WorkspaceSessionState State,
    LocalInventoryDocumentPayload? LocalInventoryDocument,
    SimulationDocumentPayload? SimulationDocument);
