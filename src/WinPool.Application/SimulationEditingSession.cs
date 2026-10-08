using WinPool.Domain;
namespace WinPool.Application;
public enum PoolVirtualDiskLayout { Ordinary, HddTiered }

public sealed record PoolEditIntent(
        bool AutoCreateVirtualDisk,
        bool AutoCreatePartition,
        string FileSystem,
        long AllocationUnitSize,
        string VolumeName,
        PoolVirtualDiskLayout Layout = PoolVirtualDiskLayout.Ordinary,
        string? VirtualDiskName = null,
        long? VirtualDiskSizeBytes = null,
        bool VirtualDiskUseMaximum = true,
        bool CreateMsr = true,
        char? DriveLetter = null,
        string PartitionStyle = "GPT",
        bool QuickFormat = true,
        string? VerifiedVirtualDiskId = null,
        bool PendingAutomaticLayout = false);

public sealed record EditorDraftState(
        StorageSnapshot Snapshot,
        IReadOnlyDictionary<string, PoolEditIntent> PoolIntents,
        IReadOnlySet<string> MaximumSizeFields);


/// <summary>Owns the baseline, draft intents, history and submission state for one editing session.</summary>
public sealed class SimulationEditingSession
{
    public StorageSnapshot Baseline { get; private set; } = StorageSnapshot.Empty("editor");
    public WinPoolFacts? BaselineSourceFacts { get; private set; }
    public StorageSnapshot Working { get; set; } = StorageSnapshot.Empty("editor");
    public SystemId SystemId { get; private set; }
    public long BaselineRevision { get; private set; }
    public long BindingGeneration { get; private set; }
    public Stack<EditorDraftState> UndoStack { get; } = [];
    public Stack<EditorDraftState> RedoStack { get; } = [];
    public HashSet<string> MaximumSizeFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PoolEditIntent> PoolIntents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SimulationDraftPlan? CurrentPlan { get; set; }
    public string PlanBuildError { get; set; } = string.Empty;
    public bool OutcomeUnknown { get; set; }
    public bool RenameInProgress { get; set; }
    public bool HasBaselineConflict { get; private set; }
    public string ConflictReason { get; private set; } = string.Empty;
    public string RealApplyMessage { get; set; } = string.Empty;

