using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class AgentBackedWorkspaceStateServiceTests
{
    [Fact]
    public async Task LoadMapsAgentContractToWorkspaceUiState()
    {
        var connection = new WorkspaceConnection(State());
        var service = new AgentBackedWorkspaceStateService(connection);

        var loaded = Assert.IsType<WorkspaceUiState>(await service.LoadAsync());

        Assert.Equal("Test", loaded.ShellPage);
        Assert.Equal("simulation:test", loaded.ActiveSystemId);
        Assert.Equal(WorkspaceCategory.Partition, loaded.Category);
        Assert.Equal("partition:selected", loaded.HighlightedTopologyStableId);
        Assert.Equal(
            "partition:remembered",
            loaded.CategorySelections![WorkspaceCategory.Partition]);
        Assert.IsType<LoadAgentWorkspaceStateRequest>(connection.Requests[0]);
    }

    [Fact]
    public async Task SaveMapsWorkspaceUiStateToAgentContractWithoutFallback()
    {
        var connection = new WorkspaceConnection(null);
        var service = new AgentBackedWorkspaceStateService(connection);
        var state = new WorkspaceUiState(
            "Monitor",
            "simulation:test",
            WorkspaceCategory.Pool,
            new Dictionary<WorkspaceCategory, string>
            {
                [WorkspaceCategory.Pool] = "pool:test"
            },
            "pool:highlighted");

        await service.SaveAsync(state);

        var request = Assert.IsType<SaveAgentWorkspaceStateRequest>(connection.Requests[0]);
        Assert.Equal(WorkspacePage.Monitor, request.State.ActivePage);
        Assert.Equal(ManageWorkspaceCategory.Pool, request.State.ActiveCategory);
        Assert.Equal("pool:test", request.State.RememberedProviderKeys[ManageWorkspaceCategory.Pool]);
        Assert.Equal("pool:highlighted", request.State.HighlightedTopologyProviderKey);
    }

    private static WorkspaceSessionState State() =>
        new(
            WorkspaceSessionState.CurrentSchemaVersion,
            WorkspacePage.Test,
            "simulation:test",
            ManageWorkspaceCategory.Partition,
            new Dictionary<ManageWorkspaceCategory, string>
            {
                [ManageWorkspaceCategory.Partition] = "partition:remembered"
            },
            "partition:selected",
            DateTimeOffset.UtcNow);

    private sealed class WorkspaceConnection(WorkspaceSessionState? initial) : IAgentConnection
    {
        private WorkspaceSessionState? current = initial;
        public List<AgentRequest> Requests { get; } = [];

        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentEvent> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<ApplicationResult<AgentResponse>> SendAsync(
            AgentRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AgentResponse response = request switch
            {
                LoadAgentWorkspaceStateRequest => new WorkspaceStateLoadedResponse(current),
                SaveAgentWorkspaceStateRequest save => Save(save.State),
                _ => throw new NotSupportedException()
            };
            return Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
        }

        private WorkspaceStateSavedResponse Save(WorkspaceSessionState state)
        {
            current = state;
            return new WorkspaceStateSavedResponse(state);
        }
    }
}
