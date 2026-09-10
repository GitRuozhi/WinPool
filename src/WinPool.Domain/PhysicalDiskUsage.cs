namespace WinPool.Domain;

/// <summary>
/// Canonical Windows physical-disk Usage strings. Retired and hot spare are
/// projections of this one value, never two independent flags.
/// </summary>
public static class PhysicalDiskUsage
{
    public const string Unknown = "";
    public const string AutoSelect = "Auto-Select";
    public const string DataStore = "Data Store";
    public const string ManualSelect = "Manual-Select";
    public const string HotSpare = "Hot Spare";
    public const string Retired = "Retired";
    public const string Journal = "Journal";

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return Unknown;
        }

        if (text.Equals("HotSpare", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Hot Spare", StringComparison.OrdinalIgnoreCase))
        {
            return HotSpare;
        }

        if (text.Equals(Retired, StringComparison.OrdinalIgnoreCase))
        {
            return Retired;
        }

        if (text.Equals("AutoSelect", StringComparison.OrdinalIgnoreCase)
            || text.Equals(AutoSelect, StringComparison.OrdinalIgnoreCase))
        {
            return AutoSelect;
        }

        if (text.Equals("DataStore", StringComparison.OrdinalIgnoreCase)
            || text.Equals(DataStore, StringComparison.OrdinalIgnoreCase))
        {
            return DataStore;
        }

        if (text.Equals("ManualSelect", StringComparison.OrdinalIgnoreCase)
            || text.Equals(ManualSelect, StringComparison.OrdinalIgnoreCase))
        {
            return ManualSelect;
        }

        if (text.Equals(Journal, StringComparison.OrdinalIgnoreCase))
        {
            return Journal;
        }

        return text;
    }

    public static bool IsRetired(string? usage) =>
        string.Equals(Normalize(usage), Retired, StringComparison.OrdinalIgnoreCase);

    public static bool IsHotSpare(string? usage) =>
        string.Equals(Normalize(usage), HotSpare, StringComparison.OrdinalIgnoreCase);

    public static bool ContributesDataCapacity(string? usage)
    {
        var normalized = Normalize(usage);
        return normalized is not HotSpare and not Retired and not Journal;
    }

    public static bool IsUnknown(string? usage)
    {
        var normalized = Normalize(usage);
        return normalized is not Unknown
            and not AutoSelect
            and not DataStore
            and not ManualSelect
            and not HotSpare
            and not Retired
            and not Journal;
    }

    public static string FromSimulatedLayer(string layer) => layer switch
    {
        "Retired" => Retired,
        "HotSpare" => HotSpare,
        "" => AutoSelect,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown simulated layer.")
    };

    public static string ToSimulatedLayer(string? usage)
    {
        if (IsRetired(usage))
        {
            return "Retired";
        }

        return IsHotSpare(usage) ? "HotSpare" : string.Empty;
    }
}
