using System.Text.Json;
using System.Diagnostics;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool_App;

public partial class EditorPageBase
{
    protected RealStructureDraftPreview BuildRealStructureDraftPreview(SimulationEditingSession session) =>
        RealStructureDraftPlanner.Build(session,
            ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn).Preview;

    protected async Task<bool> ApplyRealStructureDraftAsync(SimulationEditingSession session)
    {
        if (IsRealStructureApplyInProgress
            || session.SystemId != ViewModel.ActiveDocument.SystemId || ViewModel.AgentConnection is not { } connection)
            return false;
        if (session.RealStructureResumeJobId is { } previous)
        {
            if (!await RestoreRemainingStructureGoalAsync(session, previous, connection)) return false;
            if (session.RealStructureResumeJobId is null) return true;
        }
        if (!ViewModel.CanSubmitRealOperation) return false;
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
        foreach (var creation in draft.Creations) session.PoolIntents[creation.Pool.StableId] = creation.Intent;
        var generation = session.BindingGeneration;
        var content = session.ContentRevision;
        var productSession = ViewModel.RealProductSessionId;
        var jobId = Guid.NewGuid();
        var goal = RealStructureJobGoal.Create(session, draft, session.RealStructureResumeJobId);
        using var subscription = new CancellationTokenSource();
        await using var events = connection.WatchAsync(subscription.Token).GetAsyncEnumerator(subscription.Token);
        var next = events.MoveNextAsync().AsTask();
        AgentRealStructureJobResponse? prepared = null;
        var accepted = false;
        var uiStarted = Stopwatch.GetTimestamp();
        long confirmationTicks = 0;
        IsRealStructureApplyInProgress = true;
        try
        {
            ReportRealActivity(Text("正在核对结构目标", "Checking the structural goal"));
            var preparation = await connection.SendAsync(new PrepareAgentRealStructureJobRequest(
                goal, jobId, productSession, CorrelationId.New()), CancellationToken.None);
            if (!preparation.IsSuccess || preparation.Value is not AgentRealStructureJobResponse frozen
                || frozen.JobId != jobId || frozen.State != RealOperationState.Prepared)
                throw new InvalidDataException(preparation.Messages.FirstOrDefault()?.DiagnosticText
                    ?? preparation.Messages.FirstOrDefault()?.Code ?? "The structural job could not be prepared.");
            prepared = frozen;
            if (frozen.DraftIntentHash != goal.DraftIntentHash)
                throw new InvalidDataException("The prepared structural actions differ from the draft to be confirmed.");
            if (generation != session.BindingGeneration || content != session.ContentRevision
                || session.HasBaselineConflict || session.OutcomeUnknown
                || ViewModel.ActiveDocument.SystemId != session.SystemId || ViewModel.RealProductSessionId != productSession)
                return false;
            ViewModel.ObserveStructureJob(frozen, goal.ResumeFromJobId);
            var description = new RealStructureApplyAuthorization(draft, session, productSession).Describe(
                draft, session.BaselineSourceFacts, ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn);
            var confirmationStarted = Stopwatch.GetTimestamp();
            var confirmed = await ConfirmAsync(Text("确认真实结构变更", "Confirm real structural changes"), description);
            confirmationTicks = Stopwatch.GetTimestamp() - confirmationStarted;
            StorageOperationTiming.Record("real.structure.confirmation", confirmationStarted, new(jobId), scope: "user-review");
            if (!confirmed) return false;
            if (generation != session.BindingGeneration || content != session.ContentRevision || session.HasBaselineConflict || session.OutcomeUnknown
                || ViewModel.ActiveDocument.SystemId != session.SystemId || ViewModel.RealProductSessionId != productSession)
                return false;
            ReportRealActivity(Text("正在应用结构目标", "Applying the structural goal"));
            // Exactly one Accept. A missing reply is resolved by the same job id.
            accepted = true;
            session.RealStructureResumeJobId = jobId;
            var acceptance = await connection.SendAsync(new AcceptAgentRealStructureJobRequest(jobId,
                frozen.GoalHash, productSession, CorrelationId.New()), CancellationToken.None);
            var status = acceptance.Value as AgentRealStructureJobResponse;
            if (status is null)
            {
                var query = await connection.SendAsync(new QueryAgentRealStructureJobRequest(jobId, CorrelationId.New()), CancellationToken.None);
                status = query.Value as AgentRealStructureJobResponse;
            }
            if (status is null) throw new IOException("The accepted structural job status is unavailable.");
            while (status.State is RealOperationState.Accepted or RealOperationState.Running)
            {
                session.RealApplyMessage = string.Join(Environment.NewLine, status.Completed);
                ReportRealActivity(status.Completed.LastOrDefault()
                    ?? Text("正在执行并核对结构目标", "Executing and verifying the structural goal"));
                if (!await next) throw new IOException("The structural job event stream was disconnected. The goal is retained.");
                var item = events.Current;
                if (item is AgentRealStructureJobStatusEvent changed && changed.Status.JobId == jobId
                    && changed.Status.Revision >= status.Revision)
                    status = changed.Status;
                else if (item is AgentStateReseedEvent)
                {
                    var query = await connection.SendAsync(new QueryAgentRealStructureJobRequest(jobId, CorrelationId.New()), CancellationToken.None);
                    if (query.Value is AgentRealStructureJobResponse recovered) status = recovered;
                }
                next = events.MoveNextAsync().AsTask();
            }
            var terminalRead = await connection.SendAsync(new QueryAgentRealStructureJobRequest(jobId, CorrelationId.New()), CancellationToken.None);
            if (terminalRead.Value is AgentRealStructureJobResponse terminal) status = terminal;
            ViewModel.ObserveStructureJob(status);
            session.RealApplyMessage = string.Join(Environment.NewLine, status.Completed.Append(status.Code ?? ""));
            if (status.Children.LastOrDefault() is { Value: var childId } && childId != Guid.Empty)
            {
                var child = await RealOperationStatusCache.For(connection).QueryAsync(new(childId), CancellationToken.None);
                if (child is not null)
                {
                    LastRealOperationResponse = child;
                    ViewModel.ObserveRealOperation(child);
                    if (status.RequiresReconciliation && generation == session.BindingGeneration)
                        session.MarkOutcomeUnknown(child.Plan.OperationId, child.Plan.PlanHash);
                }
            }
            var succeeded = status.State == RealOperationState.Succeeded && !status.RequiresReconciliation;
            if (generation == session.BindingGeneration && session.SystemId == ViewModel.ActiveDocument.SystemId)
            {
                LastRealInventoryRefreshSucceeded = await ViewModel.ReadPublishedRealInventoryAsync();
                if (LastRealInventoryRefreshSucceeded && !status.RequiresReconciliation)
                {
                    if (succeeded) session.Bind(ViewModel.ActiveDocument, ViewModel.ActiveSnapshot);
                    else if (status.RemainingGoal is { } remaining)
                        session.RebaseVerified(ViewModel.ActiveDocument, new(remaining.Working,
                            remaining.PoolIntents.ToDictionary(item => item.PoolId, item => item.Intent, StringComparer.Ordinal),
                            session.MaximumSizeFields.ToHashSet(StringComparer.Ordinal)));
                    if (!succeeded) session.RealStructureResumeJobId = jobId;
                    OnRealInventoryRefreshed();
                }
            }
            PublishOperationFeedback(succeeded ? GlobalNotificationSeverity.Info : GlobalNotificationSeverity.Warning,
                succeeded ? Text("真实结构应用完成", "Structural changes completed")
                    : Text("结构应用停止，剩余目标已保留", "Structural changes stopped; remaining goals retained"),
                session.RealApplyMessage, "real", status.Code ?? "agent.structure.result", $"JobId: {jobId:D}");
            return succeeded && LastRealInventoryRefreshSucceeded;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or ArgumentException or NotSupportedException or JsonException or TimeoutException)
        {
            session.RealApplyMessage = Text("剩余目标已保留：", "Remaining goal retained: ") + exception.Message
                + $" (JobId: {jobId:D})";
            PublishOperationException(Text("结构应用未完成", "Structural changes incomplete"),
                "real", exception, "agent.structure.incomplete");
            return false;
        }
        finally
        {
            if (!accepted && prepared is not null)
                await connection.SendAsync(new StopAgentRealStructureJobRequest(jobId, prepared.GoalHash,
                    CorrelationId.New()), CancellationToken.None);
            subscription.Cancel();
            try { await next; } catch (OperationCanceledException) when (subscription.IsCancellationRequested) { }
            IsRealStructureApplyInProgress = false;
            EndRealActivity();
            StorageOperationTiming.Record("real.structure.ui.total", uiStarted, new(jobId), scope: "structure-editor");
            StorageOperationTiming.Record("real.structure.ui", uiStarted + confirmationTicks, new(jobId), scope: "structure-editor");
        }
    }
    protected async Task<bool> RestoreRemainingStructureGoalAsync(SimulationEditingSession session, Guid jobId,
        IAgentConnection connection, bool emptyDraft = false)
    {
        var binding = session.BindingGeneration;
        var content = session.ContentRevision;
        try
        {
            if (!emptyDraft && session.RealStructureResumeContentRevision != content)
                throw new InvalidOperationException(Text("草稿在作业停止后已有新修改，不能用旧剩余目标覆盖。请保留新草稿或明确放弃后再恢复。",
                    "The draft changed after the job stopped. Its new edits cannot be replaced by the saved remaining goal."));
            var read = await connection.SendAsync(new QueryAgentRealStructureJobRequest(jobId, CorrelationId.New()), CancellationToken.None);
            if (read.Value is not AgentRealStructureJobResponse status)
                throw new InvalidDataException("The durable structural job status is unavailable.");
            // Apply is an explicit user action. The child may already be terminal and absent from the unfinished list.
            if (status.RequiresReconciliation && status.Children.LastOrDefault() is { Value: var last } && last != Guid.Empty)
            {
                await connection.SendAsync(new ReconcileAgentRealOperationRequest(new(last), CorrelationId.New()), CancellationToken.None);
                read = await connection.SendAsync(new QueryAgentRealStructureJobRequest(jobId, CorrelationId.New()), CancellationToken.None);
                if (read.Value is AgentRealStructureJobResponse reconciled) status = reconciled;
            }
            if (status.State == RealOperationState.Prepared)
            {
                var cancelled = await connection.SendAsync(new StopAgentRealStructureJobRequest(jobId, status.GoalHash,
                    CorrelationId.New()), CancellationToken.None);
                if (cancelled.Value is AgentRealStructureJobResponse stopped) status = stopped;
            }
            if (status.RequiresReconciliation
                || status.State is RealOperationState.Accepted or RealOperationState.Running)
            {
                session.RealApplyMessage = Text("原结构作业仍待核对；剩余目标已保留。", "The previous structural job still needs reconciliation; its remaining goal is retained.");
                return false;
            }
            ViewModel.ObserveStructureJob(status);
            await ViewModel.RecoverRealOperationsAsync();
            if (!await ViewModel.ReadPublishedRealInventoryAsync())
                throw new IOException("The verified inventory for the remaining structural goal is unavailable.");
            if (session.SystemId != ViewModel.ActiveDocument.SystemId
                || !session.TryRestoreStructureJob(status, ViewModel.ActiveDocument, binding, content, emptyDraft))
                throw new InvalidOperationException("The bound draft changed while its saved goal was being read; it was preserved.");
            session.RealApplyMessage = string.Join(Environment.NewLine, status.Completed.Append(
                Text("剩余目标已恢复；请检查后再次点击应用并确认。", "The remaining goal is restored. Review it, then apply and confirm again.")));
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            session.RealApplyMessage = Text("剩余目标已保留：", "Remaining goal retained: ") + exception.Message;
            return false;
        }
    }

}
