using System.Globalization;
using System.Management;

namespace WinPool.Infrastructure.Windows;

public sealed record PartitionSupportedSize(long MinimumBytes, long MaximumBytes);

public interface IPartitionSupportedSizeReader
{
    Task<PartitionSupportedSize> ReadAsync(
        WindowsStorageCommandTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Invokes only the read-only MSFT_Partition.GetSupportedSize method on the
/// exact GUID and geometry observed in the fresh topology.
/// </summary>
public sealed class WindowsPartitionSupportedSizeReader : IPartitionSupportedSizeReader
{
    public Task<PartitionSupportedSize> ReadAsync(
        WindowsStorageCommandTarget target,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(target), cancellationToken);

    private static PartitionSupportedSize Read(WindowsStorageCommandTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != WinPool.Domain.StorageObjectKind.Partition
            || target.DiskNumber is not { } diskNumber || diskNumber < 0
            || target.PartitionNumber is not { } partitionNumber || partitionNumber <= 0
            || !Guid.TryParse(target.PartitionGuid, out var guid) || guid == Guid.Empty
            || target.OffsetBytes is not { } offset || offset < 0
            || target.SizeBytes is not { } currentSize || currentSize <= 0)
        {
            throw new InvalidDataException("An exact partition target is required for supported-size lookup.");
        }

        var query = string.Format(CultureInfo.InvariantCulture,
            "SELECT * FROM MSFT_Partition WHERE DiskNumber = {0} AND PartitionNumber = {1}",
            diskNumber, partitionNumber);
        using var searcher = new ManagementObjectSearcher(
            @"\\.\ROOT\Microsoft\Windows\Storage", query);
        searcher.Options.Timeout = TimeSpan.FromSeconds(30);
        using var results = searcher.Get();
        var matches = results.Cast<ManagementObject>()
            .Where(item => Guid.TryParse(item["Guid"]?.ToString(), out var observed)
                && observed == guid
                && Convert.ToInt64(item["Offset"], CultureInfo.InvariantCulture) == offset
                && Convert.ToInt64(item["Size"], CultureInfo.InvariantCulture) == currentSize)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The partition changed or has an ambiguous provider identity.");
        }

        using var partition = matches[0];
        using var output = partition.InvokeMethod("GetSupportedSize", null, null);
        if (output is null
            || Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("The storage provider did not return a supported partition range.");
        }
        var minimum = Convert.ToUInt64(output["SizeMin"], CultureInfo.InvariantCulture);
        var maximum = Convert.ToUInt64(output["SizeMax"], CultureInfo.InvariantCulture);
        if (minimum > long.MaxValue || maximum > long.MaxValue
            || minimum == 0 || maximum < minimum)
        {
            throw new InvalidDataException("The provider returned an invalid partition size range.");
        }
        return new((long)minimum, (long)maximum);
    }
}
