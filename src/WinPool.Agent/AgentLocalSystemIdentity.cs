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
    public async Task<LocalSystemIdentityResolution> ResolveAsync(
        CancellationToken cancellationToken)
    {
        var persisted = await localDocument.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var previous = persisted is null
            ? null : LocalInventoryDocumentCodec.Decode(persisted.Document);
        var preferred = previous is not null
            && StringComparer.OrdinalIgnoreCase.Equals(
                previous.Snapshot.Computer.Name, Environment.MachineName)
            ? previous.SystemId : (WinPool.Domain.SystemId?)null;
        return await localIdentity.ResolveAsync(
            Environment.MachineName, preferred, cancellationToken)
            .ConfigureAwait(false);
    }
}
