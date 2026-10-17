using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

/// <summary>
/// Resolves the Agent-owned durable identity shared by inventory and fresh real
/// preflight. A proposal or a collector's transient ID is never an authority.
/// </summary>
internal sealed class AgentLocalSystemIdentity(
    LocalInventoryDocumentRepository localDocument,
    LocalSystemIdentityResolver localIdentity)
{
    private readonly SemaphoreSlim resolutionGate = new(1, 1);
    private LocalSystemIdentityResolution? cached;
    private long bindingRevision;

    // A repository/Agent instance belongs to one data root. A deliberate local
    // authority rebind invalidates this cache; changing roots starts a new Agent.
    public void Invalidate()
    {
        Interlocked.Increment(ref bindingRevision);
        Volatile.Write(ref cached, null);
    }

    public async Task<LocalSystemIdentityResolution> ResolveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref cached) is { } known) return known;
        await resolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is { } resolved) return resolved;
            var revision = Volatile.Read(ref bindingRevision);
            var persisted = await localDocument.LoadAsync(cancellationToken).ConfigureAwait(false);
            var previous = persisted is null ? null : LocalInventoryDocumentCodec.Decode(persisted.Document);
            var preferred = previous is not null && StringComparer.OrdinalIgnoreCase.Equals(
                previous.Snapshot.Computer.Name, Environment.MachineName)
                ? previous.SystemId : (WinPool.Domain.SystemId?)null;
            resolved = await localIdentity.ResolveAsync(Environment.MachineName, preferred, cancellationToken)
                .ConfigureAwait(false);
            if (revision == Volatile.Read(ref bindingRevision)) Volatile.Write(ref cached, resolved);
            return resolved;
        }
        finally { resolutionGate.Release(); }
    }
}
