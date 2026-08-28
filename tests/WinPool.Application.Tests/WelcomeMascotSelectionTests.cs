using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class WelcomeMascotSelectionTests
{
    [Fact]
    public void MonitoringPreferencesDefaultToDisabledAtFiveHertz()
    {
        var preferences = new UserPreferences();

        Assert.False(preferences.ContinuousMonitoringEnabled);
        Assert.Equal(5, preferences.MonitoringSampleRateHz);
    }

    [Fact]
    public void PrimaryMascotOwnsTheFirstSevenTenths()
    {
        var selector = new WelcomeMascotSelector(new SequenceRandomSource(0));

        Assert.Equal("00", selector.SelectAssetKey());
    }

    [Theory]
    [InlineData(0, "01")]
    [InlineData(6, "07")]
    public void SecondaryMascotsAreSelectedFromTheRemainingSevenAssets(
        int secondaryValue,
        string expected)
    {
        var selector = new WelcomeMascotSelector(
            new SequenceRandomSource(7, secondaryValue));

        Assert.Equal(expected, selector.SelectAssetKey());
    }

    [Fact]
    public void MascotTitleComesFromTheSourceFileNameAfterTheKeyPrefix()
    {
        var asset = WelcomeMascotCatalog.FromFileName("03-苦酒入喉.png");

        Assert.Equal("03", asset.Key);
        Assert.Equal("苦酒入喉", asset.Title);
        Assert.Equal("苦酒入喉", WelcomeMascotCatalog.ByKey("03").Title);
        Assert.Equal("04", WelcomeMascotCatalog.NextKey("03"));
        Assert.Equal("00", WelcomeMascotCatalog.NextKey("07"));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("03")]
    [InlineData("07")]
    public void RandomKeyNeverReturnsTheCurrentAsset(string currentKey)
    {
        for (var i = 0; i < 50; i++)
        {
            var next = WelcomeMascotCatalog.RandomKey(currentKey);

            Assert.NotEqual(currentKey, next);
            Assert.Contains(
                WelcomeMascotCatalog.Assets,
                asset => asset.Key == next);
        }
    }

    private sealed class SequenceRandomSource(params int[] values) : IWelcomeRandomSource
    {
        private readonly Queue<int> values = new(values);

        public int Next(int exclusiveMaximum)
        {
            var value = values.Dequeue();
            Assert.InRange(value, 0, exclusiveMaximum - 1);
            return value;
        }
    }
}
