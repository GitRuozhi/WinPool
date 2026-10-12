using WinPool.Infrastructure.Windows;
using WinPool.Ipc;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsElevationRestartServiceTests
{
    [Fact]
    public void ElevationHandoffSidHashMatchesTheIpcIdentityConvention()
    {
        const string sid = " s-1-5-21-123456789-987654321-111111111-1001 ";

        Assert.Equal(IpcIdentity.HashUserSid(sid), WindowsElevationRestartService.HashUserSid(sid));
    }

    [Fact]
    public async Task ShellExecuteRunsOnAWorkerWithoutBlockingTheCallingSta()
    {
        var workerStarted = new TaskCompletionSource<ApartmentState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerReturned = new TaskCompletionSource<Task<System.Diagnostics.Process?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWorker = new ManualResetEventSlim();
        var caller = new Thread(() =>
        {
            try
            {
                callerReturned.SetResult(WindowsElevationRestartService.StartProcessOffUiThreadAsync(
                    () =>
                    {
                        workerStarted.SetResult(Thread.CurrentThread.GetApartmentState());
                        releaseWorker.Wait();
                        return null;
                    }, CancellationToken.None));
            }
            catch (Exception exception)
            {
                callerReturned.TrySetException(exception);
            }
        });
        caller.SetApartmentState(ApartmentState.STA);
        caller.Start();

        Task<System.Diagnostics.Process?>? launch = null;
        try
        {
            launch = await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ApartmentState.MTA, await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(launch.IsCompleted);
        }
        finally
        {
            releaseWorker.Set();
            caller.Join(TimeSpan.FromSeconds(5));
            if (launch is not null)
            {
                await launch.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
