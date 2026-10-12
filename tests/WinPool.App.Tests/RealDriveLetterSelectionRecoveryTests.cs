using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealDriveLetterSelectionRecoveryTests
{
    [Theory]
    [InlineData(RealOperationState.Cancelled)]
    [InlineData(RealOperationState.Rejected)]
    public void ConfirmedNoWriteRestoresFactSelectionForTheSameTarget(RealOperationState state)
    {
        var (target, response) = Response(state, [Step(RealOperationStepState.Pending)]);

        Assert.True(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            response, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void PreAcceptanceFailureWithNoBarrierCanRestoreFactSelection()
    {
        var target = Target(SystemId.New());

        Assert.True(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            response: null, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void UnchangedPriorTerminalObservationDoesNotBlockPreAcceptanceRestore()
    {
        var (target, priorTerminal) = Response(RealOperationState.Succeeded, [Step(RealOperationStepState.Verified)]);

        Assert.True(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            priorTerminal, submissionSucceeded: false, inventoryRefreshSucceeded: true,
            submissionBlocked: false, filling: false, requested: target, current: target,
            previousResponse: priorTerminal));
    }

    [Fact]
    public void UnknownOrWrittenResultNeverRestoresFactSelection()
    {
        var (target, unknown) = Response(RealOperationState.OutcomeUnknown, [], requiresReconciliation: true);
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            unknown, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: true, filling: false, requested: target, current: target));

        var (_, succeeded) = Response(RealOperationState.Succeeded, [Step(RealOperationStepState.Verified)]);
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            succeeded, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));

        var (_, cancelledAfterWrite) = Response(RealOperationState.Cancelled, [Step(RealOperationStepState.Verified)]);
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancelledAfterWrite, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));

        var (_, cancellationNeedsReview) = Response(RealOperationState.Cancelled, [], requiresReconciliation: true);
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancellationNeedsReview, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void RefreshCompletionAndChangedUiTargetDoNotApplyAnOldSelection()
    {
        var (target, cancelled) = Response(RealOperationState.Cancelled, [Step(RealOperationStepState.Pending)]);
        var otherPartition = target with { PartitionStableId = "partition:other" };
        var otherVolume = target with { VolumeStableId = "volume:other" };

        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancelled, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: otherPartition));
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancelled, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: otherVolume));
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancelled, submissionSucceeded: false, inventoryRefreshSucceeded: true,
            submissionBlocked: false, filling: false, requested: target, current: target));
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            cancelled, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: true, requested: target, current: target));
    }

    [Fact]
    public void TerminalResponseMustMatchTheExactPartitionIntent()
    {
        var (responseTarget, otherPartitionResponse) = Response(RealOperationState.Cancelled,
            [Step(RealOperationStepState.Pending)],
            partitionStableId: "partition:other");
        var target = responseTarget with { PartitionStableId = "partition:target" };

        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            otherPartitionResponse, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void TerminalResponseMustMatchTheExactPhysicalAnchor()
    {
        var (responseTarget, wrongAnchorResponse) = Response(RealOperationState.Cancelled,
            [Step(RealOperationStepState.Pending)], physicalDiskStableId: "physical:other");
        var target = responseTarget with { PhysicalDiskStableId = "physical:target" };

        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            wrongAnchorResponse, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void NoWriteRestoreRequiresCompleteSingleStepEvidence()
    {
        var (target, cancelled) = Response(RealOperationState.Cancelled, [Step(RealOperationStepState.Pending)]);
        var missingProgress = cancelled with { Steps = [] };
        var wrongStep = cancelled with { Steps = [new("different-step", RealOperationStepState.Pending, null, null, null)] };
        var extraTarget = cancelled with
        {
            Plan = cancelled.Plan with
            {
                Targets = cancelled.Plan.Targets.Append(new StorageObjectId(target.SystemId,
                    StorageObjectKind.OsDisk, "osdisk:other")).ToArray()
            }
        };

        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            missingProgress, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            wrongStep, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
        Assert.False(RealDriveLetterSelectionRecovery.ShouldRestoreFactSelection(
            extraTarget, submissionSucceeded: false, inventoryRefreshSucceeded: false,
            submissionBlocked: false, filling: false, requested: target, current: target));
    }

    [Fact]
    public void PhysicalAnchorResolverRequiresExactDirectDiskRelationships()
    {
        var snapshot = SimulationLayouts.PrimordialReady();
        var partition = snapshot.Partitions.First(item => item.OsDiskStableId is not null);
        var osDisk = Assert.Single(snapshot.OsDisks, item => item.StableId == partition.OsDiskStableId);

        Assert.Equal(osDisk.PhysicalDiskStableId,
            RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(snapshot, partition.StableId));

        var ambiguous = snapshot with
        {
            Relationships = snapshot.Relationships.Append(snapshot.Relationships.Single(item =>
                item.RelationshipKind == "ContainsPartition" && item.ToStableId == partition.StableId)).ToArray()
        };
        Assert.Null(RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(ambiguous, partition.StableId));
    }

    [Fact]
    public void PhysicalAnchorResolverSupportsOnlySingleMemberVirtualDiskClosure()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var memberId = pool.MemberPhysicalDiskIds[0];
        snapshot = snapshot with
        {
            StoragePools = snapshot.StoragePools.Select(item => item.StableId == pool.StableId
                ? item with { MemberPhysicalDiskIds = [memberId] } : item).ToArray(),
            StorageTiers = snapshot.StorageTiers.Select(item => item.PoolStableId == pool.StableId
                ? item with { MemberPhysicalDiskIds = [memberId] } : item).ToArray()
        };
        snapshot = StorageRelationshipProjector.Rebuild(snapshot);
        var virtualDisk = Assert.Single(snapshot.VirtualDisks);
        var osDisk = Assert.Single(snapshot.OsDisks,
            item => item.VirtualDiskStableId == virtualDisk.StableId);
        var partition = Assert.Single(snapshot.Partitions,
            item => item.OsDiskStableId == osDisk.StableId && item.Type == "Primary");

        Assert.Equal(memberId,
            RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(snapshot, partition.StableId));

        var ambiguousPool = snapshot with
        {
            StoragePools = snapshot.StoragePools.Select(item => item.StableId == pool.StableId
                ? item with { MemberPhysicalDiskIds = [memberId, "physical:other"] } : item).ToArray()
        };
        ambiguousPool = StorageRelationshipProjector.Rebuild(ambiguousPool);
        Assert.Null(RealDriveLetterSelectionRecovery.ResolvePhysicalDiskStableId(ambiguousPool, partition.StableId));
    }

    private static (RealDriveLetterSelectionTarget Target, AgentRealOperationResponse Response) Response(
        RealOperationState state,
        IReadOnlyList<RealOperationStepProgress> steps,
        bool requiresReconciliation = false,
        string partitionStableId = "partition:target",
        string physicalDiskStableId = "physical:target")
    {
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition, partitionStableId);
        var physicalDisk = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, physicalDiskStableId);
        var proposal = RealOperationProposalFactory.OneStep(system, OperationIntent.SetDriveLetter,
            partition, new SetDriveLetterCommand(RealTargetReference.ForExisting(partition), 'E', 'F'),
            "The selected partition has drive letter F", "No partition data loss expected");
        proposal = proposal with { Targets = [partition, physicalDisk] };
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, now,
            Path.GetFullPath("WinPool.App.Tests.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine,
            "machine-binding", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
            false, now);
        var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), environment, session,
            "inventory", "target-fingerprint", "physical-fingerprint", "support-evidence",
            now, now.AddMinutes(2));
        var progress = steps.Select((step, index) => index < plan.Steps.Count
            ? step with { StepId = plan.Steps[index].Id } : step).ToArray();
        return (new(system, partitionStableId, "volume:target", physicalDiskStableId),
            new AgentRealOperationResponse(plan, state, progress, null, requiresReconciliation));
    }

    private static RealDriveLetterSelectionTarget Target(SystemId system) =>
        new(system, "partition:target", "volume:target", "physical:target");

    private static RealOperationStepProgress Step(RealOperationStepState state) =>
        new("operation-1", state, null, null, null);
}
