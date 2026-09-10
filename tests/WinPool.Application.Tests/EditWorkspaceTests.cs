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
    public void DiskCannotLeavePinsOriginalMembersAndSystemDisks()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var ssd = snapshot.PhysicalDisks.Single(item => item.StableId == "physical:ssd");
        Assert.True(EditWorkspace.DiskCannotLeave(snapshot, snapshot, ssd));
        Assert.False(EditWorkspace.DiskHoldsStoredData(snapshot, "physical:ssd"));

        var extra = snapshot.PhysicalDisks.Single(item => item.StableId == "physical:extra");
        var drafted = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        var moved = EditWorkspace.MoveDiskToPool(drafted, extra.StableId, drafted.StoragePools.Last(
            item => EditWorkspace.IsDraftPool(item.StableId)).StableId);
        var movedExtra = moved.PhysicalDisks.Single(item => item.StableId == extra.StableId);
        Assert.False(EditWorkspace.DiskCannotLeave(moved, snapshot, movedExtra));

        var boot = extra with { StableId = "physical:boot", IsBoot = true, PoolStableId = "pool:primordial" };
        Assert.True(EditWorkspace.DiskCannotLeave(snapshot, snapshot, boot));
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
    public void PoolAndDiskEditStatusAreIndependent()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var pools = EditWorkspace.ProjectPoolWorkspace(snapshot, minUnallocatedBytes: 0);
        var pool = pools.Single(node => node.Unit.StableId == "pool:t1");
        Assert.True(pool.ShowsEditStatus);
        Assert.True(pool.HasStoredData);
        Assert.False(pool.CannotLeave);

        var virtualDisk = Assert.Single(
            pool.Children,
            child => child.Unit.Kind == StorageUnitKind.VirtualDisk);
        Assert.True(virtualDisk.ShowsEditStatus);
        Assert.True(virtualDisk.HasStoredData);
        Assert.False(virtualDisk.CannotLeave);

        var memberDisks = pool.Children
            .SelectMany(child => child.Unit.Kind == StorageUnitKind.PhysicalDisk
                ? [child]
                : child.Children.Where(item => item.Unit.Kind == StorageUnitKind.PhysicalDisk))
            .ToArray();
        Assert.NotEmpty(memberDisks);
        Assert.All(memberDisks, disk => Assert.True(disk.ShowsEditStatus));
        Assert.Contains(memberDisks, disk => disk.Unit.StableId == "physical:ssd" && disk.CannotLeave);
        Assert.Contains(memberDisks, disk => disk.Unit.StableId == "physical:extra" && !disk.CannotLeave);
        Assert.Contains(memberDisks, disk => disk.Unit.StableId == "physical:ssd" && !disk.HasStoredData);

        var draft = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        var moved = EditWorkspace.MoveDiskToPool(draft, "physical:extra", draft.StoragePools.Last(
            item => EditWorkspace.IsDraftPool(item.StableId)).StableId);
        var draftNode = EditWorkspace.ProjectPoolWorkspace(moved, 0, snapshot).Single(
            node => EditWorkspace.IsDraftPool(node.Unit.StableId));
        Assert.True(draftNode.ShowsEditStatus);
        Assert.False(draftNode.HasStoredData);
        Assert.False(draftNode.CannotLeave);
        var extra = draftNode.Children
            .SelectMany(child => child.Children.Append(child))
            .Single(item => item.Unit.StableId == "physical:extra");
        Assert.False(extra.CannotLeave);
        Assert.False(extra.HasStoredData);
        Assert.DoesNotContain(draftNode.Children, child => child.Unit.Kind == StorageUnitKind.VirtualDisk);
    }

    [Fact]
    public void ClassifyDiskEvictSeparatesSystemPageFileAndOrdinaryDisks()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var ssd = snapshot.PhysicalDisks.Single(item => item.StableId == "physical:ssd");
        Assert.Equal(EditWorkspace.DiskEvictCheck.Allowed, EditWorkspace.ClassifyDiskEvict(ssd));

        var page = ssd with { IsPageFile = true };
        Assert.Equal(EditWorkspace.DiskEvictCheck.ConfirmPageFile, EditWorkspace.ClassifyDiskEvict(page));

        var dump = ssd with { IsCrashDump = true };
        Assert.Equal(EditWorkspace.DiskEvictCheck.ConfirmCrashDump, EditWorkspace.ClassifyDiskEvict(dump));

        var system = ssd with { IsSystem = true };
        Assert.Equal(EditWorkspace.DiskEvictCheck.DeniedSystem, EditWorkspace.ClassifyDiskEvict(system));

        var boot = ssd with { IsBoot = true };
        Assert.Equal(EditWorkspace.DiskEvictCheck.DeniedSystem, EditWorkspace.ClassifyDiskEvict(boot));
    }

    [Fact]
    public void EvictDiskToUnallocatedLeavesPoolAndClearsPin()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var ssd = snapshot.PhysicalDisks.Single(item => item.StableId == "physical:ssd");
        Assert.True(EditWorkspace.DiskCannotLeave(snapshot, snapshot, ssd));
        Assert.True(EditWorkspace.DiskIsAssignedToTier(snapshot, ssd.StableId));

        var evicted = EditWorkspace.EvictDiskToUnallocated(snapshot, ssd.StableId);
        var moved = evicted.PhysicalDisks.Single(item => item.StableId == ssd.StableId);
        Assert.Equal("pool:t1", moved.PoolStableId);
        Assert.False(EditWorkspace.DiskIsAssignedToTier(evicted, ssd.StableId));
        Assert.False(EditWorkspace.DiskCannotLeave(evicted, snapshot, moved));
        Assert.True(EditWorkspace.HasPendingModifications(evicted, snapshot, ssd.StableId));
        Assert.True(EditWorkspace.HasPendingModifications(evicted, snapshot, "pool:t1"));

        var pool = EditWorkspace.ProjectPoolWorkspace(evicted, 0, snapshot)
            .Single(node => node.Unit.StableId == "pool:t1");
        var unallocated = Assert.Single(
            pool.Children,
            child => child.Unit.Kind == StorageUnitKind.DirectDiskGroup);
        Assert.Contains(unallocated.Children, disk => disk.Unit.StableId == ssd.StableId && !disk.CannotLeave);
    }

    [Fact]
    public void MoveDiskToPoolOntoSamePoolAssignsUnallocatedMemberToMatchingTier()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var evicted = EditWorkspace.EvictDiskToUnallocated(snapshot, "physical:ssd");
        Assert.False(EditWorkspace.DiskIsAssignedToTier(evicted, "physical:ssd"));

        var restored = EditWorkspace.MoveDiskToPool(evicted, "physical:ssd", "pool:t1");
        Assert.True(EditWorkspace.DiskIsAssignedToTier(restored, "physical:ssd"));
        var ssdTier = restored.StorageTiers.Single(tier =>
            tier.PoolStableId == "pool:t1" && EditWorkspace.NormalizeMedia(tier.MediaType) == "SSD");
        Assert.Contains("physical:ssd", ssdTier.MemberPhysicalDiskIds);
        Assert.True(EditWorkspace.DiskNeedsSamePoolTierAssignment(restored, evicted, "physical:ssd"));
        Assert.False(EditWorkspace.DiskNeedsSamePoolTierAssignment(evicted, evicted, "physical:ssd"));
    }

    [Fact]
    public void RestoreWorkingMembershipKeepsSamePoolTierAssignmentAndDraft()
    {
        var committed = TieredPoolSnapshot(withVirtualDisk: true);
        committed = EditWorkspace.EvictDiskToUnallocated(committed, "physical:ssd");
        var renamed = committed with
        {
            StoragePools = committed.StoragePools
                .Select(pool => pool.StableId == "pool:t1" ? pool with { FriendlyName = "Renamed" } : pool)
                .ToArray()
        };

        var working = EditWorkspace.MoveDiskToPool(committed, "physical:ssd", "pool:t1");
        working = EditWorkspace.InsertDraftPool(working, "Pool X");
        var draftId = working.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;
        working = EditWorkspace.MoveDiskToPool(working, "physical:extra", draftId);

        var restored = EditWorkspace.RestoreWorkingMembership(renamed, working);
        Assert.Equal("Renamed", restored.StoragePools.Single(item => item.StableId == "pool:t1").FriendlyName);
        Assert.True(EditWorkspace.DiskIsAssignedToTier(restored, "physical:ssd"));
        var draft = Assert.Single(restored.StoragePools, item => EditWorkspace.IsDraftPool(item.StableId));
        Assert.Equal("Pool X", draft.FriendlyName);
        Assert.Contains("physical:extra", draft.MemberPhysicalDiskIds);
        Assert.Equal(
            draft.StableId,
            restored.PhysicalDisks.Single(item => item.StableId == "physical:extra").PoolStableId);
    }

    [Fact]
    public void RestoreWorkingMembershipIsIdentityWhenMembershipMatches()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var restored = EditWorkspace.RestoreWorkingMembership(snapshot, snapshot);
        Assert.True(EditWorkspace.DiskIsAssignedToTier(restored, "physical:ssd"));
        Assert.False(EditWorkspace.DiskIsAssignedToTier(restored, "physical:hdd"));
        Assert.Equal(
            snapshot.StoragePools.Select(item => item.StableId),
            restored.StoragePools.Select(item => item.StableId));
        Assert.DoesNotContain(restored.StoragePools, item => EditWorkspace.IsDraftPool(item.StableId));
    }

    [Fact]
    public void EvictDiskToUnallocatedRefusesSystemDisks()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var system = snapshot.PhysicalDisks.Single(item => item.StableId == "physical:ssd")
            with { IsSystem = true };
        snapshot = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId == system.StableId ? system : item)
                .ToArray()
        };
        Assert.Throws<InvalidOperationException>(
            () => EditWorkspace.EvictDiskToUnallocated(snapshot, system.StableId));
    }

    [Fact]
    public void MultipleVirtualDisksUseTheHiddenHorizontalFlowGroup()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: true);
        var first = snapshot.VirtualDisks[0];
        var second = first with { StableId = "vdisk:2", FriendlyName = "Pool01-B" };
        snapshot = snapshot with { VirtualDisks = [first, second] };

        var pool = EditWorkspace.ProjectPoolWorkspace(snapshot, 0)
            .Single(node => node.Unit.StableId == "pool:t1");
        var group = Assert.Single(
            pool.Children,
            child => child.Unit.Kind == StorageUnitKind.VirtualDiskGroup);
        Assert.Equal(TopologyChildrenLayout.Flow, group.ChildrenLayout);
        Assert.False(group.IsSelectable);
        Assert.Equal(2, group.Children.Count);
        Assert.All(
            group.Children,
            child =>
            {
                Assert.Equal(StorageUnitKind.VirtualDisk, child.Unit.Kind);
                Assert.True(child.ShowsEditStatus);
            });
        Assert.DoesNotContain(pool.Children, child => child.Unit.Kind == StorageUnitKind.VirtualDisk);
        Assert.Equal(TopologyChildrenLayout.Stack, pool.ChildrenLayout);
    }

    [Fact]
    public void PoolWithoutVirtualDiskOmitsPlaceholderCard()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var pool = EditWorkspace.ProjectPoolWorkspace(snapshot, 0)
            .Single(node => node.Unit.StableId == "pool:t1");
        Assert.DoesNotContain(pool.Children, child => child.Unit.Kind == StorageUnitKind.VirtualDisk);
    }

    [Fact]
    public void DraftPoolWithDataBearingMemberIsUnsupported()
    {
        var snapshot = TieredPoolSnapshot(withVirtualDisk: false);
        var drafted = EditWorkspace.InsertDraftPool(snapshot, "Pool X");
        var draftId = drafted.StoragePools.Last(item => EditWorkspace.IsDraftPool(item.StableId)).StableId;

        // Empty draft stays supported.
        Assert.True(EditWorkspace.PoolSupportsStructureModification(drafted, draftId));

        // Dragging in a data-bearing physical disk locks the draft.
        var dataDisk = new PhysicalDiskInfo(
            "physical:data", true, "Data Disk", "Model", "DA0001", "SATA", "HDD",
            1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 9,
            false, false, false, false, "pool:primordial");
        var withDiskSource = drafted with
        {
            PhysicalDisks = drafted.PhysicalDisks.Append(dataDisk).ToArray(),
            StoragePools = drafted.StoragePools.Select(pool => pool.IsPrimordial
                ? pool with { MemberPhysicalDiskIds =
                    [.. pool.MemberPhysicalDiskIds, "physical:data"] }
                : pool).ToArray(),
            OsDisks = drafted.OsDisks.Append(new OsDiskInfo(
                "osdisk:data", "Data Disk", 9, "GPT", 1_000_000_000, false, false, false,
                "physical:data", null)).ToArray(),
            Partitions = drafted.Partitions.Append(new PartitionInfo(
                "partition:data", true, 9, 1, "Primary", 0, 1_000_000_000, false, false,
                "Z", "Data", "NTFS", 65536, 100_000, "Healthy", "OK", string.Empty,
                "osdisk:data")).ToArray()
        };
        var withDisk = EditWorkspace.MoveDiskToPool(withDiskSource, "physical:data", draftId);
        Assert.False(EditWorkspace.PoolSupportsStructureModification(withDisk, draftId));
        var node = EditWorkspace.ProjectPoolWorkspace(withDisk, 0, snapshot).Single(
            item => item.Unit.StableId == draftId);
        Assert.True(node.HasStoredData);
        Assert.False(node.CannotLeave);
        var dataMember = node.Children
            .SelectMany(child => child.Children.Append(child))
            .Single(item => item.Unit.StableId == "physical:data");
        Assert.True(dataMember.HasStoredData);
        Assert.False(dataMember.CannotLeave);

        // Dragging the data disk back out unlocks the draft (no deadlock).
        var recovered = EditWorkspace.MoveDiskToPool(withDisk, "physical:data", "pool:primordial");
        Assert.True(EditWorkspace.PoolSupportsStructureModification(recovered, draftId));
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
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
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
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
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
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
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
            [],
            []);
    }
}

