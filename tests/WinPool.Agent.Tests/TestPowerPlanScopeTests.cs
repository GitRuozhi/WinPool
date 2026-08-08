using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class TestPowerPlanScopeTests
{
    [Fact]
    public async Task SavesOriginalThenActivatesAndRestoresThroughClosedCallback()
    {
        var events = new List<string>();
        var original = Guid.NewGuid();
        var requested = Guid.NewGuid();
        var store = new RecoveryStore(events);
        var activated = new List<Guid>();
        var scope = new TestPowerPlanScope(
            new PowerPort(events, original),
            store,
            new AuditSink(events),
            (id, _, _, _) =>
            {
                events.Add($"set:{id:D}");
                activated.Add(id);
                return Task.CompletedTask;
            });

        var prepared = await scope.PrepareAsync(
            new string('a', 64),
            requested,
            CorrelationId.New(),
            CancellationToken.None);
        await scope.RestoreAsync(prepared, CorrelationId.New());

        Assert.Equal([requested, original], activated);
        Assert.Empty(store.Entries);
        Assert.True(events.IndexOf("save") < events.IndexOf($"set:{requested:D}"));
        Assert.True(events.IndexOf($"set:{original:D}") < events.IndexOf("remove"));
    }

    [Fact]
    public async Task FailedActivationRestoresOriginalAndFailedRestoreRetainsEvidence()
    {
        var events = new List<string>();
        var original = Guid.NewGuid();
        var requested = Guid.NewGuid();
        var store = new RecoveryStore(events);
        var calls = 0;
        var scope = new TestPowerPlanScope(
            new PowerPort(events, original),
            store,
            new AuditSink(events),
            (id, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException(new InvalidOperationException("activate failed"))
                    : Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.PrepareAsync(
                new string('b', 64),
                requested,
                CorrelationId.New(),
                CancellationToken.None));
        Assert.Empty(store.Entries);
        Assert.Equal(2, calls);

        var retainedStore = new RecoveryStore([]);
        var retained = new TestPowerPlanScope(
            new PowerPort([], original),
            retainedStore,
            new AuditSink([]),
            (_, _, _, _) => Task.FromException(new IOException("broker failed")));
        await Assert.ThrowsAsync<IOException>(() =>
            retained.PrepareAsync(
                new string('c', 64),
                requested,
                CorrelationId.New(),
                CancellationToken.None));
        Assert.Single(retainedStore.Entries);
    }

    private sealed class RecoveryStore(List<string> events)
        : ISystemSupportRecoveryStore
    {
        public List<SystemSupportRecoveryEntry> Entries { get; } = [];

        public Task SaveAsync(
            SystemSupportRecoveryEntry entry,
            CancellationToken cancellationToken)
        {
            events.Add("save");
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid recoveryId, CancellationToken cancellationToken)
        {
            events.Add("remove");
            Entries.RemoveAll(item => item.RecoveryId == recoveryId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemSupportRecoveryEntry>> GetPendingAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SystemSupportRecoveryEntry>>(
                Entries.ToArray());
    }

    private sealed class PowerPort(List<string> events, Guid active)
        : ITemporaryPowerPlanPort
    {
        public Task<PowerPlanSnapshot> CaptureActiveAsync(
            CancellationToken cancellationToken)
        {
            events.Add("capture");
            return Task.FromResult(new PowerPlanSnapshot(active));
        }

        public Task ActivateAsync(
            Guid powerPlanId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestoreAsync(
            PowerPlanSnapshot snapshot,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AuditSink(List<string> events) : ISystemSupportAuditSink
    {
        public ValueTask WriteAsync(
            SystemSupportAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            events.Add($"audit:{auditEvent.Stage}");
            return ValueTask.CompletedTask;
        }
    }
}
