using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.App.Services;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel
{
    private readonly Dictionary<OperationId, string> _recoveredRealOperationFeedback = [];
    private readonly CancellationTokenSource _realOperationRecoveryCancellation = new();
    private Task? _realOperationRecoveryRefresh;
    private Task? _realOperationRecoveryPoll;
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
        IsRealOperationBusy = busy;
        var retained = !busy && RealOperationSubmission.IsBlocked;
        IsRealGraphObscured = obscure || retained;
        var chinese = Localization.EffectiveLanguage == LanguagePreference.ZhCn;
        RealOperationPhase = retained ? chinese ? "正在只读核对操作结果" : "Reconciling the operation result" : phase;
        if (owner is not null) RealOperationOwner = owner;
        if (!busy && !retained) RealOperationOwner = null;
        if (busy || retained)
            _notificationService.Publish(GlobalNotificationSeverity.Info,
                IsUsingSimulatedInventory ? chinese ? "编辑操作" : "Editing operation"
                    : chinese ? "真实磁盘操作" : "Real storage operation",
                RealOperationPhase, "editor",
                new GlobalNotificationOptions { OccurrenceKey = EditorProgressKey, IsProgress = true,
                    AutoDismiss = false, RecordInHistory = false });
        else _notificationService.DismissByKey(EditorProgressKey);
        RealOperationActivityChanged?.Invoke();
        if (!busy) EnsureRealOperationRecoveryPolling();
    }

    public void ObserveRealOperation(AgentRealOperationResponse status)
    {
        if (!RealOperationSubmission.Observe(status)) return;
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
            var result = await _agentConnection.SendAsync(
                new CaptureAgentManageScopedInventoryRequest(scope, CorrelationId.New()), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Value is not ManageInventoryCaptureResponse response)
                throw new InvalidDataException(result.Messages.FirstOrDefault()?.Code ?? "Scoped refresh failed.");
            var document = LocalInventoryDocumentCodec.Decode(response.Document);
            var complete = document.SourceFacts?.ScopedCollection is { Complete: true } collection
                && collection.Scope.SystemId == scope.SystemId
                && collection.Scope.OperationId == scope.OperationId
                && collection.Scope.StepId == scope.StepId;
            // Even an incomplete batch preserves the previous facts and exposes
            // its failure metadata. It cannot authorize a following segment.
            ApplyLocalInventory(document);
            if (!complete || !result.IsSuccess)
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
            "磁盘操作结果已记录，但相关视图未刷新；后台将继续只读核对。 / The operation result is recorded, but its related view is stale. Background read-only recovery will continue.",
            "real", $"real:refresh:{scope.Key}", options: new GlobalNotificationOptions { Detail = exception.Message });
    }

    public void StopRealOperationRecovery() => _realOperationRecoveryCancellation.Cancel();

    public async Task RecoverRealOperationsAsync()
    {
        if (_realOperationRecoveryCancellation.IsCancellationRequested || IsRealOperationBusy) return;
        if (_realOperationRecoveryRefresh is { } pending)
        {
            await pending;
            return;
        }
        _realOperationRecoveryRefresh = RecoverRealOperationsCoreAsync();
        try { await _realOperationRecoveryRefresh; }
        finally
        {
            _realOperationRecoveryRefresh = null;
            EnsureRealOperationRecoveryPolling();
        }
    }

    private async Task RecoverRealOperationsCoreAsync()
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
                }, cancellationToken);
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

    private void EnsureRealOperationRecoveryPolling()
    {
        if (_agentConnection is null || _realOperationRecoveryPoll is not null
            || _realOperationRecoveryCancellation.IsCancellationRequested
            || !RealOperationSubmission.IsBlocked || IsRealOperationBusy) return;
        _realOperationRecoveryPoll = PollRealOperationRecoveryAsync();
    }

    private async Task PollRealOperationRecoveryAsync()
    {
        var cancellationToken = _realOperationRecoveryCancellation.Token;
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (RealOperationSubmission.IsBlocked)
            {
                await Task.Delay(delay, cancellationToken);
                if (!IsRealOperationBusy) await RecoverRealOperationsAsync();
                delay = TimeSpan.FromSeconds(Math.Min(5, delay.TotalSeconds + 1));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { _realOperationRecoveryPoll = null; }
    }
}
