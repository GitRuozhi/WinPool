using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class ReadOnlyWorkspaceStartupReaderTests
{
    [Fact]
    public async Task LoadsOnlyThePersistedActiveSimulationAndState()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        SimulationDocumentPayload? document = null;
        for (var revision = 1; revision <= 4; revision++)
        {
            document = await repository.SaveAsync(
                Payload("simulation:active", revision),
                document?.Sha256);
        }
        var savedDocument = document!;
        var state = State(savedDocument.DocumentId);
        await new WorkspaceSessionStateRepository(database.Store, lease)
            .SaveAsync(state, CancellationToken.None);

        var preview = await new ReadOnlyWorkspaceStartupReader(database.Store.DatabasePath)
            .LoadAsync();

        Assert.NotNull(preview);
        Assert.Equal(state.ActiveDocumentId, preview.State.ActiveDocumentId);
        Assert.Equal(state.ActivePage, preview.State.ActivePage);
        Assert.Equal(state.ActiveCategory, preview.State.ActiveCategory);
        Assert.Equal(state.UpdatedAtUtc, preview.State.UpdatedAtUtc);
        Assert.Empty(preview.State.RememberedProviderKeys);
        Assert.Equal(savedDocument, preview.SimulationDocument);
        Assert.Null(preview.LocalInventoryDocument);
    }

    [Fact]
    public async Task MissingOrCorruptActiveDocumentsDoNotProduceAPreview()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var document = Payload("simulation:active", revision: 1);
        await repository.SaveAsync(document, expectedPreviousSha256: null);
        await new WorkspaceSessionStateRepository(database.Store, lease)
            .SaveAsync(State("simulation:missing"), CancellationToken.None);

        var reader = new ReadOnlyWorkspaceStartupReader(database.Store.DatabasePath);
        Assert.Null(await reader.LoadAsync());

        await new WorkspaceSessionStateRepository(database.Store, lease)
            .SaveAsync(State(document.DocumentId), CancellationToken.None);
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE simulation_documents SET sha256 = $hash WHERE document_id = $id;";
            command.Parameters.AddWithValue("$hash", new string('0', 64));
            command.Parameters.AddWithValue("$id", document.DocumentId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await reader.LoadAsync());
    }

    [Fact]
    public async Task LoadsTheActiveLocalInventoryOnlyAfterItsIdMatchesSavedState()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var local = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "local:startup-preview",
            StorageSystemKind.Local,
            "Startup local",
            StorageSnapshot.Empty("Startup local"),
            [],
            DateTimeOffset.UtcNow);
        local.ValidateCurrentFormat();
        var json = JsonSerializer.Serialize(local, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        var payload = new LocalInventoryDocumentPayload(
            local.Id,
            local.SchemaVersion,
            local.DisplayName,
            json,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            local.UpdatedAt);
        var persistedSnapshot = await new InventorySnapshotRepository(database.Store, lease)
            .SaveAsync(
                new InventorySnapshot(
                    local.SystemId,
                    InventoryProviderKind.EmbeddedReadOnlyPowerShell,
                    "startup-preview",
                    new string('a', 64),
                    local.UpdatedAt,
                    [],
                    []),
                PersistedSystemKind.Local,
                local.DisplayName);
        await new LocalInventoryDocumentRepository(database.Store, lease)
            .SaveAsync(persistedSnapshot.SnapshotId, payload);
        await new WorkspaceSessionStateRepository(database.Store, lease)
            .SaveAsync(State(local.Id), CancellationToken.None);

        var preview = await new ReadOnlyWorkspaceStartupReader(database.Store.DatabasePath)
            .LoadAsync();

        Assert.NotNull(preview);
        Assert.Equal(local.Id, preview.State.ActiveDocumentId);
        Assert.Equal(payload.DocumentId, preview.LocalInventoryDocument?.DocumentId);
        Assert.Null(preview.SimulationDocument);
    }

    [Fact]
    public async Task CorruptWorkspaceStateFallsBackWithoutShowingAnActiveDocument()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO workspace_state(singleton, json, updated_at_utc_ms)
                VALUES(1, '{', 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await new ReadOnlyWorkspaceStartupReader(database.Store.DatabasePath)
            .LoadAsync());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public async Task UnsupportedSchemasAreRejectedWithoutMutation(int schemaVersion)
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_info SET schema_version = $version WHERE singleton = 1;";
            command.Parameters.AddWithValue("$version", schemaVersion);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => new ReadOnlyWorkspaceStartupReader(database.Store.DatabasePath).LoadAsync());

        await using var verifyConnection = await database.Store.OpenConnectionAsync();
        await using var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT schema_version FROM schema_info WHERE singleton = 1;";
        Assert.Equal(schemaVersion, Convert.ToInt32(await verify.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task MissingDatabaseIsNotCreated()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Persistence.Tests",
            Guid.NewGuid().ToString("N"));

        Assert.Null(await new ReadOnlyWorkspaceStartupReader(Path.Combine(directory, "winpool.db"))
            .LoadAsync());
        Assert.False(Directory.Exists(directory));
    }

    private static WorkspaceSessionState State(string documentId) =>
        new(
            WorkspaceSessionState.CurrentSchemaVersion,
            WorkspacePage.Manage,
            documentId,
            ManageWorkspaceCategory.System,
            new Dictionary<ManageWorkspaceCategory, string>(),
            string.Empty,
            DateTimeOffset.UtcNow);

    private static SimulationDocumentPayload Payload(string id, long revision)
    {
        var json = $$"""{"Id":"{{id}}","SchemaVersion":1,"DisplayName":"Startup preview","Kind":"Simulation","Revision":{{revision}}}""";
        return new SimulationDocumentPayload(
            id,
            1,
            "Startup preview",
            json,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            revision,
            DateTimeOffset.UtcNow);
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Persistence.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new TemporaryDatabase(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                try
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite pooling can briefly retain the temporary database.
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
