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

/// <summary>
/// Optional terminal-action contract. Implementers must check the attempt
/// immediately before committing an irreversible process or UI effect.
/// </summary>
public interface IAgentShutdownTerminalActions
{
    Task CloseNamedPipesAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken);

    Task RemoveTrayIconAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken);

    Task ExitAgentAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken);
}

public sealed class AgentShutdownAttempt
{
    private int terminalEffectsAllowed = 1;

    internal AgentShutdownAttempt(long attemptId)
    {
        AttemptId = attemptId;
    }

    public long AttemptId { get; }

    public void ThrowIfTerminalEffectIsNotAllowed(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref terminalEffectsAllowed) == 0)
        {
            throw new InvalidOperationException(
                "The shutdown attempt is no longer allowed to commit terminal effects.");
        }
    }

    internal void InvalidateTerminalEffects() =>
        Interlocked.Exchange(ref terminalEffectsAllowed, 0);
}

public sealed class AgentShutdownWorkflow
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(15);
    private static long nextAttemptId;
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
        var attempt = new AgentShutdownAttempt(Interlocked.Increment(ref nextAttemptId));
        var completed = new List<AgentShutdownStep>
        {
            AgentShutdownStep.MarkShuttingDown
        };
        var failed = new List<AgentShutdownStep>();
        var restored = false;
        var flushedCount = 0;

        await RunBoundedShutdownStepAsync(
            AgentShutdownStep.NotifyClients,
            token => actions.NotifyClientsAsync(reason, token),
            completed,
            failed,
            attempt);
        await RunBoundedShutdownStepAsync(
            AgentShutdownStep.RequestTestCancellation,
            actions.RequestTestCancellationAsync,
            completed,
            failed,
            attempt);
        await RunBoundedShutdownStepAsync(
            AgentShutdownStep.TerminateExternalToolJobs,
            actions.TerminateExternalToolJobsAsync,
            completed,
            failed,
            attempt);
        await RunBoundedShutdownStepAsync(
            AgentShutdownStep.StopMonitoring,
            actions.StopMonitoringAsync,
            completed,
            failed,
            attempt);
        var restoredResult = await RunBoundedShutdownValueStepAsync(
            AgentShutdownStep.RestoreTemporarySystemState,
            actions.RestoreTemporarySystemStateAsync,
            value => value,
            completed,
            failed,
            attempt);
        restored = restoredResult.Completed && restoredResult.Value;
        var flushedResult = await RunBoundedShutdownValueStepAsync(
            AgentShutdownStep.FlushSqliteQueues,
            actions.FlushSqliteQueuesAsync,
            value => value >= 0,
            completed,
            failed,
            attempt);
        flushedCount = flushedResult.Completed ? flushedResult.Value : 0;
        if (reason != ShutdownReason.StorageLocationSwitch)
        {
            await RunBoundedShutdownStepAsync(
                AgentShutdownStep.CloseMainApplication,
                actions.CloseMainApplicationAsync,
                completed,
                failed,
                attempt);
        }
        await RunBoundedShutdownStepAsync(
            AgentShutdownStep.StopSupervisedProcesses,
            actions.StopSupervisedProcessesAsync,
            completed,
            failed,
            attempt);

        var remainingProcessIds = processRegistry.GetLiveProcessIds();
        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunBoundedShutdownStepAsync(
                AgentShutdownStep.CloseNamedPipes,
                token => CloseNamedPipesAsync(attempt, token),
                completed,
                failed,
                attempt);
        }

        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunBoundedShutdownStepAsync(
                AgentShutdownStep.RemoveTrayIcon,
                token => RemoveTrayIconAsync(attempt, token),
                completed,
                failed,
                attempt);
        }

        if (remainingProcessIds.Count == 0 && failed.Count == 0)
        {
            await RunBoundedShutdownStepAsync(
                AgentShutdownStep.ExitAgent,
                token => ExitAgentAsync(attempt, token),
                completed,
                failed,
                attempt);
        }

        var result = new ShutdownResult(
            failed.Count == 0 && remainingProcessIds.Count == 0,
            remainingProcessIds,
            flushedCount,
            restored);
        return new(result, completed, failed);
    }

    private Task CloseNamedPipesAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken) =>
        actions is IAgentShutdownTerminalActions terminalActions
            ? terminalActions.CloseNamedPipesAsync(attempt, cancellationToken)
            : actions.CloseNamedPipesAsync(cancellationToken);

    private Task RemoveTrayIconAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken) =>
        actions is IAgentShutdownTerminalActions terminalActions
            ? terminalActions.RemoveTrayIconAsync(attempt, cancellationToken)
            : actions.RemoveTrayIconAsync(cancellationToken);

    private Task ExitAgentAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken) =>
        actions is IAgentShutdownTerminalActions terminalActions
            ? terminalActions.ExitAgentAsync(attempt, cancellationToken)
            : actions.ExitAgentAsync(cancellationToken);

    private async Task RunBoundedShutdownStepAsync(
        AgentShutdownStep step,
        Func<CancellationToken, Task> operation,
        ICollection<AgentShutdownStep> completed,
        ICollection<AgentShutdownStep> failed,
        AgentShutdownAttempt attempt)
    {
        Task? operationTask = null;
        try
        {
            using var timeout = new CancellationTokenSource(stepTimeout);
            operationTask = operation(timeout.Token);
            await operationTask.WaitAsync(timeout.Token);
            completed.Add(step);
        }
        catch (OperationCanceledException) when (
            operationTask is { IsCompleted: false })
        {
            attempt.InvalidateTerminalEffects();
            ObserveLateCompletion(operationTask);
            failed.Add(step);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException
            and not AccessViolationException)
        {
            failed.Add(step);
        }
    }

    private async Task<ShutdownStepValueResult<T>> RunBoundedShutdownValueStepAsync<T>(
        AgentShutdownStep step,
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> isValid,
        ICollection<AgentShutdownStep> completed,
        ICollection<AgentShutdownStep> failed,
        AgentShutdownAttempt attempt)
    {
        Task<T>? operationTask = null;
        try
        {
            using var timeout = new CancellationTokenSource(stepTimeout);
            operationTask = operation(timeout.Token);
            var value = await operationTask.WaitAsync(timeout.Token);
            if (!isValid(value))
            {
                failed.Add(step);
                return new(false, default!);
            }

            completed.Add(step);
            return new(true, value);
        }
        catch (OperationCanceledException) when (
            operationTask is { IsCompleted: false })
        {
            attempt.InvalidateTerminalEffects();
            ObserveLateCompletion(operationTask);
            failed.Add(step);
            return new(false, default!);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException
            and not AccessViolationException)
        {
            failed.Add(step);
            return new(false, default!);
        }
    }

    private static void ObserveLateCompletion(Task operationTask)
    {
        _ = operationTask.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record ShutdownStepValueResult<T>(bool Completed, T Value);
}
