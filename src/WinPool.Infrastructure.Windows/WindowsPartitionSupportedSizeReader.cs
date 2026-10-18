using System.Text.Json;


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
    private readonly WindowsPowerShellHost host;
    public WindowsPartitionSupportedSizeReader(WindowsPowerShellHost? host = null) => this.host = host ?? WindowsPowerShellHost.Shared;
    public async Task<PartitionSupportedSize> ReadAsync(WindowsStorageCommandTarget target, CancellationToken cancellationToken)
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

        var json = await host.QueryAsync(WindowsPowerShellQuery.PartitionSize, new { Target = target }, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<PartitionSupportedSize>(json, WindowsPowerShellHost.JsonOptions)
            ?? throw new InvalidDataException("The partition provider returned no range.");
        if (result.MinimumBytes <= 0 || result.MaximumBytes < result.MinimumBytes)
            throw new InvalidDataException("The provider returned an invalid partition size range.");
        return result;
    }
}