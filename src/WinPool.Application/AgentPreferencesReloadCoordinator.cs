using WinPool.Domain;

namespace WinPool.Application;

/// <summary>
/// Serializes every background-preference reload trigger (Agent event,
/// reconnect, file watcher) into one reader pipeline. Triggers carry no data;
/// each pass reads the file and deduplicates by the SavedAtUtc content label
/// using inequality, so concurrent or duplicate triggers are harmless and a
/// system clock step can never hide a change.
/// </summary>
public sealed class AgentPreferencesReloadCoordinator
{
    private readonly IAgentPreferencesReader reader;
    private readonly Action<AgentPreferences> apply;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stampSync = new();
    private DateTimeOffset lastAppliedStamp;
    private bool hasApplied;
    private int pending;

    public AgentPreferencesReloadCoordinator(
        IAgentPreferencesReader reader,
        Action<AgentPreferences> apply)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    /// <summary>
    /// Requests one reload. Safe from any thread and safe to call while a
    /// reload is already running; the running pass picks the request up, and
    /// a pass that arrives after the current one exits starts a new pass.
    /// </summary>
    public void Trigger()
    {
        Interlocked.Increment(ref pending);
        _ = ReloadPassAsync();
    }

    private async Task ReloadPassAsync()
    {
        await gate.WaitAsync();
        try
        {
            while (Interlocked.Exchange(ref pending, 0) > 0)
            {
                var preferences = await reader.LoadAsync();
                lock (stampSync)
                {
                    if (hasApplied
                        && preferences.SavedAtUtc == lastAppliedStamp)
                    {
                        continue;
                    }

                    lastAppliedStamp = preferences.SavedAtUtc;
                    hasApplied = true;
                }

                apply(preferences);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            // A failed pass keeps the previous in-memory state. The next
            // trigger reads the file again; no partial apply can happen
            // because LoadAsync returns only complete snapshots.
        }
        finally
        {
            gate.Release();
        }
    }
}
