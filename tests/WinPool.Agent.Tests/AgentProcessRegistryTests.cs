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
        Assert.Empty(registry.Snapshot());
        Assert.Equal(
            SupervisedProcessState.Exited,
            Assert.Single(registry.TerminalDiagnosticsSnapshot()).State);
        Assert.False(registry.TryRecordHeartbeat(201, deadline.AddSeconds(1)));

        var reused = Create(
            201,
            AgentManagedProcessKind.ExternalTool,
            deadline.AddSeconds(2));
        Assert.True(registry.TryRegister(reused));
        Assert.Equal(reused.ProcessInstanceId, registry.Snapshot().Single().ProcessInstanceId);
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

    [Fact]
    public void TerminalDiagnosticsAreBoundedAndDoNotBlockPidReuse()
    {
        var registry = new AgentProcessRegistry();
        var startedAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < 130; index++)
        {
            var processId = 1_000 + index;
            Assert.True(registry.TryRegister(Create(
                processId,
                AgentManagedProcessKind.ExternalTool,
                startedAt.AddSeconds(index))));
            Assert.True(registry.TryMarkExited(
                processId,
                startedAt.AddSeconds(index + 1)));
        }

        var diagnostics = registry.TerminalDiagnosticsSnapshot();
        Assert.Equal(128, diagnostics.Count);
        Assert.Equal(1_002, diagnostics[0].ProcessId);
        Assert.Empty(registry.Snapshot());

        Assert.True(registry.TryRegister(Create(
            1_000,
            AgentManagedProcessKind.ExternalTool,
            startedAt.AddHours(1))));
    }

    [Fact]
    public void ExactIdentityOperationsCannotMutateAPidReplacement()
    {
        var registry = new AgentProcessRegistry();
        var startedAt = new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);
        var original = Create(
            501,
            AgentManagedProcessKind.MainApplication,
            startedAt);
        var replacement = Create(
            501,
            AgentManagedProcessKind.MainApplication,
            startedAt.AddMinutes(1));

        Assert.Equal(
            AgentProcessRegistrationResult.Registered,
            registry.RegisterOrReconnect(original, startedAt));
        Assert.Equal(
            AgentProcessRegistrationResult.ReplacedStaleProcess,
            registry.RegisterOrReconnect(replacement, startedAt.AddMinutes(1)));

        Assert.False(registry.TryRecordHeartbeat(
            original.ProcessInstanceId,
            original.ProcessId,
            startedAt.AddMinutes(2)));
        Assert.False(registry.TryMarkExited(
            original.ProcessInstanceId,
            original.ProcessId,
            startedAt.AddMinutes(2),
            out _));
        Assert.True(registry.TryRecordHeartbeat(
            replacement.ProcessInstanceId,
            replacement.ProcessId,
            startedAt.AddMinutes(2)));

        var live = Assert.Single(registry.Snapshot());
        Assert.Equal(replacement.ProcessInstanceId, live.ProcessInstanceId);
        Assert.Equal(startedAt.AddMinutes(2), live.LastHeartbeatUtc);
        Assert.Contains(
            registry.TerminalDiagnosticsSnapshot(),
            item => item.ProcessInstanceId == original.ProcessInstanceId
                    && item.State == SupervisedProcessState.Exited);
    }

    [Fact]
    public void ReconnectRequiresTheSameInstancePidAndStartWitness()
    {
        var registry = new AgentProcessRegistry();
        var startedAt = new DateTimeOffset(2026, 8, 11, 2, 0, 0, TimeSpan.Zero);
        var original = Create(
            601,
            AgentManagedProcessKind.MainApplication,
            startedAt);

        Assert.Equal(
            AgentProcessRegistrationResult.Registered,
            registry.RegisterOrReconnect(original, startedAt));
        Assert.Equal(
            AgentProcessRegistrationResult.Reconnected,
            registry.RegisterOrReconnect(original, startedAt.AddSeconds(5)));
        Assert.Equal(
            AgentProcessRegistrationResult.Rejected,
            registry.RegisterOrReconnect(
                original with { ProcessId = 602 },
                startedAt.AddSeconds(6)));
        Assert.Equal(
            AgentProcessRegistrationResult.Rejected,
            registry.RegisterOrReconnect(
                Create(
                    601,
                    AgentManagedProcessKind.MainApplication,
                    startedAt),
                startedAt.AddSeconds(7)));
    }

    private static AgentManagedProcess Create(
        int processId,
        AgentManagedProcessKind kind,
        DateTimeOffset startedAt,
        bool ownsJobObject = false) =>
        new(
            ProcessInstanceId.New(),
            processId,
            kind,
            CorrelationId.New(),
            startedAt,
            startedAt,
            SupervisedProcessState.Running,
            ownsJobObject,
            null);
}
