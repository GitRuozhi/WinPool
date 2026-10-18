using System.Runtime.CompilerServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>One control-body owner coalesces references from capture replies and events.</summary>
public static class AgentInventoryReader
{
    private static readonly ConditionalWeakTable<IAgentConnection, BodyCache> Caches = new();
    private static readonly ConditionalWeakTable<StorageSystemDocument, Revision> Revisions = new();
    public static string RevisionOf(StorageSystemDocument document) => Revisions.TryGetValue(document, out var revision)
        ? revision.Hash : throw new InvalidOperationException("The inventory was not loaded by the control body owner.");

    public static async Task<StorageSystemDocument> CaptureAsync(IAgentConnection connection,
        AgentRequest request, CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is not ManageInventoryCaptureResponse receipt)
        {
            var message = result.Messages.FirstOrDefault();
            throw new InventoryScanException("The Agent local inventory request failed.",
                !string.IsNullOrWhiteSpace(message?.DiagnosticText) ? message.DiagnosticText : message?.Code ?? string.Empty);
        }
        return await LoadAsync(connection, receipt.Revision, cancellationToken)
            ?? throw new InventoryScanException("The Agent inventory body was not available.", "agent.inventory.cache_missing");
    }

    public static Task<StorageSystemDocument?> LoadLatestAsync(IAgentConnection connection, CancellationToken cancellationToken) =>
        LoadAsync(connection, null, cancellationToken);

    public static Task<StorageSystemDocument?> LoadReferenceAsync(IAgentConnection connection,
        LocalInventoryDocumentReference reference, CancellationToken cancellationToken) =>
        LoadAsync(connection, reference.Sha256, cancellationToken);

    private static async Task<StorageSystemDocument?> LoadAsync(IAgentConnection connection, string? expectedHash,
        CancellationToken cancellationToken)
    {
        var cache = Caches.GetValue(connection, _ => new());
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedHash is not null && cache.Hash == expectedHash) return cache.Document;
            var result = await connection.SendAsync(new LoadAgentInventoryCacheRequest(CorrelationId.New()), cancellationToken);
            if (!result.IsSuccess || result.Value is not ManageInventoryLoadedResponse loaded)
                throw new InventoryScanException("The Agent cached inventory request failed.", result.Messages.FirstOrDefault()?.Code ?? string.Empty);
            if (loaded.Document is null) return null;
            if (cache.Hash == loaded.Document.Sha256) return cache.Document;
            await DocumentPayloadLimits.LargeParseGate.WaitAsync(cancellationToken);
            try
            {
                var document = await Task.Run(() => LocalInventoryDocumentCodec.Decode(loaded.Document), cancellationToken);
                Revisions.Add(document, new(loaded.Document.Sha256));
                cache.Hash = loaded.Document.Sha256;
                cache.Document = document;
                return document;
            }
            finally { DocumentPayloadLimits.LargeParseGate.Release(); }
        }
        finally { cache.Gate.Release(); }
    }

    private sealed record Revision(string Hash);

    private sealed class BodyCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Hash { get; set; }
        public StorageSystemDocument? Document { get; set; }
    }
}
