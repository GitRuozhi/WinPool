using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationClearDiskTests
{
    [Fact]
    public void ClearRemovesOnlySelectedDiskPartitionsAndTheirVolumesThenSetsRaw()
    {
        var source = TestSnapshotFactory.Create();
        var otherDisk = new OsDiskInfo(
            "osdisk:other", "Other disk", 4, "GPT", 2_000_000,
            false, false, false, null, null);
        var otherPartition = source.Partitions.Single() with
        {
            StableId = "partition:other",
            OsDiskStableId = "osdisk:other",
            PartitionNumber = 1,
            DriveLetter = "D"
        };
        var otherVolume = source.Volumes.Single() with
        {
            StableId = "volume:other",
            PartitionStableId = otherPartition.StableId,
            AccessPaths = ["D:\\"]
        };
        var snapshot = source with
        {
            OsDisks = [source.OsDisks.Single() with { PartitionStyle = "MBR" }, otherDisk],
            Partitions = [source.Partitions.Single(), otherPartition],
            Volumes = [source.Volumes.Single(), otherVolume]
        };
        var document = CreateDocument(snapshot);

        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(SimulationEditKind.ClearDisk, "osdisk:3"));

        Assert.True(result.Succeeded, result.Error);
        var cleared = result.Document.Snapshot;
        Assert.Equal("RAW", cleared.OsDisks.Single(item => item.StableId == "osdisk:3").PartitionStyle);
        Assert.Equal("GPT", cleared.OsDisks.Single(item => item.StableId == "osdisk:other").PartitionStyle);
        Assert.DoesNotContain(cleared.Partitions, item => item.OsDiskStableId == "osdisk:3");
        Assert.Contains(cleared.Partitions, item => item.StableId == otherPartition.StableId);
        Assert.DoesNotContain(cleared.Volumes, item => item.PartitionStableId == "partition:1");
        Assert.Contains(cleared.Volumes, item => item.StableId == otherVolume.StableId);
        Assert.DoesNotContain(result.Commands, command => command.Contains("Clear-Disk", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Commands, command => command.Contains("Simulated only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KnownGptWithNoPartitionsCanBeClearedToRaw()
    {
        var source = TestSnapshotFactory.Create();
        var snapshot = source with
        {
            OsDisks = [source.OsDisks.Single() with { PartitionStyle = "GPT" }],
            Partitions = [],
            Volumes = []
        };
        var document = CreateDocument(snapshot);

        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(
            snapshot,
            new SimulationEditRequest(SimulationEditKind.ClearDisk, "osdisk:3")).Verdict);
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(SimulationEditKind.ClearDisk, "osdisk:3"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("RAW", Assert.Single(result.Document.Snapshot.OsDisks).PartitionStyle);
        Assert.Empty(result.Document.Snapshot.Partitions);
        Assert.Empty(result.Document.Snapshot.Volumes);
    }

    [Theory]
    [InlineData("RAW", false, false, false, false, false)]
    [InlineData("GPT", true, false, false, false, false)]
    [InlineData("GPT", false, true, false, false, false)]
    [InlineData("GPT", false, false, true, false, false)]
    [InlineData("GPT", false, false, false, true, false)]
    [InlineData("GPT", false, false, false, false, true)]
    [InlineData("unknown", false, false, false, false, false)]
    public void UnsafeOrUnclearDiskStateIsRejected(
        string style,
        bool offline,
        bool boot,
        bool system,
        bool pageFile,
        bool crashDump)
    {
        var source = TestSnapshotFactory.Create();
        var disk = source.OsDisks.Single() with
        {
            PartitionStyle = style,
            IsOffline = offline,
            IsBoot = boot,
            IsSystem = system,
            PhysicalDiskStableId = "physical:1"
        };
        var physical = source.PhysicalDisks.Single() with
        {
            IsPageFile = pageFile,
            IsCrashDump = crashDump
        };
        var snapshot = source with { OsDisks = [disk], PhysicalDisks = [physical] };
        var decision = StorageEditRules.Evaluate(
            snapshot,
            new SimulationEditRequest(SimulationEditKind.ClearDisk, disk.StableId));

        Assert.Equal(StorageRuleVerdict.Deny, decision.Verdict);
    }

    private static StorageSystemDocument CreateDocument(StorageSnapshot snapshot) => new(
        StorageSystemDocument.CurrentSchemaVersion,
        "simulation:clear-disk",
        StorageSystemKind.Simulation,
        "Clear disk simulation",
        snapshot,
        [],
        DateTimeOffset.UtcNow);
}
