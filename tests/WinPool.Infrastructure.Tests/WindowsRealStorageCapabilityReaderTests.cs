using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageCapabilityReaderTests
{
    [Fact]
    public void TierEnumerationDoesNotRequireOptionalRangeOrDivisor()
    {
        var sizes = WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U,
            new ulong[] { 10, 16 }, null, null, null);
        Assert.True(sizes.Supports(10));
        Assert.True(sizes.Supports(16));
        Assert.False(sizes.Supports(12));
        Assert.False(sizes.Supports(20));
        Assert.False(sizes.Supports(0));
    }

    [Fact]
    public void TierRangeUsesMinimumAsIncrementOriginAndPoolStillUsesAbsoluteMultiples()
    {
        var tier = WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U, null, 10UL, 20UL, 6UL);
        Assert.Equal(10, tier.RangeOriginBytes);
        Assert.True(tier.Supports(10));
        Assert.True(tier.Supports(16));
        Assert.False(tier.Supports(12));
        Assert.False(tier.Supports(20));
        Assert.False(tier.Supports(22));
        var pool = new VirtualDiskCreationSize(10, 20, 6, []);
        Assert.Equal(0, pool.RangeOriginBytes);
        Assert.True(pool.Supports(12));
        Assert.False(pool.Supports(10));
    }

    [Fact]
    public void TierBothFormsRequireConsistentEvidenceAndKeepEnumerationAuthoritative()
    {
        var result = WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U,
            new ulong[] { 10, 16 }, 10UL, 22UL, 6UL);
        Assert.True(result.Supports(16));
        Assert.False(result.Supports(22));
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U,
            new ulong[] { 12 }, 10UL, 22UL, 6UL));
    }

    [Fact]
    public void TierMethodFailureOrMissingReturnCannotAdmitEvenAValidList()
    {
        foreach (var code in new object?[] { null, 1U, 4U, 0, "0" })
            Assert.Throws<NotSupportedException>(() => WindowsRealStorageCapabilityReader.ParseTierCreationSize(
                code, new ulong[] { 16 }, null, null, null));
    }

    [Fact]
    public void TierCimEnumerationRequiresExactUInt64ArrayRuntimeType()
    {
        object signed = new long[] { 16 };
        Assert.Equal(typeof(long[]), signed.GetType());
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageCapabilityReader.ParseTierCreationSize(
            0U, signed, null, null, null));
        object unsigned = new ulong[] { 16 };
        Assert.Equal(typeof(ulong[]), unsigned.GetType());
        Assert.True(WindowsRealStorageCapabilityReader.ParseTierCreationSize(
            0U, unsigned, null, null, null).Supports(16));
    }

    public static IEnumerable<object?[]> InvalidSizeEvidence()
    {
        yield return [16UL, null, null, null];
        yield return [new long[] { 16 }, null, null, null];
        yield return [new ulong[] { 0 }, null, null, null];
        yield return [new ulong[] { 16, 16 }, null, null, null];
        yield return [new ulong[] { ulong.MaxValue }, null, null, null];
        yield return [null, null, null, null];
        yield return [Array.Empty<ulong>(), 10UL, 20UL, null];
        yield return [null, null, 20UL, 6UL];
        yield return [null, 10UL, 0UL, 6UL];
        yield return [null, 20UL, 10UL, 6UL];
        yield return [null, 10UL, 20UL, 0UL];
        yield return [null, 10UL, 20UL, 6L];
        yield return [null, 10UL, ulong.MaxValue, 6UL];
    }

    [Theory]
    [MemberData(nameof(InvalidSizeEvidence))]
    public void MalformedOrMissingSizeEvidenceRemainsRejected(object? supported,
        object? min, object? max, object? increment) =>
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageCapabilityReader.ParseTierCreationSize(
            0U, supported, min, max, increment));
}