public sealed class EditWorkspaceLayerTests
{
    [Fact]
    public void SetDiskUsageMovesPooledDiskBetweenSimulatedLayers()
    {
        var snapshot = TestSnapshotFactory.Create();
        Assert.False(EditWorkspace.DiskUsage(snapshot.PhysicalDisks.Single(x => x.StableId == "physical:1")).Length > 0);

        var retired = EditWorkspace.SetDiskUsage(snapshot, "physical:1", "Retired");
        var disk = retired.PhysicalDisks.Single(x => x.StableId == "physical:1");
        Assert.True(disk.IsRetired);
        Assert.False(disk.IsHotSpare);
        Assert.False(EditWorkspace.DiskIsAssignedToTier(retired, "physical:1"));

        var hotSpare = EditWorkspace.SetDiskUsage(retired, "physical:1", "HotSpare");
        disk = hotSpare.PhysicalDisks.Single(x => x.StableId == "physical:1");
        Assert.False(disk.IsRetired);
        Assert.True(disk.IsHotSpare);

        var cleared = EditWorkspace.SetDiskUsage(hotSpare, "physical:1", string.Empty);
        disk = cleared.PhysicalDisks.Single(x => x.StableId == "physical:1");
        Assert.False(disk.IsRetired);
        Assert.False(disk.IsHotSpare);
        Assert.Equal("pool:1", disk.PoolStableId);
    }

