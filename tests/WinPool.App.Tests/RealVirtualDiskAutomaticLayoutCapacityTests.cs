using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealVirtualDiskAutomaticLayoutCapacityTests
{
    private const long Mib = 1024L * 1024;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProviderLegalNonMiBSizeIsExactWithoutAutomaticPartitionButRejectedWithIt(
        bool useMaximum, bool createMsr)
    {
        const long bytes = 32 * Mib + 512;
        var range = Range(32 * Mib, bytes);
        Assert.True(range.Supports(bytes));
        Assert.Equal(bytes, RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, useMaximum, bytes, autoCreatePartition: false, createMsr));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, useMaximum, bytes, autoCreatePartition: true, createMsr));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.ConfigureInitializedDisk(
            range.Target.System, Disk(range.Target.System), null, createMsr, bytes));
        Assert.Equal(bytes, range.ResolveMaximum()); // No fallback to the smaller whole-MiB value.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProviderLegalSizeTooSmallForSelectedMsrLayoutIsRejectedBeforeCreation(
        bool useMaximum, bool createMsr)
    {
        var bytes = (createMsr ? 18 : 2) * Mib;
        var range = Range(bytes);
        Assert.True(range.Supports(bytes));
        Assert.Equal(bytes, RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, useMaximum, bytes, autoCreatePartition: false, createMsr));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, useMaximum, bytes, autoCreatePartition: true, createMsr));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.ConfigureInitializedDisk(
            range.Target.System, Disk(range.Target.System), null, createMsr, bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MinimumSupportedAutomaticLayoutMatchesTheLaterPartitionGeometry(
        bool useMaximum, bool createMsr)
    {
        var bytes = (createMsr ? 19 : 3) * Mib;
        var range = Range(bytes);
        var resolved = RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, useMaximum, bytes, autoCreatePartition: true, createMsr);
        Assert.Equal(bytes, resolved);
        var layout = RealOperationProposalFactory.ConfigureInitializedDisk(range.Target.System,
            Disk(range.Target.System), null, createMsr, resolved)!;
        var data = Assert.Single(layout.Steps.Select(step => step.Command).OfType<CreatePartitionCommand>(),
            command => command.Role == RealPartitionRole.BasicData);
        Assert.Equal((createMsr ? 17 : 1) * Mib, data.OffsetBytes);
        Assert.Equal(Mib, data.SizeBytes);
        Assert.Equal(bytes - Mib, data.OffsetBytes + data.SizeBytes);
    }

    private static StorageObjectId Disk(SystemId system) => new(system, StorageObjectKind.OsDisk, "verified-os-disk");

    private static RealVirtualDiskCreationRange Range(params long[] sizes) => new(
        new StorageObjectId(SystemId.New(), StorageObjectKind.StoragePool, "exact-pool"),
        0, 0, 0, 0, sizes, "fresh-provider-fingerprint", DateTimeOffset.UtcNow);
}
