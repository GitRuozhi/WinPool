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
    private AgentLifecycleState state;
    private DateTimeOffset? attemptedAtUtc;
    private IReadOnlyList<string> failedStepCodes = [];

    public AgentLifecycleStateStore(
        AgentProcessRegistry processRegistry,
        AgentLifecycleState initialState = AgentLifecycleState.Running)
    {
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        state = initialState;
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

    public void MarkRecovering()
    {
        lock (syncRoot)
        {
            if (state is AgentLifecycleState.Starting or AgentLifecycleState.Recovering)
            {
                state = AgentLifecycleState.Recovering;
            }
        }
    }

    public void MarkReady()
    {
        lock (syncRoot)
        {
            if (state is AgentLifecycleState.Starting or AgentLifecycleState.Recovering)
            {
                state = AgentLifecycleState.Running;
            }
        }
    }

    public void MarkFailed(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        lock (syncRoot)
        {
            state = AgentLifecycleState.Failed;
            failedStepCodes = [failureCode];
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
