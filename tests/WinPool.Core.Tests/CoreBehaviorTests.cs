namespace WinPool.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void StandardUserCannotEnterRealMode()
    {
        var controller = new WinPool.Core.ExecutionModeController(WinPool.Core.PrivilegeState.StandardUser);
        Assert.False(controller.TrySetMode(WinPool.Core.ExecutionMode.Real));
        Assert.Equal(WinPool.Core.ExecutionMode.Simulation, controller.Mode);
    }

    [Fact]
    public void AdministratorStillStartsInSimulation()
    {
        var controller = new WinPool.Core.ExecutionModeController(WinPool.Core.PrivilegeState.Administrator);
        Assert.Equal(WinPool.Core.ExecutionMode.Simulation, controller.Mode);
        Assert.True(controller.TrySetMode(WinPool.Core.ExecutionMode.Real));
    }

    [Fact]
    public void StableFallbackIdIsDeterministicAndMarkedUnstable()
    {
        var first = WinPool.Core.StableId.Create("physical", null, null, 4, "disk", 1000);
        var second = WinPool.Core.StableId.Create("physical", null, null, 4, "disk", 1000);
        Assert.False(first.IsStable);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void SystemDocumentSanitizerMasksSnapshotAndHardwareEvidence()
    {
        var snapshot = TestSnapshotFactory.Create();
        snapshot = snapshot with
        {
            PhysicalDisks =
            [
                snapshot.PhysicalDisks[0] with { MaskedSerialNumber = "SERIAL-123456" }
            ]
        };
        var raw = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "SERIAL-123456" });
        var item = new WinPool.Core.HardwareInventoryItemResult(
            "0803",
            "Disk",
            "SerialNumber",
            "序列号",
            raw,
            [
                new WinPool.Core.CollectorSourceResult(
                    "test",
                    WinPool.Core.CollectorSourceStatus.Success,
                    raw,
                    string.Empty,
                    0)
            ],
            []);
        var document = new WinPool.Core.StorageSystemDocument(
            1,
            "simulation:test",
            WinPool.Core.StorageSystemKind.Simulation,
            "Test",
            snapshot,
            new WinPool.Core.HardwareInventoryReport(1, DateTimeOffset.Now, [item], []),
            [],
            DateTimeOffset.Now);

        var sanitized = WinPool.Core.StorageSystemDocumentSanitizer.RedactSensitiveData(document);

        Assert.Contains('•', sanitized.Snapshot.PhysicalDisks[0].MaskedSerialNumber);
        Assert.All(
            sanitized.HardwareReport.Items[0].FinalValue!.Value.EnumerateArray(),
            value => Assert.Contains('•', value.GetString()!));
        Assert.All(
            sanitized.HardwareReport.Items[0].Sources[0].RawValue!.Value.EnumerateArray(),
            value => Assert.Contains('•', value.GetString()!));
    }

    [Fact]
    public void TieredPoolDoesNotDuplicatePhysicalDiskAtPoolLevel()
    {
        var snapshot = TestSnapshotFactory.Create();
        var root = WinPool.Core.TopologyProjector.Project(snapshot);
        var references = WinPool.Core.TopologyProjector.Flatten(root)
            .Where(x => x.Unit.StableId == "physical:1")
            .ToList();
        Assert.Single(references);
        Assert.True(references[0].IsReference);
    }

    [Fact]
    public void PartitionIsTheOnlyLeafUnderItsDisk()
    {
        var snapshot = TestSnapshotFactory.Create();
        var root = WinPool.Core.TopologyProjector.Project(snapshot);
        var virtualDisk = WinPool.Core.TopologyProjector.Flatten(root)
            .Single(x => x.Unit.StableId == "virtual:1");

        Assert.Contains(virtualDisk.Children, x => x.Unit.StableId == "partition:1");
        Assert.DoesNotContain(
            WinPool.Core.TopologyProjector.Flatten(root),
            x => x.Unit.Kind.ToString().Contains("Volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VirtualDiskAndPartitionClicksMapToRequiredCategories()
    {
        var snapshot = TestSnapshotFactory.Create();
        var virtualSelection = WinPool.Core.WorkspaceMapper.FromUnit(snapshot.FindUnit("virtual:1")!, snapshot);
        var partitionSelection = WinPool.Core.WorkspaceMapper.FromUnit(snapshot.FindUnit("partition:1")!, snapshot);
        Assert.Equal(WinPool.Core.WorkspaceCategory.Disk, virtualSelection.Category);
        Assert.Equal(WinPool.Core.WorkspaceCategory.Partition, partitionSelection.Category);
        Assert.Equal("partition:1", partitionSelection.StableId);
    }

    [Fact]
    public void RescanRestoresStableSelectionOrFallsBack()
    {
        var snapshot = TestSnapshotFactory.Create();
        var current = new WinPool.Core.WorkspaceSelection(WinPool.Core.WorkspaceCategory.Partition, "partition:1");
        Assert.Equal(current, WinPool.Core.WorkspaceState.Restore(snapshot, current));
        var missing = new WinPool.Core.WorkspaceSelection(WinPool.Core.WorkspaceCategory.Partition, "partition:gone");
        Assert.Equal("partition:1", WinPool.Core.WorkspaceState.Restore(snapshot, missing).StableId);
    }

    [Fact]
    public void PrimordialPoolIsProjectedAsARealPool()
    {
        var source = TestSnapshotFactory.Create();
        var primordial = source.StoragePools[0] with { FriendlyName = "Ignored", IsPrimordial = true };
        var snapshot = source with { StoragePools = [primordial], StorageTiers = [], VirtualDisks = [] };
        var root = WinPool.Core.TopologyProjector.Project(snapshot);
        var pool = Assert.Single(root.Children);
        Assert.Equal(WinPool.Core.StorageUnitKind.StoragePool, pool.Unit.Kind);
        Assert.Equal("Primordial", pool.Unit.DisplayName);
        Assert.DoesNotContain("virtual disk", root.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("virtual disk", pool.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartitionDisplayNamePreservesUnicodeAndCleansDriveLetter()
    {
        var source = TestSnapshotFactory.Create().Partitions.Single();
        var partition = source with
        {
            DriveLetter = "C:",
            FileSystemLabel = "本地磁盘\0 "
        };

        Assert.Equal("C: 本地磁盘", WinPool.Core.TopologyProjector.PartitionDisplayName(partition));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(":", "")]
    [InlineData("\0", "")]
    [InlineData(" \0: ", "")]
    [InlineData("d:", "D")]
    public void DriveLetterNormalizationRejectsInvalidPlaceholders(string source, string expected)
    {
        Assert.Equal(expected, WinPool.Core.TopologyProjector.NormalizeDriveLetter(source));
    }

    [Fact]
    public void PartitionWorkspaceOrderFollowsTopologyAndPartitionNumber()
    {
        var source = TestSnapshotFactory.Create();
        var first = source.Partitions.Single();
        var second = first with
        {
            StableId = "partition:2",
            PartitionNumber = 2,
            DriveLetter = "D",
            Path = "D:\\"
        };
        var snapshot = source with { Partitions = [second, first] };

        var ordered = WinPool.Core.TopologyProjector.OrderPartitionsForWorkspace(snapshot);

        Assert.Equal(["partition:1", "partition:2"], ordered.Select(x => x.StableId));
    }

    [Fact]
    public void GlobalNotificationsAreIndependentDeduplicatedAndDismissible()
    {
        var service = new WinPool.Core.GlobalNotificationService();
        service.PublishWarning("Warning", "One", "scan", "same");
        service.PublishWarning("Warning", "One", "scan", "same");
        service.PublishError("Error", "Two", "operation", "other");

        Assert.Equal(2, service.Notifications.Count);
        var firstId = service.Notifications[0].Id;
        service.Dismiss(firstId);
        Assert.Single(service.Notifications);
        Assert.Equal("Two", service.Notifications[0].Message);
    }

    [Fact]
    public void ElevatedRealStartupOptionRequiresAdministrator()
    {
        var admin = WinPool.Core.ApplicationStartupOptions.Parse(
            [WinPool.Core.ApplicationStartupOptions.ElevatedRealArgument],
            WinPool.Core.PrivilegeState.Administrator);
        var standard = WinPool.Core.ApplicationStartupOptions.Parse(
            [WinPool.Core.ApplicationStartupOptions.ElevatedRealArgument],
            WinPool.Core.PrivilegeState.StandardUser);
        var normalAdmin = WinPool.Core.ApplicationStartupOptions.Parse(
            [],
            WinPool.Core.PrivilegeState.Administrator);

        Assert.True(admin.EnterRealModeAfterElevation);
        Assert.False(standard.EnterRealModeAfterElevation);
        Assert.False(normalAdmin.EnterRealModeAfterElevation);
    }

    [Fact]
    public void ElevatedHandoffProcessIdIsParsedOnlyFromAValidPositiveValue()
    {
        Assert.Equal(
            42,
            WinPool.Core.ApplicationStartupOptions.GetHandoffProcessId(
                [WinPool.Core.ApplicationStartupOptions.WaitForProcessArgument, "42"]));
        Assert.Null(
            WinPool.Core.ApplicationStartupOptions.GetHandoffProcessId(
                [WinPool.Core.ApplicationStartupOptions.WaitForProcessArgument, "invalid"]));
        Assert.Null(WinPool.Core.ApplicationStartupOptions.GetHandoffProcessId([]));
    }

    [Fact]
    public void PartitionTopologySummaryOmitsClusterSize()
    {
        var root = WinPool.Core.TopologyProjector.Project(TestSnapshotFactory.Create());
        var partition = WinPool.Core.TopologyProjector.Flatten(root)
            .Single(x => x.Unit.StableId == "partition:1");

        Assert.Contains("NTFS", partition.Summary);
        Assert.DoesNotContain("4 KiB", partition.Summary);
    }

    [Fact]
    public void EqualFillFlowLayoutFillsEveryRowIncludingLastAndNarrowRows()
    {
        var rows = WinPool.Core.EqualFillFlowLayout.CreateRows(5, 500);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].Count);
        Assert.Equal((500d - 12d) / 3d, rows[0].ItemWidth, 6);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal((500d - 6d) / 2d, rows[1].ItemWidth, 6);

        var singleton = Assert.Single(WinPool.Core.EqualFillFlowLayout.CreateRows(1, 800));
        Assert.Equal(800d, singleton.ItemWidth, 6);
        var narrow = Assert.Single(WinPool.Core.EqualFillFlowLayout.CreateRows(1, 100));
        Assert.Equal(100d, narrow.ItemWidth, 6);
    }

    [Fact]
    public void CompressedDiskCardsKeepFourTierMembersOnOneRow()
    {
        var row = Assert.Single(
            WinPool.Core.EqualFillFlowLayout.CreateRows(
                itemCount: 4,
                availableWidth: 479,
                minimumItemWidth: 112,
                spacing: 6));

        Assert.Equal(4, row.Count);
        Assert.Equal((479d - 18d) / 4d, row.ItemWidth, 6);
        Assert.True(row.ItemWidth >= 112);
        Assert.True((row.ItemWidth * row.Count) + (6 * (row.Count - 1)) <= 479);
    }

    [Fact]
    public void SummariesUseExactlyTwoSpacesBetweenFields()
    {
        var summary = WinPool.Core.TopologyProjector.JoinSummary("one", "two", "three");
        Assert.Equal("one  two  three", summary);
        Assert.DoesNotContain('·', summary);
        Assert.DoesNotContain('|', summary);
    }

    [Fact]
    public void PoolWeightUsesLargestStorageLane()
    {
        var source = TestSnapshotFactory.Create();
        var pool = source.StoragePools[0] with
        {
            MemberPhysicalDiskIds = ["d1", "d2", "d3", "d4", "d5"]
        };
        var tiers = new[]
        {
            source.StorageTiers[0] with { MemberPhysicalDiskIds = ["d1"] },
            source.StorageTiers[0] with { StableId = "tier:2", MemberPhysicalDiskIds = ["d2", "d3", "d4", "d5"] }
        };
        var snapshot = source with { StoragePools = [pool], StorageTiers = tiers };

        Assert.Equal(4, WinPool.Core.TopologyProjector.CalculatePoolWeight(pool, snapshot));
    }

    [Fact]
    public void WeightedPoolLayoutProducesFiveThenFourToTwoRows()
    {
        var rows = WinPool.Core.WeightedPoolLayout.CreateRows([5, 4, 2], 1000);
        Assert.Equal(2, rows.Count);
        Assert.Equal([0], rows[0]);
        Assert.Equal([1, 2], rows[1]);

        var widths = WinPool.Core.WeightedPoolLayout.AllocateWidths([4, 2], 1000);
        Assert.Equal(2d, widths[0] / widths[1], 6);
    }

    [Fact]
    public void NetworkAndOtherGroupsAreSelectablePoolCategoryObjectsButNotPools()
    {
        var source = TestSnapshotFactory.Create();
        var network = new WinPool.Core.NetworkDiskInfo(
            "network:r", true, "R: Network", "R", "\\\\server\\share", "NTFS",
            4_000_000, 1_000_000);
        var otherDisk = new WinPool.Core.OsDiskInfo(
            "osdisk:other", "Other disk", 9, "GPT", 8_000_000,
            false, false, false, null, null);
        var otherPartition = source.Partitions[0] with
        {
            StableId = "partition:other",
            DiskNumber = 9,
            OsDiskStableId = otherDisk.StableId
        };
        var snapshot = source with
        {
            NetworkDisks = [network],
            OsDisks = source.OsDisks.Append(otherDisk).ToArray(),
            Partitions = source.Partitions.Append(otherPartition).ToArray()
        };

        var root = WinPool.Core.TopologyProjector.Project(snapshot);
        var networkGroup = root.Children.Single(x => x.Unit.Kind == WinPool.Core.StorageUnitKind.NetworkDiskGroup);
        var otherGroup = root.Children.Single(x => x.Unit.Kind == WinPool.Core.StorageUnitKind.OtherDiskGroup);

        Assert.True(networkGroup.IsSelectable);
        Assert.True(otherGroup.IsSelectable);
        Assert.Equal(
            WinPool.Core.WorkspaceCategory.Pool,
            WinPool.Core.WorkspaceMapper.FromUnit(networkGroup.Unit, snapshot).Category);
        Assert.Equal(
            WinPool.Core.WorkspaceCategory.Pool,
            WinPool.Core.WorkspaceMapper.FromUnit(otherGroup.Unit, snapshot).Category);
        Assert.NotNull(snapshot.FindUnit(networkGroup.Unit.StableId));
        Assert.NotNull(snapshot.FindUnit(otherGroup.Unit.StableId));
        Assert.StartsWith("1 pools  1 physical disks  1 virtual disks  1 network disks  ", root.Summary);
    }

    [Fact]
    public void StorageGroupIdsAreStableForTheSameComputer()
    {
        var snapshot = TestSnapshotFactory.Create();

        Assert.Equal(
            "group:network:system:test",
            WinPool.Core.TopologyProjector.NetworkGroupStableId(snapshot));
        Assert.Equal(
            "group:other:system:test",
            WinPool.Core.TopologyProjector.OtherGroupStableId(snapshot));
    }

    [Fact]
    public void StorageSystemCatalogKeepsLocalFirstAndImportedSimulationsInOrder()
    {
        var snapshot = TestSnapshotFactory.Create();
        var report = WinPool.Core.HardwareInventoryReport.Empty(DateTimeOffset.Now);
        var local = new WinPool.Core.StorageSystemDocument(
            1, "local", WinPool.Core.StorageSystemKind.Local, "Local",
            snapshot, report, [], DateTimeOffset.Now);
        var first = new WinPool.Core.StorageSystemDocument(
            1, "sim:1", WinPool.Core.StorageSystemKind.Simulation, "First",
            snapshot, report, [], DateTimeOffset.Now);
        var second = first with { Id = "sim:2", DisplayName = "Second" };
        var catalog = new WinPool.Core.StorageSystemCatalog();

        catalog.AddSimulation(first);
        catalog.ReplaceLocal(local);
        catalog.AddSimulation(second);

        Assert.Equal(["local", "sim:1", "sim:2"], catalog.Systems.Select(x => x.Id));
    }

    [Fact]
    public void SimulationOperationsRejectLocalAndPersistSnapshotChanges()
    {
        var snapshot = TestSnapshotFactory.Create();
        var report = WinPool.Core.HardwareInventoryReport.Empty(DateTimeOffset.Now);
        var local = new WinPool.Core.StorageSystemDocument(
            1, "local", WinPool.Core.StorageSystemKind.Local, "Local",
            snapshot, report, [], DateTimeOffset.Now);
        var simulation = local with
        {
            Id = "simulation",
            Kind = WinPool.Core.StorageSystemKind.Simulation
        };
        var service = new WinPool.Core.SimulationOperationService();

        var rejected = service.Apply(
            local,
            new WinPool.Core.SimulationOperationRequest(
                WinPool.Core.SimulationOperationKind.Rename,
                "pool:1",
                Name: "Changed"));
        var changed = service.Apply(
            simulation,
            new WinPool.Core.SimulationOperationRequest(
                WinPool.Core.SimulationOperationKind.Rename,
                "pool:1",
                Name: "Changed"));

        Assert.False(rejected.Succeeded);
        Assert.True(changed.Succeeded);
        Assert.Equal("Changed", changed.Document.Snapshot.StoragePools[0].FriendlyName);
    }
}
