using System.Diagnostics;
using WinPool.App.Services;

namespace WinPool.App.Tests;

public sealed class DataLocationHandoffTests
{
    [Fact]
    public async Task ExistingUnownedAgentMutexCanBeAcquiredAndIsHeldUntilRelease()
    {
        var name = $"Local\\WinPool.Migration.Tests.{Guid.NewGuid():N}";
        using var existing = new Mutex(initiallyOwned: false, name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var exclusion = await DataLocationSwitchRuntime.AcquireAgentMigrationExclusionAsync(name, timeout.Token);
        Assert.NotNull(exclusion);
        // Windows Mutex is thread owned: test the competing owner on another
        // thread, then release on this same continuation that acquired it.
        var competitorAcquired = false;
        var competitor = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            competitorAcquired = mutex.WaitOne(0);
            if (competitorAcquired) mutex.ReleaseMutex();
        });
        competitor.Start();
        Assert.True(competitor.Join(TimeSpan.FromSeconds(5)));
        Assert.False(competitorAcquired);
        exclusion.Release();
        exclusion.Release();
        Assert.True(existing.WaitOne(0));
        existing.ReleaseMutex();
    }

    [Fact]
    public async Task BusyAgentMutexWaitHonorsShutdownDeadlineWithoutTakingOwnership()
    {
        var name = $"Local\\WinPool.Migration.Tests.{Guid.NewGuid():N}";
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, name);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        owner.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Assert.Null(await DataLocationSwitchRuntime.AcquireAgentMigrationExclusionAsync(name, timeout.Token));
        }
        finally
        {
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task MissingEndpointDoesNotProveThatTheCapturedProcessExited()
    {
        using var current = Process.GetCurrentProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Assert.False(await DataLocationSwitchRuntime.WaitForAgentExitAsync(current, timeout.Token));
        Assert.False(current.HasExited);
    }
}
