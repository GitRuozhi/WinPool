using WinPool.Domain;

namespace WinPool.Application;

/// <summary>
/// Fresh read-only evidence that the selected physical member's current
/// subsystem can support the requested pool/virtual-disk layout. This carries
/// no capacity authorization; creation sizes are queried after the new pool or
/// tier template exists.
/// </summary>
public sealed record RealStructureCreationSupport(
    StorageObjectId PhysicalTarget,
    bool Tiered,
    string TargetFingerprint,
    DateTimeOffset CapturedAtUtc,
    string PhysicalMemberFingerprint,
    string SubsystemStableId,
    string SubsystemUniqueId,
    string SubsystemObjectId,
    string? CurrentPoolStableId,
    string? CurrentPoolUniqueId,
    string? CurrentPoolObjectId,
    bool OrdinaryProviderMethodVerified,
    bool SupportsStoragePoolCreation,
    bool? SupportsStorageTierCreation,
    bool? SupportsStorageTieredVirtualDiskCreation,
    ulong? PhysicalDisksPerStoragePoolMin,
    string ProviderEvidence);
