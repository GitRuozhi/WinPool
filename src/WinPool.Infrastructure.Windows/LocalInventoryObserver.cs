using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>Publishes history independently of IPC, then follows persisted Agent reports and reconnects.</summary>
public sealed class LocalInventoryObserver(
    IAgentConnection connection,
    Func<StorageSystemDocument, Task> apply,
    Action<string, Exception?> reportFailure,
    Action<AgentEvent>? reportCapture = null)
{
    public async Task LoadHistoryAsync(Func<Task<LocalInventoryDocumentPayload?>> read)
    {
        try
        {
            var payload = await read();
            if (payload is not null) await apply(LocalInventoryDocumentCodec.Decode(payload));
        }
        catch (Exception exception)
        {
            reportFailure("inventory.history.load_failed", exception);
        }
    }

    public async Task RunAsync(Task workspaceReady, CancellationToken cancellationToken)
    {
        await using var events = connection.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = events.MoveNextAsync().AsTask();
        try
        {
            await workspaceReady.WaitAsync(cancellationToken);
            await ReloadAsync(cancellationToken);
            while (await next)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    switch (events.Current)
                    {
                        case AgentInventoryStartedEvent started:
                            reportCapture?.Invoke(started);
                            break;
                        case AgentInventoryUpdatedEvent updated:
                            await apply(LocalInventoryDocumentCodec.Decode(updated.Document));
                            reportCapture?.Invoke(updated);
                            break;
                        case AgentInventoryFailedEvent failed:
                            if (reportCapture is not null) reportCapture(failed);
                            else reportFailure($"{failed.Code}.{failed.Purpose}", null);
                            break;
                        case AgentStateReseedEvent reseed:
                            reportCapture?.Invoke(reseed);
                            await ReloadAsync(cancellationToken);
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    reportFailure("inventory.report.apply_failed", exception);
                }
                next = events.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await next; } catch (OperationCanceledException) { }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await new AgentBackedMachineRecordService(connection).LoadLocalScanAsync(cancellationToken);
            if (document is not null) await apply(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            reportFailure("inventory.report.reload_failed", exception);
        }
    }
}
