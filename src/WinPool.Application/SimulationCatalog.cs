using WinPool.Application;

namespace WinPool.Application;

public static class SimulationCatalog
{
    public const string ReferenceDocumentId =
        "simulation:builtin:desktop-pl96ukd-20260727-114130";

    public static IReadOnlyList<StorageSystemDocument> CreateDocuments(DateTimeOffset? frozenAt = null)
    {
        var documents = new List<StorageSystemDocument>
        {
            Document(
                ReferenceDocumentId,
                "DESKTOP-PL96UKD",
                SimulationStorageSnapshotFactory.Create(frozenAt))
        };
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            documents.Add(Document(layout.Id, layout.Name, layout.Snapshot));
        }

        return documents;
    }

    public static StorageSystemDocument? TryCreateDocument(string id, DateTimeOffset? frozenAt = null) =>
        CreateDocuments(frozenAt).FirstOrDefault(
            document => document.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static StorageSystemDocument Document(
        string id,
        string name,
        StorageSnapshot snapshot)
    {
        snapshot = EditWorkspace.EnsureFreeDisksHaveOsDisks(snapshot);
        snapshot = StorageRelationshipProjector.Rebuild(snapshot);
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            id,
            StorageSystemKind.Simulation,
            name,
            snapshot,
            [],
            snapshot.ScannedAt);
    }
}
