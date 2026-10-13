using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>One confirmed structural goal, scoped to one Apply invocation. It is not a general real-mode grant.</summary>
public sealed class RealStructureApplyAuthorization : IRealOperationAuthorization
{
    private readonly SystemId systemId;
    private readonly long generation;
    private readonly string productSessionId;
    private readonly RealPoolCreationDraft? creation;
    private readonly HashSet<string> removablePools;
    private readonly HashSet<string> removableVds;
    private readonly HashSet<string> removableTiers;
    private readonly HashSet<StorageObjectId> known = [];
    private readonly Dictionary<StorageObjectId, string> knownScopes = [];
    private readonly HashSet<string> completedCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> dataPartitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> providerMsrs = new(StringComparer.Ordinal);
    private readonly HashSet<string> directDiskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> directDiskPhysicalIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> tierIds = new(StringComparer.Ordinal);
    private string? poolId;
    private string? tierId;
    private string? vdId;
    private string? osDiskId;
    private long osDiskSize;
    private string? directDiskId;
    private readonly Dictionary<string, string> physicalFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> postFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> memberScopes = new(StringComparer.Ordinal);
    private string? machineBinding;
    private string? agentSessionBinding;
    private OperationId? pendingOperation;
    private string? pendingPlanHash;
    private bool confirmed;
    private bool stopped;

    public StorageObjectId PhysicalTarget { get; }
    public IReadOnlyList<StorageObjectId> PhysicalTargets { get; }

