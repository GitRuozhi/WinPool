namespace WinPool.Agent;

internal static class ChildLifecycleCallbacks
{
    public static async Task InvokeAsync(
        Func<int, CancellationToken, Task>? callback,
        int processId,
        TimeSpan timeout,
        string callbackName)
    {
        if (callback is null)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await callback(processId, deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The child-process {callbackName} callback exceeded its lifecycle deadline.");
        }
    }
}
