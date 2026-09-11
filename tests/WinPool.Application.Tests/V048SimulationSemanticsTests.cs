using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class V048SimulationSemanticsTests
{
    [Fact]
    public void ConservativeCapacityKeepsTwoCopyLogicalAtMostHalfRawThenAligns()
    {
        var estimate = ConservativeCapacity.PlanLogicalUpperBound(
            [100L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024],
            resiliency: "Mirror",
            dataCopies: 2,
            interleaveBytes: 65536);
        Assert.Equal(100L * 1024 * 1024 * 1024, estimate.LogicalGrossBytes);
        Assert.Equal(estimate.LogicalGrossBytes, estimate.AlignedLogicalBytes);
        Assert.Equal(0, estimate.AlignedLogicalBytes % ConservativeCapacity.CapacityAlignmentBytes);
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
    public void DirectoryMountPathIsNotTreatedAsDriveLetter()
    {
        var snapshot = TestSnapshotFactory.Create();
        var volume = snapshot.Volumes[0] with
        {
            AccessPaths = ["C:\\", "C:\\Mount\\Data\\", "\\\\?\\Volume{abc}\\"]
        };
        snapshot = snapshot with { Volumes = [volume] };
        var document = new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:paths",
            StorageSystemKind.Simulation,
            "Paths",
            snapshot,
            HardwareInventoryReport.Empty(DateTimeOffset.UtcNow),
            [],
            DateTimeOffset.UtcNow);

        var changed = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.ChangeDriveLetter,
                snapshot.Partitions[0].StableId,
                DriveLetter: "D"));

        Assert.True(changed.Succeeded, changed.Error);
        var paths = changed.Document.Snapshot.Volumes[0].AccessPaths;
        Assert.Contains("D:\\", paths);
        Assert.Contains("C:\\Mount\\Data\\", paths);
        Assert.Contains("\\\\?\\Volume{abc}\\", paths);
        Assert.DoesNotContain("C:\\", paths);
    }

    [Fact]
    public void DissolvePlanDeletesChildrenOnceAndRemovesEmptyPoolLast()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                FileSystem: "NTFS"));
        Assert.True(created.Succeeded, created.Error);
        var pool = created.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var working = EditWorkspace.DissolvePoolInWorking(created.Document.Snapshot, pool.StableId);

        var plan = SimulationDraftPlanner.Build(created.Document.Snapshot, working);

        Assert.DoesNotContain(plan.Steps, item => item.Kind == SimulationEditKind.DissolveStoragePool);
        Assert.Equal(1, plan.Steps.Count(item => item.Kind == SimulationEditKind.DeleteVirtualDisk));
        Assert.Equal(SimulationEditKind.DeleteEmptyStoragePool, plan.Steps[^1].Kind);
        var applied = service.ApplyPlan(created.Document, plan);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.DoesNotContain(applied.Document.Snapshot.StoragePools, item => !item.IsPrimordial);
        Assert.Empty(StorageRelationshipProjector.Validate(applied.Document.Snapshot));
    }

    [Fact]
    public void ExistingEmptyPoolDraftUsesSameMirrorCapacityAsApply()
    {
        var service = new SimulationOperationService();
        var emptyPool = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CreateVirtualDisk: false));
        Assert.True(emptyPool.Succeeded, emptyPool.Error);
        var pool = emptyPool.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var working = EditWorkspace.InsertDraftVirtualDisk(
            emptyPool.Document.Snapshot, pool.StableId, "SpaceA", "Mirror", 65536);
        var draft = Assert.Single(working.VirtualDisks);
        Assert.Equal(100L * 1024 * 1024 * 1024, draft.Size);

        var applied = service.ApplyPlan(
            emptyPool.Document,
            SimulationDraftPlanner.Build(emptyPool.Document.Snapshot, working));

        Assert.True(applied.Succeeded, applied.Error);
        Assert.Equal(draft.Size, Assert.Single(applied.Document.Snapshot.VirtualDisks).Size);
    }

    [Fact]
    public void ExistingEmptyPoolCarriesExplicitAutoPartitionIntent()
    {
        var service = new SimulationOperationService();
        var emptyPool = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CreateVirtualDisk: false));
        var pool = emptyPool.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var working = EditWorkspace.InsertDraftVirtualDisk(
            emptyPool.Document.Snapshot, pool.StableId, "SpaceA", "Mirror", 65536);
        var steps = SimulationDraftPlanner.Build(emptyPool.Document.Snapshot, working).Steps
            .Select(step => step.Kind == SimulationEditKind.CreateVirtualDisk
                ? step with
                {
                    CreatePartition = true,
                    FileSystem = "ReFS",
                    AllocationUnitSize = 65536,
                    AllocatedPartitionId = "sim:partition:auto",
                    AllocatedVolumeId = "sim:volume:auto"
                }
                : step)
            .ToArray();

        var applied = service.ApplyPlan(
            emptyPool.Document,
            SimulationDraftPlanner.Precheck(emptyPool.Document.Snapshot, steps));

        Assert.True(applied.Succeeded, applied.Error);
        var volume = Assert.Single(applied.Document.Snapshot.Volumes);
        Assert.Equal("REFS", volume.FileSystem);
        Assert.Equal(65536, volume.AllocationUnitSize);
        Assert.Empty(StorageRelationshipProjector.Validate(applied.Document.Snapshot));
    }

    [Fact]
    public void UpdatePoolCannotTurnSingleDiskSimpleTierIntoMirror()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 1, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0"],
                PerformanceResiliency: "Simple",
                CreateVirtualDisk: false));
        Assert.True(created.Succeeded, created.Error);
        var pool = created.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);

        var updated = service.Apply(
            created.Document,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                pool.StableId,
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                PerformanceSizeBytes: 100L * 1024 * 1024 * 1024));

        Assert.False(updated.Succeeded);
        Assert.Contains("mirror", updated.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created.Document, updated.Document);
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

    [Fact]
    public void UpdatingPoolNamesDoesNotFormatOrMutateTheVolume()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                VirtualDiskName: "SpaceA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CreatePartition: true,
                FileSystem: "NTFS",
                AllocationUnitSize: 65536));
        Assert.True(created.Succeeded, created.Error);
        var pool = created.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var beforePartition = Assert.Single(created.Document.Snapshot.Partitions);
        var beforeVolume = Assert.Single(created.Document.Snapshot.Volumes);

        var renamed = service.Apply(
            created.Document,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                pool.StableId,
                Name: "PoolRenamed",
                VirtualDiskName: "SpaceRenamed",
                FileSystem: "REFS",
                AllocationUnitSize: 4096));

        Assert.True(renamed.Succeeded, renamed.Error);
        Assert.Equal(beforePartition, Assert.Single(renamed.Document.Snapshot.Partitions));
        Assert.Equal(beforeVolume, Assert.Single(renamed.Document.Snapshot.Volumes));
    }

    [Fact]
    public void EmptyDraftPoolCanCarryAZeroCapacityVirtualDiskPlaceholder()
    {
        var committed = Primordial(ssdCount: 1, hddCount: 0).Snapshot;
        var drafted = EditWorkspace.InsertDraftPool(committed, "PoolEmpty");
        var pool = drafted.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));

        drafted = EditWorkspace.InsertDraftVirtualDisk(drafted, pool.StableId, "Pending", "Simple", 65536);

        Assert.Equal(0, Assert.Single(drafted.VirtualDisks).Size);
        var plan = SimulationDraftPlanner.Build(committed, drafted);
        Assert.Contains(plan.DisplayItems, item => item.Decision?.Verdict != StorageRuleVerdict.Allow);
    }

    [Fact]
    public void UnchangedDraftBuildKeepsPlanAndAllocatedObjectIdsStable()
    {
        var committed = Primordial(ssdCount: 2, hddCount: 0).Snapshot;
        var working = EditWorkspace.InsertDraftPool(committed, "PoolA");
        var pool = working.StoragePools.Single(item => EditWorkspace.IsDraftPool(item.StableId));
        working = EditWorkspace.MoveDiskToPool(working, "physical:ssd0", pool.StableId);
        working = EditWorkspace.MoveDiskToPool(working, "physical:ssd1", pool.StableId);
        working = EditWorkspace.InsertDraftVirtualDisk(working, pool.StableId, "SpaceA", "Mirror", 65536);

        var first = SimulationDraftPlanner.Build(committed, working);
        var second = SimulationDraftPlanner.Build(committed, working);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.Steps.Single().AllocatedPoolId, second.Steps.Single().AllocatedPoolId);
        Assert.Equal(first.Steps.Single().AllocatedVirtualDiskId, second.Steps.Single().AllocatedVirtualDiskId);
    }

    [Fact]
    public void ShrinkingPoolRejectsPartitionsBeyondTheNewOsDiskBoundary()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CreatePartition: true));
        Assert.True(created.Succeeded, created.Error);
        var pool = created.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var partition = Assert.Single(created.Document.Snapshot.Partitions);
        var required = checked(partition.Offset + partition.Size);

        var exact = service.Apply(created.Document, new SimulationOperationRequest(
            SimulationOperationKind.UpdateStoragePool, pool.StableId, PerformanceSizeBytes: required));
        Assert.True(exact.Succeeded, exact.Error);

        var tooSmall = service.Apply(created.Document, new SimulationOperationRequest(
            SimulationOperationKind.UpdateStoragePool, pool.StableId, PerformanceSizeBytes: required - 1));
        Assert.False(tooSmall.Succeeded);
        Assert.Contains("requires at least", tooSmall.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created.Document, tooSmall.Document);
    }

    [Fact]
    public void ExistingEmptyPoolAppliesTierResizeBeforeCreatingItsFirstVirtualDisk()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "primordial",
                Name: "PoolA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror",
                PerformanceDataCopies: 2,
                CreateVirtualDisk: false));
        Assert.True(created.Succeeded, created.Error);
        var pool = created.Document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var working = EditWorkspace.InsertDraftVirtualDisk(
            created.Document.Snapshot, pool.StableId, "SpaceA", "Mirror", 65536);
        const long finalSize = 80L * 1024 * 1024 * 1024;
        working = working with
        {
            StorageTiers = working.StorageTiers.Select(item => item.PoolStableId == pool.StableId
                && EditWorkspace.NormalizeMedia(item.MediaType) == "SSD"
                ? item with { Size = finalSize, FootprintOnPool = finalSize * 2 }
                : item).ToArray()
        };

        var plan = SimulationDraftPlanner.Build(created.Document.Snapshot, working);
        var updateIndex = plan.Steps.ToList().FindIndex(item => item.Kind == SimulationEditKind.UpdateStoragePool);
        var createIndex = plan.Steps.ToList().FindIndex(item => item.Kind == SimulationEditKind.CreateVirtualDisk);
        Assert.InRange(updateIndex, 0, createIndex - 1);
        Assert.Equal(finalSize, plan.Steps[createIndex].SizeBytes);
        var applied = service.ApplyPlan(created.Document, plan);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.Equal(finalSize, Assert.Single(applied.Document.Snapshot.VirtualDisks).Size);
    }

    [Fact]
    public void DissolveMovesEachMemberToItsFinalDraftTargetOnce()
    {
        var service = new SimulationOperationService();
        var poolAResult = service.Apply(
            Primordial(ssdCount: 4, hddCount: 0),
            new SimulationOperationRequest(SimulationOperationKind.CreateTieredPool, "primordial",
                Name: "PoolA", MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror", PerformanceDataCopies: 2, CreateVirtualDisk: false));
        Assert.True(poolAResult.Succeeded, poolAResult.Error);
        var poolBResult = service.Apply(
            poolAResult.Document,
            new SimulationOperationRequest(SimulationOperationKind.CreateTieredPool, "primordial",
                Name: "PoolB", MemberDiskIds: ["physical:ssd2", "physical:ssd3"],
                PerformanceResiliency: "Mirror", PerformanceDataCopies: 2, CreateVirtualDisk: false));
        Assert.True(poolBResult.Succeeded, poolBResult.Error);
        var poolA = poolBResult.Document.Snapshot.StoragePools.Single(item => item.FriendlyName == "PoolA");
        var poolB = poolBResult.Document.Snapshot.StoragePools.Single(item => item.FriendlyName == "PoolB");
        var working = EditWorkspace.DissolvePoolInWorking(poolBResult.Document.Snapshot, poolA.StableId);
        working = EditWorkspace.MoveDiskToPool(working, "physical:ssd0", poolB.StableId);

        var plan = SimulationDraftPlanner.Build(poolBResult.Document.Snapshot, working);
        var move = Assert.Single(plan.Steps, item => item.Kind == SimulationEditKind.MovePhysicalDisk
            && item.TargetProviderKey == "physical:ssd0");
        Assert.Equal(poolB.StableId, move.Name);
        Assert.Contains("SSD 0", plan.DisplayItems.Single(item => item.Request == move).Title);
        Assert.Contains("PoolB", plan.DisplayItems.Single(item => item.Request == move).Title);
        var applied = service.ApplyPlan(poolBResult.Document, plan);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.Equal(poolB.StableId, applied.Document.Snapshot.PhysicalDisks.Single(item => item.StableId == "physical:ssd0").PoolStableId);
        Assert.DoesNotContain(applied.Document.Snapshot.StoragePools, item => item.StableId == poolA.StableId);
    }

    [Fact]
    public void DissolvePlanKeepsObjectNamesAndActualDataLossOnParentAndChild()
    {
        var service = new SimulationOperationService();
        var created = service.Apply(
            Primordial(ssdCount: 2, hddCount: 0),
            new SimulationOperationRequest(SimulationOperationKind.CreateTieredPool, "primordial",
                Name: "PoolA", VirtualDiskName: "SpaceA",
                MemberDiskIds: ["physical:ssd0", "physical:ssd1"],
                PerformanceResiliency: "Mirror", PerformanceDataCopies: 2,
                CreatePartition: true, FileSystem: "NTFS"));
        Assert.True(created.Succeeded, created.Error);
        var partition = Assert.Single(created.Document.Snapshot.Partitions);
        var withData = created.Document.Snapshot with
        {
            Partitions = created.Document.Snapshot.Partitions.Select(item => item.StableId == partition.StableId
                ? item with { SizeRemaining = item.Size - 1 }
                : item).ToArray()
        };
        var pool = withData.StoragePools.Single(item => !item.IsPrimordial);
        var working = EditWorkspace.DissolvePoolInWorking(withData, pool.StableId);

        var plan = SimulationDraftPlanner.Build(withData, working);
        var parent = Assert.Single(plan.DisplayItems, item => item.Request.Kind == SimulationEditKind.DissolveStoragePool);
        var deletePartition = Assert.Single(plan.DisplayItems, item => item.Request.Kind == SimulationEditKind.DeletePartition);
        var deleteVdisk = Assert.Single(plan.DisplayItems, item => item.Request.Kind == SimulationEditKind.DeleteVirtualDisk);
        Assert.True(parent.CausesDataLoss);
        Assert.True(deletePartition.CausesDataLoss);
        Assert.True(deleteVdisk.CausesDataLoss);
        Assert.Contains("partition", deletePartition.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SpaceA", deleteVdisk.Title);
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
                100L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, i + 1,
                false, false, false, false, "pool:primordial"));
            osDisks.Add(new OsDiskInfo(
                $"osdisk:ssd{i}", $"SSD {i}", i + 1, "RAW", 100L * 1024 * 1024 * 1024, false, false, false, id, null));
        }

        for (var i = 0; i < hddCount; i++)
        {
            var id = $"physical:hdd{i}";
            primordialIds.Add(id);
            disks.Add(new PhysicalDiskInfo(
                id, true, $"HDD {i}", "Model", "HD0001", "SATA", "HDD",
                100L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 20 + i,
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
