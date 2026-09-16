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
        observer = new LocalInventoryObserver(connection, ApplyAsync, ReportFailure);
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
                try { await observer!.RunAsync(viewModel.WhenWorkspaceReady, cancellation.Token); }
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

    private void ReportFailure(string code, Exception? exception) => dispatcher.TryEnqueue(() =>
    {
        if (cancellation.IsCancellationRequested) return;
        try { DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot, "inventory-refresh.jsonl", code, exception); }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException) { }
        viewModel.NotificationService.PublishWarning(
            viewModel.Localization.IsChinese ? "本机数据刷新未完成，保留上次数据" : "Local inventory refresh incomplete; previous data retained",
            code, "inventory", code);
    });

    public void Dispose() => cancellation.Cancel();
}
