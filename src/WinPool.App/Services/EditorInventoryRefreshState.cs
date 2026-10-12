using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>Coalesces observer reports with inventory batches already handled by an editor.</summary>
public sealed class EditorInventoryRefreshState
{
    private SystemId? systemId;
    private long revision;
    private DateTimeOffset updatedAt;
    private string? inventoryVersion;

    public bool IsSameSystem(StorageSystemDocument document) => systemId == document.SystemId;

    public bool HasChanged(StorageSystemDocument document) => !IsSameSystem(document)
        || revision != document.Revision || updatedAt != document.UpdatedAt
        || inventoryVersion != document.InventoryVersion;

    public void Remember(StorageSystemDocument document)
    {
        systemId = document.SystemId;
        revision = document.Revision;
        updatedAt = document.UpdatedAt;
        inventoryVersion = document.InventoryVersion;
    }

    public bool NotifyChangedSameSystem(StorageSystemDocument document, Action refresh)
    {
        if (!IsSameSystem(document) || !HasChanged(document)) return false;
        // Remember first so a callback cannot recursively notify the same batch.
        // The editor decides whether to bind, preserve a dirty draft, or defer
        // while its exact real-operation result is being synchronized.
        Remember(document);
        refresh();
        return true;
    }
}
