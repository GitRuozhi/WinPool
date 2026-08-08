using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SystemSupportRecoveryRepositoryTests
{
    [Fact]
    public async Task PendingPowerAndSchedulingStateSurvivesRepositoryReopen()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var writer = new SystemSupportRecoveryRepository(database.Store, lease);
        var powerId = Guid.NewGuid();
        var schedulingId = Guid.NewGuid();
        var prepared = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await writer.SaveAsync(
            new(
                powerId,
                new string('a', 64),
                SystemSupportActionKind.UseTemporaryPowerPlan,
                new PowerPlanRecoveryState(new PowerPlanSnapshot(Guid.NewGuid())),
                prepared),
            CancellationToken.None);
        await writer.SaveAsync(
            new(
                schedulingId,
                new string('b', 64),
                SystemSupportActionKind.AdjustProcessScheduling,
                new ProcessSchedulingRecoveryState(
                    new TestProcessSchedulingSnapshot(
                        123,
                        true,
                        TestProcessPriority.AboveNormal,
                        [0, 2])),
                prepared.AddSeconds(1)),
            CancellationToken.None);

        var reader = new SystemSupportRecoveryRepository(database.Store);
        var pending = await reader.GetPendingAsync(CancellationToken.None);

        Assert.Equal(2, pending.Count);
        Assert.IsType<PowerPlanRecoveryState>(pending[0].State);
        var scheduling = Assert.IsType<ProcessSchedulingRecoveryState>(
            pending[1].State);
        Assert.Equal([0, 2], scheduling.Snapshot.LogicalProcessorIndices);

        await writer.RemoveAsync(powerId, CancellationToken.None);
        Assert.Equal(
            schedulingId,
            Assert.Single(
                await reader.GetPendingAsync(CancellationToken.None)).RecoveryId);
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
                "WinPool.Persistence.Recovery.Tests",
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
