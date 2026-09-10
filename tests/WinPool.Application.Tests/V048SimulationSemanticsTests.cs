using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class V048SimulationSemanticsTests
{
    [Fact]
    public void ConservativeCapacityKeepsTwoCopyLogicalAtMostHalfRawThenReserve()
    {
        var estimate = ConservativeCapacity.PlanLogicalUpperBound(
            [3_000_000_000, 3_000_000_000],
            dataCopies: 2,
            interleaveBytes: 65536);
        Assert.True(estimate.LogicalGrossBytes <= 3_000_000_000);
        Assert.True(estimate.AlignedLogicalBytes < estimate.LogicalGrossBytes);
        Assert.Equal(0, estimate.AlignedLogicalBytes % 65536);
        Assert.Equal(CapacitySourceKind.SimulatedEstimate, estimate.Source);
    }

    [Fact]
    public void RetiredAndHotSpareDoNotContributeDataCapacity()
    {
        Assert.False(PhysicalDiskUsage.ContributesDataCapacity(PhysicalDiskUsage.Retired));
        Assert.False(PhysicalDiskUsage.ContributesDataCapacity(PhysicalDiskUsage.HotSpare));
        Assert.True(PhysicalDiskUsage.ContributesDataCapacity(PhysicalDiskUsage.AutoSelect));
    }

    [Fact]
    public void SingleDiskMirrorIsRejected()
    {
        var document = Primordial(ssdCount: 1, hddCount: 0);
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2));
        Assert.False(result.Succeeded);
        Assert.Contains("mirror", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTieredPoolLinksTiersAndVirtualDisk()
    {
        var document = Primordial(ssdCount: 2, hddCount: 2);
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                VirtualDiskName: "SpaceA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1", "physical:hdd0", "physical:hdd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CapacityResiliency: "Simple",
                FileSystem: "NTFS",
                AllocationUnitSize: 65536));
        Assert.True(result.Succeeded, result.Error);
        var vdisk = Assert.Single(result.Document.Snapshot.VirtualDisks);
        Assert.NotEmpty(vdisk.TierStableIds);
        Assert.All(
            result.Document.Snapshot.StorageTiers.Where(item => item.PoolStableId == vdisk.PoolStableId),
            tier => Assert.Equal(vdisk.StableId, tier.VirtualDiskStableId));
        Assert.True(vdisk.Size * 2 <= 4_000_000_000L || vdisk.SizeSource == CapacitySourceKind.SimulatedEstimate);
        Assert.Contains(result.Document.Snapshot.Volumes, item => item.FileSystem == "NTFS");
        Assert.Empty(StorageRelationshipProjector.Validate(result.Document.Snapshot));
    }

    [Fact]
    public void EmptyMemberDraftPoolPlanIsRejectedWithReason()
    {
        var committed = Primordial(ssdCount: 1, hddCount: 0).Snapshot;
        var drafted = EditWorkspace.InsertDraftPool(committed, "PoolEmpty");
        var plan = SimulationDraftPlanner.Build(committed, drafted);
        Assert.Contains(plan.Steps, step => step.Kind == SimulationEditKind.CreateTieredPool);
        var applied = new SimulationOperationService().ApplyPlan(
            Primordial(ssdCount: 1, hddCount: 0),
            plan);
        Assert.False(applied.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(applied.Error));
        Assert.Contains("physical disk", applied.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DraftPlannerDoesNotEmitDeleteForNewlyCreatedVirtualDisk()
    {
        var committed = Primordial(ssdCount: 2, hddCount: 0).Snapshot;
        var drafted = EditWorkspace.InsertDraftPool(committed, "PoolA");
        var draft = drafted.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        drafted = EditWorkspace.MoveDiskToPool(drafted, "physical:ssd0", draft.StableId);
        drafted = EditWorkspace.MoveDiskToPool(drafted, "physical:ssd1", draft.StableId);
        drafted = EditWorkspace.InsertDraftVirtualDisk(drafted, draft.StableId, "PoolA", "Mirror", 65536);
        var plan = SimulationDraftPlanner.Build(committed, drafted);
        Assert.Contains(plan.Steps, step => step.Kind == SimulationEditKind.CreateTieredPool);
        Assert.DoesNotContain(plan.Steps, step => step.Kind == SimulationEditKind.DeleteVirtualDisk);
        var applied = new SimulationOperationService().ApplyPlan(Primordial(ssdCount: 2, hddCount: 0), plan);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.Single(applied.Document.Snapshot.VirtualDisks);
    }

    [Fact]
    public void PartitionOnlyDraftIsPlannedIndependentlyOfPoolProperties()
    {
        var created = new SimulationOperationService().Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                FileSystem: "NTFS",
                AllocationUnitSize: 65536));
        Assert.True(created.Succeeded, created.Error);
        var committed = created.Document.Snapshot;
        var partition = Assert.Single(committed.Partitions, item => item.Type == "Primary");
        var volume = committed.VolumeForPartition(partition.StableId)!;
        var working = committed with
        {
            Volumes = committed.Volumes
                .Select(item => item.StableId == volume.StableId
                    ? item with { FileSystemLabel = "Renamed" }
                    : item)
                .ToArray()
        };
        working = StorageRelationshipProjector.ProjectPartitionVolumes(working);
        var plan = SimulationDraftPlanner.Build(committed, working);
        Assert.Contains(
            plan.Steps,
            step => step.Kind is SimulationEditKind.FormatPartition or SimulationEditKind.Rename);
        Assert.DoesNotContain(plan.Steps, step => step.Kind == SimulationEditKind.UpdateStoragePool);
        var applied = new SimulationOperationService().ApplyPlan(created.Document, plan);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.Contains(applied.Document.Snapshot.Volumes, item => item.FileSystemLabel == "Renamed");
    }

    [Fact]
    public void VolumeAccessPathsSurviveProjection()
    {
        var snapshot = TestSnapshotFactory.Create();
        Assert.NotNull(snapshot.VolumeForPartition("partition:1"));
        Assert.Equal("C", snapshot.DriveLetterOf(snapshot.Partitions[0]));
        Assert.Contains("C:\\", snapshot.AccessPathsOf(snapshot.Partitions[0]));
        var rebuilt = StorageRelationshipProjector.Rebuild(snapshot);
        Assert.Equal("C", rebuilt.DriveLetterOf(rebuilt.Partitions[0]));
        Assert.Contains(rebuilt.Relationships, item => item.RelationshipKind == "HostsVolume");
    }

    [Fact]
    public void MbrInitializeIsRejected()
    {
        var result = new SimulationOperationService().Apply(
            Primordial(ssdCount: 1, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.InitializeDisk,
                "osdisk:ssd0",
                Name: "MBR"));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void OperationMatrixCoversEveryEditKind()
    {
        var kinds = Enum.GetValues<SimulationOperationKind>();
        var matrix = StorageEditRules.OperationMatrix();
        Assert.All(kinds, kind => Assert.Contains(matrix, item => item.Kind == kind));
    }

    private static StorageSystemDocument Primordial(int ssdCount, int hddCount)
    {
        var disks = new List<PhysicalDiskInfo>();
        var primordialIds = new List<string>();
        var osDisks = new List<OsDiskInfo>();
        for (var i = 0; i < ssdCount; i++)
        {
            var id = $"physical:ssd{i}";
            primordialIds.Add(id);
            disks.Add(new PhysicalDiskInfo(
                id, true, $"SSD {i}", "Model", "SS0001", "SATA", "SSD",
                2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, i + 1,
                false, false, false, false, "pool:primordial"));
            osDisks.Add(new OsDiskInfo(
                $"osdisk:ssd{i}", $"SSD {i}", i + 1, "RAW", 2_000_000_000, false, false, false, id, null));
        }

        for (var i = 0; i < hddCount; i++)
        {
            var id = $"physical:hdd{i}";
            primordialIds.Add(id);
            disks.Add(new PhysicalDiskInfo(
                id, true, $"HDD {i}", "Model", "HD0001", "SATA", "HDD",
                2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 20 + i,
                false, false, false, false, "pool:primordial"));
        }

        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            disks,
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    disks.Sum(item => item.Size), 0, "subsystem:1", primordialIds)
            ],
            [],
            [],
            osDisks,
            [],
            [],
            [],
            [],
            []);
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:test",
            StorageSystemKind.Simulation,
            "Test",
            snapshot,
            HardwareInventoryReport.Empty(DateTimeOffset.UtcNow),
            [],
            DateTimeOffset.UtcNow);
    }
}