    [Fact]
    public void SetDiskUsageRefusesBootSystemAndPrimordialDisks()
    {
        var snapshot = TestSnapshotFactory.Create();
        var boot = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks
                .Select(item => item.StableId == "physical:1" ? item with { IsBoot = true } : item)
                .ToArray()
        };
        Assert.Throws<InvalidOperationException>(() => EditWorkspace.SetDiskUsage(boot, "physical:1", "Retired"));
        Assert.Throws<InvalidOperationException>(() => EditWorkspace.SetDiskUsage(snapshot, "missing", "Retired"));
        Assert.Throws<InvalidOperationException>(() => EditWorkspace.SetDiskUsage(snapshot, "physical:1", "Journal"));
    }

    [Fact]
    public void MoveDiskBackIntoDataTierClearsSimulatedLayerRole()
    {
        var snapshot = TestSnapshotFactory.Create();
        var retired = EditWorkspace.SetDiskUsage(snapshot, "physical:1", "Retired");
        var reassigned = EditWorkspace.MoveDiskToPool(retired, "physical:1", "pool:1");
        var disk = reassigned.PhysicalDisks.Single(x => x.StableId == "physical:1");
        Assert.False(disk.IsRetired);
        Assert.False(disk.IsHotSpare);
        Assert.True(EditWorkspace.DiskIsAssignedToTier(reassigned, "physical:1"));
    }

    [Fact]
    public void DissolvePoolInWorkingReturnsMembersAndRemovesObjects()
    {
        var snapshot = TestSnapshotFactory.Create();
        var primordial = new StoragePoolInfo(
            "pool:primordial", true, "Primordial", true, "Healthy", "OK", 0, 0,
            "subsystem:1", []);
        var withPrimordial = snapshot with
        {
            StoragePools = new[] { primordial }.Concat(snapshot.StoragePools).ToArray()
        };
        var dissolved = EditWorkspace.DissolvePoolInWorking(withPrimordial, "pool:1");
        Assert.DoesNotContain(dissolved.StoragePools, pool => pool.StableId == "pool:1");
        var disk = dissolved.PhysicalDisks.Single(x => x.StableId == "physical:1");
        Assert.Equal("pool:primordial", disk.PoolStableId);
        Assert.DoesNotContain(dissolved.StorageTiers, tier => tier.PoolStableId == "pool:1");
        Assert.DoesNotContain(dissolved.VirtualDisks, vdisk => vdisk.PoolStableId == "pool:1");
        Assert.Empty(dissolved.Partitions);
    }

    [Fact]
    public void StructuralChangesDetectPoolsRolesAndVirtualDisks()
    {
        var baseline = TestSnapshotFactory.Create();
        var twin = TestSnapshotFactory.Create();
        Assert.False(EditWorkspace.HasStructuralChanges(baseline, twin));

        var retired = EditWorkspace.SetDiskUsage(baseline, "physical:1", "Retired");
        Assert.True(EditWorkspace.HasStructuralChanges(baseline, retired));

        var noVdisk = baseline with
        {
            VirtualDisks = [],
            OsDisks = [],
            Partitions = [],
            StoragePools = baseline.StoragePools
                .Select(item => item with { AllocatedSize = 0 })
                .ToArray()
        };
        Assert.True(EditWorkspace.HasStructuralChanges(baseline, noVdisk));

        var noPool = baseline with { StoragePools = baseline.StoragePools.Where(item => item.StableId != "pool:1").ToArray() };
        Assert.True(EditWorkspace.HasStructuralChanges(baseline, noPool));
    }

    [Fact]
    public void DraftVirtualDiskInsertAndDeleteStayOnTheWorkingCopy()
    {
        var snapshot = TestSnapshotFactory.Create();
        // The factory pool already owns one virtual disk; remove it first to
        // reach the create-one-virtual-disk state.
        var existing = snapshot.VirtualDisks.Single(x => x.PoolStableId == "pool:1");
        var withoutVdisk = snapshot with
        {
            VirtualDisks = [],
            OsDisks = snapshot.OsDisks.Where(item => item.VirtualDiskStableId != existing.StableId).ToArray(),
            Partitions = snapshot.Partitions.Where(item => item.OsDiskStableId != "osdisk:3").ToArray()
        };
        var next = EditWorkspace.InsertDraftVirtualDisk(withoutVdisk, "pool:1", "Draft01", "Parity", 65536);
        var draft = next.VirtualDisks.Single(vdisk => EditWorkspace.IsDraftVirtualDisk(vdisk.StableId));
        Assert.Equal("pool:1", draft.PoolStableId);
        Assert.True(EditWorkspace.HasStructuralChanges(withoutVdisk, next));
        Assert.Throws<InvalidOperationException>(
            () => EditWorkspace.InsertDraftVirtualDisk(next, "pool:1", "Second", "Simple", 65536));

        var removed = EditWorkspace.DeleteVirtualDiskFromWorking(next, draft.StableId);
        Assert.DoesNotContain(removed.VirtualDisks, vdisk => vdisk.StableId == draft.StableId);
    }
}

