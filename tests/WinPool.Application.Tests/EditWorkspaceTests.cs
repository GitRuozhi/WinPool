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

    [Fact]
    public void PoolRendersSnapshotTiersWithMembersOnly()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var pools = EditWorkspace.ProjectPoolWorkspace(snapshot, minUnallocatedBytes: 0);
        var pool = pools.Single(node => node.Unit.StableId == "pool:t1");
        var tierChildren = pool.Children
            .Where(child => child.Unit.Kind == StorageUnitKind.StorageTier)
            .ToArray();
        var tier = Assert.Single(tierChildren);
        Assert.Equal("tier:ssd", tier.Unit.StableId);
        Assert.Single(tier.Children);
    }

    [Fact]
    public void TierUncoveredMembersRenderInTheUnallocatedGroup()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var pools = EditWorkspace.ProjectPoolWorkspace(snapshot, minUnallocatedBytes: 0);
        var pool = pools.Single(node => node.Unit.StableId == "pool:t1");
        var group = Assert.Single(pool.Children, child => child.Unit.Kind == StorageUnitKind.DirectDiskGroup);
        Assert.Equal("group:direct:pool:t1", group.Unit.StableId);
        Assert.Equal(2, group.Children.Count);
        Assert.Contains(group.Children, disk => disk.Unit.StableId == "physical:extra");
        Assert.Contains(group.Children, disk => disk.Unit.StableId == "physical:hdd");
    }

    [Fact]
    public void PlusPoolDisappearsWhileADraftExists()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        Assert.Contains(
            EditWorkspace.ProjectPoolWorkspace(snapshot, 0),
            node => EditWorkspace.IsPlus(node.Unit.StableId));

        var drafted = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        var duringDraft = EditWorkspace.ProjectPoolWorkspace(drafted, 0);
        Assert.DoesNotContain(duringDraft, node => EditWorkspace.IsPlus(node.Unit.StableId));
        Assert.Single(duringDraft, node => EditWorkspace.IsDraftPool(node.Unit.StableId));

        var discarded = EditWorkspace.DiscardDraftPool(drafted, duringDraft.Single(node => EditWorkspace.IsDraftPool(node.Unit.StableId)).Unit.StableId);
        Assert.Contains(
            EditWorkspace.ProjectPoolWorkspace(discarded, 0),
            node => EditWorkspace.IsPlus(node.Unit.StableId));
    }

    [Fact]
    public void ModifiabilityRulesFollowDataPresence()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);

        // The virtual disk carries a data-bearing NTFS partition.
        Assert.False(EditWorkspace.PoolSupportsStructureModification(snapshot, "pool:t1"));
        Assert.False(EditWorkspace.DiskSupportsStructureModification(snapshot, "vdisk:1", isVirtualDisk: true));

        // Tier member disks have no partitions of their own.
        Assert.True(EditWorkspace.DiskSupportsStructureModification(snapshot, "physical:ssd", isVirtualDisk: false));

        // A raw disk with a data partition is unsupported; an empty
        // (unformatted) partition keeps the disk supported.
        var withRaw = snapshot with
        {
            OsDisks =
            [
                new OsDiskInfo(
                    "osdisk:raw", "Raw Disk", 5, "GPT", 2_000_000_000, false, false, false,
                    "physical:extra", null)
            ],
            Partitions =
            [
                new PartitionInfo(
                    "partition:raw", true, 5, 1, "Primary", 0, 2_000_000_000, false, false,
                    string.Empty, string.Empty, "RAW", null, 2_000_000_000, "Healthy", "OK",
                    string.Empty, "osdisk:raw")
            ]
        };
        Assert.True(EditWorkspace.DiskSupportsStructureModification(withRaw, "physical:extra", isVirtualDisk: false));

        var withData = withRaw with
        {
            Partitions =
            [
                new PartitionInfo(
                    "partition:raw", true, 5, 1, "Primary", 0, 2_000_000_000, false, false,
                    "F", "Data", "NTFS", 65536, 1_000_000_000, "Healthy", "OK",
                    string.Empty, "osdisk:raw")
            ]
        };
        Assert.False(EditWorkspace.DiskSupportsStructureModification(withData, "physical:extra", isVirtualDisk: false));
        Assert.Equal(
            withData.Partitions.Single().SizeRemaining < withData.Partitions.Single().Size,
            EditWorkspace.PartitionHoldsStoredData(withData.Partitions.Single()));
    }

    [Fact]
    public void CollectStructureProblemsNamesThePoolAndItsDataBearingDisks()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var problems = EditWorkspace.CollectStructureProblems(snapshot, "pool:t1");

        Assert.Contains(problems, problem =>
            problem.StableId == "pool:t1"
            && problem.Kind == EditWorkspace.StructureProblemKind.PoolVirtualDiskData);
        Assert.Contains(problems, problem =>
            problem.StableId == "vdisk:1"
            && problem.Kind == EditWorkspace.StructureProblemKind.DiskHoldsData);

        // A pool without virtual disks has nothing data-bearing to report.
        Assert.Empty(EditWorkspace.CollectStructureProblems(snapshot, "pool:primordial"));
    }

    [Fact]
    public void InsertDraftPoolRefusesWhenADraftAlreadyExists()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var drafted = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        Assert.Throws<InvalidOperationException>(
            () => EditWorkspace.InsertDraftPool(drafted, "Pool Y"));
    }

    [Fact]
    public void PendingModificationRulesFollowWorkingCopyChanges()
    {
        var committed = TieredPoolSnapshot(withVirtualDisk: true);
        var working = EditWorkspace.InsertDraftPool(committed, "Pool X");

        // a draft pool is pending by definition
        var draftId = working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
        Assert.True(EditWorkspace.HasPendingModifications(working, committed, draftId));

        // moving a disk marks both the disk and the receiving pool pending
        var moved = EditWorkspace.MoveDiskToPool(working, "physical:extra", draftId);
        Assert.True(EditWorkspace.HasPendingModifications(moved, committed, "physical:extra"));
        Assert.False(EditWorkspace.HasPendingModifications(committed, committed, "physical:extra"));

        // untouched committed objects are not pending
        Assert.False(EditWorkspace.HasPendingModifications(committed, committed, "pool:t1"));
        Assert.False(EditWorkspace.HasPendingModifications(committed, committed, "physical:ssd"));
    }

    [Fact]
    public void PoolMemberDisksFollowThePoolModifiability()
    {
        // The pool is unsupported: its virtual disk holds a data partition,
        // so every member disk must show the locked state as well.
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var pools = EditWorkspace.ProjectPoolWorkspace(snapshot, minUnallocatedBytes: 0);
        var pool = pools.Single(node => node.Unit.StableId == "pool:t1");
        Assert.False(pool.StructureModifiable);
        Assert.All(
            pool.Children.Where(child => child.Unit.Kind == StorageUnitKind.PhysicalDisk),
            child => Assert.False(child.StructureModifiable));

        // A supported pool keeps its members modifiable.
        var draft = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        var moved = EditWorkspace.MoveDiskToPool(draft, "physical:extra", draft.StoragePools.Last(
            item => EditWorkspace.IsDraftPool(item.StableId)).StableId);
        var draftNode = EditWorkspace.ProjectPoolWorkspace(moved, 0).Single(
            node => EditWorkspace.IsDraftPool(node.Unit.StableId));
        Assert.True(draftNode.StructureModifiable);
    }

    private static StorageSnapshot TieredPoolSnapshot(bool withVirtualDisk)
    {
        var ssd = new PhysicalDiskInfo(
            "physical:ssd", true, "SSD One", "Model", "SS0001", "SATA", "SSD",
            1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 2,
            false, false, false, false, "pool:t1");
        var hdd = new PhysicalDiskInfo(
            "physical:hdd", true, "HDD One", "Model", "HD0001", "SATA", "HDD",
            2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 3,
            false, false, false, false, "pool:t1");
        var extra = new PhysicalDiskInfo(
            "physical:extra", true, "Extra Disk", "Model", "EX0001", "SATA", "HDD",
            2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 4,
            false, false, false, false, "pool:t1");
        var tiers = new List<StorageTierInfo>
        {
            new("tier:ssd", true, "Pool-t1 SSD", "SSD", "Mirror", 1_000_000_000,
                1_000_000_000, "pool:t1", null, ["physical:ssd"]),
            new("tier:hdd-empty", true, "Pool-t1 HDD", "HDD", "Parity", 0,
                0, "pool:t1", null, [])
        };
        return new StorageSnapshot(
            2, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [ssd, hdd, extra],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    0, 0, "subsystem:1", []),
                new StoragePoolInfo(
                    "pool:t1", true, "Pool01", false, "Healthy", "OK",
                    5_000_000_000L, 0, "subsystem:1", ["physical:ssd", "physical:hdd", "physical:extra"])
            ],
            tiers,
            withVirtualDisk
                ? [new VirtualDiskInfo(
                    "vdisk:1", true, "Pool01", "Healthy", "OK", "Simple", "Fixed",
                    null, null, 1_000_000_000, 1_000_000_000, "pool:t1", ["tier:ssd"], [])]
                : [],
            withVirtualDisk
                ? [new OsDiskInfo(
                    "osdisk:vd", "Pool01 Disk", 9, "GPT", 1_000_000_000, false, false, false,
                    null, "vdisk:1")]
                : [],
            withVirtualDisk
                ? [new PartitionInfo(
                    "partition:vd", true, 9, 1, "Primary", 0, 1_000_000_000, false, false,
                    "H", "Pool01", "NTFS", 65536, 100_000, "Healthy", "OK",
                    string.Empty, "osdisk:vd")]
                : [],
            [],
            [],
            []);
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
