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
            new RecordingLauncher());
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
            new RecordingLauncher());

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
            launcher);
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
            new AgentTestEvent(
                new TestEvent(
                    new TestRunId(Guid.NewGuid()),
                    TestEventKind.Progress,
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
                        0.42))));
        Assert.True(await eventEnumerator.MoveNextAsync());
        var pushed = Assert.IsType<AgentTestEvent>(eventEnumerator.Current);
        Assert.Equal(0.42, pushed.TestEvent.TaskEvent.ProgressFraction);
        Assert.True(await secondEventEnumerator.MoveNextAsync());
        var pushedToSecondWatcher = Assert.IsType<AgentTestEvent>(
            secondEventEnumerator.Current);
        Assert.Equal(0.42, pushedToSecondWatcher.TestEvent.TaskEvent.ProgressFraction);
        var correlation = CorrelationId.New();
        var response = await connection.SendAsync(
            new GetAgentSnapshotRequest(correlation),
            CancellationToken.None);
        var diagnosticsResponse = await connection.SendAsync(
            new GetDevelopmentDiagnosticsRequest(10, CorrelationId.New()),
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
        var supportCorrelation = CorrelationId.New();
        var supportRequest = new ElevatedBrokerExecutionRequest(
            Guid.NewGuid(),
            sessionId,
            Environment.ProcessId,
            userHash,
            new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(1),
            ElevatedBrokerOperationKind.SetActivePowerPlan,
            PowerPlanId: Guid.NewGuid());
        var reviewResponse = await connection.SendAsync(
            new ReviewAgentSystemSupportRequest(
                supportRequest,
                supportCorrelation),
            CancellationToken.None);
        var review = Assert.IsType<SystemSupportReviewResponse>(
            reviewResponse.Value);
        var executeCorrelation = CorrelationId.New();
        var supportResponse = await connection.SendAsync(
            new ExecuteAgentSystemSupportRequest(
                review.ReviewId,
                true,
                executeCorrelation),
            CancellationToken.None);
        var diteCorrelation = CorrelationId.New();
        var diteResponse = await connection.SendAsync(
            new PersistDiteLegacyImportRequest(
                Path.Combine(Path.GetTempPath(), "dite-results.csv"),
                new string('a', 64),
                diteCorrelation),
            CancellationToken.None);
        var diteHistoryCorrelation = CorrelationId.New();
        var diteHistoryResponse = await connection.SendAsync(
            new ListDiteLegacyImportsRequest(10, diteHistoryCorrelation),
            CancellationToken.None);
        var diteSummaryCorrelation = CorrelationId.New();
        var diteSummaryResponse = await connection.SendAsync(
            new GetDiteLegacyImportSummaryRequest(
                Guid.NewGuid(),
                diteSummaryCorrelation),
            CancellationToken.None);
        var presetNow = DateTimeOffset.UtcNow;
        var preset = new UserTestPreset(
            Guid.NewGuid(),
            "Pipe preset",
            TestPresetScenario.IoBenchmark,
            new ToolId("microsoft.diskspd"),
            TestPresetVerificationMode.FullHash,
            50_505,
            IoAccessPattern.Random,
            30,
            1024L * 1024 * 1024,
            4096,
            4,
            32,
            60,
            5,
            2,
            3,
            true,
            presetNow,
            presetNow);
        var savedPresetResponse = await connection.SendAsync(
            new SaveUserTestPresetRequest(preset, CorrelationId.New()),
            CancellationToken.None);
        var listedPresetResponse = await connection.SendAsync(
            new ListUserTestPresetsRequest(CorrelationId.New()),
            CancellationToken.None);
        var deletedPresetResponse = await connection.SendAsync(
            new DeleteUserTestPresetRequest(
                preset.PresetId,
                CorrelationId.New()),
            CancellationToken.None);
        var installNow = DateTimeOffset.UtcNow;
        var msiResponse = await connection.SendAsync(
            new InstallAgentMsiToolRequest(
                new ToolInstallPlan(
                    new ToolId("fio"),
                    new Uri("https://github.com/axboe/fio/releases/download/fio-3.42/fio-3.42-x64.msi"),
                    new string('a', 64),
                    ToolInstallerKind.Msi,
                    ToolInstallLocation.PerUserManagedDirectory,
                    true,
                    installNow,
                    installNow.AddMinutes(15),
                    new string('b', 64)),
                Path.Combine("tool-downloads", $"{new string('a', 64)}.msi"),
                true,
                CorrelationId.New()),
            CancellationToken.None);

        Assert.True(connected.IsSuccess);
        Assert.Equal(sessionId, connected.Value!.AgentInstanceId.Value);
        Assert.False(launcher.WasCalled);
        Assert.True(response.IsSuccess);
        var snapshot = Assert.IsType<AgentSnapshotResponse>(response.Value);
        Assert.Equal(sessionId, snapshot.Snapshot.AgentInstanceId.Value);
        Assert.Equal(correlation, response.CorrelationId);
        var diagnostics = Assert.IsType<DevelopmentDiagnosticsResponse>(
            diagnosticsResponse.Value).Diagnostics;
        Assert.Equal(sessionId, diagnostics.Agent.AgentInstanceId.Value);
        Assert.Empty(diagnostics.RecentPlans);
        Assert.Single(diagnostics.Algorithms);
        Assert.True(supportResponse.IsSuccess);
        var support = Assert.IsType<SystemSupportExecutionResponse>(
            supportResponse.Value);
        Assert.Equal(
            ElevatedBrokerOperationKind.SetActivePowerPlan,
            support.Result.Operation);
        Assert.True(support.Result.Succeeded);
        Assert.Equal(executeCorrelation, supportResponse.CorrelationId);
        var dite = Assert.IsType<DiteLegacyImportPersistenceResponse>(
            diteResponse.Value);
        Assert.Equal(2, dite.RunCount);
        Assert.Equal(3, dite.MetricCount);
        Assert.Equal(diteCorrelation, diteResponse.CorrelationId);
        Assert.Single(
            Assert.IsType<DiteLegacyImportHistoryResponse>(
                diteHistoryResponse.Value).Imports);
        Assert.Single(
            Assert.IsType<DiteLegacyImportSummaryResponse>(
                diteSummaryResponse.Value).Summaries);
        Assert.Equal(
            preset,
            Assert.IsType<UserTestPresetSavedResponse>(
                savedPresetResponse.Value).Preset);
        Assert.Empty(
            Assert.IsType<UserTestPresetListResponse>(
                listedPresetResponse.Value).Presets);
        Assert.True(
            Assert.IsType<UserTestPresetDeletedResponse>(
                deletedPresetResponse.Value).Deleted);
        Assert.True(msiResponse.IsSuccess);
        Assert.Equal(
            ElevatedBrokerOperationKind.InstallMsiTool,
            Assert.IsType<MsiToolInstallResponse>(msiResponse.Value).Result.Operation);
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
            new FastForwardTimeProvider());
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
            launcher);
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
        var operations = new BlockingDiagnosticsOperations(sessionId);
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
        var connection = new NamedPipeAgentConnection(endpointPath, new RecordingLauncher());

        Assert.True((await connection.ConnectAsync(CancellationToken.None)).IsSuccess);
        var send = connection.SendAsync(
            new GetDevelopmentDiagnosticsRequest(10, CorrelationId.New()),
            CancellationToken.None);
        await operations.DiagnosticsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = connection.DisposeAsync().AsTask();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.RequiresEnvironment, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "agent.request.connection_lost");

        operations.ReleaseDiagnostics.TrySetResult();
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
            });

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
            new RecordingLauncher());

        var result = await connection.ConnectAsync(CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.RequiresEnvironment, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "agent.connect.failed");
        Directory.Delete(directory, recursive: true);
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
        private ElevatedBrokerOperationKind reviewedOperation;
        private int snapshotRequestCount;

        public int SnapshotRequestCount => Volatile.Read(ref snapshotRequestCount);

        public Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
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

        public virtual Task<ApplicationResult<AgentResponse>> GetDevelopmentDiagnosticsAsync(
            GetDevelopmentDiagnosticsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new DevelopmentDiagnosticsResponse(
                        new DevelopmentDiagnostics(
                            new AgentSnapshot(
                                new AgentInstanceId(sessionId),
                                true,
                                null,
                                null,
                                new AgentShutdownStatus(
                                    AgentLifecycleState.Running,
                                    null,
                                    [],
                                    [],
                                    false),
                                []),
                            [],
                            [new AlgorithmIdentity(
                                "ALG-TEST",
                                "1",
                                AlgorithmConfidence.Proven,
                                "test")])),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
            OpenMainWindowRequest request,
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

        public Task<ApplicationResult<AgentResponse>> StartTestAsync(
            StartAgentTestRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> CancelTestAsync(
            CancelAgentTestRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> GetTestResultAsync(
            GetAgentTestResultRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> ListTestRunsAsync(
            ListAgentTestRunsRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> ListUserTestPresetsAsync(
            ListUserTestPresetsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new UserTestPresetListResponse([]),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> SaveUserTestPresetAsync(
            SaveUserTestPresetRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new UserTestPresetSavedResponse(request.Preset),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> DeleteUserTestPresetAsync(
            DeleteUserTestPresetRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new UserTestPresetDeletedResponse(request.PresetId, true),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> PersistDiteLegacyImportAsync(
            PersistDiteLegacyImportRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new DiteLegacyImportPersistenceResponse(
                        Guid.NewGuid(),
                        false,
                        2,
                        3),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> ListDiteLegacyImportsAsync(
            ListDiteLegacyImportsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new DiteLegacyImportHistoryResponse(
                    [
                        new(
                            Guid.NewGuid(),
                            "dite.csv",
                            new string('a', 64),
                            DateTimeOffset.UtcNow,
                            2,
                            3)
                    ]),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>>
            GetDiteLegacyImportSummaryAsync(
                GetDiteLegacyImportSummaryRequest request,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new DiteLegacyImportSummaryResponse(
                        request.ImportId,
                        [
                            new(
                                "throughput",
                                "MiB/s",
                                2,
                                100,
                                110,
                                120)
                        ]),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> ExportTestRunAsync(
            ExportAgentTestRunRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

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

        private static LocalInventoryDocumentPayload LocalInventory() =>
            new(
                "local:test",
                2,
                "Test",
                "{}",
                new string('a', 64),
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        public Task<ApplicationResult<AgentResponse>> DetectToolAsync(
            DetectAgentToolRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> InstallMsiToolAsync(
            InstallAgentMsiToolRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new MsiToolInstallResponse(
                        new ElevatedBrokerExecutionResult(
                            ElevatedBrokerOperationKind.InstallMsiTool,
                            true,
                            "broker.msi-install.completed",
                            MsiInstallEvidence: new MsiToolInstallEvidence(0, false))),
                    request.CorrelationId));

        public Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
            ExportAgentMonitorCsvRequest request,
            CancellationToken cancellationToken) =>
            Acknowledge(request);

        public Task<ApplicationResult<AgentResponse>> ReviewSystemSupportAsync(
            ReviewAgentSystemSupportRequest request,
            CancellationToken cancellationToken)
        {
            reviewedOperation = request.ExecutionRequest.Operation;
            return Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SystemSupportReviewResponse(
                        Guid.NewGuid(),
                        reviewedOperation,
                        request.ExecutionRequest.PlanHash,
                        DateTimeOffset.UtcNow.AddMinutes(2),
                        0,
                        0,
                        "system-support.warning"),
                    request.CorrelationId));
        }

        public Task<ApplicationResult<AgentResponse>> ExecuteSystemSupportAsync(
            ExecuteAgentSystemSupportRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new SystemSupportExecutionResponse(
                        new ElevatedBrokerExecutionResult(
                            reviewedOperation,
                            true,
                            "broker.completed")),
                    request.CorrelationId));

        private static Task<ApplicationResult<AgentResponse>> Acknowledge(
            AgentRequest request) =>
            Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(
                    new AgentAcknowledgement(),
                    request.CorrelationId));
    }

    private sealed class BlockingDiagnosticsOperations(Guid sessionId)
        : SnapshotOperations(sessionId)
    {
        public TaskCompletionSource DiagnosticsStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDiagnostics { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ApplicationResult<AgentResponse>> GetDevelopmentDiagnosticsAsync(
            GetDevelopmentDiagnosticsRequest request,
            CancellationToken cancellationToken)
        {
            DiagnosticsStarted.TrySetResult();
            await ReleaseDiagnostics.Task;
            return await base.GetDevelopmentDiagnosticsAsync(request, cancellationToken);
        }
    }

    private sealed class NoOpShutdownActions : IAgentShutdownActions
    {
        public bool HasActiveTest => false;
        public Task NotifyClientsAsync(ShutdownReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RequestTestCancellationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TerminateExternalToolJobsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RestoreTemporarySystemStateAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task CloseNamedPipesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseMainApplicationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopSupervisedProcessesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
