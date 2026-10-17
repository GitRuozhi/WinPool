using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SimulationDocumentRepositoryTests
{
    [Fact]
    public async Task MetadataPagesStayBoundedWhileLargeDocumentsLoadIndividually()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var largeValue = new string('x', 2_200_000);
        var first = await repository.SaveAsync(Payload("simulation:a", largeValue, 1), null);
        var second = await repository.SaveAsync(Payload("simulation:b", largeValue, 1), null);

        var firstPage = await repository.ListMetadataAsync(1, null);
        var secondPage = await repository.ListMetadataAsync(1, firstPage.NextAfterDocumentId);

        Assert.Single(firstPage.Documents);
        Assert.NotNull(firstPage.NextAfterDocumentId);
        Assert.Single(secondPage.Documents);
        Assert.Null(secondPage.NextAfterDocumentId);
        Assert.True(Encoding.UTF8.GetByteCount(
            System.Text.Json.JsonSerializer.Serialize(firstPage)) < 4 * 1024 * 1024);
        Assert.True(first.Json.Length + second.Json.Length > 4 * 1024 * 1024);
        Assert.Equal(first.Sha256, (await repository.LoadAsync(first.DocumentId))!.Sha256);
        Assert.Equal(second.Sha256, (await repository.LoadAsync(second.DocumentId))!.Sha256);
    }

    [Fact]
    public async Task SaveUsesOptimisticHashAndIncrementsRevision()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var first = Payload("simulation:test", "one", 1);

        var saved = await repository.SaveAsync(first, null);
        var second = Payload(first.DocumentId, "two", 2);
        var updated = await repository.SaveAsync(second, saved.Sha256);

        Assert.Equal(1, saved.Revision);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(second.Sha256, (await repository.LoadAsync(first.DocumentId))!.Sha256);
        await Assert.ThrowsAsync<SimulationDocumentConflictException>(
            () => repository.SaveAsync(Payload(first.DocumentId, "stale", 2), saved.Sha256));
    }

    [Fact]
    public async Task EditCommitPersistsDocumentAndLightweightReceiptAtomically()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var initial = await repository.SaveAsync(Payload("simulation:test", "one", 1), null);
        var intent = Intent(initial.DocumentId, 1, "first", "two");
        var candidate = Payload(initial.DocumentId, "two", 2);
        var receipt = await repository.CommitEditAsync(candidate, initial.Sha256, intent);
        Assert.Equal(2, receipt.AfterRevision);
        Assert.Equal(intent.IntentHash, receipt.IntentHash);
        Assert.Equal(candidate.Sha256, (await repository.LoadAsync(initial.DocumentId))!.Sha256);
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_plans;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM execution_events;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ReceiptFailureRollsBackDocumentChange()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var initial = await repository.SaveAsync(Payload("simulation:test", "one", 1), null);
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON simulation_edit_commits BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => repository.CommitEditAsync(
            Payload(initial.DocumentId, "two", 2), initial.Sha256, Intent(initial.DocumentId, 1, "failed", "two")));
        Assert.Equal(initial.Sha256, (await repository.LoadAsync(initial.DocumentId))!.Sha256);
        Assert.Null(await repository.FindByCommitIdAsync("failed"));
    }

    [Fact]
    public async Task CommitIdBindsIntentAndKeepsAnImmutableReceiptWithoutHistoricalDocument()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var initial = await repository.SaveAsync(Payload("simulation:test", "one", 1), null);
        var intent = Intent(initial.DocumentId, 1, "fixed", "two");
        var edited = Payload(initial.DocumentId, "two", 2);
        var first = await repository.CommitEditAsync(edited, initial.Sha256, intent);
        var retry = await repository.CommitEditAsync(edited, initial.Sha256, intent);
        Assert.Equal(first, retry);
        await Assert.ThrowsAsync<SimulationDocumentConflictException>(() => repository.CommitEditAsync(
            Payload(initial.DocumentId, "different", 2), initial.Sha256,
            Intent(initial.DocumentId, 1, "fixed", "different")));
        await repository.CommitEditAsync(Payload(initial.DocumentId, "three", 3), edited.Sha256,
            Intent(initial.DocumentId, 2, "second", "three"));
        Assert.Equal(first, await repository.FindByCommitIdAsync("fixed"));
        Assert.Equal(edited.Sha256, first.AfterSha256);
        Assert.Equal(2, first.AfterRevision);
        await Assert.ThrowsAsync<SimulationDocumentConflictException>(() => repository.CommitEditAsync(
            Payload(initial.DocumentId, "stale", 2), initial.Sha256, Intent(initial.DocumentId, 1, "stale", "stale")));
        Assert.Null(await repository.FindByCommitIdAsync("stale"));
    }

    private static SimulationCommitIntent Intent(string id, long before, string commit, string name) =>
        new(id, before, commit, SimulationEditIntent.Freeze(
            [new SimulationEditRequest(SimulationEditKind.Rename, "physical:test", Name: name)],
            commit, DateTimeOffset.UnixEpoch));

    private static SimulationDocumentPayload Payload(string id, string value, long revision)
    {
        var json = $$"""{"Id":"{{id}}","SchemaVersion":1,"DisplayName":"Test","Kind":"Simulation","Revision":{{revision}},"Value":"{{value}}"}""";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new(id, 1, "Test", json, hash, revision, DateTimeOffset.UtcNow);
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
            var directory = Path.Combine(Path.GetTempPath(), "WinPool.Persistence.Tests", Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new(directory, store);
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
                    // The process-wide SQLite pool may still own the temporary file.
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
