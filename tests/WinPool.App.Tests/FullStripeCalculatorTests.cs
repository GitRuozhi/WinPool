using WinPool.App.Services;

namespace WinPool.App.Tests;

public sealed class FullStripeCalculatorTests
{
    [Theory]
    [InlineData("Simple", 4, 1, 0, 256, 0, StripeExtraKind.None)]
    [InlineData("Mirror", 2, 2, 1, 128, 128, StripeExtraKind.Copies)]
    [InlineData("Mirror", 2, 3, 2, 128, 256, StripeExtraKind.Copies)]
    [InlineData("Parity", 5, 1, 1, 256, 64, StripeExtraKind.Parity)]
    [InlineData("Parity", 6, 1, 2, 256, 128, StripeExtraKind.Parity)]
    public void SeparatesDataFromParityOrExtraCopies(string resiliency, int columns,
        int copies, int redundancy, long dataKiB, long extraKiB, StripeExtraKind kind)
    {
        var actual = FullStripeCalculator.Calculate(resiliency, 65536, columns, copies, redundancy);
        Assert.Equal(new FullStripe(dataKiB * 1024, extraKiB * 1024, kind), actual);
    }

    [Fact]
    public void MissingFactsAndInvalidLayoutsStayBlank()
    {
        Assert.Null(FullStripeCalculator.Calculate("Simple", null, 4, 1, 0));
        Assert.Null(FullStripeCalculator.Calculate("Simple", 65536, null, 1, 0));
        Assert.Null(FullStripeCalculator.Calculate("Mirror", 65536, 2, null, 1));
        Assert.Null(FullStripeCalculator.Calculate("Mirror", 65536, 2, 1, 0));
        Assert.Null(FullStripeCalculator.Calculate("Parity", 65536, 2, 1, null));
        Assert.Null(FullStripeCalculator.Calculate("Parity", 65536, 2, 1, 2));
        Assert.Null(FullStripeCalculator.Calculate("Unknown", 65536, 2, 2, 1));
        Assert.Null(FullStripeCalculator.Calculate("Mirror", long.MaxValue, 2, 2, 1));
    }
}
