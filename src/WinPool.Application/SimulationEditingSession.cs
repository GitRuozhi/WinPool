using WinPool.Domain;
namespace WinPool.Application;
public sealed record PoolEditIntent(
        bool AutoCreateVirtualDisk,
        bool AutoCreatePartition,
        string FileSystem,
        long AllocationUnitSize,
        string VolumeName);

public sealed record EditorDraftState(
        StorageSnapshot Snapshot,
        IReadOnlyDictionary<string, PoolEditIntent> PoolIntents,
        IReadOnlySet<string> MaximumSizeFields);


/// <summary>Owns the baseline, draft intents, history and submission state for one editing session.</summary>
public sealed class SimulationEditingSession
{
    public StorageSnapshot Baseline { get; private set; } = StorageSnapshot.Empty("editor");
    public StorageSnapshot Working { get; set; } = StorageSnapshot.Empty("editor");
    public SystemId SystemId { get; private set; }
    public long BaselineRevision { get; private set; }
    public Stack<EditorDraftState> UndoStack { get; } = [];
    public Stack<EditorDraftState> RedoStack { get; } = [];
    public HashSet<string> MaximumSizeFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PoolEditIntent> PoolIntents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SimulationDraftPlan? CurrentPlan { get; set; }
    public string PlanBuildError { get; set; } = string.Empty;
    public bool OutcomeUnknown { get; set; }
    public bool RenameInProgress { get; set; }

    public void Bind(StorageSystemDocument document, StorageSnapshot working)
    {
        SystemId = document.SystemId;
        BaselineRevision = document.Revision;
        Baseline = document.Snapshot;
        Working = working;
        UndoStack.Clear(); RedoStack.Clear(); MaximumSizeFields.Clear(); PoolIntents.Clear();
        CurrentPlan = null; PlanBuildError = string.Empty; OutcomeUnknown = false; RenameInProgress = false;
    }

    public EditorDraftState Capture() => new(Working,
        new Dictionary<string, PoolEditIntent>(PoolIntents, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(MaximumSizeFields, StringComparer.OrdinalIgnoreCase));

    public void Restore(EditorDraftState state)
    {
        Working = state.Snapshot;
        PoolIntents.Clear();
        foreach (var pair in state.PoolIntents) PoolIntents[pair.Key] = pair.Value;
        MaximumSizeFields.Clear(); MaximumSizeFields.UnionWith(state.MaximumSizeFields);
        CurrentPlan = null;
    }

    public bool Undo()
    {
        if (UndoStack.Count == 0) return false;
        RedoStack.Push(Capture()); Restore(UndoStack.Pop()); return true;
    }
    public bool Redo()
    {
        if (RedoStack.Count == 0) return false;
        UndoStack.Push(Capture()); Restore(RedoStack.Pop()); return true;
    }
    public void AcceptRename(StorageSystemDocument committed, string targetId)
    {
        if (committed.SystemId != SystemId) throw new InvalidOperationException("Rename belongs to another system.");
        Working = SynchronizeCommittedName(Working, committed.Snapshot, targetId);
        SynchronizeHistoryNames(UndoStack, committed.Snapshot, targetId);
        SynchronizeHistoryNames(RedoStack, committed.Snapshot, targetId);
        Baseline = committed.Snapshot; BaselineRevision = committed.Revision; CurrentPlan = null;
    }
    public void Discard()
    {
        Working = Baseline; UndoStack.Clear(); RedoStack.Clear(); MaximumSizeFields.Clear(); PoolIntents.Clear();
        CurrentPlan = null; PlanBuildError = string.Empty; OutcomeUnknown = false;
    }
    public SimulationDraftPlan Prepare(SimulationDraftPlan plan) => plan with
    {
        BaselineSystemId = SystemId,
        BaselineRevision = BaselineRevision,
        BaselineInventoryVersion = Baseline.SnapshotVersion
    };
    internal static void SynchronizeHistoryNames(
        Stack<EditorDraftState> stack,
        StorageSnapshot committed,
        string targetId)
    {
        var states = stack.ToArray();
        stack.Clear();
        for (var index = states.Length - 1; index >= 0; index--)
        {
            stack.Push(states[index] with
            {
                Snapshot = SynchronizeCommittedName(states[index].Snapshot, committed, targetId)
            });
        }
    }

    internal static StorageSnapshot SynchronizeCommittedName(
        StorageSnapshot snapshot,
        StorageSnapshot committed,
        string targetId)
    {
        var pool = committed.StoragePools.FirstOrDefault(item => item.StableId == targetId);
        var tier = committed.StorageTiers.FirstOrDefault(item => item.StableId == targetId);
        var physical = committed.PhysicalDisks.FirstOrDefault(item => item.StableId == targetId);
        var vdisk = committed.VirtualDisks.FirstOrDefault(item => item.StableId == targetId);
        var osDisk = committed.OsDisks.FirstOrDefault(item => item.StableId == targetId);
        var projectedOsDisks = vdisk is null
            ? osDisk is null ? snapshot.OsDisks : snapshot.OsDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = osDisk.FriendlyName } : item).ToArray()
            : snapshot.OsDisks
                .Select(item => item.VirtualDiskStableId == targetId
                    ? item with { FriendlyName = vdisk.FriendlyName }
                    : item).ToArray();
        var volume = committed.Volumes.FirstOrDefault(item => item.StableId == targetId);
        var partition = committed.Partitions.FirstOrDefault(item => item.StableId == targetId)
            ?? (volume?.PartitionStableId is string partitionId
                ? committed.Partitions.FirstOrDefault(item => item.StableId == partitionId)
                : null);
        return snapshot with
        {
            StoragePools = pool is null ? snapshot.StoragePools : snapshot.StoragePools
                .Select(item => item.StableId == targetId ? item with { FriendlyName = pool.FriendlyName } : item).ToArray(),
            StorageTiers = tier is null ? snapshot.StorageTiers : snapshot.StorageTiers
                .Select(item => item.StableId == targetId ? item with { FriendlyName = tier.FriendlyName } : item).ToArray(),
            PhysicalDisks = physical is null ? snapshot.PhysicalDisks : snapshot.PhysicalDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = physical.FriendlyName } : item).ToArray(),
            VirtualDisks = vdisk is null ? snapshot.VirtualDisks : snapshot.VirtualDisks
                .Select(item => item.StableId == targetId ? item with { FriendlyName = vdisk.FriendlyName } : item).ToArray(),
            OsDisks = projectedOsDisks,
            Partitions = partition is null ? snapshot.Partitions : snapshot.Partitions
                .Select(item => item.StableId == partition.StableId
                    ? item with { FileSystemLabel = partition.FileSystemLabel }
                    : item).ToArray(),
            Volumes = volume is null ? snapshot.Volumes : snapshot.Volumes
                .Select(item => item.StableId == targetId ? item with { FileSystemLabel = volume.FileSystemLabel } : item).ToArray()
        };
    }

}
