using WinPool.Application;

namespace WinPool.Agent;

public enum AgentManagedProcessKind
{
    MainApplication,
    TestWorker,
    InventoryWorker,
    ElevatedBroker,
    ExternalTool
}

public sealed record AgentManagedProcess(
    ProcessInstanceId ProcessInstanceId,
    int ProcessId,
    AgentManagedProcessKind Kind,
    CorrelationId CorrelationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastHeartbeatUtc,
    SupervisedProcessState State,
    bool OwnsJobObject,
    DateTimeOffset? ShutdownDeadlineUtc);

public enum AgentProcessRegistrationResult
{
    Registered,
    Reconnected,
    ReplacedStaleProcess,
    Rejected
}

/// <summary>
/// Tracks process identity and health without starting, stopping, or inspecting a real process.
/// </summary>
public sealed class AgentProcessRegistry
{
    private const int MaximumTerminalDiagnostics = 128;
    private readonly object syncRoot = new();
    private readonly Dictionary<ProcessInstanceId, AgentManagedProcess> registrations = [];
    private readonly Dictionary<int, ProcessInstanceId> processIndex = [];
    private readonly Queue<AgentManagedProcess> terminalDiagnostics = [];

    public bool TryRegister(AgentManagedProcess registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRegistration(registration);

        lock (syncRoot)
        {
            if (registrations.ContainsKey(registration.ProcessInstanceId)
                || processIndex.ContainsKey(registration.ProcessId))
            {
                return false;
            }

            registrations.Add(registration.ProcessInstanceId, registration);
            processIndex.Add(registration.ProcessId, registration.ProcessInstanceId);
            return true;
        }
    }

    public bool TryMarkRunning(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        DateTimeOffset observedAtUtc) =>
        TryUpdate(
            processInstanceId,
            expectedProcessId,
            current => current.State is SupervisedProcessState.Starting
                ? current with
                {
                    State = SupervisedProcessState.Running,
                    LastHeartbeatUtc = Max(current.LastHeartbeatUtc, observedAtUtc)
                }
                : null);

    public bool TryRecordHeartbeat(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        DateTimeOffset observedAtUtc) =>
        TryUpdate(
            processInstanceId,
            expectedProcessId,
            current => IsHeartbeatEligible(current)
                       && observedAtUtc >= current.LastHeartbeatUtc
                ? current with
                {
                    State = SupervisedProcessState.Running,
                    LastHeartbeatUtc = observedAtUtc
                }
                : null);

    public bool TryBeginStopping(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        DateTimeOffset shutdownDeadlineUtc) =>
        TryUpdate(
            processInstanceId,
            expectedProcessId,
            current => shutdownDeadlineUtc < current.StartedAtUtc
                ? null
                : current.State == SupervisedProcessState.Running
                    ? current with
                    {
                        State = SupervisedProcessState.Stopping,
                        ShutdownDeadlineUtc = shutdownDeadlineUtc
                    }
                    : current.State == SupervisedProcessState.Stopping
                      && current.ShutdownDeadlineUtc == shutdownDeadlineUtc
                        ? current
                        : null);

    public bool TryMarkExited(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        DateTimeOffset observedAtUtc,
        out AgentManagedProcess? terminalRegistration,
        bool failed = false) =>
        TryComplete(
            processInstanceId,
            expectedProcessId,
            observedAtUtc,
            failed,
            out terminalRegistration);

