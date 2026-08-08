using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class UserTestPresetRepositoryTests
{
    [Fact]
    public async Task SavesListsUpdatesAndDeletesNamespacedPresets()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.TestPreset.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WinPoolSqliteStore(Path.Combine(directory, "test.db"));
            await store.InitializeAsync();
            await using var lease = AgentWriteOwnerLease.Acquire(store, "agent");
            var writer = new UserTestPresetRepository(store, lease);
            var reader = new UserTestPresetRepository(store);
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            var preset = CreatePreset(now) with { Name = "  My preset  " };

            var saved = await writer.SaveAsync(preset, CancellationToken.None);
            var listed = Assert.Single(
                await reader.ListAsync(CancellationToken.None));
            Assert.Equal("My preset", saved.Name);
            Assert.Equal(saved, listed);

            var updated = saved with
            {
                Name = "Updated",
                QueueDepth = 32,
                UpdatedAtUtc = now.AddMinutes(1)
            };
            await writer.SaveAsync(updated, CancellationToken.None);
            Assert.Equal(
                updated,
                Assert.Single(await reader.ListAsync(CancellationToken.None)));
            Assert.True(await writer.DeleteAsync(
                preset.PresetId,
                CancellationToken.None));
            Assert.Empty(await reader.ListAsync(CancellationToken.None));
            Assert.False(await writer.DeleteAsync(
                preset.PresetId,
                CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsInvalidPresetAndReaderCannotWrite()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.TestPreset.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WinPoolSqliteStore(Path.Combine(directory, "test.db"));
            await store.InitializeAsync();
            var reader = new UserTestPresetRepository(store);
            var preset = CreatePreset(DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<AgentWriteOwnershipException>(
                () => reader.SaveAsync(preset, CancellationToken.None));
            await using var lease = AgentWriteOwnerLease.Acquire(store, "agent");
            var writer = new UserTestPresetRepository(store, lease);
            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.SaveAsync(
                    preset with { RepeatCount = 101 },
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static UserTestPreset CreatePreset(DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            "Preset",
            TestPresetScenario.IoBenchmark,
            new ToolId("microsoft.diskspd"),
            TestPresetVerificationMode.FullHash,
            50_505,
            IoAccessPattern.Random,
            30,
            1024L * 1024 * 1024,
            4096,
            4,
            32,
            60,
            5,
            2,
            3,
            true,
            now,
            now);
}
