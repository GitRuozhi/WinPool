using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class AgentPreferencesReloadCoordinatorTests
{
    [Fact]
    public async Task TriggerAppliesSnapshotAndDeduplicatesEqualStamps()
    {
        var stamp = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var reader = new SequenceReader(
            new AgentPreferences(SavedAtUtc: stamp),
            new AgentPreferences(SavedAtUtc: stamp));
        var applied = new List<AgentPreferences>();
        var coordinator = new AgentPreferencesReloadCoordinator(
            reader,
            applied.Add);

        coordinator.Trigger();
        coordinator.Trigger();
        await reader.WaitForLoadsAsync(2);

        // Two triggers, one generation: only one apply.
        var snapshot = Assert.Single(applied);
        Assert.Equal(stamp, snapshot.SavedAtUtc);
    }

    [Fact]
    public async Task DifferentStampsApplyEvenWhenTheClockStepsBackwards()
    {
        var newer = DateTimeOffset.FromUnixTimeSeconds(2_000);
        var steppedBack = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var reader = new SequenceReader(
            new AgentPreferences(MonitoringSampleRateHz: 1, SavedAtUtc: newer),
            new AgentPreferences(MonitoringSampleRateHz: 2, SavedAtUtc: steppedBack));
        var applied = new List<AgentPreferences>();
        var coordinator = new AgentPreferencesReloadCoordinator(
            reader,
            applied.Add);

        coordinator.Trigger();
        await reader.WaitForLoadsAsync(1);
        coordinator.Trigger();
        await reader.WaitForLoadsAsync(2);

        // Inequality, not ordering: a clock step back must still apply.
        Assert.Equal(2, applied.Count);
        Assert.Equal(1, applied[0].MonitoringSampleRateHz);
        Assert.Equal(2, applied[1].MonitoringSampleRateHz);
    }

    [Fact]
    public async Task FailedReadKeepsPreviousStateAndNextTriggerRecovers()
    {
        var stamp = DateTimeOffset.FromUnixTimeSeconds(3_000);
        var reader = new SequenceReader();
        var applied = new List<AgentPreferences>();
        var coordinator = new AgentPreferencesReloadCoordinator(
            reader,
            applied.Add);

        reader.FailNextRead();
        coordinator.Trigger();
        await reader.WaitForLoadsAsync(1);
        Assert.Empty(applied);

        reader.Enqueue(new AgentPreferences(
            ContinuousMonitoringEnabled: true,
            SavedAtUtc: stamp.AddSeconds(1)));
        coordinator.Trigger();
        await reader.WaitForLoadsAsync(2);

        Assert.Single(applied);
        Assert.True(applied[0].ContinuousMonitoringEnabled);
    }

    /// <summary>
    /// Stands in for agent-settings.json: queued generations are returned in
    /// order, and once the queue is empty the last generation is returned
    /// again, exactly like re-reading an unchanged file.
    /// </summary>
    private sealed class SequenceReader(params AgentPreferences[] snapshots)
        : IAgentPreferencesReader
    {
        private readonly Queue<AgentPreferences> queue = new(snapshots);
        private AgentPreferences last = new();
        private int inFlight;
        private int loadCount;
        private readonly object sync = new();

        public void Enqueue(AgentPreferences preferences)
        {
            lock (sync)
            {
                queue.Enqueue(preferences);
            }
        }

        public void FailNextRead()
        {
            lock (sync)
            {
                queue.Enqueue(null!);
            }
        }

        public async Task WaitForLoadsAsync(int expectedLoadCount)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                bool done;
                lock (sync)
                {
                    done = loadCount >= expectedLoadCount && inFlight == 0;
                }

                if (done)
                {
                    // Give the coordinator's synchronous post-read apply
                    // a moment to land before returning.
                    await Task.Delay(50);
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail($"Only {loadCount} loads completed; expected {expectedLoadCount}.");
        }

        public Task<AgentPreferences> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                inFlight++;
                loadCount++;
                try
                {
                    if (queue.Count > 0)
                    {
                        var next = queue.Dequeue();
                        if (next is null)
                        {
                            throw new InvalidOperationException(
                                "simulated unreadable settings file");
                        }

                        last = next;
                    }

                    return Task.FromResult(last);
                }
                finally
                {
                    inFlight--;
                }
            }
        }
    }
}

public sealed class AgentPreferenceRequestsTests
{
    [Fact]
    public void BooleanFieldsRequireBooleanValue()
    {
        var preferences = new AgentPreferences();

        var enabled = AgentPreferenceRequests.Apply(
            preferences,
            AgentPreferenceField.ContinuousMonitoringEnabled,
            booleanValue: true,
            numberValue: null);
        var missing = AgentPreferenceRequests.Apply(
            preferences,
            AgentPreferenceField.ContinuousMonitoringEnabled,
            booleanValue: null,
            numberValue: null);
        var wrongSlot = AgentPreferenceRequests.Apply(
            preferences,
            AgentPreferenceField.StartAgentAtLogin,
            booleanValue: null,
            numberValue: 1);

        Assert.NotNull(enabled);
        Assert.True(enabled!.ContinuousMonitoringEnabled);
        Assert.Null(missing);
        Assert.Null(wrongSlot);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(5)]
    [InlineData(20)]
    public void SampleRateAcceptsTheDocumentedRange(double rateHz)
    {
        var updated = AgentPreferenceRequests.Apply(
            new AgentPreferences(),
            AgentPreferenceField.MonitoringSampleRateHz,
            booleanValue: null,
            numberValue: rateHz);

        Assert.NotNull(updated);
        Assert.Equal(rateHz, updated!.MonitoringSampleRateHz);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(20.1)]
    [InlineData(-1)]
    public void SampleRateRejectsValuesOutsideTheRange(double rateHz)
    {
        Assert.Null(AgentPreferenceRequests.Apply(
            new AgentPreferences(),
            AgentPreferenceField.MonitoringSampleRateHz,
            booleanValue: null,
            numberValue: rateHz));
    }

    [Fact]
    public void SampleRateRejectsNonFiniteValues()
    {
        Assert.Null(AgentPreferenceRequests.Apply(
            new AgentPreferences(),
            AgentPreferenceField.MonitoringSampleRateHz,
            booleanValue: null,
            numberValue: double.NaN));
        Assert.Null(AgentPreferenceRequests.Apply(
            new AgentPreferences(),
            AgentPreferenceField.MonitoringSampleRateHz,
            booleanValue: null,
            numberValue: double.PositiveInfinity));
    }
}