    public RealStructureApplyAuthorization(RealStructureDraftPlan draft, SimulationEditingSession session, string productSessionId)
    {
        if (!draft.Preview.CanApply || session.HasBaselineConflict || session.OutcomeUnknown)
            throw new InvalidOperationException("The structural goal is not applicable.");
        systemId = session.SystemId;
        generation = session.BindingGeneration;
        this.productSessionId = productSessionId;
        creation = draft.Creations.SingleOrDefault();
        var before = session.Baseline;
        removablePools = draft.RemovedPools.Select(item => item.StableId).ToHashSet(StringComparer.Ordinal);
        removableVds = draft.RemovedVirtualDisks.Select(item => item.StableId)
            .Concat(before.VirtualDisks.Where(item => removablePools.Contains(item.PoolStableId ?? "")).Select(item => item.StableId))
            .ToHashSet(StringComparer.Ordinal);
        removableTiers = draft.RemovedTiers.Select(item => item.StableId)
            .Concat(before.StorageTiers.Where(item => removablePools.Contains(item.PoolStableId ?? "") && item.VirtualDiskStableId is null).Select(item => item.StableId))
            .ToHashSet(StringComparer.Ordinal);
        var memberIds = draft.RemovedPools.SelectMany(item => item.MemberPhysicalDiskIds)
            .Concat(draft.RemovedVirtualDisks.SelectMany(item => before.StoragePools.Single(pool => pool.StableId == item.PoolStableId).MemberPhysicalDiskIds))
            .Concat(draft.RemovedTiers.SelectMany(item => before.StoragePools.Single(pool => pool.StableId == item.PoolStableId).MemberPhysicalDiskIds))
            .Concat(draft.Creations.SelectMany(item => item.Pool.MemberPhysicalDiskIds)).Distinct(StringComparer.Ordinal).ToArray();
        if (memberIds.Length == 0 || memberIds.Any(id => before.PhysicalDisks.SingleOrDefault(item => item.StableId == id)
                is not { IsStable: true, IsBoot: false, IsSystem: false, IsPageFile: false, IsCrashDump: false }))
            throw new InvalidDataException("Every confirmed physical member needs an exact safe source identity.");
        PhysicalTargets = memberIds.Select(id => Id(StorageObjectKind.PhysicalDisk, id)).ToArray();
        PhysicalTarget = Id(StorageObjectKind.PhysicalDisk, memberIds[0]);
        foreach (var member in PhysicalTargets) known.Add(member);
        foreach (var pool in draft.RemovedPools) memberScopes.Add(MemberScope(pool.MemberPhysicalDiskIds));
        foreach (var vd in draft.RemovedVirtualDisks) memberScopes.Add(MemberScope(before.StoragePools.Single(pool => pool.StableId == vd.PoolStableId).MemberPhysicalDiskIds));
        foreach (var tier in draft.RemovedTiers) memberScopes.Add(MemberScope(before.StoragePools.Single(pool => pool.StableId == tier.PoolStableId).MemberPhysicalDiskIds));
        foreach (var goal in draft.Creations) memberScopes.Add(MemberScope(goal.Pool.MemberPhysicalDiskIds));
        foreach (var id in removablePools) known.Add(Id(StorageObjectKind.StoragePool, id));
        foreach (var id in removableVds) known.Add(Id(StorageObjectKind.VirtualDisk, id));
        foreach (var id in removableTiers) known.Add(Id(StorageObjectKind.StorageTier, id));
        foreach (var pool in before.StoragePools.Where(item => removablePools.Contains(item.StableId)))
            knownScopes[Id(StorageObjectKind.StoragePool, pool.StableId)] = MemberScope(pool.MemberPhysicalDiskIds);
        foreach (var vd in before.VirtualDisks.Where(item => removableVds.Contains(item.StableId)))
            knownScopes[Id(StorageObjectKind.VirtualDisk, vd.StableId)] = MemberScope(before.StoragePools.Single(pool => pool.StableId == vd.PoolStableId).MemberPhysicalDiskIds);
        foreach (var tier in before.StorageTiers.Where(item => removableTiers.Contains(item.StableId)))
            knownScopes[Id(StorageObjectKind.StorageTier, tier.StableId)] = MemberScope(before.StoragePools.Single(pool => pool.StableId == tier.PoolStableId).MemberPhysicalDiskIds);
        foreach (var member in PhysicalTargets)
        {
            var directId = before.OsDisks.SingleOrDefault(item => item.PhysicalDiskStableId == member.ProviderKey && item.VirtualDiskStableId is null)?.StableId;
            if (directId is null) continue;
            directDiskIds.Add(directId);
            directDiskPhysicalIds[directId] = member.ProviderKey;
            known.Add(Id(StorageObjectKind.OsDisk, directId));
        }
        directDiskId = directDiskIds.FirstOrDefault();
        if (creation is null) return;
        if (!creation.CreatePool)
        {
            poolId = creation.Pool.StableId;
            known.Add(Id(StorageObjectKind.StoragePool, poolId));
            knownScopes[Id(StorageObjectKind.StoragePool, poolId)] = MemberScope(creation.Pool.MemberPhysicalDiskIds);
            foreach (var tier in before.StorageTiers.Where(item => item.PoolStableId == poolId && item.VirtualDiskStableId is null))
            {
                if (!tierIds.TryAdd(tier.MediaType, tier.StableId)) throw new InvalidDataException("The confirmed pool has ambiguous media templates.");
                known.Add(Id(StorageObjectKind.StorageTier, tier.StableId));
                knownScopes[Id(StorageObjectKind.StorageTier, tier.StableId)] = MemberScope(creation.Pool.MemberPhysicalDiskIds);
            }
            tierId = tierIds.Values.FirstOrDefault();
        }
        if (creation.ContinueLayout)
        {
            vdId = creation.Intent.VerifiedVirtualDiskId;
            var disk = before.OsDisks.Single(item => item.VirtualDiskStableId == vdId);
            osDiskId = disk.StableId;
            osDiskSize = disk.Size;
            known.Add(Id(StorageObjectKind.VirtualDisk, vdId!));
            known.Add(Id(StorageObjectKind.OsDisk, osDiskId));
            knownScopes[Id(StorageObjectKind.VirtualDisk, vdId!)] = MemberScope(creation.Pool.MemberPhysicalDiskIds);
            knownScopes[Id(StorageObjectKind.OsDisk, osDiskId)] = MemberScope(creation.Pool.MemberPhysicalDiskIds);
            foreach (var partition in before.Partitions.Where(item => item.OsDiskStableId == osDiskId))
            {
                known.Add(Id(StorageObjectKind.Partition, partition.StableId));
                knownScopes[Id(StorageObjectKind.Partition, partition.StableId)] = MemberScope(creation.Pool.MemberPhysicalDiskIds);
                if (IsMsr(partition)) providerMsrs.Add(partition.StableId);
                else dataPartitions.Add(partition.StableId);
            }
        }
    }

