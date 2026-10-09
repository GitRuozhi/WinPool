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

    [Fact]
    public void MaximumIntentIsFrozenByAgentAndLayoutWaitsForVerifiedFinalCapacity()
    {
        var range = Range(32 * Mib + 512);
        Assert.Throws<InvalidOperationException>(() => RealOperationProposalFactory.ResolveVirtualDiskCreationSize(
            range, true, null, autoCreatePartition: true, createMsr: true));
        var request = RealOperationProposalFactory.CreateFirstVirtualDisk(range.Target.System, range.Target,
            new("Native MAX", 0, false, true, true, "Data", 'E', UseMaximumSize: true));
        var command = Assert.IsType<CreateVirtualDiskCommand>(Assert.Single(request.Steps).Command);
        Assert.True(command.UseMaximumSize);
        Assert.Equal(0, command.SizeBytes);
        Assert.Contains("WinPool MAX", request.ExpectedFinalState);
        Assert.Empty(request.Steps.Select(step => step.Command).OfType<CreatePartitionCommand>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TieredFactoryPreservesExactTemplateForBothExplicitAndMaximumSearch(bool nativeMaximum)
    {
        var pool = Range(32 * Mib).Target;
        var tier = new StorageObjectId(pool.System, StorageObjectKind.StorageTier, "constraint-template");
        var request = RealOperationProposalFactory.CreateTieredVirtualDisk(pool.System, pool, tier,
            "HDD", nativeMaximum ? 0 : 16L << 30, nativeMaximum);
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(Assert.Single(request.Steps).Command);
        Assert.Equal(TieredVirtualDiskCreationMechanism.ExactTemplate, command.CreationMechanism);
        Assert.Equal(tier, command.Tier.Existing);
        Assert.Equal(nativeMaximum, command.UseMaximumSize);
        if (nativeMaximum)
        {
            Assert.Contains("WinPool MAX", request.ExpectedFinalState);
            Assert.Null(command.MaximumCapacity);
        }
    }

    private static StorageObjectId Disk(SystemId system) => new(system, StorageObjectKind.OsDisk, "verified-os-disk");

    private static RealVirtualDiskCreationRange Range(params long[] sizes) => new(
        new StorageObjectId(SystemId.New(), StorageObjectKind.StoragePool, "exact-pool"),
        0, 0, 0, 0, sizes, "fresh-provider-fingerprint", DateTimeOffset.UtcNow);
}
