using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.ToolManagement;

namespace WinPool.Infrastructure.Tests;

public sealed class PreferencesToolPathConfigurationTests
{
    [Fact]
    public async Task WritesCustomPathIntoUserPreferencesAndKeepsOtherPreferences()
    {
        var service = new MemoryPreferencesService(new UserPreferences(
            Theme: ThemePreference.Dark,
            CustomToolPaths: new Dictionary<string, string>
            {
                ["fio"] = @"D:\Tools\fio.exe"
            }));
        var configuration = await PreferencesToolPathConfiguration.CreateAsync(service);

        await configuration.SetCustomExecutablePathAsync(
            new ToolId("microsoft.diskspd"),
            @"D:\Tools\diskspd.exe",
            CancellationToken.None);

        Assert.Equal(ThemePreference.Dark, service.Current.Theme);
        Assert.Equal(1, service.Current.FormatVersion);
        Assert.Equal(@"D:\Tools\fio.exe", service.Current.CustomToolPaths!["fio"]);
        Assert.Equal(
            @"D:\Tools\diskspd.exe",
            configuration.GetCustomExecutablePath(new ToolId("microsoft.diskspd")));
    }

    [Fact]
    public async Task ClearingCustomPathRemovesOnlyRequestedTool()
    {
        var service = new MemoryPreferencesService(new UserPreferences(
            CustomToolPaths: new Dictionary<string, string>
            {
                ["fio"] = @"D:\Tools\fio.exe"
            }));
        var configuration = await PreferencesToolPathConfiguration.CreateAsync(service);

        await configuration.SetCustomExecutablePathAsync(
            new ToolId("fio"),
            null,
            CancellationToken.None);

        Assert.Null(configuration.GetCustomExecutablePath(new ToolId("fio")));
        Assert.Empty(service.Current.CustomToolPaths!);
    }

    private sealed class MemoryPreferencesService(UserPreferences initial) : IUserPreferencesService
    {
        public UserPreferences Current { get; private set; } = initial;

        public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
        {
            Current = preferences;
            return Task.CompletedTask;
        }
    }
}
