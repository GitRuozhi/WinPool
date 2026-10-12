using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class FormatAllocationUnitPolicyTests
{
    [Theory]
    [InlineData(null, 65_536)]
    [InlineData("10240", 65_536)]
    [InlineData("16298", 65_536)]
    [InlineData("16299", 2_097_152)]
    [InlineData("22631", 2_097_152)]
    public void NtfsRangeFollowsWindowsBuild(string? build, int expectedMaximum)
    {
        var values = FormatAllocationUnitPolicy.GetSupportedClusterSizes("NTFS", build);

        Assert.Equal(512, values[0]);
        Assert.Equal(expectedMaximum, values[^1]);
        Assert.All(values, value => Assert.True(value > 0 && (value & (value - 1)) == 0));
    }

    [Fact]
    public void RefsProvidesBothDocumentedSizesAndKeeps64KiBAsTheDefault()
    {
        Assert.Equal(new[] { 4096, 65536 }, FormatAllocationUnitPolicy.GetSupportedClusterSizes("ReFS", "22631"));
        Assert.True(FormatAllocationUnitPolicy.IsKnownClusterSize("ReFS", 4096));
        Assert.True(FormatAllocationUnitPolicy.IsKnownClusterSize("ReFS", 65536));
        Assert.Equal(65536, FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes("ReFS"));
    }

    [Fact]
    public void ExFatOffersTheDocumentedPowerOfTwoRangeThrough32MiB()
    {
        var values = FormatAllocationUnitPolicy.GetSupportedClusterSizes("exFAT", "22631");

        Assert.Equal(17, values.Count);
        Assert.Equal(512, values[0]);
        Assert.Equal(33_554_432, values[^1]);
        Assert.True(FormatAllocationUnitPolicy.IsKnownClusterSize("exFAT", 8 * 1024 * 1024));
        Assert.False(FormatAllocationUnitPolicy.IsKnownClusterSize("exFAT", 64 * 1024 * 1024));
        Assert.Equal(65536, FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes("exFAT"));
    }

    [Fact]
    public void FileSystemRecommendationsRemainIndependentFromAllowedChoices()
    {
        Assert.Equal(65536, FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes("NTFS"));
        Assert.Contains(4096, FormatAllocationUnitPolicy.GetSupportedClusterSizes("NTFS", "22631"));
        Assert.Equal(4096, FormatAllocationUnitPolicy.GetDefaultAllocationUnitBytes("FAT32"));
    }
}
