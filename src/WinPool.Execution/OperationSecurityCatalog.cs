namespace WinPool.Execution;

public sealed record OperationSecurityDefinition(
    ExecutionCapability RequiredCapabilities,
    RiskLevel MinimumRisk,
    string ImpactScope,
    string RollbackDescription,
    string IrreversibleEffects);

public static class OperationSecurityCatalog
{
    private static readonly IReadOnlyDictionary<OperationIntent, OperationSecurityDefinition> Definitions =
        new Dictionary<OperationIntent, OperationSecurityDefinition>
        {
            [OperationIntent.ReadInventory] = Define(ExecutionCapability.ReadInventory, RiskLevel.R0ReadOnly, "Storage inventory", "No rollback is required."),
            [OperationIntent.ReadPerformanceCounters] = Define(ExecutionCapability.ReadPerformanceCounters, RiskLevel.R0ReadOnly, "Performance counters", "No rollback is required."),
            [OperationIntent.OpenNativeProperties] = Define(ExecutionCapability.OpenNativeProperties, RiskLevel.R0ReadOnly, "Native property user interface", "No rollback is required."),
            [OperationIntent.ReplayHistoricalEvents] = Define(ExecutionCapability.ReplayEvidence, RiskLevel.R0ReadOnly, "Previously recorded execution and test events", "No rollback is required."),
            [OperationIntent.SimulateStorageMutation] = Define(ExecutionCapability.SimulateStorageMutation, RiskLevel.R1SimulationWrite, "Simulation document", "Restore the prior simulation revision."),
            [OperationIntent.RunFileTest] = Define(
                ExecutionCapability.ReadFileTest | ExecutionCapability.WriteFileTest | ExecutionCapability.RunExternalTestTool,
                RiskLevel.R2RecoverableFileWrite,
                "Registered files under the selected test-run directory",
                "Stop the tool and clean only files registered by this run."),
            [OperationIntent.CleanRegisteredTestFiles] = Define(
                ExecutionCapability.WriteFileTest,
                RiskLevel.R2RecoverableFileWrite,
                "Files registered by the current test run",
                "Retain remaining files and evidence if cleanup is interrupted."),
            [OperationIntent.CleanTemporaryFiles] = Define(ExecutionCapability.CleanTemporaryFiles, RiskLevel.R3ControlledSystemSupport, "Approved non-protected temporary files", "No automatic content recovery is available."),
            [OperationIntent.FlushVolume] = Define(ExecutionCapability.FlushVolume, RiskLevel.R3ControlledSystemSupport, "Selected volume write cache", "No rollback is required."),
            [OperationIntent.TrimOrOptimizeVolume] = Define(ExecutionCapability.TrimOrOptimizeVolume, RiskLevel.R3ControlledSystemSupport, "Selected volume", "The optimization request cannot be rolled back."),
            [OperationIntent.AdjustProcessScheduling] = Define(ExecutionCapability.AdjustProcessScheduling, RiskLevel.R3ControlledSystemSupport, "Registered test processes", "Restore the recorded priority and CPU affinity."),
            [OperationIntent.UseTemporaryPowerPlan] = Define(ExecutionCapability.UseTemporaryPowerPlan, RiskLevel.R3ControlledSystemSupport, "Current process power policy", "Restore the recorded power plan."),
            [OperationIntent.InstallExternalTool] = Define(ExecutionCapability.InstallExternalTool, RiskLevel.R3ControlledSystemSupport, "One configured external tool", "Use the tool's registered uninstall procedure when available."),
            [OperationIntent.ClearSystemFileCache] = Define(ExecutionCapability.ClearSystemFileCache, RiskLevel.R3ControlledSystemSupport, "System file cache or standby list", "No rollback is required."),

            [OperationIntent.InitializeDisk] = Mutation(RiskLevel.R4StorageStructureMutation, "Physical disk initialization"),
            [OperationIntent.ConvertDisk] = Mutation(RiskLevel.R4StorageStructureMutation, "Physical disk type or state conversion"),
            [OperationIntent.SetDiskOnlineState] = Mutation(RiskLevel.R4StorageStructureMutation, "Physical disk online state"),
            [OperationIntent.CreatePartition] = Mutation(RiskLevel.R4StorageStructureMutation, "Partition table"),
            [OperationIntent.DeletePartition] = Mutation(RiskLevel.R4StorageStructureMutation, "Partition table"),
            [OperationIntent.ResizePartition] = Mutation(RiskLevel.R4StorageStructureMutation, "Partition table"),
            [OperationIntent.FormatVolume] = Mutation(RiskLevel.R4StorageStructureMutation, "Volume file system"),
            [OperationIntent.CreateStoragePool] = Mutation(RiskLevel.R4StorageStructureMutation, "Storage pool topology"),
            [OperationIntent.CreateStorageTier] = Mutation(RiskLevel.R4StorageStructureMutation, "Storage tier topology"),
            [OperationIntent.DeleteStorageTier] = Mutation(RiskLevel.R4StorageStructureMutation, "Storage tier topology"),
            [OperationIntent.ResizeStorageTier] = Mutation(RiskLevel.R4StorageStructureMutation, "Storage tier topology"),
            [OperationIntent.CreateVirtualDisk] = Mutation(RiskLevel.R4StorageStructureMutation, "Virtual disk topology"),
            [OperationIntent.DeleteVirtualDisk] = Mutation(RiskLevel.R4StorageStructureMutation, "Virtual disk topology"),
            [OperationIntent.ResizeVirtualDisk] = Mutation(RiskLevel.R4StorageStructureMutation, "Virtual disk topology"),

            [OperationIntent.DeleteStoragePool] = Mutation(RiskLevel.R5IrreversibleOrBroadDestruction, "Storage pool removal"),
            [OperationIntent.RepairStorageObject] = Mutation(RiskLevel.R5IrreversibleOrBroadDestruction, "Storage object repair"),
            [OperationIntent.ClearDisk] = Mutation(RiskLevel.R5IrreversibleOrBroadDestruction, "Whole disk"),
            [OperationIntent.RawDeviceWrite] = Mutation(RiskLevel.R5IrreversibleOrBroadDestruction, "Raw device")
        };

    public static OperationSecurityDefinition Get(OperationIntent intent)
    {
        if (!Definitions.TryGetValue(intent, out var definition))
        {
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "The operation intent has no security definition.");
        }

        return definition;
    }

    public static bool IsStorageStructureMutation(OperationIntent intent) =>
        Get(intent).MinimumRisk >= RiskLevel.R4StorageStructureMutation;

    public static IReadOnlyList<OperationIntent> StorageStructureMutationIntents { get; } =
        Enum.GetValues<OperationIntent>()
            .Where(IsStorageStructureMutation)
            .ToArray();

    private static OperationSecurityDefinition Define(
        ExecutionCapability capability,
        RiskLevel risk,
        string impact,
        string rollback,
        string irreversible = "None.") =>
        new(capability, risk, impact, rollback, irreversible);

    private static OperationSecurityDefinition Mutation(RiskLevel risk, string impact) =>
        Define(
            ExecutionCapability.MutateStorageStructure,
            risk,
            impact,
            "No WinPool V0.21 rollback implementation exists.",
            "May change or destroy real storage structure or data.");
}
