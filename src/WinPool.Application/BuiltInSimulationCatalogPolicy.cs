namespace WinPool.Application;

/// <summary>
/// Defines the persisted lifecycle of the built-in simulation catalog.
/// Missing built-ins are added only during the first successful seed or an
/// explicit reset; a user's deletion must therefore survive a later reload.
/// </summary>
public static class BuiltInSimulationCatalogPolicy
{
    private const string BuiltInIdPrefix = "simulation:builtin:";

    public static BuiltInSimulationCatalogPlan PlanLoad(
        IEnumerable<StorageSystemDocument> persisted,
        IEnumerable<StorageSystemDocument> builtIns,
        bool catalogSeeded)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(builtIns);

        var persistedById = persisted.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var merged = new List<StorageSystemDocument>();
        var documentsToPersist = new List<StorageSystemDocument>();
        foreach (var builtIn in builtIns)
        {
            if (persistedById.TryGetValue(builtIn.Id, out var existing))
            {
                if (existing.Snapshot.SnapshotVersion != builtIn.Snapshot.SnapshotVersion)
                {
                    var updated = RestoreBuiltIn(existing, builtIn);
                    merged.Add(updated);
                    documentsToPersist.Add(updated);
                }
                else
                {
                    merged.Add(existing);
                }

                continue;
            }

            // The first seed fills the initial catalog. Once it has completed,
            // a missing built-in is an intentional deletion rather than data to
            // silently recreate during startup.
            if (!catalogSeeded)
            {
                merged.Add(builtIn);
                documentsToPersist.Add(builtIn);
            }
        }

        merged.AddRange(persisted.Where(document => !IsBuiltIn(document.Id)));
        return new BuiltInSimulationCatalogPlan(
            merged,
            documentsToPersist,
            MarkCatalogSeededAfterPersist: !catalogSeeded);
    }

    /// <summary>
    /// Recreates every built-in document for the explicit Reset all defaults
    /// action. Existing documents retain their stable identity and receive a
    /// new revision so repository concurrency checks remain valid.
    /// </summary>
    public static BuiltInSimulationCatalogPlan PlanReset(
        IEnumerable<StorageSystemDocument> currentDocuments,
        IEnumerable<StorageSystemDocument> builtIns)
    {
        ArgumentNullException.ThrowIfNull(currentDocuments);
        ArgumentNullException.ThrowIfNull(builtIns);

        var currentById = currentDocuments.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var reset = builtIns
            .Select(builtIn => currentById.TryGetValue(builtIn.Id, out var existing)
                ? RestoreBuiltIn(existing, builtIn)
                : builtIn)
            .ToArray();
        return new BuiltInSimulationCatalogPlan(
            reset,
            reset,
            MarkCatalogSeededAfterPersist: true);
    }

    public static bool IsBuiltIn(string documentId) =>
        documentId.StartsWith(BuiltInIdPrefix, StringComparison.Ordinal);

    private static StorageSystemDocument RestoreBuiltIn(
        StorageSystemDocument existing,
        StorageSystemDocument builtIn) =>
        existing with
        {
            DisplayName = builtIn.DisplayName,
            SourceFacts = builtIn.SourceFacts is { } resetFacts
                ? resetFacts with
                {
                    SystemId = existing.SystemId,
                    Revision = checked((existing.SourceFacts?.Revision ?? 0) + 1)
                }
                : null,
            Jobs = [],
            Revision = checked(existing.Revision + 1),
            UpdatedAt = DateTimeOffset.Now
        };
}

/// <summary>
/// A startup/reset plan whose documents must all be persisted before the
/// preference marker is set. This lets the caller keep a failed seed retryable.
/// </summary>
public sealed record BuiltInSimulationCatalogPlan(
    IReadOnlyList<StorageSystemDocument> Documents,
    IReadOnlyList<StorageSystemDocument> DocumentsToPersist,
    bool MarkCatalogSeededAfterPersist);