    public IReadOnlyList<AgentManagedProcess> SweepUnresponsive(
        DateTimeOffset observedAtUtc,
        TimeSpan heartbeatTimeout)
    {
        if (heartbeatTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatTimeout),
                "The heartbeat timeout must be positive.");
        }

        lock (syncRoot)
        {
            var changed = new List<AgentManagedProcess>();
            foreach (var pair in registrations.ToArray())
            {
                var current = pair.Value;
                if (!RequiresHeartbeat(current.Kind)
                    || current.State is not SupervisedProcessState.Running
                    || observedAtUtc - current.LastHeartbeatUtc <= heartbeatTimeout)
                {
                    continue;
                }

                var unresponsive = current with
                {
                    State = SupervisedProcessState.Unresponsive
                };
                registrations[pair.Key] = unresponsive;
                changed.Add(unresponsive);
            }

            return changed;
        }
    }

    public IReadOnlyList<AgentManagedProcess> Snapshot()
    {
        lock (syncRoot)
        {
            return registrations.Values
                .OrderBy(registration => registration.ProcessId)
                .ToArray();
        }
    }

    public AgentProcessRegistrationResult RegisterOrReconnect(
        AgentManagedProcess registration,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRegistration(registration);

        lock (syncRoot)
        {
            if (registrations.TryGetValue(registration.ProcessInstanceId, out var existing))
            {
                return existing.ProcessId == registration.ProcessId
                       && existing.StartedAtUtc == registration.StartedAtUtc
                    ? AgentProcessRegistrationResult.Reconnected
                    : AgentProcessRegistrationResult.Rejected;
            }

            if (processIndex.TryGetValue(registration.ProcessId, out var incumbentId)
                && registrations.TryGetValue(incumbentId, out var incumbent))
            {
                if (incumbent.StartedAtUtc == registration.StartedAtUtc)
                {
                    return AgentProcessRegistrationResult.Rejected;
                }

                CompleteLocked(incumbentId, incumbent, observedAtUtc, failed: false);
                registrations.Add(registration.ProcessInstanceId, registration);
                processIndex[registration.ProcessId] = registration.ProcessInstanceId;
                return AgentProcessRegistrationResult.ReplacedStaleProcess;
            }

            registrations.Add(registration.ProcessInstanceId, registration);
            processIndex.Add(registration.ProcessId, registration.ProcessInstanceId);
            return AgentProcessRegistrationResult.Registered;
        }
    }

    public IReadOnlyList<AgentManagedProcess> TerminalDiagnosticsSnapshot()
    {
        lock (syncRoot)
        {
            return terminalDiagnostics.ToArray();
        }
    }

    public bool TryGet(int processId, out AgentManagedProcess? registration)
    {
        lock (syncRoot)
        {
            if (processIndex.TryGetValue(processId, out var instanceId))
            {
                return registrations.TryGetValue(instanceId, out registration);
            }

            registration = null;
            return false;
        }
    }

    public bool TryGet(
        ProcessInstanceId processInstanceId,
        out AgentManagedProcess? registration)
    {
        lock (syncRoot)
        {
            return registrations.TryGetValue(processInstanceId, out registration);
        }
    }

    public IReadOnlyList<int> GetLiveProcessIds()
    {
        lock (syncRoot)
        {
            return registrations.Values
                .Select(registration => registration.ProcessId)
                .Order()
                .ToArray();
        }
    }

    private bool TryUpdate(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        Func<AgentManagedProcess, AgentManagedProcess?> update)
    {
        if (processInstanceId.Value == Guid.Empty || expectedProcessId <= 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (!registrations.TryGetValue(processInstanceId, out var current)
                || current.ProcessId != expectedProcessId
                || !processIndex.TryGetValue(expectedProcessId, out var indexedInstanceId)
                || indexedInstanceId != processInstanceId)
            {
                return false;
            }

            var updated = update(current);
            if (updated is null)
            {
                return false;
            }

            registrations[processInstanceId] = updated;
            return true;
        }
    }

    private bool TryComplete(
        ProcessInstanceId processInstanceId,
        int expectedProcessId,
        DateTimeOffset observedAtUtc,
        bool failed,
        out AgentManagedProcess? terminalRegistration)
    {
        terminalRegistration = null;
        if (processInstanceId.Value == Guid.Empty || expectedProcessId <= 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (!registrations.TryGetValue(processInstanceId, out var current)
                || current.ProcessId != expectedProcessId
                || observedAtUtc < current.StartedAtUtc
                || !processIndex.TryGetValue(expectedProcessId, out var indexedInstanceId)
                || indexedInstanceId != processInstanceId)
            {
                return false;
            }

            terminalRegistration = CompleteLocked(
                processInstanceId,
                current,
                observedAtUtc,
                failed);
            return true;
        }
    }

    private AgentManagedProcess CompleteLocked(
        ProcessInstanceId processInstanceId,
        AgentManagedProcess current,
        DateTimeOffset observedAtUtc,
        bool failed)
    {
        var terminal = current with
        {
            State = failed
                ? SupervisedProcessState.Failed
                : SupervisedProcessState.Exited,
            LastHeartbeatUtc = Max(current.LastHeartbeatUtc, observedAtUtc)
        };
        registrations.Remove(processInstanceId);
        processIndex.Remove(current.ProcessId);
        terminalDiagnostics.Enqueue(terminal);
        while (terminalDiagnostics.Count > MaximumTerminalDiagnostics)
        {
            terminalDiagnostics.Dequeue();
        }

        return terminal;
    }

    private static bool IsHeartbeatEligible(AgentManagedProcess registration) =>
        RequiresHeartbeat(registration.Kind)
        && registration.State is SupervisedProcessState.Starting
            or SupervisedProcessState.Running
            or SupervisedProcessState.Unresponsive;

    private static bool RequiresHeartbeat(AgentManagedProcessKind kind) =>
        kind is AgentManagedProcessKind.MainApplication
            or AgentManagedProcessKind.TestWorker
            or AgentManagedProcessKind.InventoryWorker;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static void ValidateRegistration(AgentManagedProcess registration)
    {
        if (registration.ProcessInstanceId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A process registration requires an instance ID.",
                nameof(registration));
        }

        if (registration.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registration),
                "A process registration requires a positive process ID.");
        }

        if (registration.CorrelationId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A process registration requires a correlation ID.",
                nameof(registration));
        }

        if (registration.LastHeartbeatUtc < registration.StartedAtUtc)
        {
            throw new ArgumentException(
                "The last heartbeat cannot precede process start.",
                nameof(registration));
        }
    }
}
