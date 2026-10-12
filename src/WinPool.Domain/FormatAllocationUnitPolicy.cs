namespace WinPool.Domain;

/// <summary>
/// Describes allocation-unit sizes that WinPool can present for the selected
/// file system on the target Windows version. The real-operation planner still
/// checks the exact provider's current capability before formatting.
/// </summary>
public static class FormatAllocationUnitPolicy
{
    private const int MinimumNtfsClusterBytes = 512;
    private const int EarlierWindowsMaximumNtfsClusterBytes = 65_536;
    private const int CurrentWindowsMaximumNtfsClusterBytes = 2 * 1024 * 1024;
    private const int Windows10FirstBuild = 10_240;
    private const int Windows10Version1709Build = 16_299;

    private static readonly int[] RefsClusterSizes = [4_096, 65_536];
    private static readonly int[] ExFatClusterSizes = CreatePowerOfTwoSizes(9, 25);
    private static readonly int[] Fat32ClusterSizes = [4_096];

    public static IReadOnlyList<int> GetSupportedClusterSizes(string? fileSystem, string? osBuild) =>
        GetSupportedClusterSizes(fileSystem, ParseBuild(osBuild));

    public static IReadOnlyList<int> GetSupportedClusterSizes(string? fileSystem, int? osBuild)
    {
        switch (NormalizeFileSystem(fileSystem))
        {
            case "NTFS":
                var maximum = osBuild >= Windows10Version1709Build
                    ? CurrentWindowsMaximumNtfsClusterBytes
                    : EarlierWindowsMaximumNtfsClusterBytes;
                var maximumPower = System.Numerics.BitOperations.Log2((uint)maximum);
                return Enumerable.Range(9, maximumPower - 8)
                    .Select(power => 1 << power)
                    .ToArray();
            case "REFS":
                return RefsClusterSizes;
            case "EXFAT":
                return ExFatClusterSizes;
            case "FAT32":
                return Fat32ClusterSizes;
            default:
                return [];
        }
    }

    public static bool IsKnownClusterSize(string? fileSystem, int clusterBytes)
    {
        if (clusterBytes <= 0)
            return false;

        return NormalizeFileSystem(fileSystem) switch
        {
            "NTFS" => clusterBytes is >= MinimumNtfsClusterBytes and <= CurrentWindowsMaximumNtfsClusterBytes
                && (clusterBytes & (clusterBytes - 1)) == 0,
            "REFS" => RefsClusterSizes.Contains(clusterBytes),
            "EXFAT" => ExFatClusterSizes.Contains(clusterBytes),
            "FAT32" => Fat32ClusterSizes.Contains(clusterBytes),
            _ => false
        };
    }

    public static bool IsSupportedClusterSize(string? fileSystem, int clusterBytes, string? osBuild) =>
        IsSupportedClusterSize(fileSystem, clusterBytes, ParseBuild(osBuild));

    public static bool IsSupportedClusterSize(string? fileSystem, int clusterBytes, int? osBuild) =>
        clusterBytes > 0 && GetSupportedClusterSizes(fileSystem, osBuild).Contains(clusterBytes);

    public static int GetDefaultAllocationUnitBytes(string? fileSystem) =>
        NormalizeFileSystem(fileSystem) switch
        {
            "FAT32" => 4_096,
            _ => 65_536
        };

    private static int? ParseBuild(string? osBuild)
    {
        if (string.IsNullOrWhiteSpace(osBuild))
            return null;

        var separator = osBuild.IndexOf('.');
        var build = separator < 0 ? osBuild : osBuild[..separator];
        return int.TryParse(build, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int[] CreatePowerOfTwoSizes(int minimumPower, int maximumPower) =>
        Enumerable.Range(minimumPower, maximumPower - minimumPower + 1)
            .Select(power => 1 << power)
            .ToArray();

    private static string NormalizeFileSystem(string? fileSystem) =>
        (fileSystem ?? string.Empty).Trim().ToUpperInvariant();
}
