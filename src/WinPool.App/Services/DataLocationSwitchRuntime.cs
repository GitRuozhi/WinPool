using System.Diagnostics;
using System.Security.Principal;
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
    public static StorageLocationManager CreateManager(Func<bool> simulationSavesFlushed) =>
        new(
            StorageDataLocations.StandardRoot,
            StorageDataLocations.PortableRoot,
            new StoppedAgentWriteCoordinator(simulationSavesFlushed));

    public static Process CaptureAgentProcess(AgentHandshake handshake)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(handshake.ProcessId);
            // Keep the exact OS process handle through shutdown, so PID reuse
            // cannot turn endpoint removal into evidence that its owner exited.
            _ = process.SafeHandle;
            if (process.HasExited
                || !string.Equals(process.ProcessName, "WinPool.Agent", StringComparison.OrdinalIgnoreCase)
                || Math.Abs((process.StartTime.ToUniversalTime() - handshake.StartedAtUtc.UtcDateTime).TotalSeconds) > 5)
                throw new InvalidDataException("The connected Agent process witness is unavailable or changed.");
            return process;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            process?.Dispose();
            throw new IOException("The connected Agent process witness is unavailable or changed.", exception);
        }
    }

    public static async Task<bool> WaitForAgentExitAsync(
        Process agentProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            await agentProcess.WaitForExitAsync(cancellationToken);
            while (File.Exists(DataRootLayout.AgentEndpointPath(StorageDataLocations.CurrentRoot)))
                await Task.Delay(100, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static bool StartReplacementApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(CreateReplacementStartInfo(executable, Environment.ProcessId));
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateReplacementStartInfo(string executable, int previousProcessId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ApplicationStartupOptions.StorageLocationHandoffArgument);
        startInfo.ArgumentList.Add(ApplicationStartupOptions.WaitForProcessArgument);
        startInfo.ArgumentList.Add(previousProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(ApplicationStartupOptions.PageArgument);
        startInfo.ArgumentList.Add(nameof(ApplicationStartupTarget.Settings));
        return startInfo;
    }

    public static Task<AgentMigrationExclusion?> AcquireAgentMigrationExclusionAsync(
        CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("The current Windows SID is unavailable.");
        return AcquireAgentMigrationExclusionAsync(
            $"Local\\WinPool.Agent.{IpcIdentity.HashUserSid(sid)[..24]}", cancellationToken);
    }

    internal static async Task<AgentMigrationExclusion?> AcquireAgentMigrationExclusionAsync(
        string mutexName,
        CancellationToken cancellationToken)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var ownsMutex = false;
        try
        {
            while (!ownsMutex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) await Task.Delay(100, cancellationToken);
            }
            return new AgentMigrationExclusion(mutex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally { if (!ownsMutex) mutex.Dispose(); }
    }

    private sealed class StoppedAgentWriteCoordinator(Func<bool> simulationSavesFlushed) : IStorageWriteQuiescenceCoordinator
    {
        public Task<IAsyncDisposable> QuiesceAndFlushAsync(
            CorrelationId correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!simulationSavesFlushed() || File.Exists(DataRootLayout.AgentEndpointPath(StorageDataLocations.CurrentRoot)))
                throw new InvalidOperationException("Migration requires all App saves flushed and the old Agent endpoint removed while its exclusion is held.");
            return Task.FromResult<IAsyncDisposable>(new VerifiedStoppedAgentLease());
        }
    }

    private sealed class VerifiedStoppedAgentLease : IAsyncDisposable
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
