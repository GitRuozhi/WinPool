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
