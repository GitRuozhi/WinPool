using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentProcessRegistryTests
{
    [Fact]
    public void RegistryDetectsHeartbeatLossAndAcceptsRecovery()
    {
        var registry = new AgentProcessRegistry();
        var startedAt = new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero);
        Assert.True(registry.TryRegister(Create(
            101,
            AgentManagedProcessKind.MainApplication,
            startedAt)));
        Assert.True(registry.TryRegister(Create(
            102,
            AgentManagedProcessKind.TestWorker,
            startedAt)));
        Assert.True(registry.TryRegister(Create(
            103,
            AgentManagedProcessKind.ElevatedBroker,
            startedAt)));

        var unresponsive = registry.SweepUnresponsive(
            startedAt.AddSeconds(16),
            TimeSpan.FromSeconds(15));

        Assert.Equal([101, 102], unresponsive.Select(process => process.ProcessId));
        Assert.DoesNotContain(
            registry.Snapshot(),
            process => process.ProcessId == 103
                       && process.State == SupervisedProcessState.Unresponsive);

        Assert.True(registry.TryRecordHeartbeat(101, startedAt.AddSeconds(17)));
        Assert.Equal(
            SupervisedProcessState.Running,
            registry.Snapshot().Single(process => process.ProcessId == 101).State);
    }

    [Fact]
    public void RegistryPreservesShutdownDeadlineAndTerminalProcessesAreNotLive()
    {
        var registry = new AgentProcessRegistry();
        var startedAt = DateTimeOffset.UtcNow;
        Assert.True(registry.TryRegister(Create(
            201,
            AgentManagedProcessKind.ExternalTool,
            startedAt,
            ownsJobObject: true)));
        var deadline = startedAt.AddSeconds(10);

        Assert.True(registry.TryBeginStopping(201, deadline));
        Assert.Equal(deadline, registry.Snapshot().Single().ShutdownDeadlineUtc);
        Assert.Equal([201], registry.GetLiveProcessIds());

        Assert.True(registry.TryMarkExited(201, deadline));
        Assert.Empty(registry.GetLiveProcessIds());
        Assert.False(registry.TryRecordHeartbeat(201, deadline.AddSeconds(1)));
    }

    [Fact]
    public void RegistryRejectsDuplicateProcessIdentity()
    {
        var registry = new AgentProcessRegistry();
        var registration = Create(
            301,
            AgentManagedProcessKind.InventoryWorker,
            DateTimeOffset.UtcNow);

        Assert.True(registry.TryRegister(registration));
        Assert.False(registry.TryRegister(registration));
        Assert.True(registry.TryGet(301, out var stored));
        Assert.Equal(registration, stored);
        Assert.False(registry.TryGet(999, out _));
    }

    private static AgentManagedProcess Create(
        int processId,
        AgentManagedProcessKind kind,
        DateTimeOffset startedAt,
        bool ownsJobObject = false) =>
        new(
            processId,
            kind,
            CorrelationId.New(),
            startedAt,
            startedAt,
            SupervisedProcessState.Running,
            ownsJobObject,
            null);
}
