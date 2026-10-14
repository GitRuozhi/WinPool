using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Re-enters a GUI-owned structure draft without restoring any persisted write authorization.</summary>
public static class StructureDraftReentry
{
    public static SimulationEditingSession Enter(SimulationEditingSession? previous,
        StorageSystemDocument document, StorageSnapshot freshWorking, bool hasUnappliedChanges,
        out bool rebound, long? capturedContentRevision = null, long? capturedBindingGeneration = null)
    {
        if (previous is null || previous.SystemId != document.SystemId)
        {
            var created = new SimulationEditingSession();
            created.Bind(document, freshWorking);
            rebound = true;
            return created;
        }
        rebound = false;
        var captureIsCurrent = (capturedContentRevision is null || capturedContentRevision == previous.ContentRevision)
            && (capturedBindingGeneration is null || capturedBindingGeneration == previous.BindingGeneration);
        // A confirmation or verified apply can finish after the page is hidden.
        // Once that mutates/rebinds the session, its saved dirty flag describes
        // an obsolete draft. Determine remaining changes from the live session.
        var hasCurrentChanges = captureIsCurrent ? hasUnappliedChanges : HasDraftContent(previous);
        if (hasCurrentChanges || previous.RealResultSynchronizationPending || previous.OutcomeUnknown)
            previous.MarkBaselineConflict(document);
        else if (previous.BaselineRevision != document.Revision
            || previous.Baseline.SnapshotVersion != document.Snapshot.SnapshotVersion)
        {
            previous.Bind(document, freshWorking);
            rebound = true;
        }
        return previous;
    }

    public static bool HasDraftContent(SimulationEditingSession session)
    {
        var working = session.Working;
        var baseline = session.Baseline;
        if (EditWorkspace.HasStructuralChanges(working, baseline)
            || EditWorkspace.HasAnyPoolPropertyChanges(working, baseline)
            || session.PoolIntents.Values.Any(intent => intent.PendingAutomaticLayout)
            || baseline.StorageTiers.Any(tier => !working.StorageTiers.Any(item => item.StableId == tier.StableId)))
            return true;
        foreach (var pair in session.PoolIntents)
        {
            if (!working.StoragePools.Any(pool => pool.StableId == pair.Key)
                || PrimaryPartition(working, pair.Key) is not { } finalPartition
                || PrimaryPartition(baseline, pair.Key) is not { } committedPartition
                || finalPartition.StableId != committedPartition.StableId)
                continue;
            if (!string.Equals(pair.Value.FileSystem, committedPartition.FileSystem, StringComparison.OrdinalIgnoreCase)
                || pair.Value.AllocationUnitSize != committedPartition.AllocationUnitSize)
                return true;
        }
        return false;
    }

    private static PartitionInfo? PrimaryPartition(StorageSnapshot snapshot, string poolId)
    {
        var vdisk = snapshot.VirtualDisks.FirstOrDefault(item =>
            string.Equals(item.PoolStableId, poolId, StringComparison.OrdinalIgnoreCase));
        if (vdisk is null) return null;
        var disk = snapshot.OsDisks.FirstOrDefault(item => item.VirtualDiskStableId == vdisk.StableId);
        return snapshot.Partitions.Where(item => item.OsDiskStableId == disk?.StableId
                && item.Type is "Primary" or "BasicData")
            .OrderBy(item => item.Offset).FirstOrDefault();
    }
}
