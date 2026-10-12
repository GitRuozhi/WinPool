using WinPool.App.Services;

namespace WinPool.App.Tests;

public sealed class IntegerInputPolicyTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("0123456789", true)]
    [InlineData("1.5", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    [InlineData("１", false)]
    [InlineData("1 GiB", false)]
    public void RejectsNonDigitEntry(string text, bool allowed) =>
        Assert.Equal(allowed, IntegerInputPolicy.ContainsOnlyDigits(text));

    [Theory]
    [InlineData("256", "100")]
    [InlineData("999999999999999999999999999999999999", "100")]
    [InlineData("0", "1")]
    [InlineData("42", "42")]
    [InlineData("", "")]
    public void CapacityIsClampedImmediately(string text, string expected) =>
        Assert.Equal(expected, IntegerInputPolicy.Clamp(text, 1, 100));

    [Fact]
    public void EmptyEditCommitsToCapacityMaximumButValidZeroCountIsPreserved()
    {
        Assert.Equal("100", IntegerInputPolicy.Clamp("", 1, 100, true, true));
        Assert.Equal("1", IntegerInputPolicy.Clamp("", 1, 100, true));
        Assert.Equal("1", IntegerInputPolicy.Clamp("", 1, null, true, true));
        Assert.Equal("0", IntegerInputPolicy.Clamp("0", 0, 8));
        Assert.Equal("5", IntegerInputPolicy.Clamp("9", 0, 5));
    }
}
