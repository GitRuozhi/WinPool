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
    int ProcessId,
    AgentManagedProcessKind Kind,
    CorrelationId CorrelationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastHeartbeatUtc,
    SupervisedProcessState State,
    bool OwnsJobObject,
    DateTimeOffset? ShutdownDeadlineUtc);

/// <summary>
/// Tracks process identity and health without starting, stopping, or inspecting a real process.
/// </summary>
public sealed class AgentProcessRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<int, AgentManagedProcess> registrations = [];

    public bool TryRegister(AgentManagedProcess registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRegistration(registration);

        lock (syncRoot)
        {
            return registrations.TryAdd(registration.ProcessId, registration);
        }
    }

    public bool TryMarkRunning(int processId, DateTimeOffset observedAtUtc) =>
        TryUpdate(
            processId,
            current => current.State is SupervisedProcessState.Starting
                ? current with
                {
                    State = SupervisedProcessState.Running,
                    LastHeartbeatUtc = Max(current.LastHeartbeatUtc, observedAtUtc)
                }
                : null);

    public bool TryRecordHeartbeat(int processId, DateTimeOffset observedAtUtc) =>
        TryUpdate(
            processId,
            current => IsHeartbeatEligible(current)
                       && observedAtUtc >= current.LastHeartbeatUtc
                ? current with
                {
                    State = SupervisedProcessState.Running,
                    LastHeartbeatUtc = observedAtUtc
                }
                : null);

    public bool TryBeginStopping(
        int processId,
        DateTimeOffset shutdownDeadlineUtc) =>
        TryUpdate(
            processId,
            current => current.State is not (
                    SupervisedProcessState.Exited or SupervisedProcessState.Failed)
                && shutdownDeadlineUtc >= current.StartedAtUtc
                    ? current with
                    {
                        State = SupervisedProcessState.Stopping,
                        ShutdownDeadlineUtc = shutdownDeadlineUtc
                    }
                    : null);

    public bool TryMarkExited(
        int processId,
        DateTimeOffset observedAtUtc,
        bool failed = false) =>
        TryUpdate(
            processId,
            current => observedAtUtc >= current.StartedAtUtc
                ? current with
                {
                    State = failed
                        ? SupervisedProcessState.Failed
                        : SupervisedProcessState.Exited,
                    LastHeartbeatUtc = Max(current.LastHeartbeatUtc, observedAtUtc)
                }
                : null);

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

    public bool TryGet(int processId, out AgentManagedProcess? registration)
    {
        lock (syncRoot)
        {
            return registrations.TryGetValue(processId, out registration);
        }
    }

    public IReadOnlyList<int> GetLiveProcessIds()
    {
        lock (syncRoot)
        {
            return registrations.Values
                .Where(registration => registration.State is not (
                    SupervisedProcessState.Exited or SupervisedProcessState.Failed))
                .Select(registration => registration.ProcessId)
                .Order()
                .ToArray();
        }
    }

    private bool TryUpdate(
        int processId,
        Func<AgentManagedProcess, AgentManagedProcess?> update)
    {
        if (processId <= 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (!registrations.TryGetValue(processId, out var current))
            {
                return false;
            }

            var updated = update(current);
            if (updated is null)
            {
                return false;
            }

            registrations[processId] = updated;
            return true;
        }
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
