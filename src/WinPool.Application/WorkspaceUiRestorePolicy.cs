namespace WinPool.Application;

/// <summary>
/// Decides when a persisted Manage-page selection has been fully applied
/// and when it is safe to write workspace UI state back. Restore must not
/// persist defaults over a saved selection while the catalog is still empty.
/// </summary>
public static class WorkspaceUiRestorePolicy
{
    public static bool ShouldPersist(bool restoreInProgress, bool persistAllowed) =>
        !restoreInProgress && persistAllowed;

    public static string? WantedObjectKey(WorkspaceUiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.CategorySelections is not null
            && state.CategorySelections.TryGetValue(state.Category, out var key)
            && !string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        return string.IsNullOrWhiteSpace(state.HighlightedTopologyStableId)
            ? null
            : state.HighlightedTopologyStableId;
    }

    public static bool IsRestoreSatisfied(
        WorkspaceUiState? state,
        string currentSystemId,
        bool savedSystemExists,
        bool rememberedObjectResolved)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.ActiveSystemId))
        {
            return true;
        }

        if (!savedSystemExists)
        {
            return true;
        }

        if (!currentSystemId.Equals(state.ActiveSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var wanted = WantedObjectKey(state);
        return string.IsNullOrWhiteSpace(wanted) || rememberedObjectResolved;
    }
}
