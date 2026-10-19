using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
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

internal interface IAgentProcessLiveness
{
    bool IsExpectedAgentProcess(AgentEndpoint endpoint);
}

internal sealed class WindowsAgentProcessLiveness : IAgentProcessLiveness
{
    public bool IsExpectedAgentProcess(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            using var process = Process.GetProcessById(endpoint.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            if (!string.Equals(
                    process.ProcessName,
                    "WinPool.Agent",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var startedAt = process.StartTime.ToUniversalTime();
            var tolerance = TimeSpan.FromSeconds(5);
            return Math.Abs(
                (startedAt - endpoint.StartedAtUtc).TotalSeconds)
                <= tolerance.TotalSeconds;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
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
    private static readonly JsonSerializerOptions JsonOptions = IpcJson.CreateOptions();

    private readonly string endpointPath;
    private readonly IAgentProcessLauncher launcher;
    private readonly IAgentProcessLiveness agentProcessLiveness;
    private readonly TimeProvider timeProvider;
    private readonly Func<CancellationToken, Task>? beforeEventRecoveryConnectAsync;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim pendingSlots = new(32, 32);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> pending = new();
    private CancellationTokenSource? controlCancellation;
    private Task? controlReaderTask;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim eventRecoveryGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object lifetimeSync = new();
    private readonly TaskCompletionSource activeOperationsDrained = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid clientProcessInstanceId = Guid.NewGuid();
    private readonly AgentClientEventFanout eventFanout = new();
    private NamedPipeClientStream? stream;
    private NamedPipeClientStream? eventStream;
    private CancellationTokenSource? eventCancellation;
    private Task? eventReaderTask;
    private AgentHandshake? handshake;
    private TaskCompletionSource? reconnectPaused;
    private int disposeStarted;
    private int activeOperations;

    public NamedPipeAgentConnection(
        string endpointPath,
        IAgentProcessLauncher launcher,
        TimeProvider? timeProvider = null)
        : this(endpointPath, launcher, timeProvider, null, null)
    {
    }

    internal NamedPipeAgentConnection(
        string endpointPath,
        IAgentProcessLauncher launcher,
        TimeProvider? timeProvider,
        Func<CancellationToken, Task>? beforeEventRecoveryConnectAsync,
        IAgentProcessLiveness? agentProcessLiveness = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPath);
        this.endpointPath = Path.GetFullPath(endpointPath);
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.agentProcessLiveness =
            agentProcessLiveness ?? new WindowsAgentProcessLiveness();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.beforeEventRecoveryConnectAsync = beforeEventRecoveryConnectAsync;

    }

    public static string DefaultEndpointPath =>
        DataRootLayout.AgentEndpointPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPool"));

    /// <summary>
    /// Identity of the Agent accepted by the current control and event
    /// handshakes. It is deliberately unavailable until both handshakes and
    /// the recovery snapshot have completed.
    /// </summary>
    public AgentHandshake? ActiveHandshake
    {
        get
        {
            lock (lifetimeSync)
            {
                return handshake;
            }
        }
    }

    public async Task<ApplicationResult<AgentHandshake>> ConnectAsync(
        CancellationToken cancellationToken)
    {
        using var operation = EnterOperation(cancellationToken);
        var correlation = CorrelationId.New();
        while (true)
        {
            await connectionGate.WaitAsync(operation.Token);
            Task? resumed;
            lock (lifetimeSync)
            {
                resumed = stream?.IsConnected == true && handshake is not null
                    ? null : reconnectPaused?.Task;
            }
            if (resumed is null) break;
            connectionGate.Release();
            await resumed.WaitAsync(operation.Token);
        }
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
            var endpoint = await ReadLiveEndpointAsync(operation.Token);
            if (endpoint is null)
            {
                await launcher.EnsureStartedAsync(operation.Token);
                endpoint = await WaitForLiveEndpointAsync(operation.Token);
            }

            ValidateEndpoint(endpoint);
            var client = new NamedPipeClientStream(
                ".",
                endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(5_000, operation.Token);
                var sid = WindowsIdentity.GetCurrent().User?.Value
                    ?? throw new InvalidOperationException(
                        "The current Windows SID is unavailable.");
                var request = new AgentHandshakeRequest(
                    IpcProtocol.CurrentVersion,
                    endpoint.Nonce,
                    IpcIdentity.HashUserSid(sid),
                    Environment.ProcessId,
                    clientProcessInstanceId,
                    timeProvider.GetUtcNow());
                var messageId = Guid.NewGuid();
                await IpcFrameCodec.WriteAsync(
                    client,
                    Envelope(
                        messageId,
                        correlation.Value,
                        AgentControlMessageTypes.HandshakeRequest,
                        request),
                    operation.Token);
                var replyEnvelope = await IpcFrameCodec.ReadAsync(
                    client,
                    operation.Token);
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
                controlCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                controlReaderTask = ReadControlResponsesAsync(client, controlCancellation.Token);
                await ConnectEventStreamAsync(
                    reply,
                    endpoint,
                    operation.Token).ConfigureAwait(false);
                handshake = new AgentHandshake(
                    reply.ProtocolVersion,
                    new AgentInstanceId(reply.AgentSessionId),
                    reply.AgentProcessId,
                    AgentCapability.Monitoring
                    | AgentCapability.Inventory
                    | AgentCapability.Tray
                    | AgentCapability.Persistence
                    | AgentCapability.RealStorageOperations,
                    endpoint.StartedAtUtc);
                var snapshotResult = await SendConnectedAsync(
                        new GetAgentSnapshotRequest(CorrelationId.New()),
                        operation.Token)
                    .ConfigureAwait(false);
                if (!snapshotResult.IsSuccess
                    || snapshotResult.Value is not AgentSnapshotResponse snapshot)
                {
                    throw new IOException(
                        "The Agent did not provide a recovery snapshot after event subscription.");
                }

                eventFanout.PublishReseed(
                    snapshot.Snapshot,
                    "agent.events.connected_snapshot");
                StartEventReader(eventStream!);
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
        catch (OperationCanceledException) when (IsDisposing)
        {
            await DisposeStreamAsync();
            return Failure<AgentHandshake>(
                ApplicationStatus.RequiresEnvironment,
                correlation,
                "agent.connect.failed");
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
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
    /// Keeps the existing transport available for ordered shutdown/save/ack,
    /// while preventing a missing Agent from being launched during a handoff.
    /// A successful handoff leaves this pause owned by the old connection until
    /// disposal; an aborted handoff disposes the lease before explicit reconnect.
    /// </summary>
    public async Task<IDisposable> PauseReconnectAsync(CancellationToken cancellationToken)
    {
        using var operation = EnterOperation(cancellationToken);
        await connectionGate.WaitAsync(operation.Token);
        try
        {
            ThrowIfDisposed();
            lock (lifetimeSync)
            {
                if (reconnectPaused is not null)
                    throw new InvalidOperationException("An Agent handoff already owns the connection.");
                reconnectPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new ReconnectPause(this, reconnectPaused);
            }
        }
        finally { connectionGate.Release(); }
    }

    private bool IsReconnectPaused
    {
        get { lock (lifetimeSync) return reconnectPaused is not null; }
    }

    /// <summary>
    /// Drops a transport whose server has intentionally exited, then starts or
    /// connects to the replacement Agent. NamedPipeClientStream.IsConnected can
    /// remain true until another I/O operation observes the remote close.
    /// </summary>
    public async Task<ApplicationResult<AgentHandshake>> ReconnectAsync(
        CancellationToken cancellationToken)
    {
        using var operation = EnterOperation(cancellationToken);
        await connectionGate.WaitAsync(operation.Token);
        try
        {
            ThrowIfDisposed();
            await DisposeStreamAsync();
        }
        finally
        {
            connectionGate.Release();
        }

        return await ConnectAsync(operation.Token).ConfigureAwait(false);
    }

    public async Task<ApplicationResult<AgentResponse>> SendAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = EnterOperation(cancellationToken);
        var connected = await ConnectAsync(operation.Token).ConfigureAwait(false);
        if (!connected.IsSuccess)
            return new(connected.Status, null, connected.Messages, request.CorrelationId);
        return await SendConnectedAsync(request, operation.Token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = eventFanout.Subscribe();
        await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            await disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        lifetimeCancellation.Cancel();
        try
        {
            eventFanout.Dispose();
            lock (lifetimeSync)
            {
                if (activeOperations == 0)
                {
                    activeOperationsDrained.TrySetResult();
                }
            }
            await activeOperationsDrained.Task.ConfigureAwait(false);
            await DisposeStreamAsync().ConfigureAwait(false);
            connectionGate.Dispose();
            pendingSlots.Dispose();
            sendGate.Dispose();
            eventRecoveryGate.Dispose();
            lifetimeCancellation.Dispose();
        }
        finally
        {
            disposeCompletion.TrySetResult();
        }
    }

    private async Task<ApplicationResult<AgentResponse>> SendConnectedAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = EnterOperation(cancellationToken);
        var sent = false;
        var slot = false;
        var correlation = request.CorrelationId.Value;
        TaskCompletionSource<IpcEnvelope>? completion = null;
        try
        {
            await pendingSlots.WaitAsync(operation.Token).ConfigureAwait(false);
            slot = true;
            var target = stream;
            if (target?.IsConnected != true || handshake is null)
                return Failure<AgentResponse>(ApplicationStatus.RequiresEnvironment, request.CorrelationId, "agent.request.not_connected");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(correlation, completion))
                throw new InvalidDataException("The request correlation is already pending.");
            var envelope = request is SaveAgentSimulationDocumentRequest or CommitAgentSimulationEditRequest
                ? await DocumentPayloadLimits.RunLargeAsync(() => Envelope(Guid.NewGuid(), correlation,
                    RequestMessageType(request), request, request.GetType()), operation.Token)
                : Envelope(Guid.NewGuid(), correlation, RequestMessageType(request), request, request.GetType());
            if (!ReferenceEquals(stream, target)) throw new IOException("The control connection changed before send.");
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            transfer.CancelAfter(IpcProtocol.RequestTransferTimeout);
            using var payloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(transfer.Token, operation.Token);
            var writing = IpcPayloadTransfer.WriteAsync(target, envelope, sendGate, payloadCancellation.Token, () => sent = true);
            try { await writing.WaitAsync(operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                _ = writing.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            var responseEnvelope = await completion.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            var decoded = responseEnvelope.Payload.TryGetProperty("responseType", out var responseType)
                && responseType.GetString() is nameof(SimulationDocumentLoadedResponse) or nameof(ManageInventoryLoadedResponse)
                    or nameof(AgentRealOperationEvidenceResponse)
                ? await DocumentPayloadLimits.RunLargeAsync(() => DecodeControlResponse(responseEnvelope), operation.Token)
                : DecodeControlResponse(responseEnvelope);
            Observe(decoded.Response);
            return new(decoded.Payload.Status, decoded.Response, decoded.Payload.Messages, decoded.Payload.CorrelationId);
        }
        catch (IpcPayloadBusyException)
        {
            return Failure<AgentResponse>(ApplicationStatus.Rejected, request.CorrelationId, "agent.request.busy");
        }
        catch (IpcFrameTooLargeException)
        {
            return Failure<AgentResponse>(ApplicationStatus.Rejected, request.CorrelationId, "agent.request.frame_too_large");
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Failure<AgentResponse>(sent ? ApplicationStatus.OutcomeUnknown : ApplicationStatus.Cancelled,
                request.CorrelationId, sent ? "agent.request.outcome_unknown" : "agent.request.cancelled");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or NotSupportedException)
        {
            return Failure<AgentResponse>(sent ? ApplicationStatus.OutcomeUnknown : ApplicationStatus.RequiresEnvironment,
                request.CorrelationId, sent ? "agent.request.outcome_unknown" : "agent.request.connection_lost");
        }
        finally
        {
            if (completion is not null) ((ICollection<KeyValuePair<Guid, TaskCompletionSource<IpcEnvelope>>>)pending).Remove(new(correlation, completion));
            if (slot) pendingSlots.Release();
        }
    }

    private static (AgentResponseWirePayload Payload, AgentResponse? Response) DecodeControlResponse(IpcEnvelope envelope)
    {
        var payload = envelope.Payload.Deserialize<AgentResponseWirePayload>(JsonOptions)
            ?? throw new InvalidDataException("WinPool Agent returned an empty response.");
        return (payload, DeserializeResponse(payload));
    }

    private async Task ReadControlResponsesAsync(NamedPipeClientStream target, CancellationToken cancellationToken)
    {
        await using var bodies = new IpcPayloadTransfer(DocumentPayloadLimits.LargeParseGate, sendGate, (correlation, code) =>
        {
            if (pending.TryRemove(correlation, out var completion)) completion.TrySetException(new IpcPayloadBusyException(code));
        });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await bodies.ReadAsync(target, cancellationToken).ConfigureAwait(false);
                if (envelope.MessageType != AgentControlMessageTypes.Response)
                    throw new InvalidDataException("The Agent returned an invalid control response.");
                if (ReferenceEquals(stream, target) && pending.TryRemove(envelope.CorrelationId, out var completion))
                    completion.TrySetResult(envelope);
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or JsonException or FormatException)
        {
            if (ReferenceEquals(stream, target))
            {
                foreach (var entry in pending)
                    if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new IOException("The control connection closed.", exception));
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }
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

        return endpoint is not null && agentProcessLiveness.IsExpectedAgentProcess(endpoint)
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
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task SuperviseEventStreamAsync(
        NamedPipeClientStream client,
        CancellationToken cancellationToken)
    {
        var disconnectedUnexpectedly = false;
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
                if (item is not null && eventFanout.Publish(item).HasEventGap)
                {
                    disconnectedUnexpectedly = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (EndOfStreamException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (IOException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (ObjectDisposedException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (JsonException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (InvalidDataException)
        {
            disconnectedUnexpectedly = true;
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException
            and not AccessViolationException)
        {
            disconnectedUnexpectedly = true;
        }

        if (!disconnectedUnexpectedly
            || cancellationToken.IsCancellationRequested
            || IsDisposing
            || IsReconnectPaused)
        {
            return;
        }

        try
        {
            await eventRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (!ReferenceEquals(eventStream, client) || IsDisposing)
            {
                return;
            }

            // Intentional shutdown is owned by the handoff caller. Do not
            // publish a gap/reseed or let observers restart its old Agent.
            // An aborted handoff explicitly reconnects after releasing its lock.
            if (IsReconnectPaused) return;

            PublishTransportState(
                AgentEventTransportState.Disconnected,
                "agent.events.disconnected");
            PublishTransportState(
                AgentEventTransportState.Reconnecting,
                "agent.events.reconnecting");
            try
            {
                await DisconnectFailedEventTransportAsync(client, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var delay = TimeSpan.FromMilliseconds(100);
            while (!IsDisposing)
            {
                if (beforeEventRecoveryConnectAsync is not null)
                {
                    await beforeEventRecoveryConnectAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var connected = await ConnectAsync(attempt.Token).ConfigureAwait(false);
                if (connected.IsSuccess)
                {
                    PublishTransportState(
                        AgentEventTransportState.Reconnected,
                        "agent.events.reconnected_with_snapshot");
                    return;
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, 2_000));
            }
        }
        finally
        {
            eventRecoveryGate.Release();
        }
    }

    private void StartEventReader(NamedPipeClientStream client)
    {
        if (!ReferenceEquals(eventStream, client)
            || eventReaderTask is not null)
        {
            throw new InvalidOperationException(
                "The Agent event reader cannot start without its current stream.");
        }

        eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        eventReaderTask = SuperviseEventStreamAsync(
            client,
            eventCancellation.Token);
    }

    private void PublishTransportState(
        AgentEventTransportState state,
        string diagnosticCode) =>
        eventFanout.Publish(
            new AgentEventTransportStateEvent(
                state,
                HasEventGap: true,
                diagnosticCode,
                timeProvider.GetUtcNow()));

    private async Task DisconnectFailedEventTransportAsync(
        NamedPipeClientStream failedEventStream,
        CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(eventStream, failedEventStream)) return;
            handshake = null;
            await failedEventStream.DisposeAsync().ConfigureAwait(false);
            eventStream = null;
            eventCancellation?.Dispose();
            eventCancellation = null;
            eventReaderTask = null;
            await DisposeControlStreamAsync().ConfigureAwait(false);
        }
        finally { connectionGate.Release(); }
    }

    private static AgentEvent? DeserializeEvent(AgentEventWirePayload payload) =>
        payload.EventType switch
        {
            nameof(AgentTaskEvent) => payload.Event.Deserialize<AgentTaskEvent>(JsonOptions),
            nameof(AgentInventoryStartedEvent) => payload.Event.Deserialize<AgentInventoryStartedEvent>(JsonOptions),
            nameof(AgentInventoryUpdatedEvent) => payload.Event.Deserialize<AgentInventoryUpdatedEvent>(JsonOptions),
            nameof(AgentInventoryFailedEvent) => payload.Event.Deserialize<AgentInventoryFailedEvent>(JsonOptions),
            nameof(AgentMonitorBatchEvent) =>
                payload.Event.Deserialize<AgentMonitorBatchEvent>(JsonOptions),
            nameof(AgentMonitorStateEvent) =>
                payload.Event.Deserialize<AgentMonitorStateEvent>(JsonOptions),
            nameof(AgentRealOperationStatusEvent) =>
                payload.Event.Deserialize<AgentRealOperationStatusEvent>(JsonOptions),
            nameof(AgentRealStructureJobStatusEvent) => payload.Event.Deserialize<AgentRealStructureJobStatusEvent>(JsonOptions),
            nameof(AgentProcessStateEvent) =>
                payload.Event.Deserialize<AgentProcessStateEvent>(JsonOptions),
            nameof(PrepareAgentShutdownEvent) => payload.Event.Deserialize<PrepareAgentShutdownEvent>(JsonOptions),
            nameof(AgentShutdownPreparationEndedEvent) => payload.Event.Deserialize<AgentShutdownPreparationEndedEvent>(JsonOptions),
            nameof(AgentShutdownEvent) =>
                payload.Event.Deserialize<AgentShutdownEvent>(JsonOptions),
            nameof(AgentStateReseedEvent) =>
                payload.Event.Deserialize<AgentStateReseedEvent>(JsonOptions),
            nameof(AgentPreferencesChangedEvent) =>
                payload.Event.Deserialize<AgentPreferencesChangedEvent>(JsonOptions),
            _ => null
        };

    private static string RequestMessageType(AgentRequest request) =>
        request switch
        {
            GetAgentSnapshotRequest => AgentControlMessageTypes.GetSnapshot,
            OpenAgentNativePropertiesRequest => AgentControlMessageTypes.OpenNativeProperties,
            StartAgentMonitoringRequest => AgentControlMessageTypes.StartMonitoring,
            StopAgentMonitoringRequest => AgentControlMessageTypes.StopMonitoring,
            LoadAgentWorkspaceStateRequest =>
                AgentControlMessageTypes.LoadWorkspaceState,
            SaveAgentWorkspaceStateRequest =>
                AgentControlMessageTypes.SaveWorkspaceState,
            ListAgentSimulationDocumentsRequest =>
                AgentControlMessageTypes.ListSimulationDocuments,
            LoadAgentSimulationDocumentRequest =>
                AgentControlMessageTypes.LoadSimulationDocument,
            SaveAgentSimulationDocumentRequest =>
                AgentControlMessageTypes.SaveSimulationDocument,
            DeleteAgentSimulationDocumentRequest =>
                AgentControlMessageTypes.DeleteSimulationDocument,
            CommitAgentSimulationEditRequest =>
                AgentControlMessageTypes.CommitSimulationEdit,
            LookupAgentSimulationCommitRequest =>
                AgentControlMessageTypes.LookupSimulationCommit,
            CaptureAgentManageInventoryRequest =>
                AgentControlMessageTypes.CaptureManageInventory,
            LoadAgentInventoryCacheRequest =>
                AgentControlMessageTypes.LoadInventoryCache,
            ExportAgentMonitorCsvRequest => AgentControlMessageTypes.ExportMonitorCsv,
            SetAgentPreferenceRequest => AgentControlMessageTypes.SetAgentPreference,
            EnterAgentRealModeRequest => AgentControlMessageTypes.EnterRealMode,
            ExitAgentRealModeRequest => AgentControlMessageTypes.ExitRealMode,
            PrepareAgentRealOperationRequest =>
                AgentControlMessageTypes.PrepareRealOperation,
            AcceptAgentRealOperationRequest =>
                AgentControlMessageTypes.AcceptRealOperation,
            QueryAgentRealOperationRequest =>
                AgentControlMessageTypes.QueryRealOperation,
            ReconcileAgentRealOperationRequest => AgentControlMessageTypes.ReconcileRealOperation,
            LoadAgentRealOperationEvidenceRequest => AgentControlMessageTypes.LoadRealOperationEvidence,
            PrepareAgentRealStructureJobRequest => AgentControlMessageTypes.PrepareRealStructureJob,
            AcceptAgentRealStructureJobRequest => AgentControlMessageTypes.AcceptRealStructureJob,
            QueryAgentRealStructureJobRequest => AgentControlMessageTypes.QueryRealStructureJob,
            StopAgentRealStructureJobRequest => AgentControlMessageTypes.StopRealStructureJob,
            QueryAgentRealVirtualDiskCreationRangeRequest => AgentControlMessageTypes.QueryRealVirtualDiskCreationRange,
            QueryAgentRealStructureCreationSupportRequest => AgentControlMessageTypes.QueryRealStructureCreationSupport,
            ListAgentRecoverableRealOperationsRequest => AgentControlMessageTypes.ListRecoverableRealOperations,
            CaptureAgentManageScopedInventoryRequest => AgentControlMessageTypes.CaptureManageScopedInventory,
            QueryAgentRealPartitionResizeRangeRequest =>
                AgentControlMessageTypes.QueryRealPartitionResizeRange,
            StopAgentRealOperationFollowingStepsRequest =>
                AgentControlMessageTypes.StopRealOperationFollowingSteps,
            AcknowledgeAgentShutdownPreparationRequest => AgentControlMessageTypes.AcknowledgeShutdownPreparation,
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
            nameof(MonitoringSessionResponse) =>
                response.Deserialize<MonitoringSessionResponse>(JsonOptions),
            nameof(WorkspaceStateLoadedResponse) =>
                response.Deserialize<WorkspaceStateLoadedResponse>(JsonOptions),
            nameof(WorkspaceStateSavedResponse) =>
                response.Deserialize<WorkspaceStateSavedResponse>(JsonOptions),
            nameof(SimulationDocumentListResponse) =>
                response.Deserialize<SimulationDocumentListResponse>(JsonOptions),
            nameof(SimulationDocumentLoadedResponse) =>
                response.Deserialize<SimulationDocumentLoadedResponse>(JsonOptions),
            nameof(SimulationDocumentSavedResponse) =>
                response.Deserialize<SimulationDocumentSavedResponse>(JsonOptions),
            nameof(SimulationDocumentDeletedResponse) =>
                response.Deserialize<SimulationDocumentDeletedResponse>(JsonOptions),
            nameof(SimulationEditCommittedResponse) =>
                response.Deserialize<SimulationEditCommittedResponse>(JsonOptions),
            nameof(SimulationCommitLookupResponse) =>
                response.Deserialize<SimulationCommitLookupResponse>(JsonOptions),
            nameof(ManageInventoryCaptureResponse) =>
                response.Deserialize<ManageInventoryCaptureResponse>(JsonOptions),
            nameof(ManageInventoryLoadedResponse) =>
                response.Deserialize<ManageInventoryLoadedResponse>(JsonOptions),
            nameof(ExportArtifactResponse) =>
                response.Deserialize<ExportArtifactResponse>(JsonOptions),
            nameof(AgentPreferenceSavedResponse) =>
                response.Deserialize<AgentPreferenceSavedResponse>(JsonOptions),
            nameof(AgentRealModeResponse) =>
                response.Deserialize<AgentRealModeResponse>(JsonOptions),
            nameof(AgentRealOperationResponse) =>
                response.Deserialize<AgentRealOperationResponse>(JsonOptions),
            nameof(AgentRealOperationStatusResponse) =>
                response.Deserialize<AgentRealOperationStatusResponse>(JsonOptions),
            nameof(AgentRealOperationEvidenceResponse) =>
                response.Deserialize<AgentRealOperationEvidenceResponse>(JsonOptions),
            nameof(AgentRealStructureJobResponse) => response.Deserialize<AgentRealStructureJobResponse>(JsonOptions),
            nameof(AgentRealVirtualDiskCreationRangeResponse) => response.Deserialize<AgentRealVirtualDiskCreationRangeResponse>(JsonOptions),
            nameof(AgentRealStructureCreationSupportResponse) => response.Deserialize<AgentRealStructureCreationSupportResponse>(JsonOptions),
            nameof(AgentRecoverableRealOperationsResponse) => response.Deserialize<AgentRecoverableRealOperationsResponse>(JsonOptions),
            nameof(AgentRealPartitionResizeRangeResponse) =>
                response.Deserialize<AgentRealPartitionResizeRangeResponse>(JsonOptions),
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
            eventFanout.CacheSnapshot(snapshot.Snapshot);
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
        await DisposeControlStreamAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeControlStreamAsync()
    {
        controlCancellation?.Cancel();
        var old = stream;
        stream = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        foreach (var entry in pending)
            if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new IOException("The control generation ended."));
        if (controlReaderTask is not null)
            await controlReaderTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        controlReaderTask = null;
        controlCancellation?.Dispose();
        controlCancellation = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposing, this);
    }

    private bool IsDisposing => Volatile.Read(ref disposeStarted) != 0;

    private OperationLease EnterOperation(CancellationToken cancellationToken)
    {
        lock (lifetimeSync)
        {
            ThrowIfDisposed();
            activeOperations++;
        }

        try
        {
            return new OperationLease(this, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private void ExitOperation()
    {
        lock (lifetimeSync)
        {
            if (--activeOperations == 0 && IsDisposing)
            {
                activeOperationsDrained.TrySetResult();
            }
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private readonly NamedPipeAgentConnection owner;
        private readonly CancellationTokenSource linkedCancellation;
        private int disposed;

        public OperationLease(
            NamedPipeAgentConnection owner,
            CancellationToken callerCancellation)
        {
            this.owner = owner;
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                owner.lifetimeCancellation.Token);
        }

        public CancellationToken Token => linkedCancellation.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            linkedCancellation.Dispose();
            owner.ExitOperation();
        }
    }

    private sealed class ReconnectPause(
        NamedPipeAgentConnection owner,
        TaskCompletionSource resumed) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.lifetimeSync)
            {
                if (!ReferenceEquals(owner.reconnectPaused, resumed)) return;
                owner.reconnectPaused = null;
            }
            resumed.TrySetResult();
        }
    }

    private sealed record AgentResponseWirePayload(
        ApplicationStatus Status,
        CorrelationId CorrelationId,
        IReadOnlyList<ApplicationMessage> Messages,
        string? ResponseType,
        JsonElement? Response);
}
