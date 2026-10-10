using System.Globalization;
using System.Numerics;
using WinPool.Application;

namespace WinPool.App.Services;

internal readonly record struct RealVirtualDiskCreationCapacity(long SizeBytes, bool UseMaximumSize);

/// <summary>Preserves MAX intent for Agent-side freezing and search; explicit bytes are validated here.</summary>
internal static class RealVirtualDiskCreationCapacityResolver
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

    public static bool TryParseGigabytes(string text, out long? bytes, out bool maximum, CultureInfo? culture = null)
    {
        bytes = null;
        text = text.Trim();
        maximum = text.Equals("MAX", StringComparison.OrdinalIgnoreCase);
        if (maximum) return true;
        culture ??= CultureInfo.CurrentCulture;
        if (!decimal.TryParse(text, NumberStyles.Number, culture, out var gib) || gib <= 0)
            return false;
        var format = culture.NumberFormat;
        var normalized = string.IsNullOrEmpty(format.NumberGroupSeparator)
            ? text : text.Replace(format.NumberGroupSeparator, string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace(format.NumberDecimalSeparator, ".", StringComparison.Ordinal);
        if (normalized.StartsWith(format.PositiveSign, StringComparison.Ordinal))
            normalized = normalized[format.PositiveSign.Length..];
        var parts = normalized.Split('.');
        if (parts.Length > 2 || !BigInteger.TryParse(string.Concat(parts), NumberStyles.None,
                CultureInfo.InvariantCulture, out var digits))
            return false;
        var divisor = BigInteger.Pow(10, parts.Length == 2 ? parts[1].Length : 0);
        var value = BigInteger.DivRem(digits * 1073741824L, divisor, out var fraction);
        if (fraction != 0 || value <= 0 || value > long.MaxValue) return false;
        bytes = (long)value;
        return true;
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
