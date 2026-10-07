using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class RealPartitionCreateGeometryTests
{
    private const long MiB = 1L << 20;

    [Fact]
    public void ObservedWdcFinalGapMaximumFitsTheRealGptTailReserve()
    {
        const long diskSize = 4_000_787_030_016;
        const long offset = 17 * MiB;
        var real = EditWorkspace.GetRealPartitionCreateGeometry(diskSize, offset, diskSize - offset);
        var simulated = EditWorkspace.GetPartitionCreateGeometry(offset, diskSize - offset);

        Assert.True(real.CanCreate);
        Assert.Equal(offset, real.StartOffsetBytes);
        Assert.Equal(3_815_429 * MiB, real.MaximumSizeBytes);
        Assert.Equal(real.MaximumSizeBytes, real.DefaultSizeBytes);
        Assert.Equal(4_000_785_104_896, real.MaximumEndOffsetExclusiveBytes);
        Assert.Equal(diskSize - MiB, real.GapEndOffsetExclusiveBytes);
        Assert.Equal(3_815_430 * MiB, simulated.MaximumSizeBytes);
        Assert.Equal(172_032, simulated.MaximumEndOffsetExclusiveBytes!.Value - (diskSize - MiB));
    }

    [Fact]
    public void RealMiddleGapRetainsItsPartitionBoundary()
    {
        var real = EditWorkspace.GetRealPartitionCreateGeometry(100 * MiB, 5 * MiB + MiB / 2, 3 * MiB);
        var original = EditWorkspace.GetPartitionCreateGeometry(5 * MiB + MiB / 2, 3 * MiB);
        Assert.Equal(original, real);
    }

    [Fact]
    public void RealEmptyDiskMaximumReservesBothEnds()
    {
        var real = EditWorkspace.GetRealPartitionCreateGeometry(32L << 30, 0, 32L << 30);
        Assert.True(real.CanCreate);
        Assert.Equal(MiB, real.StartOffsetBytes);
        Assert.Equal((32L << 30) - 2 * MiB, real.MaximumSizeBytes);
    }

    [Theory]
    [InlineData(1048576, 0, 1048576)]
    [InlineData(104857600, 103809024, 1048576)]
    [InlineData(104857600, 104857600, 1048576)]
    [InlineData(104857600, 1048576, 104857600)]
    [InlineData(long.MaxValue, long.MaxValue - 1, 2)]
    public void UnknownOrUnusableDiskGapNeverProducesRealCapacity(long disk, long offset, long size)
    {
        var geometry = EditWorkspace.GetRealPartitionCreateGeometry(disk, offset, size);
        Assert.False(geometry.CanCreate);
        Assert.Null(geometry.MaximumSizeBytes);
    }
}
