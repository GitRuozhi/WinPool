using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class VirtualDiskCreationSizeIntersectionTests
{
    [Fact]
    public void NativeHddMaximumIsConstrainedByBothExactProviderQueries()
    {
        var pool = new VirtualDiskCreationSize(1073741824, 3999688294400, 1073741824, []);
        var tier = new VirtualDiskCreationSize(268435456, 3999956729856, 268435456, [])
            { RangeOriginBytes = 268435456 };
        var common = VirtualDiskCreationSize.Intersect(pool, tier);
        Assert.Equal(3999688294400, common.MaximumBytes);
        Assert.True(common.Supports(32L << 30));
        Assert.True(pool.Supports(common.MaximumBytes));
        Assert.True(tier.Supports(common.MaximumBytes));
        Assert.True(tier.Supports(3999956729856));
        Assert.False(common.Supports(3999956729856));
        Assert.False(common.Supports(common.MaximumBytes - 268435456));
    }

    [Fact]
    public void OffsetGridsUseTheirCommonCongruenceRatherThanMinimumOfEndpoints()
    {
        var common = VirtualDiskCreationSize.Intersect(
            new(6, 40, 6, []), new(4, 40, 8, []) { RangeOriginBytes = 4 });
        Assert.Equal(12, common.MinimumBytes);
        Assert.Equal(36, common.MaximumBytes);
        Assert.Equal(24, common.DivisorBytes);
        Assert.True(common.Supports(12)); Assert.True(common.Supports(36));
        Assert.False(common.Supports(24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteEnumerationRemainsAuthoritative(bool reverse)
    {
        var enumeration = new VirtualDiskCreationSize(0, 0, 0, [10, 16, 22, 28]);
        var range = new VirtualDiskCreationSize(12, 26, 4, []);
        var common = reverse ? VirtualDiskCreationSize.Intersect(range, enumeration)
            : VirtualDiskCreationSize.Intersect(enumeration, range);
        Assert.Equal(new long[] { 16 }, common.EnumeratedSizes);
        Assert.False(common.Supports(20));
    }

    [Fact]
    public void TwoEnumerationsCannotAdmitUnlistedValues()
    {
        var common = VirtualDiskCreationSize.Intersect(new(0, 0, 0, [10, 20, 30]), new(0, 0, 0, [20, 30, 40]));
        Assert.Equal(new long[] { 20, 30 }, common.EnumeratedSizes);
        Assert.False(common.Supports(25));
    }

    [Fact]
    public void OverflowingPeriodStillRetainsOneExactCommonValue()
    {
        var common = VirtualDiskCreationSize.Intersect(
            new(1, long.MaxValue, long.MaxValue - 1, []) { RangeOriginBytes = 1 },
            new(1, long.MaxValue, long.MaxValue - 2, []) { RangeOriginBytes = 1 });
        Assert.Equal(new long[] { 1 }, common.EnumeratedSizes);
        Assert.False(common.Supports(long.MaxValue));
    }

    [Theory]
    [InlineData(1, 10, 2, 2, 10, 2)]
    [InlineData(1, 3, 1, 4, 10, 1)]
    public void IncompatibleOrDisjointGridsFailClosed(long minA, long maxA, long stepA,
        long minB, long maxB, long stepB) => Assert.Throws<InvalidDataException>(() =>
            VirtualDiskCreationSize.Intersect(new(minA, maxA, stepA, []) { RangeOriginBytes = minA },
                new(minB, maxB, stepB, []) { RangeOriginBytes = minB }));

    [Fact]
    public void DisjointEnumerationsFailClosed() => Assert.Throws<InvalidDataException>(() =>
        VirtualDiskCreationSize.Intersect(new(0, 0, 0, [1, 3]), new(0, 0, 0, [2, 4])));
}
