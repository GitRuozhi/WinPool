namespace WinPool.App.Services;

public enum ManualScanOutcome
{
    Completed,
    Failed,
    Skipped,
    Canceled
}

public readonly record struct ManualScanResult(ManualScanOutcome Outcome, Exception? Error = null);

/// <summary>Runs one manual inventory request at a time and preserves its outcome.</summary>
public sealed class ManualScanRunner
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ManualScanOutcome> RunAsync(
        Func<CancellationToken, Task<bool>> work,
        Action<ManualScanResult> finish,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ManualScanOutcome.Canceled;
        }

        if (!await gate.WaitAsync(0))
        {
            return ManualScanOutcome.Skipped;
        }

        try
        {
            ManualScanResult result;
            try
            {
                var applied = await work(cancellationToken);
                result = new(applied ? ManualScanOutcome.Completed : ManualScanOutcome.Skipped);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new(ManualScanOutcome.Canceled);
            }
            catch (Exception exception)
            {
                result = new(ManualScanOutcome.Failed, exception);
            }

            finish(result);
            return result.Outcome;
        }
        finally
        {
            gate.Release();
        }
    }
}
