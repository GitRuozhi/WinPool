using WinPool.Domain;

namespace WinPool.Application;

public sealed class ExecutionModeController
{
    public ExecutionModeController(PrivilegeState privilegeState) => PrivilegeState = privilegeState;

    public PrivilegeState PrivilegeState { get; }

    public ExecutionMode Mode { get; private set; } = ExecutionMode.Simulation;

    public bool CanUseRealMode => PrivilegeState == PrivilegeState.Administrator;

    public bool TrySetMode(ExecutionMode mode)
    {
        if (mode == ExecutionMode.Real && !CanUseRealMode)
        {
            Mode = ExecutionMode.Simulation;
            return false;
        }

        Mode = mode;
        return true;
    }
}

public static class WorkspaceSelectionState
{
    public static WorkspaceSelection Restore(StorageSnapshot snapshot, WorkspaceSelection previous)
    {
        if (previous.StableId is not null && snapshot.FindUnit(previous.StableId) is not null)
        {
            return previous;
        }

        var fallback = previous.Category switch
        {
            WorkspaceCategory.System => snapshot.FindUnit(snapshot.Computer.StableId),
            WorkspaceCategory.Pool => snapshot.StoragePools.Select(x => snapshot.FindUnit(x.StableId)).FirstOrDefault(x => x is not null),
            WorkspaceCategory.Tier => snapshot.StorageTiers.Select(x => snapshot.FindUnit(x.StableId)).FirstOrDefault(x => x is not null),
            WorkspaceCategory.Disk =>
                snapshot.PhysicalDisks.Select(x => snapshot.FindUnit(x.StableId))
                    .Concat(snapshot.VirtualDisks.Select(x => snapshot.FindUnit(x.StableId)))
                    .Concat(snapshot.NetworkDisks.Select(x => snapshot.FindUnit(x.StableId)))
                    .FirstOrDefault(x => x is not null),
            WorkspaceCategory.Partition => snapshot.Partitions.Select(x => snapshot.FindUnit(x.StableId)).FirstOrDefault(x => x is not null),
            _ => null
        };

        return fallback is null
            ? new WorkspaceSelection(previous.Category, null)
            : WorkspaceMapper.FromUnit(fallback, snapshot);
    }
}
