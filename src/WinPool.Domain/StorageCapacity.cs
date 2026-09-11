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
    string Policy,
    string Resiliency = "Simple",
    int? Columns = null,
    int ParityColumns = 0,
    long PhysicalFootprintBytes = 0);

public static class ConservativeCapacity
{
    public const long CapacityAlignmentBytes = 4L * 1024 * 1024 * 1024;

    public static ConservativeCapacityEstimate PlanLogicalUpperBound(
        IReadOnlyList<long> dataMemberBytes,
        string resiliency,
        int dataCopies = 1,
        int? columns = null,
        int parityColumns = 0,
        long interleaveBytes = 65536,
        long knownPhysicalAllocatedBytes = 0)
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

        if (knownPhysicalAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(knownPhysicalAllocatedBytes));
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
            return new(0, 0, 0, 0, dataCopies, interleaveBytes,
                CapacitySourceKind.SimulatedEstimate, PolicyLabel(), resiliency, columns, parityColumns, 0);
        }

        var setting = (resiliency ?? string.Empty).Trim();
        long gross;
        long footprint;
        int effectiveColumns;
        if (setting.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            effectiveColumns = columns ?? count;
            ValidateColumns(effectiveColumns, count, nameof(columns));
            var groups = count / effectiveColumns;
            gross = checked(min * effectiveColumns * groups);
            footprint = gross;
        }
        else if (setting.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            if (dataCopies < 2 || count < dataCopies)
            {
                throw new ArgumentOutOfRangeException(nameof(dataCopies));
            }

            effectiveColumns = columns ?? 1;
            ValidateColumns(effectiveColumns, count / dataCopies, nameof(columns));
            var groups = count / checked(effectiveColumns * dataCopies);
            gross = checked(min * effectiveColumns * groups);
            footprint = checked(gross * dataCopies);
        }
        else if (setting.Equals("Parity", StringComparison.OrdinalIgnoreCase))
        {
            effectiveColumns = columns ?? count;
            if (parityColumns < 1 || effectiveColumns < parityColumns + 2)
            {
                throw new ArgumentOutOfRangeException(nameof(parityColumns));
            }

            ValidateColumns(effectiveColumns, count, nameof(columns));
            var groups = count / effectiveColumns;
            gross = checked(min * (effectiveColumns - parityColumns) * groups);
            footprint = checked(min * effectiveColumns * groups);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(resiliency));
        }

        var availablePhysical = Math.Max(0, footprint - knownPhysicalAllocatedBytes);
        var availableLogical = footprint == 0
            ? 0
            : (long)((decimal)gross * availablePhysical / footprint);
        var aligned = (long)StorageMath.AlignDown(
            (ulong)availableLogical,
            (ulong)CapacityAlignmentBytes);
        return new(
            raw,
            gross,
            0,
            aligned,
            dataCopies,
            interleaveBytes,
            CapacitySourceKind.SimulatedEstimate,
            PolicyLabel(),
            setting,
            effectiveColumns,
            parityColumns,
            footprint);
    }

    public static long PhysicalFootprintForLogical(
        long logicalBytes,
        string resiliency,
        int dataCopies = 1,
        int? columns = null,
        int parityColumns = 0)
    {
        if (logicalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalBytes));
        }

        if (resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase))
        {
            return logicalBytes;
        }

        if (resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            if (dataCopies < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(dataCopies));
            }

            return checked(logicalBytes * dataCopies);
        }

        if (resiliency.Equals("Parity", StringComparison.OrdinalIgnoreCase))
        {
            var totalColumns = columns ?? throw new ArgumentNullException(nameof(columns));
            var dataColumns = totalColumns - parityColumns;
            if (parityColumns < 1 || dataColumns < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(parityColumns));
            }

            return checked((long)Math.Ceiling((decimal)logicalBytes * totalColumns / dataColumns));
        }

        throw new ArgumentOutOfRangeException(nameof(resiliency));
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

    private static void ValidateColumns(int columns, int availableColumns, string parameterName)
    {
        if (columns < 1 || columns > availableColumns)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string PolicyLabel() =>
        "WinPool V0.50 simulated Fixed estimate: exclude non-data members, apply the selected Simple/Mirror/Parity layout, and align the logical maximum down to 4 GiB. Not a Windows guarantee.";
}
