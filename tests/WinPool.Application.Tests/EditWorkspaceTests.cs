using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class EditWorkspaceTests
{
    [Fact]
    public void PartitionWorkspaceShowsDiskAndPartitionOnlyAndSplitsUnallocatedGaps()
    {
        var snapshot = TwoGapDiskSnapshot();
        var disks = EditWorkspace.ProjectPartitionWorkspace(snapshot, minUnallocatedBytes: 0);
        var disk = Assert.Single(disks);
        Assert.Equal(StorageUnitKind.OsDisk, disk.Unit.Kind);
        Assert.Equal(3, disk.Children.Count);
        Assert.True(EditWorkspace.IsUnallocated(disk.Children[0].Unit.StableId));
        Assert.Equal(StorageUnitKind.Partition, disk.Children[1].Unit.Kind);
        Assert.False(EditWorkspace.IsUnallocated(disk.Children[1].Unit.StableId));
        Assert.True(EditWorkspace.IsUnallocated(disk.Children[2].Unit.StableId));
        Assert.All(disks, node => Assert.DoesNotContain(
            TopologyProjector.Flatten(node),
            child => child.Unit.Kind is StorageUnitKind.System
                or StorageUnitKind.StoragePool
                or StorageUnitKind.StorageTier
                or StorageUnitKind.NetworkDisk));
    }

    [Fact]
    public void PartitionWorkspaceIgnoresGapsBelowDefaultThreshold()
    {
        var snapshot = TwoGapDiskSnapshot();
        var disk = Assert.Single(EditWorkspace.ProjectPartitionWorkspace(snapshot));
        Assert.Equal(StorageUnitKind.Partition, Assert.Single(disk.Children).Unit.Kind);
    }

    [Fact]
    public void PartitionWorkspaceKeepsGapsAtOrAboveThreshold()
    {
        var snapshot = TwoGapDiskSnapshot();
        var disk = Assert.Single(EditWorkspace.ProjectPartitionWorkspace(snapshot, 200_000));
        Assert.Equal(3, disk.Children.Count);
        Assert.True(EditWorkspace.IsUnallocated(disk.Children[0].Unit.StableId));
        Assert.Equal(StorageUnitKind.Partition, disk.Children[1].Unit.Kind);
        Assert.True(EditWorkspace.IsUnallocated(disk.Children[2].Unit.StableId));
    }

    [Fact]
    public void PartitionWorkspaceHidesNonPrimordialPhysicalMembersAndShowsVirtualDisks()
    {
        var snapshot = TestSnapshotFactory.Create();
        var disks = EditWorkspace.ProjectPartitionWorkspace(snapshot);
        Assert.DoesNotContain(disks, node => node.Unit.Kind == StorageUnitKind.PhysicalDisk);
        Assert.Contains(disks, node => node.Unit.StableId == "osdisk:3");
    }

    [Fact]
    public void PoolWorkspaceShowsInternalPoolsPlusNodeAndHidesNetwork()
    {
        var snapshot = TestSnapshotFactory.Create() with
        {
            NetworkDisks =
            [
                new NetworkDiskInfo("net:1", true, "Share", "Z", "\\\\s\\z", "NTFS", 1, 1)
            ]
        };
        var nodes = EditWorkspace.ProjectPoolWorkspace(snapshot);
        Assert.DoesNotContain(nodes, node => node.Unit.Kind == StorageUnitKind.NetworkDiskGroup);
        Assert.Contains(nodes, node => node.Unit.StableId == "pool:1");
        Assert.True(EditWorkspace.IsPlus(nodes[^1].Unit.StableId));
        var named = nodes.Single(node => node.Unit.StableId == "pool:1");
        Assert.Contains(named.Children, child => child.Unit.Kind == StorageUnitKind.StorageTier);
        Assert.Contains(named.Children, child => child.Unit.Kind == StorageUnitKind.VirtualDisk);
    }

    [Fact]
    public void PrimordialPoolStopsAtDisks()
    {
        var snapshot = PrimordialSnapshot();
        var primordial = EditWorkspace.ProjectPoolWorkspace(snapshot)
            .Single(node => node.Unit.StableId == "pool:primordial");
        Assert.All(primordial.Children, child => Assert.Equal(StorageUnitKind.PhysicalDisk, child.Unit.Kind));
        Assert.All(primordial.Children, child => Assert.Empty(child.Children));
    }

    [Fact]
    public void MoveDiskToPoolSendsSsdToPerformanceAndHddToCapacity()
    {
        var snapshot = PrimordialSnapshot();
        snapshot = EditWorkspace.InsertDraftPool(snapshot, "PoolA");
        var draft = snapshot.StoragePools.Single(pool => EditWorkspace.IsDraftPool(pool.StableId));
        snapshot = EditWorkspace.MoveDiskToPool(snapshot, "physical:ssd", draft.StableId);
        snapshot = EditWorkspace.MoveDiskToPool(snapshot, "physical:hdd", draft.StableId);
        var ssdTier = snapshot.StorageTiers.Single(tier =>
            tier.PoolStableId == draft.StableId && tier.MediaType == "SSD");
        var hddTier = snapshot.StorageTiers.Single(tier =>
            tier.PoolStableId == draft.StableId && tier.MediaType == "HDD");
        Assert.Contains("physical:ssd", ssdTier.MemberPhysicalDiskIds);
        Assert.Contains("physical:hdd", hddTier.MemberPhysicalDiskIds);
        Assert.Equal("Simple", ssdTier.ResiliencySettingName);
        Assert.Equal("Simple", hddTier.ResiliencySettingName);
    }

    [Fact]
    public void MoveUnknownMediaIsRejected()
    {
        var snapshot = PrimordialSnapshot();
        snapshot = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(disk => disk.StableId == "physical:ssd"
                    ? disk with { MediaType = "Unspecified" }
                    : disk)
                .ToArray()
        };
        snapshot = EditWorkspace.InsertDraftPool(snapshot, "PoolA");
        var draft = snapshot.StoragePools.Single(pool => EditWorkspace.IsDraftPool(pool.StableId));
        Assert.Throws<InvalidOperationException>(() =>
            EditWorkspace.MoveDiskToPool(snapshot, "physical:ssd", draft.StableId));
    }

    [Fact]
    public void ManageProjectionStillIncludesSystemAndIsUnchangedByEditWorkspace()
    {
        var snapshot = TestSnapshotFactory.Create();
        var root = TopologyProjector.Project(snapshot);
        Assert.Equal(StorageUnitKind.System, root.Unit.Kind);
        Assert.Contains(root.Children, child => child.Unit.Kind == StorageUnitKind.StoragePool);
        var edit = EditWorkspace.ProjectPartitionWorkspace(snapshot);
        Assert.DoesNotContain(edit, node => node.Unit.Kind == StorageUnitKind.System);
    }

    [Fact]
    public void ToManageViewPreservesOccurrenceTree()
    {
        var snapshot = TestSnapshotFactory.Create();
        var disk = Assert.Single(EditWorkspace.ProjectPartitionWorkspace(snapshot));
        var view = EditWorkspace.ToManageView(disk, SystemId.New(), "edit");
        Assert.Equal(disk.Unit.StableId, view.Id.ProviderKey);
        Assert.Equal(disk.Children.Count, view.Children.Count);
    }

    private static StorageSnapshot TwoGapDiskSnapshot()
    {
        var osDisk = new OsDiskInfo(
            "osdisk:gap", "Gap Disk", 1, "GPT", 1_000_000, false, false, false, "physical:gap", null);
        var partition = new PartitionInfo(
            "partition:mid", true, 1, 1, "Primary", 200_000, 300_000, false, false,
            "E", "Data", "NTFS", 65536, 100_000, "Healthy", "OK", "E:\\", "osdisk:gap");
        return new StorageSnapshot(
            2, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [],
            [
                new PhysicalDiskInfo(
                    "physical:gap", true, "Gap Disk", "Model", "XX0001", "SATA", "HDD",
                    1_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 1,
                    false, false, false, false, "pool:primordial")
            ],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    1_000_000, 0, null, ["physical:gap"])
            ],
            [],
            [],
            [osDisk],
            [partition],
            [],
            [],
            []);
    }

    private static StorageSnapshot PrimordialSnapshot()
    {
        var ssd = new PhysicalDiskInfo(
            "physical:ssd", true, "SSD One", "Model", "SS0001", "SATA", "SSD",
            1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 2,
            false, false, false, false, "pool:primordial");
        var hdd = new PhysicalDiskInfo(
            "physical:hdd", true, "HDD One", "Model", "HD0001", "SATA", "HDD",
            2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 3,
            false, false, false, false, "pool:primordial");
        return new StorageSnapshot(
            2, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [ssd, hdd],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    3_000_000_000L, 0, "subsystem:1", ["physical:ssd", "physical:hdd"])
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
    }
}
