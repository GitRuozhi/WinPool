using Microsoft.UI.Dispatching;
using WinPool.Application;
using WinPool.App.ViewModels;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

internal sealed class AgentInventorySynchronizer : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly LocalInventoryObserver? observer;
    private readonly DispatcherQueue dispatcher;
    private readonly WorkspaceViewModel viewModel;

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
                if (!cancellation.IsCancellationRequested) viewModel.ApplyLocalInventory(document);
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
            if (viewModel.NotificationService.Notifications.Any(n =>
                n.DeduplicationKey == AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Storage)
                || n.DeduplicationKey == AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Hardware)))
                ReportFailureCore("inventory.events.reconnected", null);
            return;
        }
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
        new ApplicationNotificationPresenter(viewModel.NotificationService, viewModel.Localization).Present(notification);
    });

    private void ReportFailure(string code, Exception? exception) => dispatcher.TryEnqueue(() => ReportFailureCore(code, exception));

    private void ReportFailureCore(string code, Exception? exception)
    {
        if (cancellation.IsCancellationRequested) return;
        viewModel.NotificationService.DismissByKey(AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Storage));
        viewModel.NotificationService.DismissByKey(AutomaticInventoryNotification.ProgressKey(CollectionPurpose.Hardware));
        LogFailure(code, exception);
        viewModel.NotificationService.PublishWarning(
            viewModel.Localization.IsChinese ? "本机数据刷新未完成，保留上次数据" : "Local inventory refresh incomplete; previous data retained",
            code, "inventory", code);
    }

    private static void LogFailure(string code, Exception? exception)
    {
        try { DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot, "inventory-refresh.jsonl", code, exception); }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => cancellation.Cancel();
}