    /// <summary>Real drag expresses a replacement relationship, never a member-move operation.</summary>
    public bool CanAssignRealDraftMember(string diskId, string targetPoolId)
    {
        var disk = Working.PhysicalDisks.FirstOrDefault(item => item.StableId == diskId);
        var target = Working.StoragePools.FirstOrDefault(item => item.StableId == targetPoolId);
        if (disk is null || target is null || !disk.IsStable || disk.IsBoot || disk.IsSystem
            || disk.IsPageFile || disk.IsCrashDump || disk.IsRetired || disk.IsHotSpare) return false;
        var origin = Baseline.PhysicalDisks.FirstOrDefault(item => item.StableId == diskId);
        if (origin is null || !origin.IsStable || origin.IsBoot || origin.IsSystem
            || origin.IsPageFile || origin.IsCrashDump || origin.IsRetired || origin.IsHotSpare
            || PhysicalDiskUsage.Normalize(origin.Usage) is PhysicalDiskUsage.ManualSelect or PhysicalDiskUsage.Journal
            || PhysicalDiskUsage.IsUnknown(origin.Usage)) return false;
        var owners = Baseline.StoragePools.Where(item => !item.IsPrimordial
            && (item.StableId == origin.PoolStableId
                || item.MemberPhysicalDiskIds.Contains(diskId, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (owners.Length > 1) return false;
        var originPool = owners.SingleOrDefault();
        var explicitlyDissolved = originPool is { IsPrimordial: false }
            && !Working.StoragePools.Any(item => item.StableId == originPool.StableId);
        if (originPool is { IsPrimordial: false } && !explicitlyDissolved) return false;
        if (target.IsPrimordial)
            return disk.PoolStableId is not null && EditWorkspace.IsDraftPool(disk.PoolStableId);
        return EditWorkspace.IsDraftPool(target.StableId)
            && target.MemberPhysicalDiskIds.All(id => id == diskId);
    }


    /// <summary>A real creation placeholder carries no simulated/provider capacity claim.</summary>
    public static StorageSnapshot InsertRealDraftVirtualDisk(StorageSnapshot snapshot, string poolId, string name)
    {
        var pool = snapshot.StoragePools.SingleOrDefault(item => item.StableId == poolId);
        if (pool is null || pool.IsPrimordial || snapshot.VirtualDisks.Any(item => item.PoolStableId == poolId))
            throw new InvalidOperationException("The target requires an empty non-primordial pool.");
        var id = $"{EditWorkspace.DraftVirtualDiskPrefix}{Guid.NewGuid():N}";
        var disk = new VirtualDiskInfo(id, false, string.IsNullOrWhiteSpace(name) ? pool.FriendlyName : name.Trim(),
            "", "", "Simple", "Fixed", 1, 65536, 0, 0, poolId, [], [], CapacitySourceKind.SimulatedEstimate);
        return snapshot with
        {
            VirtualDisks = snapshot.VirtualDisks.Append(disk).ToArray(),
            FieldIssues = snapshot.FieldIssues.Concat([
                new StorageFieldIssue(id, nameof(VirtualDiskInfo.Size), FieldReadState.NotCollected,
                    "The requested size or MAX is frozen from the exact provider creation range during Apply.")]).ToArray()
        };
    }

    /// <summary>Remove the exact VD and its child relations from the local real draft.</summary>
    public static StorageSnapshot RemoveRealVirtualDiskDraft(StorageSnapshot snapshot, string virtualDiskId)
    {
        var next = EditWorkspace.DeleteVirtualDiskFromWorking(snapshot, virtualDiskId);
        return RemoveVirtualDiskChildren(next, snapshot, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { virtualDiskId });
    }

    /// <summary>Stage an explicit dissolution or discard, including its local descendants.</summary>
    public static StorageSnapshot RemoveRealPoolDraft(StorageSnapshot snapshot, string poolId)
    {
        var virtualDiskIds = snapshot.VirtualDisks.Where(vd => vd.PoolStableId == poolId)
            .Select(vd => vd.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = EditWorkspace.IsDraftPool(poolId)
            ? EditWorkspace.DiscardDraftPool(snapshot, poolId)
            : EditWorkspace.DissolvePoolInWorking(snapshot, poolId);
        return RemoveVirtualDiskChildren(next, snapshot, virtualDiskIds);
    }

    private static StorageSnapshot RemoveVirtualDiskChildren(StorageSnapshot next, StorageSnapshot source,
        IReadOnlySet<string> virtualDiskIds)
    {
        var diskIds = source.OsDisks.Where(disk => virtualDiskIds.Contains(disk.VirtualDiskStableId ?? ""))
            .Select(disk => disk.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partitionIds = source.Partitions.Where(partition => diskIds.Contains(partition.OsDiskStableId ?? ""))
            .Select(partition => partition.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedIds = virtualDiskIds.Concat(diskIds).Concat(partitionIds)
            .Concat(source.Volumes.Where(volume => partitionIds.Contains(volume.PartitionStableId ?? ""))
                .Select(volume => volume.StableId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return next with
        {
            VirtualDisks = next.VirtualDisks.Where(vd => !virtualDiskIds.Contains(vd.StableId)).ToArray(),
            StorageTiers = next.StorageTiers.Where(tier => !virtualDiskIds.Contains(tier.VirtualDiskStableId ?? "")).ToArray(),
            OsDisks = next.OsDisks.Where(disk => !diskIds.Contains(disk.StableId)).ToArray(),
            Partitions = next.Partitions.Where(partition => !partitionIds.Contains(partition.StableId)).ToArray(),
            Volumes = next.Volumes.Where(volume => !partitionIds.Contains(volume.PartitionStableId ?? "")).ToArray(),
            Relationships = next.Relationships.Where(relation => !removedIds.Contains(relation.FromStableId)
                && !removedIds.Contains(relation.ToStableId)).ToArray(),
            FieldIssues = next.FieldIssues.Where(issue => !removedIds.Contains(issue.ObjectId)).ToArray()
        };
    }

    public void MarkBaselineConflict(StorageSystemDocument document)
    {
        if (document.SystemId == SystemId && document.Revision == BaselineRevision
            && document.Snapshot.SnapshotVersion == Baseline.SnapshotVersion) return;
        HasBaselineConflict = true;
        ConflictReason = "The live storage baseline changed. The draft is preserved; discard it to edit the new facts.";
        CurrentPlan = null;
    }

    /// <summary>Advance only after the caller has verified an expected operation result.</summary>
    public void RebaseVerified(StorageSystemDocument document, EditorDraftState remainingDraft)
    {
        if (document.SystemId != SystemId)
            throw new InvalidOperationException("Verified result belongs to another system.");
        Baseline = document.Snapshot;
        BaselineSourceFacts = document.SourceFacts;
        BaselineRevision = document.Revision;
        Restore(remainingDraft);
        // Completed writes cannot enter the structural undo history.
        UndoStack.Clear(); RedoStack.Clear();
        HasBaselineConflict = false; ConflictReason = string.Empty;
        PlanBuildError = string.Empty;
    }

    public void Bind(StorageSystemDocument document, StorageSnapshot working)
    {
        BindingGeneration = checked(BindingGeneration + 1);
        SystemId = document.SystemId;
        BaselineRevision = document.Revision;
        Baseline = document.Snapshot;
        BaselineSourceFacts = document.SourceFacts;
        Working = working;
        UndoStack.Clear(); RedoStack.Clear(); MaximumSizeFields.Clear(); PoolIntents.Clear();
        CurrentPlan = null; PlanBuildError = string.Empty; OutcomeUnknown = false; RenameInProgress = false;
        HasBaselineConflict = false; ConflictReason = string.Empty; RealApplyMessage = string.Empty;
    }

    /// <summary>Preview the same draft against the exact observed baseline without reseeding or reprojecting source facts.</summary>
    public SimulationEditingSession ForkPreview(EditorDraftState draft)
    {
        var preview = new SimulationEditingSession
        {
            Baseline = Baseline, BaselineSourceFacts = BaselineSourceFacts,
            SystemId = SystemId, BaselineRevision = BaselineRevision,
            BindingGeneration = BindingGeneration, OutcomeUnknown = OutcomeUnknown,
            HasBaselineConflict = HasBaselineConflict, ConflictReason = ConflictReason,
            RealApplyMessage = RealApplyMessage
        };
        preview.Restore(draft);
        return preview;
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
        Baseline = committed.Snapshot; BaselineSourceFacts = committed.SourceFacts;
        BaselineRevision = committed.Revision; CurrentPlan = null;
    }
    public void Discard()
    {
        BindingGeneration = checked(BindingGeneration + 1);
        Working = Baseline; UndoStack.Clear(); RedoStack.Clear(); MaximumSizeFields.Clear(); PoolIntents.Clear();
        CurrentPlan = null; PlanBuildError = string.Empty; OutcomeUnknown = false;
        HasBaselineConflict = false; ConflictReason = string.Empty; RealApplyMessage = string.Empty;
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
