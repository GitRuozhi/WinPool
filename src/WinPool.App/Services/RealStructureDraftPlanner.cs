using WinPool.Application;
using WinPool.Domain;
using System.Text.Json;

namespace WinPool.App.Services;

public sealed record RealStructureDraftPreview(bool CanApply,
    IReadOnlyList<string> Actions, IReadOnlyList<string> BlockingReasons);

public sealed record RealPoolCreationDraft(StoragePoolInfo Pool, string PhysicalDiskId,
    PoolEditIntent Intent, bool CreatePool, bool CreateVirtualDisk, bool ContinueLayout = false, bool CreateTier = false);

public sealed record RealStructureDraftPlan(RealStructureDraftPreview Preview,
    IReadOnlyList<StoragePoolInfo> RemovedPools,
    IReadOnlyList<VirtualDiskInfo> RemovedVirtualDisks,
    IReadOnlyList<StorageTierInfo> RemovedTiers,
    IReadOnlyList<RealPoolCreationDraft> Creations);

/// <summary>Maps final draft relationships, never simulation commands or temporary IDs, to the supported real workflow.</summary>
public static class RealStructureDraftPlanner
{
    /// <summary>Used only after a successful verified-result refresh, never as permission to infer a write outcome.</summary>
    public static bool IsSatisfiedByVerifiedBaseline(SimulationEditingSession session)
    {
        var remaining = Build(session).Preview;
        return remaining.Actions.Count == 0 && remaining.BlockingReasons.Count == 0;
    }

