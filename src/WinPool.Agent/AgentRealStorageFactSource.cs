using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

/// <summary>
/// Gives each fresh Windows report the same durable Local identity as inventory.
/// It does not merge cached observations into real-operation facts.
/// </summary>
internal sealed class AgentRealStorageFactSource(
    IWindowsRealStorageFactSource source,
    AgentLocalSystemIdentity localIdentity) : IWindowsRealStorageFactSource
{
    public async Task<StorageSystemDocument> CaptureFreshAsync(
        CancellationToken cancellationToken)
    {
        var document = await source.CaptureFreshAsync(cancellationToken)
            .ConfigureAwait(false);
        // Rebinding must not hide an inconsistent or non-local collector report.
        // Freshness, complete sources and exact target checks remain in topology.
        if (document.Kind != StorageSystemKind.Local
            || document.SourceFacts is not { IsSimulation: false } facts
            || facts.SystemId != document.SystemId
            || !StringComparer.OrdinalIgnoreCase.Equals(
                document.Snapshot.Computer.Name, Environment.MachineName))
            throw new InvalidDataException("Real preflight requires consistent local Windows facts.");

        var identity = await localIdentity.ResolveAsync(cancellationToken)
            .ConfigureAwait(false);
        return document with
        {
            SystemId = identity.SystemId,
            SourceFacts = facts with { SystemId = identity.SystemId }
        };
    }
}
