using System.Security.Cryptography;
using System.Text;
using WinPool.Domain;

namespace WinPool.Application;

public enum ManageObjectRole
{
    System,
    StorageSubsystem,
    StoragePool,
    StorageTier,
    PhysicalDisk,
    VirtualDisk,
    NetworkDisk,
    OsDisk,
    Partition,
    Volume,
    NetworkGroup,
    OtherGroup,
    DirectDiskGroup,
    VirtualDiskGroup
}

public enum ManageTopologyLayout
{
    Stack,
    Flow,
    WeightedFlow
}

public enum ManageWorkspaceCategory
{
    System,
    Pool,
    Tier,
    Disk,
    Partition,
    Volume
}

public sealed record ManageObjectListItemView(
    StorageObjectId Id,
    ManageObjectRole Role,
    ManageWorkspaceCategory Category,
    string DisplayName,
    bool IsStableIdentity,
    string? ParentProviderKey,
    int SortOrder,
    IReadOnlyDictionary<string, string?> Metadata);

/// <summary>
/// One object in the Manage topology. Occurrence identity remains separate for
/// stable tree expansion and layout state; one projected system must not emit
/// the same object identity in more than one location.
/// </summary>
public sealed record ManageTopologyNodeView(
    string OccurrenceKey,
    StorageObjectId Id,
    ManageObjectRole Role,
    string DisplayName,
    bool IsStableIdentity,
    string Summary,
    bool IsReference,
    bool IsExpanded,
    bool IsSelectable,
    ManageTopologyLayout ChildrenLayout,
    int LayoutWeight,
    IReadOnlyList<ManageTopologyNodeView> Children);

public sealed record ManageSystemProjection(
    SystemId SystemId,
    string DocumentId,
    string DisplayName,
    StorageSystemSourceKind SourceKind,
    string InventoryVersion,
    DateTimeOffset CapturedAtUtc,
    ManageTopologyNodeView Root,
    IReadOnlyList<ManageObjectListItemView> WorkspaceObjects);

public enum ManageValuePresentation
{
    Plain,
    LocalizationKey,
    PartitionType,
    MaskedSerial,
    ProductName,
    LocalDateTime
}

public sealed record ManagePropertyView(
    string PropertyTextKey,
    string RawValue,
    ManageValuePresentation Presentation = ManageValuePresentation.Plain);

public sealed record ManageObjectComparisonView(
    StorageObjectId ObjectId,
    IReadOnlyList<ManagePropertyView> Properties);

public sealed record ManageObjectDetailsView(
    StorageObjectId ObjectId,
    ManageObjectRole Role,
    string DisplayName,
    IReadOnlyList<ManagePropertyView> Properties);

public sealed record ManageObjectTarget(
    StorageObjectId Id,
    ManageObjectRole Role);

public sealed record ManageSelectionKey(
    StorageObjectId Id,
    ManageObjectRole Role,
    ManageWorkspaceCategory Category);

public sealed record ManageObjectNavigationView(
    StorageObjectId ObjectId,
    IReadOnlyDictionary<ManageWorkspaceCategory, ManageObjectTarget?> RelatedSelections,
    ManageObjectTarget? PrimaryTarget);

public enum ManageCommandKind
{
    RefreshLocal,
    ConvertLocalToSimulation,
    ImportSimulation,
    ExportSimulation,
    DeleteSimulation,
    RenamePool,
    CreatePool,
    EditPool,
    OptimizePoolUsage,
    RenameTier,
    CreateTier,
    EditTier,
    RenameDisk,
    InitializeDisk,
    CreatePartition,
    ConvertDiskStyle,
    OnlineDisk,
    OfflineDisk,
    ShowSystemProperties,
    OpenExplorer,
    ChangeDriveLetter,
    RenamePartition,
    FormatPartition,
    EditPartition,
    DeletePartition,
    OptimizeDrive,
    ExportCategory
}

public sealed record ManageCommandView(
    ManageCommandKind Kind,
    bool IsEnabled);

public sealed record ManageSystemDialogTargetView(
    bool HasResolvedPartition,
    bool HasResolvedDisk,
    string PartitionPath,
    string DriveLetter,
    string PhysicalDeviceInstanceId,
    bool UseDiskManagementFallback,
    int? DiskNumber = null);

public sealed record ManageCommandSurfaceView(
    StorageObjectId ObjectId,
    IReadOnlyList<ManageCommandView> Commands,
    ManageSystemDialogTargetView SystemDialogTarget);

public interface IManageSystemProjector<in TDocument>
{
    ManageSystemProjection Project(TDocument document);
}

public interface IManageComparisonProjector<in TDocument>
{
    ManageObjectComparisonView Project(
        TDocument document,
        StorageObjectId objectId,
        ManageObjectRole role);
}

public interface IManageDetailsProjector<in TDocument>
{
    ManageObjectDetailsView Project(
        TDocument document,
        StorageObjectId objectId,
        ManageObjectRole role,
        string displayName);
}

public interface IManageNavigationProjector<in TDocument>
{
    ManageObjectNavigationView Project(
        TDocument document,
        StorageObjectId objectId,
        ManageObjectRole role);
}

public interface IManageCommandProjector<in TDocument>
{
    ManageCommandSurfaceView Project(
        TDocument activeDocument,
        TDocument localDocument,
        StorageObjectId objectId,
        ManageObjectRole role,
        ManageWorkspaceCategory category);
}

public static class InternalStableIdentity
{
    public static SystemId SystemFromDocumentId(string documentId) =>
        new(CreateGuid("system", documentId));

    public static EnvironmentId EnvironmentFromDocumentId(string documentId) =>
        new(CreateGuid("environment", documentId));

    private static Guid CreateGuid(string scope, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"winpool|{scope}|{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public static class ManageSelectionRules
{
    public static bool SameObject(StorageObjectId left, StorageObjectId right) =>
        left.System == right.System
        && left.Kind == right.Kind
        && left.ProviderKey.Equals(right.ProviderKey, StringComparison.OrdinalIgnoreCase);

    public static bool SameTarget(ManageObjectTarget? left, ManageObjectTarget? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Role == right.Role && SameObject(left.Id, right.Id);

    public static bool SameSelection(ManageSelectionKey? left, ManageSelectionKey? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Role == right.Role
                && left.Category == right.Category
                && SameObject(left.Id, right.Id);

    public static ManageObjectTarget TopologyTargetFor(ManageSelectionKey selection) =>
        selection.Role == ManageObjectRole.Volume
            ? new ManageObjectTarget(selection.Id, ManageObjectRole.Partition)
            : new ManageObjectTarget(selection.Id, selection.Role);

    public static ManageWorkspaceCategory CategoryFor(ManageObjectRole role) =>
        role switch
        {
            ManageObjectRole.System or ManageObjectRole.StorageSubsystem =>
                ManageWorkspaceCategory.System,
            ManageObjectRole.StoragePool or ManageObjectRole.NetworkGroup
                or ManageObjectRole.OtherGroup => ManageWorkspaceCategory.Pool,
            ManageObjectRole.StorageTier or ManageObjectRole.DirectDiskGroup =>
                ManageWorkspaceCategory.Tier,
            ManageObjectRole.PhysicalDisk or ManageObjectRole.VirtualDisk
                or ManageObjectRole.OsDisk or ManageObjectRole.VirtualDiskGroup =>
                ManageWorkspaceCategory.Disk,
            ManageObjectRole.Partition or ManageObjectRole.NetworkDisk =>
                ManageWorkspaceCategory.Partition,
            ManageObjectRole.Volume => ManageWorkspaceCategory.Volume,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
}
