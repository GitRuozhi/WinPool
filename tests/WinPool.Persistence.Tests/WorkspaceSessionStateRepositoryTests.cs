using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class WorkspaceSessionStateRepositoryTests
{
    [Fact]
    public async Task SaveRequiresAgentWriteOwnership()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new WorkspaceSessionStateRepository(database.Store);

        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => repository.SaveAsync(State("simulation:one"), CancellationToken.None));
    }

    [Fact]
    public async Task AgentOwnedStateRoundTripsAndCanBeReplaced()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new WorkspaceSessionStateRepository(database.Store, lease);

        await repository.SaveAsync(State("simulation:one"), CancellationToken.None);
        await repository.SaveAsync(
            State("simulation:two") with
            {
                ActivePage = WorkspacePage.Monitor,
                HighlightedTopologyProviderKey = "partition:test"
            },
            CancellationToken.None);

        var loaded = Assert.IsType<WorkspaceSessionState>(
            await repository.LoadAsync(CancellationToken.None));
        Assert.Equal("simulation:two", loaded.ActiveDocumentId);
        Assert.Equal(WorkspacePage.Monitor, loaded.ActivePage);
        Assert.Equal("partition:test", loaded.HighlightedTopologyProviderKey);
        Assert.Equal("pool:test", loaded.RememberedProviderKeys[ManageWorkspaceCategory.Pool]);
        Assert.Equal(TimeSpan.Zero, loaded.UpdatedAtUtc.Offset);
    }

    [Fact]
    public async Task CorruptPreferenceIsIgnored()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO preferences(key, json, updated_at_utc_ms)
                VALUES('workspace.session.v1', '{', 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await new WorkspaceSessionStateRepository(database.Store)
            .LoadAsync(CancellationToken.None));
    }

    private static WorkspaceSessionState State(string documentId) =>
        new(
            WorkspaceSessionState.CurrentSchemaVersion,
            WorkspacePage.Manage,
            documentId,
            ManageWorkspaceCategory.Pool,
            new Dictionary<ManageWorkspaceCategory, string>
            {
                [ManageWorkspaceCategory.Pool] = "pool:test"
            },
            string.Empty,
            DateTimeOffset.Now);

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
                    System.IO.Directory.Delete(Directory, true);
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