    public string Describe(RealStructureDraftPlan draft, WinPoolFacts? facts, bool chinese)
    {
        string T(string zh, string en) => chinese ? zh : en;
        var lines = new List<string> { T("请核对本次结构变更：", "Review this structural change:") };
        lines.AddRange(PhysicalTargets.Select(target => RealOperationConfirmationFormatter.FormatTarget(target, chinese, facts)));
        foreach (var pool in draft.RemovedPools)
            lines.Add(T($"• 解散池“{pool.FriendlyName}”；其中虚拟磁盘、分区、卷和全部数据将丢失。", $"• Dissolve pool “{pool.FriendlyName}”; its virtual disks, partitions, volumes and all data will be lost."));
        foreach (var vd in draft.RemovedVirtualDisks)
            lines.Add(T($"• 删除虚拟磁盘“{vd.FriendlyName}”及其分区、卷和全部数据。", $"• Delete virtual disk “{vd.FriendlyName}”, its partitions, volumes and all data."));
        foreach (var tier in draft.RemovedTiers)
            lines.Add(T($"• 删除未使用的存储层“{tier.FriendlyName}”。", $"• Delete unused storage tier “{tier.FriendlyName}”."));
        if (creation is { } goal)
        {
            var intent = goal.Intent;
            if (goal.CreatePool)
                lines.Add(T($"• 创建池“{goal.Pool.FriendlyName}”；入池所需的清空会使该物理磁盘上的现有分区和数据丢失。", $"• Create pool “{goal.Pool.FriendlyName}”; clearing required to join the pool will destroy existing partitions and data on this physical disk."));
            foreach (var tier in TierGoals())
                lines.Add(T($"• {tier.MediaType} 层：{tier.Layout.ResiliencySettingName} / Fixed，{tier.Layout.NumberOfColumns} 列，{tier.Layout.InterleaveBytes / 1024} KiB，副本 {tier.Layout.NumberOfDataCopies}，容错 {tier.Layout.PhysicalDiskRedundancy}；容量 {(tier.UseMaximumSize ? "MAX" : (tier.SizeBytes / MaximumCapacityAlgorithm.GiB).ToString() + " GiB")}。",
                    $"• {tier.MediaType} tier: {tier.Layout.ResiliencySettingName} / Fixed, {tier.Layout.NumberOfColumns} columns, {tier.Layout.InterleaveBytes / 1024} KiB, {tier.Layout.NumberOfDataCopies} copies, {tier.Layout.PhysicalDiskRedundancy} redundancy; {(tier.UseMaximumSize ? "MAX" : (tier.SizeBytes / MaximumCapacityAlgorithm.GiB).ToString() + " GiB")} capacity."));
            foreach (var member in intent.MemberUsages ?? [])
                lines.Add(T($"• 成员 {member.PhysicalDiskId}：{member.Usage}。", $"• Member {member.PhysicalDiskId}: {member.Usage}."));
            if (goal.CreateVirtualDisk || goal.ContinueLayout)
            {
                var capacity = intent.VirtualDiskUseMaximum ? "MAX" : RealVirtualDiskCreationCapacityResolver.FormatGigabytes(intent.VirtualDiskSizeBytes) + " GiB";
                var layout = intent.Layout == PoolVirtualDiskLayout.HddTiered ? T("按介质分层", "media-tiered") : T("普通", "ordinary");
                lines.Add(T($"• 虚拟磁盘“{intent.VirtualDiskName}”：{layout}，{capacity}。", $"• Virtual disk “{intent.VirtualDiskName}”: {layout}, {capacity}."));
                if (intent.AutoCreatePartition)
                    lines.Add(T($"• 自动创建 GPT 分区：{(intent.CreateMsr ? "16 MiB MSR" : "无 MSR")}，其余空间一个数据分区；{intent.FileSystem} / {intent.AllocationUnitSize / 1024} KiB，{(intent.QuickFormat ? "快速格式化" : "完整格式化")}，卷标“{intent.VolumeName}”，盘符 {intent.DriveLetter?.ToString() ?? "无"}。", $"• Automatic GPT layout: {(intent.CreateMsr ? "16 MiB MSR" : "no MSR")}, one data partition in the remaining space; {intent.FileSystem} / {intent.AllocationUnitSize / 1024} KiB, {(intent.QuickFormat ? "quick format" : "full format")}, label “{intent.VolumeName}”, drive letter {intent.DriveLetter?.ToString() ?? "none"}."));
                else lines.Add(T("• 不自动创建分区。", "• Do not automatically create partitions."));
            }
        }
        lines.Add(T("已完成的更改不会自动回滚；执行失败或结果待核对时停止后续操作。", "Completed changes are not automatically rolled back; failure or an uncertain result stops further operations."));
        return string.Join(Environment.NewLine, lines);
    }

