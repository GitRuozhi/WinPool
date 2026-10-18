using WinPool.Application;

namespace WinPool.App.Services;

/// <summary>The observer and capture reply may deliver the same committed batch in either order.</summary>
public static class RealScopedInventoryRefreshPolicy
{
    public static async Task<bool> GuardInvalidDataAsync(Func<Task<bool>> refresh,
        Action<InvalidDataException> reportFailure)
    {
        try { return await refresh(); }
        catch (InvalidDataException exception)
        {
            reportFailure(exception);
            return false;
        }
    }

    public static bool IsCurrentCompleteBatch(StorageSystemDocument active,
        StorageSystemDocument response, StorageInventoryScope requested)
    {
        if (!active.IsLocal || !response.IsLocal || active.Id != response.Id
            || active.SystemId != requested.SystemId || response.SystemId != requested.SystemId
            || active.UpdatedAt != response.UpdatedAt
            || active.SourceFacts is not { ScopedCollection: { Complete: true } current } currentFacts
            || response.SourceFacts is not { ScopedCollection: { Complete: true } returned } responseFacts
            || returned.Scope.SystemId != requested.SystemId
            || returned.Scope.OperationId != requested.OperationId
            || returned.Scope.StepId != requested.StepId
            || !returned.Scope.Targets.ToHashSet().SetEquals(requested.Targets)
            || current.Scope.SystemId != returned.Scope.SystemId
            || current.Scope.OperationId != returned.Scope.OperationId
            || current.Scope.StepId != returned.Scope.StepId
            || current.Scope.Generation != returned.Scope.Generation
            || current.StartedAt != returned.StartedAt || current.CompletedAt != returned.CompletedAt
            || currentFacts.InventoryCapturedAt != responseFacts.InventoryCapturedAt
            || currentFacts.InventoryVersion != responseFacts.InventoryVersion
            || currentFacts.ObservationId == Guid.Empty
            || currentFacts.ObservationId != responseFacts.ObservationId
            || currentFacts.Revision != responseFacts.Revision
            || currentFacts.IsMerged != responseFacts.IsMerged)
            return false;

        // Both deliveries came through the validated Agent document boundary.
        // The observation owner identifies the complete facts, including source
        // evidence and retained-cache metadata, without re-encoding the body.
        return true;
    }
}
