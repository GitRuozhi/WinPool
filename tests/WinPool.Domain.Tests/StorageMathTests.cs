using WinPool.Domain;

namespace WinPool.Domain.Tests;

public sealed class StorageMathTests
{
    [Fact]
    public void BinaryCapacityUsesChecked1024BasedUnits()
    {
        Assert.Equal(1_073_741_824UL, StorageMath.ToBytes(1, BinaryCapacityUnit.GiB));
        Assert.Throws<OverflowException>(() => StorageMath.ToBytes(ulong.MaxValue, BinaryCapacityUnit.KiB));
    }

    [Theory]
    [InlineData(0UL, 64UL, 0UL, 0UL)]
    [InlineData(1UL, 64UL, 0UL, 64UL)]
    [InlineData(64UL, 64UL, 64UL, 64UL)]
    [InlineData(65UL, 64UL, 64UL, 128UL)]
    public void AlignmentMatchesRegisteredAlgorithm(
        ulong value,
        ulong alignment,
        ulong expectedDown,
        ulong expectedUp)
    {
        Assert.Equal(expectedDown, StorageMath.AlignDown(value, alignment));
        Assert.Equal(expectedUp, StorageMath.AlignUp(value, alignment));
    }

    [Fact]
    public void AlignmentRejectsZeroAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageMath.AlignDown(1, 0));
        Assert.Throws<OverflowException>(() => StorageMath.AlignUp(ulong.MaxValue, 2));
    }

    [Fact]
    public void PercentagePreservesUnavailableAndRawValue()
    {
        Assert.Null(StorageMath.Percentage(1, 0));
        Assert.Equal(125d, StorageMath.Percentage(5, 4));
        Assert.Equal(100d, StorageMath.ClampActivityPercentage(125d));
    }

    [Fact]
    public void TheoreticalCapacityIsExplicitlySpeculative()
    {
        var estimate = TheoreticalPoolCapacity.Estimate(
            minimumSelectedDiskBytes: 1_048_576,
            interleaveBytes: 65_536,
            columns: 4,
            dataColumns: 2);

        Assert.Equal(16UL, estimate.StripeCount);
        Assert.Equal(4_194_304UL, estimate.RawStripeBytes);
        Assert.Equal(2_097_152UL, estimate.EstimatedLogicalBytes);
        Assert.Equal(AlgorithmConfidence.Speculative, estimate.Algorithm.Confidence);
        Assert.Contains("【推测，待验证】", estimate.Limitation);
    }

    [Fact]
    public void MachineBindingIsDeterministicAndDoesNotContainInput()
    {
        var first = MachineBinding.Create(["SERIAL-SECRET", "host"]);
        var second = MachineBinding.Create([" HOST ", "serial-secret"]);

        Assert.Equal(first, second);
        Assert.DoesNotContain("SERIAL", first, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, first.Length);
    }
}
