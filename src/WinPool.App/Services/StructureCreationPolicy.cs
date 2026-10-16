using WinPool.Application;

namespace WinPool.App.Services;

public static class StructureCreationPolicy
{
    public static PoolEditIntent NewPool(string name, bool createMsr) =>
        new(true, true, "NTFS", 65536, name, VirtualDiskName: name, CreateMsr: createMsr);

    public static bool WantsVirtualDisk(StorageSnapshot working, string? poolId, PoolEditIntent? previous) =>
        working.VirtualDisks.Any(vd => vd.PoolStableId == poolId)
        || EditWorkspace.IsDraftPool(poolId ?? "") && (previous?.AutoCreateVirtualDisk ?? true);

    public static PoolEditIntent ChooseVirtualDisk(PoolEditIntent intent, bool exists) => intent with
    {
        AutoCreateVirtualDisk = exists,
        AutoCreatePartition = true,
        PendingAutomaticLayout = exists && intent.PendingAutomaticLayout,
        VerifiedVirtualDiskId = exists ? intent.VerifiedVirtualDiskId : null
    };
}
