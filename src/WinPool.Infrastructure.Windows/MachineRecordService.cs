using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public sealed class AgentBackedMachineRecordService(IAgentConnection connection)
    : IMachineRecordService
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public Task RecordLocalScanAsync(
        StorageSystemDocument localDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localDocument);
        if (!localDocument.IsLocal)
        {
            throw new InvalidOperationException("Only a local inventory document can be recorded.");
        }

        // AgentBackedHardwareInventoryProvider asks the Agent to persist the
        // same document before returning it to the workspace.
        return Task.CompletedTask;
    }

    public async Task<StorageSystemDocument?> LoadLocalScanAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await connection.SendAsync(
            new LoadAgentInventoryCacheRequest(CorrelationId.New()),
            cancellationToken);
        if (!result.IsSuccess
            || result.Value is not ManageInventoryLoadedResponse response)
        {
            throw new InventoryScanException(
                "The Agent cached inventory request failed.",
                result.Messages.FirstOrDefault()?.Code ?? string.Empty);
        }

        return LocalInventoryDocumentCodec.TryDecodeCached(response.Document);
    }
}
