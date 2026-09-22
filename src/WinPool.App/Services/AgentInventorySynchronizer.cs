using Microsoft.UI.Dispatching;
using WinPool.Application;
using WinPool.App.ViewModels;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

internal sealed class AgentInventorySynchronizer : IDisposable
{
    private const string ConnectionFaultKey = "inventory.events.disconnected";
    private const string ConnectionRecoveredKey = "inventory.events.reconnected";
    private const string ReloadFaultKey = "inventory.report.reload_failed";
    private const string ReloadRecoveredKey = "inventory.report.reload_recovered";

    private readonly CancellationTokenSource cancellation = new();
    private readonly LocalInventoryObserver? observer;
    private readonly DispatcherQueue dispatcher;
    private readonly WorkspaceViewModel viewModel;
    private bool connectionFaultActive;
    private bool reloadFaultActive;

    public AgentInventorySynchronizer(WorkspaceViewModel viewModel, IAgentConnection? connection, DispatcherQueue dispatcher)
    {
        this.viewModel = viewModel;
        this.dispatcher = dispatcher;
        if (connection is null) return;
        observer = new LocalInventoryObserver(connection, ApplyAsync, ReportFailure, ReportCapture);
        _ = RunAsync();
    }

    public Task LoadHistoryAsync() => observer?.LoadHistoryAsync(() => Task.Run(() =>
        new ReadOnlyLocalInventoryReader(Path.Combine(StorageDataLocations.CurrentRoot, "winpool.db"))
            .LoadAsync(cancellation.Token))) ?? Task.CompletedTask;

