using WinPool.Application;

namespace WinPool.App.Services;

public static class RealPartitionResizeUiRange
{
    private const long Mib = 1024L * 1024;

    public static bool IsSupportedFileSystem(string? fileSystem) =>
        string.IsNullOrWhiteSpace(fileSystem) ||
        fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetWholeMibTargets(
        RealPartitionResizeRange range, bool extend,
        out long minimumMib, out long maximumMib)
    {
        ArgumentNullException.ThrowIfNull(range);
        minimumMib = 0;
        maximumMib = -1;
        if (range.CurrentSizeBytes <= 0 || range.AllowedMinBytes <= 0 ||
            range.AllowedMaxBytes < range.AllowedMinBytes)
            return false;
        var lower = extend
            ? Math.Max(range.AllowedMinBytes,
                range.CurrentSizeBytes == long.MaxValue
                    ? long.MaxValue : range.CurrentSizeBytes + 1)
            : range.AllowedMinBytes;
        var upper = extend
            ? range.AllowedMaxBytes
            : Math.Min(range.AllowedMaxBytes, range.CurrentSizeBytes - 1);
        minimumMib = lower <= 0 ? 0 : 1 + (lower - 1) / Mib;
        maximumMib = upper < 0 ? -1 : upper / Mib;
        return minimumMib > 0 && minimumMib <= maximumMib;
    }
}
