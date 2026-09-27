using System.Management;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public sealed record VirtualDiskCreationSize(
    long MinimumBytes,
    long MaximumBytes,
    long DivisorBytes,
    IReadOnlyList<long> EnumeratedSizes)
{
    public bool Supports(long bytes) => bytes > 0 &&
        (EnumeratedSizes.Count > 0
            ? EnumeratedSizes.Contains(bytes)
            : bytes >= MinimumBytes && bytes <= MaximumBytes
              && DivisorBytes > 0 && bytes % DivisorBytes == 0);
}

public interface IVirtualDiskCreationSizeReader
{
    Task<VirtualDiskCreationSize> ReadAsync(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken);
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

    private static VirtualDiskCreationSize Read(
        WindowsStorageCommandTarget pool,
        CancellationToken cancellationToken)
    {
        if (pool.Kind != StorageObjectKind.StoragePool
            || string.IsNullOrWhiteSpace(pool.UniqueId)
            || string.IsNullOrWhiteSpace(pool.ObjectId))
            throw new InvalidDataException("An exact concrete pool is required for creation size validation.");
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT UniqueId,ObjectId,IsPrimordial,IsReadOnly,IsClustered FROM MSFT_StoragePool");
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var candidates = results.Cast<ManagementObject>().ToArray();
        try
        {
            var matches = candidates.Where(item =>
                StringComparer.Ordinal.Equals(item["UniqueId"]?.ToString(), pool.UniqueId)
                && StringComparer.Ordinal.Equals(item["ObjectId"]?.ToString(), pool.ObjectId)
                && item["IsPrimordial"] is false).ToArray();
            if (matches.Length != 1 || matches[0]["IsReadOnly"] is not false
                || matches[0]["IsClustered"] is not false)
                throw new InvalidDataException("The concrete pool is absent, ambiguous, clustered, or read-only.");
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
}
