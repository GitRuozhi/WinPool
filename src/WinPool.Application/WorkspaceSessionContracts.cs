namespace WinPool.Application;

public sealed record WorkspaceSessionState(
    int SchemaVersion,
    WorkspacePage ActivePage,
    string ActiveDocumentId,
    ManageWorkspaceCategory ActiveCategory,
    IReadOnlyDictionary<ManageWorkspaceCategory, string> RememberedProviderKeys,
    string HighlightedTopologyProviderKey,
    DateTimeOffset UpdatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public static class WorkspaceSessionStateValidator
{
    public static bool IsValid(WorkspaceSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SchemaVersion == WorkspaceSessionState.CurrentSchemaVersion
            && IsBounded(state.ActiveDocumentId, 512, allowEmpty: true)
            && IsBounded(state.HighlightedTopologyProviderKey, 1024, allowEmpty: true)
            && state.RememberedProviderKeys is not null
            && state.RememberedProviderKeys.Count <= Enum.GetValues<ManageWorkspaceCategory>().Length
            && state.RememberedProviderKeys.All(pair =>
                Enum.IsDefined(pair.Key)
                && IsBounded(pair.Value, 1024, allowEmpty: false))
            && Enum.IsDefined(state.ActivePage)
            && Enum.IsDefined(state.ActiveCategory)
            && state.UpdatedAtUtc != default;
    }

    private static bool IsBounded(string? value, int maximum, bool allowEmpty) =>
        value is not null
        && value.Length <= maximum
        && (allowEmpty || !string.IsNullOrWhiteSpace(value));
}
