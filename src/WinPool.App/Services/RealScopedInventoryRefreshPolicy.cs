using System.Text.Json;
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
            || active.InventoryVersion != response.InventoryVersion)
            return false;

        // A projection fingerprint alone does not cover all source evidence or retained-cache metadata.
        return JsonSerializer.Serialize(currentFacts) == JsonSerializer.Serialize(responseFacts);
    }
}
