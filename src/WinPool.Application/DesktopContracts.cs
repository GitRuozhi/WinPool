namespace WinPool.Application;

using System.Collections.ObjectModel;
using WinPool.Domain;

public interface IStorageInventoryProvider
{
    Task<StorageSnapshot> ScanAsync(CancellationToken cancellationToken);
}

public interface IHardwareInventoryProvider
{
    Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken);
}

public interface IReadOnlyInventoryCommandRunner
{
    Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken);
}

public sealed record ReadOnlyCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

public interface IStorageSystemRepository
{
    Task<IReadOnlyList<StorageSystemDocument>> LoadSimulationsAsync(
        CancellationToken cancellationToken = default);

    Task SaveSimulationAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default);

    Task DeleteSimulationAsync(
        string id,
        CancellationToken cancellationToken = default);
}

public interface IStorageSystemImportExportService
{
    Task<string?> ExportAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default);

    Task<StorageSystemDocument?> ImportAsync(CancellationToken cancellationToken = default);
}

public interface IMachineRecordService
{
    Task RecordLocalScanAsync(
        StorageSystemDocument localDocument,
        CancellationToken cancellationToken = default);

    Task<StorageSystemDocument?> LoadLocalScanAsync(
        CancellationToken cancellationToken = default);
}

public sealed record WorkspaceUiState(
    string ShellPage = "",
    string ActiveSystemId = "",
    ManageWorkspaceCategory Category = ManageWorkspaceCategory.System,
    IReadOnlyDictionary<ManageWorkspaceCategory, string>? CategorySelections = null,
    string HighlightedTopologyStableId = "");

public interface IWorkspaceStateService
{
    Task<WorkspaceUiState?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WorkspaceUiState state, CancellationToken cancellationToken = default);
}

public interface ISimulationOperationService
{
    SimulationOperationResult Apply(
        StorageSystemDocument document,
        SimulationOperationRequest request);
}

public interface IPrivilegeService
{
    PrivilegeState Current { get; }
}

public interface IUserPreferencesReader
{
    Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Write access to app-settings.json. Only the App process composes this
/// interface; the Agent reads user preferences through IUserPreferencesReader.
/// </summary>
public interface IUserPreferencesService : IUserPreferencesReader
{
    Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}

public interface IAgentPreferencesReader
{
    Task<AgentPreferences> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Write access to agent-settings.json. Only the Agent process composes this
/// interface; the App reads background preferences through
/// IAgentPreferencesReader and changes them through the typed Agent request.
/// SaveAsync returns the persisted snapshot including its fresh SavedAtUtc
/// label so callers never have to re-read the file.
/// </summary>
public interface IAgentPreferencesStore : IAgentPreferencesReader
{
    Task<AgentPreferences> SaveAsync(
        AgentPreferences preferences,
        CancellationToken cancellationToken = default);
}

public interface IExportService
{
    Task<string?> ExportAsync(
        StorageSnapshot snapshot,
        StorageUnitRef? selectedUnit,
        CancellationToken cancellationToken = default);
}

public enum GlobalNotificationSeverity
{
    Info,
    Warning,
    Error
}

public sealed record GlobalNotification(
    string Id,
    GlobalNotificationSeverity Severity,
    string Title,
    string Message,
    string Source,
    DateTimeOffset CreatedAt,
    string DeduplicationKey,
    bool AutoDismiss = true);

public interface IGlobalNotificationService
{
    ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    void PublishInfo(
        string title,
        string message,
        string source,
        string? occurrenceKey = null,
        bool autoDismiss = true);

    void PublishWarning(string title, string message, string source, string? occurrenceKey = null);

    void PublishError(string title, string message, string source, string? occurrenceKey = null);

    void Dismiss(string id);

    void DismissByKey(string deduplicationKey);
}

public enum ElevationRestartStatus
{
    Started,
    Cancelled,
    Failed
}

public sealed record ElevationRestartResult(ElevationRestartStatus Status, string? ErrorMessage = null);

public interface IElevationRestartService
{
    Task<ElevationRestartResult> RestartElevatedAsync(
        string startupArgument,
        CancellationToken cancellationToken = default);
}
