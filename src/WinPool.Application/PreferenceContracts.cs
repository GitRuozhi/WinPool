using WinPool.Domain;

namespace WinPool.Application;

public interface IUserPreferencesRepository
{
    Task<ApplicationResult<UserPreferences>> LoadAsync(
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<ApplicationResult> SaveAsync(
        UserPreferences preferences,
        CorrelationId correlationId,
        CancellationToken cancellationToken);
}

public sealed record StorageLocationState(
    StorageLocationMode Mode,
    string DataRoot,
    string DatabasePath,
    bool IsWritable);

public sealed record StorageLocationSwitchPlan(
    StorageLocationMode SourceMode,
    StorageLocationMode TargetMode,
    string SourceRoot,
    string TargetRoot,
    long FileCount,
    long TotalBytes,
    string SourceManifestSha256,
    DateTimeOffset CreatedAtUtc);

public interface IStorageLocationManager
{
    Task<ApplicationResult<StorageLocationState>> GetCurrentAsync(
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<ApplicationResult<StorageLocationSwitchPlan>> PlanSwitchAsync(
        StorageLocationMode targetMode,
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<ApplicationResult<StorageLocationState>> ApplySwitchAsync(
        StorageLocationSwitchPlan plan,
        CorrelationId correlationId,
        CancellationToken cancellationToken);
}
