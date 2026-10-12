namespace WinPool.Agent;

/// <summary>
/// Owns optional startup work until it completes or ordered shutdown drains it.
/// Completion cannot publish Ready or start inventory after shutdown begins.
/// </summary>
internal sealed class AgentStartupRecovery : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private Task recoveryTask = Task.CompletedTask;
    private bool started;
    private bool stopping;

    internal void Start(
        Func<CancellationToken, Task> recover,
        Action onReady,
        Action<Exception> onFailure)
    {
        lock (sync)
        {
            if (started || stopping) return;
            started = true;
            recoveryTask = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    await recover(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                lock (sync)
                {
                    if (stopping) return;
                    if (failure is not null) onFailure(failure);
                    // Optional monitoring recovery has finished, including
                    // its failure report. It must not disable the runtime.
                    onReady();
                }
            });
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (sync)
        {
            stopping = true;
            pending = recoveryTask;
        }

        cancellation.Cancel();
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => cancellation.Dispose();
}
