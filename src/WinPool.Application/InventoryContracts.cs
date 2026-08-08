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

public enum InventoryCaptureReason
{
    Startup,
    UserRefresh,
    PreExecutionValidation,
    BackgroundRefresh,
    Comparison
}

public sealed record InventoryRequest(
    SystemId SystemId,
    InventoryCaptureReason Reason,
    bool IncludeSensitiveValuesInMemory,
    string? ExpectedInventoryVersion = null);

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

public enum InventoryDifferenceKind
{
    MissingFromCandidate,
    AddedByCandidate,
    PropertyMismatch,
    RelationshipMismatch,
    IdentityMismatch
}

public sealed record InventoryDifference(
    InventoryDifferenceKind Kind,
    StorageObjectId? ReferenceObjectId,
    StorageObjectId? CandidateObjectId,
    string PropertyKey,
    string ReferenceValue,
    string CandidateValue);

public sealed record InventoryComparison(
    string ReferenceVersion,
    string CandidateVersion,
    bool IsEquivalent,
    IReadOnlyList<InventoryDifference> Differences);

public interface IInventoryProvider
{
    InventoryProviderKind Kind { get; }

    Task<ApplicationResult<InventorySnapshot>> CaptureAsync(
        InventoryRequest request,
        CancellationToken cancellationToken);
}

public interface IInventoryComparer
{
    InventoryComparison Compare(
        InventorySnapshot reference,
        InventorySnapshot candidate);
}
