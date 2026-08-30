using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Bridges the accepted V0.13 UI-state shape to the Agent-owned V0.2
/// workspace-session contract. It never falls back to or dual-writes JSON.
/// </summary>
public sealed class AgentBackedWorkspaceStateService(IAgentConnection connection)
    : IWorkspaceStateService
{
    private readonly IAgentConnection connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<WorkspaceUiState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new LoadAgentWorkspaceStateRequest(CorrelationId.New()),
            cancellationToken);
        if (response is not WorkspaceStateLoadedResponse loaded)
        {
            throw new InvalidDataException("The Agent returned an unexpected workspace response.");
        }
        return loaded.State is null ? null : Decode(loaded.State);
    }

    public async Task SaveAsync(
        WorkspaceUiState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = Encode(state);
        var response = await SendAsync(
            new SaveAgentWorkspaceStateRequest(payload, CorrelationId.New()),
            cancellationToken);
        if (response is not WorkspaceStateSavedResponse saved
            || !WorkspaceSessionStateValidator.IsValid(saved.State))
        {
            throw new InvalidDataException("The Agent returned an unexpected workspace save response.");
        }
    }

    internal static WorkspaceSessionState Encode(WorkspaceUiState state)
    {
        var page = Enum.TryParse<WorkspacePage>(state.ShellPage, out var parsedPage)
            ? parsedPage
            : WorkspacePage.Manage;
        return new WorkspaceSessionState(
            WorkspaceSessionState.CurrentSchemaVersion,
            page,
            state.ActiveSystemId?.Trim() ?? string.Empty,
            state.Category,
            (state.CategorySelections ?? new Dictionary<ManageWorkspaceCategory, string>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim()),
            state.HighlightedTopologyStableId?.Trim() ?? string.Empty,
            DateTimeOffset.UtcNow);
    }

    internal static WorkspaceUiState Decode(WorkspaceSessionState state)
    {
        if (!WorkspaceSessionStateValidator.IsValid(state))
        {
            throw new InvalidDataException("The stored workspace state is invalid.");
        }
        return new WorkspaceUiState(
            state.ActivePage.ToString(),
            state.ActiveDocumentId,
            state.ActiveCategory,
            state.RememberedProviderKeys.ToDictionary(
                pair => pair.Key,
                pair => pair.Value),
            state.HighlightedTopologyProviderKey);
    }

    private async Task<AgentResponse> SendAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await connection.SendAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            throw new IOException(
                result.Messages.FirstOrDefault()?.Code
                ?? "The Agent workspace persistence request failed.");
        }
        return result.Value;
    }

}
