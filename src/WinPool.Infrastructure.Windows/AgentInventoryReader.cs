using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>Inventory bodies arrive on the event stream; a missing event has one cache read.</summary>
public static class AgentInventoryReader
{
    public static async Task<StorageSystemDocument> CaptureAsync(IAgentConnection connection,
        AgentRequest request, CancellationToken cancellationToken)
    {
        using var eventsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var events = connection.WatchAsync(eventsCancellation.Token)
            .GetAsyncEnumerator(eventsCancellation.Token);
        var next = events.MoveNextAsync().AsTask();
        try
        {
            var result = await connection.SendAsync(request, cancellationToken);
            if (!result.IsSuccess || result.Value is not ManageInventoryCaptureResponse receipt)
            {
                var message = result.Messages.FirstOrDefault();
                throw new InventoryScanException("The Agent local inventory request failed.",
                    !string.IsNullOrWhiteSpace(message?.DiagnosticText)
                        ? message.DiagnosticText : message?.Code ?? string.Empty);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                while (await next.WaitAsync(deadline.Token))
                {
                    if (events.Current is AgentInventoryUpdatedEvent updated
                        && updated.Document.DocumentId == receipt.DocumentId
                        && updated.Document.Sha256 == receipt.Revision)
                        return LocalInventoryDocumentCodec.Decode(updated.Document);
                    next = events.MoveNextAsync().AsTask();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            return await new AgentBackedMachineRecordService(connection).LoadLocalScanAsync(cancellationToken)
                ?? throw new InventoryScanException("The Agent inventory body was not available.",
                    "agent.inventory.event_missing");
        }
        finally
        {
            eventsCancellation.Cancel();
            try { await next; }
            catch (OperationCanceledException) when (eventsCancellation.IsCancellationRequested) { }
        }
    }
}
