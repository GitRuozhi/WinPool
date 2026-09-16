using System.Threading.Channels;
using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class LocalInventoryObserverTests
{
    [Fact]
    public async Task CaptureLifecycleIsForwardedButHistoryAndCacheAreNotCaptureSuccesses()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection { Cached = Payload(1, "cache") };
        var reports = new List<AgentEvent>();
        var failures = new List<string>();
        var applied = new List<string>();
        var observer = new LocalInventoryObserver(connection, document =>
        {
            applied.Add(document.DisplayName);
            return Task.CompletedTask;
        }, (code, _) => failures.Add(code), report =>
        {
            if (report is AgentInventoryUpdatedEvent) Assert.Contains("captured", applied);
            reports.Add(report);
        });
        await observer.LoadHistoryAsync(() => Task.FromResult<LocalInventoryDocumentPayload?>(Payload(0, "history")));
        var watching = observer.RunAsync(Task.CompletedTask, timeout.Token);
        Assert.Empty(reports);
        AgentEvent[] expected =
        [
            new AgentInventoryStartedEvent(CollectionPurpose.Storage, DateTimeOffset.UtcNow, true),
            new AgentInventoryUpdatedEvent(CollectionPurpose.Storage, Payload(2, "captured"), DateTimeOffset.UtcNow, true),
            new AgentInventoryStartedEvent(CollectionPurpose.Hardware, DateTimeOffset.UtcNow, true),
            new AgentInventoryFailedEvent(CollectionPurpose.Hardware, "test.failure", DateTimeOffset.UtcNow, true)
        ];
        foreach (var report in expected) connection.Events.Writer.TryWrite(report);
        // Invalid reports must never display a successful-capture notification.
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Hardware,
            Payload(3, "invalid") with { Sha256 = new string('0', 64) }, DateTimeOffset.UtcNow, true));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.Equal(expected, reports);
        Assert.Equal(new[] { "history", "cache", "captured" }, applied);
        Assert.Equal("inventory.report.apply_failed", Assert.Single(failures));
    }

    [Fact]
    public async Task HistoryIsVisibleBeforeAgentReadyThenBothReportsReplaceItWithoutRescanning()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection();
        var catalog = new StorageSystemCatalog();
        var displayed = Channel.CreateUnbounded<string>();
        var failures = new List<string>();
        var observer = CreateObserver(connection, catalog, displayed, failures);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = observer.RunAsync(ready.Task, timeout.Token);
        await observer.LoadHistoryAsync(() => Task.FromResult<LocalInventoryDocumentPayload?>(Payload(0, "history")));
        Assert.Equal("history", await displayed.Reader.ReadAsync(timeout.Token));
        Assert.Empty(connection.Requests);
        Assert.False(ready.Task.IsCompleted);

        // Reports may arrive while simulations/workspace restoration are still loading.
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Storage, Payload(1, "storage"), DateTimeOffset.UtcNow));
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Hardware, Payload(2, "hardware"), DateTimeOffset.UtcNow));
        ready.SetResult();
        Assert.Equal("storage", await displayed.Reader.ReadAsync(timeout.Token));
        Assert.Equal("hardware", await displayed.Reader.ReadAsync(timeout.Token));

        // Duplicate/late reports and refresh failures must preserve the complete report.
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Storage, Payload(1, "late-storage"), DateTimeOffset.UtcNow));
        connection.Events.Writer.TryWrite(new AgentInventoryFailedEvent(CollectionPurpose.Hardware, "test.failure", DateTimeOffset.UtcNow));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.Equal("hardware", Assert.Single(catalog.Systems).DisplayName);
        Assert.False(displayed.Reader.TryRead(out _));
        Assert.Single(failures);
        Assert.All(connection.Requests, request => Assert.IsType<LoadAgentManageInventoryRequest>(request));
    }

    [Fact]
    public async Task LateConnectionAndReconnectLoadLatestCacheEvenWithoutALiveCaptureEvent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection { Cached = Payload(2, "already-complete") };
        var catalog = new StorageSystemCatalog();
        var displayed = Channel.CreateUnbounded<string>();
        var failures = new List<string>();
        var observer = CreateObserver(connection, catalog, displayed, failures);
        await observer.LoadHistoryAsync(() => Task.FromResult<LocalInventoryDocumentPayload?>(null));
        var watching = observer.RunAsync(Task.CompletedTask, timeout.Token);
        Assert.Equal("already-complete", await displayed.Reader.ReadAsync(timeout.Token));
        connection.Cached = Payload(3, "reconnected");
        connection.Events.Writer.TryWrite(new AgentStateReseedEvent(null!, "test.reconnect", DateTimeOffset.UtcNow));
        Assert.Equal("reconnected", await displayed.Reader.ReadAsync(timeout.Token));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.Empty(failures);
        Assert.Equal(2, connection.Requests.Count);
    }

    [Fact]
    public async Task InvalidHistoryAndReportAreVisibleFailuresAndNextGoodReportStillApplies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new Connection();
        var catalog = new StorageSystemCatalog();
        var displayed = Channel.CreateUnbounded<string>();
        var failures = new List<string>();
        var observer = CreateObserver(connection, catalog, displayed, failures);
        var corrupt = Payload(0, "invalid") with { Sha256 = new string('0', 64) };
        await observer.LoadHistoryAsync(() => Task.FromResult<LocalInventoryDocumentPayload?>(corrupt));
        var watching = observer.RunAsync(Task.CompletedTask, timeout.Token);
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Storage, corrupt, DateTimeOffset.UtcNow));
        connection.Events.Writer.TryWrite(new AgentInventoryUpdatedEvent(CollectionPurpose.Hardware, Payload(1, "recovered"), DateTimeOffset.UtcNow));
        connection.Events.Writer.TryComplete();
        await watching.WaitAsync(timeout.Token);
        Assert.Equal("recovered", Assert.Single(catalog.Systems).DisplayName);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task ClosingBeforeAgentReadyCancelsPendingSubscriptionCleanly()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new Connection();
        var observer = new LocalInventoryObserver(connection, _ => Task.CompletedTask, (_, _) => { });
        var watching = observer.RunAsync(new TaskCompletionSource().Task, cancellation.Token);
        cancellation.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(connection.Requests);
    }

    [Fact]
    public async Task BothManualRefreshEntrypointsRequestTheirOwnPurpose()
    {
        var connection = new Connection { Cached = Payload(1, "manual") };
        var provider = new AgentBackedHardwareInventoryProvider(connection);
        await provider.CollectLocalAsync(CancellationToken.None);
        await provider.CollectHardwareAsync(CancellationToken.None);
        Assert.Equal(new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware },
            connection.Requests.Cast<CaptureAgentManageInventoryRequest>().Select(x => x.Purpose));
    }

    private static LocalInventoryObserver CreateObserver(Connection connection, StorageSystemCatalog catalog,
        Channel<string> displayed, List<string> failures) => new(connection, document =>
        {
            if (catalog.TryReplaceLocalReport(document)) displayed.Writer.TryWrite(document.DisplayName);
            return Task.CompletedTask;
        }, (code, _) => failures.Add(code));

    private static LocalInventoryDocumentPayload Payload(int second, string name) => LocalInventoryDocumentCodec.Encode(
        new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:test", StorageSystemKind.Local,
            name, StorageSnapshot.Empty("Test"), [], DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + second)));

    private sealed class Connection : IAgentConnection
    {
        public Channel<AgentEvent> Events { get; } = Channel.CreateUnbounded<AgentEvent>();
        public List<AgentRequest> Requests { get; } = [];
        public LocalInventoryDocumentPayload? Cached { get; set; }
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("History must not connect to the Agent.");
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) =>
            Events.Reader.ReadAllAsync(cancellationToken);
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AgentResponse response = request switch
            {
                LoadAgentManageInventoryRequest => new ManageInventoryLoadedResponse(null, Cached),
                CaptureAgentManageInventoryRequest => new ManageInventoryCaptureResponse(Guid.NewGuid(), Cached!),
                _ => throw new InvalidOperationException("Unexpected request")
            };
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
        }
    }
}
