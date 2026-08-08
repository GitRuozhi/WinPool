using System.Collections.ObjectModel;
using WinPool.Domain;

namespace WinPool.Execution;

[Flags]
public enum ExecutionCapability
{
    None = 0,
    ReadInventory = 1 << 0,
    ReadPerformanceCounters = 1 << 1,
    OpenNativeProperties = 1 << 2,
    SimulateStorageMutation = 1 << 3,
    ReadFileTest = 1 << 4,
    WriteFileTest = 1 << 5,
    RunExternalTestTool = 1 << 6,
    CleanTemporaryFiles = 1 << 7,
    FlushVolume = 1 << 8,
    TrimOrOptimizeVolume = 1 << 9,
    AdjustProcessScheduling = 1 << 10,
    UseTemporaryPowerPlan = 1 << 11,
    InstallExternalTool = 1 << 12,
    ClearSystemFileCache = 1 << 13,
    MutateStorageStructure = 1 << 14,
    ReplayEvidence = 1 << 15
}

public enum RiskLevel
{
    R0ReadOnly,
    R1SimulationWrite,
    R2RecoverableFileWrite,
    R3ControlledSystemSupport,
    R4StorageStructureMutation,
    R5IrreversibleOrBroadDestruction
}

public enum OperationIntent
{
    ReadInventory,
    ReadPerformanceCounters,
    OpenNativeProperties,
    ReplayHistoricalEvents,
    SimulateStorageMutation,
    RunFileTest,
    CleanRegisteredTestFiles,
    CleanTemporaryFiles,
    FlushVolume,
    TrimOrOptimizeVolume,
    AdjustProcessScheduling,
    UseTemporaryPowerPlan,
    InstallExternalTool,
    ClearSystemFileCache,
    InitializeDisk,
    ConvertDisk,
    SetDiskOnlineState,
    CreatePartition,
    DeletePartition,
    ResizePartition,
    FormatVolume,
    CreateStoragePool,
    DeleteStoragePool,
    CreateStorageTier,
    DeleteStorageTier,
    ResizeStorageTier,
    CreateVirtualDisk,
    DeleteVirtualDisk,
    ResizeVirtualDisk,
    RepairStorageObject,
    ClearDisk,
    RawDeviceWrite
}

public sealed record EnvironmentProfile(
    EnvironmentId Id,
    EnvironmentKind Kind,
    string MachineBinding,
    ExecutionCapability AllowedCapabilities,
    bool IsUserProvidedDisposableEnvironment,
    DateTimeOffset CreatedAt);

public sealed record OperationRequest(
    OperationId Id,
    EnvironmentId EnvironmentId,
    SystemId SystemId,
    OperationIntent Intent,
    IReadOnlyList<StorageObjectId> Targets,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset RequestedAt);

public sealed record PlanStep(
    string Id,
    string Action,
    IReadOnlyList<string> DependsOn,
    bool IsCancellationBoundary = false);

public sealed record OperationPlan(
    OperationId OperationId,
    EnvironmentId EnvironmentId,
    SystemId SystemId,
    OperationIntent Intent,
    IReadOnlyList<StorageObjectId> Targets,
    IReadOnlyDictionary<string, string> Parameters,
    ExecutionCapability RequiredCapabilities,
    RiskLevel Risk,
    string InventoryVersion,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<PlanStep> Steps,
    long? EstimatedWriteBytes,
    string ImpactScope,
    string RollbackDescription,
    string IrreversibleEffects,
    AlgorithmIdentity PlannerAlgorithm,
    DateTimeOffset CreatedAt,
    string PlanHash)
{
    public static OperationPlan Create(
        OperationRequest request,
        ExecutionCapability requiredCapabilities,
        RiskLevel risk,
        string inventoryVersion,
        IEnumerable<string> preconditions,
        IEnumerable<PlanStep> steps,
        long? estimatedWriteBytes,
        string impactScope,
        string rollbackDescription,
        string irreversibleEffects,
        AlgorithmIdentity plannerAlgorithm,
        DateTimeOffset createdAt)
    {
        var plan = new OperationPlan(
            request.Id,
            request.EnvironmentId,
            request.SystemId,
            request.Intent,
            request.Targets.ToArray(),
            new ReadOnlyDictionary<string, string>(
                new SortedDictionary<string, string>(
                    request.Parameters.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal)),
            requiredCapabilities,
            risk,
            inventoryVersion,
            preconditions.ToArray(),
            steps.ToArray(),
            estimatedWriteBytes,
            impactScope,
            rollbackDescription,
            irreversibleEffects,
            plannerAlgorithm,
            createdAt,
            string.Empty);

        return plan with { PlanHash = OperationPlanHasher.Compute(plan) };
    }
}

public enum PolicyDecisionKind
{
    Allowed,
    RequiresConfirmation,
    Rejected
}

public sealed record PolicyDecision(
    PolicyDecisionKind Kind,
    string Code,
    string Message)
{
    public static PolicyDecision Allow() => new(PolicyDecisionKind.Allowed, "policy.allowed", "Allowed.");
    public static PolicyDecision Confirm(string code, string message) => new(PolicyDecisionKind.RequiresConfirmation, code, message);
    public static PolicyDecision Reject(string code, string message) => new(PolicyDecisionKind.Rejected, code, message);
}

public sealed record ExecutionContext(
    EnvironmentProfile Environment,
    ExecutionMode Mode,
    PrivilegeState Privilege,
    string CurrentMachineBinding,
    string CurrentInventoryVersion,
    bool IsReleaseBuild);

public enum ExecutionEventKind
{
    Accepted,
    Started,
    Progress,
    Completed,
    Cancelled,
    Rejected,
    Failed
}

public sealed record ExecutionEvent(
    OperationId OperationId,
    ExecutionEventKind Kind,
    DateTimeOffset At,
    string Code,
    string Message,
    OperationId? SourceOperationId = null);
