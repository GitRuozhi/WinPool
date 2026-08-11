using System.Diagnostics;

namespace WinPool.Agent;

internal sealed record SupervisedProcessExitOutcome(
    bool ExitedDuringGrace,
    bool ProcessTreeKillRequested,
    bool ExitedAfterKill);

internal static class SupervisedProcessExitPolicy
{
    internal static readonly TimeSpan DefaultExitGrace = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultFinalWait = TimeSpan.FromSeconds(5);

    public static async Task<SupervisedProcessExitOutcome> EnsureExitedAsync(
        Process process,
        TimeSpan exitGrace,
        TimeSpan finalWait,
        CancellationToken lifecycleCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfLessThan(exitGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(finalWait, TimeSpan.Zero);

        if (process.HasExited)
        {
            return new(true, false, true);
        }

        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(
                   lifecycleCancellation))
        {
            grace.CancelAfter(exitGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                return new(true, false, true);
            }
            catch (OperationCanceledException) when (grace.IsCancellationRequested)
            {
                // The lifecycle deadline or the short cooperative grace elapsed.
            }
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        using var final = new CancellationTokenSource(finalWait);
        try
        {
            await process.WaitForExitAsync(final.Token).ConfigureAwait(false);
            return new(false, true, true);
        }
        catch (OperationCanceledException) when (final.IsCancellationRequested)
        {
            return new(false, true, process.HasExited);
        }
    }
}
