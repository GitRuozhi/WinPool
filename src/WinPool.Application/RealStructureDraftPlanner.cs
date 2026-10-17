using WinPool.Application;
using WinPool.Domain;
using System.Text.Json;

namespace WinPool.Application;

public sealed record RealStructureDraftPreview(bool CanApply,
    IReadOnlyList<string> Actions, IReadOnlyList<string> BlockingReasons)
{
    public IReadOnlyList<PendingStructureAction> Presentation { get; init; } = [];
}

public sealed record RealPoolCreationDraft(StoragePoolInfo Pool, string PhysicalDiskId,
    PoolEditIntent Intent, bool CreatePool, bool CreateVirtualDisk, bool ContinueLayout = false, bool CreateTier = false)
{
    /// <summary>All exact members in the target pool; PhysicalDiskId remains the compatibility anchor.</summary>
    public IReadOnlyList<string> PhysicalDiskIds => Pool.MemberPhysicalDiskIds;

    /// <summary>Saved per-media creation goals, including goals retained across a verified partial apply.</summary>
    public IReadOnlyList<PoolTierEditIntent> TierGoals => Intent.Tiers ?? [];
}

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
        var presentation = new List<PendingStructureAction>();
        void AddAction(string title, params PendingStructureParameter[] parameters)
        {
            actions.Add(title);
            presentation.Add(new(title, parameters.Where(parameter => parameter.Key != "SizeBytes"
                || parameter.Value.Length > 0).ToArray()));
        }
        PendingStructureParameter P(string key, object? value) => PendingStructureAction.Parameter(key, value);
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
        var allowedUsageEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (!pool.IsStable || pool.MemberPhysicalDiskIds.Count == 0
                || pool.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.MemberPhysicalDiskIds.Count)
                blocks.Add(T("解散需要身份准确且包含成员的池。", "Dissolution requires an exact pool identity and at least one distinct member."));
            if (before.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId && !SupportedObservedVirtualDisk(session, vd))
                || before.StorageTiers.Any(tier => tier.PoolStableId == pool.StableId && !SupportedObservedTier(session, tier)))
                blocks.Add(T("旧池包含本阶段未支持的 VD 或层结构；不能借解散绕过能力核验。", "The old pool contains an unsupported VD or tier layout."));
            AddAction(T($"解散 {pool.FriendlyName}：删除其 VD、分区、卷、层模板和池；全部数据丢失。",
                $"Dissolve {pool.FriendlyName}: remove its VD, partitions, volumes, templates and pool; all data is lost."), P("Target", pool.StableId), P("Name", pool.FriendlyName));
        }
        foreach (var vd in removedVds)
        {
            var pool = before.StoragePools.SingleOrDefault(item => item.StableId == vd.PoolStableId);
            if (pool is not { IsPrimordial: false, IsStable: true } || pool.MemberPhysicalDiskIds.Count == 0
                || pool.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.MemberPhysicalDiskIds.Count)
                blocks.Add(T("删除 VD 需要准确且包含成员的非原始池。", "Deleting a VD requires an exact non-primordial pool with distinct members."));
            if (!SupportedObservedVirtualDisk(session, vd))
                blocks.Add(T("所选 VD 布局不在本阶段支持范围内。", "The selected VD layout is outside the supported scope."));
            AddAction(T($"删除 VD {vd.FriendlyName} 及全部子分区、卷和数据。", $"Delete VD {vd.FriendlyName}, its child partitions, volumes and data."), P("Target", vd.StableId), P("VirtualDiskName", vd.FriendlyName));
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
            var hasIntent = session.PoolIntents.TryGetValue(pool.StableId, out var saved);
            var continueLayout = hasIntent && saved!.PendingAutomaticLayout;
            var tierContinuationCandidate = hasIntent && !newPool
                && saved!.Layout == PoolVirtualDiskLayout.HddTiered
                && !before.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId)
                && (saved.Tiers is { Count: > 0 }
                    || saved.Tiers is null && saved.AutoCreateVirtualDisk
                        && !before.StorageTiers.Any(tier => tier.PoolStableId == pool.StableId));
            var roleChanges = pool.MemberPhysicalDiskIds.Where(memberId =>
            {
                var source = before.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                var current = after.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                return source is not null && current is not null
                    && PhysicalDiskUsage.Normalize(source.Usage) != PhysicalDiskUsage.Normalize(current.Usage);
            }).ToArray();
            if (!newPool && draftVds.Length == 0 && !continueLayout && !tierContinuationCandidate && roleChanges.Length == 0) continue;
            if (pool.MemberPhysicalDiskIds.Count == 0)
            {
                blocks.Add(T("真实建池至少需要一个准确物理成员。", "A real pool requires at least one identified physical member."));
                continue;
            }
            if (pool.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.MemberPhysicalDiskIds.Count)
                blocks.Add(T("池成员身份重复。", "Pool member identities must be unique."));
            if (!hasIntent)
            {
                blocks.Add(T("池草稿缺少原属性区的创建参数，请保存属性。", "Save the original property form: this pool draft has no creation parameters."));
                continue;
            }
            var intent = saved!;
            var memberIds = pool.MemberPhysicalDiskIds.ToArray();
            var roleGoals = intent.MemberUsages?.ToArray() ?? memberIds.Select(memberId =>
            {
                var current = after.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                return new PoolMemberEditIntent(memberId, NormalizeMemberRole(current?.Usage)
                    ?? PhysicalDiskUsage.AutoSelect);
            }).ToArray();
            if (intent.MemberUsages is not null && (roleGoals.Select(item => item.PhysicalDiskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roleGoals.Length
                || roleGoals.Any(item => !memberIds.Contains(item.PhysicalDiskId, StringComparer.OrdinalIgnoreCase)
                    || NormalizeMemberRole(item.Usage) is null)))
                blocks.Add(T("成员角色目标必须使用池内唯一成员及 Auto-Select、Hot Spare 或 Retired。", "Member role goals must use unique pool members and Auto-Select, Hot Spare or Retired."));
            var roleByMember = roleGoals.Where(item => memberIds.Contains(item.PhysicalDiskId, StringComparer.OrdinalIgnoreCase))
                .GroupBy(item => item.PhysicalDiskId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => NormalizeMemberRole(group.Single().Usage)!, StringComparer.OrdinalIgnoreCase);
            var physicalById = new Dictionary<string, PhysicalDiskInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var memberId in memberIds)
            {
                var physical = before.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                var current = after.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                if (physical is null || current is null || physical is not { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }
                    || string.IsNullOrWhiteSpace(physical.SerialNumber) || NormalizeMedia(physical.MediaType) is null
                    || PhysicalDiskUsage.IsUnknown(physical.Usage))
                    blocks.Add(T("成员身份、介质或运行依赖无法核实。", "A member identity, media type or runtime dependency cannot be verified."));
                else physicalById[memberId] = physical;
                var role = roleByMember.GetValueOrDefault(memberId);
                if (role is not null && PhysicalDiskUsage.Normalize(current?.Usage) != role)
                    blocks.Add(T("保存的成员角色与结构草稿不一致。", "The saved member role does not match the structure draft."));
                if (intent.MemberUsages is not null && role is not null
                    && !after.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId
                        && before.VirtualDisks.Any(old => old.StableId == vd.StableId)))
                    allowedUsageEdits.Add(memberId);
            }
            var priorOwners = memberIds.SelectMany(memberId =>
            {
                var disk = before.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
                return before.StoragePools.Where(item => !item.IsPrimordial
                    && (item.MemberPhysicalDiskIds.Contains(memberId, StringComparer.OrdinalIgnoreCase)
                        || item.StableId == disk?.PoolStableId)).Select(item => (MemberId: memberId, Pool: item));
            }).ToArray();
            foreach (var group in priorOwners.GroupBy(item => item.MemberId, StringComparer.OrdinalIgnoreCase))
            {
                var owners = group.Select(item => item.Pool).DistinctBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).ToArray();
                if (owners.Length > 1)
                    blocks.Add(T("成员源池关系不唯一，不能准备真实操作。", "The member source pool relationship is ambiguous."));
                if (newPool && owners.FirstOrDefault() is { } owner
                    && removedPools.All(item => item.StableId != owner.StableId))
                    blocks.Add(T("不能直接迁移池成员；同一草稿须显式解散其旧池。", "A member cannot be migrated directly; its previous pool must be explicitly dissolved in this draft."));
            }
            if (draftVds.Length > 1 || (draftVds.Length > 0 && !newPool
                && sourceVds.Length > 0 && !replacesSoleSourceVd))
                blocks.Add(T("仅支持已有空池的首块 VD，或同池明确删除唯一受支持旧 VD 后创建；保留旧 VD 或第二块 VD 不支持。",
                    "Only the first VD in an empty pool or creation after explicitly deleting its sole supported VD is supported; retaining an old VD or adding a second VD is unsupported."));
            var createVd = draftVds.Length > 0;
            var dataMembers = memberIds.Select(memberId => physicalById.GetValueOrDefault(memberId))
                .Where(item => item is not null && PhysicalDiskUsage.ContributesDataCapacity(
                    roleByMember.GetValueOrDefault(item.StableId, PhysicalDiskUsage.Normalize(item.Usage))))
                .Select(item => item!).ToArray();
            var tierGoals = ResolveTierGoals(session, pool.StableId, intent, memberIds, physicalById, roleByMember);
            var pendingTier = tierContinuationCandidate && dataMembers.Length > 0 && tierGoals.Count > 0
                && tierGoals.Any(goal => !HasVerifiedTemplate(session, pool, goal, dataMembers));
            var missingTierTemplates = tierGoals.Count > 0
                && tierGoals.Any(goal => !HasVerifiedTemplate(session, pool, goal, dataMembers));
            var tierTemplateWork = intent.Layout == PoolVirtualDiskLayout.HddTiered
                && dataMembers.Length > 0
                && (newPool || pendingTier || createVd && missingTierTemplates);
            var requiresPerMediaGoals = createVd || continueLayout || tierTemplateWork;
            if ((createVd || continueLayout || pendingTier) && dataMembers.Length == 0)
                blocks.Add(T("没有参与数据容量的成员，不能创建 VD 或层模板。", "A VD or tier template requires at least one data-capacity member."));
            var virtualDiskUseMaximum = intent.VirtualDiskUseMaximum;
            long? virtualDiskSizeBytes = intent.VirtualDiskSizeBytes;
            if (intent.Layout == PoolVirtualDiskLayout.HddTiered && tierGoals.Count > 0)
            {
                tierGoals = tierGoals.OrderBy(goal => NormalizeMedia(goal.MediaType) switch
                {
                    "SCM" => 0,
                    "SSD" => 1,
                    "HDD" => 2,
                    _ => 3
                }).ToArray();
                virtualDiskUseMaximum = tierGoals.Any(goal => goal.UseMaximumSize);
                if (virtualDiskUseMaximum) virtualDiskSizeBytes = null;
                else
                {
                    try { virtualDiskSizeBytes = tierGoals.Aggregate(0L, (total, goal) => checked(total + goal.SizeBytes)); }
                    catch (OverflowException)
                    {
                        virtualDiskSizeBytes = null;
                        blocks.Add(T("层容量总和超过可支持的字节范围。", "The sum of tier capacities exceeds the supported byte range."));
                    }
                }
            }
            intent = intent with
            {
                Tiers = tierGoals,
                MemberUsages = roleGoals,
                VirtualDiskSizeBytes = virtualDiskSizeBytes,
                VirtualDiskUseMaximum = virtualDiskUseMaximum
            };
            if (!Enum.IsDefined(intent.Layout) || string.IsNullOrWhiteSpace(pool.FriendlyName))
                blocks.Add(T("创建布局或池名无效。", "The creation layout or pool name is invalid."));
            if (intent.Layout == PoolVirtualDiskLayout.Ordinary && tierGoals.Count > 0)
                blocks.Add(T("普通 VD 目标不能包含层模板目标。", "An ordinary VD goal cannot include tier template goals."));
            if (intent.Layout == PoolVirtualDiskLayout.HddTiered && requiresPerMediaGoals
                && tierGoals.Count == 0)
                blocks.Add(T("分层 VD 缺少按介质保存的层目标。", "A tiered VD requires saved per-media tier goals."));
            if (intent.Layout == PoolVirtualDiskLayout.HddTiered && requiresPerMediaGoals)
            {
                var dataMedia = dataMembers.Select(item => NormalizeMedia(item.MediaType))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var goalMedia = tierGoals.Select(goal => NormalizeMedia(goal.MediaType))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!dataMedia.SetEquals(goalMedia))
                    blocks.Add(T("分层目标必须覆盖每种参与数据容量的介质。", "Tier goals must cover every media type that contributes data capacity."));
                if (tierGoals.GroupBy(goal => NormalizeMedia(goal.MediaType), StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
                    blocks.Add(T("每种介质只能保存一个层目标。", "Only one tier goal may be saved for each media type."));
            }
            if (requiresPerMediaGoals)
            {
                foreach (var goal in tierGoals)
                {
                    var goalMembers = dataMembers.Where(item => NormalizeMedia(item.MediaType) == NormalizeMedia(goal.MediaType)).ToArray();
                    var validateCapacity = createVd || continueLayout;
                    ValidateLayout(goal.Layout, goalMembers, validateCapacity ? goal.SizeBytes : 0,
                        validateCapacity && goal.UseMaximumSize || !validateCapacity,
                        message => blocks.Add(T("层布局无效：", "Tier layout is invalid: ") + message));
                }
            }
            var plannedTiers = (newPool || createVd || continueLayout || pendingTier) ? after.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                && tier.MemberPhysicalDiskIds.Count > 0 && before.StorageTiers.All(old => old.StableId != tier.StableId)).ToArray()
                : Array.Empty<StorageTierInfo>();
            if (plannedTiers.Any(tier => intent.Layout != PoolVirtualDiskLayout.HddTiered
                || !tierGoals.Any(goal => NormalizeMedia(goal.MediaType) == NormalizeMedia(tier.MediaType)
                    && DraftTierMatchesGoal(tier, goal, dataMembers, createVd || continueLayout))))
                blocks.Add(T("草稿层的介质、成员或布局与保存的层目标不一致。", "A draft tier's media, members or layout does not match the saved tier goal."));
            var expectedMedia = tierGoals.Select(goal => NormalizeMedia(goal.MediaType)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plannedTiers.Select(tier => NormalizeMedia(tier.MediaType)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plannedTiers.Length
                || plannedTiers.Any(tier => !expectedMedia.Contains(NormalizeMedia(tier.MediaType)!)))
                blocks.Add(T("同一介质只能有一个准确的层模板目标。", "Each media type may have only one exact tier template goal."));
            var existingTiers = after.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                && tier.VirtualDiskStableId is null).ToArray();
            if (!newPool && createVd && intent.Layout == PoolVirtualDiskLayout.Ordinary && existingTiers.Length > 0)
                blocks.Add(T("当前池模板不匹配所选布局；不能降级或忽略已有模板。", "The existing pool templates do not match the requested layout."));
            var createTier = tierTemplateWork && intent.Layout == PoolVirtualDiskLayout.HddTiered
                && tierGoals.Any(goal => !HasVerifiedTemplate(session, pool, goal, dataMembers));
            if (tierGoals.Any(goal => NormalizeMedia(goal.MediaType) is null))
                blocks.Add(T("层目标介质必须为 HDD、SSD 或 SCM。", "A tier goal must use HDD, SSD or SCM media."));
            if (!newPool && !createVd && !continueLayout && !createTier && roleChanges.Length == 0)
                continue;
            if (continueLayout && (newPool || createVd || saved!.VerifiedVirtualDiskId is null
                || before.VirtualDisks.SingleOrDefault(vd => vd.StableId == saved.VerifiedVirtualDiskId)
                    is not { IsStable: true } verified || verified.PoolStableId != pool.StableId))
                blocks.Add(T("剩余布局缺少同一会话已核对的准确 VD。", "The remaining layout lacks this session's exact verified VD."));
            if (draftVds.Any(vd => intent.Layout == PoolVirtualDiskLayout.Ordinary
                && !SupportedVirtualDisk(vd, dataMembers)))
                blocks.Add(T("新 VD 布局与当前准确数据成员不兼容。", "The new VD layout is incompatible with the exact data members."));
            if (createVd && intent.Layout == PoolVirtualDiskLayout.Ordinary && draftVds.Length == 1)
            {
                if (RequestedLayout(draftVds[0]) is { } requestedLayout)
                    ValidateLayout(requestedLayout, dataMembers, intent.VirtualDiskSizeBytes ?? 0,
                        intent.VirtualDiskUseMaximum,
                        message => blocks.Add(T("VD 布局无效：", "Virtual disk layout is invalid: ") + message));
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
                    || (!virtualDiskUseMaximum && virtualDiskSizeBytes is not > 0))
                    blocks.Add(T("请输入 VD 名称及准确正容量或 MAX。", "Enter a VD name and a positive exact capacity or MAX."));
                if (intent.AutoCreatePartition && !virtualDiskUseMaximum
                    && virtualDiskSizeBytes is long explicitBytes)
                {
                    try { RealOperationProposalFactory.ValidateAutomaticLayoutCapacity(explicitBytes, intent.CreateMsr); }
                    catch (ArgumentException exception)
                    {
                        blocks.Add(T("明确容量不能承载所选自动布局：", "The exact capacity cannot hold the selected automatic layout: ") + exception.Message);
                    }
                }
                if (intent.AutoCreatePartition && (!intent.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                    || intent.FileSystem.ToUpperInvariant() is not ("NTFS" or "REFS" or "EXFAT")
                    || intent.AllocationUnitSize > int.MaxValue
                    || !FormatAllocationUnitPolicy.IsSupportedClusterSize(
                        intent.FileSystem, (int)intent.AllocationUnitSize, after.Computer.OsBuild)
                    || !intent.QuickFormat
                    || intent.DriveLetter is { } letter && (letter < 'D' || letter > 'Z')))
                    blocks.Add(T("自动布局要求 GPT、Windows 支持的 NTFS/ReFS/exFAT 分配单元、快速格式化及可用 D–Z 盘符。", "Automatic layout requires GPT, a Windows-supported NTFS/ReFS/exFAT allocation unit, quick format and an available D–Z letter."));
            }
            var anchorMember = memberIds[0];
            creations.Add(new(pool, anchorMember, intent, newPool, createVd, continueLayout, createTier));
            if (newPool)
            {
                foreach (var memberId in memberIds)
                {
                    var direct = before.OsDisks.SingleOrDefault(disk => disk.PhysicalDiskStableId == memberId && disk.VirtualDiskStableId is null);
                    if (priorOwners.Any(owner => owner.MemberId == memberId)
                        || direct is not null && (!direct.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                            || before.Partitions.Any(partition => partition.OsDiskStableId == direct.StableId)))
                        AddAction(T($"清除成员 {memberId} 上的现有分区和数据。", $"Clear existing partitions and data on member {memberId}."), P("Target", memberId));
                }
                AddAction(T($"创建包含 {memberIds.Length} 个准确成员的池 {pool.FriendlyName}。",
                    $"Create pool {pool.FriendlyName} with {memberIds.Length} exact members."), P("Target", pool.StableId), P("Name", pool.FriendlyName), P("Members", string.Join(", ", memberIds)));
                foreach (var memberId in memberIds)
                {
                    var media = NormalizeMedia(physicalById.GetValueOrDefault(memberId)?.MediaType) ?? "Unknown";
                    var role = roleByMember.GetValueOrDefault(memberId) ?? "Unknown";
                    AddAction(T($"成员 {memberId}：{media}，角色 {role}。", $"Member {memberId}: {media}, usage {role}."), P("Target", memberId), P("Media", media), P("Usage", role));
                }
            }
            foreach (var role in roleGoals)
            {
                var source = before.PhysicalDisks.SingleOrDefault(item => item.StableId == role.PhysicalDiskId);
                var normalized = NormalizeMemberRole(role.Usage);
                if (!newPool && normalized is not null && (source is null
                    || PhysicalDiskUsage.Normalize(source.Usage) != normalized))
                    AddAction(T($"成员 {role.PhysicalDiskId} 使用角色 {normalized}。",
                        $"Set member {role.PhysicalDiskId} usage to {normalized}."), P("Target", role.PhysicalDiskId), P("Usage", normalized));
            }
            if (createTier)
            {
                foreach (var goal in tierGoals.Where(goal => !HasVerifiedTemplate(session, pool, goal, dataMembers)))
                {
                    var media = NormalizeMedia(goal.MediaType)!;
                    AddAction(T($"为 {media} 成员创建层模板：{FormatLayout(goal.Layout, chinese)}；VD 目标容量 {FormatCapacity(goal.SizeBytes, goal.UseMaximumSize, chinese)}。",
                        $"Create a {media} tier template: {FormatLayout(goal.Layout, false)}; VD target capacity {FormatCapacity(goal.SizeBytes, goal.UseMaximumSize, false)}."), P("Target", pool.StableId), P("Media", media), P("Resiliency", goal.Layout.ResiliencySettingName), P("Columns", goal.Layout.NumberOfColumns), P("Interleave", goal.Layout.InterleaveBytes), P("Copies", goal.Layout.NumberOfDataCopies), P("Failures", goal.Layout.PhysicalDiskRedundancy), P("SizeBytes", goal.UseMaximumSize && goal.SizeBytes == 0 ? null : goal.SizeBytes), P("Maximum", goal.UseMaximumSize));
                }
            }
            if (createVd || continueLayout)
            {
                if (createVd)
                {
                    var capacity = intent.Layout == PoolVirtualDiskLayout.HddTiered
                        ? chinese ? "各介质层目标容量" : "the per-media tier capacity goals"
                        : FormatCapacity(virtualDiskSizeBytes ?? 0, virtualDiskUseMaximum, chinese);
                    var kind = intent.Layout == PoolVirtualDiskLayout.HddTiered ? T("分层", "tiered") : T("普通", "ordinary");
                    var layout = intent.Layout == PoolVirtualDiskLayout.HddTiered
                        ? string.Join("; ", tierGoals.Select(goal => $"{NormalizeMedia(goal.MediaType)} {FormatLayout(goal.Layout, chinese)}"))
                        : draftVds.Length == 0 || RequestedLayout(draftVds[0]) is not { } requested
                            ? "" : FormatLayout(requested, chinese);
                    AddAction(T($"创建{kind} VD {intent.VirtualDiskName}，{layout}，{capacity}。",
                        $"Create {kind} VD {intent.VirtualDiskName}: {layout}, {capacity}."), P("Target", pool.StableId), P("VirtualDiskName", intent.VirtualDiskName), P("Layout", layout), P("Capacity", capacity));
                }
                if (intent.AutoCreatePartition)
                    AddAction(T($"自动 GPT 布局，{(intent.CreateMsr ? "规范 16 MiB MSR" : "无 MSR")}，{intent.FileSystem}/{FormatClusterSize(intent.AllocationUnitSize)}，卷标 {intent.VolumeName}，盘符 {intent.DriveLetter?.ToString() ?? "无"}。",
                        $"Automatic GPT layout, {(intent.CreateMsr ? "canonical 16 MiB MSR" : "no MSR")}, {intent.FileSystem}/{FormatClusterSize(intent.AllocationUnitSize)}, label {intent.VolumeName}, letter {intent.DriveLetter?.ToString() ?? "none"}."), P("Target", pool.StableId), P("PartitionStyle", "GPT"), P("CreateMsr", intent.CreateMsr), P("FileSystem", intent.FileSystem), P("AllocationUnitSize", intent.AllocationUnitSize), P("VolumeName", intent.VolumeName), P("DriveLetter", intent.DriveLetter), P("QuickFormat", intent.QuickFormat));
            }
        }
        foreach (var old in before.StorageTiers.Where(t => removedPools.All(p => p.StableId != t.PoolStableId)
                     && removedVds.All(vd => vd.StableId != t.VirtualDiskStableId)))
        {
            var current = after.StorageTiers.SingleOrDefault(item => item.StableId == old.StableId);
            if (current is null && old.VirtualDiskStableId is null
                && SupportedObservedTier(session, old)
                && before.StoragePools.SingleOrDefault(p => p.StableId == old.PoolStableId) is
                    { IsPrimordial: false, IsStable: true, MemberPhysicalDiskIds.Count: > 0 } parent
                && !after.VirtualDisks.Any(vd => vd.PoolStableId == parent.StableId)
                && before.VirtualDisks.Where(vd => vd.PoolStableId == parent.StableId)
                    .All(vd => removedVds.Any(removed => removed.StableId == vd.StableId)))
            {
                removedTiers.Add(old);
                AddAction(T($"删除未使用的准确 {NormalizeMedia(old.MediaType) ?? old.MediaType} 模板 {old.FriendlyName}。",
                    $"Delete exact unused {NormalizeMedia(old.MediaType) ?? old.MediaType} template {old.FriendlyName}."), P("Target", old.StableId), P("Name", old.FriendlyName), P("Media", old.MediaType));
            }
            else if (current is null || current.ResiliencySettingName != old.ResiliencySettingName
                || current.NumberOfColumns != old.NumberOfColumns || current.Interleave != old.Interleave
                || current.Size != old.Size || current.FootprintOnPool != old.FootprintOnPool
                || current.MediaType != old.MediaType || current.IsStable != old.IsStable
                || current.FriendlyName != old.FriendlyName
                || current.NumberOfDataCopies != old.NumberOfDataCopies
                || current.PhysicalDiskRedundancy != old.PhysicalDiskRedundancy
                || current.PoolStableId != old.PoolStableId || current.VirtualDiskStableId != old.VirtualDiskStableId
                || !current.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(old.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase))
                    && !IsUnusedTemplateRoleProjection(session, old, current))
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
        // Free bytes are observations, not requested edits; ordinary OS activity can change them before Agent preparation.
        if (NestedObjectsDiffer(before.OsDisks.Where(disk => !deletedDiskIds.Contains(disk.StableId)).ToArray(),
                after.OsDisks.Where(disk => !deletedDiskIds.Contains(disk.StableId)).ToArray(), disk => disk.StableId)
            || NestedObjectsDiffer(before.Partitions.Where(partition => !deletedPartitionIds.Contains(partition.StableId)).ToArray(),
                after.Partitions.Where(partition => !deletedPartitionIds.Contains(partition.StableId)).ToArray(), partition => partition.StableId,
                (old, current) => (current with { SizeRemaining = old.SizeRemaining }) == old)
            || NestedObjectsDiffer(before.Volumes.Where(volume => !deletedPartitionIds.Contains(volume.PartitionStableId ?? "")).ToArray(),
                after.Volumes.Where(volume => !deletedPartitionIds.Contains(volume.PartitionStableId ?? "")).ToArray(), volume => volume.StableId,
                (old, current) => (current with { AccessPaths = old.AccessPaths, SizeRemaining = old.SizeRemaining }) == old
                    && current.AccessPaths.SequenceEqual(old.AccessPaths)))
            blocks.Add(T("磁盘、分区或卷的独立更改不属于结构草稿；请使用磁盘分区页。", "Independent disk, partition or volume changes belong in the disk partition editor."));
        if (after.PhysicalDisks.Count != before.PhysicalDisks.Count
            || after.PhysicalDisks.Any(disk => before.PhysicalDisks.SingleOrDefault(old => old.StableId == disk.StableId) is not { } old
                || (disk with { PoolStableId = old.PoolStableId, CanPool = old.CanPool, Usage = old.Usage }) != old
                || PhysicalDiskUsage.Normalize(disk.Usage) != PhysicalDiskUsage.Normalize(old.Usage)
                    && !allowedUsageEdits.Contains(disk.StableId)
                    && !(disk.PoolStableId != old.PoolStableId
                        && PhysicalDiskUsage.Normalize(disk.Usage) == PhysicalDiskUsage.AutoSelect)))
            blocks.Add(T("成员硬件身份不可修改；角色变更必须作为池成员目标保存。", "Member hardware identities cannot change; usage changes must be saved as pool member goals."));
        if (creations.Count > 1)
            blocks.Add(T("每次 Apply 仅支持一个目标池。", "Each Apply supports one target pool."));
        var unblocked = blocks.Count == 0;
        // Unsupported drafts remain editable, but must not describe a different
        // supported layout as if it were the user's executable target.
        return new(new(actions.Count > 0 && unblocked, unblocked ? actions : [], blocks.Distinct().ToArray())
            { Presentation = unblocked ? presentation : [] },
            unblocked ? removedPools : [], unblocked ? removedVds : [],
            unblocked ? removedTiers : [], unblocked ? creations : []);
    }

    private static bool DuplicateIdentities(StorageSnapshot snapshot) =>
        snapshot.StoragePools.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.VirtualDisks.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.StorageTiers.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
        || snapshot.PhysicalDisks.GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    private static bool SupportedVirtualDisk(VirtualDiskInfo disk, IReadOnlyList<PhysicalDiskInfo> dataMembers)
    {
        if (!disk.ProvisioningType.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
            || disk.NumberOfColumns is null || disk.Interleave is null) return false;
        return RequestedLayout(disk) is { } layout && IsValidLayout(layout, dataMembers, 0, true);
    }

    private static bool SupportedObservedVirtualDisk(SimulationEditingSession session, VirtualDiskInfo disk)
    {
        if (!disk.IsStable || disk.PoolStableId is null) return false;
        var snapshot = session.Baseline;
        var pool = snapshot.StoragePools.SingleOrDefault(item => item.StableId == disk.PoolStableId);
        if (pool is not { IsStable: true, IsPrimordial: false, MemberPhysicalDiskIds.Count: > 0 }
            || pool.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.MemberPhysicalDiskIds.Count)
            return false;
        var poolMembers = pool.MemberPhysicalDiskIds.Select(id => snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == id))
            .ToArray();
        if (poolMembers.Any(item => item is not { IsStable: true }
            || NormalizeMedia(item.MediaType) is null || PhysicalDiskUsage.IsUnknown(item.Usage)
            || PhysicalDiskUsage.Normalize(item.Usage) is PhysicalDiskUsage.ManualSelect or PhysicalDiskUsage.Journal)) return false;
        var dataMembers = poolMembers.Where(item => PhysicalDiskUsage.ContributesDataCapacity(item!.Usage))
            .Select(item => item!).ToArray();
        if (dataMembers.Length == 0 || disk.Size <= 0 || disk.FootprintOnPool < disk.Size) return false;
        if (disk.TierStableIds.Count == 0)
        {
            return !snapshot.StorageTiers.Any(tier => tier.VirtualDiskStableId == disk.StableId)
                && SupportedVirtualDisk(disk, dataMembers);
        }
        if (disk.TierStableIds.Count != disk.TierStableIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || disk.Size <= 0 || disk.FootprintOnPool < disk.Size
            || disk.TierStableIds.Count == 0
            || session.BaselineSourceFacts is not { IsSimulation: false } facts) return false;
        var tiers = disk.TierStableIds.Select(id => snapshot.StorageTiers.SingleOrDefault(item => item.StableId == id)).ToArray();
        if (tiers.Any(tier => tier is null) || tiers.Select(tier => NormalizeMedia(tier!.MediaType))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != tiers.Length
            || snapshot.StorageTiers.Count(item => item.VirtualDiskStableId == disk.StableId) != tiers.Length)
            return false;
        long totalTierBytes = 0;
        var layouts = new List<StorageLayoutParameters>();
        foreach (var tier in tiers.Select(item => item!))
        {
            if (tier.PoolStableId != pool.StableId || tier.VirtualDiskStableId != disk.StableId
                || tier.Size <= 0 || tier.FootprintOnPool < tier.Size || !SupportedTierLayout(tier, out var layout)) return false;
            var media = NormalizeMedia(tier.MediaType)!;
            var eligible = dataMembers.Where(item => NormalizeMedia(item.MediaType) == media)
                .ToDictionary(item => item.StableId, StringComparer.OrdinalIgnoreCase);
            if (tier.MemberPhysicalDiskIds.Count == 0
                || tier.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tier.MemberPhysicalDiskIds.Count
                || tier.MemberPhysicalDiskIds.Any(id => !eligible.ContainsKey(id))
                || !IsValidLayout(layout!, tier.MemberPhysicalDiskIds.Select(id => eligible[id]).ToArray(), tier.Size, false)) return false;
            try { totalTierBytes = checked(totalTierBytes + tier.Size); }
            catch (OverflowException) { return false; }
            layouts.Add(layout!);
        }
        if (totalTierBytes != disk.Size) return false;
        var vdSource = ExactSource(facts, disk.StableId, FactObjectType.VirtualDisk, "MSFT_VirtualDisk");
        if (vdSource is null || !ExactParent(facts, "pool-virtual-disk", disk.StableId, pool.StableId)
            || !ExactParents(facts, "virtual-disk-tier", disk.StableId, tiers.Select(item => item!.StableId))
            || tiers.Any(item => !ExactParent(facts, "virtual-disk-tier", item!.StableId, disk.StableId))
            || !ExactParents(facts, "pool-member", pool.StableId, pool.MemberPhysicalDiskIds)
            || pool.MemberPhysicalDiskIds.Any(item => !ExactParent(facts, "pool-member", item, pool.StableId))) return false;
        foreach (var tier in tiers.Select(item => item!))
        {
            var source = ExactSource(facts, tier.StableId, FactObjectType.StorageTier, "MSFT_StorageTier");
            var membersForTier = tier.MemberPhysicalDiskIds;
            if (source is null || !ExactParent(facts, "pool-tier", tier.StableId, pool.StableId)
                || !ExactParents(facts, "tier-member", tier.StableId, membersForTier)
                || membersForTier.Any(item => !ExactParent(facts, "tier-member", item, tier.StableId))
                || !TierSourceMatches(source, tier, out _)) return false;
        }
        return MatchesAny(vdSource, "ResiliencySettingName", layouts.Select(item => item.ResiliencySettingName).Cast<object>(), allowNull: true)
            && Matches(vdSource, "ProvisioningType", 2L, true)
            && MatchesAny(vdSource, "NumberOfColumns", layouts.Select(item => (long)item.NumberOfColumns).Cast<object>(), true)
            && MatchesAny(vdSource, "Interleave", layouts.Select(item => (long)item.InterleaveBytes).Cast<object>(), true)
            && MatchesAny(vdSource, "NumberOfDataCopies", layouts.Select(item => (long)item.NumberOfDataCopies).Cast<object>(), true)
            && MatchesAny(vdSource, "PhysicalDiskRedundancy", layouts.Select(item => (long)item.PhysicalDiskRedundancy).Cast<object>(), true)
            && Matches(vdSource, "AllocatedSize", disk.Size);

        static WinPoolSourceObject? ExactSource(WinPoolFacts sourceFacts, string id, FactObjectType kind, string className)
        {
            var item = sourceFacts.Objects.SingleOrDefault(item => item.Id == id);
            var source = item is null ? null : sourceFacts.Sources.SingleOrDefault(source => source.Id == item.SourceRef);
            return item is { HasReliableIdentity: true } && item.ObjectType == kind
                && source is { Origin: FactOrigin.StorageCim, ReadState: FieldReadState.Returned }
                && source.ClassName == className
                && item.Field("UniqueId") is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } uid }
                && !string.IsNullOrWhiteSpace(uid.GetString())
                && item.Field("ObjectId") is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } oid }
                && !string.IsNullOrWhiteSpace(oid.GetString()) ? item : null;
        }
        static bool ExactParent(WinPoolFacts sourceFacts, string kind, string child, string parent)
        {
            var edges = sourceFacts.Relationships.Where(edge => edge.Kind == kind && edge.ToId == child).ToArray();
            return edges.Length == 1 && !edges[0].IsRetained && edges[0].FromId == parent;
        }
        static bool ExactParents(WinPoolFacts sourceFacts, string kind, string parent, IEnumerable<string> children)
        {
            var expected = children.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var edges = sourceFacts.Relationships.Where(edge => edge.Kind == kind && edge.FromId == parent).ToArray();
            return edges.Length == expected.Length && edges.All(edge => !edge.IsRetained)
                && edges.Select(edge => edge.ToId).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected);
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
        static bool MatchesAny(WinPoolSourceObject source, string name, IEnumerable<object> expected, bool allowNull)
        {
            if (source.Field(name) is not { ReadState: FieldReadState.Returned } field) return false;
            if (field.Value is not { } value || value.ValueKind == JsonValueKind.Null) return allowNull;
            return expected.Any(item => item is string text
                ? value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), text, StringComparison.OrdinalIgnoreCase)
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number == (long)item);
        }
    }

    private static bool SupportedObservedTier(SimulationEditingSession session, StorageTierInfo tier)
    {
        if (!tier.IsStable || NormalizeMedia(tier.MediaType) is null || tier.PoolStableId is null) return false;
        var pool = session.Baseline.StoragePools.SingleOrDefault(item => item.StableId == tier.PoolStableId);
        if (pool is not { IsStable: true, IsPrimordial: false } || !SupportedTierLayout(tier, out var layout)) return false;
        var candidateFacts = tier.MemberPhysicalDiskIds.Count == 0
            ? pool.MemberPhysicalDiskIds.Select(id => session.Baseline.PhysicalDisks.SingleOrDefault(item => item.StableId == id))
                .Where(item => item is not null && NormalizeMedia(item.MediaType) == NormalizeMedia(tier.MediaType)
                    && PhysicalDiskUsage.ContributesDataCapacity(item.Usage)).Select(item => item!).ToArray()
            : tier.MemberPhysicalDiskIds.Select(id => session.Baseline.PhysicalDisks.SingleOrDefault(item => item.StableId == id)).ToArray();
        if (candidateFacts.Any(item => item is null)) return false;
        var candidates = candidateFacts.Select(item => item!).ToArray();
        var expectedMembers = pool.MemberPhysicalDiskIds.Select(id => session.Baseline.PhysicalDisks.SingleOrDefault(item => item.StableId == id))
            .Where(item => item is not null && NormalizeMedia(item.MediaType) == NormalizeMedia(tier.MediaType)
                && PhysicalDiskUsage.ContributesDataCapacity(item.Usage)).Select(item => item!).ToArray();
        if (candidates.Any(item => item is not { IsStable: true } || NormalizeMedia(item.MediaType) != NormalizeMedia(tier.MediaType))
            || tier.MemberPhysicalDiskIds.Count > 0 && !tier.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(candidates.Select(item => item.StableId).Order(StringComparer.OrdinalIgnoreCase))
            || tier.MemberPhysicalDiskIds.Any(id => !expectedMembers.Any(member => member.StableId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            || tier.MemberPhysicalDiskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tier.MemberPhysicalDiskIds.Count) return false;
        if (tier.VirtualDiskStableId is not null && (tier.Size <= 0 || tier.FootprintOnPool < tier.Size)) return false;
        if (!IsValidLayout(layout!, candidates, tier.Size, tier.VirtualDiskStableId is null || tier.Size == 0)) return false;
        if (session.BaselineSourceFacts is not { IsSimulation: false } facts) return true;
        var source = ExactSource(facts, tier.StableId, FactObjectType.StorageTier, "MSFT_StorageTier");
        if (source is null || !TierSourceMatches(source, tier, out _)) return false;
        var poolEdges = facts.Relationships.Where(edge => edge.Kind == "pool-tier" && edge.ToId == tier.StableId).ToArray();
        var tierMembersExact = HasExactRelationshipChildren(facts, "tier-member", tier.StableId, tier.MemberPhysicalDiskIds)
            && tier.MemberPhysicalDiskIds.All(item => HasExactRelationshipParent(facts, "tier-member", item, tier.StableId));
        return poolEdges.Length == 1 && !poolEdges[0].IsRetained && poolEdges[0].FromId == pool.StableId
            && facts.Relationships.Where(edge => edge.Kind == "pool-member" && edge.FromId == pool.StableId)
                .All(edge => !edge.IsRetained)
            && facts.Relationships.Count(edge => edge.Kind == "pool-member" && edge.FromId == pool.StableId)
                == pool.MemberPhysicalDiskIds.Count
            && pool.MemberPhysicalDiskIds.All(item => HasExactRelationshipParent(facts, "pool-member", item, pool.StableId))
            && tierMembersExact;
    }

    private static string FormatClusterSize(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes % (1024 * 1024) == 0
            ? $"{bytes / (1024 * 1024)} MiB"
            : $"{bytes / 1024} KiB";

    private static bool SupportedTierLayout(StorageTierInfo tier, out StorageLayoutParameters? layout)
    {
        layout = tier.NumberOfColumns is int columns && tier.Interleave is long interleave
            && interleave is >= 0 and <= int.MaxValue
            ? CanonicalLayout(new(tier.ResiliencySettingName, columns, (int)interleave,
                tier.NumberOfDataCopies ?? 1, tier.PhysicalDiskRedundancy ?? 0)) : null;
        return layout is not null && IsValidLayout(layout, [], 0, true);
    }

    private static bool TierSourceMatches(WinPoolSourceObject source, StorageTierInfo tier,
        out StorageLayoutParameters? layout)
    {
        if (!SupportedTierLayout(tier, out layout)) return false;
        return Matches(source, "ResiliencySettingName", layout!.ResiliencySettingName)
            && Matches(source, "NumberOfColumns", (long)layout.NumberOfColumns)
            && Matches(source, "Interleave", (long)layout.InterleaveBytes)
            && Matches(source, "NumberOfDataCopies", (long)layout.NumberOfDataCopies)
            && Matches(source, "PhysicalDiskRedundancy", (long)layout.PhysicalDiskRedundancy)
            // Pool-level templates have no allocated size. For an allocated
            // tier, AllocatedSize is its logical allocation; FootprintOnPool
            // separately reports the physical allocation required by resiliency.
            && (tier.VirtualDiskStableId is null
                || Matches(source, "ProvisioningType", 2L) && Matches(source, "AllocatedSize", tier.Size));
    }

    private static bool Matches(WinPoolSourceObject source, string name, object expected)
    {
        if (source.Field(name) is not { ReadState: FieldReadState.Returned, Value: { } value }
            || value.ValueKind == JsonValueKind.Null) return false;
        return expected is string text
            ? value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), text, StringComparison.OrdinalIgnoreCase)
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number == (long)expected;
    }

    private static WinPoolSourceObject? ExactSource(WinPoolFacts facts, string id, FactObjectType kind, string className)
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

    private static bool HasExactRelationshipParent(WinPoolFacts facts, string kind, string child, string parent)
    {
        var edges = facts.Relationships.Where(edge => edge.Kind == kind && edge.ToId == child).ToArray();
        return edges.Length == 1 && !edges[0].IsRetained && edges[0].FromId == parent;
    }

    private static bool HasExactRelationshipChildren(WinPoolFacts facts, string kind, string parent,
        IEnumerable<string> children)
    {
        var expected = children.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var edges = facts.Relationships.Where(edge => edge.Kind == kind && edge.FromId == parent).ToArray();
        return edges.Length == expected.Length && edges.All(edge => !edge.IsRetained)
            && edges.Select(edge => edge.ToId).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected);
    }

    private static StorageLayoutParameters? RequestedLayout(VirtualDiskInfo disk) => CanonicalLayout(new(
        disk.ResiliencySettingName, disk.NumberOfColumns ?? 1, ToInt32OrInvalid(disk.Interleave ?? 65536),
        disk.NumberOfDataCopies ?? 1, disk.PhysicalDiskRedundancy ?? 0));

    private static int ToInt32OrInvalid(long value) => value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;

    private static StorageLayoutParameters? CanonicalLayout(StorageLayoutParameters layout)
    {
        var resiliency = (layout.ResiliencySettingName ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "SIMPLE" => "Simple",
            "MIRROR" => "Mirror",
            "PARITY" => "Parity",
            _ => null
        };
        return resiliency is null ? null : layout with { ResiliencySettingName = resiliency };
    }

    private static bool IsValidLayout(StorageLayoutParameters layout, IReadOnlyList<PhysicalDiskInfo> members,
        long sizeBytes, bool useMaximum)
    {
        var canonical = CanonicalLayout(layout);
        if (canonical is null) return false;
        try
        {
            canonical.Validate();
            if (canonical.ResiliencySettingName == "Simple"
                && (canonical.NumberOfDataCopies != 1 || canonical.PhysicalDiskRedundancy != 0)) return false;
            if (canonical.ResiliencySettingName == "Mirror"
                && (canonical.NumberOfDataCopies < 2 || canonical.PhysicalDiskRedundancy != canonical.NumberOfDataCopies - 1)) return false;
            if (canonical.ResiliencySettingName == "Parity"
                && (canonical.NumberOfDataCopies != 1 || canonical.PhysicalDiskRedundancy < 1
                    || canonical.NumberOfColumns < canonical.PhysicalDiskRedundancy + 2)) return false;
            if (members.Count == 0) return true;
            var estimate = canonical.EstimateCapacity(members.Select(item => item.Size).ToArray());
            return useMaximum || sizeBytes > 0 && sizeBytes <= estimate.AlignedLogicalBytes;
        }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static void ValidateLayout(StorageLayoutParameters layout, IReadOnlyList<PhysicalDiskInfo> members,
        long sizeBytes, bool useMaximum, Action<string> invalid)
    {
        if (members.Count == 0)
        {
            invalid("no data-capacity members match this media type.");
            return;
        }
        if (!useMaximum && sizeBytes <= 0)
        {
            invalid("choose MAX or a positive exact capacity.");
            return;
        }
        if (!IsValidLayout(layout, members, sizeBytes, useMaximum))
            invalid("columns, copies, redundancy, capacity or interleave do not fit the exact members.");
    }

    private static IReadOnlyList<PoolTierEditIntent> ResolveTierGoals(SimulationEditingSession session, string poolId,
        PoolEditIntent intent, IReadOnlyList<string> memberIds,
        IReadOnlyDictionary<string, PhysicalDiskInfo> physicalById,
        IReadOnlyDictionary<string, string> roleByMember)
    {
        if (intent.Tiers is not null) return intent.Tiers;
        if (intent.Layout != PoolVirtualDiskLayout.HddTiered) return [];
        var media = memberIds.Select(id => physicalById.GetValueOrDefault(id))
            .Where(item => item is not null && PhysicalDiskUsage.ContributesDataCapacity(
                roleByMember.GetValueOrDefault(item.StableId, PhysicalDiskUsage.Normalize(item.Usage))))
            .Select(item => NormalizeMedia(item!.MediaType)).Where(item => item is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item switch { "SCM" => 0, "SSD" => 1, "HDD" => 2, _ => 3 })
            .ToArray();
        var goals = new List<PoolTierEditIntent>();
        foreach (var mediaType in media)
        {
            var tier = session.Working.StorageTiers.FirstOrDefault(item => item.PoolStableId == poolId
                    && NormalizeMedia(item.MediaType) == mediaType)
                ?? session.Baseline.StorageTiers.FirstOrDefault(item => item.PoolStableId == poolId
                    && NormalizeMedia(item.MediaType) == mediaType);
            var useMaximum = session.MaximumSizeFields.Contains($"{poolId}|{mediaType}")
                || session.MaximumSizeFields.Count == 0 && intent.VirtualDiskUseMaximum;
            if (tier is null)
            {
                // Older saved intents carried only one global HDD goal. Its
                // layout and capacity remain recoverable for a single-media
                // pool; a multi-media aggregate cannot be split accurately.
                if (media.Length == 1 && mediaType == "HDD")
                {
                    var legacySize = useMaximum ? 0 : intent.VirtualDiskSizeBytes ?? 0;
                    goals.Add(new(mediaType!, StorageLayoutParameters.LegacyDefault,
                        legacySize, useMaximum));
                }
                continue;
            }
            var layout = tier.NumberOfColumns is int columns && tier.Interleave is long interleave
                && interleave is >= 0 and <= int.MaxValue
                ? CanonicalLayout(new(tier.ResiliencySettingName, columns, (int)interleave,
                    tier.NumberOfDataCopies ?? 1, tier.PhysicalDiskRedundancy ?? 0)) : null;
            if (layout is null) continue;
            var size = useMaximum ? 0 : tier is { Size: > 0 } ? tier.Size : intent.VirtualDiskSizeBytes ?? 0;
            var verifiedId = tier is { IsStable: true }
                && session.Baseline.StorageTiers.Any(item => item.StableId == tier.StableId) ? tier.StableId : null;
            goals.Add(new(mediaType!, layout, size, useMaximum, verifiedId));
        }
        return goals;
    }

    private static bool DraftTierMatchesGoal(StorageTierInfo tier, PoolTierEditIntent goal,
        IReadOnlyList<PhysicalDiskInfo> dataMembers, bool validateSize)
    {
        if (tier.VirtualDiskStableId is not null || NormalizeMedia(tier.MediaType) != NormalizeMedia(goal.MediaType)
            || !SupportedTierLayout(tier, out var layout) || !SameLayout(layout!, goal.Layout)) return false;
        var expected = dataMembers.Where(item => NormalizeMedia(item.MediaType) == NormalizeMedia(goal.MediaType))
            .Select(item => item.StableId).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return tier.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected)
            && (!validateSize || goal.UseMaximumSize || tier.Size == goal.SizeBytes);
    }

    private static bool ExistingTemplateMatchesGoal(SimulationEditingSession session, StorageTierInfo tier,
        StoragePoolInfo pool, PoolTierEditIntent goal, IReadOnlyList<PhysicalDiskInfo> dataMembers)
    {
        if (tier.PoolStableId != pool.StableId || tier.VirtualDiskStableId is not null
            || NormalizeMedia(tier.MediaType) != NormalizeMedia(goal.MediaType)
            || !SupportedObservedTier(session, tier) || !SupportedTierLayout(tier, out var layout)
            || !SameLayout(layout!, goal.Layout)) return false;
        var expected = dataMembers.Where(item => NormalizeMedia(item.MediaType) == NormalizeMedia(goal.MediaType))
            .Select(item => item.StableId).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return tier.MemberPhysicalDiskIds.Count == 0
            || tier.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected);
    }

    private static bool IsUnusedTemplateRoleProjection(SimulationEditingSession session,
        StorageTierInfo beforeTemplate, StorageTierInfo afterTemplate)
    {
        if (beforeTemplate.VirtualDiskStableId is not null || afterTemplate.VirtualDiskStableId is not null
            || beforeTemplate.PoolStableId is null || beforeTemplate.PoolStableId != afterTemplate.PoolStableId
            || !SupportedObservedTier(session, beforeTemplate)) return false;

        var pool = session.Baseline.StoragePools.SingleOrDefault(item => item.StableId == beforeTemplate.PoolStableId);
        var currentPool = session.Working.StoragePools.SingleOrDefault(item => item.StableId == beforeTemplate.PoolStableId);
        if (pool is not { IsStable: true, IsPrimordial: false }
            || currentPool is null
            || !pool.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(currentPool.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase))
            || session.Baseline.VirtualDisks.Any(item => item.PoolStableId == pool.StableId)
            || session.Working.VirtualDisks.Any(item => item.PoolStableId == pool.StableId
                && !EditWorkspace.IsDraftVirtualDisk(item.StableId))) return false;

        foreach (var memberId in pool.MemberPhysicalDiskIds)
        {
            var original = session.Baseline.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
            var current = session.Working.PhysicalDisks.SingleOrDefault(item => item.StableId == memberId);
            if (original is null || current is null
                || NormalizeMemberRole(original.Usage) is null || NormalizeMemberRole(current.Usage) is null) return false;
        }

        // An unused template can be reported with no member edges. The editor
        // projects the pool's currently eligible members onto that template,
        // including when no member role changed in this draft.
        var expectedProjection = pool.MemberPhysicalDiskIds
            .Select(id => session.Working.PhysicalDisks.SingleOrDefault(item => item.StableId == id))
            .Where(item => item is not null
                && NormalizeMedia(item.MediaType) == NormalizeMedia(beforeTemplate.MediaType)
                && PhysicalDiskUsage.ContributesDataCapacity(item.Usage))
            .Select(item => item!.StableId)
            .Order(StringComparer.OrdinalIgnoreCase);
        return afterTemplate.MemberPhysicalDiskIds.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(expectedProjection);
    }

    private static bool HasVerifiedTemplate(SimulationEditingSession session, StoragePoolInfo pool,
        PoolTierEditIntent goal, IReadOnlyList<PhysicalDiskInfo> dataMembers)
    {
        var existing = session.Baseline.StorageTiers.Where(item => item.PoolStableId == pool.StableId
            && item.VirtualDiskStableId is null && NormalizeMedia(item.MediaType) == NormalizeMedia(goal.MediaType)).ToArray();
        if (goal.VerifiedTemplateId is not null)
        {
            var named = existing.SingleOrDefault(item => item.StableId == goal.VerifiedTemplateId);
            if (named is not null)
                return ExistingTemplateMatchesGoal(session, named, pool, goal, dataMembers);
        }
        return existing.Any(item => ExistingTemplateMatchesGoal(session, item, pool, goal, dataMembers));
    }

    private static bool SameLayout(StorageLayoutParameters first, StorageLayoutParameters second) =>
        CanonicalLayout(first) is { } a && CanonicalLayout(second) is { } b && a == b;

    private static string? NormalizeMedia(string? media) => (media ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "HDD" => "HDD",
        "SSD" => "SSD",
        "SCM" => "SCM",
        _ => null
    };

    private static string? NormalizeMemberRole(string? usage) => PhysicalDiskUsage.Normalize(usage) switch
    {
        PhysicalDiskUsage.AutoSelect => PhysicalDiskUsage.AutoSelect,
        PhysicalDiskUsage.HotSpare => PhysicalDiskUsage.HotSpare,
        PhysicalDiskUsage.Retired => PhysicalDiskUsage.Retired,
        _ => null
    };

    private static string FormatLayout(StorageLayoutParameters layout, bool chinese) => chinese
        ? $"{CanonicalLayout(layout)?.ResiliencySettingName ?? layout.ResiliencySettingName}、{layout.NumberOfColumns} 列、{FormatClusterSize(layout.InterleaveBytes)}、{layout.NumberOfDataCopies} 份数据、容忍 {layout.PhysicalDiskRedundancy} 次故障"
        : $"{CanonicalLayout(layout)?.ResiliencySettingName ?? layout.ResiliencySettingName}, {layout.NumberOfColumns} columns, {FormatClusterSize(layout.InterleaveBytes)}, {layout.NumberOfDataCopies} copies, {layout.PhysicalDiskRedundancy} tolerated failures";

    private static string FormatCapacity(long bytes, bool useMaximum, bool chinese) => useMaximum
        ? chinese ? "最大容量（MAX，核对后显示实际容量）" : "maximum capacity (MAX; actual capacity shown after verification)"
        : chinese ? $"准确容量 {bytes} bytes" : $"exact capacity {bytes} bytes";

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
