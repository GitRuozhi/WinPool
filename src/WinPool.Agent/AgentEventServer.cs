using System.Text.Json;
using System.Threading.Channels;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent;

public sealed class AgentEventHub
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Channel<AgentEvent>> _subscribers = [];

    public AgentEventSubscription Subscribe(int capacity = 1_024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentEvent>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        lock (_sync)
        {
            _subscribers.Add(id, channel);
        }

        return new AgentEventSubscription(id, channel.Reader, Remove);
    }

    public void Publish(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        Channel<AgentEvent>[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(agentEvent);
        }
    }

    private void Remove(Guid id)
    {
        Channel<AgentEvent>? channel;
        lock (_sync)
        {
            _subscribers.Remove(id, out channel);
        }

        channel?.Writer.TryComplete();
    }
}

public sealed class AgentEventSubscription(
    Guid id,
    ChannelReader<AgentEvent> reader,
    Action<Guid> remove) : IDisposable
{
    private int _disposed;

    public ChannelReader<AgentEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            remove(id);
        }
    }
}

public sealed class CurrentUserAgentEventServer
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly AgentEventHub _hub;
    private readonly AgentEventPipeEndpoint _endpoint;
    private readonly int _agentProcessId;
    private readonly int _expectedClientProcessId;
    private readonly Func<int, bool> _verifyClientProcess;
    private readonly TimeProvider _timeProvider;

    public CurrentUserAgentEventServer(
        AgentEventHub hub,
        AgentEventPipeEndpoint endpoint,
        int agentProcessId,
        int expectedClientProcessId,
        Func<int, bool> verifyClientProcess,
        TimeProvider? timeProvider = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(agentProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedClientProcessId);
        _agentProcessId = agentProcessId;
        _expectedClientProcessId = expectedClientProcessId;
        _verifyClientProcess = verifyClientProcess
            ?? throw new ArgumentNullException(nameof(verifyClientProcess));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pipe = CurrentUserPipeFactory.CreateServer(_endpoint.PipeName);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var remaining = _endpoint.ExpiresAtUtc - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        connectTimeout.CancelAfter(remaining);
        await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        var envelope = await IpcFrameCodec.ReadAsync(pipe, connectTimeout.Token)
            .ConfigureAwait(false);
        var request = envelope.Payload.Deserialize<AgentEventHandshakeRequest>(JsonOptions)
            ?? throw new InvalidDataException("The Agent event handshake is empty.");
        var validation = AgentEventHandshakeValidator.Validate(
            request,
            _endpoint,
            _expectedClientProcessId,
            CurrentUserPipeFactory.GetConnectedClientProcessId(pipe),
            _timeProvider.GetUtcNow());
        if (envelope.MessageType != AgentEventMessageTypes.HandshakeRequest ||
            !validation.IsAccepted ||
            !_verifyClientProcess(request.ClientProcessId))
        {
            throw new UnauthorizedAccessException("The Agent event client identity is invalid.");
        }

        using var subscription = _hub.Subscribe();
        await IpcFrameCodec.WriteAsync(
            pipe,
            Envelope(
                AgentEventMessageTypes.HandshakeAccepted,
                envelope.CorrelationId,
                new AgentEventHandshakeReply(
                    IpcProtocol.CurrentVersion,
                    _endpoint.ConnectionId,
                    _agentProcessId,
                    _timeProvider.GetUtcNow())),
            connectTimeout.Token).ConfigureAwait(false);

        await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            await IpcFrameCodec.WriteAsync(
                pipe,
                Envelope(
                    AgentEventMessageTypes.Event,
                    Guid.NewGuid(),
                    new AgentEventWirePayload(
                        item.GetType().Name,
                        JsonSerializer.SerializeToElement(
                            item,
                            item.GetType(),
                            JsonOptions))),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static IpcEnvelope Envelope<T>(
        string messageType,
        Guid correlationId,
        T payload) =>
        new(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            correlationId,
            messageType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
}
