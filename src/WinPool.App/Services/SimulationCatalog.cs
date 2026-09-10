using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

public static class SimulationCatalog
{
    public const string ReferenceDocumentId =
        "simulation:builtin:desktop-pl96ukd-20260727-114130";

    public static IReadOnlyList<StorageSystemDocument> CreateDocuments()
    {
        var documents = new List<StorageSystemDocument>
        {
            Document(
                ReferenceDocumentId,
                "DESKTOP-PL96UKD",
                SimulationStorageSnapshotFactory.Create(),
                KsReferenceReportFactory.Create())
        };
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            documents.Add(Document(layout.Id, layout.Name, layout.Snapshot));
        }

        return documents;
    }

    public static StorageSystemDocument? TryCreateDocument(string id) =>
        CreateDocuments().FirstOrDefault(
            document => document.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static StorageSystemDocument Document(
        string id,
        string name,
        StorageSnapshot snapshot,
        HardwareInventoryReport? hardwareReport = null)
    {
        snapshot = EditWorkspace.EnsureFreeDisksHaveOsDisks(snapshot);
        snapshot = StorageRelationshipProjector.Rebuild(snapshot);
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            id,
            StorageSystemKind.Simulation,
            name,
            snapshot,
            hardwareReport ?? HardwareInventoryReport.Empty(snapshot.ScannedAt),
            [],
            snapshot.ScannedAt);
    }
}
