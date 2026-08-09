using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent;

public sealed class ElevatedBrokerProcessHost
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _brokerExecutablePath;
    private readonly string _userSidHash;
    private readonly int _agentProcessId;
    private readonly Guid _agentSessionId;
    private readonly string _dataRoot;
    private readonly TimeProvider _timeProvider;

    public ElevatedBrokerProcessHost(
        string brokerExecutablePath,
        string userSidHash,
        int agentProcessId,
        Guid agentSessionId,
        string dataRoot,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutablePath);
        if (!Path.IsPathFullyQualified(brokerExecutablePath) ||
            !File.Exists(brokerExecutablePath))
        {
            throw new FileNotFoundException(
                "The fixed elevated Broker executable was not found.",
                brokerExecutablePath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userSidHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(agentProcessId);
        if (agentSessionId == Guid.Empty)
        {
            throw new ArgumentException("An Agent session ID is required.", nameof(agentSessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _brokerExecutablePath = Path.GetFullPath(brokerExecutablePath);
        _userSidHash = userSidHash;
        _agentProcessId = agentProcessId;
        _agentSessionId = agentSessionId;
        _dataRoot = Path.GetFullPath(dataRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ElevatedBrokerExecutionResult> ExecuteAsync(
        ElevatedBrokerExecutionRequest request,
        Func<int, CancellationToken, Task>? brokerStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nonce = Guid.NewGuid();
        var expiresAtUtc = _timeProvider.GetUtcNow().AddMinutes(1);
        var pipeName = IpcIdentity.CreateElevatedBrokerPipeName(
            _userSidHash,
            _agentSessionId,
            nonce);
        var stamped = request with
        {
            Nonce = nonce,
            AgentSessionId = _agentSessionId,
            AgentProcessId = _agentProcessId,
            UserSidHash = _userSidHash,
            ExpiresAtUtc = expiresAtUtc
        };

        await using var server = CurrentUserPipeFactory.CreateServer(pipeName);
        using var broker = StartBroker(pipeName, nonce, expiresAtUtc);
        if (brokerStarted is not null)
        {
            await brokerStarted(broker.Id, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await AuthenticateAsync(
                server,
                broker.Id,
                nonce,
                expiresAtUtc,
                timeout.Token).ConfigureAwait(false);
            await WriteAsync(
                server,
                ElevatedBrokerMessageTypes.Execute,
                stamped,
                timeout.Token).ConfigureAwait(false);
            var envelope = await IpcFrameCodec.ReadAsync(server, timeout.Token)
                .ConfigureAwait(false);
            if (envelope.MessageType is not (
                    ElevatedBrokerMessageTypes.Completed or
                    ElevatedBrokerMessageTypes.Rejected))
            {
                throw new InvalidDataException("The elevated Broker returned an invalid result type.");
            }

            var result = envelope.Payload.Deserialize<ElevatedBrokerExecutionResult>(JsonOptions)
                ?? throw new InvalidDataException("The elevated Broker result is empty.");
            await broker.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                await broker.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task AuthenticateAsync(
        System.IO.Pipes.NamedPipeServerStream server,
        int brokerProcessId,
        Guid nonce,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var envelope = await IpcFrameCodec.ReadAsync(server, cancellationToken)
            .ConfigureAwait(false);
        var handshake =
            envelope.Payload.Deserialize<ElevatedBrokerHandshakeRequest>(JsonOptions)
            ?? throw new InvalidDataException("The elevated Broker handshake is empty.");
        var validation = ElevatedBrokerHandshakeValidator.Validate(
            handshake,
            nonce,
            _agentSessionId,
            _userSidHash,
            _agentProcessId,
            brokerProcessId,
            _timeProvider.GetUtcNow());
        if (envelope.MessageType != ElevatedBrokerMessageTypes.HandshakeRequest ||
            !validation.IsAccepted ||
            handshake.ExpiresAtUtc != expiresAtUtc ||
            CurrentUserPipeFactory.GetConnectedClientProcessId(server) != brokerProcessId)
        {
            throw new InvalidDataException("The elevated Broker handshake identity is invalid.");
        }

        await WriteAsync(
            server,
            ElevatedBrokerMessageTypes.HandshakeReply,
            new ElevatedBrokerHandshakeReply(
                IpcProtocol.CurrentVersion,
                _agentSessionId,
                _agentProcessId,
                brokerProcessId,
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    private Process StartBroker(
        string pipeName,
        Guid nonce,
        DateTimeOffset expiresAtUtc)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _brokerExecutablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(_brokerExecutablePath)!
        };
        foreach (var argument in new[]
                 {
                     "--pipe",
                     pipeName,
                     "--nonce",
                     nonce.ToString("N"),
                     "--agent-session",
                     _agentSessionId.ToString("N"),
                     "--agent-pid",
                     _agentProcessId.ToString(
                         System.Globalization.CultureInfo.InvariantCulture),
                     "--user-hash",
                     _userSidHash,
                     "--expires-utc",
                     expiresAtUtc.ToString("O"),
                     "--data-root",
                     _dataRoot
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo)
                   ?? throw new InvalidOperationException(
                       "Windows did not start the fixed elevated Broker.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "The user cancelled the elevated Broker request.",
                exception);
        }
    }

    private static ValueTask WriteAsync<T>(
        Stream stream,
        string messageType,
        T payload,
        CancellationToken cancellationToken) =>
        IpcFrameCodec.WriteAsync(
            stream,
            new IpcEnvelope(
                IpcProtocol.CurrentVersion,
                Guid.NewGuid(),
                Guid.NewGuid(),
                messageType,
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(payload, JsonOptions)),
            cancellationToken);
}
