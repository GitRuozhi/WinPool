using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class SystemSupportRecoveryCoordinatorTests
{
    [Fact]
    public async Task RestoresPowerAndRegisteredProcessAndRemovesEvidence()
    {
        var store = new RecoveryStore(
        [
            PowerEntry(),
            SchedulingEntry(123)
        ]);
        var scheduling = new SchedulingPort();
        var power = new PowerPort();
        var audit = new AuditSink();
        var coordinator = new SystemSupportRecoveryCoordinator(
            store,
            scheduling,
            power,
            audit,
            processId => processId == 123,
            _ => true);

        var result = await coordinator.RecoverPendingAsync(CancellationToken.None);

        Assert.Equal(2, result.Restored);
        Assert.Equal(0, result.Failed);
        Assert.Equal([123], scheduling.RestoredProcessIds);
        Assert.Single(power.RestoredPlanIds);
        Assert.Empty(store.Entries);
        Assert.Equal(
            2,
            audit.Events.Count(item =>
                item.Stage == SystemSupportAuditStage.RecoveryCompleted));
    }

    [Fact]
    public async Task ExitedProcessIsNoLongerApplicableButLiveForeignProcessIsRetained()
    {
        var exited = SchedulingEntry(10);
        var liveForeign = SchedulingEntry(20);
        var store = new RecoveryStore([exited, liveForeign]);
        var audit = new AuditSink();
        var coordinator = new SystemSupportRecoveryCoordinator(
            store,
            new SchedulingPort(),
            new PowerPort(),
            audit,
            _ => false,
            processId => processId == 20);

        var result = await coordinator.RecoverPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.NoLongerApplicable);
        Assert.Equal(1, result.Failed);
        Assert.Equal(
            liveForeign.RecoveryId,
            Assert.Single(store.Entries).RecoveryId);
        Assert.Contains(
            audit.Events,
            item => item.Stage == SystemSupportAuditStage.RecoveryFailed);
    }

    [Fact]
    public async Task AuditFailureRetainsEntryAndDoesNotStopIndependentRecovery()
    {
        var first = PowerEntry();
        var second = PowerEntry() with { PlanHash = new string('c', 64) };
        var store = new RecoveryStore([first, second]);
        var audit = new AuditSink { FailPlanHash = first.PlanHash };
        var power = new PowerPort();
        var coordinator = new SystemSupportRecoveryCoordinator(
            store,
            new SchedulingPort(),
            power,
            audit,
            _ => false,
            _ => false);

        var result = await coordinator.RecoverPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.Equal(first.RecoveryId, Assert.Single(store.Entries).RecoveryId);
        Assert.Single(power.RestoredPlanIds);
    }

    private static SystemSupportRecoveryEntry PowerEntry() =>
        new(
            Guid.NewGuid(),
            new string('a', 64),
            SystemSupportActionKind.UseTemporaryPowerPlan,
            new PowerPlanRecoveryState(new PowerPlanSnapshot(Guid.NewGuid())),
            DateTimeOffset.UtcNow);

    private static SystemSupportRecoveryEntry SchedulingEntry(int processId) =>
        new(
            Guid.NewGuid(),
            new string('b', 64),
            SystemSupportActionKind.AdjustProcessScheduling,
            new ProcessSchedulingRecoveryState(
                new TestProcessSchedulingSnapshot(
                    processId,
                    true,
                    TestProcessPriority.Normal,
                    [0])),
            DateTimeOffset.UtcNow);

    private sealed class RecoveryStore(IEnumerable<SystemSupportRecoveryEntry> entries)
        : ISystemSupportRecoveryStore
    {
        public List<SystemSupportRecoveryEntry> Entries { get; } = [.. entries];

        public Task SaveAsync(
            SystemSupportRecoveryEntry entry,
            CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid recoveryId, CancellationToken cancellationToken)
        {
            Entries.RemoveAll(item => item.RecoveryId == recoveryId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemSupportRecoveryEntry>> GetPendingAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SystemSupportRecoveryEntry>>(
                Entries.ToArray());
    }

    private sealed class SchedulingPort : ITestProcessSchedulingPort
    {
        public List<int> RestoredProcessIds { get; } = [];

        public Task<TestProcessSchedulingSnapshot?> CaptureAsync(
            int processId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TestProcessSchedulingSnapshot?>(null);

        public Task ApplyAsync(
            int processId,
            TestProcessPriority priority,
            IReadOnlyList<int> logicalProcessorIndices,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAsync(
            TestProcessSchedulingSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            RestoredProcessIds.Add(snapshot.ProcessId);
            return Task.CompletedTask;
        }
    }

    private sealed class PowerPort : ITemporaryPowerPlanPort
    {
        public List<Guid> RestoredPlanIds { get; } = [];

        public Task<PowerPlanSnapshot> CaptureActiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new PowerPlanSnapshot(Guid.NewGuid()));

        public Task ActivateAsync(
            Guid powerPlanId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAsync(
            PowerPlanSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            RestoredPlanIds.Add(snapshot.PowerPlanId);
            return Task.CompletedTask;
        }
    }

    private sealed class AuditSink : ISystemSupportAuditSink
    {
        public List<SystemSupportAuditEvent> Events { get; } = [];
        public string? FailPlanHash { get; init; }

        public ValueTask WriteAsync(
            SystemSupportAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            if (string.Equals(
                    auditEvent.PlanHash,
                    FailPlanHash,
                    StringComparison.Ordinal))
            {
                throw new IOException("Simulated audit persistence failure.");
            }

            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
