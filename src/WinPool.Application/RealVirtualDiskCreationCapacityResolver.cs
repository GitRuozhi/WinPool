using System.Globalization;
using System.Numerics;
using WinPool.Application;

namespace WinPool.Application;

public readonly record struct RealVirtualDiskCreationCapacity(long SizeBytes, bool UseMaximumSize);

/// <summary>Preserves MAX intent for Agent-side freezing and search; explicit bytes are validated here.</summary>
public static class RealVirtualDiskCreationCapacityResolver
{
    public static string FormatGigabytes(long? bytes, CultureInfo? culture = null)
    {
        if (bytes is null or < 0) return string.Empty;
        culture ??= CultureInfo.CurrentCulture;
        var whole = bytes.Value / 1073741824L;
        var remainder = bytes.Value % 1073741824L;
        if (remainder == 0) return whole.ToString(culture);
        // 1 GiB = 2^30 bytes, so thirty decimal places represent any byte
        // exactly. decimal/double formatting can round an odd-byte goal.
        var fraction = (new BigInteger(remainder) * BigInteger.Pow(10, 30) / 1073741824L)
            .ToString("D30", CultureInfo.InvariantCulture).TrimEnd('0');
        return whole.ToString(culture) + culture.NumberFormat.NumberDecimalSeparator + fraction;
    }



    public static async Task<RealVirtualDiskCreationCapacity> ResolveAsync(
        bool useMaximumSize,
        long? requestedSizeBytes,
        bool autoCreatePartition,
        bool createMsr,
        Func<Task<RealVirtualDiskCreationRange>> readRangeAsync)
    {
        ArgumentNullException.ThrowIfNull(readRangeAsync);
        if (useMaximumSize)
            return new(0, true);

        var range = await readRangeAsync();
        var bytes = RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, false, requestedSizeBytes, autoCreatePartition, createMsr);
        return new(bytes, false);
    }
}