    public void Confirm() => confirmed = !stopped;

    public void ValidateContext(SystemId system, string session, long bindingGeneration, bool conflict, bool unknown)
    {
        if (stopped || system != systemId || session != productSessionId || bindingGeneration != generation || conflict || unknown)
            throw new InvalidOperationException("The confirmed structural Apply context changed; further writes are stopped.");
    }

    public void ValidateProposal(RealOperationIntentRequest proposal)
    {
        if (!confirmed || stopped || proposal.SystemId != systemId || proposal.Targets.Any(target => !known.Contains(target)))
            throw new InvalidDataException("The proposal expands the confirmed structural targets.");
        RealOperationValidator.Validate(proposal);
        _ = ProposalMemberScope(proposal);
        foreach (var step in proposal.Steps)
            if (completedCommands.Contains(Key(step.Command)) || !Allows(step.Command))
                throw new InvalidDataException("The proposal changes or repeats a confirmed structural action.");
    }

    public void ValidateFrozen(RealOperationIntentRequest proposal, AgentRealOperationResponse frozen)
    {
        ValidateProposal(proposal);
        var plan = frozen.Plan;
        var real = plan.RealOperation;
        var scope = ProposalMemberScope(proposal);
        if (frozen.State != RealOperationState.Prepared || real is null || !RealOperationValidator.IsValid(plan)
            || plan.PlanHash != OperationPlanHasher.Compute(plan)
            || plan.SystemId != systemId || plan.Intent != proposal.Intent
            || !plan.Targets.ToHashSet().SetEquals(proposal.Targets.Any(target => target.Kind == StorageObjectKind.PhysicalDisk)
                ? proposal.Targets : proposal.Targets.Append(PhysicalTarget))
            || real.Steps.Count != proposal.Steps.Count
            || physicalFingerprints.TryGetValue(scope, out var physicalFingerprint) && real.PhysicalMemberFingerprint != physicalFingerprint
            || postFingerprints.TryGetValue(scope, out var postFingerprint) && real.TargetFingerprint != postFingerprint
            || machineBinding is not null && real.MachineBinding != machineBinding
            || agentSessionBinding is not null && real.SessionBinding != agentSessionBinding
            || pendingOperation is not null && (plan.OperationId != pendingOperation || plan.PlanHash != pendingPlanHash))
            throw new InvalidDataException("The frozen plan does not belong to this confirmed structural Apply.");
        for (var index = 0; index < proposal.Steps.Count; index++)
        {
            var expected = proposal.Steps[index];
            var actual = real.Steps[index];
            if (actual.Id != expected.Id || !actual.DependsOn.SequenceEqual(expected.DependsOn)
                || Key(WithoutFreshMaximum(actual.Command, expected.Command, real)) != Key(expected.Command))
                throw new InvalidDataException("The Agent changed the confirmed command, target or parameters.");
        }
        physicalFingerprints.TryAdd(scope, real.PhysicalMemberFingerprint);
        machineBinding ??= real.MachineBinding;
        agentSessionBinding ??= real.SessionBinding;
        pendingOperation = plan.OperationId;
        pendingPlanHash = plan.PlanHash;
    }

    public void ObserveVerified(RealOperationIntentRequest proposal, AgentRealOperationResponse response, StorageSystemDocument fresh)
    {
        try { ObserveVerifiedCore(proposal, response, fresh); }
        catch { stopped = true; confirmed = false; throw; }
    }

