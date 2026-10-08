using System.Management;
using System.Numerics;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public sealed record VirtualDiskCreationSize(
    long MinimumBytes,
    long MaximumBytes,
    long DivisorBytes,
    IReadOnlyList<long> EnumeratedSizes)
{
    // Pool sizes use absolute multiples. Tier GetSupportedSize instead describes
    // a sequence beginning at TierSizeMin, with TierSizeDivisor as its increment.
    public long RangeOriginBytes { get; init; }

    public bool Supports(long bytes) => bytes > 0 &&
        (EnumeratedSizes.Count > 0
            ? EnumeratedSizes.Contains(bytes)
            : bytes >= MinimumBytes && bytes <= MaximumBytes
              && MinimumBytes > 0 && MaximumBytes >= MinimumBytes
              && DivisorBytes > 0 && RangeOriginBytes >= 0 && bytes >= RangeOriginBytes
              && (bytes - RangeOriginBytes) % DivisorBytes == 0);

    /// <summary>Intersection of two live provider constraints, without estimating or reserving capacity.</summary>
    public static VirtualDiskCreationSize Intersect(VirtualDiskCreationSize left, VirtualDiskCreationSize right)
    {
        Validate(left); Validate(right);
        if (left.EnumeratedSizes.Count > 0 || right.EnumeratedSizes.Count > 0)
        {
            var enumeration = left.EnumeratedSizes.Count > 0 ? left : right;
            var other = ReferenceEquals(enumeration, left) ? right : left;
            var sizes = enumeration.EnumeratedSizes.Where(other.Supports).Distinct().Order().ToArray();
            if (sizes.Length == 0) throw new InvalidDataException("Pool and tier creation size constraints have no common size.");
            return new(sizes[0], sizes[^1], 0, sizes);
        }

        // Solve both grids exactly. BigInteger prevents an overflowing LCM or
        // intermediate product from admitting an unsupported size.
        BigInteger a = left.DivisorBytes, b = right.DivisorBytes;
        var gcd = BigInteger.GreatestCommonDivisor(a, b);
        var difference = (BigInteger)right.RangeOriginBytes - left.RangeOriginBytes;
        if (difference % gcd != 0) throw new InvalidDataException("Pool and tier creation size grids do not intersect.");
        var modulus = b / gcd;
        BigInteger inverse = 0, nextInverse = 1, remainder = modulus, nextRemainder = a / gcd;
        while (nextRemainder != 0)
        {
            var quotient = remainder / nextRemainder;
            (inverse, nextInverse) = (nextInverse, inverse - quotient * nextInverse);
            (remainder, nextRemainder) = (nextRemainder, remainder - quotient * nextRemainder);
        }
        static BigInteger Mod(BigInteger value, BigInteger divisor) => (value % divisor + divisor) % divisor;
        var period = a * modulus;
        var residue = Mod(left.RangeOriginBytes + a * Mod(difference / gcd * inverse, modulus), period);
        BigInteger minimum = Math.Max(left.MinimumBytes, right.MinimumBytes);
        BigInteger maximum = Math.Min(left.MaximumBytes, right.MaximumBytes);
        var first = minimum + Mod(residue - minimum, period);
        if (first > maximum) throw new InvalidDataException("Pool and tier creation size constraints have no common size.");
        var last = maximum - Mod(maximum - residue, period);
        if (first == last) return new((long)first, (long)last, 0, [(long)first]);
        return new((long)first, (long)last, (long)period, []) { RangeOriginBytes = (long)first };
    }

    private static void Validate(VirtualDiskCreationSize range)
    {
        if (range.EnumeratedSizes.Count > 0)
        {
            if (range.EnumeratedSizes.Any(size => size <= 0)
                || range.EnumeratedSizes.Distinct().Count() != range.EnumeratedSizes.Count)
                throw new InvalidDataException("A provider creation enumeration is invalid.");
        }
        else if (range.MinimumBytes <= 0 || range.MaximumBytes < range.MinimumBytes
            || range.DivisorBytes <= 0 || range.RangeOriginBytes < 0
            || range.MinimumBytes < range.RangeOriginBytes)
            throw new InvalidDataException("A provider creation range is incomplete.");
    }
}

/// <summary>A successful exact-pool GetSupportedSize invocation, without any capacity claim.</summary>
public sealed record VirtualDiskCreationSupportProbe(
    string PoolUniqueId,
    string PoolObjectId,
    string ResiliencySettingName,
    uint ReturnValue,
    DateTimeOffset ObservedAtUtc);

public interface IVirtualDiskCreationSizeReader
{
    Task<VirtualDiskCreationSize> ReadAsync(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken);

    Task<VirtualDiskCreationSupportProbe> ProbeAsync(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken) =>
        Task.FromException<VirtualDiskCreationSupportProbe>(
            new NotSupportedException("Creation support probes are unavailable."));
}

