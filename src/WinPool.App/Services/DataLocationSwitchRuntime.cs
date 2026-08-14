using System.Diagnostics;
using System.Security.Principal;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;

namespace WinPool.App.Services;

/// <summary>
/// Owns the process and storage primitives for a data-location handoff.
/// SettingsPage supplies the user-facing confirmation and error presentation.
/// </summary>
internal static class DataLocationSwitchRuntime
{
    public static StorageLocationManager CreateManager() =>
        new(
            StorageDataLocations.StandardRoot,
            StorageDataLocations.PortableRoot,
            new StoppedAgentWriteCoordinator());

    public static async Task<bool> WaitForAgentExitAsync(
        CancellationToken cancellationToken)
    {
        while (File.Exists(DataRootLayout.AgentEndpointPath(StorageDataLocations.CurrentRoot)))
        {
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    public static bool StartReplacementApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ApplicationStartupOptions.StorageLocationHandoffArgument);
        startInfo.ArgumentList.Add(ApplicationStartupOptions.WaitForProcessArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public static AgentMigrationExclusion? TryAcquireAgentMigrationExclusion()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\WinPool.Agent.{IpcIdentity.HashUserSid(sid)[..24]}",
            out var ownsMutex);
        if (!ownsMutex)
        {
            mutex.Dispose();
            return null;
        }

        return new AgentMigrationExclusion(mutex);
    }

    private sealed class StoppedAgentWriteCoordinator : IStorageWriteQuiescenceCoordinator
    {
        public Task<IAsyncDisposable> QuiesceAndFlushAsync(
            CorrelationId correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class AgentMigrationExclusion(Mutex mutex) : IDisposable
    {
        private Mutex? mutex = mutex;

        public void Release()
        {
            var owned = Interlocked.Exchange(ref mutex, null);
            if (owned is null)
            {
                return;
            }

            owned.ReleaseMutex();
            owned.Dispose();
        }

        public void Dispose() => Release();
    }
}
