using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class RealVirtualDiskCreationRangeTests
{
    [Fact]
    public void EnumeratedSizesAreExactAndMaximumDoesNotInventAnIntermediateValue()
    {
        var range = Range(1, 1000, 1, 0, [17, 41, 83]);
        Assert.True(range.Supports(17));
        Assert.True(range.Supports(41));
        Assert.True(range.Supports(83));
        Assert.False(range.Supports(42));
        Assert.False(range.Supports(1000));
        Assert.Equal(83, range.ResolveMaximum());
    }

    [Fact]
    public void EnumeratedSizesDoNotNeedAContinuousRangeOrRelyOnEnumerationOrder()
    {
        var range = Range(0, 0, 0, -1, [83, 17, 41, 17]);
        Assert.True(range.Supports(41));
        Assert.Equal(83, range.ResolveMaximum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidEnumeratedSizeCannotFallBackToAnOtherwiseValidContinuousRange(long invalid)
    {
        var range = Range(1, 100, 1, 0, [invalid, 100]);
        Assert.False(range.Supports(100));
        Assert.False(range.Supports(invalid));
        Assert.Throws<InvalidDataException>(() => range.ResolveMaximum());
    }

    [Theory]
    [InlineData(10, 100, 0, 0)]
    [InlineData(10, 100, -1, 0)]
    [InlineData(10, 100, 10, -1)]
    [InlineData(0, 100, 10, 0)]
    [InlineData(-1, 100, 10, 0)]
    [InlineData(101, 100, 10, 0)]
    [InlineData(10, 100, 10, 101)]
    [InlineData(91, 99, 10, 0)]
    public void IncompleteOrEmptyContinuousRangesNeverAuthorizeAValueOrMaximum(
        long minimum, long maximum, long divisor, long origin)
    {
        var range = Range(minimum, maximum, divisor, origin);
        Assert.False(range.Supports(maximum));
        Assert.Throws<InvalidDataException>(() => range.ResolveMaximum());
    }

    [Fact]
    public void ContinuousRangeUsesProviderOriginRatherThanMinimumForCongruence()
    {
        var range = Range(20, 100, 16, 3);
        Assert.False(range.Supports(19));
        Assert.False(range.Supports(20));
        Assert.True(range.Supports(35));
        Assert.True(range.Supports(99));
        Assert.False(range.Supports(100));
        Assert.Equal(99, range.ResolveMaximum());
    }

    [Fact]
    public void ExactSingleValueAndPositiveOriginBoundariesAreSupported()
    {
        var range = Range(3, 3, 16, 3);
        Assert.True(range.Supports(3));
        Assert.False(range.Supports(2));
        Assert.False(range.Supports(4));
        Assert.Equal(3, range.ResolveMaximum());
        Assert.False(Range(1, 100, 16, 3).Supports(0));
        Assert.False(Range(1, 100, 16, 3).Supports(1));
    }

    [Fact]
    public void MaximumNearInt64LimitAvoidsOverflowAndStillUsesExactOrigin()
    {
        var range = Range(1, long.MaxValue, 16, 3);
        var maximum = range.ResolveMaximum();
        Assert.Equal(long.MaxValue - 12, maximum);
        Assert.True(range.Supports(maximum));
        Assert.False(range.Supports(long.MaxValue));
    }

    private static RealVirtualDiskCreationRange Range(long minimum, long maximum, long divisor, long origin,
        IReadOnlyList<long>? enumerated = null) => new(
        new StorageObjectId(new SystemId(Guid.Parse("f45fbb67-bdad-47e0-918c-8e623ee2ddbc")), StorageObjectKind.StoragePool, "pool:exact"),
        minimum, maximum, divisor, origin, enumerated ?? [], "fingerprint:exact", DateTimeOffset.UtcNow);
}
