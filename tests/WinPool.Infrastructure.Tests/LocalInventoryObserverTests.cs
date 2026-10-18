using System.Threading.Channels;
using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class LocalInventoryObserverTests
{
    [Fact]
    public async Task HistoryPrecedesReadyAndStartupReferencesLoadOnlyOneLatestBody()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection { Cached = Payload(2, "latest") };
        var applied = new List<string>();
        var reports = new List<AgentEvent>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new LocalInventoryObserver(connection, document => { applied.Add(document.DisplayName); return Task.CompletedTask; },
            (code, exception) => throw new Exception(code, exception), reports.Add);
        var watching = observer.RunAsync(ready.Task, timeout.Token);
        await observer.LoadHistoryAsync(() => Task.FromResult<LocalInventoryDocumentPayload?>(Payload(1, "history")));
        Assert.Equal(new[] { "history" }, applied);
        Assert.Empty(connection.Requests);
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Storage, connection.Cached));
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Hardware, connection.Cached));
        connection.Events.Writer.TryComplete();
        ready.SetResult();
        await watching.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "history", "latest" }, applied);
        Assert.Equal(2, reports.Count);
        Assert.Single(connection.Requests);
    }

    [Fact]
    public async Task SupersededReferenceUsesLatestIdentityWithoutClaimingItsOldHash()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection();
        var displayed = Channel.CreateUnbounded<string>();
        var observer = new LocalInventoryObserver(connection, document => { displayed.Writer.TryWrite(document.DisplayName); return Task.CompletedTask; },
            (code, exception) => throw new Exception(code, exception));
        var watching = observer.RunAsync(Task.CompletedTask, timeout.Token);
        connection.Cached = Payload(2, "latest");
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Storage, Payload(1, "superseded")));
        Assert.Equal("latest", await displayed.Reader.ReadAsync(timeout.Token));
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Hardware, connection.Cached));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.False(displayed.Reader.TryRead(out _));
        Assert.Equal(2, connection.Requests.Count); // initial empty cache, then one referenced body
    }

    [Fact]
    public async Task CorruptReferencedBodyFailsAndLaterGoodBodyStillApplies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new List<string>();
        var observer = new LocalInventoryObserver(connection, document => { displayed.Add(document.DisplayName); return Task.CompletedTask; },
            (_, _) => failed.TrySetResult());
        var watching = observer.RunAsync(Task.CompletedTask, timeout.Token);
        connection.Cached = Payload(1, "corrupt") with { Sha256 = new string('0', 64) };
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Storage, connection.Cached));
        await failed.Task.WaitAsync(timeout.Token);
        connection.Cached = Payload(2, "recovered");
        connection.Events.Writer.TryWrite(Updated(CollectionPurpose.Hardware, connection.Cached));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "recovered" }, displayed);
    }

    [Fact]
    public async Task CaptureAndObserverShareTheSameRevisionBody()
    {
        var connection = new Connection { Cached = Payload(1, "captured") };
        var first = await AgentInventoryReader.LoadReferenceAsync(connection, Reference(connection.Cached), CancellationToken.None);
        var second = await new AgentBackedHardwareInventoryProvider(connection).CollectLocalAsync(CancellationToken.None);
        Assert.Same(first, second);
        Assert.Collection(connection.Requests,
            request => Assert.IsType<LoadAgentInventoryCacheRequest>(request),
            request => Assert.IsType<CaptureAgentManageInventoryRequest>(request));
    }

    [Fact]
    public async Task ClosingBeforeAgentReadyCancelsSubscriptionWithoutLoading()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new Connection();
        var observer = new LocalInventoryObserver(connection, _ => Task.CompletedTask, (_, _) => { });
        var watching = observer.RunAsync(new TaskCompletionSource().Task, cancellation.Token);
        cancellation.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(connection.Requests);
    }

    private static LocalInventoryDocumentReference Reference(LocalInventoryDocumentPayload payload) =>
        new(payload.DocumentId, payload.Sha256, payload.CapturedAtUtc);
    private static AgentInventoryUpdatedEvent Updated(CollectionPurpose purpose, LocalInventoryDocumentPayload payload) =>
        new(purpose, Reference(payload), DateTimeOffset.UtcNow);
    private static LocalInventoryDocumentPayload Payload(int second, string name) => LocalInventoryDocumentCodec.Encode(
        new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:test", StorageSystemKind.Local,
            name, StorageSnapshot.Empty("Test"), [], DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + second)));

    private sealed class Connection : IAgentConnection
    {
        public Channel<AgentEvent> Events { get; } = Channel.CreateUnbounded<AgentEvent>();
        public List<AgentRequest> Requests { get; } = [];
        public LocalInventoryDocumentPayload? Cached { get; set; }
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) => Events.Reader.ReadAllAsync(cancellationToken);
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AgentResponse response = request switch
            {
                LoadAgentInventoryCacheRequest => new ManageInventoryLoadedResponse(null, Cached),
                CaptureAgentManageInventoryRequest => new ManageInventoryCaptureResponse(Guid.NewGuid(), Cached!.DocumentId, Cached.Sha256, 1),
                _ => throw new InvalidOperationException("Unexpected request")
            };
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
        }
    }
}
