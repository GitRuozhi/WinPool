using WinPool.Domain;

namespace WinPool.Application;

public enum SimulationEditKind
{
    Rename,
    ChangeDriveLetter,
    FormatPartition,
    DeletePartition,
    ConvertDisk,
    SetDiskOffline,
    OptimizePool,
    InitializeDisk,
    CreatePartition,
    ExtendPartition,
    ShrinkPartition,
    CreateStoragePool,
    CreateVirtualDisk,
    MovePhysicalDisk,
    OptimizeDrive,
    ResetDocument,
    CreateTieredPool,
    UpdateStoragePool,
    DissolveStoragePool
}

/// <summary>
/// Stable internal command contract for simulated storage edits. The target key is
/// resolved to a structured <see cref="StorageObjectId"/> against the current
/// simulation snapshot before a plan can be produced.
/// </summary>
public sealed record SimulationEditRequest(
    SimulationEditKind Kind,
    string TargetProviderKey,
    string? Name = null,
    string? DriveLetter = null,
    string? FileSystem = null,
    long? AllocationUnitSize = null,
    bool? Offline = null,
    long? SizeBytes = null,
    bool? CreateMsr = null,
    long? InterleaveBytes = null,
    string? Resiliency = null,
    IReadOnlyList<string>? MemberDiskIds = null,
    string? VirtualDiskName = null,
    string? PerformanceResiliency = null,
    long? PerformanceInterleaveBytes = null,
    long? PerformanceSizeBytes = null,
    int? PerformanceDataCopies = null,
    string? CapacityResiliency = null,
    long? CapacityInterleaveBytes = null,
    long? CapacitySizeBytes = null,
    int? CapacityColumns = null,
    int? CapacityToleratedFailures = null,
    string? ScmResiliency = null,
    long? ScmInterleaveBytes = null,
    int? ScmDataCopies = null,
    long? OffsetBytes = null);

public sealed record SimulationEditReceipt(
    OperationId OperationId,
    string PlanHash,
    SystemId SystemId,
    StorageObjectId Target,
    long BeforeRevision,
    long AfterRevision,
    IReadOnlyList<string> SimulatedCommands);

public interface ISimulationEditCoordinator
{
    Task<ApplicationResult<SimulationEditReceipt>> ExecuteAsync(
        SimulationEditRequest request,
        CancellationToken cancellationToken);
}
