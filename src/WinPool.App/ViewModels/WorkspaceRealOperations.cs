using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.App.Services;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel
{
    private readonly Dictionary<OperationId, string> _recoveredRealOperationFeedback = [];
    private readonly Dictionary<Guid, AgentRealStructureJobResponse> _recoverableStructureJobs = [];
    public AgentRealStructureJobResponse? FindRecoverableStructureJob(SystemId systemId) =>
        _recoverableStructureJobs.Values.Where(job => job.SystemId == systemId)
            .OrderByDescending(job => job.Revision).FirstOrDefault();
    public void ObserveStructureJob(AgentRealStructureJobResponse status, Guid? resumeFromJobId = null)
    {
        if (resumeFromJobId is { } previous) _recoverableStructureJobs.Remove(previous);
        if (status.State == RealOperationState.Succeeded) _recoverableStructureJobs.Remove(status.JobId);
        else _recoverableStructureJobs[status.JobId] = status with { RemainingGoal = null };
        RealOperationActivityChanged?.Invoke();
    }
    private readonly CancellationTokenSource _realOperationRecoveryCancellation = new();
    private Task? _realOperationRecoveryRefresh;
    private Task? _realOperationRecoveryWatch;
    private CancellationTokenSource? _realOperationRecoveryWatchCancellation;
    private OperationId? _watchedRecoveryOperation;
    private bool _refreshingRecoveredRealOperation;
    public RealStructureResultSynchronization RealStructureResults { get; } = new();
    public bool IsRealOperationBusy { get; private set; }
    public bool IsRealGraphObscured { get; private set; }
    public string RealOperationPhase { get; private set; } = string.Empty;
    public string? RealOperationOwner { get; private set; }
    private const string EditorProgressKey = "editor:active-operation";
    public AgentRealOperationResponse? LastRealOperationStatus { get; private set; }
    public event Action? RealOperationActivityChanged;
    public event Func<AgentRealOperationResponse, bool, bool>? RealOperationRecovered;

    public void SetRealOperationActivity(bool busy, bool obscure, string phase, string? owner = null)
    {
        var wasBusy = IsRealOperationBusy;
        var retained = !busy && RealOperationSubmission.IsBlocked;
        var chinese = Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        var nextPhase = retained ? chinese ? "操作结果待核对" : "Operation result needs review" : phase;
        var nextOwner = !busy && !retained ? null : owner ?? RealOperationOwner;
        if (IsRealOperationBusy == busy && IsRealGraphObscured == (obscure || retained)
            && RealOperationPhase == nextPhase && RealOperationOwner == nextOwner) return;
        IsRealOperationBusy = busy;
        IsRealGraphObscured = obscure || retained;
        RealOperationPhase = nextPhase;
        RealOperationOwner = nextOwner;
        if (busy || retained)
            _notificationService.Publish(GlobalNotificationSeverity.Info,
                IsUsingSimulatedInventory ? chinese ? "编辑操作" : "Editing operation"
                    : chinese ? "真实磁盘操作" : "Real storage operation",
                RealOperationPhase, "editor",
                new GlobalNotificationOptions { OccurrenceKey = EditorProgressKey, IsProgress = true,
                    AutoDismiss = false, RecordInHistory = false });
        else _notificationService.DismissByKey(EditorProgressKey);
        RealOperationActivityChanged?.Invoke();
        if (!busy)
        {
            EnsureRealOperationRecoveryWatch();
            // The originating page may have stopped waiting after an uncertain
            // response while its terminal event arrived during that active flow.
            if (wasBusy && RealOperationSubmission.IsBlocked) _ = RecoverRealOperationsAsync();
        }
    }

    public void ObserveRealOperation(AgentRealOperationResponse status)
    {
        if (!RealOperationSubmission.Observe(status)) return;
        RealStructureResults.ObserveUnresolved(status);
        LastRealOperationStatus = status;
        if (_recoveredRealOperationFeedback.TryGetValue(status.Plan.OperationId, out var previous))
        {
            var key = RealOperationFeedback.RecoveryKey(status);
            if (!StringComparer.Ordinal.Equals(previous, key))
            {
                _recoveredRealOperationFeedback[status.Plan.OperationId] = key;
                var feedback = RealOperationFeedback.Recovery(status,
                    Localization.EffectiveLanguage == LanguagePreference.ZhCn, ActiveDocument.SourceFacts);
                _notificationService.PublishWarning(feedback.Title, feedback.Message, "real",
                    $"real:recover:{status.Plan.OperationId.Value}", options: feedback.Options);
            }
        }
    }

    public Task<bool> RefreshRealOperationScopeAsync(StorageInventoryScope scope,
        CancellationToken cancellationToken = default) =>
        RealScopedInventoryRefreshPolicy.GuardInvalidDataAsync(
            () => RefreshRealOperationScopeCoreAsync(scope, cancellationToken),
            exception => ReportScopedRefreshFailure(scope, exception));

    private async Task<bool> RefreshRealOperationScopeCoreAsync(StorageInventoryScope scope,
        CancellationToken cancellationToken)
    {
        if (_agentConnection is null) return false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ScanError = string.Empty;
            var document = await AgentInventoryReader.CaptureAsync(_agentConnection,
                new CaptureAgentManageScopedInventoryRequest(scope, CorrelationId.New()), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var complete = document.SourceFacts?.ScopedCollection is { Complete: true } collection
                && collection.Scope.SystemId == scope.SystemId
                && collection.Scope.OperationId == scope.OperationId
                && collection.Scope.StepId == scope.StepId;
            // Even an incomplete batch preserves the previous facts and exposes
            // its failure metadata. It cannot authorize a following segment.
            ApplyLocalInventory(document);
            if (!complete)
                throw new InvalidDataException(document.SourceFacts?.ScopedCollection?.ReasonCode ?? "The related inventory is incomplete.");
            if (!RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(ActiveDocument, document, scope))
                return false;
            if (SelectedSystem.IsLocal)
            {
                BuildDetails();
                RebuildComparisonColumns();
                PublishStorageFindings(document.Snapshot);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            ReportScopedRefreshFailure(scope, exception);
            return false;
        }
        finally
        {
            StorageOperationTiming.Record("real.ui_scoped_refresh", started,
                scope.OperationId, scope.StepId, scope.Key);
        }
    }

    private void ReportScopedRefreshFailure(StorageInventoryScope scope, Exception exception)
    {
        ScanError = exception.Message;
        _notificationService.PublishWarning(Localization["Warning"],
            "磁盘操作结果已记录，但相关视图未刷新；请手动刷新以重新只读核对。 / The operation result is recorded, but its related view is stale. Refresh to recheck it without writing.",
            "real", $"real:refresh:{scope.Key}", options: new GlobalNotificationOptions { Detail = exception.Message });
    }

    public void StopRealOperationRecovery() => _realOperationRecoveryCancellation.Cancel();

    public async Task RecheckRealOperationsAsync()
    {
        if (_agentConnection is null || IsRealOperationBusy) return;
        await RecoverRealOperationsAsync();
        await RealOperationRecovery.RecheckAsync(_agentConnection, RealOperationSubmission,
            _ => { }, _realOperationRecoveryCancellation.Token);
        await RecoverRealOperationsAsync();
    }

    public Task RecoverRealOperationsAsync() => RecoverRealOperationsAsync(null);

    private async Task RecoverRealOperationsAsync(AgentRealOperationResponse? retainedStatus)
    {
        if (_realOperationRecoveryCancellation.IsCancellationRequested || IsRealOperationBusy) return;
        if (_realOperationRecoveryRefresh is { } pending)
        {
            await pending;
            if (retainedStatus is null || !RealOperationSubmission.IsBlocked) return;
        }
        _realOperationRecoveryRefresh = RecoverRealOperationsCoreAsync(retainedStatus);
        try { await _realOperationRecoveryRefresh; }
        finally
        {
            _realOperationRecoveryRefresh = null;
            EnsureRealOperationRecoveryWatch();
        }
    }

    private void EnsureRealOperationRecoveryWatch()
    {
        var operationId = RealOperationSubmission.OperationId;
        if (_watchedRecoveryOperation != operationId) _realOperationRecoveryWatchCancellation?.Cancel();
        if (_agentConnection is null || _realOperationRecoveryCancellation.IsCancellationRequested
            || IsRealOperationBusy || operationId is null
            || _watchedRecoveryOperation == operationId && _realOperationRecoveryWatch is { IsCompleted: false }) return;
        var subscription = CancellationTokenSource.CreateLinkedTokenSource(_realOperationRecoveryCancellation.Token);
        _realOperationRecoveryWatchCancellation = subscription;
        _watchedRecoveryOperation = operationId;
        _realOperationRecoveryWatch = WatchRecoveredRealOperationAsync(subscription);
    }

    private async Task WatchRecoveredRealOperationAsync(CancellationTokenSource subscription)
    {
        // Keep UI continuations on the workspace context, but publish ownership
        // before the initial synchronous cache/query result can call recovery.
        await Task.Yield();
        try
        {
            await foreach (var status in RealOperationRecovery.WatchAsync(_agentConnection!,
                RealOperationSubmission, subscription.Token))
            {
                if (IsRealOperationBusy) continue;
                if (RealOperationSubmissionState.IsReconciledTerminal(status))
                    await RecoverRealOperationsAsync(status);
                else ObserveRealOperation(status);
            }
        }
        catch (OperationCanceledException) when (subscription.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or ArgumentException or JsonException)
        {
            if (!subscription.IsCancellationRequested)
                _notificationService.PublishWarning(Localization["Warning"], exception.Message,
                    "real", "real:recover:unavailable");
        }
        finally
        {
            if (ReferenceEquals(_realOperationRecoveryWatchCancellation, subscription))
            {
                _realOperationRecoveryWatchCancellation = null;
                _realOperationRecoveryWatch = null;
                var previous = _watchedRecoveryOperation;
                _watchedRecoveryOperation = null;
                // A refresh can reveal another unfinished identity. Transfer
                // ownership once; a failed stream is not retried in a loop.
                if (RealOperationSubmission.OperationId is { } next && next != previous)
                    EnsureRealOperationRecoveryWatch();
            }
            subscription.Dispose();
        }
    }

    private async Task RecoverRealOperationsCoreAsync(AgentRealOperationResponse? retainedStatus)
    {
        if (_agentConnection is null) return;
        var closed = new List<AgentRealOperationResponse>();
        var deferred = new List<AgentRealOperationResponse>();
        var cancellationToken = _realOperationRecoveryCancellation.Token;
        try
        {
            try
            {
                await RealOperationRecovery.RefreshAsync(_agentConnection, RealOperationSubmission, status =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsRealOperationBusy) return;
                    if (RealOperationSubmission.OperationId is { } retained
                        && (retained != status.Plan.OperationId
                            || !StringComparer.Ordinal.Equals(RealOperationSubmission.PlanHash, status.Plan.PlanHash)))
                    {
                        deferred.Add(status);
                        return;
                    }
                    if (RealOperationSubmission.OperationId == status.Plan.OperationId
                        && StringComparer.Ordinal.Equals(RealOperationSubmission.PlanHash, status.Plan.PlanHash)
                        && RealOperationSubmissionState.IsReconciledTerminal(status))
                    {
                        // Keep submission disabled until the recovered result's
                        // related facts and the editor's uncertainty flag refresh.
                        _refreshingRecoveredRealOperation = true;
                        closed.Add(status);
                        LastRealOperationStatus = status;
                        return;
                    }
                    _recoveredRealOperationFeedback.TryAdd(status.Plan.OperationId, string.Empty);
                    ObserveRealOperation(status);
                }, cancellationToken, job =>
                {
                    ObserveStructureJob(job);
                    var chinese = Localization.EffectiveLanguage == LanguagePreference.ZhCn;
                    _notificationService.PublishWarning(chinese ? "结构作业尚未完成" : "Structural job incomplete",
                        chinese ? "已完成变更不会回滚。可在结构编辑页的空草稿中点击“恢复待处理作业”，查看剩余目标；再次应用需要重新确认。"
                            : "Completed changes remain. In an empty structure draft, select Restore pending job to inspect the remaining goal; applying again requires confirmation.",
                        "real", $"real:job:{job.JobId:D}", options: new GlobalNotificationOptions
                        { Detail = $"JobId: {job.JobId:D}\n{string.Join(Environment.NewLine, job.Completed)}" });
                }, retainedStatus);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A list failure cannot discard a terminal status already
                // returned by the exact query earlier in this refresh.
                _notificationService.PublishWarning(Localization["Warning"], exception.Message,
                    "real", "real:recover:unavailable");
            }
            foreach (var status in closed)
            {
                await RealStructureResults.RecoverAsync(status,
                    () => RefreshRecoveredRealOperationAsync(status, cancellationToken),
                    () => SystemCatalog.Systems.Single(document => document.IsLocal && document.SystemId == status.Plan.SystemId),
                    NotifyRecoveredRealOperation, ObserveRealOperation, cancellationToken);
            }
            // A terminal retained plan can have hidden another recoverable
            // identity in the same list. Adopt it before exposing submission.
            foreach (var status in deferred.Where(status =>
                closed.All(terminal => terminal.Plan.OperationId != status.Plan.OperationId)))
            {
                _recoveredRealOperationFeedback.TryAdd(status.Plan.OperationId, string.Empty);
                ObserveRealOperation(status);
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
                _notificationService.PublishWarning(Localization["Warning"], exception.Message,
                    "real", "real:recover:unavailable");
        }
        finally
        {
            _refreshingRecoveredRealOperation = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                OnPropertyChanged(nameof(CanSubmitRealOperation));
                // Notify views only after both the exact identity and the recovery
                // busy gate have settled, so topology interaction is rebuilt enabled.
                // Recovery does not arm real mode, restore tokens or resume any draft.
                if (!IsRealOperationBusy)
                    SetRealOperationActivity(false, false,
                        RealOperationSubmission.IsBlocked ? "待核对 / Needs reconciliation" : string.Empty);
            }
        }
    }

    private bool NotifyRecoveredRealOperation(AgentRealOperationResponse status, bool inventoryRefreshed)
    {
        var recovered = true;
        if (RealOperationRecovered is { } handlers)
            foreach (Func<AgentRealOperationResponse, bool, bool> handler in handlers.GetInvocationList())
                recovered &= handler(status, inventoryRefreshed);
        return recovered;
    }

    private Task<bool> RefreshRecoveredRealOperationAsync(AgentRealOperationResponse status,
        CancellationToken cancellationToken)
    {
        var local = SystemCatalog.Systems.FirstOrDefault(document => document.IsLocal
            && document.SystemId == status.Plan.SystemId);
        var anchors = status.Plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (local?.SourceFacts is not { } facts || anchors.Length == 0) return Task.FromResult(false);
        var scope = StorageInventoryScopeFactory.Create(facts, status.Plan.OperationId, null, anchors);
        return RefreshRealOperationScopeAsync(scope, cancellationToken);
    }

    public async Task<bool> ReadPublishedRealInventoryAsync(OperationId? operationId = null,
        CancellationToken cancellationToken = default)
    {
        bool Matches(StorageSystemDocument document) => operationId is null
            || document.SourceFacts?.ScopedCollection is { Complete: true } scoped
                && scoped.Scope.OperationId == operationId;
        if (operationId is not null && Matches(ActiveDocument)) return true;
        var document = await _machineRecordService.LoadLocalScanAsync(cancellationToken);
        if (document is null || !Matches(document)) return false;
        ApplyLocalInventory(document);
        return true;
    }
}