    private async Task RunAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await observer!.RunAsync(viewModel.WhenWorkspaceReady, cancellation.Token);
                    if (!cancellation.IsCancellationRequested) ReportFailure("inventory.events.disconnected", null);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception) { ReportFailure("inventory.events.disconnected", exception); }
                // A bounded subscription may close after an event gap. Resubscribe and
                // reload the committed report so refreshes do not silently stop forever.
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private Task ApplyAsync(StorageSystemDocument document)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (!cancellation.IsCancellationRequested)
                {
                    viewModel.ApplyLocalInventory(document);
                    ReportReloadRecoveredAfterApplyCore();
                }
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetCanceled();
        return completion.Task.WaitAsync(cancellation.Token);
    }

    private void ReportCapture(AgentEvent report) => dispatcher.TryEnqueue(() =>
    {
        if (cancellation.IsCancellationRequested) return;
        if (report is AgentInventoryFailedEvent failedReport) LogFailure($"{failedReport.Code}.{failedReport.Purpose}", null);
        if (report is AgentStateReseedEvent)
        {
            // A reseed proves that the event transport is reachable, but its
            // following cache reload is not a new capture and must never be
            // presented as inventory success.
            ClearAutomaticProgressWithUnknownOutcome();
            ReportTransportRecoveredCore();
            return;
        }

        // Any lifecycle event is also a real transport recovery. It deliberately
        // says nothing about the capture result; success remains tied to Updated.
        ReportTransportRecoveredCore();
        var notification = AutomaticInventoryNotification.FromEvent(report);
        if (notification is null) return;
        var purpose = report switch
        {
            AgentInventoryStartedEvent started => started.Purpose,
            AgentInventoryUpdatedEvent updated => updated.Purpose,
            AgentInventoryFailedEvent failed => failed.Purpose,
            _ => throw new InvalidOperationException("Unexpected inventory event.")
        };
        viewModel.NotificationService.DismissByKey(AutomaticInventoryNotification.ProgressKey(purpose));
        if (report is AgentInventoryUpdatedEvent)
        {
            viewModel.NotificationService.ResolveByKey(AutomaticInventoryNotification.FailedKey(purpose));
            viewModel.NotificationService.ResolveByKey(AutomaticInventoryNotification.InterruptedKey(purpose));
        }
        new ApplicationNotificationPresenter(viewModel.NotificationService, viewModel.Localization).Present(notification);
    });

    private void ReportFailure(string code, Exception? exception) => dispatcher.TryEnqueue(() => ReportFailureCore(code, exception));

    private void ReportFailureCore(string code, Exception? exception)
    {
        if (cancellation.IsCancellationRequested) return;
        ClearAutomaticProgressWithUnknownOutcome();
        LogFailure(code, exception);
        var isConnectionFault = IsConnectionFault(code);
        if (isConnectionFault)
        {
            connectionFaultActive = true;
        }
        if (code.Equals(ReloadFaultKey, StringComparison.Ordinal))
        {
            reloadFaultActive = true;
        }
        viewModel.NotificationService.PublishWarning(
            viewModel.Localization.IsChinese ? "本机数据刷新未完成，保留上次数据" : "Local inventory refresh incomplete; previous data retained",
            code,
            "inventory",
            isConnectionFault ? ConnectionFaultKey : code,
            autoDismiss: false,
            options: new GlobalNotificationOptions
            {
                Code = code,
                Detail = code
            });
    }

    private void ReportTransportRecoveredCore()
    {
        if (!connectionFaultActive)
        {
            return;
        }

        connectionFaultActive = false;
        viewModel.NotificationService.ResolveByKey(ConnectionFaultKey);
        viewModel.NotificationService.PublishInfo(
            viewModel.Localization.IsChinese
                ? "与本机 Agent 的连接已恢复；未将缓存重读视为新的采集成功"
                : "Connection to the local Agent was restored; cached data was not treated as a new collection success",
            string.Empty,
            "inventory",
            ConnectionRecoveredKey,
            autoDismiss: true,
            options: new GlobalNotificationOptions { Code = ConnectionRecoveredKey });
    }

    private void ClearAutomaticProgressWithUnknownOutcome()
    {
        foreach (var purpose in new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware })
        {
            var progressKey = AutomaticInventoryNotification.ProgressKey(purpose);
            if (!viewModel.NotificationService.Notifications.Any(notification =>
                    notification.DeduplicationKey.Equals(progressKey, StringComparison.Ordinal)))
            {
                continue;
            }

            viewModel.NotificationService.DismissByKey(progressKey);
            viewModel.NotificationService.PublishWarning(
                viewModel.Localization.IsChinese
                    ? $"自动{PurposeText(purpose, true)}采集的结果未知，已保留上一次数据"
                    : $"Automatic {PurposeText(purpose, false)} collection has an unknown outcome; previous data was retained",
                string.Empty,
                "inventory",
                AutomaticInventoryNotification.InterruptedKey(purpose),
                autoDismiss: false,
                options: new GlobalNotificationOptions
                {
                    Code = "inventory.automatic.outcome_unknown",
                    Detail = progressKey
                });
        }
    }

    private static bool IsConnectionFault(string code) =>
        code.Equals(ConnectionFaultKey, StringComparison.Ordinal);

    private void ReportReloadRecoveredAfterApplyCore()
    {
        if (!reloadFaultActive)
        {
            return;
        }

        reloadFaultActive = false;
        viewModel.NotificationService.ResolveByKey(ReloadFaultKey);
        viewModel.NotificationService.Publish(
            GlobalNotificationSeverity.Info,
            viewModel.Localization.IsChinese ? "本机数据重读已恢复" : "Local inventory reload recovered",
            viewModel.Localization.IsChinese
                ? "已成功应用一份本机数据；这不是新的采集成功通知。"
                : "A local inventory document was applied; this is not a new collection-success notification.",
            "inventory",
            new GlobalNotificationOptions
            {
                OccurrenceKey = ReloadRecoveredKey,
                ShowNotification = false,
                RecordInHistory = true,
                Code = ReloadRecoveredKey
            });
    }

    private static string PurposeText(CollectionPurpose purpose, bool chinese) => purpose switch
    {
        CollectionPurpose.Hardware => chinese ? "硬件" : "hardware",
        _ => chinese ? "存储" : "storage"
    };

    private static void LogFailure(string code, Exception? exception)
    {
        try { DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot, "inventory-refresh.jsonl", code, exception); }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => cancellation.Cancel();
}
