namespace WinPool.Application;

/// <summary>Checks an imported document before it can become a persisted simulation.</summary>
public static class StorageSystemImportValidator
{
    public static void Validate(StorageSystemDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshot = document.Snapshot;
        if (string.IsNullOrWhiteSpace(document.DisplayName)
            || string.IsNullOrWhiteSpace(snapshot.Computer.StableId))
        {
            throw new InvalidDataException("The imported system is missing required identity data.");
        }

        var ids = snapshot.PhysicalDisks.Select(x => x.StableId)
            .Concat(snapshot.StoragePools.Select(x => x.StableId))
            .Concat(snapshot.StorageTiers.Select(x => x.StableId))
            .Concat(snapshot.VirtualDisks.Select(x => x.StableId))
            .Concat(snapshot.OsDisks.Select(x => x.StableId))
            .Concat(snapshot.Partitions.Select(x => x.StableId))
            .Concat(snapshot.NetworkDisks.Select(x => x.StableId))
            .ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
        {
            throw new InvalidDataException("The imported system contains invalid or duplicate object IDs.");
        }

        var geometryErrors = SimulationSnapshotAuditor.AuditPartitionGeometry(snapshot);
        if (geometryErrors.Count > 0)
        {
            throw new InvalidDataException(
                $"The imported system has invalid partition geometry: {geometryErrors[0]}");
        }
    }
}
