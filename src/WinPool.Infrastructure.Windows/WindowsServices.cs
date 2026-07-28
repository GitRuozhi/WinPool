using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
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

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public string SettingsPath => Path.Combine(StorageDataLocations.CurrentRoot, "settings.json");

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
        await SaveLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            SaveLock.Release();
        }
    }
}

public sealed class LocalWorkspaceStateService : IWorkspaceStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public string StatePath => Path.Combine(StorageDataLocations.CurrentRoot, "workspace.json");

    public async Task<WorkspaceUiState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<WorkspaceUiState>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(WorkspaceUiState state, CancellationToken cancellationToken = default)
    {
        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = StatePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, StatePath, true);
        }
        finally
        {
            SaveLock.Release();
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
