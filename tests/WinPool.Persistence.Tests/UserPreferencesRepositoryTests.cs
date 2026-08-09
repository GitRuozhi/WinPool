using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class UserPreferencesRepositoryTests
{
    [Fact]
    public async Task DefaultsAndRoundTripExcludeExecutionModeByType()
    {
        await using var database = await PreferenceDatabase.CreateAsync();
        var correlation = CorrelationId.New();
        var reader = new UserPreferencesRepository(database.Store);
        var defaults = await reader.LoadAsync(correlation, CancellationToken.None);
        Assert.True(defaults.IsSuccess);
        Assert.Equal(ThemePreference.System, defaults.Value!.Theme);

        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new UserPreferencesRepository(database.Store, lease);
        var expected = new UserPreferences(
            ThemePreference.Dark,
            AccentColorPreference.Purple,
            LanguagePreference.ZhCn,
            ShowHardwareIds: true,
            CreateMsrOnInitialize: false,
            ShowWelcomeAtStart: false,
            StartAgentAtLogin: true,
            ContinueMonitoringWhenUiCloses: true);
        Assert.True((await writer.SaveAsync(expected, correlation, CancellationToken.None)).IsSuccess);

        var actual = await reader.LoadAsync(correlation, CancellationToken.None);
        Assert.Equal(expected, actual.Value);

        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM preferences WHERE key='global';";
        var json = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain("ExecutionMode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyRepositoryCannotSave()
    {
        await using var database = await PreferenceDatabase.CreateAsync();
        var repository = new UserPreferencesRepository(database.Store);
        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => repository.SaveAsync(
                new UserPreferences(),
                CorrelationId.New(),
                CancellationToken.None));
    }

    private sealed class PreferenceDatabase : IAsyncDisposable
    {
        private PreferenceDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<PreferenceDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Preference.Tests",
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
