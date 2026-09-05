using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class LocalUserPreferencesServiceTests
{
    [Fact]
    public async Task SaveReplacesExistingPreferencesWithoutLeavingTemporaryFiles()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalUserPreferencesService(location.Root);

        await service.SaveAsync(new UserPreferences(Language: LanguagePreference.ZhCn));
        await using (var reader = new FileStream(
                         service.SettingsPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            await service.SaveAsync(new UserPreferences(Language: LanguagePreference.EnUs));
        }

        var saved = await service.LoadAsync();
        Assert.Equal(LanguagePreference.EnUs, saved.Language);
        Assert.Empty(Directory.EnumerateFiles(location.Root, "app-settings.json.tmp-*"));
    }

    [Fact]
    public async Task UnreadableFileBlocksWritesUntilItBecomesReadable()
    {
        using var location = TemporaryLocation.Create();
        var service = new LocalUserPreferencesService(location.Root);
        await service.SaveAsync(new UserPreferences(Language: LanguagePreference.ZhCn));

        await File.WriteAllTextAsync(service.SettingsPath, "{ broken");

        var loaded = await service.LoadAsync();
        Assert.Equal(LanguagePreference.SystemDefault, loaded.Language);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(new UserPreferences(Language: LanguagePreference.EnUs)));
        Assert.Equal("{ broken", await File.ReadAllTextAsync(service.SettingsPath));
    }

    private sealed class TemporaryLocation(string root) : IDisposable
    {
        public string Root { get; } = root;

        public static TemporaryLocation Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WinPool.LocalUserPreferences.Tests",
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
