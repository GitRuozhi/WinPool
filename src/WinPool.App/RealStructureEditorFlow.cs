using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool_App;

public partial class EditorPageBase
{
    private readonly Dictionary<SimulationEditingSession, RealAutomaticLayoutProgress> automaticLayoutProgress = new();
    protected RealStructureDraftPreview BuildRealStructureDraftPreview(SimulationEditingSession session) =>
        RealStructureDraftPlanner.Build(session,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn).Preview;

    protected async Task<bool> ApplyRealStructureDraftAsync(SimulationEditingSession session)
    {
        if (IsRealStructureApplyInProgress || !ViewModel.CanSubmitRealOperation
            || session.SystemId != ViewModel.ActiveDocument.SystemId) return false;
        if (ViewModel.RealStructureResults.HasPending(session)) return false;
        if (session.BaselineRevision != ViewModel.ActiveDocument.Revision
            || session.Baseline.SnapshotVersion != ViewModel.ActiveDocument.Snapshot.SnapshotVersion)
        {
            session.MarkBaselineConflict(ViewModel.ActiveDocument);
            return false;
        }
        var draft = RealStructureDraftPlanner.Build(session,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn);
        if (!draft.Preview.CanApply)
        {
            session.RealApplyMessage = string.Join(Environment.NewLine, draft.Preview.BlockingReasons);
            return false;
        }
        RealStructureApplyAuthorization authorization;
        try
        {
            authorization = new RealStructureApplyAuthorization(draft, session, ViewModel.RealProductSessionId);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            session.RealApplyMessage = exception.Message;
            PublishOperationException(Text("无法确认结构变更", "Cannot confirm structural changes"),
                "real", exception, "real.structure.confirmation_invalid");
            return false;
        }
        IsRealStructureApplyInProgress = true;
        var completed = new List<string>();
        try
        {
            // Reject known provider/layout limitations before deleting or clearing anything.
            foreach (var creation in draft.Creations.Where(item => item.CreatePool || item.CreateVirtualDisk || item.CreateTier))
            {
                ReportRealActivity(Text("正在核对目标布局支持", "Checking the target layout support"));
                var target = Id(StorageObjectKind.PhysicalDisk, creation.PhysicalDiskId);
                var tiered = (creation.CreateTier || creation.CreateVirtualDisk) && creation.Intent.Layout == PoolVirtualDiskLayout.HddTiered;
                var support = await ViewModel.AgentConnection!.SendAsync(new QueryAgentRealStructureCreationSupportRequest(
                    target, tiered, ViewModel.RealProductSessionId, CorrelationId.New()), CancellationToken.None);
                if (!support.IsSuccess || support.Value is not AgentRealStructureCreationSupportResponse actual
                    || actual.Support.PhysicalTarget != target || actual.Support.Tiered != tiered)
                    throw new InvalidDataException(support.Messages.FirstOrDefault()?.DiagnosticText
                        ?? support.Messages.FirstOrDefault()?.Code ?? "The requested layout is unsupported.");
            }
            ValidateApplyContext();
            if (!await ConfirmAsync(Text("确认真实结构变更", "Confirm real structural changes"),
                authorization.Describe(draft, session.BaselineSourceFacts,
                    ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn))) return false;
            ValidateApplyContext();
            authorization.Confirm();
            foreach (var pool in draft.RemovedPools)
            {
                var vds = session.Baseline.VirtualDisks.Where(vd => vd.PoolStableId == pool.StableId).ToArray();
                var tiers = session.Baseline.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                    && tier.VirtualDiskStableId is null).Select(tier => Id(StorageObjectKind.StorageTier, tier.StableId)).ToArray();
                if (!await Stage(RealOperationProposalFactory.DissolveSingleMemberPool(session.SystemId,
                    Id(StorageObjectKind.StoragePool, pool.StableId), vds.Length == 0 ? null : Id(StorageObjectKind.VirtualDisk, vds.Single().StableId), tiers),
                    Text($"已解散 {pool.FriendlyName}", $"Dissolved {pool.FriendlyName}"))) return false;
            }
            foreach (var vd in draft.RemovedVirtualDisks)
                if (!await Stage(RealOperationProposalFactory.OneStep(session.SystemId, OperationIntent.DeleteVirtualDisk,
                    Id(StorageObjectKind.VirtualDisk, vd.StableId),
                    new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(Id(StorageObjectKind.VirtualDisk, vd.StableId))),
                    "The exact virtual disk and its children are absent", "All files, partitions and volumes on the exact virtual disk are lost"),
                    Text($"已删除 VD {vd.FriendlyName}", $"Deleted VD {vd.FriendlyName}"))) return false;
            foreach (var tier in draft.RemovedTiers)
                if (!await Stage(RealOperationProposalFactory.DeleteHddTierTemplate(session.SystemId,
                    Id(StorageObjectKind.StorageTier, tier.StableId)),
                    Text($"已删除模板 {tier.FriendlyName}", $"Deleted template {tier.FriendlyName}"))) return false;

