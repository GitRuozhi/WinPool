using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

public sealed record RealDriveLetterSelectionTarget(
    SystemId SystemId,
    string PartitionStableId,
    string VolumeStableId,
    string? PhysicalDiskStableId);

/// <summary>Restores only the selection for the same target after a confirmed no-write result.</summary>
public static class RealDriveLetterSelectionRecovery
{
    public static bool ShouldRestoreFactSelection(
        AgentRealOperationResponse? response,
        bool submissionSucceeded,
        bool inventoryRefreshSucceeded,
        bool submissionBlocked,
        bool filling,
        RealDriveLetterSelectionTarget requested,
        RealDriveLetterSelectionTarget? current,
        AgentRealOperationResponse? previousResponse = null)
    {
        if (submissionSucceeded || submissionBlocked || filling
            || current is null || !SameTarget(requested, current))
            return false;

        // A proposal rejected before reservation leaves the previous observation
        // untouched. Do not let that stale terminal or refresh state veto the
        // factual UI restore for this selection attempt.
        var isNewObservation = response is not null && !SameOperation(response, previousResponse);
        if (!isNewObservation || response is null)
            return true; // Validation or preparation ended before an operation was accepted.

        if (inventoryRefreshSucceeded)
            return false;

        var partitionTarget = new StorageObjectId(requested.SystemId,
            StorageObjectKind.Partition, requested.PartitionStableId);
        StorageObjectId? physicalTarget = string.IsNullOrWhiteSpace(requested.PhysicalDiskStableId) ? null
            : new StorageObjectId(requested.SystemId, StorageObjectKind.PhysicalDisk,
                requested.PhysicalDiskStableId);
        var partitionTargets = response.Plan.Targets.Where(target => target.Kind == StorageObjectKind.Partition).ToArray();
        var physicalTargets = response.Plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (response.Plan.SystemId != requested.SystemId
            || response.Plan.Intent != OperationIntent.SetDriveLetter
            || physicalTarget is null
            || response.Plan.Targets.Count != 2
            || partitionTargets.Length != 1 || partitionTargets[0] != partitionTarget
            || physicalTargets.Length != 1 || physicalTargets[0] != physicalTarget
            || response.Plan.Targets.Any(target => target.System != requested.SystemId)
            || response.Plan.Steps.Count != 1
            || response.Steps.Count != 1
            || response.Steps[0].StepId != response.Plan.Steps[0].Id)
            return false;
        var frozenSteps = response.Plan.RealOperation?.Steps;
        if (frozenSteps is not { Count: 1 }
            || frozenSteps[0].Id != response.Plan.Steps[0].Id
            || frozenSteps[0].Command is not SetDriveLetterCommand driveLetter
            || driveLetter.Partition != RealTargetReference.ForExisting(partitionTarget))
            return false;

        return !response.RequiresReconciliation
            && (response.State is RealOperationState.Cancelled or RealOperationState.Rejected)
            && response.Steps.All(step => step.State is RealOperationStepState.Pending
                or RealOperationStepState.StoppedBeforeCall);
    }

    private static bool SameOperation(AgentRealOperationResponse current,
        AgentRealOperationResponse? previous) => previous is not null
        && current.Plan.OperationId == previous.Plan.OperationId
        && string.Equals(current.Plan.PlanHash, previous.Plan.PlanHash, StringComparison.Ordinal);

    private static bool SameTarget(RealDriveLetterSelectionTarget expected,
        RealDriveLetterSelectionTarget current) =>
        expected.SystemId == current.SystemId
        && StringComparer.Ordinal.Equals(expected.PartitionStableId, current.PartitionStableId)
        && StringComparer.Ordinal.Equals(expected.VolumeStableId, current.VolumeStableId)
        && StringComparer.Ordinal.Equals(expected.PhysicalDiskStableId, current.PhysicalDiskStableId);

    /// <summary>Resolves only a complete direct-disk or single-member virtual-disk parent chain.</summary>
    public static string? ResolvePhysicalDiskStableId(StorageSnapshot snapshot, string partitionStableId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(partitionStableId)) return null;
        var partitions = snapshot.Partitions.Where(item => item.StableId == partitionStableId).ToArray();
        if (partitions.Length != 1
            || partitions[0] is not { IsStable: true, OsDiskStableId: { } osDiskId }) return null;
        var osDisks = snapshot.OsDisks.Where(item => item.StableId == osDiskId).ToArray();
        if (osDisks.Length != 1 || !HasExactIncoming("ContainsPartition", partitionStableId, osDiskId)) return null;
        var osDisk = osDisks[0];

        string? physicalDiskId;
        if (osDisk.PhysicalDiskStableId is { } directPhysicalId && osDisk.VirtualDiskStableId is null)
        {
            if (!HasExactIncoming("MapsToOsDisk", osDiskId, directPhysicalId)) return null;
            physicalDiskId = directPhysicalId;
        }
        else if (osDisk.PhysicalDiskStableId is null && osDisk.VirtualDiskStableId is { } virtualDiskId)
        {
            var virtualDisks = snapshot.VirtualDisks.Where(item => item.StableId == virtualDiskId && item.IsStable).ToArray();
            if (virtualDisks.Length != 1 || virtualDisks[0].PoolStableId is not { } poolId
                || !HasExactIncoming("MapsToOsDisk", osDiskId, virtualDiskId)) return null;
            var pools = snapshot.StoragePools.Where(item => item.StableId == poolId
                && item.IsStable && !item.IsPrimordial && item.MemberPhysicalDiskIds.Count == 1).ToArray();
            if (pools.Length != 1 || !HasExactIncoming("ContainsVirtualDisk", virtualDiskId, poolId)) return null;
            var members = snapshot.Relationships.Where(item => item.RelationshipKind == "PoolMember"
                && item.FromStableId == poolId).ToArray();
            if (members.Length != 1 || members[0].ToStableId != pools[0].MemberPhysicalDiskIds[0]) return null;
            physicalDiskId = members[0].ToStableId;
        }
        else return null;

        return snapshot.PhysicalDisks.Count(item => item.StableId == physicalDiskId && item.IsStable) == 1
            ? physicalDiskId : null;

        bool HasExactIncoming(string kind, string childId, string parentId)
        {
            var edges = snapshot.Relationships.Where(item => item.RelationshipKind == kind
                && item.ToStableId == childId).ToArray();
            return edges.Length == 1 && edges[0].FromStableId == parentId;
        }
    }
}
