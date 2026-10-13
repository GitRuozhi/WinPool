using WinPool.Application;

namespace WinPool.App.Services;

public static class RealPartitionResizeUiRange
{
    private const long Mib = 1024L * 1024;

    public static bool IsSupportedFileSystem(string? fileSystem) =>
        string.IsNullOrWhiteSpace(fileSystem) ||
        fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetWholeMibTargets(
        RealPartitionResizeRange range, bool extend,
        out long minimumMib, out long maximumMib)
    {
        ArgumentNullException.ThrowIfNull(range);
        return TryGetWholeMibTargets(
            range.CurrentSizeBytes,
            range.AllowedMinBytes,
            range.AllowedMaxBytes,
            extend,
            out minimumMib,
            out maximumMib);
    }

    public static bool TryGetWholeMibTargets(
        long currentSizeBytes,
        long allowedMinBytes,
        long allowedMaxBytes,
        bool extend,
        out long minimumMib,
        out long maximumMib)
    {
        minimumMib = 0;
        maximumMib = -1;
        if (currentSizeBytes <= 0
            || allowedMinBytes <= 0
            || allowedMaxBytes < allowedMinBytes)
            return false;
        var lower = extend
            ? Math.Max(allowedMinBytes,
                currentSizeBytes == long.MaxValue ? long.MaxValue : currentSizeBytes + 1)
            : allowedMinBytes;
        var upper = extend
            ? allowedMaxBytes
            : Math.Min(allowedMaxBytes, currentSizeBytes - 1);
        minimumMib = lower <= 0 ? 0 : 1 + (lower - 1) / Mib;
        maximumMib = upper < 0 ? -1 : upper / Mib;
        return minimumMib > 0 && minimumMib <= maximumMib;
    }

    public static bool IsWholeMib(long bytes) => bytes > 0 && bytes % Mib == 0;

    public static bool TryClampResizeFormula(
        long currentSizeBytes, long inputMib, bool inputIsDelta, bool extend,
        long minimumTargetMib, long maximumTargetMib,
        out long targetSizeBytes, out long deltaBytes)
    {
        targetSizeBytes = 0;
        deltaBytes = 0;
        if (currentSizeBytes <= 0 || minimumTargetMib <= 0
            || maximumTargetMib < minimumTargetMib || maximumTargetMib > long.MaxValue / Mib)
            return false;
        var currentMib = currentSizeBytes / Mib;
        if (extend ? minimumTargetMib * Mib <= currentSizeBytes : maximumTargetMib * Mib >= currentSizeBytes)
            return false;
        var minimumInputMib = inputIsDelta
            ? extend ? minimumTargetMib - currentMib : currentMib - maximumTargetMib
            : minimumTargetMib;
        var maximumInputMib = inputIsDelta
            ? extend ? maximumTargetMib - currentMib : currentMib - minimumTargetMib
            : maximumTargetMib;
        return TryGetResizeFormula(currentSizeBytes,
            Math.Clamp(inputMib, minimumInputMib, maximumInputMib), inputIsDelta, extend,
            out targetSizeBytes, out deltaBytes);
    }

    public static bool TryGetResizeFormula(
        long currentSizeBytes,
        long inputMib,
        bool inputIsDelta,
        bool extend,
        out long targetSizeBytes,
        out long deltaBytes)
    {
        targetSizeBytes = 0;
        deltaBytes = 0;
        if (currentSizeBytes <= 0
            || inputMib < (inputIsDelta ? 0 : 1)
            || inputMib > long.MaxValue / Mib)
            return false;

        var inputBytes = inputMib * Mib;
        if (inputIsDelta)
        {
            // A is an integer-MiB display. C remains a whole-MiB target,
            // while the authoritative change is computed from the exact current bytes.
            var displayedCurrentBytes = currentSizeBytes / Mib * Mib;
            if (extend)
            {
                if (displayedCurrentBytes > long.MaxValue - inputBytes)
                    return false;
                targetSizeBytes = displayedCurrentBytes + inputBytes;
            }
            else
            {
                if (inputBytes >= displayedCurrentBytes)
                    return false;
                targetSizeBytes = displayedCurrentBytes - inputBytes;
            }
            deltaBytes = extend ? targetSizeBytes - currentSizeBytes : currentSizeBytes - targetSizeBytes;
        }
        else
        {
            targetSizeBytes = inputBytes;
            if (extend)
            {
                if (targetSizeBytes <= currentSizeBytes)
                    return false;
                deltaBytes = targetSizeBytes - currentSizeBytes;
            }
            else
            {
                if (targetSizeBytes >= currentSizeBytes)
                    return false;
                deltaBytes = currentSizeBytes - targetSizeBytes;
            }
        }

        return targetSizeBytes > 0 && deltaBytes > 0;
    }
}
