using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SimulationDocumentRepositoryTests
{
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
        Assert.Equal(second.Sha256, Assert.Single(await repository.ListAsync()).Sha256);
        await Assert.ThrowsAsync<SimulationDocumentConflictException>(
            () => repository.SaveAsync(Payload(first.DocumentId, "stale", 2), saved.Sha256));
    }

    [Fact]
    public async Task EditCommitPersistsDocumentPlanStepsEventsAndLinkAtomically()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var initial = await repository.SaveAsync(Payload("simulation:test", "one", 1), null);
        var plan = Plan();
        var events = new[]
        {
            new ExecutionEvent(plan.OperationId, ExecutionEventKind.Started, plan.CreatedAt, "started", ""),
            new ExecutionEvent(plan.OperationId, ExecutionEventKind.Completed, plan.CreatedAt.AddMilliseconds(1), "completed", "")
        };

        var saved = await repository.CommitEditAsync(
            Payload(initial.DocumentId, "two", 2),
            initial.Sha256,
            plan,
            events);

        Assert.Equal(2, saved.Revision);
        var persistedPlan = await new OperationPlanRepository(database.Store).GetAsync(plan.OperationId);
        Assert.Equal(PersistedOperationState.Completed, persistedPlan!.State);
        Assert.Equal(2, (await new ExecutionEventRepository(database.Store)
            .ListAsync(plan.OperationId)).Count);
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM simulation_edit_commits WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", plan.OperationId.Value.ToString("N"));
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InvalidCompletedEventStreamRollsBackDocumentChange()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new SimulationDocumentRepository(database.Store, lease);
        var initial = await repository.SaveAsync(Payload("simulation:test", "one", 1), null);
        var plan = Plan();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CommitEditAsync(
            Payload(initial.DocumentId, "two", 2),
            initial.Sha256,
            plan,
            [new ExecutionEvent(plan.OperationId, ExecutionEventKind.Failed, plan.CreatedAt, "failed", "")])) ;

        var actual = Assert.Single(await repository.ListAsync());
        Assert.Equal(initial.Sha256, actual.Sha256);
        Assert.Null(await new OperationPlanRepository(database.Store).GetAsync(plan.OperationId));
    }

    private static SimulationDocumentPayload Payload(string id, string value, long revision)
    {
        var json = $$"""{"Id":"{{id}}","SchemaVersion":1,"DisplayName":"Test","Kind":"Simulation","Revision":{{revision}},"Value":"{{value}}"}""";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return new(id, 1, "Test", json, hash, revision, DateTimeOffset.UtcNow);
    }

    private static OperationPlan Plan()
    {
        var systemId = SystemId.New();
        var request = new OperationRequest(
            OperationId.New(),
            EnvironmentId.New(),
            systemId,
            OperationIntent.SimulateStorageMutation,
            [new StorageObjectId(systemId, StorageObjectKind.System, "system:test")],
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
        return OperationPlan.Create(
            request,
            ExecutionCapability.SimulateStorageMutation,
            RiskLevel.R1SimulationWrite,
            "test-v1",
            [],
            [new PlanStep("apply", "simulate", [])],
            0,
            "simulation",
            "restore",
            "none",
            new AlgorithmIdentity("ALG-TEST", "1", AlgorithmConfidence.Proven, "unit-test"),
            request.RequestedAt);
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
