using WinPool.Application;
using WinPool.Execution;

namespace WinPool.Agent;

/// <summary>
/// An OS-observed control-pipe peer. The process instance and Agent session IDs
/// come from the accepted handshake; they are never read from a mutation payload.
/// </summary>
public sealed record AgentVerifiedPeer(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ImagePath,
    ProcessInstanceId ProcessInstanceId,
    Guid AgentSessionId);

/// <summary>
/// Holds the current App's real-mode choice only in Agent memory. Pipe renewal
/// leaves it intact; a different or exited process cannot use the choice.
/// </summary>
public sealed class AgentRealModeGate
{
    private readonly object sync = new();
    private readonly Guid agentSessionId;
    private readonly string expectedAppImagePath;
    private readonly IProcessIncarnationVerifier processVerifier;
    private ArmedSession? armed;

    public AgentRealModeGate(
        Guid agentSessionId,
        string expectedAppImagePath,
        IProcessIncarnationVerifier processVerifier)
    {
        if (agentSessionId == Guid.Empty)
        {
            throw new ArgumentException("An Agent session ID is required.", nameof(agentSessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAppImagePath);
        if (!Path.IsPathFullyQualified(expectedAppImagePath))
        {
            throw new ArgumentException("The App image path must be absolute.", nameof(expectedAppImagePath));
        }

        this.agentSessionId = agentSessionId;
        this.expectedAppImagePath = Path.GetFullPath(expectedAppImagePath);
        this.processVerifier = processVerifier ?? throw new ArgumentNullException(nameof(processVerifier));
    }

    public bool TryEnter(
        AgentVerifiedPeer peer,
        string productSessionId,
        out string code)
    {
        if (string.IsNullOrWhiteSpace(productSessionId))
        {
            code = "agent.real_mode.invalid_product_session";
            return false;
        }

        lock (sync)
        {
            if (!IsCurrentPeer(peer))
            {
                if (armed is not null && Equals(armed.Peer, peer))
                {
                    armed = null;
                }
                code = "agent.real_mode.client_identity_changed";
                return false;
            }

            if (armed is not null && !IsCurrentPeer(armed.Peer))
            {
                armed = null;
            }

            if (armed is not null
                && (!Equals(armed.Peer, peer)
                    || !StringComparer.Ordinal.Equals(
                        armed.ProductSessionId,
                        productSessionId)))
            {
                code = "agent.real_mode.another_session_armed";
                return false;
            }

            armed = new(peer, productSessionId);
            code = "agent.real_mode.armed";
            return true;
        }
    }

    public bool TryUseForNewWrite(
        AgentVerifiedPeer peer,
        string productSessionId,
        out string code)
    {
        lock (sync)
        {
            if (!IsCurrentPeer(peer))
            {
                if (armed is not null && Equals(armed.Peer, peer))
                {
                    armed = null;
                }

                code = "agent.real_mode.client_identity_changed";
                return false;
            }

            if (armed is null
                || !Equals(armed.Peer, peer)
                || !StringComparer.Ordinal.Equals(
                    armed.ProductSessionId,
                    productSessionId))
            {
                code = "agent.real_mode.not_armed";
                return false;
            }

            code = "agent.real_mode.armed";
            return true;
        }
    }

    public bool IsSessionArmed(TrustedRealSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Guid.TryParse(session.ProcessInstanceId, out var instanceId))
        {
            return false;
        }

        var peer = new AgentVerifiedPeer(
            session.ProcessId,
            session.ProcessStartUtc,
            session.ImagePath,
            new ProcessInstanceId(instanceId),
            session.AgentSessionId.Value);
        return TryUseForNewWrite(peer, session.ProductSessionId, out _);
    }

    public bool TryValidatePeer(AgentVerifiedPeer peer, out string code)
    {
        lock (sync)
        {
            if (!IsCurrentPeer(peer))
            {
                if (armed is not null && Equals(armed.Peer, peer))
                {
                    armed = null;
                }

                code = "agent.real_mode.client_identity_changed";
                return false;
            }

            code = "agent.real_mode.client_verified";
            return true;
        }
    }

    public bool TryExit(AgentVerifiedPeer peer, string productSessionId)
    {
        lock (sync)
        {
            if (armed is null
                || !Equals(armed.Peer, peer)
                || !StringComparer.Ordinal.Equals(armed.ProductSessionId, productSessionId))
            {
                return false;
            }

            armed = null;
            return true;
        }
    }

    public void Revoke() { lock (sync) armed = null; }

    private bool IsCurrentPeer(AgentVerifiedPeer? peer)
    {
        if (peer is null
            || peer.AgentSessionId != agentSessionId
            || peer.ProcessId <= 0
            || peer.ProcessInstanceId.Value == Guid.Empty
            || !Path.IsPathFullyQualified(peer.ImagePath)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(peer.ImagePath), expectedAppImagePath))
        {
            return false;
        }

        var witness = processVerifier.TryRead(peer.ProcessId);
        return witness is not null
            && witness.ProcessId == peer.ProcessId
            && witness.StartedAtUtc.ToUnixTimeMilliseconds()
                == peer.StartedAtUtc.ToUnixTimeMilliseconds()
            && StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(witness.ImagePath), expectedAppImagePath);
    }

    private sealed record ArmedSession(
        AgentVerifiedPeer Peer,
        string ProductSessionId);
}
