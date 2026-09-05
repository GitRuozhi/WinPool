using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;

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
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    private readonly string? dataRoot;
    private bool writesBlocked;

    public LocalUserPreferencesService(string? dataRoot = null)
    {
        this.dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? null
            : Path.GetFullPath(dataRoot);
    }

    public string SettingsPath => Path.Combine(
        dataRoot ?? StorageDataLocations.CurrentRoot,
        "app-settings.json");

    public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                writesBlocked = false;
                return Task.FromResult(new UserPreferences());
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preferences = JsonSerializer.Deserialize<UserPreferences>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            writesBlocked = false;
            return Task.FromResult(
                preferences is { FormatVersion: CurrentFormatVersion }
                    ? Normalize(preferences)
                    : new UserPreferences());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The file exists but cannot be read. Returning defaults is safe only
            // as long as nothing persists them over the unreadable original.
            writesBlocked = true;
            return Task.FromResult(new UserPreferences());
        }
    }

    public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        if (writesBlocked)
        {
            throw new InvalidOperationException(
                $"User preferences could not be read; writing '{Path.GetFileName(SettingsPath)}' is blocked until the file is readable again.");
        }

        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(preferences) with { FormatVersion = CurrentFormatVersion },
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static UserPreferences Normalize(UserPreferences preferences) =>
        preferences with
        {
            FormatVersion = CurrentFormatVersion,
            PartitionIgnoreSizeBytes = Math.Clamp(
                preferences.PartitionIgnoreSizeBytes,
                0,
                1024L * 1024 * 1024)
        };
}

public sealed class LocalAgentPreferencesService : IAgentPreferencesStore
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    private readonly string? dataRoot;
    private bool writesBlocked;

    public LocalAgentPreferencesService(string? dataRoot = null)
    {
        this.dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? null
            : Path.GetFullPath(dataRoot);
    }

    public string AgentSettingsPath => Path.Combine(
        dataRoot ?? StorageDataLocations.CurrentRoot,
        "agent-settings.json");

    public Task<AgentPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(AgentSettingsPath))
            {
                writesBlocked = false;
                return Task.FromResult(new AgentPreferences());
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preferences = JsonSerializer.Deserialize<AgentPreferences>(
                File.ReadAllText(AgentSettingsPath),
                JsonOptions);
            writesBlocked = false;
            return Task.FromResult(
                preferences is { FormatVersion: CurrentFormatVersion }
                    ? Normalize(preferences)
                    : new AgentPreferences());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Same unreadable-file rule as the user preferences: defaults in
            // memory, but never persist defaults over a file we could not read.
            writesBlocked = true;
            return Task.FromResult(new AgentPreferences());
        }
    }

    public async Task<AgentPreferences> SaveAsync(
        AgentPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (writesBlocked)
        {
            throw new InvalidOperationException(
                $"Agent preferences could not be read; writing '{Path.GetFileName(AgentSettingsPath)}' is blocked until the file is readable again.");
        }

        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(AgentSettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = AgentSettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var saved = Normalize(preferences) with
            {
                FormatVersion = CurrentFormatVersion,
                SavedAtUtc = DateTimeOffset.UtcNow
            };
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    saved,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(AgentSettingsPath))
            {
                File.Replace(temporaryPath, AgentSettingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, AgentSettingsPath);
            }

            return saved;
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static AgentPreferences Normalize(AgentPreferences preferences) =>
        preferences with
        {
            FormatVersion = CurrentFormatVersion,
            MonitoringSampleRateHz = Math.Clamp(preferences.MonitoringSampleRateHz, 0.2, 20),
            DataCapacityLimitBytes = Math.Clamp(
                preferences.DataCapacityLimitBytes,
                1024L * 1024,
                1024L * 1024 * 1024 * 1024)
        };
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

/// <summary>
/// Explicit no-Agent runtime fallback for the product build. It does not
/// recreate legacy workspace.json when the Agent is unavailable.
/// </summary>
public sealed class EphemeralWorkspaceStateService : IWorkspaceStateService
{
    public Task<WorkspaceUiState?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<WorkspaceUiState?>(null);

    public Task SaveAsync(WorkspaceUiState state, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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
