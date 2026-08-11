using WinPool.Application;

namespace WinPool.Agent;

/// <summary>
/// Owns the externally visible lifecycle state. The tray is a presentation
/// surface only; control admission and snapshots read this single state.
/// </summary>
public sealed class AgentLifecycleStateStore
{
    private readonly object syncRoot = new();
    private readonly AgentProcessRegistry processRegistry;
    private AgentLifecycleState state = AgentLifecycleState.Running;
    private DateTimeOffset? attemptedAtUtc;
    private IReadOnlyList<string> failedStepCodes = [];

    public AgentLifecycleStateStore(AgentProcessRegistry processRegistry)
    {
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
    }

    public AgentLifecycleState State
    {
        get
        {
            lock (syncRoot)
            {
                return state;
            }
        }
    }

    public AgentShutdownStatus Snapshot()
    {
        lock (syncRoot)
        {
            return new(
                state,
                attemptedAtUtc,
                failedStepCodes,
                processRegistry.GetLiveProcessIds(),
                state == AgentLifecycleState.ShutdownPending);
        }
    }

    public void MarkShuttingDown(DateTimeOffset attemptedAtUtc)
    {
        lock (syncRoot)
        {
            state = AgentLifecycleState.ShuttingDown;
            this.attemptedAtUtc = attemptedAtUtc;
            failedStepCodes = [];
        }
    }

    public void RecordExecution(AgentShutdownExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (syncRoot)
        {
            failedStepCodes = execution.FailedSteps
                .Select(ToStableCode)
                .ToArray();
            state = execution.Result.Completed
                ? AgentLifecycleState.Stopped
                : AgentLifecycleState.ShutdownPending;
        }
    }

    private static string ToStableCode(AgentShutdownStep step) =>
        $"agent.shutdown.{string.Concat(step.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()))}";
}
