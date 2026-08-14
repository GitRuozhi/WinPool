using WinPool.Application;

namespace WinPool.Agent;

internal enum TestPauseState
{
    None,
    Running,
    Pausing,
    Paused
}

/// <summary>
/// Owns one test run and its cooperative pause gate. It never suspends a worker
/// thread: a request becomes Paused only when the workflow reaches a safe step
/// boundary, and cancellation always releases a waiting workflow.
/// </summary>
internal sealed class AgentTestCoordinator
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? task;
    private TestRunId? runId;
    private TestPauseState pauseState;
    private TaskCompletionSource? resumeSignal;

    public event Action<TestRunId?, TestPauseState>? PauseStateChanged;

    public bool HasActiveTest { get { lock (sync) return task is { IsCompleted: false }; } }
    public TestRunId? ActiveRunId { get { lock (sync) return task is { IsCompleted: false } ? runId : null; } }
    public TestPauseState PauseState { get { lock (sync) return pauseState; } }

    public bool TryReserve(TestRunId requestedRunId, CancellationTokenSource runCancellation)
    {
        ArgumentNullException.ThrowIfNull(runCancellation);
        lock (sync)
        {
            if (task is { IsCompleted: false }) return false;
            cancellation = runCancellation;
            runId = requestedRunId;
            pauseState = TestPauseState.Running;
            task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        PublishPauseState(requestedRunId, TestPauseState.Running);
        return true;
    }

    public void Attach(Task activeTask)
    {
        ArgumentNullException.ThrowIfNull(activeTask);
        lock (sync) task = activeTask;
    }

    public bool TryRequestPause(TestRunId requestedRunId)
    {
        lock (sync)
        {
            if (runId != requestedRunId || task is not { IsCompleted: false }
                || pauseState is TestPauseState.None or TestPauseState.Paused)
                return false;
            pauseState = TestPauseState.Pausing;
        }
        PublishPauseState(requestedRunId, TestPauseState.Pausing);
        return true;
    }

    public bool TryResume(TestRunId requestedRunId)
    {
        TaskCompletionSource? signal;
        lock (sync)
        {
            if (runId != requestedRunId || pauseState != TestPauseState.Paused)
                return false;
            pauseState = TestPauseState.Running;
            signal = resumeSignal;
            resumeSignal = null;
        }
        signal?.TrySetResult();
        PublishPauseState(requestedRunId, TestPauseState.Running);
        return true;
    }

    public async Task WaitForSafePauseBoundaryAsync(TestRunId requestedRunId, CancellationToken cancellationToken)
    {
        Task wait;
        lock (sync)
        {
            if (runId != requestedRunId || pauseState != TestPauseState.Pausing)
                return;
            pauseState = TestPauseState.Paused;
            resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = resumeSignal.Task;
        }
        PublishPauseState(requestedRunId, TestPauseState.Paused);
        await wait.WaitAsync(cancellationToken);
    }

    public bool TryCancel(TestRunId requestedRunId)
    {
        TaskCompletionSource? signal;
        lock (sync)
        {
            if (runId != requestedRunId || task is not { IsCompleted: false } || cancellation is null)
                return false;
            cancellation.Cancel();
            signal = resumeSignal;
            resumeSignal = null;
        }
        signal?.TrySetResult();
        return true;
    }

    public Task? CancelActive()
    {
        TestRunId? active;
        lock (sync)
        {
            active = runId;
        }
        if (active is not null) TryCancel(active.Value);
        lock (sync) return task;
    }

    public void ReleaseReservation()
    {
        lock (sync)
        {
            cancellation = null; runId = null; task = null; pauseState = TestPauseState.None;
            resumeSignal?.TrySetResult(); resumeSignal = null;
        }
        PublishPauseState(null, TestPauseState.None);
    }

    public void Complete(TestRunId completedRunId)
    {
        var changed = false;
        lock (sync)
        {
            if (runId == completedRunId)
            {
                runId = null; cancellation = null; pauseState = TestPauseState.None;
                resumeSignal?.TrySetResult(); resumeSignal = null; changed = true;
            }
        }
        if (changed) PublishPauseState(null, TestPauseState.None);
    }

    private void PublishPauseState(TestRunId? id, TestPauseState state) => PauseStateChanged?.Invoke(id, state);
}
