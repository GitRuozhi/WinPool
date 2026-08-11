using WinPool.Application;

namespace WinPool.Agent;

/// <summary>
/// Owns the single active test slot independently from request routing and the
/// concrete test execution pipeline.
/// </summary>
internal sealed class AgentTestCoordinator
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? task;
    private TestRunId? runId;

    public bool HasActiveTest
    {
        get
        {
            lock (sync)
            {
                return task is { IsCompleted: false };
            }
        }
    }

    public TestRunId? ActiveRunId
    {
        get
        {
            lock (sync)
            {
                return task is { IsCompleted: false } ? runId : null;
            }
        }
    }

    public bool TryReserve(TestRunId requestedRunId, CancellationTokenSource runCancellation)
    {
        ArgumentNullException.ThrowIfNull(runCancellation);
        lock (sync)
        {
            if (task is { IsCompleted: false })
            {
                return false;
            }

            cancellation = runCancellation;
            runId = requestedRunId;
            task = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return true;
        }
    }

    public void Attach(Task activeTask)
    {
        ArgumentNullException.ThrowIfNull(activeTask);
        lock (sync)
        {
            task = activeTask;
        }
    }

    public bool TryCancel(TestRunId requestedRunId)
    {
        lock (sync)
        {
            if (runId != requestedRunId
                || task is not { IsCompleted: false }
                || cancellation is null)
            {
                return false;
            }

            cancellation.Cancel();
            return true;
        }
    }

    public Task? CancelActive()
    {
        lock (sync)
        {
            cancellation?.Cancel();
            return task;
        }
    }

    public void ReleaseReservation()
    {
        lock (sync)
        {
            cancellation = null;
            runId = null;
            task = null;
        }
    }

    public void Complete(TestRunId completedRunId)
    {
        lock (sync)
        {
            if (runId == completedRunId)
            {
                runId = null;
                cancellation = null;
            }
        }
    }
}
