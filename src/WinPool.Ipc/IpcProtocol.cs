using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinPool.Ipc;

public static class IpcProtocol
{
    public const int CurrentVersion = 4;
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan MaximumHandshakeAge = TimeSpan.FromSeconds(30);
}

public sealed record IpcEnvelope(
    int ProtocolVersion,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    DateTimeOffset SentAt,
    JsonElement Payload);

public sealed record AgentHandshakeRequest(
    int ProtocolVersion,
    Guid Nonce,
    string UserSidHash,
    int ProcessId,
    Guid ProcessInstanceId,
    DateTimeOffset SentAtUtc);

public sealed record AgentHandshakeReply(
    int ProtocolVersion,
    Guid ConnectionId,
    Guid AgentSessionId,
    int AgentProcessId,
    DateTimeOffset SentAtUtc,
    AgentEventPipeEndpoint? EventEndpoint = null);

public sealed record AgentEventPipeEndpoint(
    string PipeName,
    Guid ConnectionId,
    Guid Nonce,
    DateTimeOffset ExpiresAtUtc);

public sealed record AgentEventHandshakeRequest(
    int ProtocolVersion,
    Guid ConnectionId,
    Guid Nonce,
    int ClientProcessId,
    DateTimeOffset SentAtUtc);

public sealed record AgentEventHandshakeReply(
    int ProtocolVersion,
    Guid ConnectionId,
    int AgentProcessId,
    DateTimeOffset SentAtUtc);

public sealed record AgentEventWirePayload(
    string EventType,
    JsonElement Event);

public static class AgentEventMessageTypes
{
    public const string HandshakeRequest = "agent.events.handshake.request";
    public const string HandshakeAccepted = "agent.events.handshake.accepted";
    public const string Event = "agent.events.item";
}

public static class AgentEventHandshakeValidator
{
    public static HandshakeValidation Validate(
        AgentEventHandshakeRequest request,
        AgentEventPipeEndpoint endpoint,
        int expectedClientProcessId,
        int actualClientProcessId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (request.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            return Reject(HandshakeRejection.ProtocolMismatch, "ipc.events.protocol_mismatch");
        }

        if (request.ConnectionId == Guid.Empty ||
            request.ConnectionId != endpoint.ConnectionId ||
            request.Nonce == Guid.Empty ||
            request.Nonce != endpoint.Nonce)
        {
            return Reject(HandshakeRejection.NonceMismatch, "ipc.events.endpoint_mismatch");
        }

        if (request.ClientProcessId <= 0 ||
            request.ClientProcessId != expectedClientProcessId ||
            request.ClientProcessId != actualClientProcessId)
        {
            return Reject(HandshakeRejection.InvalidProcess, "ipc.events.process_mismatch");
        }

        if (endpoint.ExpiresAtUtc <= nowUtc ||
            endpoint.ExpiresAtUtc - nowUtc > TimeSpan.FromSeconds(30) ||
            (nowUtc - request.SentAtUtc).Duration() > IpcProtocol.MaximumHandshakeAge)
        {
            return Reject(HandshakeRejection.Expired, "ipc.events.expired");
        }

        return new(true, HandshakeRejection.None, "ipc.events.accepted");
    }

    private static HandshakeValidation Reject(
        HandshakeRejection rejection,
        string code) =>
        new(false, rejection, code);
}

public enum HandshakeRejection
{
    None,
    ProtocolMismatch,
    NonceMismatch,
    UserMismatch,
    InvalidProcess,
    Expired
}

public sealed record HandshakeValidation(
    bool IsAccepted,
    HandshakeRejection Rejection,
    string Code);

public static class IpcIdentity
{
    public static string HashUserSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sid.Trim().ToUpperInvariant())))
            .ToLowerInvariant();
    }

    public static string CreateAgentControlPipeName(string userSidHash, Guid nonce)
    {
        ValidateHash(userSidHash);
        if (nonce == Guid.Empty)
        {
            throw new ArgumentException("A random pipe nonce is required.", nameof(nonce));
        }

        return $"WinPool.Agent.Control.{userSidHash[..24]}.{nonce:N}";
    }

    public static string CreateAgentEventPipeName(
        string userSidHash,
        Guid connectionId,
        Guid nonce)
    {
        ValidateHash(userSidHash);
        if (connectionId == Guid.Empty || nonce == Guid.Empty)
        {
            throw new ArgumentException("Connection and nonce values must not be empty.");
        }

        return $"WinPool.Agent.Events.{userSidHash[..24]}.{connectionId:N}.{nonce:N}";
    }

    private static void ValidateHash(string userSidHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSidHash);
        if (userSidHash.Length != 64 || userSidHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The SID hash must be a SHA-256 hexadecimal value.", nameof(userSidHash));
        }
    }
}

public static class AgentHandshakeValidator
{
    public static HandshakeValidation Validate(
        AgentHandshakeRequest request,
        Guid expectedNonce,
        string expectedUserSidHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            return Reject(HandshakeRejection.ProtocolMismatch, "ipc.handshake.protocol_mismatch");
        }

        if (request.Nonce == Guid.Empty || request.Nonce != expectedNonce)
        {
            return Reject(HandshakeRejection.NonceMismatch, "ipc.handshake.nonce_mismatch");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(request.UserSidHash),
                Encoding.ASCII.GetBytes(expectedUserSidHash)))
        {
            return Reject(HandshakeRejection.UserMismatch, "ipc.handshake.user_mismatch");
        }

        if (request.ProcessId <= 0 || request.ProcessInstanceId == Guid.Empty)
        {
            return Reject(HandshakeRejection.InvalidProcess, "ipc.handshake.invalid_process");
        }

        var age = nowUtc - request.SentAtUtc;
        if (age.Duration() > IpcProtocol.MaximumHandshakeAge)
        {
            return Reject(HandshakeRejection.Expired, "ipc.handshake.expired");
        }

        return new(true, HandshakeRejection.None, "ipc.handshake.accepted");
    }

    private static HandshakeValidation Reject(HandshakeRejection rejection, string code) =>
        new(false, rejection, code);
}
