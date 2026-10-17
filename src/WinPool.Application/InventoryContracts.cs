using WinPool.Domain;

namespace WinPool.Application;

public enum InventoryProviderKind
{
    EmbeddedReadOnlyPowerShell,
    NativeWindows,
    KsReference,
    Simulation,
    Import,
    Replay
}

public sealed record InventoryIdentityDiagnostic(
    StorageObjectId ObjectId,
    IdentityStability Stability,
    string Code,
    string DiagnosticText);

public sealed record InventorySnapshot(
    SystemId SystemId,
    InventoryProviderKind ProviderKind,
    string InventoryVersion,
    string MachineBinding,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<StorageObjectView> Objects,
    IReadOnlyList<InventoryIdentityDiagnostic> IdentityDiagnostics,
    IReadOnlyList<StorageRelationshipView>? Relationships = null);

public sealed record StorageRelationshipView(
    StorageObjectId FromObjectId,
    StorageObjectId ToObjectId,
    string RelationshipKind);
