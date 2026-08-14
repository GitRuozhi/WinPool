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
