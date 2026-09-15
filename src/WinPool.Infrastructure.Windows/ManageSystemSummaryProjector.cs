using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public static class ManageSystemSummaryProjector
{
    public static IReadOnlyList<ManagePropertyView> Project(StorageSystemDocument document)
    {
        var snapshot = document.Snapshot;
        var uniquePhysical = snapshot.PhysicalDisks.DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase).ToList();
        return
        [
            Value("LocalStorage", Known(document, "MSFT_PhysicalDisk"),
                () => TopologyProjector.FormatBytes(uniquePhysical.Sum(x => x.Size))),
            Value("ExternalStorage", Known(document, "Win32_LogicalDisk"),
                () => TopologyProjector.FormatBytes(snapshot.NetworkDisks.Sum(x => x.Size))),
            Value("StoragePool", Known(document, "MSFT_StoragePool"), () => snapshot.StoragePools.Count.ToString()),
            Value("PhysicalDisk", Known(document, "MSFT_PhysicalDisk"), () => uniquePhysical.Count.ToString()),
            Value("VirtualDisk", Known(document, "MSFT_VirtualDisk"), () => snapshot.VirtualDisks.Count.ToString()),
            Value("Partition", Known(document, "MSFT_Partition"), () => snapshot.PartitionUnions.Count.ToString()),
            Value("AccessibleVolumes", Known(document, "MSFT_Volume", "Win32_LogicalDisk"),
                () => snapshot.PartitionUnions.Count(x => x.AccessPaths.Count > 0 || !string.IsNullOrWhiteSpace(x.DriveLetter)).ToString())
        ];
    }

    private static ManagePropertyView Value(string key, bool known, Func<string> value) => known
        ? new(key, value())
        : new(key, "Unknown", ManageValuePresentation.LocalizationKey);

    private static bool Known(StorageSystemDocument document, params string[] requiredClasses)
    {
        if (!document.IsLocal) return true;
        var facts = document.SourceFacts;
        if (facts is null) return false;
        return requiredClasses.All(className => facts.Sources
            .Where(x => x.ClassName.Equals(className, StringComparison.Ordinal))
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefault()?.ReadState == FieldReadState.Returned);
    }
}