/// <summary>
/// Calls the read-only MSFT_StoragePool.GetSupportedSize method on one exact
/// local pool. These values apply to creation, never to resizing an existing VD.
/// </summary>
public sealed class WindowsVirtualDiskCreationSizeReader : IVirtualDiskCreationSizeReader
{
    public Task<VirtualDiskCreationSize> ReadAsync(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken) => Task.Run(() =>
            Read(pool, cancellationToken), cancellationToken);

    public Task<VirtualDiskCreationSupportProbe> ProbeAsync(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken) => Task.Run(() =>
            Probe(pool, cancellationToken), cancellationToken);

    private static VirtualDiskCreationSupportProbe Probe(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken)
    {
        RequireExactConcretePoolTarget(pool);
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT UniqueId,ObjectId,IsPrimordial,IsReadOnly,IsClustered FROM MSFT_StoragePool");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var candidates = results.Cast<ManagementObject>().ToArray();
        try
        {
            var matches = FindExactConcretePool(candidates, pool);
            using var input = matches[0].GetMethodParameters("GetSupportedSize");
            if (input is null)
                throw new NotSupportedException("The exact pool does not expose GetSupportedSize inputs.");
            input["ResiliencySettingName"] = "Simple";
            using var output = matches[0].InvokeMethod("GetSupportedSize", input, null);
            if (output is null || output["ReturnValue"] is null)
                throw new NotSupportedException("The exact pool did not return a GetSupportedSize result.");
            var returnValue = Convert.ToUInt32(output["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            if (returnValue != 0)
                throw new NotSupportedException("The exact pool rejected the read-only Simple GetSupportedSize query.");
            return new VirtualDiskCreationSupportProbe(pool.UniqueId, pool.ObjectId,
                "Simple", returnValue, DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static VirtualDiskCreationSize Read(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken)
    {
        RequireExactConcretePoolTarget(pool);
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT UniqueId,ObjectId,IsPrimordial,IsReadOnly,IsClustered FROM MSFT_StoragePool");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var candidates = results.Cast<ManagementObject>().ToArray();
        try
        {
            var matches = FindExactConcretePool(candidates, pool);
            using var input = matches[0].GetMethodParameters("GetSupportedSize");
            input["ResiliencySettingName"] = "Simple";
            using var output = matches[0].InvokeMethod("GetSupportedSize", input, null);
            if (output is null || output["ReturnValue"] is null
                || Convert.ToUInt32(output["ReturnValue"]) != 0)
                throw new InvalidDataException("The pool did not return a supported Simple virtual-disk size.");
            var sizes = output["SupportedSizes"] is Array enumerated
                ? enumerated.Cast<object>().Select(Convert.ToInt64).ToArray()
                : [];
            var minimum = output["VirtualDiskSizeMin"] is null ? 0L
                : Convert.ToInt64(output["VirtualDiskSizeMin"]);
            var maximum = output["VirtualDiskSizeMax"] is null ? 0L
                : Convert.ToInt64(output["VirtualDiskSizeMax"]);
            var divisor = output["VirtualDiskSizeDivisor"] is null ? 0L
                : Convert.ToInt64(output["VirtualDiskSizeDivisor"]);
            if (sizes.Length == 0 && (minimum <= 0 || maximum < minimum || divisor <= 0))
                throw new InvalidDataException("The pool returned an incomplete Simple size range.");
            if (sizes.Any(value => value <= 0 || divisor <= 0 || value % divisor != 0))
                throw new InvalidDataException("The pool returned inconsistent supported sizes.");
            return new VirtualDiskCreationSize(minimum, maximum, divisor, sizes);
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static void RequireExactConcretePoolTarget(WindowsStorageCommandTarget pool)
    {
        if (pool.Kind != StorageObjectKind.StoragePool
            || string.IsNullOrWhiteSpace(pool.UniqueId)
            || string.IsNullOrWhiteSpace(pool.ObjectId))
            throw new InvalidDataException("An exact concrete pool is required for creation size validation.");
    }

    private static ManagementObject[] FindExactConcretePool(
        IEnumerable<ManagementObject> candidates,
        WindowsStorageCommandTarget pool)
    {
        var matches = candidates.Where(item =>
            StringComparer.Ordinal.Equals(item["UniqueId"]?.ToString(), pool.UniqueId)
            && StringComparer.Ordinal.Equals(item["ObjectId"]?.ToString(), pool.ObjectId)
            && item["IsPrimordial"] is false).ToArray();
        if (matches.Length != 1 || matches[0]["IsReadOnly"] is not false
            || matches[0]["IsClustered"] is not false)
            throw new InvalidDataException("The concrete pool is absent, ambiguous, clustered, or read-only.");
        return matches;
    }
}
