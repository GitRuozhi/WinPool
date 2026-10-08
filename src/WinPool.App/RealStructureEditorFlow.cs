using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool_App;

public partial class EditorPageBase
{
    private sealed record PendingVerifiedDraftRefresh(AgentRealOperationResponse Response, string Description, Action? Map, long BindingGeneration);
    private readonly Dictionary<SimulationEditingSession, PendingVerifiedDraftRefresh> pendingDraftRefreshes = new();
    private readonly Dictionary<SimulationEditingSession, RealAutomaticLayoutProgress> automaticLayoutProgress = new();
    protected RealStructureDraftPreview BuildRealStructureDraftPreview(SimulationEditingSession session) =>
        RealStructureDraftPlanner.Build(session,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn).Preview;

    protected async Task<bool> ApplyRealStructureDraftAsync(SimulationEditingSession session)
    {
        if (IsRealStructureApplyInProgress || !ViewModel.CanSubmitRealOperation
            || session.SystemId != ViewModel.ActiveDocument.SystemId) return false;
        var recoveredVerifiedWrite = false;
        if (pendingDraftRefreshes.TryGetValue(session, out var pending))
        {
            var anchors = pending.Response.Plan.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray();
            if (ViewModel.ActiveDocument.SourceFacts is not { } facts || anchors.Length == 0) return false;
            try
            {
                ReportRealActivity(Text("正在重新核对已完成结果", "Refreshing the verified result"), true);
                await RefreshRealInventoryAsync(StorageInventoryScopeFactory.Create(facts,
                    pending.Response.Plan.OperationId, "draft-recovery", anchors));
                if (!LastRealInventoryRefreshSucceeded) return false;
                LastRealOperationResponse = pending.Response;
                if (pending.BindingGeneration != session.BindingGeneration)
                {
                    pendingDraftRefreshes.Remove(session);
                    automaticLayoutProgress.Remove(session);
                    session.MarkBaselineConflict(ViewModel.ActiveDocument);
                    session.RealApplyMessage = Text("已核对先前写入；新的草稿未被旧结果改写，请按新事实重新核对。", "The earlier write was refreshed. Its old mapping was not applied to the new draft; reconcile the new facts.");
                    return false;
                }
                pending.Map?.Invoke();
                RebaseRemainingDraft(session, ViewModel.ActiveDocument);
                recoveredVerifiedWrite = pending.Response.State == RealOperationState.Succeeded
                    && !pending.Response.RequiresReconciliation && pending.Response.Steps.Count > 0
                    && pending.Response.Steps.All(step => step.State == RealOperationStepState.Verified);
                session.RealApplyMessage = pending.Description;
                pendingDraftRefreshes.Remove(session);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
            {
                session.RealApplyMessage = Text("已完成写入的只读核对尚未完成，保留剩余目标：", "Read-only recovery of the completed write is incomplete; remaining target retained: ") + exception.Message;
                return false;
            }
            finally { EndRealActivity(); }
        }
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
            if (recoveredVerifiedWrite && RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(session)) return true;
            session.RealApplyMessage = string.Join(Environment.NewLine, draft.Preview.BlockingReasons);
            return false;
        }
        IsRealStructureApplyInProgress = true;
        structureHasWritten = false;
        var completed = new List<string>();
        try
        {
            // Reject known provider/layout limitations before deleting or clearing anything.
            foreach (var creation in draft.Creations.Where(item => item.CreatePool || item.CreateVirtualDisk))
            {
                ReportRealActivity(Text("正在核对目标布局支持", "Checking the target layout support"));
                var target = Id(StorageObjectKind.PhysicalDisk, creation.PhysicalDiskId);
                var tiered = creation.CreateVirtualDisk && creation.Intent.Layout == PoolVirtualDiskLayout.HddTiered;
                var support = await ViewModel.AgentConnection!.SendAsync(new QueryAgentRealStructureCreationSupportRequest(
                    target, tiered, ViewModel.RealProductSessionId, CorrelationId.New()), CancellationToken.None);
                if (!support.IsSuccess || support.Value is not AgentRealStructureCreationSupportResponse actual
                    || actual.Support.PhysicalTarget != target || actual.Support.Tiered != tiered)
                    throw new InvalidDataException(support.Messages.FirstOrDefault()?.DiagnosticText
                        ?? support.Messages.FirstOrDefault()?.Code ?? "The requested layout is unsupported.");
            }
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
                        Text("已核对新池", "Verified new pool"), () =>
                        {
                            poolId = RequireVerifiedCreatedId(StorageObjectKind.StoragePool, "create-pool");
                            RemapCreatedPool(session, temporaryPoolId, poolId);
                        })) return false;
                }
                if (!creation.CreateVirtualDisk && !creation.ContinueLayout) continue;
                string? vdId = intent.VerifiedVirtualDiskId;
                if (creation.CreateVirtualDisk)
                {
                    var sizeTarget = Id(StorageObjectKind.StoragePool, poolId);
                    if (intent.Layout == PoolVirtualDiskLayout.HddTiered)
                    {
                        var existing = ViewModel.ActiveDocument.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                            && tier.VirtualDiskStableId is null).ToArray();
                        if (existing.Length == 0)
                        {
                            if (!await Stage(RealOperationProposalFactory.CreateHddTierTemplate(session.SystemId,
                                Id(StorageObjectKind.StoragePool, poolId), creation.Pool.FriendlyName + "_HDD"),
                                Text("已核对 HDD 模板", "Verified HDD template"))) return false;
                            existing = ViewModel.ActiveDocument.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                                && tier.VirtualDiskStableId is null).ToArray();
                        }
                        if (existing.Length != 1) throw new InvalidDataException("One exact unused HDD template is required.");
                        sizeTarget = Id(StorageObjectKind.StorageTier, existing[0].StableId);
                    }
                    ReportRealActivity(Text("正在读取准确创建容量范围", "Reading the exact provider creation range"));
                    var result = await ViewModel.AgentConnection!.SendAsync(new QueryAgentRealVirtualDiskCreationRangeRequest(
                        sizeTarget, ViewModel.RealProductSessionId, CorrelationId.New()), CancellationToken.None);
                    if (!result.IsSuccess || result.Value is not AgentRealVirtualDiskCreationRangeResponse sizeResponse
                        || sizeResponse.Range.Target != sizeTarget)
                        throw new InvalidDataException(result.Messages.FirstOrDefault()?.DiagnosticText
                            ?? result.Messages.FirstOrDefault()?.Code ?? "The exact provider creation range is unavailable.");
                    var bytes = RealOperationProposalFactory.ResolveVirtualDiskCreationSize(sizeResponse.Range,
                        intent.VirtualDiskUseMaximum, intent.VirtualDiskSizeBytes,
                        intent.AutoCreatePartition, intent.CreateMsr);
                    var proposal = intent.Layout == PoolVirtualDiskLayout.HddTiered
                        ? RealOperationProposalFactory.CreateTieredVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), sizeTarget, intent.VirtualDiskName!, bytes)
                        : RealOperationProposalFactory.CreateFirstVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), new(intent.VirtualDiskName!, bytes, false,
                                intent.CreateMsr, true, intent.VolumeName, intent.DriveLetter));
                    if (!await Stage(proposal, Text("已核对新 VD", "Verified new VD"), () =>
                        {
                            vdId = RequireVerifiedCreatedId(StorageObjectKind.VirtualDisk,
                                intent.Layout == PoolVirtualDiskLayout.HddTiered ? "create-tiered-vdisk" : "create-vdisk");
                            RemapCreatedVirtualDisk(session, poolId, vdId);
                            session.PoolIntents[poolId] = intent with { VerifiedVirtualDiskId = vdId,
                                PendingAutomaticLayout = intent.AutoCreatePartition };
                        })) return false;
                }
                if (!intent.AutoCreatePartition) continue;
                if (vdId is null) throw new InvalidDataException("Automatic layout lacks a verified VD identity.");
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
                        osDisk.Size, true, intent.VolumeName, intent.DriveLetter)!;
                    progress = new RealAutomaticLayoutProgress(layout, vdId, currentIntent);
                    automaticLayoutProgress[session] = progress;
                }
                while (progress.BuildNext(ViewModel.ActiveDocument, currentIntent) is { } next)
                {
                    if (!await Stage(next, Text("已核对自动布局步骤 ", "Verified automatic layout step ") + next.Steps[0].Id,
                        observe: response => progress.Observe(next, response))) return false;
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
            IsRealStructureApplyInProgress = false;
            structureHasWritten = false;
            EndRealActivity();
        }

        StorageObjectId Id(StorageObjectKind kind, string key) => new(session.SystemId, kind, key);
        async Task<bool> Stage(RealOperationIntentRequest proposal, string description, Action? map = null,
            Action<AgentRealOperationResponse>? observe = null)
        {
            var succeeded = await SubmitRealAsync(proposal);
            if (LastRealOperationResponse is { } durable)
            {
                if (durable.RequiresReconciliation) session.OutcomeUnknown = true;
                observe?.Invoke(durable);
                if ((durable.State == RealOperationState.Succeeded
                        || durable.Steps.Any(step => step.State == RealOperationStepState.Verified))
                    && !durable.RequiresReconciliation && !LastRealInventoryRefreshSucceeded)
                {
                    pendingDraftRefreshes[session] = new(durable, description,
                        durable.State == RealOperationState.Succeeded ? map : null, session.BindingGeneration);
                    session.RealApplyMessage = Text("写入已完成，等待只读刷新后继续剩余草稿。", "The write completed. Refresh its result before continuing the remaining draft.");
                    return false;
                }
            }
            if (LastRealInventoryRefreshSucceeded && LastRealOperationResponse is { } response)
            {
                if (succeeded) map?.Invoke();
                if (response.Steps.Any(item => item.State == RealOperationStepState.Verified))
                    RebaseRemainingDraft(session, ViewModel.ActiveDocument);
                if (response.RequiresReconciliation) session.OutcomeUnknown = true;
            }
            if (succeeded) completed.Add(description);
            else completed.Add(Text("本段停止；未执行后续，已核对结果保留。", "This segment stopped; following writes were not executed and verified results remain."));
            session.RealApplyMessage = string.Join(Environment.NewLine, completed);
            return succeeded;
        }
    }

    private string RequireVerifiedCreatedId(StorageObjectKind kind, string stepId)
    {
        var step = LastRealOperationResponse?.Steps.SingleOrDefault(step => step.StepId == stepId
            && step.State == RealOperationStepState.Verified)
            ?? throw new InvalidDataException("The creation step has no verified output.");
        using var evidence = JsonDocument.Parse(step.ResultEvidence ?? "{}");
        var key = evidence.RootElement.GetProperty("CreatedObjectId").GetString();
        if (string.IsNullOrWhiteSpace(key) || ViewModel.ActiveDocument.SourceFacts?.Objects
            .SingleOrDefault(item => item.Id == key) is not { HasReliableIdentity: true })
            throw new InvalidDataException("The verified creation output is absent from the fresh related inventory.");
        var exists = kind switch
        {
            StorageObjectKind.StoragePool => ViewModel.ActiveDocument.Snapshot.StoragePools.Any(item => item.StableId == key),
            StorageObjectKind.VirtualDisk => ViewModel.ActiveDocument.Snapshot.VirtualDisks.Any(item => item.StableId == key),
            _ => false
        };
        return exists ? key : throw new InvalidDataException("The verified creation output has the wrong object kind.");
    }

    private static void RemapCreatedPool(SimulationEditingSession session, string temporaryId, string realId)
    {
        session.Working = session.Working with
        {
            StoragePools = session.Working.StoragePools.Where(pool => pool.StableId != temporaryId).ToArray(),
            StorageTiers = session.Working.StorageTiers.Where(tier => tier.PoolStableId != temporaryId).ToArray(),
            VirtualDisks = session.Working.VirtualDisks.Select(vd => vd.PoolStableId == temporaryId ? vd with { PoolStableId = realId } : vd).ToArray(),
            PhysicalDisks = session.Working.PhysicalDisks.Select(disk => disk.PoolStableId == temporaryId ? disk with { PoolStableId = realId } : disk).ToArray()
        };
        if (session.PoolIntents.Remove(temporaryId, out var intent)) session.PoolIntents[realId] = intent;
    }

    private static void RemapCreatedVirtualDisk(SimulationEditingSession session, string poolId, string realId)
    {
        var temporaryVds = session.Working.VirtualDisks.Where(vd => vd.PoolStableId == poolId
            && EditWorkspace.IsDraftVirtualDisk(vd.StableId)).Select(vd => vd.StableId).ToHashSet();
        var temporaryDisks = session.Working.OsDisks.Where(disk => temporaryVds.Contains(disk.VirtualDiskStableId ?? ""))
            .Select(disk => disk.StableId).ToHashSet();
        var temporaryPartitions = session.Working.Partitions.Where(partition => temporaryDisks.Contains(partition.OsDiskStableId ?? ""))
            .Select(partition => partition.StableId).ToHashSet();
        session.Working = session.Working with
        {
            VirtualDisks = session.Working.VirtualDisks.Where(vd => !temporaryVds.Contains(vd.StableId)).ToArray(),
            OsDisks = session.Working.OsDisks.Where(disk => !temporaryDisks.Contains(disk.StableId)).ToArray(),
            Partitions = session.Working.Partitions.Where(partition => !temporaryPartitions.Contains(partition.StableId)).ToArray(),
            Volumes = session.Working.Volumes.Where(volume => !temporaryPartitions.Contains(volume.PartitionStableId ?? "")).ToArray()
        };
    }

    private static void RebaseRemainingDraft(SimulationEditingSession session, StorageSystemDocument fresh)
    {
        var old = session.Baseline;
        var target = session.Working;
        var actual = fresh.Snapshot;
        static T[] Merge<T>(IReadOnlyList<T> baseline, IReadOnlyList<T> desired, IReadOnlyList<T> live, Func<T, string> id)
        {
            var oldIds = baseline.Select(id).ToHashSet();
            var desiredIds = desired.Select(id).ToHashSet();
            var liveIds = live.Select(id).ToHashSet();
            return live.Where(item => !oldIds.Contains(id(item)) || desiredIds.Contains(id(item)))
                .Concat(desired.Where(item => !oldIds.Contains(id(item)) && !liveIds.Contains(id(item)))).ToArray();
        }
        var remaining = target with
        {
            SnapshotVersion = actual.SnapshotVersion, ScannedAt = actual.ScannedAt,
            PhysicalDisks = actual.PhysicalDisks.Select(live =>
            {
                var desired = target.PhysicalDisks.SingleOrDefault(item => item.StableId == live.StableId);
                var prior = old.PhysicalDisks.SingleOrDefault(item => item.StableId == live.StableId);
                return desired is not null && prior is not null && desired.PoolStableId != prior.PoolStableId
                    ? live with { PoolStableId = desired.PoolStableId } : live;
            }).ToArray(),
            StoragePools = Merge(old.StoragePools, target.StoragePools, actual.StoragePools, item => item.StableId),
            StorageTiers = Merge(old.StorageTiers, target.StorageTiers, actual.StorageTiers, item => item.StableId),
            VirtualDisks = Merge(old.VirtualDisks, target.VirtualDisks, actual.VirtualDisks, item => item.StableId),
            OsDisks = Merge(old.OsDisks, target.OsDisks, actual.OsDisks, item => item.StableId),
            Partitions = Merge(old.Partitions, target.Partitions, actual.Partitions, item => item.StableId),
            Volumes = Merge(old.Volumes, target.Volumes, actual.Volumes, item => item.StableId)
        };
        session.RebaseVerified(fresh, session.Capture() with { Snapshot = remaining });
    }
}
