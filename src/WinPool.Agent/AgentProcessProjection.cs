using WinPool.Application;

namespace WinPool.Agent;

internal static class AgentProcessProjection
{
    internal static ProcessRegistration ToRegistration(AgentManagedProcess process) =>
        new(
            process.ProcessInstanceId,
            process.ProcessId,
            process.Kind switch
            {
                AgentManagedProcessKind.MainApplication => WorkerKind.MainApplication,
                AgentManagedProcessKind.InventoryWorker => WorkerKind.Inventory,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(process), process.Kind, "Unknown managed process kind.")
            },
            process.CorrelationId,
            process.StartedAtUtc,
            process.LastHeartbeatUtc,
            process.State,
            process.OwnsJobObject,
            process.ShutdownDeadlineUtc);

    internal static DateTimeOffset GetStartedAtUtc(int processId) =>
        AgentClientProcessVerifier.TryGetStartedAtUtc(processId)
        ?? throw new InvalidOperationException(
            "The supervised child-process start witness is unavailable.");
}
