using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Agent;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Ipc;

namespace WinPool.Agent.Client.Tests;

public sealed class NamedPipeAgentConnectionTests
{
    [Fact]
    public async Task EventTransportReconnectReportsGapAndReseedsSnapshot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var registry = new AgentProcessRegistry();
        var operations = new SnapshotOperations(sessionId);
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));

        using var firstCancellation = new CancellationTokenSource();
        var firstServer = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator);
        var firstTask = firstServer.RunAsync(firstCancellation.Token);
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());
        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);
        using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var events = connection.WatchAsync(watchTimeout.Token)
            .GetAsyncEnumerator(watchTimeout.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<AgentStateReseedEvent>(events.Current);

        firstCancellation.Cancel();
        try
        {
            await firstTask;
        }
        catch (OperationCanceledException)
        {
        }

        using var secondCancellation = new CancellationTokenSource();
        var secondServer = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator);
        var secondTask = secondServer.RunAsync(secondCancellation.Token);

        var states = new List<AgentEventTransportState>();
        var reseedAfterGap = false;
        while ((!reseedAfterGap || states.Count < 3) && await events.MoveNextAsync())
        {
            if (events.Current is AgentEventTransportStateEvent transport)
            {
                Assert.True(transport.HasEventGap);
                states.Add(transport.State);
            }
            else if (events.Current is AgentStateReseedEvent
                     && states.Contains(AgentEventTransportState.Reconnecting))
            {
                reseedAfterGap = true;
            }
        }

        Assert.Equal(
            [
                AgentEventTransportState.Disconnected,
                AgentEventTransportState.Reconnecting,
                AgentEventTransportState.Reconnected
            ],
            states);
        Assert.True(reseedAfterGap);
        Assert.True(operations.SnapshotRequestCount > 0);

        secondCancellation.Cancel();
        try
        {
            await secondTask;
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MalformedConnectionDoesNotStopControlListener()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            new SnapshotOperations(sessionId),
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator);
        var serverTask = server.RunAsync(serverCancellation.Token);

        await using (var malformed = CurrentUserPipeFactory.CreateClient(pipeName))
        {
            await malformed.ConnectAsync(CancellationToken.None);
            await malformed.WriteAsync(new byte[sizeof(int)]);
            await malformed.FlushAsync();
        }

        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = await connection.ConnectAsync(timeout.Token);
        var response = await connection.SendAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()),
            timeout.Token);

        Assert.True(connected.IsSuccess);
        Assert.True(response.IsSuccess);

        serverCancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ConnectsWithPidBoundHandshakeAndSendsTypedRequest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var registry = new AgentProcessRegistry();
        var operations = new SnapshotOperations(sessionId);
        var shutdown = new AgentShutdownWorkflow(
            new NoOpShutdownActions(),
            registry);
        var coordinator = new AgentSessionCoordinator(
            operations,
            shutdown,
            registry);
        var persistedProcesses = new List<ProcessRegistration>();
        var eventHub = new AgentEventHub();
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator,
            persistProcess: (registration, _) =>
            {
                lock (persistedProcesses)
                {
                    persistedProcesses.Add(registration);
                }

                return Task.CompletedTask;
            },
            verifyClientProcess: _ => true,
            eventHub: eventHub);
        var serverTask = server.RunAsync(serverCancellation.Token);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        var launcher = new RecordingLauncher();

        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            launcher,
            null,
            null,
            new TrueAgentProcessLiveness());
        var connected = await connection.ConnectAsync(CancellationToken.None);
        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var eventEnumerator = connection
            .WatchAsync(eventTimeout.Token)
            .GetAsyncEnumerator(eventTimeout.Token);
        await using var secondEventEnumerator = connection
            .WatchAsync(eventTimeout.Token)
            .GetAsyncEnumerator(eventTimeout.Token);
        Assert.True(await eventEnumerator.MoveNextAsync());
        Assert.IsType<AgentStateReseedEvent>(eventEnumerator.Current);
        Assert.True(await secondEventEnumerator.MoveNextAsync());
        Assert.IsType<AgentStateReseedEvent>(secondEventEnumerator.Current);
        var occurredAt = DateTimeOffset.UtcNow;
        eventHub.Publish(
            new AgentTaskEvent(
                new ApplicationTaskEvent(
                    ApplicationTaskId.New(),
                    CorrelationId.New(),
                    ApplicationTaskEventKind.Progress,
                    ApplicationTaskState.Running,
                    occurredAt,
                    "tool.progress.native",
                    "tool.progress.native",
                    string.Empty,
                    "step-1",
                    0.42)));
        Assert.True(await eventEnumerator.MoveNextAsync());
        var pushed = Assert.IsType<AgentTaskEvent>(eventEnumerator.Current);
        Assert.Equal(0.42, pushed.TaskEvent.ProgressFraction);
        Assert.True(await secondEventEnumerator.MoveNextAsync());
        var pushedToSecondWatcher = Assert.IsType<AgentTaskEvent>(
            secondEventEnumerator.Current);
        Assert.Equal(0.42, pushedToSecondWatcher.TaskEvent.ProgressFraction);
        var correlation = CorrelationId.New();
        var response = await connection.SendAsync(
            new GetAgentSnapshotRequest(correlation),
            CancellationToken.None);
        var manageInventoryResponse = await connection.SendAsync(
            new CaptureAgentManageInventoryRequest(
                CorrelationId.New()),
            CancellationToken.None);
        var manageInventory = Assert.IsType<ManageInventoryCaptureResponse>(
            manageInventoryResponse.Value);
        Assert.Equal("local:test", manageInventory.Document.DocumentId);
        var cachedInventoryResponse = await connection.SendAsync(
            new LoadAgentManageInventoryRequest(CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(
            Assert.IsType<ManageInventoryLoadedResponse>(cachedInventoryResponse.Value)
                .Document);

        Assert.True(connected.IsSuccess);
        Assert.Equal(sessionId, connected.Value!.AgentInstanceId.Value);
        Assert.False(launcher.WasCalled);
        Assert.True(response.IsSuccess);
        var snapshot = Assert.IsType<AgentSnapshotResponse>(response.Value);
        Assert.Equal(sessionId, snapshot.Snapshot.AgentInstanceId.Value);
        Assert.Equal(correlation, response.CorrelationId);
        Assert.True(manageInventoryResponse.IsSuccess);
        Assert.True(cachedInventoryResponse.IsSuccess);
        lock (persistedProcesses)
        {
            Assert.True(persistedProcesses.Count >= 2);
            Assert.All(
                persistedProcesses,
                process => Assert.Equal(
                    WorkerKind.MainApplication,
                    process.Kind));
        }

        serverCancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MissingEndpointInvokesLauncherAndReturnsEnvironmentFailure()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        var launcher = new RecordingLauncher();
        await using var connection = new NamedPipeAgentConnection(
            Path.Combine(directory, "missing.json"),
            launcher,
            new FastForwardTimeProvider(),
            null,
            new TrueAgentProcessLiveness());
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        var result = await connection.ConnectAsync(cancellation.Token);

        Assert.True(launcher.WasCalled);
        Assert.Equal(ApplicationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task DisposeDuringConnectCancelsTheInFlightLaunchAndIsIdempotent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        var launcher = new BlockingLauncher();
        var connection = new NamedPipeAgentConnection(
            Path.Combine(directory, "missing.json"),
            launcher,
            null,
            null,
            new TrueAgentProcessLiveness());
        var connect = connection.ConnectAsync(CancellationToken.None);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = connection.DisposeAsync().AsTask();
        var secondDispose = connection.DisposeAsync().AsTask();

        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));
        var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(launcher.Cancelled);
        Assert.Equal(ApplicationStatus.RequiresEnvironment, result.Status);
    }

    [Fact]
    public async Task DisposeDuringSendCancelsTheOutstandingRequest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var operations = new BlockingMainWindowOperations(sessionId);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator,
            verifyClientProcess: _ => true);
        var serverTask = server.RunAsync(serverCancellation.Token);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());

        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);
        var send = connection.SendAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()),
            CancellationToken.None);
        await operations.MainWindowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = connection.DisposeAsync().AsTask();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.RequiresEnvironment, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "agent.request.connection_lost");

        operations.ReleaseMainWindow.TrySetResult();
        serverCancellation.Cancel();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingForRequestGateKeepsConnectionUsable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var operations = new BlockingMainWindowOperations(sessionId);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator,
            verifyClientProcess: _ => true);
        var serverTask = server.RunAsync(serverCancellation.Token);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());

        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);
        var first = connection.SendAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()),
            CancellationToken.None);
        await operations.MainWindowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var waitingCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));
        var waiting = await connection.SendAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()),
            waitingCancellation.Token);

        Assert.Equal(ApplicationStatus.Cancelled, waiting.Status);
        operations.ReleaseMainWindow.TrySetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.True(
            (await connection.SendAsync(
                new GetAgentSnapshotRequest(CorrelationId.New()),
                CancellationToken.None)).IsSuccess);

        serverCancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task CallerCancellationAfterRequestWriteReturnsUnknownAndNextRequestReconnects()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var operations = new BlockingMainWindowOperations(sessionId);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            operations,
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        using var firstServerCancellation = new CancellationTokenSource();
        var firstServer = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator,
            verifyClientProcess: _ => true);
        var firstServerTask = firstServer.RunAsync(firstServerCancellation.Token);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());

        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);
        using var requestCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));
        var request = connection.SendAsync(
            new OpenMainWindowRequest(null, CorrelationId.New()),
            requestCancellation.Token);
        await operations.MainWindowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unknown = await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.OutcomeUnknown, unknown.Status);
        operations.ReleaseMainWindow.TrySetResult();
        firstServerCancellation.Cancel();
        await firstServerTask.WaitAsync(TimeSpan.FromSeconds(5));

        using var secondServerCancellation = new CancellationTokenSource();
        var secondServer = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator);
        var secondServerTask = secondServer.RunAsync(secondServerCancellation.Token);
        var recovered = await connection.SendAsync(
            new GetAgentSnapshotRequest(CorrelationId.New()),
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        secondServerCancellation.Cancel();
        try
        {
            await secondServerTask;
        }
        catch (OperationCanceledException)
        {
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task DisposeDuringEventRecoveryDoesNotCreateAnotherTransport()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var userHash = IpcIdentity.HashUserSid(sid);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(
            new SnapshotOperations(sessionId),
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry),
            registry);
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(
            pipeName,
            nonce,
            userHash,
            sessionId,
            Environment.ProcessId,
            coordinator);
        var serverTask = server.RunAsync(serverCancellation.Token);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));
        var recoveryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new RecordingLauncher();
        var connection = new NamedPipeAgentConnection(
            endpointPath,
            launcher,
            null,
            async cancellationToken =>
            {
                recoveryStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            },
            new TrueAgentProcessLiveness());

        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);

        serverCancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(launcher.WasCalled);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.ConnectAsync(CancellationToken.None));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MalformedHandshakeJsonReturnsConnectFailure()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(
            IpcIdentity.HashUserSid(sid),
            nonce);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));

        using var server = CurrentUserPipeFactory.CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await IpcFrameCodec.ReadAsync(server);
            var payload = "{not-json"u8.ToArray();
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await server.WriteAsync(header);
            await server.WriteAsync(payload);
            await server.FlushAsync();
        });
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            new RecordingLauncher(),
            null,
            null,
            new TrueAgentProcessLiveness());

        var result = await connection.ConnectAsync(CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.RequiresEnvironment, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "agent.connect.failed");
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task StaleEndpointProcessIdentityInvokesReplacementLauncher()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.Agent.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current SID unavailable.");
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(
            IpcIdentity.HashUserSid(sid),
            nonce);
        await File.WriteAllTextAsync(
            endpointPath,
            JsonSerializer.Serialize(
                new AgentEndpoint(
                    IpcProtocol.CurrentVersion,
                    pipeName,
                    nonce,
                    sessionId,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow)));

        var launcher = new BlockingLauncher();
        await using var connection = new NamedPipeAgentConnection(
            endpointPath,
            launcher,
            null,
            null,
            new FalseAgentProcessLiveness());
        using var cancellation = new CancellationTokenSource();
        var connect = connection.ConnectAsync(cancellation.Token);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(launcher.Cancelled);
        Assert.Equal(ApplicationStatus.Cancelled, result.Status);
        Directory.Delete(directory, recursive: true);
    }

    private sealed class TrueAgentProcessLiveness : IAgentProcessLiveness
    {
        public bool IsExpectedAgentProcess(AgentEndpoint endpoint) => true;
    }

    private sealed class FalseAgentProcessLiveness : IAgentProcessLiveness
    {
        public bool IsExpectedAgentProcess(AgentEndpoint endpoint) => false;
    }

    private sealed class RecordingLauncher : IAgentProcessLauncher
    {
        public bool WasCalled { get; private set; }

        public Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingLauncher : IAgentProcessLauncher
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    private class SnapshotOperations(Guid sessionId)
        : IAgentRequestOperations
    {
        private int snapshotRequestCount;

        public int SnapshotRequestCount => Volatile.Read(ref snapshotRequestCount);

        public virtual Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
            GetAgentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref snapshotRequestCount);
            return Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new AgentSnapshotResponse(
                        new AgentSnapshot(
                            new AgentInstanceId(sessionId),
                            true,
                            null,
                            new AgentShutdownStatus(
                                AgentLifecycleState.Running,
                                null,
                                [],
                                [],
                                false),
                            [])),
                    request.CorrelationId));
        }

        public virtual Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
            OpenMainWindowRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync(
            OpenAgentNativePropertiesRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
            StartAgentMonitoringRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
            StopAgentMonitoringRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
            LoadAgentWorkspaceStateRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new WorkspaceStateLoadedResponse(null),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> SaveWorkspaceStateAsync(
            SaveAgentWorkspaceStateRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new WorkspaceStateSavedResponse(request.State),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> ListSimulationDocumentsAsync(
            ListAgentSimulationDocumentsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SimulationDocumentListResponse([]),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
            SaveAgentSimulationDocumentRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SimulationDocumentSavedResponse(request.Document),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
            DeleteAgentSimulationDocumentRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SimulationDocumentDeletedResponse(request.DocumentId, true),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
            CommitAgentSimulationEditRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SimulationDocumentSavedResponse(request.Document),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
            CaptureAgentInventoryRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
            CaptureAgentManageInventoryRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new ManageInventoryCaptureResponse(
                        Guid.NewGuid(),
                        LocalInventory()),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
            LoadAgentManageInventoryRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new ManageInventoryLoadedResponse(
                        Guid.NewGuid(),
                        LocalInventory()),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
            ExportAgentMonitorCsvRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        private static LocalInventoryDocumentPayload LocalInventory() =>
            new(
                "local:test",
                2,
                "Test",
                "{}",
                new string('a', 64),
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        private static Task<ApplicationResult<AgentResponse>> Acknowledge(
            AgentRequest request) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new AgentAcknowledgement(),
                    request.CorrelationId));
    }

    private sealed class BlockingMainWindowOperations(Guid sessionId)
        : SnapshotOperations(sessionId)
    {
        public TaskCompletionSource MainWindowStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseMainWindow { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
            OpenMainWindowRequest request,
            CancellationToken cancellationToken)
        {
            MainWindowStarted.TrySetResult();
            await ReleaseMainWindow.Task.WaitAsync(cancellationToken);
            return await base.OpenMainWindowAsync(request, cancellationToken);
        }
    }

    private sealed class NoOpShutdownActions : IAgentShutdownActions
    {
        public Task NotifyClientsAsync(ShutdownReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RestoreTemporarySystemStateAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task CloseNamedPipesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseMainApplicationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveTrayIconAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExitAgentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FastForwardTimeProvider : TimeProvider
    {
        private long calls;

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UtcNow.AddSeconds(Interlocked.Increment(ref calls));
    }
}
