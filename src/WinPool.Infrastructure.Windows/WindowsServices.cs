using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Core;

namespace WinPool.Infrastructure.Windows;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    public PrivilegeState Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? PrivilegeState.Administrator
                : PrivilegeState.StandardUser;
        }
    }
}

public sealed class WindowsElevationRestartService : IElevationRestartService
{
    public Task<ElevationRestartResult> RestartElevatedAsync(
        string startupArgument,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Task.FromResult(new ElevationRestartResult(
                ElevationRestartStatus.Failed,
                "The WinPool executable path could not be determined."));
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments =
                    $"{startupArgument} {ApplicationStartupOptions.WaitForProcessArgument} {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return Task.FromResult(process is null
                ? new ElevationRestartResult(
                    ElevationRestartStatus.Failed,
                    "Windows did not start the elevated WinPool process.")
                : new ElevationRestartResult(ElevationRestartStatus.Started));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Task.FromResult(new ElevationRestartResult(ElevationRestartStatus.Cancelled));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(new ElevationRestartResult(
                ElevationRestartStatus.Failed,
                ex.Message));
        }
    }
}

public sealed class LocalUserPreferencesService : IUserPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPool",
        "settings.json");

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UserPreferences();
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<UserPreferences>(stream, JsonOptions, cancellationToken)
                ?? new UserPreferences();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UserPreferences();
        }
    }

    public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, SettingsPath, true);
    }
}

public sealed class WindowsStorageInventoryProvider : IStorageInventoryProvider
{
    public const int TimeoutSeconds = 30;
    private readonly string _scriptPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public WindowsStorageInventoryProvider(string? scriptPath = null)
    {
        _scriptPath = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "Scripts", "Get-StorageInventory.ps1");
    }

    public async Task<StorageSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
        {
            throw new FileNotFoundException("The fixed WinPool inventory script was not found.", _scriptPath);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{_scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the read-only inventory process.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Storage inventory scan exceeded {TimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InventoryScanException(
                $"The read-only inventory process failed with exit code {process.ExitCode}.",
                error);
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InventoryScanException("The read-only inventory process returned no JSON.", error);
        }

        try
        {
            var raw = JsonSerializer.Deserialize<RawSnapshot>(output, JsonOptions)
                ?? throw new JsonException("The JSON snapshot was empty.");
            return RawSnapshotProjector.Project(raw, output);
        }
        catch (JsonException ex)
        {
            throw new InventoryScanException($"The inventory JSON could not be parsed: {ex.Message}", error, ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class InventoryScanException : Exception
{
    public InventoryScanException(string message, string diagnostic, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostic = diagnostic;
    }

    public string Diagnostic { get; }
}
