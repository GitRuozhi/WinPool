using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class LocalAgentPreferencesServiceTests
{
    [Fact]
    public async Task SaveStampsSavedAtUtcAndRoundTripsValues()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalAgentPreferencesService(location.Root);

        var first = await service.SaveAsync(new AgentPreferences(
            ContinuousMonitoringEnabled: true,
            MonitoringSampleRateHz: 12.5,
            StartAgentAtLogin: true));

        Assert.NotEqual(default, first.SavedAtUtc);
        var loaded = await service.LoadAsync();
        Assert.True(loaded.ContinuousMonitoringEnabled);
        Assert.Equal(12.5, loaded.MonitoringSampleRateHz);
        Assert.True(loaded.StartAgentAtLogin);
        Assert.Equal(first.SavedAtUtc, loaded.SavedAtUtc);
    }

    [Fact]
    public async Task EachSaveProducesAFreshSavedAtUtcLabel()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalAgentPreferencesService(location.Root);

        var first = await service.SaveAsync(new AgentPreferences());
        var second = await service.SaveAsync(new AgentPreferences());

        Assert.NotEqual(first.SavedAtUtc, second.SavedAtUtc);
    }

    [Fact]
    public async Task SaveClampsSampleRateAndLeavesNoTemporaryFiles()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalAgentPreferencesService(location.Root);

        await service.SaveAsync(new AgentPreferences(MonitoringSampleRateHz: 99));

        Assert.Equal(20, (await service.LoadAsync()).MonitoringSampleRateHz);
        Assert.Empty(Directory.EnumerateFiles(
            location.Root,
            "agent-settings.json.tmp-*"));
    }

    [Fact]
    public async Task UnreadableFileBlocksWritesUntilItBecomesReadable()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalAgentPreferencesService(location.Root);
        await service.SaveAsync(new AgentPreferences(StartAgentAtLogin: true));

        // Corrupt content simulates a file that exists but cannot be read
        // into a valid document. Defaults come back, but must never be
        // persisted over the unreadable original.
        await File.WriteAllTextAsync(service.AgentSettingsPath, "{ not json");

        var loaded = await service.LoadAsync();
        Assert.False(loaded.StartAgentAtLogin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(new AgentPreferences(StartAgentAtLogin: false)));
        Assert.Equal(
            "{ not json",
            await File.ReadAllTextAsync(service.AgentSettingsPath));

        await File.WriteAllTextAsync(
            service.AgentSettingsPath,
            "{}");
        var recovered = await service.LoadAsync();
        Assert.False(recovered.StartAgentAtLogin);
        var saved = await service.SaveAsync(new AgentPreferences(StartAgentAtLogin: true));
        Assert.True((await service.LoadAsync()).StartAgentAtLogin);
        Assert.NotEqual(default, saved.SavedAtUtc);
    }

    [Fact]
    public async Task MissingFileYieldsDefaultsAndAllowsWrites()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalAgentPreferencesService(location.Root);

        var loaded = await service.LoadAsync();
        Assert.False(loaded.ContinuousMonitoringEnabled);
        Assert.Equal(5, loaded.MonitoringSampleRateHz);

        await service.SaveAsync(new AgentPreferences(ContinuousMonitoringEnabled: true));
        Assert.True((await service.LoadAsync()).ContinuousMonitoringEnabled);
    }

    private sealed class TemporaryLocation(string root) : IDisposable
    {
        public string Root { get; } = root;

        public static TemporaryLocation Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.LocalAgentPreferences.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryLocation(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
