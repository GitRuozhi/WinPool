using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent.Tests;

public sealed class AgentStartupRecoveryTests
{
    [Fact]
    public async Task ShutdownCancelsAndDrainsRecoveryBeforeAllowingTerminalWork()
    {
        using var recovery = new AgentStartupRecovery();
        var entered = Signal();
        var cancellationObserved = Signal();
        var release = Signal();
        var ready = false;
        var failed = false;
        recovery.Start(async token =>
        {
            entered.SetResult();
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await release.Task;
        }, () => ready = true, _ => failed = true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = recovery.StopAsync(CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(ready);
        Assert.False(failed);
    }

    [Fact]
    public async Task TimedOutDrainCanBeRetriedWithoutPublishingLateStartupFailure()
    {
        using var recovery = new AgentStartupRecovery();
        var entered = Signal();
        var release = Signal();
        var published = false;
        recovery.Start(async _ =>
        {
            entered.SetResult();
            await release.Task;
            throw new IOException("late recovery failure");
        }, () => published = true, _ => published = true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recovery.StopAsync(timeout.Token));
        release.SetResult();
        await recovery.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(published);
    }

    [Fact]
    public async Task SuccessfulManualMonitoringOnClearsStartupFailureAfterDurableSave()
    {
        var store = new PreferencesStore();
        var cleared = false;
        var saved = await DesktopAgentRuntime.SaveAgentPreferenceAsync(
            store, new AgentPreferences(ContinuousMonitoringEnabled: true),
            AgentPreferenceField.ContinuousMonitoringEnabled, () =>
            {
                Assert.True(store.Saved);
                cleared = true;
            }, CancellationToken.None);

        Assert.True(saved.ContinuousMonitoringEnabled);
        Assert.True(cleared);
    }

    [Fact]
    public async Task FailedManualMonitoringOnSaveRetainsStartupFailure()
    {
        var store = new PreferencesStore { FailSave = true };
        var cleared = false;

        await Assert.ThrowsAsync<IOException>(() => DesktopAgentRuntime.SaveAgentPreferenceAsync(
            store, new AgentPreferences(ContinuousMonitoringEnabled: true),
            AgentPreferenceField.ContinuousMonitoringEnabled, () => cleared = true,
            CancellationToken.None));

        Assert.False(cleared);
        Assert.False(store.Saved);
    }

    [Theory]
    [InlineData(AgentPreferenceField.ContinuousMonitoringEnabled, false)]
    [InlineData(AgentPreferenceField.MonitoringSampleRateHz, true)]
    public async Task OtherSuccessfulPreferenceSavesRetainStartupFailure(AgentPreferenceField field, bool enabled)
    {
        var cleared = false;
        await DesktopAgentRuntime.SaveAgentPreferenceAsync(
            new PreferencesStore(), new AgentPreferences(ContinuousMonitoringEnabled: enabled), field,
            () => cleared = true, CancellationToken.None);

        Assert.False(cleared);
    }

    [Fact]
    public async Task ShutdownBeforeScheduledRecoveryPreventsStartingIt()
    {
        using var recovery = new AgentStartupRecovery();
        var started = false;
        await recovery.StopAsync(CancellationToken.None);

        recovery.Start(_ =>
        {
            started = true;
            return Task.CompletedTask;
        }, () => started = true, _ => started = true);
        await recovery.StopAsync(CancellationToken.None);

        Assert.False(started);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PreferencesStore : IAgentPreferencesStore
    {
        internal bool FailSave { get; init; }
        internal bool Saved { get; private set; }

        public Task<AgentPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentPreferences());

        public Task<AgentPreferences> SaveAsync(AgentPreferences preferences, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Preference save failed.");
            Saved = true;
            return Task.FromResult(preferences);
        }
    }
}
