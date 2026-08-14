using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.ToolManagement;

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

    public LocalUserPreferencesService(string? dataRoot = null)
    {
        this.dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? null
            : Path.GetFullPath(dataRoot);
    }

    public string SettingsPath => Path.Combine(
        dataRoot ?? StorageDataLocations.CurrentRoot,
        "settings.json");

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UserPreferences();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preferences = JsonSerializer.Deserialize<UserPreferences>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return preferences is { FormatVersion: CurrentFormatVersion }
                ? Normalize(preferences)
                : new UserPreferences();
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
            MonitoringSampleRateHz = Math.Clamp(preferences.MonitoringSampleRateHz, 0.2, 20),
            CustomToolPaths = preferences.CustomToolPaths is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : preferences.CustomToolPaths
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                        && !string.IsNullOrWhiteSpace(pair.Value)
                        && Path.IsPathFullyQualified(pair.Value))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => Path.GetFullPath(pair.Value),
                        StringComparer.OrdinalIgnoreCase)
        };
}

/// <summary>
/// Stores registered external-tool overrides inside the sole user-preferences
/// document. It keeps an immutable in-memory view for synchronous discovery
/// while serializing changes through the preference service's atomic writer.
/// </summary>
public sealed class PreferencesToolPathConfiguration : IMutableToolPathConfiguration
{
    private readonly IUserPreferencesService preferencesService;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private IReadOnlyDictionary<string, string> paths;

    public PreferencesToolPathConfiguration(IUserPreferencesService preferencesService)
        : this(
            preferencesService,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
    {
    }

    private PreferencesToolPathConfiguration(
        IUserPreferencesService preferencesService,
        IReadOnlyDictionary<string, string> paths)
    {
        this.preferencesService = preferencesService
            ?? throw new ArgumentNullException(nameof(preferencesService));
        this.paths = paths;
    }

    public static async Task<PreferencesToolPathConfiguration> CreateAsync(
        IUserPreferencesService preferencesService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferencesService);
        var preferences = await preferencesService.LoadAsync(cancellationToken);
        return new PreferencesToolPathConfiguration(
            preferencesService,
            Normalize(preferences.CustomToolPaths));
    }

    public string? GetCustomExecutablePath(ToolId toolId) =>
        paths.TryGetValue(toolId.Value, out var path) ? path : null;

    public async Task SetCustomExecutablePathAsync(
        ToolId toolId,
        string? executablePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolId.Value))
        {
            throw new ArgumentException("A ToolId is required.", nameof(toolId));
        }
        if (!string.IsNullOrWhiteSpace(executablePath)
            && !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "A custom tool path must be fully qualified.",
                nameof(executablePath));
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var latest = await preferencesService.LoadAsync(cancellationToken);
            var updated = new Dictionary<string, string>(
                Normalize(latest.CustomToolPaths),
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                updated.Remove(toolId.Value);
            }
            else
            {
                updated[toolId.Value] = Path.GetFullPath(executablePath);
            }

            paths = new Dictionary<string, string>(updated, StringComparer.OrdinalIgnoreCase);
            await preferencesService.SaveAsync(
                latest with { FormatVersion = 1, CustomToolPaths = paths },
                cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? values) =>
        values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : values
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                    && !string.IsNullOrWhiteSpace(pair.Value)
                    && Path.IsPathFullyQualified(pair.Value))
                .ToDictionary(
                    pair => pair.Key,
                    pair => Path.GetFullPath(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
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
