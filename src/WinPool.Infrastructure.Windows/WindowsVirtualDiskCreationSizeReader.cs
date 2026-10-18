using System.Text.Json;
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
    private readonly WindowsPowerShellHost host;
    public WindowsVirtualDiskCreationSizeReader(WindowsPowerShellHost? host = null) => this.host = host ?? WindowsPowerShellHost.Shared;
    public async Task<VirtualDiskCreationSize> ReadAsync(WindowsStorageCommandTarget pool, CancellationToken cancellationToken)
    {
        RequireExactConcretePoolTarget(pool);
        var json = await host.QueryAsync(WindowsPowerShellQuery.VirtualDiskSize, new { Target = pool }, cancellationToken).ConfigureAwait(false);
        var range = JsonSerializer.Deserialize<VirtualDiskCreationSize>(json, WindowsPowerShellHost.JsonOptions)
            ?? throw new InvalidDataException("The pool did not return a supported virtual-disk size.");
        if (range.EnumeratedSizes.Count == 0 && (range.MinimumBytes <= 0 || range.MaximumBytes < range.MinimumBytes || range.DivisorBytes <= 0)
            || range.EnumeratedSizes.Any(value => value <= 0 || range.DivisorBytes <= 0 || value % range.DivisorBytes != 0))
            throw new InvalidDataException("The pool returned an incomplete or inconsistent size range.");
        return range;
    }
    public async Task<VirtualDiskCreationSupportProbe> ProbeAsync(WindowsStorageCommandTarget pool, CancellationToken cancellationToken)
    {
        RequireExactConcretePoolTarget(pool);
        var json = await host.QueryAsync(WindowsPowerShellQuery.VirtualDiskProbe, new { Target = pool }, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VirtualDiskCreationSupportProbe>(json, WindowsPowerShellHost.JsonOptions)
            ?? throw new InvalidDataException("The pool did not return a creation support observation.");
    }
    private static void RequireExactConcretePoolTarget(WindowsStorageCommandTarget pool)
    {
        if (pool.Kind != StorageObjectKind.StoragePool
            || string.IsNullOrWhiteSpace(pool.UniqueId)
            || string.IsNullOrWhiteSpace(pool.ObjectId))
            throw new InvalidDataException("An exact concrete pool is required for creation size validation.");
    }

}