            foreach (var creation in draft.Creations)
            {
                var intent = creation.Intent;
                var poolId = creation.Pool.StableId;
                if (creation.CreatePool)
                {
                    var snapshot = ViewModel.ActiveDocument.Snapshot;
                    if (snapshot.StoragePools.Any(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Contains(creation.PhysicalDiskId)))
                        throw new InvalidDataException("The exact member still belongs to a concrete pool.");
                    var direct = snapshot.OsDisks.SingleOrDefault(disk => disk.PhysicalDiskStableId == creation.PhysicalDiskId
                        && disk.VirtualDiskStableId is null);
                    if (direct is not null && (!direct.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                        || snapshot.Partitions.Any(partition => partition.OsDiskStableId == direct.StableId)))
                    {
                        if (!await Stage(RealOperationProposalFactory.ClearToRaw(session.SystemId,
                            Id(StorageObjectKind.OsDisk, direct.StableId)), Text("已核对清空至 RAW", "Verified clear to RAW"))) return false;
                    }
                    var temporaryPoolId = poolId;
                    if (!await Stage(RealOperationProposalFactory.CreateSingleMemberPool(session.SystemId,
                        Id(StorageObjectKind.PhysicalDisk, creation.PhysicalDiskId), creation.Pool.FriendlyName, null),
                        Text("已核对新池", "Verified new pool"),
                        new(StorageObjectKind.StoragePool, temporaryPoolId, "create-pool"))) return false;
                    poolId = session.PoolIntents.Keys.Single(id => ViewModel.ActiveDocument.Snapshot.StoragePools
                        .Any(pool => pool.StableId == id && pool.MemberPhysicalDiskIds.Contains(creation.PhysicalDiskId)));
                }
                var sizeTarget = Id(StorageObjectKind.StoragePool, poolId);
                if ((creation.CreateTier || creation.CreateVirtualDisk) && intent.Layout == PoolVirtualDiskLayout.HddTiered)
                {
                    var existing = ViewModel.ActiveDocument.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                        && tier.VirtualDiskStableId is null).ToArray();
                    if (existing.Length == 0)
                    {
                        if (!await Stage(RealOperationProposalFactory.CreateHddTierTemplate(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), creation.Pool.FriendlyName + "_HDD"),
                            Text("已核对自动 HDD 容量层", "Verified automatic HDD capacity tier"))) return false;
                        existing = ViewModel.ActiveDocument.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                            && tier.VirtualDiskStableId is null).ToArray();
                    }
                    if (existing.Length != 1) throw new InvalidDataException("One exact unused HDD template is required.");
                    sizeTarget = Id(StorageObjectKind.StorageTier, existing[0].StableId);
                }
                if (!creation.CreateVirtualDisk && !creation.ContinueLayout) continue;
                string? vdId = intent.VerifiedVirtualDiskId;
                if (creation.CreateVirtualDisk)
                {
                    // In a same-pool replacement this is reached only after the exact
                    // old VD deletion stage and its scoped refresh have completed.
                    var freshPool = ViewModel.ActiveDocument.Snapshot.StoragePools.SingleOrDefault(pool => pool.StableId == poolId);
                    if (freshPool is null || ViewModel.ActiveDocument.SourceFacts?.Objects
                        .SingleOrDefault(item => item.Id == poolId) is not { HasReliableIdentity: true }
                        || ViewModel.ActiveDocument.Snapshot.VirtualDisks.Any(vd => vd.PoolStableId == poolId))
                        throw new InvalidDataException("The fresh exact pool must be empty before preparing its first virtual disk.");
                    if (intent.VirtualDiskUseMaximum)
                        ReportRealActivity(Text("正在确定最大可用容量", "Determining maximum available capacity"));
                    var capacity = await RealVirtualDiskCreationCapacityResolver.ResolveAsync(
                        intent.VirtualDiskUseMaximum, intent.VirtualDiskSizeBytes,
                        intent.AutoCreatePartition, intent.CreateMsr, async () =>
                        {
                            ReportRealActivity(Text("正在核对创建容量", "Checking creation capacity"));
                            var result = await ViewModel.AgentConnection!.SendAsync(new QueryAgentRealVirtualDiskCreationRangeRequest(
                                sizeTarget, ViewModel.RealProductSessionId, CorrelationId.New()), CancellationToken.None);
                            if (!result.IsSuccess || result.Value is not AgentRealVirtualDiskCreationRangeResponse sizeResponse
                                || sizeResponse.Range.Target != sizeTarget)
                                throw new InvalidDataException(result.Messages.FirstOrDefault()?.DiagnosticText
                                    ?? result.Messages.FirstOrDefault()?.Code ?? "The exact provider creation range is unavailable.");
                            return sizeResponse.Range;
                        });
                    var proposal = intent.Layout == PoolVirtualDiskLayout.HddTiered
                        ? RealOperationProposalFactory.CreateTieredVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), sizeTarget, intent.VirtualDiskName!,
                            capacity.SizeBytes, capacity.UseMaximumSize)
                        : RealOperationProposalFactory.CreateFirstVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), new(intent.VirtualDiskName!, capacity.SizeBytes, false,
                                intent.CreateMsr, true, intent.VolumeName, intent.DriveLetter, capacity.UseMaximumSize));
                    if (!await Stage(proposal, Text("已核对新 VD", "Verified new VD"),
                        new(StorageObjectKind.VirtualDisk, poolId,
                            intent.Layout == PoolVirtualDiskLayout.HddTiered ? "create-tiered-vdisk" : "create-vdisk"))) return false;
                    vdId = session.PoolIntents[poolId].VerifiedVirtualDiskId;
                }
                if (!intent.AutoCreatePartition) continue;
                if (vdId is null) throw new InvalidDataException("Automatic layout lacks a verified VD identity.");
                // Partition layout uses the actual OS-disk size only after the MAX search/explicit creation is verified.
                var osDisk = ViewModel.ActiveDocument.Snapshot.OsDisks.SingleOrDefault(disk => disk.VirtualDiskStableId == vdId)
                    ?? throw new InvalidDataException("The verified VD has no unique current OS disk.");
                if (osDisk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await Stage(RealOperationProposalFactory.InitializeGpt(session.SystemId,
                        Id(StorageObjectKind.OsDisk, osDisk.StableId)), Text("已核对 GPT 初始化", "Verified GPT initialization"))) return false;
                    osDisk = ViewModel.ActiveDocument.Snapshot.OsDisks.Single(disk => disk.VirtualDiskStableId == vdId);
                }
                var currentIntent = session.PoolIntents[poolId];
                if (!automaticLayoutProgress.TryGetValue(session, out var progress)
                    || progress.VirtualDiskId != vdId)
                {
                    var partitions = ViewModel.ActiveDocument.Snapshot.Partitions
                        .Where(partition => partition.OsDiskStableId == osDisk.StableId).ToArray();
                    if (!osDisk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                        || partitions.Length > 1 || partitions.Any(partition => !IsExactProviderMsr(partition)))
                        throw new InvalidDataException("The automatic layout has no session-verified progression for existing data partitions. The remaining goal is retained.");
                    var layout = RealOperationProposalFactory.ConfigureInitializedDisk(session.SystemId,
                        Id(StorageObjectKind.OsDisk, osDisk.StableId), partitions.Length == 0 ? null
                            : Id(StorageObjectKind.Partition, partitions[0].StableId), intent.CreateMsr,
                        osDisk.Size, true, intent.VolumeName, intent.DriveLetter,
                        checked((int)intent.AllocationUnitSize), intent.FileSystem)!;
                    progress = new RealAutomaticLayoutProgress(layout, vdId, currentIntent);
                    automaticLayoutProgress[session] = progress;
                }
                while (progress.BuildNext(ViewModel.ActiveDocument, currentIntent) is { } next)
                {
                    var description = next.Steps[0].Command switch
                    {
                        DeletePartitionCommand => Text("已调整保留分区", "Adjusted the reserved partition"),
                        CreatePartitionCommand { Role: RealPartitionRole.Msr } => Text("已配置 MSR", "Configured the MSR"),
                        CreatePartitionCommand => Text("已创建数据分区", "Created the data partition"),
                        FormatVolumeCommand => Text("已格式化数据卷", "Formatted the data volume"),
                        SetDriveLetterCommand => Text("已设置盘符", "Assigned the drive letter"),
                        _ => Text("已完成自动分区", "Completed automatic partitioning")
                    };
                    if (!await Stage(next, description, layout: progress)) return false;
                }
                session.PoolIntents[poolId] = currentIntent with { PendingAutomaticLayout = false };
                automaticLayoutProgress.Remove(session);
            }
            session.RealApplyMessage = string.Join(Environment.NewLine, completed);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or JsonException)
        {
            session.RealApplyMessage = string.Join(Environment.NewLine, completed.Append(
                Text("剩余目标已保留：", "Remaining target retained: ") + exception.Message));
            PublishOperationException(Text("应用未完成，已保留目标", "Apply incomplete; target retained"),
                "real", exception, "real.structure.incomplete");
            return false;
        }
        finally
        {
            authorization.Stop();
            IsRealStructureApplyInProgress = false;
            EndRealActivity();
        }

        StorageObjectId Id(StorageObjectKind kind, string key) => new(session.SystemId, kind, key);
        void ValidateApplyContext()
        {
            authorization.ValidateContext(ViewModel.ActiveDocument.SystemId, ViewModel.RealProductSessionId,
                session.BindingGeneration, session.HasBaselineConflict, session.OutcomeUnknown);
            if (!ViewModel.IsRealMode || !ViewModel.IsLocalSystem
                || session.BaselineRevision != ViewModel.ActiveDocument.Revision
                || session.Baseline.SnapshotVersion != ViewModel.ActiveDocument.Snapshot.SnapshotVersion)
                throw new InvalidOperationException("The structural baseline changed outside this Apply.");
        }
        async Task<bool> Stage(RealOperationIntentRequest proposal, string description,
            RealStructureResultSynchronization.OutputMapping? mapping = null, RealAutomaticLayoutProgress? layout = null)
        {
            ValidateApplyContext();
            var succeeded = await SubmitRealAsync(proposal, authorization, ValidateApplyContext,
                frozen => ViewModel.RealStructureResults.Register(session, frozen, proposal, mapping, layout));
            if (LastRealOperationResponse is { } response)
            {
                ViewModel.RealStructureResults.ObserveUnresolved(response);
                if (RealOperationSubmissionState.IsReconciledTerminal(response))
                {
                    if (!ViewModel.RealStructureResults.Reconcile(response, ViewModel.ActiveDocument, LastRealInventoryRefreshSucceeded))
                    {
                        authorization.Stop();
                        session.RealApplyMessage = Text("结果同步尚未完成，保留剩余目标并等待只读核对。", "Result synchronization is incomplete; remaining goals are retained for read-only recovery.");
                        return false;
                    }
                    ViewModel.ObserveRealOperation(response);
                    if (succeeded)
                    {
                        authorization.ValidateContext(ViewModel.ActiveDocument.SystemId, ViewModel.RealProductSessionId,
                            session.BindingGeneration, session.HasBaselineConflict, session.OutcomeUnknown);
                        authorization.ObserveVerified(proposal, response, ViewModel.ActiveDocument);
                    }
                }
            }
            if (!succeeded) authorization.Stop();
            completed.Add(succeeded ? description : Text("应用停止；已完成的更改保留，剩余目标未执行。", "Apply stopped; completed changes remain and the remaining goal was not executed."));
            session.RealApplyMessage = string.Join(Environment.NewLine, completed);
            return succeeded;
        }
    }
}
