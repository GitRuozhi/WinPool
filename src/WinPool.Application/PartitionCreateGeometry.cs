namespace WinPool.Application;

/// <summary>
/// A 1 MiB-aligned placement calculated inside an unallocated byte range.
/// End offsets are exclusive.
/// </summary>
public sealed record PartitionCreateGeometry(
    long GapOffsetBytes,
    long? GapEndOffsetExclusiveBytes,
    long? StartOffsetBytes,
    long? MaximumSizeBytes,
    long? DefaultSizeBytes,
    long? MaximumEndOffsetExclusiveBytes,
    bool CanCreate,
    string? UnavailableReason)
{
    internal static PartitionCreateGeometry Unavailable(
        long gapOffsetBytes,
        long? gapEndOffsetExclusiveBytes,
        string reason) =>
        new(
            gapOffsetBytes,
            gapEndOffsetExclusiveBytes,
            null,
            null,
            null,
            null,
            false,
            reason);
}