    public static RealStructureDraftPlan Build(SimulationEditingSession session, bool chinese = false)
    {
        var actions = new List<string>();
        var blocks = new List<string>();
        string T(string zh, string en) => chinese ? zh : en;
        var before = session.Baseline;
        var after = session.Working;
        var removedPools = before.StoragePools.Where(pool => !pool.IsPrimordial
            && after.StoragePools.All(item => item.StableId != pool.StableId)).ToArray();
        var removedVds = before.VirtualDisks.Where(vd => after.VirtualDisks.All(item => item.StableId != vd.StableId)
            && removedPools.All(pool => pool.StableId != vd.PoolStableId)).ToArray();
        var creations = new List<RealPoolCreationDraft>();
        var removedTiers = new List<StorageTierInfo>();
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (DuplicateIdentities(before) || DuplicateIdentities(after))
            return new(new(false, [], [T("草稿包含重复对象身份，不能准备真实操作。", "The draft contains duplicate object identities.")]), [], [], [], []);
        if (before.StoragePools.Any(pool => pool.IsPrimordial
                && !after.StoragePools.Any(item => item.StableId == pool.StableId && item.IsPrimordial))
            || after.StoragePools.Any(pool => pool.IsPrimordial
                && !before.StoragePools.Any(item => item.StableId == pool.StableId && item.IsPrimordial)))
            blocks.Add(T("系统原始池身份不能在结构草稿中修改。", "The primordial pool identity cannot change in a structure draft."));
        if (after.VirtualDisks.Any(vd => !after.StoragePools.Any(pool => !pool.IsPrimordial && pool.StableId == vd.PoolStableId)))
            blocks.Add(T("VD 草稿缺少唯一的目标池关系。", "A VD draft requires its identified non-primordial target pool."));
        if (session.HasBaselineConflict || session.OutcomeUnknown)
            blocks.Add(T("实时基线冲突或结果待核对；保留草稿，先完成核对。", "The baseline conflicts or an outcome needs reconciliation. The draft is retained."));
        foreach (var pool in removedPools)
        {
            if (!pool.IsStable || pool.MemberPhysicalDiskIds.Count != 1
                || before.VirtualDisks.Count(vd => vd.PoolStableId == pool.StableId) > 1)
                blocks.Add(T("仅支持准确单成员、至多一块 VD 的池解散。", "Dissolution requires an exact single-member pool with at most one VD."));
            if (before.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId && !SupportedObservedVirtualDisk(session, vd))
                || before.StorageTiers.Any(tier => tier.PoolStableId == pool.StableId && !SupportedHddTier(tier)))
                blocks.Add(T("旧池包含本阶段未支持的 VD 或层结构；不能借解散绕过能力核验。", "The old pool contains an unsupported VD or tier layout."));
            if (pool.MemberPhysicalDiskIds.Count == 1) members.Add(pool.MemberPhysicalDiskIds[0]);
            actions.Add(T($"解散 {pool.FriendlyName}：删除其 VD、分区、卷、层模板和池；全部数据丢失。",
                $"Dissolve {pool.FriendlyName}: remove its VD, partitions, volumes, templates and pool; all data is lost."));
        }
        foreach (var vd in removedVds)
        {
            var pool = before.StoragePools.SingleOrDefault(item => item.StableId == vd.PoolStableId);
            if (pool is not { IsPrimordial: false, IsStable: true } || pool.MemberPhysicalDiskIds.Count != 1
                || before.VirtualDisks.Count(item => item.PoolStableId == pool.StableId) != 1)
                blocks.Add(T("删除 VD 仅支持准确单成员池中的唯一 VD。", "Only the sole VD in an exact single-member pool can be deleted."));
            if (!SupportedObservedVirtualDisk(session, vd))
                blocks.Add(T("所选 VD 布局不在本阶段支持范围内。", "The selected VD layout is outside the supported scope."));
            if (pool is { MemberPhysicalDiskIds.Count: 1 }) members.Add(pool.MemberPhysicalDiskIds[0]);
            actions.Add(T($"删除 VD {vd.FriendlyName} 及全部子分区、卷和数据。", $"Delete VD {vd.FriendlyName}, its child partitions, volumes and data."));
        }
        foreach (var pool in after.StoragePools.Where(pool => !pool.IsPrimordial))
        {
            var original = before.StoragePools.SingleOrDefault(item => item.StableId == pool.StableId);
            var newPool = original is null;
            if (newPool && !EditWorkspace.IsDraftPool(pool.StableId))
                blocks.Add(T("新池必须来自本地新建草稿，不能把未采集的源身份当作新对象。", "A new pool must be a local draft, never an unobserved source identity."));
            var draftVds = after.VirtualDisks.Where(vd => vd.PoolStableId == pool.StableId
                && before.VirtualDisks.All(old => old.StableId != vd.StableId)).ToArray();
            if (draftVds.Any(vd => !EditWorkspace.IsDraftVirtualDisk(vd.StableId)))
                blocks.Add(T("新 VD 必须来自本地草稿，不能使用未核验的源身份。", "A new VD must be a local draft, never an unverified source identity."));
            var sourceVds = before.VirtualDisks.Where(vd => vd.PoolStableId == pool.StableId).ToArray();
            var explicitlyRemovedSourceVds = sourceVds.Where(vd => removedVds.Any(removed => removed.StableId == vd.StableId)).ToArray();
            var retainedSourceVds = sourceVds.Where(vd => after.VirtualDisks.Any(current =>
                current.StableId == vd.StableId && current.PoolStableId == pool.StableId)).ToArray();
            var replacesSoleSourceVd = !newPool && draftVds.Length == 1 && sourceVds.Length == 1
                && explicitlyRemovedSourceVds.Length == 1 && retainedSourceVds.Length == 0
                && SupportedObservedVirtualDisk(session, explicitlyRemovedSourceVds[0]);
            if (!newPool)
            {
                if (!original!.MemberPhysicalDiskIds.Order().SequenceEqual(pool.MemberPhysicalDiskIds.Order()))
                    blocks.Add(T("已有池成员迁移/移除不支持；重建请先显式解散旧池。", "Existing pool membership changes are unsupported; explicitly dissolve the old pool to rebuild."));
                if (original.FriendlyName != pool.FriendlyName)
                    blocks.Add(T("已有池名称请按 Enter 独立提交。", "Submit an existing pool rename independently with Enter."));
                if (original.Size != pool.Size || original.AllocatedSize != pool.AllocatedSize
                    || original.SubsystemStableId != pool.SubsystemStableId || original.IsStable != pool.IsStable
                    || original.IsPrimordial != pool.IsPrimordial || original.LogicalSectorSize != pool.LogicalSectorSize
                    || original.PhysicalSectorSize != pool.PhysicalSectorSize
                    || original.ProvisioningTypeDefault != pool.ProvisioningTypeDefault)
                    blocks.Add(T("已有池容量或归属不可通过草稿直接修改。", "Existing pool capacity or ownership cannot be edited directly in a draft."));
            }
            var continueLayout = session.PoolIntents.TryGetValue(pool.StableId, out var saved)
                && saved.PendingAutomaticLayout;
            var pendingTier = saved?.Layout == PoolVirtualDiskLayout.HddTiered
                && !before.StorageTiers.Any(tier => tier.PoolStableId == pool.StableId)
                && !before.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId);
            if (!newPool && draftVds.Length == 0 && !continueLayout && !pendingTier) continue;
            if (pool.MemberPhysicalDiskIds.Count != 1)
            {
                blocks.Add(T("真实池草稿必须且只能包含一块准确成员；无成员不能应用。", "A real pool draft requires exactly one identified physical member."));
                continue;
            }
            var memberId = pool.MemberPhysicalDiskIds[0];
            var physical = before.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
            if (physical is not { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }
                || physical.IsRetired || physical.IsHotSpare
                || PhysicalDiskUsage.Normalize(physical.Usage) is PhysicalDiskUsage.ManualSelect or PhysicalDiskUsage.Journal
                || PhysicalDiskUsage.IsUnknown(physical.Usage) || string.IsNullOrWhiteSpace(physical.SerialNumber))
                blocks.Add(T("成员身份或运行依赖不满足单盘真实操作要求。", "The member identity or runtime dependencies do not satisfy real single-disk editing."));
            var priorOwners = before.StoragePools.Where(item => !item.IsPrimordial
                && (item.MemberPhysicalDiskIds.Contains(memberId) || item.StableId == physical?.PoolStableId)).ToArray();
            if (priorOwners.Length > 1)
                blocks.Add(T("成员源池关系不唯一，不能准备真实操作。", "The member source pool relationship is ambiguous."));
            var priorOwner = priorOwners.FirstOrDefault();
            if (newPool && priorOwner is not null && removedPools.All(item => item.StableId != priorOwner.StableId))
                blocks.Add(T("不能直接迁移池成员；同一草稿须显式解散其旧池。", "A member cannot be migrated directly; its previous pool must be explicitly dissolved in this draft."));
            if (draftVds.Length > 1 || (draftVds.Length > 0 && !newPool
                && sourceVds.Length > 0 && !replacesSoleSourceVd))
                blocks.Add(T("仅支持已有空池的首块 VD，或同池明确删除唯一受支持旧 VD 后创建；保留旧 VD 或第二块 VD 不支持。",
                    "Only the first VD in an empty pool or creation after explicitly deleting its sole supported VD is supported; retaining an old VD or adding a second VD is unsupported."));
            if (!session.PoolIntents.TryGetValue(pool.StableId, out var intent))
            {
                blocks.Add(T("池草稿缺少原属性区的创建参数，请保存属性。", "Save the original property form: this pool draft has no creation parameters."));
                continue;
            }
            var createVd = draftVds.Length > 0;
            var plannedTiers = after.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                && tier.MemberPhysicalDiskIds.Count > 0 && before.StorageTiers.All(old => old.StableId != tier.StableId)).ToArray();
            var targetHddTemplates = after.StorageTiers.Where(tier => IsExactHddTemplateForMember(tier,
                pool.StableId, memberId)).ToArray();
            var createTier = intent.Layout == PoolVirtualDiskLayout.HddTiered
                && (newPool || ((createVd || pendingTier) && targetHddTemplates.Length == 0));
            if (newPool && plannedTiers.Length > 0)
            {
                if (intent.Layout != PoolVirtualDiskLayout.HddTiered || plannedTiers.Length != 1
                    || !SupportedHddTier(plannedTiers[0])
                    || plannedTiers[0].VirtualDiskStableId is not null
                    || plannedTiers[0].MemberPhysicalDiskIds.Count != 1
                    || plannedTiers[0].MemberPhysicalDiskIds[0] != memberId)
                    blocks.Add(T("新池的自动层草稿仅支持准确单 HDD、Simple、Fixed、1 列、64 KiB；其余媒体或规格保留草稿但不能应用。",
                        "The automatic tier draft requires one exact HDD, Simple, Fixed, one column and 64 KiB; other media or specifications remain in the draft and cannot be applied."));
                if (createVd && !intent.VirtualDiskUseMaximum && plannedTiers.Length == 1
                    && plannedTiers[0].Size != intent.VirtualDiskSizeBytes)
                    blocks.Add(T("层容量与已保存的虚拟磁盘创建目标不一致，请保存原层属性。",
                        "The tier capacity differs from the saved virtual-disk creation goal. Save the tier properties."));
            }
            if (newPool && createTier && plannedTiers.Length == 0)
                blocks.Add(T("自动 HDD 层缺少草稿成员关系，不能推断或忽略该目标。", "The automatic HDD tier lacks its draft member relationship."));
            if (continueLayout && (newPool || createVd || saved!.VerifiedVirtualDiskId is null
                || before.VirtualDisks.SingleOrDefault(vd => vd.StableId == saved.VerifiedVirtualDiskId)
                    is not { IsStable: true } verified || verified.PoolStableId != pool.StableId))
                blocks.Add(T("剩余布局缺少同一会话已核对的准确 VD。", "The remaining layout lacks this session's exact verified VD."));
            if (!Enum.IsDefined(intent.Layout) || string.IsNullOrWhiteSpace(pool.FriendlyName))
                blocks.Add(T("创建布局或池名无效。", "The creation layout or pool name is invalid."));
            if (intent.Layout == PoolVirtualDiskLayout.HddTiered && physical is { MediaType: not "HDD" })
                blocks.Add(T("本阶段分层仅支持准确单 HDD 成员。", "Tiered creation requires one exact HDD member."));
            if (draftVds.Any(vd => !SupportedVirtualDisk(vd)))
                blocks.Add(T("新 VD 草稿仅支持 Simple/Fixed、1 列及 64 KiB interleave。", "A new VD draft requires Simple/Fixed, one column and 64 KiB interleave."));
            if (!newPool && createVd)
            {
                var existingTiers = after.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId).ToArray();
                if (intent.Layout == PoolVirtualDiskLayout.Ordinary && existingTiers.Length > 0
                    || intent.Layout == PoolVirtualDiskLayout.HddTiered
                        && (existingTiers.Length > 1 || existingTiers.Any(tier =>
                            !IsExactHddTemplateForMember(tier, pool.StableId, memberId))))
                    blocks.Add(T("当前池模板不匹配所选布局；不能降级或忽略已有模板。", "The existing pool templates do not match the requested layout."));
            }
            if (continueLayout)
            {
                var verifiedVd = before.VirtualDisks.FirstOrDefault(vd => vd.StableId == intent.VerifiedVirtualDiskId);
                if (!intent.AutoCreatePartition || verifiedVd is null || !SupportedObservedVirtualDisk(session, verifiedVd)
                    || (intent.Layout == PoolVirtualDiskLayout.HddTiered) != (verifiedVd.TierStableIds.Count > 0)
                    || after.VirtualDisks.All(vd => vd.StableId != intent.VerifiedVirtualDiskId))
                    blocks.Add(T("已核对 VD 与剩余布局意图不一致。", "The verified VD does not match the remaining automatic layout intent."));
            }
            if (createVd || continueLayout)
            {
                if (string.IsNullOrWhiteSpace(intent.VirtualDiskName)
                    || (!intent.VirtualDiskUseMaximum && intent.VirtualDiskSizeBytes is not > 0))
                    blocks.Add(T("请输入 VD 名称及准确正容量或 MAX。", "Enter a VD name and a positive exact capacity or MAX."));
                if (intent.AutoCreatePartition && !intent.VirtualDiskUseMaximum
                    && intent.VirtualDiskSizeBytes is long explicitBytes)
                {
                    try { RealOperationProposalFactory.ValidateAutomaticLayoutCapacity(explicitBytes, intent.CreateMsr); }
                    catch (ArgumentException exception)
                    {
                        blocks.Add(T("明确容量不能承载所选自动布局：", "The exact capacity cannot hold the selected automatic layout: ") + exception.Message);
                    }
                }
                if (intent.AutoCreatePartition && (!intent.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                    || !intent.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                    || intent.AllocationUnitSize != 65536 || !intent.QuickFormat
                    || intent.DriveLetter is { } letter && (letter < 'D' || letter > 'Z')))
                    blocks.Add(T("自动布局当前仅支持 GPT、NTFS/64 KiB、快速格式化及可用 D–Z 盘符。", "Automatic layout currently requires GPT, quick NTFS/64 KiB and an available D–Z letter."));
            }
            members.Add(memberId);
            creations.Add(new(pool, memberId, intent, newPool, createVd, continueLayout, createTier));
            if (newPool)
            {
                var direct = before.OsDisks.SingleOrDefault(disk => disk.PhysicalDiskStableId == memberId && disk.VirtualDiskStableId is null);
                if (priorOwner is not null || direct is not null && (!direct.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                    || before.Partitions.Any(p => p.OsDiskStableId == direct.StableId)))
                    actions.Add(T("清除目标成员磁盘上的现有分区和数据，再创建存储池。",
                        "Clear existing partitions and data on the target member disk before creating the storage pool."));
                actions.Add(T($"创建单成员池 {pool.FriendlyName}。", $"Create single-member pool {pool.FriendlyName}."));
            }
            if (createTier)
                actions.Add(T($"按 HDD 成员自动创建容量层 {pool.FriendlyName}_HDD：Simple、Fixed、1 列、64 KiB；模板尚未分配容量。",
                    $"Automatically create HDD capacity tier {pool.FriendlyName}_HDD: Simple, Fixed, one column, 64 KiB; the template has no allocated capacity."));
            if (createVd || continueLayout)
            {
                if (createVd)
                {
                    if (intent.Layout == PoolVirtualDiskLayout.HddTiered && intent.VirtualDiskUseMaximum)
                        actions.Add(T(
                            $"创建单 HDD 分层虚拟磁盘 {intent.VirtualDiskName}，使用最大容量（MAX）；完成后显示实际容量。",
                            $"Create single-HDD tiered virtual disk {intent.VirtualDiskName} with maximum capacity (MAX); show the actual capacity when complete."));
                    else
                    {
                        var capacity = intent.VirtualDiskUseMaximum
                            ? T("最大容量（MAX，完成后显示实际容量）", "maximum capacity (MAX; actual capacity shown when complete)")
                            : T($"准确容量 {intent.VirtualDiskSizeBytes} bytes", $"exact capacity {intent.VirtualDiskSizeBytes} bytes");
                        actions.Add(T($"创建{(intent.Layout == PoolVirtualDiskLayout.HddTiered ? "单 HDD 分层" : "普通")} VD {intent.VirtualDiskName}，{capacity}。",
                            $"Create {(intent.Layout == PoolVirtualDiskLayout.HddTiered ? "single-HDD tiered" : "ordinary")} VD {intent.VirtualDiskName} with {capacity}."));
                    }
                }
                if (intent.AutoCreatePartition)
                    actions.Add(T($"自动 GPT 布局，{(intent.CreateMsr ? "规范 16 MiB MSR" : "无 MSR")}，NTFS/64 KiB，卷标 {intent.VolumeName}，盘符 {intent.DriveLetter?.ToString() ?? "无"}。",
                        $"Automatic GPT layout, {(intent.CreateMsr ? "canonical 16 MiB MSR" : "no MSR")}, NTFS/64 KiB, label {intent.VolumeName}, letter {intent.DriveLetter?.ToString() ?? "none"}."));
            }
        }
        foreach (var old in before.StorageTiers.Where(t => removedPools.All(p => p.StableId != t.PoolStableId)
                     && removedVds.All(vd => vd.StableId != t.VirtualDiskStableId)))
        {
            var current = after.StorageTiers.SingleOrDefault(item => item.StableId == old.StableId);
            if (current is null && old.VirtualDiskStableId is null
                && SupportedHddTier(old)
                && before.StoragePools.SingleOrDefault(p => p.StableId == old.PoolStableId) is
                    { IsPrimordial: false, IsStable: true, MemberPhysicalDiskIds.Count: 1 } parent
                && !after.VirtualDisks.Any(vd => vd.PoolStableId == parent.StableId)
                && before.VirtualDisks.Where(vd => vd.PoolStableId == parent.StableId)
                    .All(vd => removedVds.Any(removed => removed.StableId == vd.StableId)))
            {
                members.Add(parent.MemberPhysicalDiskIds[0]);
                removedTiers.Add(old);
                actions.Add(T($"删除未使用的准确 HDD 模板 {old.FriendlyName}。", $"Delete exact unused HDD template {old.FriendlyName}."));
            }
            else if (current is null || current.ResiliencySettingName != old.ResiliencySettingName
                || current.NumberOfColumns != old.NumberOfColumns || current.Interleave != old.Interleave
                || current.Size != old.Size || current.FootprintOnPool != old.FootprintOnPool
                || current.MediaType != old.MediaType || current.IsStable != old.IsStable
                || current.FriendlyName != old.FriendlyName
                || current.NumberOfDataCopies != old.NumberOfDataCopies
                || current.PhysicalDiskRedundancy != old.PhysicalDiskRedundancy
                || current.PoolStableId != old.PoolStableId || current.VirtualDiskStableId != old.VirtualDiskStableId
                || !current.MemberPhysicalDiskIds.Order().SequenceEqual(old.MemberPhysicalDiskIds.Order()))
                blocks.Add(T("已有层规格/用量不能原地修改；请在原流程显式解散并新建目标池。", "Existing tier specifications/usage cannot be changed in place; explicitly dissolve and create the replacement pool."));
        }
        foreach (var old in before.VirtualDisks.Where(vd => after.VirtualDisks.Any(item => item.StableId == vd.StableId)))
        {
            var current = after.VirtualDisks.Single(item => item.StableId == old.StableId);
            if (current.Size != old.Size || current.ResiliencySettingName != old.ResiliencySettingName
                || current.Interleave != old.Interleave || current.NumberOfColumns != old.NumberOfColumns
                || current.ProvisioningType != old.ProvisioningType || current.FriendlyName != old.FriendlyName
                || current.IsStable != old.IsStable || current.NumberOfDataCopies != old.NumberOfDataCopies
                || current.PhysicalDiskRedundancy != old.PhysicalDiskRedundancy
                || current.FootprintOnPool != old.FootprintOnPool || current.AllocatedSize != old.AllocatedSize
                || !current.OsDiskNumbers.Order().SequenceEqual(old.OsDiskNumbers.Order())
                || current.PoolStableId != old.PoolStableId
                || !current.TierStableIds.Order().SequenceEqual(old.TierStableIds.Order()))
                blocks.Add(T("既有 VD 扩缩或结构更改不支持；须显式解散/新建重建。", "Existing VD resize or layout changes are unsupported; explicitly dissolve and create the replacement."));
        }
        if (after.StorageTiers.Any(tier => before.StorageTiers.All(old => old.StableId != tier.StableId)
            && !EditWorkspace.IsDraftPool(tier.PoolStableId)))
            blocks.Add(T("新增层信息尚未核实；请先核对后再应用更改。", "New tier details are unverified; review them before applying the change."));
        var deletedVdIds = before.VirtualDisks.Where(vd => removedPools.Any(pool => pool.StableId == vd.PoolStableId)
            || removedVds.Any(removed => removed.StableId == vd.StableId)).Select(vd => vd.StableId).ToHashSet();
        var deletedDiskIds = before.OsDisks.Where(disk => deletedVdIds.Contains(disk.VirtualDiskStableId ?? ""))
            .Select(disk => disk.StableId).ToHashSet();
        var deletedPartitionIds = before.Partitions.Where(partition => deletedDiskIds.Contains(partition.OsDiskStableId ?? ""))
            .Select(partition => partition.StableId).ToHashSet();
        if (NestedObjectsDiffer(before.OsDisks.Where(disk => !deletedDiskIds.Contains(disk.StableId)).ToArray(),
                after.OsDisks.Where(disk => !deletedDiskIds.Contains(disk.StableId)).ToArray(), disk => disk.StableId)
            || NestedObjectsDiffer(before.Partitions.Where(partition => !deletedPartitionIds.Contains(partition.StableId)).ToArray(),
                after.Partitions.Where(partition => !deletedPartitionIds.Contains(partition.StableId)).ToArray(), partition => partition.StableId)
            || NestedObjectsDiffer(before.Volumes.Where(volume => !deletedPartitionIds.Contains(volume.PartitionStableId ?? "")).ToArray(),
                after.Volumes.Where(volume => !deletedPartitionIds.Contains(volume.PartitionStableId ?? "")).ToArray(), volume => volume.StableId,
                (old, current) => (current with { AccessPaths = old.AccessPaths }) == old
                    && current.AccessPaths.SequenceEqual(old.AccessPaths)))
            blocks.Add(T("磁盘、分区或卷的独立更改不属于结构草稿；请使用磁盘分区页。", "Independent disk, partition or volume changes belong in the disk partition editor."));
        if (after.PhysicalDisks.Count != before.PhysicalDisks.Count
            || after.PhysicalDisks.Any(disk => before.PhysicalDisks.SingleOrDefault(old => old.StableId == disk.StableId) is not { } old
                || (disk with { PoolStableId = old.PoolStableId, CanPool = old.CanPool, Usage = old.Usage }) != old
                || disk.Usage != old.Usage && (disk.PoolStableId == old.PoolStableId
                    || PhysicalDiskUsage.Normalize(disk.Usage) != PhysicalDiskUsage.AutoSelect)))
            blocks.Add(T("成员硬件身份或角色不能在结构草稿中修改。", "Member hardware identities or roles cannot be edited in a structure draft."));
        if (members.Count > 1 || creations.Count > 1)
            blocks.Add(T("本阶段每次 Apply 仅支持一个准确物理成员及一个目标池。", "Each Apply supports one exact physical member and one target pool."));
        var unblocked = blocks.Count == 0;
        return new(new(actions.Count > 0 && unblocked, actions, blocks.Distinct().ToArray()),
            unblocked ? removedPools : [], unblocked ? removedVds : [],
            unblocked ? removedTiers : [], unblocked ? creations : []);
    }

    private static bool DuplicateIdentities(StorageSnapshot snapshot) =>
        snapshot.StoragePools.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.VirtualDisks.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.StorageTiers.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.PhysicalDisks.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    private static bool SupportedVirtualDisk(VirtualDiskInfo disk) =>
        disk.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
        && disk.ProvisioningType.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
        && disk.NumberOfColumns == 1 && disk.Interleave == 65536
        && disk.NumberOfDataCopies is null or 1 && disk.PhysicalDiskRedundancy is null or 0;

    private static bool SupportedObservedVirtualDisk(SimulationEditingSession session, VirtualDiskInfo disk)
    {
        if (!disk.IsStable) return false;
        if (disk.TierStableIds.Count == 0) return SupportedVirtualDisk(disk)
            && !session.Baseline.StorageTiers.Any(tier => tier.VirtualDiskStableId == disk.StableId);
        if (disk.TierStableIds.Count != 1 || disk.Size <= 0 || disk.FootprintOnPool != disk.Size
            || session.BaselineSourceFacts is not { IsSimulation: false } facts) return false;
        var snapshot = session.Baseline;
        var pool = snapshot.StoragePools.SingleOrDefault(item => item.StableId == disk.PoolStableId);
        var tier = snapshot.StorageTiers.SingleOrDefault(item => item.StableId == disk.TierStableIds[0]);
        if (pool is not { IsStable: true, IsPrimordial: false, MemberPhysicalDiskIds.Count: 1 }
            || tier is null || !SupportedHddTier(tier) || tier.VirtualDiskStableId != disk.StableId
            || tier.PoolStableId != pool.StableId || tier.Size != disk.Size || tier.FootprintOnPool != disk.Size
            || tier.MemberPhysicalDiskIds.Count != 1 || tier.MemberPhysicalDiskIds[0] != pool.MemberPhysicalDiskIds[0]
            || snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == pool.MemberPhysicalDiskIds[0])
                is not { IsStable: true, MediaType: "HDD" }
            || snapshot.StorageTiers.Count(item => item.PoolStableId == pool.StableId && item.VirtualDiskStableId is not null) != 1
            || snapshot.StorageTiers.Count(item => item.PoolStableId == pool.StableId && item.VirtualDiskStableId is null) > 1)
            return false;
        var vdSource = ExactSource(disk.StableId, FactObjectType.VirtualDisk, "MSFT_VirtualDisk");
        var tierSource = ExactSource(tier.StableId, FactObjectType.StorageTier, "MSFT_StorageTier");
        if (vdSource is null || tierSource is null
            || !ExactParent("pool-virtual-disk", disk.StableId, pool.StableId)
            || !ExactParent("virtual-disk-tier", tier.StableId, disk.StableId)
            || facts.Relationships.Count(edge => edge.Kind == "virtual-disk-tier" && edge.FromId == disk.StableId) != 1
            || !ExactParent("pool-tier", tier.StableId, pool.StableId)
            || !ExactChild("tier-member", tier.StableId, pool.MemberPhysicalDiskIds[0])
            || !ExactChild("pool-member", pool.StableId, pool.MemberPhysicalDiskIds[0])) return false;
        // Aggregate VD fields may be explicitly returned null; missing/failed observations cannot borrow tier values.
        return Matches(vdSource, "ResiliencySettingName", "Simple", true)
            && Matches(vdSource, "ProvisioningType", 2L, true)
            && Matches(vdSource, "NumberOfColumns", 1L, true)
            && Matches(vdSource, "Interleave", 65536L, true)
            && Matches(vdSource, "NumberOfDataCopies", 1L, true)
            && Matches(vdSource, "PhysicalDiskRedundancy", 0L, true)
            && Matches(vdSource, "AllocatedSize", disk.Size)
            && Matches(tierSource, "ResiliencySettingName", "Simple")
            && Matches(tierSource, "ProvisioningType", 2L)
            && Matches(tierSource, "NumberOfColumns", 1L)
            && Matches(tierSource, "Interleave", 65536L)
            && Matches(tierSource, "NumberOfDataCopies", 1L)
            && Matches(tierSource, "PhysicalDiskRedundancy", 0L)
            && Matches(tierSource, "AllocatedSize", disk.Size);

        WinPoolSourceObject? ExactSource(string id, FactObjectType kind, string className)
        {
            var item = facts.Objects.SingleOrDefault(item => item.Id == id);
            var source = item is null ? null : facts.Sources.SingleOrDefault(source => source.Id == item.SourceRef);
            return item is { HasReliableIdentity: true } && item.ObjectType == kind
                && source is { Origin: FactOrigin.StorageCim, ReadState: FieldReadState.Returned }
                && source.ClassName == className
                && item.Field("UniqueId") is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } uid }
                && !string.IsNullOrWhiteSpace(uid.GetString())
                && item.Field("ObjectId") is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } oid }
                && !string.IsNullOrWhiteSpace(oid.GetString()) ? item : null;
        }
        bool ExactParent(string kind, string child, string parent)
        {
            var edges = facts.Relationships.Where(edge => edge.Kind == kind && edge.ToId == child).ToArray();
            return edges.Length == 1 && !edges[0].IsRetained && edges[0].FromId == parent;
        }
        bool ExactChild(string kind, string parent, string child)
        {
            var edges = facts.Relationships.Where(edge => edge.Kind == kind && edge.FromId == parent).ToArray();
            return edges.Length == 1 && !edges[0].IsRetained && edges[0].ToId == child;
        }
        static bool Matches(WinPoolSourceObject source, string name, object expected, bool allowNull = false)
        {
            if (source.Field(name) is not { ReadState: FieldReadState.Returned } field) return false;
            if (field.Value is not { } value) return allowNull;
            if (value.ValueKind == JsonValueKind.Null) return allowNull;
            return expected is string text
                ? value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), text, StringComparison.OrdinalIgnoreCase)
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number == (long)expected;
        }
    }

    private static bool SupportedHddTier(StorageTierInfo tier) => tier.IsStable
        && tier.MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase)
        && tier.ResiliencySettingName.Equals("Simple", StringComparison.OrdinalIgnoreCase)
        && tier.NumberOfColumns == 1 && tier.Interleave == 65536
        && tier.NumberOfDataCopies is null or 1 && tier.PhysicalDiskRedundancy is null or 0;

    private static bool IsExactHddTemplateForMember(StorageTierInfo tier, string poolId, string memberId) =>
        tier.PoolStableId == poolId && tier.VirtualDiskStableId is null && SupportedHddTier(tier)
        && (tier.MemberPhysicalDiskIds.Count == 0
            || tier.MemberPhysicalDiskIds.Count == 1 && tier.MemberPhysicalDiskIds[0] == memberId);

    private static bool NestedObjectsDiffer<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> id,
        Func<T, T, bool>? equal = null)
    {
        if (before.Count != after.Count || before.GroupBy(id).Any(group => group.Count() > 1)
            || after.GroupBy(id).Any(group => group.Count() > 1)) return true;
        equal ??= EqualityComparer<T>.Default.Equals;
        return before.Any(old => after.SingleOrDefault(current => id(current) == id(old)) is not { } current
            || !equal(old, current));
    }
}
