using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationSnapshotAuditorTests
{
    [Fact]
    public void DetectsOverlappingPartitionsAndDuplicateLetters()
    {
        var os = new OsDiskInfo("os:1", "Disk", 1, "GPT", 10_000, false, false, false, "p:1", null);
        var a = new PartitionInfo(
            "part:a", true, 1, 1, "Primary", 100, 500, false, false,
            "C", "", "NTFS", 4096, 100, "Healthy", "OK", "C:\\", "os:1");
        var b = new PartitionInfo(
            "part:b", true, 1, 2, "Primary", 200, 500, false, false,
            "C", "", "NTFS", 4096, 100, "Healthy", "OK", "C:\\", "os:1");
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "PC", "Windows", "10", "1", DateTimeOffset.UtcNow),
            [],
            [],
            [],
            [],
            [],
            [os],
            [a, b],
            [
                new VolumeInfo("vol:a", true, "part:a", "NTFS", "", 500, 100, 4096, "Healthy", "OK", ["C:\\"]),
                new VolumeInfo("vol:b", true, "part:b", "NTFS", "", 500, 100, 4096, "Healthy", "OK", ["C:\\"])
            ],
            [],
            [],
            []);
        var findings = SimulationSnapshotAuditor.Audit(snapshot, "sample");
        Assert.Contains(findings, item => item.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, item => item.Contains("Drive letter C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CuratedLayoutsHaveNoIntegrityFindings()
    {
        foreach (var layout in SimulationLayouts.CreateAll())
        {
            var findings = SimulationSnapshotAuditor.Audit(layout.Snapshot, layout.Name);
            Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
        }
    }

    [Fact]
    public void CuratedPhysicalDisksUseDecimalMarketedCapacities()
    {
        var standard = SimulationLayouts.StandardTiered();
        Assert.Equal(
            1_000_000_000_000L,
            standard.PhysicalDisks.Single(disk => disk.FriendlyName == "SSD-1").Size);
        Assert.Equal(
            4_000_000_000_000L,
            standard.PhysicalDisks.Single(disk => disk.FriendlyName == "HDD-1").Size);
        Assert.Equal(
            18_000_000_000_000L,
            standard.StoragePools.Single(pool => pool.FriendlyName == "Pool01").Size);
        Assert.Equal("931.32 GiB", TopologyProjector.FormatBytes(1_000_000_000_000L));

        var triple = SimulationLayouts.TripleTier();
        Assert.Equal(
            256_000_000_000L,
            triple.PhysicalDisks.Single(disk => disk.FriendlyName == "Cache-1").Size);

        var tall = SimulationLayouts.ManyPartitions();
        var physical = Assert.Single(tall.PhysicalDisks, disk => disk.IsSystem);
        var os = Assert.Single(tall.OsDisks, disk => disk.IsSystem);
        Assert.Equal(2_000_000_000_000L, physical.Size);
        Assert.Equal(physical.Size, os.Size);
    }

    [Fact]
    public void TripleTierUsesScmCacheAndManyPartitionDiskHasEightSlices()
    {
        var triple = SimulationLayouts.TripleTier();
        var cache = Assert.Single(triple.StorageTiers, item => item.FriendlyName == "Cache");
        Assert.Equal("SCM", cache.MediaType);
        Assert.All(
            cache.MemberPhysicalDiskIds,
            id => Assert.Equal("SCM", triple.PhysicalDisks.Single(disk => disk.StableId == id).MediaType));

        var tall = SimulationLayouts.ManyPartitions();
        var system = Assert.Single(tall.OsDisks, item => item.IsSystem);
        var parts = tall.Partitions.Where(item => item.OsDiskStableId == system.StableId).OrderBy(item => item.Offset).ToArray();
        Assert.Equal(8, parts.Length);
        Assert.Equal("EfiSystem", parts[0].Type);
        Assert.Equal("MicrosoftReserved", parts[1].Type);
        Assert.Equal("WindowsRecovery", parts[^1].Type);
    }

    [Fact]
    public void DenseServerHasALargePhysicalDiskSet()
    {
        var server = SimulationLayouts.DenseServer();
        Assert.True(server.PhysicalDisks.Count >= 40, $"Expected 40+ disks, found {server.PhysicalDisks.Count}.");
        Assert.Empty(SimulationSnapshotAuditor.Audit(server, "超多磁盘服务器"));
    }
}
