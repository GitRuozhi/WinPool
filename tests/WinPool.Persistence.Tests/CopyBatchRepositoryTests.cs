using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class CopyBatchRepositoryTests
{
    [Fact]
    public async Task ManifestAndEntriesRoundTripAtomicallyAndIdempotently()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease =
            AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var manifest = await CreateRunAndManifestAsync(
            database.Store,
            lease);
        var writer = new CopyBatchRepository(database.Store, lease);

        Assert.True(
            await writer.SaveManifestAsync(
                manifest,
                CancellationToken.None));
        Assert.False(
            await writer.SaveManifestAsync(
                manifest,
                CancellationToken.None));

        var persisted = await new CopyBatchRepository(database.Store)
            .GetManifestAsync(
                manifest.RunId,
                manifest.StepId,
                CancellationToken.None);
        var checkpoints = await new CopyBatchRepository(database.Store)
            .ListEntryCheckpointsAsync(
                manifest.RunId,
                manifest.StepId,
                CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(manifest.ManifestHash, persisted.ManifestHash);
        Assert.Equal(manifest.PlanHash, persisted.PlanHash);
        Assert.Equal(manifest.Algorithm, persisted.Algorithm);
        Assert.Equal(manifest.Entries, persisted.Entries);
        Assert.Equal(manifest.Batches, persisted.Batches);
        Assert.Equal(2, checkpoints.Count);
        Assert.All(
            checkpoints,
            item =>
            {
                Assert.Equal(CopyBatchEntryState.Pending, item.State);
                Assert.Equal(0, item.Attempts);
            });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.SaveManifestAsync(
                Rehash(manifest with { BatchThresholdBytes = 101 }),
                CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedCopyReturnsOnlyCopyingEntriesToPending()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease =
            AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var manifest = await CreateRunAndManifestAsync(
            database.Store,
            lease);
        var repository = new CopyBatchRepository(database.Store, lease);
        await repository.SaveManifestAsync(
            manifest,
            CancellationToken.None);
        var started = manifest.CreatedAtUtc.AddSeconds(1);
        await repository.UpdateEntryCheckpointAsync(
            new(
                manifest.RunId,
                manifest.StepId,
                0,
                CopyBatchEntryState.Copying,
                1,
                null,
                "copy.started",
                started),
            CancellationToken.None);
        await repository.UpdateEntryCheckpointAsync(
            new(
                manifest.RunId,
                manifest.StepId,
                1,
                CopyBatchEntryState.Completed,
                0,
                0,
                "copy.recovery.target_accepted",
                started),
            CancellationToken.None);

        var recoveredAt = started.AddMinutes(1);
        await repository.MarkOpenBatchInterruptedAsync(
            manifest.RunId,
            manifest.StepId,
            recoveredAt,
            CancellationToken.None);
        var recovered = await repository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            CancellationToken.None);

        Assert.Equal(CopyBatchEntryState.Pending, recovered[0].State);
        Assert.Equal(1, recovered[0].Attempts);
        Assert.Equal("copy.recovery.interrupted", recovered[0].DiagnosticCode);
        Assert.Equal(CopyBatchEntryState.Completed, recovered[1].State);
        Assert.Equal(
            (long)CopyBatchState.Interrupted,
            await ScalarAsync(
                database.Store,
                """
                SELECT state FROM copy_batches
                WHERE run_id=$run AND step_id=$step AND batch_number=1;
                """,
                manifest));

        await repository.UpdateEntryCheckpointAsync(
            recovered[0] with
            {
                State = CopyBatchEntryState.Copying,
                Attempts = 2,
                DiagnosticCode = "copy.restarted",
                UpdatedAtUtc = recoveredAt.AddSeconds(1)
            },
            CancellationToken.None);
        await repository.UpdateEntryCheckpointAsync(
            recovered[0] with
            {
                State = CopyBatchEntryState.Completed,
                Attempts = 2,
                LastExitCode = 1,
                DiagnosticCode = "copy.completed",
                UpdatedAtUtc = recoveredAt.AddSeconds(2)
            },
            CancellationToken.None);

        Assert.Equal(
            (long)CopyBatchState.Completed,
            await ScalarAsync(
                database.Store,
                """
                SELECT state FROM copy_batches
                WHERE run_id=$run AND step_id=$step AND batch_number=1;
                """,
                manifest));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpdateEntryCheckpointAsync(
                recovered[1] with
                {
                    State = CopyBatchEntryState.Copying,
                    Attempts = 1
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task BulkStartAndRecoveryReportPersistOneAtomicDecisionPerEntry()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease =
            AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var manifest = await CreateRunAndManifestAsync(
            database.Store,
            lease);
        var repository = new CopyBatchRepository(database.Store, lease);
        await repository.SaveManifestAsync(
            manifest,
            CancellationToken.None);
        var started = manifest.CreatedAtUtc.AddSeconds(1);

        await repository.MarkEntriesCopyingAsync(
            manifest.RunId,
            manifest.StepId,
            [0],
            started,
            CancellationToken.None);
        var scoped = await repository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            CancellationToken.None);
        Assert.Equal(CopyBatchEntryState.Copying, scoped[0].State);
        Assert.Equal(CopyBatchEntryState.Pending, scoped[1].State);
        await repository.MarkOpenBatchInterruptedAsync(
            manifest.RunId,
            manifest.StepId,
            started.AddMilliseconds(1),
            CancellationToken.None);
        var interrupted = await repository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            CancellationToken.None);
        await repository.UpdateEntryCheckpointAsync(
            interrupted[0] with
            {
                State = CopyBatchEntryState.Failed,
                LastExitCode = 8,
                DiagnosticCode = "copy.process_failed",
                UpdatedAtUtc = started.AddMilliseconds(2)
            },
            CancellationToken.None);

        await repository.MarkPendingEntriesCopyingAsync(
            manifest.RunId,
            manifest.StepId,
            started,
            CancellationToken.None);

        var copying = await repository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            CancellationToken.None);
        Assert.All(
            copying,
            item =>
            {
                Assert.Equal(CopyBatchEntryState.Copying, item.State);
                Assert.Equal(item.Ordinal == 0 ? 2 : 1, item.Attempts);
            });
        var report = new CopyBatchRecoveryReport(
            manifest.ManifestHash,
            1,
            1,
            0,
            [
                new(
                    0,
                    "a.dat",
                    CopyBatchRecoveryDecision.AcceptCompletedTarget,
                    "copy.recovery.target_accepted"),
                new(
                    1,
                    "b.dat",
                    CopyBatchRecoveryDecision.Pending,
                    "copy.recovery.target_missing")
            ]);
        await repository.ApplyRecoveryReportAsync(
            manifest.RunId,
            manifest.StepId,
            report,
            started.AddSeconds(1),
            CancellationToken.None);

        var recovered = await repository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            CancellationToken.None);
        Assert.Equal(CopyBatchEntryState.Completed, recovered[0].State);
        Assert.Equal(CopyBatchEntryState.Pending, recovered[1].State);
        Assert.Equal(
            (long)CopyBatchState.Pending,
            await ScalarAsync(
                database.Store,
                """
                SELECT state FROM copy_batches
                WHERE run_id=$run AND step_id=$step AND batch_number=1;
                """,
                manifest));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.ApplyRecoveryReportAsync(
                manifest.RunId,
                manifest.StepId,
                report with { Items = report.Items.Take(1).ToArray() },
                started.AddSeconds(2),
                CancellationToken.None));
    }

    private static async Task<CopyBatchManifest> CreateRunAndManifestAsync(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease lease)
    {
        var definitionId = TestDefinitionId.New();
        var runId = TestRunId.New();
        var stepId = "copy-batch";
        var definition = new TestDefinition(
            definitionId,
            "copy batch",
            "1",
            new Dictionary<string, TestParameter>(),
            [],
            [],
            AlgorithmConfidence.Derived);
        var systemId = SystemId.New();
        var target = new TestTarget(
            systemId,
            new StorageObjectId(
                systemId,
                StorageObjectKind.Partition,
                "copy-batch-test"),
            Path.GetTempPath(),
            1024,
            true);
        var workspace = new TestWorkspacePlan(
            target.TestRootDirectory,
            Path.Combine(target.TestRootDirectory, "run"),
            [],
            1024,
            TestWorkspaceCleanupPolicy.KeepAll,
            DateTimeOffset.FromUnixTimeSeconds(1_900_000_000));
        var step = new TestStep(
            stepId,
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>(),
            [],
            true);
        var created = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var plan = new TestPlan(
            runId,
            definitionId,
            "1",
            target,
            workspace,
            [step],
            [],
            [new ToolId("windows.robocopy")],
            30,
            RiskLevel.R2RecoverableFileWrite,
            new(
                "ALG-TEST",
                "1",
                AlgorithmConfidence.Derived,
                "test"),
            created,
            new string('a', 64));
        var runs = new TestRunRepository(store, lease);
        await runs.SaveDefinitionAsync(definition, created);
        await runs.CreateRunAsync(plan, "{}");
        return Rehash(
            new(
                runId,
                stepId,
                plan.PlanHash,
                new string('b', 64),
                new string('c', 64),
                100,
                10,
                [
                    new(
                        0,
                        1,
                        "a.dat",
                        10,
                        created.UtcTicks,
                        FileAttributes.Normal,
                        null),
                    new(
                        1,
                        1,
                        "b.dat",
                        20,
                        created.UtcTicks,
                        FileAttributes.Normal,
                        null)
                ],
                [new(1, 30, 2)],
                new(
                    "ALG-COPY-BATCH-001",
                    "1.0.0",
                    AlgorithmConfidence.Derived,
                    "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §6"),
                created,
                string.Empty));
    }

    private static CopyBatchManifest Rehash(CopyBatchManifest manifest) =>
        manifest with
        {
            ManifestHash = CopyBatchManifestHash.Compute(
                manifest with { ManifestHash = string.Empty })
        };

    private static async Task<long> ScalarAsync(
        WinPoolSqliteStore store,
        string sql,
        CopyBatchManifest manifest)
    {
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "$run",
            manifest.RunId.Value.ToString("N"));
        command.Parameters.AddWithValue("$step", manifest.StepId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
                "WinPool.Persistence.CopyBatch.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(
                Path.Combine(directory, "winpool.db"));
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
