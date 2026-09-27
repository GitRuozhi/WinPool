using WinPool.App.Services;

namespace WinPool.Application.Tests;

public sealed class ManualScanRunnerTests
{
    [Fact]
    public async Task FailedWorkReportsFailureAndNeverCompletion()
    {
        var runner = new ManualScanRunner();
        ManualScanResult? finished = null;

        var outcome = await runner.RunAsync(
            _ => throw new IOException("Inventory unavailable"),
            result => finished = result,
            CancellationToken.None);

        Assert.Equal(ManualScanOutcome.Failed, outcome);
        Assert.Equal(ManualScanOutcome.Failed, finished?.Outcome);
        Assert.IsType<IOException>(finished?.Error);
    }

    [Fact]
    public async Task ConcurrentRequestIsSkippedWithoutFinishingCallback()
    {
        var runner = new ManualScanRunner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCount = 0;
        var first = runner.RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
            return true;
        }, _ => finishCount++, CancellationToken.None);
        await started.Task;

        var second = await runner.RunAsync(
            _ => Task.FromResult(true),
            _ => finishCount++,
            CancellationToken.None);
        Assert.Equal(ManualScanOutcome.Skipped, second);
        Assert.Equal(0, finishCount);

        release.SetResult();
        Assert.Equal(ManualScanOutcome.Completed, await first);
        Assert.Equal(1, finishCount);
    }

    [Fact]
    public async Task StaleResultIsSkippedAfterWorkWithoutReportingCompletion()
    {
        var runner = new ManualScanRunner();
        ManualScanResult? finished = null;

        var outcome = await runner.RunAsync(
            _ => Task.FromResult(false),
            result => finished = result,
            CancellationToken.None);

        Assert.Equal(ManualScanOutcome.Skipped, outcome);
        Assert.Equal(ManualScanOutcome.Skipped, finished?.Outcome);
    }

    [Fact]
    public async Task CancelAfterStartReportsCanceledWithoutCompletion()
    {
        var runner = new ManualScanRunner();
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualScanResult? finished = null;
        var pending = runner.RunAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, result => finished = result, cancel.Token);
        await started.Task;

        cancel.Cancel();
        Assert.Equal(ManualScanOutcome.Canceled, await pending);
        Assert.Equal(ManualScanOutcome.Canceled, finished?.Outcome);
    }
}
