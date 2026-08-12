using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class InventoryRepositoryTests
{
    [Fact]
    public async Task SnapshotRoundTripHashesIdsAndOmitsSensitiveFields()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new InventorySnapshotRepository(database.Store, lease);
        var snapshot = CreateSnapshot(
            "version-a",
            DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000));

        var saved = await repository.SaveAsync(
            snapshot,
            PersistedSystemKind.Local,
            "Test computer");
        var loaded = await new InventorySnapshotRepository(database.Store)
            .GetAsync(saved.SnapshotId);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.InventoryVersion, loaded.Snapshot.InventoryVersion);
        Assert.All(
            loaded.Snapshot.Objects,
            item =>
            {
                Assert.Equal(64, item.Id.ProviderKey.Length);
                Assert.DoesNotContain(
                    item.Properties.Keys,
                    key => key.Contains("serial", StringComparison.OrdinalIgnoreCase)
                           || key.Contains("guid", StringComparison.OrdinalIgnoreCase));
            });
        Assert.All(
            loaded.Snapshot.IdentityDiagnostics,
            item => Assert.Empty(item.DiagnosticText));
        var relationship = Assert.Single(loaded.Snapshot.Relationships!);
        Assert.Contains(
            loaded.Snapshot.Objects,
            item => item.Id == relationship.FromObjectId);
        Assert.Contains(
            loaded.Snapshot.Objects,
            item => item.Id == relationship.ToObjectId);

        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sanitized_json
            FROM inventory_snapshots
            WHERE snapshot_id = $snapshot;
            """;
        command.Parameters.AddWithValue(
            "$snapshot",
            saved.SnapshotId.ToString("N"));
        var databaseText = Assert.IsType<string>(
            await command.ExecuteScalarAsync());
        Assert.DoesNotContain("RAW-SYSTEM-ID", databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-VOLUME-ID", databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SERIAL", databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-DIAGNOSTIC", databaseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotListUsesStableNewestFirstPaging()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var writer = new InventorySnapshotRepository(database.Store, lease);
        var systemId = SystemId.New();
        var first = CreateSnapshot(
            "version-1",
            DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000),
            systemId);
        var second = CreateSnapshot(
            "version-2",
            first.CapturedAtUtc.AddMinutes(1),
            systemId);
        await writer.SaveAsync(first, PersistedSystemKind.Local, "Test");
        await writer.SaveAsync(second, PersistedSystemKind.Local, "Test");

        var reader = new InventorySnapshotRepository(database.Store);
        var page = await reader.ListAsync(systemId, 1);
        var older = await reader.ListAsync(
            systemId,
            5,
            page.Single().Snapshot.CapturedAtUtc);

        Assert.Equal("version-2", page.Single().Snapshot.InventoryVersion);
        Assert.Equal("version-1", older.Single().Snapshot.InventoryVersion);
    }

    [Fact]
    public async Task ComparisonRoundTripRedactsSensitiveDifferenceValues()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var snapshots = new InventorySnapshotRepository(database.Store, lease);
        var systemId = SystemId.New();
        var reference = await snapshots.SaveAsync(
            CreateSnapshot("left", DateTimeOffset.UtcNow, systemId),
            PersistedSystemKind.Local,
            "Test");
        var candidate = await snapshots.SaveAsync(
            CreateSnapshot("right", DateTimeOffset.UtcNow.AddSeconds(1), systemId),
            PersistedSystemKind.Local,
            "Test");
        var comparison = new InventoryComparison(
            "left",
            "right",
            false,
            [
                new(
                    InventoryDifferenceKind.PropertyMismatch,
                    null,
                    null,
                    "serialNumber",
                    "SECRET-LEFT",
                    "SECRET-RIGHT")
            ]);
        var writer = new InventoryComparisonRepository(database.Store, lease);

        var saved = await writer.SaveAsync(
            reference.SnapshotId,
            candidate.SnapshotId,
            comparison);
        var loaded = await new InventoryComparisonRepository(database.Store)
            .GetAsync(saved.ComparisonId);

        Assert.NotNull(loaded);
        var difference = Assert.Single(loaded.Comparison.Differences);
        Assert.Equal("[redacted]", difference.ReferenceValue);
        Assert.Equal("[redacted]", difference.CandidateValue);
    }

    [Fact]
    public async Task ReadOnlyInventoryRepositoriesCannotWrite()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        var snapshot = CreateSnapshot("version", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => new InventorySnapshotRepository(database.Store).SaveAsync(
                snapshot,
                PersistedSystemKind.Local,
                "Test"));
    }

    [Fact]
    public async Task LatestSanitizedManageDocumentIsBoundToItsNormalizedSnapshot()
    {
        await using var database = await InventoryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var snapshot = await new InventorySnapshotRepository(database.Store, lease)
            .SaveAsync(
                CreateSnapshot("manage", DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000)),
                PersistedSystemKind.Local,
                "Test");
        var payload = new LocalInventoryDocumentPayload(
            "local:test",
            2,
            "Test",
            "{\"kind\":\"local\"}",
            new string('a', 64),
            DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000));
        var repository = new LocalInventoryDocumentRepository(database.Store, lease);

        await repository.SaveAsync(snapshot.SnapshotId, payload);
        var loaded = await repository.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SnapshotId, loaded.SnapshotId);
        Assert.Equal(payload, loaded.Document);
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
        var snapshots = new InventorySnapshotRepository(database.Store, lease);
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var first = SystemId.New();
        var preferred = SystemId.New();
        var binding = LocalSystemIdentityResolver.CreateAuthorityBinding("LOCALHOST");
        await snapshots.SaveAsync(
            CreateSnapshot("first", DateTimeOffset.FromUnixTimeMilliseconds(1), first),
            PersistedSystemKind.Local,
            "LOCALHOST",
            canonicalLocalSystemBinding: binding);
        await snapshots.SaveAsync(
            CreateSnapshot("preferred", DateTimeOffset.FromUnixTimeMilliseconds(2), preferred),
            PersistedSystemKind.Local,
            "LOCALHOST",
            canonicalLocalSystemBinding: binding);

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
        var snapshots = new InventorySnapshotRepository(database.Store, lease);
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var preferred = SystemId.New();
        await snapshots.SaveAsync(
            CreateSnapshot("preferred", DateTimeOffset.FromUnixTimeMilliseconds(1), preferred),
            PersistedSystemKind.Local,
            "LOCALHOST",
            canonicalLocalSystemBinding:
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
        var snapshots = new InventorySnapshotRepository(database.Store, lease);
        var resolver = new LocalSystemIdentityResolver(database.Store, lease);
        var simulation = SystemId.New();
        await snapshots.SaveAsync(
            CreateSnapshot("simulation", DateTimeOffset.FromUnixTimeMilliseconds(1), simulation),
            PersistedSystemKind.Simulation,
            "Simulation");

        var resolved = await resolver.ResolveAsync("LOCALHOST", simulation);

        Assert.NotEqual(simulation, resolved.SystemId);
    }

    private static InventorySnapshot CreateSnapshot(
        string version,
        DateTimeOffset capturedAt,
        SystemId? existingSystemId = null)
    {
        var systemId = existingSystemId ?? SystemId.New();
        var system = new StorageObjectId(
            systemId,
            StorageObjectKind.System,
            "RAW-SYSTEM-ID");
        var volume = new StorageObjectId(
            systemId,
            StorageObjectKind.Partition,
            "RAW-VOLUME-ID");
        return new(
            systemId,
            InventoryProviderKind.NativeWindows,
            version,
            new string('a', 64),
            capturedAt,
            [
                new(
                    system,
                    null,
                    "Computer",
                    IdentityStability.Stable,
                    new Dictionary<string, string?>
                    {
                        ["serialNumber"] = "SECRET-SERIAL",
                        ["model"] = "Test"
                    }),
                new(
                    volume,
                    system,
                    "C:",
                    IdentityStability.Stable,
                    new Dictionary<string, string?>
                    {
                        ["volumeGuid"] = "SECRET-GUID",
                        ["fileSystem"] = "NTFS"
                    })
            ],
            [
                new(
                    volume,
                    IdentityStability.Stable,
                    "test.identity",
                    "SECRET-DIAGNOSTIC")
            ],
            [new(system, volume, "contains")]);
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
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
