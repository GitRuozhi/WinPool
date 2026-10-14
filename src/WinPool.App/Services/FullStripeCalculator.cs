namespace WinPool.App.Services;

public enum StripeExtraKind { None, Parity, Copies }
public sealed record FullStripe(long DataBytes, long ExtraBytes, StripeExtraKind ExtraKind);

/// <summary>Displays a complete logical stripe and its extra physical writes.
/// Microsoft defines Interleave as the strip on one disk and NumberOfColumns
/// as the stripe width; mirror copies are additional complete copies of that data.
/// https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-virtualdisk
/// Parity columns share that physical width with data columns.
/// </summary>
public static class FullStripeCalculator
{
    public static FullStripe? Calculate(string? resiliency, long? stripeUnitBytes,
        int? columns, int? copies, int? redundancy)
    {
        if (stripeUnitBytes is not > 0 || columns is not > 0) return null;
        try
        {
            var width = checked(stripeUnitBytes.Value * columns.Value);
            if (string.Equals(resiliency, "Simple", StringComparison.OrdinalIgnoreCase))
                return new(width, 0, StripeExtraKind.None);
            if (string.Equals(resiliency, "Mirror", StringComparison.OrdinalIgnoreCase))
                return copies is >= 2 ? new(width, checked(width * (copies.Value - 1)), StripeExtraKind.Copies) : null;
            if (string.Equals(resiliency, "Parity", StringComparison.OrdinalIgnoreCase))
                return redundancy is > 0 && redundancy.Value < columns.Value
                    ? new(checked(stripeUnitBytes.Value * (columns.Value - redundancy.Value)),
                        checked(stripeUnitBytes.Value * redundancy.Value), StripeExtraKind.Parity) : null;
            return null;
        }
        catch (OverflowException) { return null; }
    }
}
