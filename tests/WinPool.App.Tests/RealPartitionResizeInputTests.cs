using WinPool.App.Services;

namespace WinPool.App.Tests;

public sealed class RealPartitionResizeInputTests
{
    private const long Mib = 1024L * 1024;

    [Theory]
    [InlineData(true, true, 1, 1050, 50)]
    [InlineData(true, true, 5000, 2000, 1000)]
    [InlineData(true, false, 1, 1050, 50)]
    [InlineData(true, false, 5000, 2000, 1000)]
    [InlineData(false, true, 1, 950, 50)]
    [InlineData(false, true, 5000, 250, 750)]
    [InlineData(false, false, 1, 250, 750)]
    [InlineData(false, false, 5000, 950, 50)]
    public void InputsClampToTheExactDirectionRange(
        bool extend, bool deltaInput, long input, long expectedTarget, long expectedDelta)
    {
        Assert.True(RealPartitionResizeUiRange.TryClampResizeFormula(
            1000 * Mib, input, deltaInput, extend,
            extend ? 1050 : 250, extend ? 2000 : 950,
            out var targetBytes, out var deltaBytes));
        Assert.Equal(expectedTarget * Mib, targetBytes);
        Assert.Equal(expectedDelta * Mib, deltaBytes);
    }

    [Theory]
    [InlineData(true, true, 2000)]
    [InlineData(true, false, 2000)]
    [InlineData(false, true, 250)]
    public void ExtremelyLargeInputClampsWithoutOverflow(bool extend, bool deltaInput, long expectedTarget)
    {
        Assert.True(RealPartitionResizeUiRange.TryClampResizeFormula(
            1000 * Mib, long.MaxValue, deltaInput, extend,
            extend ? 1050 : 250, extend ? 2000 : 950,
            out var targetBytes, out _));
        Assert.Equal(expectedTarget * Mib, targetBytes);
    }

    [Fact]
    public void FractionalCurrentKeepsExactBytesAndWrongDirectionBoundsRemainRejected()
    {
        const long currentBytes = 1000 * Mib + 1;
        Assert.True(RealPartitionResizeUiRange.TryClampResizeFormula(
            currentBytes, 50, true, true, 1050, 2000, out var targetBytes, out var deltaBytes));
        Assert.Equal(1050 * Mib, targetBytes);
        Assert.Equal(50 * Mib - 1, deltaBytes);
        Assert.Equal(currentBytes, targetBytes - deltaBytes);
        Assert.False(RealPartitionResizeUiRange.TryClampResizeFormula(
            1000 * Mib, 50, true, true, 950, 2000, out _, out _));
        Assert.False(RealPartitionResizeUiRange.TryClampResizeFormula(
            1000 * Mib, 50, true, false, 250, 1050, out _, out _));
    }
}
