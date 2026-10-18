using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class InventoryRepositoryTests
{
    [Fact]
    public async Task LatestDocumentAndSystemIdentityCommitTogetherWithoutHistoricalCopies()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var systemId = SystemId.New();
        var payload = new LocalInventoryDocumentPayload("local:test", 3, "Test",
            "{\"kind\":\"local\"}", new string('a',64), DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000));
        var snapshotId = Guid.NewGuid();
        var repository = new LocalInventoryDocumentRepository(database.Store, lease);
        await repository.SaveAsync(snapshotId, payload, systemId, "Test", "canonical-binding");
        var loaded = await repository.LoadAsync();
        Assert.Equal(snapshotId, loaded!.SnapshotId);
        Assert.Equal(payload, loaded.Document);
        Assert.Equal(payload, await new ReadOnlyLocalInventoryReader(database.Store.DatabasePath).LoadAsync());
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine_binding_hash FROM systems WHERE system_id=$id;";
        command.Parameters.AddWithValue("$id", systemId.Value.ToString("N"));
        Assert.Equal("canonical-binding", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('inventory_snapshots','storage_objects','storage_relationships','inventory_comparisons');";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MissingStartupHistoryDoesNotCreateDatabaseOrDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPool.MissingHistory.Tests", Guid.NewGuid().ToString("N"));
        Assert.Null(await new ReadOnlyLocalInventoryReader(Path.Combine(directory, "winpool.db")).LoadAsync());
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(22)]
    public async Task StartupHistoryRejectsOtherSchemasWithoutChangingThem(int version)
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_info SET schema_version = $version;";
            command.Parameters.AddWithValue("$version", version);
            await command.ExecuteNonQueryAsync();
        }
        var reader = new ReadOnlyLocalInventoryReader(database.Store.DatabasePath);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => reader.LoadAsync());
        Assert.Equal(version, (await new SqliteSchemaVersionReader(database.Store).ReadAsync())!.Version);
    }

    [Fact]
    public async Task LocalIdentityResolverCreatesOneIdentityForConcurrentFirstCapture()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => resolver.ResolveAsync("LOCALHOST"))
            .ToArray();

        var resolutions = await Task.WhenAll(tasks);

        var canonical = Assert.Single(resolutions.Select(item => item.SystemId).Distinct());
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM systems
            WHERE kind = $kind AND machine_binding_hash = $binding;
            """;
        command.Parameters.AddWithValue("$kind", (int)PersistedSystemKind.Local);
        command.Parameters.AddWithValue(
            "$binding",
            LocalSystemIdentityResolver.CreateAuthorityBinding("LOCALHOST"));
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1, count);
        Assert.Equal(canonical, resolutions[0].SystemId);
    }

    [Fact]
    public async Task LocalIdentityResolverUsesStablePreferredFragmentAndStopsCreatingNewRows()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var first = SystemId.New();
        var preferred = SystemId.New();
        var binding = LocalSystemIdentityResolver.CreateAuthorityBinding("LOCALHOST");
        await SeedSystemAsync(database.Store, first, PersistedSystemKind.Local, "LOCALHOST", binding);
        await SeedSystemAsync(database.Store, preferred, PersistedSystemKind.Local, "LOCALHOST", binding);

        var resolved = await resolver.ResolveAsync("LOCALHOST", preferred);
        var repeated = await resolver.ResolveAsync("LOCALHOST", preferred);

        Assert.Equal(preferred, resolved.SystemId);
        Assert.Equal(preferred, repeated.SystemId);
        Assert.True(resolved.HasFragmentedHistory);
        Assert.True(repeated.HasFragmentedHistory);
    }

    [Fact]
    public async Task LocalIdentityResolverHonorsPreferredLocalIdWithStaleMetadata()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var preferred = SystemId.New();
        await SeedSystemAsync(database.Store, preferred, PersistedSystemKind.Local, "LOCALHOST",
            LocalSystemIdentityResolver.CreateAuthorityBinding("LOCALHOST"));
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE systems
                SET display_name = 'old-host', machine_binding_hash = 'old-binding'
                WHERE system_id = $system;
                """;
            command.Parameters.AddWithValue("$system", preferred.Value.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        var resolved = await resolver.ResolveAsync("LOCALHOST", preferred);

        Assert.Equal(preferred, resolved.SystemId);
    }

    [Fact]
    public async Task LocalIdentityResolverRejectsPreferredNonLocalId()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var simulation = SystemId.New();
        await SeedSystemAsync(database.Store, simulation, PersistedSystemKind.Simulation, "Simulation", "simulation-binding");

        var resolved = await resolver.ResolveAsync("LOCALHOST", simulation);

        Assert.NotEqual(simulation, resolved.SystemId);
    }

    private static async Task SeedSystemAsync(WinPoolSqliteStore store, SystemId systemId,
        PersistedSystemKind kind, string name, string binding)
    {
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO systems(system_id,kind,display_name,machine_binding_hash,created_at_utc_ms) VALUES($id,$kind,$name,$binding,1);";
        command.Parameters.AddWithValue("$id", systemId.Value.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$binding", binding);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class InventoryDatabase : IAsyncDisposable
    {
        private InventoryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }

        public WinPoolSqliteStore Store { get; }

        public static async Task<InventoryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.InventoryPersistence.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            Store.DrainConnectionPool();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
