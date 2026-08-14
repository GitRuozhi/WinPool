using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class PartitionableDiskPolicyTests
{
    [Theory]
    [InlineData("RAW")]
    [InlineData("GPT")]
    [InlineData("MBR")]
    public void EligibleStylesIncludeSupportedLocalDisks(string partitionStyle)
    {
        Assert.True(PartitionableDiskPolicy.IsEligible(Disk(partitionStyle)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("Network")]
    public void UnsupportedStylesAreNotEligible(string partitionStyle)
    {
        Assert.False(PartitionableDiskPolicy.IsEligible(Disk(partitionStyle)));
    }

    [Fact]
    public void EmptyCapacityDiskIsNotEligible()
    {
        Assert.False(PartitionableDiskPolicy.IsEligible(Disk("GPT") with { Size = 0 }));
    }

    [Fact]
    public void BootAndSystemDisksRemainVisibleForProtectedPresentation()
    {
        Assert.True(PartitionableDiskPolicy.IsEligible(Disk("GPT") with { IsBoot = true, IsSystem = true }));
    }

    private static OsDiskInfo Disk(string partitionStyle) =>
        new("disk:test", "Test disk", 1, partitionStyle, 1024, false, false, false, null, null);
}
