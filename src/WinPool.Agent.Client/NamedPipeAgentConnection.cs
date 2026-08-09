using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent.Client;

public sealed record AgentEndpoint(
    int ProtocolVersion,
    string PipeName,
    Guid Nonce,
    Guid AgentSessionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc);

public interface IAgentProcessLauncher
{
    Task EnsureStartedAsync(CancellationToken cancellationToken);
}

public sealed class AgentProcessLauncher(string agentExecutablePath)
    : IAgentProcessLauncher
{
    private readonly string executablePath =
        Path.GetFullPath(
            !string.IsNullOrWhiteSpace(agentExecutablePath)
                ? agentExecutablePath
                : throw new ArgumentException(
                    "Agent executable path is required.",
                    nameof(agentExecutablePath)));

    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "WinPool Agent executable was not found.",
                executablePath);
        }

        var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            });
        if (process is null)
        {
            throw new InvalidOperationException("WinPool Agent could not be started.");
        }

        process.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class NamedPipeAgentConnection : IAgentConnection, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string endpointPath;
    private readonly IAgentProcessLauncher launcher;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly Channel<AgentEvent> observedEvents =
        Channel.CreateBounded<AgentEvent>(
            new BoundedChannelOptions(1_024)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    private NamedPipeClientStream? stream;
    private NamedPipeClientStream? eventStream;
    private CancellationTokenSource? eventCancellation;
    private Task? eventReaderTask;
    private AgentHandshake? handshake;
    private bool disposed;

    public NamedPipeAgentConnection(
        string endpointPath,
        IAgentProcessLauncher launcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPath);
        this.endpointPath = Path.GetFullPath(endpointPath);
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string DefaultEndpointPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPool",
            "agent-endpoint.json");

    public async Task<ApplicationResult<AgentHandshake>> ConnectAsync(
        CancellationToken cancellationToken)
    {
        var correlation = CorrelationId.New();
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (stream?.IsConnected == true && handshake is not null)
            {
                return ApplicationResult<AgentHandshake>.Succeeded(
                    handshake,
                    correlation);
            }

            await DisposeStreamAsync();
            var endpoint = await ReadLiveEndpointAsync(cancellationToken);
            if (endpoint is null)
            {
                await launcher.EnsureStartedAsync(cancellationToken);
                endpoint = await WaitForLiveEndpointAsync(cancellationToken);
            }

            ValidateEndpoint(endpoint);
            var client = new NamedPipeClientStream(
                ".",
                endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(5_000, cancellationToken);
                var sid = WindowsIdentity.GetCurrent().User?.Value
                    ?? throw new InvalidOperationException(
                        "The current Windows SID is unavailable.");
                var request = new AgentHandshakeRequest(
                    IpcProtocol.CurrentVersion,
                    endpoint.Nonce,
                    IpcIdentity.HashUserSid(sid),
                    Environment.ProcessId,
                    timeProvider.GetUtcNow());
                var messageId = Guid.NewGuid();
                await IpcFrameCodec.WriteAsync(
                    client,
                    Envelope(
                        messageId,
                        correlation.Value,
                        AgentControlMessageTypes.HandshakeRequest,
                        request),
                    cancellationToken);
                var replyEnvelope = await IpcFrameCodec.ReadAsync(
                    client,
                    cancellationToken);
                if (replyEnvelope.CorrelationId != correlation.Value
                    || replyEnvelope.MessageType
                        != AgentControlMessageTypes.HandshakeAccepted)
                {
                    throw new InvalidDataException(
                        "WinPool Agent rejected or mismatched the handshake.");
                }

                var reply = replyEnvelope.Payload
                    .Deserialize<AgentHandshakeReply>(JsonOptions)
                    ?? throw new InvalidDataException(
                        "WinPool Agent returned an empty handshake.");
                if (reply.AgentSessionId != endpoint.AgentSessionId
                    || reply.AgentProcessId != endpoint.ProcessId)
                {
                    throw new InvalidDataException(
                        "WinPool Agent endpoint identity changed during connection.");
                }

                stream = client;
                await ConnectEventStreamAsync(
                    reply,
                    endpoint,
                    cancellationToken).ConfigureAwait(false);
                handshake = new AgentHandshake(
                    reply.ProtocolVersion,
                    new AgentInstanceId(reply.AgentSessionId),
                    reply.AgentProcessId,
                    AgentCapability.Monitoring
                    | AgentCapability.Testing
                    | AgentCapability.ToolManagement
                    | AgentCapability.Tray
                    | AgentCapability.Persistence,
                    endpoint.StartedAtUtc);
                return ApplicationResult<AgentHandshake>.Succeeded(
                    handshake,
                    correlation);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentHandshake>(
                ApplicationStatus.Cancelled,
                correlation,
                "agent.connect.cancelled");
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            await DisposeStreamAsync();
            return Failure<AgentHandshake>(
                ApplicationStatus.RequiresEnvironment,
                correlation,
                "agent.connect.failed");
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>
    /// Drops a transport whose server has intentionally exited, then starts or
    /// connects to the replacement Agent. NamedPipeClientStream.IsConnected can
    /// remain true until another I/O operation observes the remote close.
    /// </summary>
    public async Task<ApplicationResult<AgentHandshake>> ReconnectAsync(
        CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await DisposeStreamAsync();
        }
        finally
        {
            connectionGate.Release();
        }

        return await ConnectAsync(cancellationToken);
    }

    public async Task<ApplicationResult<AgentResponse>> SendAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connected = await ConnectAsync(cancellationToken);
        if (!connected.IsSuccess)
        {
            return new(
                connected.Status,
                null,
                connected.Messages,
                request.CorrelationId);
        }

        await requestGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var messageType = RequestMessageType(request);
            await IpcFrameCodec.WriteAsync(
                stream!,
                Envelope(
                    Guid.NewGuid(),
                    request.CorrelationId.Value,
                    messageType,
                    request,
                    request.GetType()),
                cancellationToken);
            var responseEnvelope = await IpcFrameCodec.ReadAsync(
                stream!,
                cancellationToken);
            if (responseEnvelope.MessageType != AgentControlMessageTypes.Response
                || responseEnvelope.CorrelationId != request.CorrelationId.Value)
            {
                throw new InvalidDataException(
                    "WinPool Agent returned a mismatched response.");
            }

            var payload = responseEnvelope.Payload
                .Deserialize<AgentResponseWirePayload>(JsonOptions)
                ?? throw new InvalidDataException(
                    "WinPool Agent returned an empty response.");
            var response = DeserializeResponse(payload);
            Observe(response);
            return new(
                payload.Status,
                response,
                payload.Messages,
                payload.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or JsonException
                or NotSupportedException)
        {
            await DisposeStreamAsync();
            return Failure<AgentResponse>(
                ApplicationStatus.RequiresEnvironment,
                request.CorrelationId,
                "agent.request.connection_lost");
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async IAsyncEnumerable<AgentEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in observedEvents.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        observedEvents.Writer.TryComplete();
        await DisposeStreamAsync();
        connectionGate.Dispose();
        requestGate.Dispose();
    }

    private async Task<AgentEndpoint?> ReadLiveEndpointAsync(
        CancellationToken cancellationToken)
    {
        AgentEndpoint? endpoint;
        try
        {
            await using var file = new FileStream(
                endpointPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            endpoint = await JsonSerializer.DeserializeAsync<AgentEndpoint>(
                file,
                JsonOptions,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return endpoint is not null && IsProcessLive(endpoint.ProcessId)
            ? endpoint
            : null;
    }

    private async Task<AgentEndpoint> WaitForLiveEndpointAsync(
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = await ReadLiveEndpointAsync(cancellationToken);
            if (endpoint is not null)
            {
                return endpoint;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                timeProvider,
                cancellationToken);
        }

        throw new IOException("Timed out waiting for the WinPool Agent endpoint.");
    }

    private static void ValidateEndpoint(AgentEndpoint endpoint)
    {
        if (endpoint.ProtocolVersion != IpcProtocol.CurrentVersion
            || endpoint.Nonce == Guid.Empty
            || endpoint.AgentSessionId == Guid.Empty
            || endpoint.ProcessId <= 0
            || endpoint.PipeName
                != IpcIdentity.CreateAgentControlPipeName(
                    CurrentUserSidHash(),
                    endpoint.Nonce))
        {
            throw new InvalidDataException("WinPool Agent endpoint metadata is invalid.");
        }
    }

    private static string CurrentUserSidHash()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
        return IpcIdentity.HashUserSid(sid);
    }

    private async Task ConnectEventStreamAsync(
        AgentHandshakeReply reply,
        AgentEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var eventEndpoint = reply.EventEndpoint
            ?? throw new InvalidDataException("The Agent did not provide an event endpoint.");
        if (eventEndpoint.ConnectionId != reply.ConnectionId ||
            eventEndpoint.Nonce == Guid.Empty ||
            eventEndpoint.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
            eventEndpoint.PipeName != IpcIdentity.CreateAgentEventPipeName(
                CurrentUserSidHash(),
                eventEndpoint.ConnectionId,
                eventEndpoint.Nonce))
        {
            throw new InvalidDataException("The Agent event endpoint is invalid.");
        }

        var client = CurrentUserPipeFactory.CreateClient(eventEndpoint.PipeName);
        try
        {
            await client.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
            if (CurrentUserPipeFactory.GetConnectedServerProcessId(client) != endpoint.ProcessId)
            {
                throw new InvalidDataException("The Agent event server process is invalid.");
            }

            var correlationId = Guid.NewGuid();
            await IpcFrameCodec.WriteAsync(
                client,
                Envelope(
                    Guid.NewGuid(),
                    correlationId,
                    AgentEventMessageTypes.HandshakeRequest,
                    new AgentEventHandshakeRequest(
                        IpcProtocol.CurrentVersion,
                        eventEndpoint.ConnectionId,
                        eventEndpoint.Nonce,
                        Environment.ProcessId,
                        timeProvider.GetUtcNow())),
                cancellationToken).ConfigureAwait(false);
            var envelope = await IpcFrameCodec.ReadAsync(client, cancellationToken)
                .ConfigureAwait(false);
            var accepted = envelope.Payload.Deserialize<AgentEventHandshakeReply>(JsonOptions)
                ?? throw new InvalidDataException("The Agent event handshake reply is empty.");
            if (envelope.MessageType != AgentEventMessageTypes.HandshakeAccepted ||
                envelope.CorrelationId != correlationId ||
                accepted.ConnectionId != eventEndpoint.ConnectionId ||
                accepted.AgentProcessId != endpoint.ProcessId)
            {
                throw new InvalidDataException("The Agent event handshake was rejected.");
            }

            eventStream = client;
            eventCancellation = new CancellationTokenSource();
            eventReaderTask = ReadEventStreamAsync(client, eventCancellation.Token);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadEventStreamAsync(
        NamedPipeClientStream client,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await IpcFrameCodec.ReadAsync(client, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope.MessageType != AgentEventMessageTypes.Event)
                {
                    throw new InvalidDataException("The Agent event stream returned an invalid frame.");
                }

                var payload = envelope.Payload.Deserialize<AgentEventWirePayload>(JsonOptions)
                    ?? throw new InvalidDataException("The Agent event payload is empty.");
                var item = DeserializeEvent(payload);
                if (item is not null)
                {
                    observedEvents.Writer.TryWrite(item);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    private static AgentEvent? DeserializeEvent(AgentEventWirePayload payload) =>
        payload.EventType switch
        {
            nameof(AgentTaskEvent) => payload.Event.Deserialize<AgentTaskEvent>(JsonOptions),
            nameof(AgentMonitorSampleEvent) =>
                payload.Event.Deserialize<AgentMonitorSampleEvent>(JsonOptions),
            nameof(AgentTestEvent) => payload.Event.Deserialize<AgentTestEvent>(JsonOptions),
            nameof(AgentToolStateEvent) =>
                payload.Event.Deserialize<AgentToolStateEvent>(JsonOptions),
            nameof(AgentProcessStateEvent) =>
                payload.Event.Deserialize<AgentProcessStateEvent>(JsonOptions),
            nameof(AgentShutdownEvent) =>
                payload.Event.Deserialize<AgentShutdownEvent>(JsonOptions),
            _ => null
        };

    private static bool IsProcessLive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string RequestMessageType(AgentRequest request) =>
        request switch
        {
            GetAgentSnapshotRequest => AgentControlMessageTypes.GetSnapshot,
            GetDevelopmentDiagnosticsRequest =>
                AgentControlMessageTypes.GetDevelopmentDiagnostics,
            OpenMainWindowRequest => AgentControlMessageTypes.OpenMainWindow,
            OpenAgentNativePropertiesRequest => AgentControlMessageTypes.OpenNativeProperties,
            StartAgentMonitoringRequest => AgentControlMessageTypes.StartMonitoring,
            StopAgentMonitoringRequest => AgentControlMessageTypes.StopMonitoring,
            StartAgentTestRequest => AgentControlMessageTypes.StartTest,
            CancelAgentTestRequest => AgentControlMessageTypes.CancelTest,
            GetAgentTestResultRequest => AgentControlMessageTypes.GetTestResult,
            ListAgentTestRunsRequest => AgentControlMessageTypes.ListTestRuns,
            ListUserTestPresetsRequest =>
                AgentControlMessageTypes.ListUserTestPresets,
            SaveUserTestPresetRequest =>
                AgentControlMessageTypes.SaveUserTestPreset,
            DeleteUserTestPresetRequest =>
                AgentControlMessageTypes.DeleteUserTestPreset,
            LoadAgentWorkspaceStateRequest =>
                AgentControlMessageTypes.LoadWorkspaceState,
            SaveAgentWorkspaceStateRequest =>
                AgentControlMessageTypes.SaveWorkspaceState,
            ListAgentSimulationDocumentsRequest =>
                AgentControlMessageTypes.ListSimulationDocuments,
            SaveAgentSimulationDocumentRequest =>
                AgentControlMessageTypes.SaveSimulationDocument,
            DeleteAgentSimulationDocumentRequest =>
                AgentControlMessageTypes.DeleteSimulationDocument,
            CommitAgentSimulationEditRequest =>
                AgentControlMessageTypes.CommitSimulationEdit,
            PersistDiteLegacyImportRequest =>
                AgentControlMessageTypes.PersistDiteLegacyImport,
            ListDiteLegacyImportsRequest =>
                AgentControlMessageTypes.ListDiteLegacyImports,
            GetDiteLegacyImportSummaryRequest =>
                AgentControlMessageTypes.GetDiteLegacyImportSummary,
            ExportAgentTestRunRequest => AgentControlMessageTypes.ExportTestRun,
            CaptureAgentInventoryRequest => AgentControlMessageTypes.CaptureInventory,
            CaptureAgentManageInventoryRequest =>
                AgentControlMessageTypes.CaptureManageInventory,
            LoadAgentManageInventoryRequest =>
                AgentControlMessageTypes.LoadManageInventory,
            DetectAgentToolRequest => AgentControlMessageTypes.DetectTool,
            InstallAgentMsiToolRequest => AgentControlMessageTypes.InstallMsiTool,
            ExportAgentMonitorCsvRequest => AgentControlMessageTypes.ExportMonitorCsv,
            ReviewAgentSystemSupportRequest =>
                AgentControlMessageTypes.ReviewSystemSupport,
            ExecuteAgentSystemSupportRequest =>
                AgentControlMessageTypes.ExecuteSystemSupport,
            RequestAgentShutdownRequest => AgentControlMessageTypes.Shutdown,
            _ => throw new NotSupportedException(
                $"Unsupported Agent request {request.GetType().Name}.")
        };

    private static AgentResponse? DeserializeResponse(
        AgentResponseWirePayload payload)
    {
        if (payload.Response is not { } response
            || string.IsNullOrWhiteSpace(payload.ResponseType))
        {
            return null;
        }

        return payload.ResponseType switch
        {
            nameof(AgentAcknowledgement) =>
                response.Deserialize<AgentAcknowledgement>(JsonOptions),
            nameof(AgentSnapshotResponse) =>
                response.Deserialize<AgentSnapshotResponse>(JsonOptions),
            nameof(DevelopmentDiagnosticsResponse) =>
                response.Deserialize<DevelopmentDiagnosticsResponse>(JsonOptions),
            nameof(MonitoringSessionResponse) =>
                response.Deserialize<MonitoringSessionResponse>(JsonOptions),
            nameof(ToolStateResponse) =>
                response.Deserialize<ToolStateResponse>(JsonOptions),
            nameof(MsiToolInstallResponse) =>
                response.Deserialize<MsiToolInstallResponse>(JsonOptions),
            nameof(TestRunResultResponse) =>
                response.Deserialize<TestRunResultResponse>(JsonOptions),
            nameof(TestRunHistoryResponse) =>
                response.Deserialize<TestRunHistoryResponse>(JsonOptions),
            nameof(UserTestPresetListResponse) =>
                response.Deserialize<UserTestPresetListResponse>(JsonOptions),
            nameof(UserTestPresetSavedResponse) =>
                response.Deserialize<UserTestPresetSavedResponse>(JsonOptions),
            nameof(UserTestPresetDeletedResponse) =>
                response.Deserialize<UserTestPresetDeletedResponse>(JsonOptions),
            nameof(WorkspaceStateLoadedResponse) =>
                response.Deserialize<WorkspaceStateLoadedResponse>(JsonOptions),
            nameof(WorkspaceStateSavedResponse) =>
                response.Deserialize<WorkspaceStateSavedResponse>(JsonOptions),
            nameof(SimulationDocumentListResponse) =>
                response.Deserialize<SimulationDocumentListResponse>(JsonOptions),
            nameof(SimulationDocumentSavedResponse) =>
                response.Deserialize<SimulationDocumentSavedResponse>(JsonOptions),
            nameof(SimulationDocumentDeletedResponse) =>
                response.Deserialize<SimulationDocumentDeletedResponse>(JsonOptions),
            nameof(DiteLegacyImportPersistenceResponse) =>
                response.Deserialize<DiteLegacyImportPersistenceResponse>(JsonOptions),
            nameof(DiteLegacyImportHistoryResponse) =>
                response.Deserialize<DiteLegacyImportHistoryResponse>(JsonOptions),
            nameof(DiteLegacyImportSummaryResponse) =>
                response.Deserialize<DiteLegacyImportSummaryResponse>(JsonOptions),
            nameof(InventoryCaptureResponse) =>
                response.Deserialize<InventoryCaptureResponse>(JsonOptions),
            nameof(ManageInventoryCaptureResponse) =>
                response.Deserialize<ManageInventoryCaptureResponse>(JsonOptions),
            nameof(ManageInventoryLoadedResponse) =>
                response.Deserialize<ManageInventoryLoadedResponse>(JsonOptions),
            nameof(ExportArtifactResponse) =>
                response.Deserialize<ExportArtifactResponse>(JsonOptions),
            nameof(SystemSupportExecutionResponse) =>
                response.Deserialize<SystemSupportExecutionResponse>(JsonOptions),
            nameof(SystemSupportReviewResponse) =>
                response.Deserialize<SystemSupportReviewResponse>(JsonOptions),
            nameof(ShutdownResponse) =>
                response.Deserialize<ShutdownResponse>(JsonOptions),
            _ => throw new InvalidDataException(
                $"Unsupported Agent response {payload.ResponseType}.")
        };
    }

    private void Observe(AgentResponse? response)
    {
        if (response is AgentSnapshotResponse snapshot)
        {
            foreach (var process in snapshot.Snapshot.Processes)
            {
                observedEvents.Writer.TryWrite(
                    new AgentProcessStateEvent(process, timeProvider.GetUtcNow()));
            }
        }
    }

    private static IpcEnvelope Envelope<T>(
        Guid messageId,
        Guid correlationId,
        string messageType,
        T payload) =>
        Envelope(
            messageId,
            correlationId,
            messageType,
            payload!,
            typeof(T));

    private static IpcEnvelope Envelope(
        Guid messageId,
        Guid correlationId,
        string messageType,
        object payload,
        Type payloadType) =>
        new(
            IpcProtocol.CurrentVersion,
            messageId,
            correlationId,
            messageType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, payloadType, JsonOptions));

    private static ApplicationResult<T> Failure<T>(
        ApplicationStatus status,
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<T>.FromStatus(
            status,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                string.Empty,
                ApplicationMessageSeverity.Warning,
                []));

    private async ValueTask DisposeStreamAsync()
    {
        handshake = null;
        eventCancellation?.Cancel();
        if (eventStream is not null)
        {
            await eventStream.DisposeAsync();
            eventStream = null;
        }
        if (eventReaderTask is not null)
        {
            await eventReaderTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            eventReaderTask = null;
        }
        eventCancellation?.Dispose();
        eventCancellation = null;
        if (stream is not null)
        {
            await stream.DisposeAsync();
            stream = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record AgentResponseWirePayload(
        ApplicationStatus Status,
        CorrelationId CorrelationId,
        IReadOnlyList<ApplicationMessage> Messages,
        string? ResponseType,
        JsonElement? Response);
}
