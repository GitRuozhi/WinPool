using WinPool.Application;

namespace WinPool.App.Services;

internal readonly record struct RealVirtualDiskCreationCapacity(long SizeBytes, bool UseMaximumSize);

/// <summary>Preserves MAX intent for Agent-side freezing and search; explicit bytes are validated here.</summary>
internal static class RealVirtualDiskCreationCapacityResolver
{
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
