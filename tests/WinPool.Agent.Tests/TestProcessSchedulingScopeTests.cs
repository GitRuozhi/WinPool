using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class TestProcessSchedulingScopeTests
{
    [Fact]
    public async Task PersistsBeforeApplyAndRestoresBeforeRemovingRecoveryEntry()
    {
        var events = new List<string>();
        var store = new RecoveryStore(events);
        var port = new SchedulingPort(events);
        var audit = new AuditSink(events);
        var scope = new TestProcessSchedulingScope(port, store, audit);

        var prepared = await scope.PrepareAsync(
            new string('a', 64),
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [0, 1]),
            42,
            CorrelationId.New(),
            CancellationToken.None);
        await scope.RestoreAsync(prepared, CorrelationId.New());

        Assert.Empty(store.Entries);
        Assert.Equal(
            [
                "capture",
                "save",
                "audit:RestorationPrepared",
                "apply",
                "audit:Completed",
                "restore",
                "audit:Restored",
                "remove"
            ],
            events);
    }

    [Fact]
    public async Task FailedApplyAttemptsImmediateRestore()
    {
        var events = new List<string>();
        var store = new RecoveryStore(events);
        var port = new SchedulingPort(events) { FailApply = true };
        var scope = new TestProcessSchedulingScope(
            port,
            store,
            new AuditSink(events));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.PrepareAsync(
                new string('b', 64),
                new TestProcessSchedulingPolicyAction(
                    TestProcessPriority.High,
                    [0]),
                42,
                CorrelationId.New(),
                CancellationToken.None));

        Assert.Empty(store.Entries);
        Assert.Contains("restore", events);
    }

    [Fact]
    public async Task FailedRestoreRetainsRecoveryEvidence()
    {
        var events = new List<string>();
        var store = new RecoveryStore(events);
        var port = new SchedulingPort(events);
        var scope = new TestProcessSchedulingScope(
            port,
            store,
            new AuditSink(events));
        var prepared = await scope.PrepareAsync(
            new string('c', 64),
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [0]),
            42,
            CorrelationId.New(),
            CancellationToken.None);
        port.FailRestore = true;

        await Assert.ThrowsAsync<IOException>(() =>
            scope.RestoreAsync(prepared, CorrelationId.New()));

        Assert.Single(store.Entries);
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

    private sealed class SchedulingPort(List<string> events)
        : ITestProcessSchedulingPort
    {
        public bool FailApply { get; init; }
        public bool FailRestore { get; set; }

        public Task<TestProcessSchedulingSnapshot?> CaptureAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            events.Add("capture");
            return Task.FromResult<TestProcessSchedulingSnapshot?>(
                new(
                    processId,
                    true,
                    TestProcessPriority.Normal,
                    [0, 1]));
        }

        public Task ApplyAsync(
            int processId,
            TestProcessPriority priority,
            IReadOnlyList<int> logicalProcessorIndices,
            CancellationToken cancellationToken)
        {
            events.Add("apply");
            return FailApply
                ? Task.FromException(new InvalidOperationException("apply failed"))
                : Task.CompletedTask;
        }

        public Task RestoreAsync(
            TestProcessSchedulingSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            events.Add("restore");
            return FailRestore
                ? Task.FromException(new IOException("restore failed"))
                : Task.CompletedTask;
        }
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
