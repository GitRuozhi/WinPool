namespace WinPool.Domain;

public enum BinaryCapacityUnit : ulong
{
    Bytes = 1,
    KiB = 1UL << 10,
    MiB = 1UL << 20,
    GiB = 1UL << 30,
    TiB = 1UL << 40
}

public static class StorageMath
{
    public static readonly AlgorithmIdentity CapacityAlgorithm =
        new("ALG-MATH-001", "1.0.0", AlgorithmConfidence.Proven, "Plan/07 §2");

    public static readonly AlgorithmIdentity AlignmentAlgorithm =
        new("ALG-MATH-002", "1.0.0", AlgorithmConfidence.Proven, "Plan/07 §2");

    public static readonly AlgorithmIdentity PercentageAlgorithm =
        new("ALG-MATH-003", "1.0.0", AlgorithmConfidence.Proven, "Plan/07 §2");

    public static ulong ToBytes(ulong value, BinaryCapacityUnit unit) =>
        checked(value * (ulong)unit);

    public static ulong AlignDown(ulong value, ulong alignment)
    {
        EnsureAlignment(alignment);
        return value - (value % alignment);
    }

    public static ulong AlignUp(ulong value, ulong alignment)
    {
        EnsureAlignment(alignment);
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    public static double? Percentage(ulong numerator, ulong denominator) =>
        denominator == 0 ? null : numerator / (double)denominator * 100d;

    public static double ClampActivityPercentage(double rawPercentage)
    {
        if (double.IsNaN(rawPercentage))
        {
            return 0;
        }

        return Math.Clamp(rawPercentage, 0d, 100d);
    }

    private static void EnsureAlignment(ulong alignment)
    {
        if (alignment == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be greater than zero.");
        }
    }
}

public sealed record TheoreticalPoolCapacityEstimate(
    ulong StripeCount,
    ulong RawStripeBytes,
    ulong EstimatedLogicalBytes,
    AlgorithmIdentity Algorithm,
    string Limitation)
{
    public const string UnverifiedLabel = "【推测，待验证】";
}

public static class TheoreticalPoolCapacity
{
    // 【推测，待验证】ALG-CAP-002：仅用于计划估算，不能成为真实执行参数。
    public static readonly AlgorithmIdentity Algorithm =
        new("ALG-CAP-002", "1.0.0", AlgorithmConfidence.Speculative, "Plan/07 §5");

    public static TheoreticalPoolCapacityEstimate Estimate(
        ulong minimumSelectedDiskBytes,
        ulong interleaveBytes,
        uint columns,
        uint dataColumns)
    {
        if (interleaveBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interleaveBytes));
        }

        if (columns == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (dataColumns == 0 || dataColumns > columns)
        {
            throw new ArgumentOutOfRangeException(nameof(dataColumns));
        }

        var stripeCount = minimumSelectedDiskBytes / interleaveBytes;
        var rawStripeBytes = checked(checked(stripeCount * interleaveBytes) * columns);
        var logicalBytes = checked(checked(stripeCount * interleaveBytes) * dataColumns);

        return new(
            stripeCount,
            rawStripeBytes,
            logicalBytes,
            Algorithm,
            $"{TheoreticalPoolCapacityEstimate.UnverifiedLabel} Does not model slabs, metadata, alignment, fault domains, heterogeneous disks, or Windows allocation policy.");
    }
}
