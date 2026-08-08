using WinPool.Domain;

namespace WinPool.Application;

public enum StorageSystemSourceKind
{
    Local,
    Simulation,
    Import,
    Replay
}

public enum IdentityStability
{
    Stable,
    Temporary
}

public sealed record StorageObjectView(
    StorageObjectId Id,
    StorageObjectId? ParentId,
    string DisplayName,
    IdentityStability IdentityStability,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record StorageSystemView(
    SystemId Id,
    string DisplayName,
    StorageSystemSourceKind SourceKind,
    string InventoryVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<StorageObjectView> Objects);

public enum StorageChangeKind
{
    SnapshotReplaced,
    ObjectAdded,
    ObjectUpdated,
    ObjectRemoved
}

public sealed record StorageChange(
    SystemId SystemId,
    StorageChangeKind Kind,
    string InventoryVersion,
    DateTimeOffset OccurredAtUtc,
    StorageObjectView? Object,
    StorageObjectId? RemovedObjectId);

public enum WorkspacePage
{
    Manage,
    Edit,
    Test,
    Monitor,
    Development,
    Settings
}

public sealed record WorkspaceState(
    WorkspacePage ActivePage,
    SystemId? ActiveSystemId,
    StorageObjectKind ActiveCategory,
    StorageObjectId? SelectedObjectId,
    StorageObjectId? HighlightedTopologyObjectId,
    IReadOnlyDictionary<StorageObjectKind, StorageObjectId> RememberedSelections);

public sealed record ObjectComparisonValue(
    StorageObjectId ObjectId,
    string? DisplayValue);

public sealed record ObjectComparisonRow(
    string PropertyKey,
    string PropertyTextKey,
    IReadOnlyList<ObjectComparisonValue> Values);

public interface IStorageSystemQuery
{
    Task<ApplicationResult<StorageSystemView>> GetSystemAsync(
        SystemId systemId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<StorageChange> WatchSystemAsync(
        SystemId systemId,
        CancellationToken cancellationToken);
}

public interface IWorkspaceQuery
{
    Task<ApplicationResult<WorkspaceState>> RestoreAsync(
        CancellationToken cancellationToken);

    Task<ApplicationResult<IReadOnlyList<ObjectComparisonRow>>> CompareAsync(
        SystemId systemId,
        StorageObjectKind category,
        CancellationToken cancellationToken);
}
