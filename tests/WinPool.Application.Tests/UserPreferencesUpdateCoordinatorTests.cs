using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class UserPreferencesUpdateCoordinatorTests
{
    [Fact]
    public async Task TwoOverlappingSettingsKeepBothChangesInMemoryAndOnDisk()
    {
        var service = new DelayedPreferencesService();
        var current = new UserPreferences();
        var coordinator = new UserPreferencesUpdateCoordinator(service, () => current, value => current = value);

        var developerSave = coordinator.UpdateAsync(value => value with { DeveloperMode = true });
        await service.FirstSaveStarted;
        var themeSave = coordinator.UpdateAsync(value => value with { Theme = ThemePreference.Dark });

        Assert.False(themeSave.IsCompleted);
        Assert.Equal(1, service.SaveAttempts);
        service.ReleaseFirstSave();
        await Task.WhenAll(developerSave, themeSave);

        Assert.True(current.DeveloperMode);
        Assert.Equal(ThemePreference.Dark, current.Theme);
        Assert.Equal(current, await service.LoadAsync());
        Assert.Equal(2, service.SaveAttempts);
    }

    [Fact]
    public async Task FailedSaveDoesNotChangeMemoryOrOverwriteNextUpdate()
    {
        var service = new DelayedPreferencesService { FailFirstSave = true };
        var current = new UserPreferences();
        var coordinator = new UserPreferencesUpdateCoordinator(service, () => current, value => current = value);

        var failingSave = coordinator.UpdateAsync(value => value with { DeveloperMode = true });
        await service.FirstSaveStarted;
        var themeSave = coordinator.UpdateAsync(value => value with { Theme = ThemePreference.Dark });
        service.ReleaseFirstSave();

        await Assert.ThrowsAsync<IOException>(() => failingSave);
        await themeSave;
        Assert.False(current.DeveloperMode);
        Assert.Equal(ThemePreference.Dark, current.Theme);
        Assert.Equal(current, await service.LoadAsync());
    }

    [Fact]
    public async Task ReloadWaitsForPendingSaveBeforeApplyingDiskSnapshot()
    {
        var service = new DelayedPreferencesService();
        var current = new UserPreferences();
        var coordinator = new UserPreferencesUpdateCoordinator(service, () => current, value => current = value);

        var save = coordinator.UpdateAsync(value => value with { DeveloperMode = true });
        await service.FirstSaveStarted;
        var reload = coordinator.LoadAsync();
        Assert.False(reload.IsCompleted);
        service.ReleaseFirstSave();
        await Task.WhenAll(save, reload);

        Assert.True(current.DeveloperMode);
        Assert.Equal(current, await service.LoadAsync());
    }

    private sealed class DelayedPreferencesService : IUserPreferencesService
    {
        private readonly TaskCompletionSource firstSaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private UserPreferences persisted = new();

        public bool FailFirstSave { get; set; }
        public int SaveAttempts { get; private set; }
        public Task FirstSaveStarted => firstSaveStarted.Task;
        public void ReleaseFirstSave() => releaseFirstSave.TrySetResult();
        public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(persisted);

        public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                firstSaveStarted.TrySetResult();
                await releaseFirstSave.Task.WaitAsync(cancellationToken);
                if (FailFirstSave)
                {
                    throw new IOException("Simulated preference write failure.");
                }
            }

            persisted = preferences;
        }
    }
}
