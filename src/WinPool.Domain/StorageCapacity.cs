namespace WinPool.Domain;

public enum CapacitySourceKind
{
    Collected = 0,
    ProviderSupportedRange = 1,
    SimulatedEstimate = 2
}

public enum StorageRuleVerdict
{
    Allow,
    Deny,
    InsufficientInfo
}

public sealed record StorageRuleDecision(
    StorageRuleVerdict Verdict,
    string Code,
    string Message,
    string? ObjectId = null,
    string? Source = null);

public sealed record ConservativeCapacityEstimate(
    long RawDataMemberBytes,
    long LogicalGrossBytes,
    long ReservedBytes,
    long AlignedLogicalBytes,
    int DataCopies,
    long InterleaveBytes,
    CapacitySourceKind Source,
    string Policy);

public static class ConservativeCapacity
{
    /// <summary>
    /// Initial WinPool planning reserve. Not a Windows formula.
    /// Source: docs/Development.md capacity policy (V0.48).
    /// </summary>
    public const int PlanningReservePercent = 1;

    public static ConservativeCapacityEstimate PlanLogicalUpperBound(
        IReadOnlyList<long> dataMemberBytes,
        int dataCopies,
        long interleaveBytes,
        long knownLogicalAllocatedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(dataMemberBytes);
        if (dataCopies < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dataCopies));
        }

        if (interleaveBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interleaveBytes));
        }

        long raw = 0;
        long min = long.MaxValue;
        var count = 0;
        foreach (var size in dataMemberBytes)
        {
            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dataMemberBytes));
            }

            raw = checked(raw + size);
            if (size < min)
            {
                min = size;
            }

            count++;
        }

        if (count == 0)
        {
            return new(0, 0, 0, 0, dataCopies, interleaveBytes, CapacitySourceKind.SimulatedEstimate, PolicyLabel());
        }

        var bySum = raw / dataCopies;
        var byMin = checked(min * (count / dataCopies));
        var gross = Math.Min(bySum, byMin);
        if (knownLogicalAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(knownLogicalAllocatedBytes));
        }

        var available = Math.Max(0, gross - knownLogicalAllocatedBytes);
        var reserved = available * PlanningReservePercent / 100;
        var afterReserve = Math.Max(0, available - reserved);
        var aligned = (long)StorageMath.AlignDown((ulong)afterReserve, (ulong)interleaveBytes);
        return new(
            raw,
            gross,
            reserved,
            aligned,
            dataCopies,
            interleaveBytes,
            CapacitySourceKind.SimulatedEstimate,
            PolicyLabel());
    }

    public static long ApplySupportedRange(
        long plannedBytes,
        long? minBytes,
        long? maxBytes,
        long? divisorBytes)
    {
        var value = plannedBytes;
        if (maxBytes is > 0)
        {
            value = Math.Min(value, maxBytes.Value);
        }

        if (divisorBytes is > 1)
        {
            value = (long)StorageMath.AlignDown((ulong)Math.Max(0, value), (ulong)divisorBytes.Value);
        }

        if (minBytes is > 0 && value < minBytes.Value)
        {
            return 0;
        }

        return value;
    }

    private static string PolicyLabel() =>
        $"WinPool V0.48 conservative Fixed estimate: exclude non-data members, divide by copies, reserve {PlanningReservePercent}%, align down. Not a Windows guarantee.";
}
