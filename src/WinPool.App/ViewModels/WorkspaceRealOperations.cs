using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.App.Services;

namespace WinPool.App.ViewModels;

public sealed partial class WorkspaceViewModel
{
    private readonly Dictionary<OperationId, string> _recoveredRealOperationFeedback = [];
    public bool IsRealOperationBusy { get; private set; }
    public bool IsRealGraphObscured { get; private set; }
    public string RealOperationPhase { get; private set; } = string.Empty;
    public AgentRealOperationResponse? LastRealOperationStatus { get; private set; }
    public event Action? RealOperationActivityChanged;

    public void SetRealOperationActivity(bool busy, bool obscure, string phase)
    {
        IsRealOperationBusy = busy;
        IsRealGraphObscured = obscure;
        RealOperationPhase = phase;
        RealOperationActivityChanged?.Invoke();
    }

    public void ObserveRealOperation(AgentRealOperationResponse status)
    {
        LastRealOperationStatus = status;
        RealOperationSubmission.Observe(status);
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

    public Task<bool> RefreshRealOperationScopeAsync(StorageInventoryScope scope) =>
        RealScopedInventoryRefreshPolicy.GuardInvalidDataAsync(
            () => RefreshRealOperationScopeCoreAsync(scope),
            exception => ReportScopedRefreshFailure(scope, exception));

    private async Task<bool> RefreshRealOperationScopeCoreAsync(StorageInventoryScope scope)
    {
        if (_agentConnection is null) return false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ScanError = string.Empty;
            var result = await _agentConnection.SendAsync(
                new CaptureAgentManageScopedInventoryRequest(scope, CorrelationId.New()), CancellationToken.None);
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
            "磁盘操作结果已记录，但相关视图未刷新；请查询结果并重试只读刷新。 / The operation result is recorded, but its related view is stale. Query the result and retry a read-only refresh.",
            "real", $"real:refresh:{scope.Key}", options: new GlobalNotificationOptions { Detail = exception.Message });
    }

    public async Task RecoverRealOperationsAsync()
    {
        if (_agentConnection is null) return;
        try
        {
            var result = await _agentConnection.SendAsync(
                new ListAgentRecoverableRealOperationsRequest(CorrelationId.New()), CancellationToken.None);
            if (result.Value is not AgentRecoverableRealOperationsResponse recovered) return;
            foreach (var status in recovered.Operations)
            {
                _recoveredRealOperationFeedback.TryAdd(status.Plan.OperationId, string.Empty);
                ObserveRealOperation(status);
            }
            // Restart restores query/reconciliation only. It does not arm real
            // mode, reconstruct tokens, replay calls, or resume a draft.
            SetRealOperationActivity(false, false, recovered.Operations.Count == 0 ? string.Empty : "待核对 / Needs reconciliation");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            _notificationService.PublishWarning(Localization["Warning"], exception.Message,
                "real", "real:recover:unavailable");
        }
    }
}
