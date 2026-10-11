using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>One confirmed structural goal, scoped to one Apply invocation. It is not a general real-mode grant.</summary>
public sealed class RealStructureApplyAuthorization
{
    private readonly SystemId systemId;
    private readonly long generation;
    private readonly string productSessionId;
    private readonly RealPoolCreationDraft? creation;
    private readonly HashSet<string> removablePools;
    private readonly HashSet<string> removableVds;
    private readonly HashSet<string> removableTiers;
    private readonly HashSet<StorageObjectId> known = [];
    private readonly HashSet<string> completedCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> dataPartitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> providerMsrs = new(StringComparer.Ordinal);
    private string? poolId;
    private string? tierId;
    private string? vdId;
    private string? osDiskId;
    private long osDiskSize;
    private string? directDiskId;
    private string? physicalFingerprint;
    private string? postFingerprint;
    private string? machineBinding;
    private string? agentSessionBinding;
    private OperationId? pendingOperation;
    private string? pendingPlanHash;
    private bool confirmed;
    private bool stopped;

    public StorageObjectId PhysicalTarget { get; }

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
            .Concat(draft.Creations.Select(item => item.PhysicalDiskId)).Distinct(StringComparer.Ordinal).ToArray();
        if (memberIds.Length != 1 || before.PhysicalDisks.SingleOrDefault(item => item.StableId == memberIds[0]) is not { IsStable: true })
            throw new InvalidDataException("One exact physical member is required for structural confirmation.");
        PhysicalTarget = Id(StorageObjectKind.PhysicalDisk, memberIds[0]);
        known.Add(PhysicalTarget);
        foreach (var id in removablePools) known.Add(Id(StorageObjectKind.StoragePool, id));
        foreach (var id in removableVds) known.Add(Id(StorageObjectKind.VirtualDisk, id));
        foreach (var id in removableTiers) known.Add(Id(StorageObjectKind.StorageTier, id));
        directDiskId = before.OsDisks.SingleOrDefault(item => item.PhysicalDiskStableId == PhysicalTarget.ProviderKey && item.VirtualDiskStableId is null)?.StableId;
        if (directDiskId is not null) known.Add(Id(StorageObjectKind.OsDisk, directDiskId));
        if (creation is null) return;
        if (!creation.CreatePool)
        {
            poolId = creation.Pool.StableId;
            known.Add(Id(StorageObjectKind.StoragePool, poolId));
            tierId = before.StorageTiers.SingleOrDefault(item => item.PoolStableId == poolId && item.VirtualDiskStableId is null)?.StableId;
            if (tierId is not null) known.Add(Id(StorageObjectKind.StorageTier, tierId));
        }
        if (creation.ContinueLayout)
        {
            vdId = creation.Intent.VerifiedVirtualDiskId;
            var disk = before.OsDisks.Single(item => item.VirtualDiskStableId == vdId);
            osDiskId = disk.StableId;
            osDiskSize = disk.Size;
            known.Add(Id(StorageObjectKind.VirtualDisk, vdId!));
            known.Add(Id(StorageObjectKind.OsDisk, osDiskId));
            foreach (var partition in before.Partitions.Where(item => item.OsDiskStableId == osDiskId))
            {
                known.Add(Id(StorageObjectKind.Partition, partition.StableId));
                if (IsMsr(partition)) providerMsrs.Add(partition.StableId);
                else dataPartitions.Add(partition.StableId);
            }
        }
    }

    public string Describe(RealStructureDraftPlan draft, WinPoolFacts? facts, bool chinese)
    {
        string T(string zh, string en) => chinese ? zh : en;
        var lines = new List<string> { T("请核对本次结构变更：", "Review this structural change:"),
            RealOperationConfirmationFormatter.FormatTarget(PhysicalTarget, chinese, facts) };
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
            if (goal.CreateTier)
                lines.Add(T($"• 按 HDD 成员自动创建容量层“{goal.Pool.FriendlyName}_HDD”：Simple / Fixed，1 列，64 KiB；层模板尚未分配容量。", $"• Automatically create HDD capacity tier “{goal.Pool.FriendlyName}_HDD”: Simple / Fixed, one column, 64 KiB; the template has no allocated capacity."));
            if (goal.CreateVirtualDisk || goal.ContinueLayout)
            {
                var capacity = intent.VirtualDiskUseMaximum ? "MAX" : RealVirtualDiskCreationCapacityResolver.FormatGigabytes(intent.VirtualDiskSizeBytes) + " GiB";
                var layout = intent.Layout == PoolVirtualDiskLayout.HddTiered ? T("单 HDD 分层", "single-HDD tiered") : T("普通", "ordinary");
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
        foreach (var step in proposal.Steps)
            if (completedCommands.Contains(Key(step.Command)) || !Allows(step.Command))
                throw new InvalidDataException("The proposal changes or repeats a confirmed structural action.");
    }

    public void ValidateFrozen(RealOperationIntentRequest proposal, AgentRealOperationResponse frozen)
    {
        ValidateProposal(proposal);
        var plan = frozen.Plan;
        var real = plan.RealOperation;
        if (frozen.State != RealOperationState.Prepared || real is null || !RealOperationValidator.IsValid(plan)
            || plan.PlanHash != OperationPlanHasher.Compute(plan)
            || plan.SystemId != systemId || plan.Intent != proposal.Intent
            || !plan.Targets.ToHashSet().SetEquals(proposal.Targets.Append(PhysicalTarget))
            || real.Steps.Count != proposal.Steps.Count
            || physicalFingerprint is not null && real.PhysicalMemberFingerprint != physicalFingerprint
            || postFingerprint is not null && real.TargetFingerprint != postFingerprint
            || machineBinding is not null && real.MachineBinding != machineBinding
            || agentSessionBinding is not null && real.SessionBinding != agentSessionBinding
            || pendingOperation is not null && (plan.OperationId != pendingOperation || plan.PlanHash != pendingPlanHash))
            throw new InvalidDataException("The frozen plan does not belong to this confirmed structural Apply.");
        for (var index = 0; index < proposal.Steps.Count; index++)
        {
            var expected = proposal.Steps[index];
            var actual = real.Steps[index];
            if (actual.Id != expected.Id || !actual.DependsOn.SequenceEqual(expected.DependsOn)
                || Key(WithoutFreshMaximum(actual.Command, real)) != Key(expected.Command))
                throw new InvalidDataException("The Agent changed the confirmed command, target or parameters.");
        }
        physicalFingerprint ??= real.PhysicalMemberFingerprint;
        machineBinding ??= real.MachineBinding;
        agentSessionBinding ??= real.SessionBinding;
        pendingOperation = plan.OperationId;
        pendingPlanHash = plan.PlanHash;
    }

    public void ObserveVerified(RealOperationIntentRequest proposal, AgentRealOperationResponse response, StorageSystemDocument fresh)
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
        foreach (var step in proposal.Steps)
        {
            var progress = response.Steps.Single(item => item.StepId == step.Id);
            using var evidence = JsonDocument.Parse(progress.ResultEvidence ?? "{}");
            var root = evidence.RootElement;
            if (Read(root, "PhysicalMemberFingerprint") != physicalFingerprint || Read(root, "PostFingerprint") is not { Length: > 0 } post)
                throw new InvalidDataException("Verified output lacks the confirmed physical member and post-state proof.");
            postFingerprint = post;
            string Output(StorageObjectKind kind, string field)
            {
                var id = Read(root, field);
                if (string.IsNullOrWhiteSpace(id) || fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == id) is not { HasReliableIdentity: true })
                    throw new InvalidDataException("The verified output is missing from fresh source facts.");
                known.Add(Id(kind, id));
                return id;
            }
            switch (step.Command)
            {
                case CreatePoolCommand:
                    poolId = Output(StorageObjectKind.StoragePool, "CreatedObjectId");
                    if (fresh.Snapshot.StoragePools.Single(item => item.StableId == poolId) is not { MemberPhysicalDiskIds.Count: 1 } pool || pool.MemberPhysicalDiskIds[0] != PhysicalTarget.ProviderKey)
                        throw new InvalidDataException("The verified pool has a different physical member.");
                    break;
                case CreateTierCommand:
                    tierId = Output(StorageObjectKind.StorageTier, "CreatedObjectId");
                    var template = fresh.Snapshot.StorageTiers.Single(item => item.StableId == tierId);
                    if (template.PoolStableId != poolId || template.VirtualDiskStableId is not null
                        || template.MediaType != "HDD" || template.ResiliencySettingName != "Simple"
                        || template.Interleave != 65536 || template.NumberOfColumns != 1
                        || template.NumberOfDataCopies is not (null or 1) || template.PhysicalDiskRedundancy is not (null or 0)
                        || template.Size != 0 || template.FootprintOnPool != 0)
                        throw new InvalidDataException("The verified template does not match the confirmed HDD tier goal.");
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
                    var direct = fresh.Snapshot.OsDisks.SingleOrDefault(item => item.PhysicalDiskStableId == PhysicalTarget.ProviderKey && item.VirtualDiskStableId is null);
                    if (direct is not null)
                    {
                        directDiskId = direct.StableId;
                        known.Add(Id(StorageObjectKind.OsDisk, directDiskId));
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
            ClearDiskCommand value => creation is { CreatePool: true } && !value.RemoveOem && Target(value.Disk, StorageObjectKind.OsDisk, directDiskId),
            CreatePoolCommand value => creation is { CreatePool: true } && poolId is null && value.Name == creation.Pool.FriendlyName && value.PhysicalDisk == RealTargetReference.ForExisting(PhysicalTarget),
            CreateTierCommand value => creation is not null && (creation.CreateTier || creation.CreateVirtualDisk)
                && intent!.Layout == PoolVirtualDiskLayout.HddTiered && tierId is null
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId) && value.Name == creation.Pool.FriendlyName + "_HDD" && value.InterleaveBytes == 65536 && value.DataColumns == 1,
            CreateVirtualDiskCommand value => creation is { CreateVirtualDisk: true } && vdId is null && intent!.Layout == PoolVirtualDiskLayout.Ordinary
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId) && value.Name == intent.VirtualDiskName && Capacity(value.SizeBytes, value.UseMaximumSize)
                && value.InterleaveBytes == 65536 && value.DataColumns == 1 && value.MaximumCapacity is null,
            CreateTieredVirtualDiskCommand value => creation is { CreateVirtualDisk: true } && vdId is null && intent!.Layout == PoolVirtualDiskLayout.HddTiered
                && Target(value.Pool, StorageObjectKind.StoragePool, poolId) && Target(value.Tier, StorageObjectKind.StorageTier, tierId)
                && value.Name == intent.VirtualDiskName && Capacity(value.SizeBytes, value.UseMaximumSize) && value.CreationMechanism == TieredVirtualDiskCreationMechanism.ExactTemplate
                && value.MaximumCapacity is null && value.CapacityTiers is null,
            InitializeGptCommand value => intent is { AutoCreatePartition: true } && Target(value.Disk, StorageObjectKind.OsDisk, osDiskId),
            DeletePartitionCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && providerMsrs.Contains(partition.ProviderKey),
            CreatePartitionCommand value => intent is { AutoCreatePartition: true } && osDiskId is not null && osDiskSize > 0
                && RealOperationProposalFactory.ConfigureInitializedDisk(systemId, Id(StorageObjectKind.OsDisk, osDiskId), null, intent.CreateMsr, osDiskSize)!
                    .Steps.Any(step => step.Command == value),
            FormatVolumeCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && dataPartitions.Contains(partition.ProviderKey)
                && value.FileSystem == RealFileSystem.Ntfs && intent.FileSystem == "NTFS" && value.ClusterBytes == intent.AllocationUnitSize
                && value.Full == !intent.QuickFormat && value.Label == intent.VolumeName,
            SetDriveLetterCommand value => intent is { AutoCreatePartition: true } && value.Partition.Existing is { } partition && dataPartitions.Contains(partition.ProviderKey)
                && value.PreviousLetter is null && value.NewLetter == intent.DriveLetter && value.NewLetter is not null,
            _ => false
        };
    }

    private static RealStorageCommand WithoutFreshMaximum(RealStorageCommand command, RealOperationSpecification real)
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
        if (command is CreateTieredVirtualDiskCommand { UseMaximumSize: true } tiered)
        {
            Policy(tiered.MaximumCapacity);
            if (tiered.CapacityTiers is not null) throw new InvalidDataException("Additional tier targets were not confirmed.");
            return tiered with { MaximumCapacity = null };
        }
        return command;
    }

    private StorageObjectId Id(StorageObjectKind kind, string key) => new(systemId, kind, key);
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
