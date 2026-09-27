using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>
/// Keeps the App's complete preference snapshot and its persisted copy in the
/// same order. A failed save leaves the current snapshot untouched.
/// </summary>
public sealed class UserPreferencesUpdateCoordinator(
    IUserPreferencesService service,
    Func<UserPreferences> current,
    Action<UserPreferences> apply)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await service.LoadAsync(cancellationToken);
            apply(loaded);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateAsync(
        Func<UserPreferences, UserPreferences> update,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var updated = update(current());
            await service.SaveAsync(updated, cancellationToken);
            apply(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReplaceAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await service.SaveAsync(preferences, cancellationToken);
            apply(preferences);
        }
        finally
        {
            gate.Release();
        }
    }
}