    private void ObserveVerifiedCore(RealOperationIntentRequest proposal, AgentRealOperationResponse response, StorageSystemDocument fresh)
    {
        if (response.State != RealOperationState.Succeeded || response.RequiresReconciliation || fresh.SystemId != systemId
            || response.Plan.OperationId != pendingOperation || response.Plan.PlanHash != pendingPlanHash
            || response.Steps.Count != proposal.Steps.Count || response.Steps.Any(step => step.State != RealOperationStepState.Verified))
        {
            stopped = true;
            throw new InvalidDataException("Only a complete verified result can advance structural authorization.");
        }
        // A receipt is usable only for the exact submitted commands. Agent-added MAX proof is compared separately.
        var prepared = response with { State = RealOperationState.Prepared };
        ValidateFrozen(proposal, prepared);
        var scope = ProposalMemberScope(proposal);
        var physicalFingerprint = physicalFingerprints[scope];
        foreach (var step in proposal.Steps)
        {
            var progress = response.Steps.Single(item => item.StepId == step.Id);
            using var evidence = JsonDocument.Parse(progress.ResultEvidence ?? "{}");
            var root = evidence.RootElement;
            if (Read(root, "PhysicalMemberFingerprint") != physicalFingerprint || Read(root, "PostFingerprint") is not { Length: > 0 } post)
                throw new InvalidDataException("Verified output lacks the confirmed physical member and post-state proof.");
            postFingerprints[scope] = post;
            string Output(StorageObjectKind kind, string field)
            {
                var id = Read(root, field);
                if (string.IsNullOrWhiteSpace(id) || fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == id) is not { HasReliableIdentity: true })
                    throw new InvalidDataException("The verified output is missing from fresh source facts.");
                known.Add(Id(kind, id));
                knownScopes[Id(kind, id)] = scope;
                return id;
            }
            switch (step.Command)
            {
                case CreatePoolCommand:
                    poolId = Output(StorageObjectKind.StoragePool, "CreatedObjectId");
                    if (creation is null || !fresh.Snapshot.StoragePools.Single(item => item.StableId == poolId)
                            .MemberPhysicalDiskIds.ToHashSet(StringComparer.Ordinal).SetEquals(creation.Pool.MemberPhysicalDiskIds))
                        throw new InvalidDataException("The verified pool has a different physical member set.");
                    break;
                case SetPhysicalDiskUsageCommand usage:
                    if (usage.PhysicalDisk.Existing is not { } physical
                        || fresh.Snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == physical.ProviderKey) is not { } member
                        || member.PoolStableId != poolId || PhysicalDiskUsage.Normalize(member.Usage) != PhysicalDiskUsage.Normalize(usage.Usage))
                        throw new InvalidDataException("The verified member role differs from the confirmed goal.");
                    break;
                case CreateTierCommand tierCommand:
                    tierId = Output(StorageObjectKind.StorageTier, "CreatedObjectId");
                    var template = fresh.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
                    if (template.PoolStableId != poolId || template.VirtualDiskStableId is not null
                        || template.MediaType != tierCommand.EffectiveMediaType || !LayoutMatches(template, tierCommand.EffectiveLayout)
                        || template.Size != 0 || template.FootprintOnPool != 0)
                        throw new InvalidDataException("The verified template does not match the confirmed media and layout.");
                    if (!tierIds.TryAdd(template.MediaType, tierId))
                        throw new InvalidDataException("A media template was already verified for this goal.");
                    break;
                case CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand:
                    vdId = Output(StorageObjectKind.VirtualDisk, "CreatedObjectId");
                    osDiskId = Output(StorageObjectKind.OsDisk, "CreatedOsDiskId");
                    var disk = fresh.Snapshot.OsDisks.Single(item => item.StableId == osDiskId);
                    if (disk.VirtualDiskStableId != vdId || fresh.Snapshot.VirtualDisks.Single(item => item.StableId == vdId).PoolStableId != poolId)
                        throw new InvalidDataException("The verified VD or OS disk belongs to another target.");
                    osDiskSize = disk.Size;
                    break;
                case InitializeGptCommand:
                    if (root.TryGetProperty("GptInitialization", out var gpt) && Read(gpt, "ProviderMsrStableId") is { Length: > 0 } msr)
                    {
                        if (Read(gpt, "OsDiskStableId") != osDiskId || fresh.Snapshot.Partitions.SingleOrDefault(item => item.StableId == msr) is not { } partition
                            || partition.OsDiskStableId != osDiskId || !IsMsr(partition))
                            throw new InvalidDataException("The provider MSR is not a verified output on the confirmed disk.");
                        providerMsrs.Add(msr);
                        known.Add(Id(StorageObjectKind.Partition, msr));
                        knownScopes[Id(StorageObjectKind.Partition, msr)] = scope;
                    }
                    break;
                case CreatePartitionCommand create:
                    var partitionId = Output(StorageObjectKind.Partition, "CreatedObjectId");
                    var created = fresh.Snapshot.Partitions.Single(item => item.StableId == partitionId);
                    if (created.OsDiskStableId != osDiskId || created.Offset != create.OffsetBytes || created.Size != create.SizeBytes)
                        throw new InvalidDataException("The verified partition has a different geometry or parent.");
                    if (create.Role == RealPartitionRole.BasicData) dataPartitions.Add(partitionId);
                    break;
                case DeletePoolCommand:
                    // A released physical OS disk is observed, not invented as a creation output.
                    // The next frozen plan must retain this verified post-fingerprint.
                    foreach (var target in PhysicalTargets)
                    {
                        var direct = fresh.Snapshot.OsDisks.SingleOrDefault(item => item.PhysicalDiskStableId == target.ProviderKey && item.VirtualDiskStableId is null);
                        if (direct is null) continue;
                        directDiskId = direct.StableId;
                        directDiskIds.Add(direct.StableId);
                        directDiskPhysicalIds[direct.StableId] = target.ProviderKey;
                        known.Add(Id(StorageObjectKind.OsDisk, direct.StableId));
                    }
                    break;
            }
            completedCommands.Add(Key(step.Command));
        }
        pendingOperation = null;
        pendingPlanHash = null;
    }

    public void Stop() { stopped = true; confirmed = false; }

    private bool Allows(RealStorageCommand command)
    {
        bool Target(RealTargetReference target, StorageObjectKind kind, string? id) => id is not null && target == RealTargetReference.ForExisting(Id(kind, id));
        var intent = creation?.Intent;
        bool Capacity(long bytes, bool maximum) => intent is not null && maximum == intent.VirtualDiskUseMaximum
            && bytes == (maximum ? 0 : intent.VirtualDiskSizeBytes);
        return command switch
        {
            DeletePoolCommand value => value.Pool.Existing is { } pool && removablePools.Contains(pool.ProviderKey),
            DeleteVirtualDiskCommand value => value.VirtualDisk.Existing is { } vd && removableVds.Contains(vd.ProviderKey),
            DeleteTierCommand value => value.Tier.Existing is { } tier && removableTiers.Contains(tier.ProviderKey),
            ClearDiskCommand value => creation is { CreatePool: true } && !value.RemoveOem
                && value.Disk.Existing is { } disk && directDiskIds.Contains(disk.ProviderKey)
                && directDiskPhysicalIds.TryGetValue(disk.ProviderKey, out var physicalId) && creation.Pool.MemberPhysicalDiskIds.Contains(physicalId),
            CreatePoolCommand value => creation is { CreatePool: true } && poolId is null && value.Name == creation.Pool.FriendlyName
                && value.EffectiveMembers.Select(member => member.Existing?.ProviderKey ?? "").ToHashSet(StringComparer.Ordinal)
                    .SetEquals(creation.Pool.MemberPhysicalDiskIds),
            SetPhysicalDiskUsageCommand value => creation is not null && vdId is null && poolId is not null
                && value.PhysicalDisk.Existing is { } member
                && creation.Pool.MemberPhysicalDiskIds.Contains(member.ProviderKey)
                && (intent!.MemberUsages ?? []).Any(goal => goal.PhysicalDiskId == member.ProviderKey
                    && PhysicalDiskUsage.Normalize(goal.Usage) == PhysicalDiskUsage.Normalize(value.Usage)),
            CreateTierCommand value => creation is not null && (creation.CreateTier || creation.CreateVirtualDisk)
                && intent!.Layout == PoolVirtualDiskLayout.HddTiered && !tierIds.ContainsKey(value.EffectiveMediaType)
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId)
                && TierGoals().Any(goal => goal.MediaType == value.EffectiveMediaType && goal.Layout == value.EffectiveLayout
                    && value.Name == creation.Pool.FriendlyName + "_" + goal.MediaType),
            CreateVirtualDiskCommand value => creation is { CreateVirtualDisk: true } && vdId is null && intent!.Layout == PoolVirtualDiskLayout.Ordinary
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId) && value.Name == intent.VirtualDiskName && Capacity(value.SizeBytes, value.UseMaximumSize)
                && value.EffectiveLayout == (intent.Tiers?.FirstOrDefault()?.Layout ?? StorageLayoutParameters.LegacyDefault)
                && value.MaximumCapacity is null,
            CreateTieredVirtualDiskCommand value => creation is { CreateVirtualDisk: true } && vdId is null && intent!.Layout == PoolVirtualDiskLayout.HddTiered
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId)
                && value.Name == intent.VirtualDiskName && value.CreationMechanism == TieredVirtualDiskCreationMechanism.ExactTemplate
                && AllowsTieredCapacity(value),
            InitializeGptCommand value => intent is { AutoCreatePartition: true } && Target(value.Disk, StorageObjectKind.OsDisk, osDiskId),
            DeletePartitionCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && providerMsrs.Contains(partition.ProviderKey),
            CreatePartitionCommand value => intent is { AutoCreatePartition: true } && osDiskId is not null && osDiskSize > 0
                && RealOperationProposalFactory.ConfigureInitializedDisk(systemId, Id(StorageObjectKind.OsDisk, osDiskId), null, intent.CreateMsr, osDiskSize)!
                    .Steps.Any(step => step.Command == value),
            FormatVolumeCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && dataPartitions.Contains(partition.ProviderKey)
                && value.FileSystem == ParseFileSystem(intent.FileSystem) && value.ClusterBytes == intent.AllocationUnitSize
                && value.Full == !intent.QuickFormat && value.Label == intent.VolumeName,
            SetDriveLetterCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && dataPartitions.Contains(partition.ProviderKey)
                && value.PreviousLetter is null && value.NewLetter == intent.DriveLetter && value.NewLetter is not null,
            _ => false
        };
    }

    private static RealStorageCommand WithoutFreshMaximum(RealStorageCommand command, RealStorageCommand expected, RealOperationSpecification real)
    {
        void Policy(MaximumCapacityPolicy? policy)
        {
            if (policy is null || policy.SourceFingerprint != real.TargetFingerprint)
                throw new InvalidDataException("MAX proof does not describe the freshly frozen target.");
        }
        if (command is CreateVirtualDiskCommand { UseMaximumSize: true } ordinary)
        {
            Policy(ordinary.MaximumCapacity);
            return ordinary with { MaximumCapacity = null };
        }
        if (command is CreateTieredVirtualDiskCommand tiered && expected is CreateTieredVirtualDiskCommand requested)
        {
            if (tiered.UseMaximumSize) Policy(tiered.MaximumCapacity);
            if (requested.CapacityTiers is null)
            {
                if (tiered.CapacityTiers is { } normalized && (normalized.Count != 1
                    || normalized[0].Tier != requested.Tier || normalized[0].Layout != requested.EffectiveLayout
                    || normalized[0].UsesMaximumSize(tiered.UseMaximumSize) != requested.UseMaximumSize
                    || !requested.UseMaximumSize && normalized[0].SizeBytes != requested.SizeBytes))
                    throw new InvalidDataException("The normalized template changes the confirmed single-tier goal.");
                return tiered with { MaximumCapacity = null, CapacityTiers = null };
            }
            if (tiered.CapacityTiers is not { } actualTiers || actualTiers.Count != requested.CapacityTiers.Count)
                throw new InvalidDataException("The Agent changed the confirmed tier set.");
            var cleared = new List<MaximumCapacityTier>();
            for (var index = 0; index < actualTiers.Count; index++)
            {
                var actual = actualTiers[index];
                var input = requested.CapacityTiers[index];
                if (actual.UsesMaximumSize(tiered.UseMaximumSize)) Policy(actual.MaximumCapacity);
                else if (actual.MaximumCapacity is not null) throw new InvalidDataException("An explicit layer cannot acquire MAX authorization.");
                if (actual.Layout != (input.Layout ?? requested.EffectiveLayout)
                    || input.MediaType is not null && actual.MediaType != input.MediaType)
                    throw new InvalidDataException("The frozen per-tier layout differs from the confirmed layout.");
                cleared.Add(actual with { MaximumCapacity = null, Layout = input.Layout, MediaType = input.MediaType,
                    UseMaximumSize = input.UseMaximumSize });
            }
            return tiered with { MaximumCapacity = null, CapacityTiers = cleared };
        }
        return command;
    }

    private IReadOnlyList<PoolTierEditIntent> TierGoals() => creation?.Intent.Tiers
        ?? (creation?.Intent.Layout == PoolVirtualDiskLayout.HddTiered
            ? [new("HDD", StorageLayoutParameters.LegacyDefault, creation.Intent.VirtualDiskSizeBytes ?? 0,
                creation.Intent.VirtualDiskUseMaximum)] : []);

    private bool AllowsTieredCapacity(CreateTieredVirtualDiskCommand command)
    {
        if (command.MaximumCapacity is not null) return false;
        var goals = TierGoals();
        var inputs = command.CapacityTiers ?? [new(command.Tier, null,
            command.UseMaximumSize ? null : command.SizeBytes, command.UseMaximumSize, command.Layout)];
        if (inputs.Count != goals.Count || command.Tier != inputs[0].Tier
            || command.UseMaximumSize != goals.Any(goal => goal.UseMaximumSize)
            || inputs.Count > 1 && command.Layout is not null
            || inputs.Count == 1 && command.EffectiveLayout != goals[0].Layout
            || command.SizeBytes != (command.UseMaximumSize ? 0 : goals.Sum(goal => goal.SizeBytes))) return false;
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var goal = goals[index];
            if (!tierIds.TryGetValue(goal.MediaType, out var id) || input.Tier != RealTargetReference.ForExisting(Id(StorageObjectKind.StorageTier, id))
                || input.MaximumCapacity is not null || input.UsesMaximumSize(command.UseMaximumSize) != goal.UseMaximumSize
                || !goal.UseMaximumSize && input.SizeBytes != goal.SizeBytes
                || input.Layout is { } layout && layout != goal.Layout
                || input.MediaType is { } media && media != goal.MediaType) return false;
        }
        return true;
    }

    private static bool LayoutMatches(StorageTierInfo tier, StorageLayoutParameters layout) =>
        tier.ResiliencySettingName == layout.ResiliencySettingName && tier.NumberOfColumns == layout.NumberOfColumns
        && tier.Interleave == layout.InterleaveBytes
        && (tier.NumberOfDataCopies ?? (tier.ResiliencySettingName == "Simple" ? 1 : -1)) == layout.NumberOfDataCopies
        && (tier.PhysicalDiskRedundancy ?? (tier.ResiliencySettingName == "Simple" ? 0 : -1)) == layout.PhysicalDiskRedundancy;

    private StorageObjectId Id(StorageObjectKind kind, string key) => new(systemId, kind, key);
    private static string MemberScope(IEnumerable<string> ids) => JsonSerializer.Serialize(ids.Order(StringComparer.Ordinal).ToArray());
    private string ProposalMemberScope(RealOperationIntentRequest proposal)
    {
        var declared = proposal.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).Select(target => target.ProviderKey).ToArray();
        if (declared.Length == 0 && PhysicalTargets.Count == 1) declared = [PhysicalTarget.ProviderKey];
        var scope = MemberScope(declared);
        if (declared.Length == 0 || declared.Distinct(StringComparer.Ordinal).Count() != declared.Length || !memberScopes.Contains(scope))
            throw new InvalidDataException("Each stage must declare one complete confirmed physical member set.");
        if (proposal.Targets.Any(target => knownScopes.TryGetValue(target, out var targetScope) && targetScope != scope))
            throw new InvalidDataException("An existing target is declared with another confirmed member scope.");
        if (proposal.Steps.Any(step => step.Command is ClearDiskCommand or CreatePoolCommand or SetPhysicalDiskUsageCommand
                or CreateTierCommand or CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand
                or InitializeGptCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand)
            && creation is { } goal && scope != MemberScope(goal.Pool.MemberPhysicalDiskIds))
            throw new InvalidDataException("The creation stage must retain its complete confirmed physical member set.");
        return scope;
    }
    private static RealFileSystem? ParseFileSystem(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "NTFS" => RealFileSystem.Ntfs,
        "REFS" => RealFileSystem.ReFs,
        "EXFAT" => RealFileSystem.ExFat,
        _ => null
    };
    private static string Key(RealStorageCommand command) => JsonSerializer.Serialize(command);
    private static string? Read(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsMsr(PartitionInfo partition)
    {
        var msr = Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae");
        return (Guid.TryParse(partition.PartitionTypeId, out var role) && role == msr
                || Guid.TryParse(partition.GptType, out role) && role == msr)
            && (string.IsNullOrWhiteSpace(partition.PartitionTypeId) || Guid.TryParse(partition.PartitionTypeId, out role) && role == msr)
            && (string.IsNullOrWhiteSpace(partition.GptType) || Guid.TryParse(partition.GptType, out role) && role == msr)
            && string.IsNullOrWhiteSpace(partition.FileSystem);
    }
}
