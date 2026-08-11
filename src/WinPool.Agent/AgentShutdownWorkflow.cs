using WinPool.Application;

namespace WinPool.Agent;

public enum AgentShutdownStep
{
    MarkShuttingDown,
    NotifyClients,
    RequestTestCancellation,
    TerminateExternalToolJobs,
    StopMonitoring,
    RestoreTemporarySystemState,
    FlushSqliteQueues,
    CloseNamedPipes,
    CloseMainApplication,
    StopSupervisedProcesses,
    RemoveTrayIcon,
    ExitAgent
}

public sealed record AgentShutdownExecution(
    ShutdownResult Result,
    IReadOnlyList<AgentShutdownStep> CompletedSteps,
    IReadOnlyList<AgentShutdownStep> FailedSteps);

/// <summary>
/// Typed shutdown capabilities. Implementations may operate on processes and system state;
/// callers cannot supply executable paths, command lines, or arbitrary commands.
/// </summary>
public interface IAgentShutdownActions
{
    bool HasActiveTest { get; }

    Task NotifyClientsAsync(
        ShutdownReason reason,
        CancellationToken cancellationToken);

    Task RequestTestCancellationAsync(CancellationToken cancellationToken);

    Task TerminateExternalToolJobsAsync(CancellationToken cancellationToken);

    Task StopMonitoringAsync(CancellationToken cancellationToken);

    Task<bool> RestoreTemporarySystemStateAsync(CancellationToken cancellationToken);

    Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken);

    Task CloseNamedPipesAsync(CancellationToken cancellationToken);

    Task CloseMainApplicationAsync(CancellationToken cancellationToken);

    Task StopSupervisedProcessesAsync(CancellationToken cancellationToken);

    Task RemoveTrayIconAsync(CancellationToken cancellationToken);

    Task ExitAgentAsync(CancellationToken cancellationToken);
}

public sealed class AgentShutdownWorkflow
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(15);
    private readonly IAgentShutdownActions actions;
    private readonly AgentProcessRegistry processRegistry;
    private readonly TimeSpan stepTimeout;

    public AgentShutdownWorkflow(
        IAgentShutdownActions actions,
        AgentProcessRegistry processRegistry,
        TimeSpan? stepTimeout = null)
    {
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.stepTimeout = stepTimeout ?? DefaultStepTimeout;
        if (this.stepTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepTimeout),
                "The shutdown step timeout must be positive.");
        }
    }

    public bool HasActiveTest => actions.HasActiveTest;

    public async Task<AgentShutdownExecution> ExecuteAsync(ShutdownReason reason)
    {
        var completed = new List<AgentShutdownStep>
        {
            AgentShutdownStep.MarkShuttingDown
        };
        var failed = new List<AgentShutdownStep>();
        var restored = false;
        var flushedCount = 0;

        await RunStepAsync(
            AgentShutdownStep.NotifyClients,
            token => actions.NotifyClientsAsync(reason, token),
            completed,
            failed);
        await RunStepAsync(
            AgentShutdownStep.RequestTestCancellation,
            actions.RequestTestCancellationAsync,
            completed,
            failed);
        await RunStepAsync(
            AgentShutdownStep.TerminateExternalToolJobs,
            actions.TerminateExternalToolJobsAsync,
            completed,
            failed);
        await RunStepAsync(
            AgentShutdownStep.StopMonitoring,
            actions.StopMonitoringAsync,
            completed,
            failed);
        await RunStepAsync(
            AgentShutdownStep.RestoreTemporarySystemState,
            async token =>
            {
                restored = await actions.RestoreTemporarySystemStateAsync(token);
                if (!restored)
                {
                    throw new InvalidOperationException(
                        "Temporary system state remains unrestored.");
                }
            },
            completed,
            failed);
        await RunStepAsync(
            AgentShutdownStep.FlushSqliteQueues,
            async token =>
            {
                flushedCount = await actions.FlushSqliteQueuesAsync(token);
                if (flushedCount < 0)
                {
                    throw new InvalidOperationException(
                        "The flushed event count cannot be negative.");
                }
            },
            completed,
            failed);
        if (reason != ShutdownReason.StorageLocationSwitch)
        {
            await RunStepAsync(
                AgentShutdownStep.CloseMainApplication,
                actions.CloseMainApplicationAsync,
                completed,
                failed);
        }
        await RunStepAsync(
            AgentShutdownStep.StopSupervisedProcesses,
            actions.StopSupervisedProcessesAsync,
            completed,
            failed);

        var remainingProcessIds = processRegistry.GetLiveProcessIds();
        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunStepAsync(
                AgentShutdownStep.CloseNamedPipes,
                actions.CloseNamedPipesAsync,
                completed,
                failed);
        }

        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunStepAsync(
                AgentShutdownStep.RemoveTrayIcon,
                actions.RemoveTrayIconAsync,
                completed,
                failed);
        }

        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunStepAsync(
                AgentShutdownStep.ExitAgent,
                actions.ExitAgentAsync,
                completed,
                failed);
        }

        var result = new ShutdownResult(
            failed.Count == 0 && remainingProcessIds.Count == 0,
            remainingProcessIds,
            flushedCount,
            restored);
        return new(result, completed, failed);
    }

    private async Task RunStepAsync(
        AgentShutdownStep step,
        Func<CancellationToken, Task> operation,
        ICollection<AgentShutdownStep> completed,
        ICollection<AgentShutdownStep> failed)
    {
        try
        {
            // Once complete exit is accepted, a disconnected caller cannot cancel cleanup.
            using var timeout = new CancellationTokenSource(stepTimeout);
            await operation(timeout.Token);
            completed.Add(step);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException
            and not AccessViolationException)
        {
            failed.Add(step);
        }
    }
}
