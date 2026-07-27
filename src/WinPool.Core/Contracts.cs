namespace WinPool.Core;

using System.Collections.ObjectModel;

public interface IStorageInventoryProvider
{
    Task<StorageSnapshot> ScanAsync(CancellationToken cancellationToken);
}

public interface IHardwareInventoryProvider
{
    Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken);
}

public interface IReadOnlyCommandRunner
{
    Task<ReadOnlyCommandResult> RunAsync(string fixedCommand, CancellationToken cancellationToken);
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

public interface IUserPreferencesService
{
    Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
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
    string DeduplicationKey);

public interface IGlobalNotificationService
{
    ReadOnlyObservableCollection<GlobalNotification> Notifications { get; }

    void PublishWarning(string title, string message, string source, string? occurrenceKey = null);

    void PublishError(string title, string message, string source, string? occurrenceKey = null);

    void Dismiss(string id);
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