public sealed class TierCardCapacityTests
{
    private static TopologyNode? FindNode(TopologyNode root, StorageUnitKind kind)
    {
        if (root.Unit.Kind == kind)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var found = FindNode(child, kind);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void TierCardSummaryShowsTierCapacityNotMemberDiskSum()
    {
        var snapshot = TestSnapshotFactory.Create();
        // The factory tier reserves 1_000_000 while its member disk holds
        // 2_000_000: the card must show the RESERVATION, not the disk sum.
        var reserved = snapshot with
        {
            StorageTiers = snapshot.StorageTiers
                .Select(tier => tier with { Size = 700L * 1024 * 1024 * 1024, FootprintOnPool = 700L * 1024 * 1024 * 1024 })
                .ToArray()
        };
        var root = EditWorkspace.ProjectPoolWorkspaceRoot(reserved);
        var tierNode = FindNode(root, StorageUnitKind.StorageTier);
        Assert.NotNull(tierNode);
        Assert.Contains("700 GiB", tierNode.Summary, StringComparison.Ordinal);
        // 2_000_000 bytes formats to "1.91 MiB": the member-disk total must
        // not be what the card reports.
        Assert.DoesNotContain("1.91 MiB", tierNode.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsetTierCapacityFallsBackToMemberDiskSum()
    {
        var snapshot = TestSnapshotFactory.Create();
        var unset = snapshot with
        {
            StorageTiers = snapshot.StorageTiers
                .Select(tier => tier with { Size = 0, FootprintOnPool = 0 })
                .ToArray()
        };
        var root = EditWorkspace.ProjectPoolWorkspaceRoot(unset);
        var tierNode = FindNode(root, StorageUnitKind.StorageTier);
        Assert.NotNull(tierNode);
        // 2_000_000 bytes -> "1.91 MiB": the member-disk fallback.
        Assert.Contains("1.91 MiB", tierNode.Summary, StringComparison.Ordinal);
    }
}

public sealed class NormalizeTierCapacityTests
{
    [Fact]
    public void UnsetTierCapacityNormalizesToMemberCapacity()
    {
        var snapshot = TestSnapshotFactory.Create();
        // Factory tier has a size; zero it to simulate legacy documents.
        var zeroed = snapshot with
        {
            StorageTiers = snapshot.StorageTiers
                .Select(tier => tier with { Size = 0, FootprintOnPool = 0 })
                .ToArray()
        };
        var normalized = EditWorkspace.NormalizeTierCapacities(zeroed);
        var tier = normalized.StorageTiers.Single();
        Assert.True(tier.Size > 0);
        Assert.True(tier.Size <= 2_000_000);
        Assert.Equal(CapacitySourceKind.SimulatedEstimate, tier.SizeSource);
    }

    [Fact]
    public void ExplicitCapacityIsNeverOverwritten()
    {
        var snapshot = TestSnapshotFactory.Create();
        var normalized = EditWorkspace.NormalizeTierCapacities(snapshot);
        Assert.Equal(1_000_000, normalized.StorageTiers.Single().Size);
    }
}
