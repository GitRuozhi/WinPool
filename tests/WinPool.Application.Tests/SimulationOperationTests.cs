using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationOperationTests
{
    private static StorageSystemDocument CreateDocument()
    {
        var primordialDisks = new[]
        {
            new PhysicalDiskInfo(
                "physical:p1", true, "Free Disk One", "Model", "AA0001", "SATA", "SSD",
                100L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                false, false, false, false, "pool:primordial"),
            new PhysicalDiskInfo(
                "physical:p2", true, "Free Disk Two", "Model", "AA0002", "SATA", "HDD",
                200L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 6,
                false, false, false, false, "pool:primordial")
        };
        var primordial = new StoragePoolInfo(
            "pool:primordial", true, "Primordial", true, "Healthy", "OK",
            300L * 1024 * 1024 * 1024, 0, "subsystem:1", ["physical:p1", "physical:p2"]);
        var osDisk = new OsDiskInfo(
            "osdisk:5", "Free Disk One", 5, "GPT", 100L * 1024 * 1024 * 1024, false, false, false, "physical:p1", null);
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            primordialDisks,
            [primordial],
            [],
            [],
            [osDisk],
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
            HardwareInventoryReport.Empty(DateTimeOffset.Now),
            [],
            DateTimeOffset.Now);
    }

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationOperationRequest request)
    {
        var result = new SimulationOperationService().Apply(document, request);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Commands);
        return result.Document;
    }

    [Fact]
    public void InitializeDiskCreatesMsrAndClearsPartitions()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 500_000_000));
        Assert.Single(document.Snapshot.Partitions);

        document = document with
        {
            Snapshot = document.Snapshot with
            {
                OsDisks = document.Snapshot.OsDisks
                    .Select(item => item.StableId == "osdisk:5"
                        ? item with { PartitionStyle = "RAW" }
                        : item)
                    .ToArray()
            }
        };

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.InitializeDisk,
            "osdisk:5",
            Name: "GPT",
            CreateMsr: true));

        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("MicrosoftReserved", partition.Type);
        Assert.Equal(16 * 1024 * 1024, partition.Size);
        Assert.Equal("GPT", document.Snapshot.OsDisks.Single(x => x.StableId == "osdisk:5").PartitionStyle);
    }

    [Fact]
    public void CreatePoolVirtualDiskAndPartitionChainProducesUsableVolume()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreateStoragePool,
            "pool:primordial",
            Name: "Pool03",
            MemberDiskIds: ["physical:p1"]));

        var pool = Assert.Single(document.Snapshot.StoragePools, x => !x.IsPrimordial);
        Assert.Equal("Pool03", pool.FriendlyName);
        Assert.Equal(["physical:p1"], pool.MemberPhysicalDiskIds);
        Assert.DoesNotContain(
            "physical:p1",
            document.Snapshot.StoragePools.Single(x => x.IsPrimordial).MemberPhysicalDiskIds);

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreateVirtualDisk,
            pool.StableId,
            Name: "Pool03",
            Resiliency: "Simple",
            InterleaveBytes: 65536,
            AllocationUnitSize: 65536));
        var vdisk = Assert.Single(document.Snapshot.VirtualDisks);
        Assert.Equal(65536, vdisk.Interleave);

        var osDisk = Assert.Single(
            document.Snapshot.OsDisks, x => x.VirtualDiskStableId == vdisk.StableId);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            osDisk.StableId));
        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("BasicData", partition.Type);

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.FormatPartition,
            partition.StableId,
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var formatted = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("NTFS", formatted.FileSystem);
        Assert.Equal(65536, formatted.AllocationUnitSize);
    }

    [Fact]
    public void ShrinkBelowUsedSpaceIsRejected()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 500_000_000,
            FileSystem: "NTFS"));
        var partition = Assert.Single(document.Snapshot.Partitions);
        var usedPartition = partition with { SizeRemaining = 100_000_000 };
        document = document with
        {
            Snapshot = document.Snapshot with { Partitions = [usedPartition] }
        };

        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.ShrinkPartition,
                usedPartition.StableId,
                SizeBytes: 50_000_000));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void FindingInspectorFlagsBusyPoolAndMbr()
    {
        var snapshot = CreateDocument().Snapshot with
        {
            StoragePools =
            [
                CreateDocument().Snapshot.StoragePools[0],
                new StoragePoolInfo(
                    "pool:busy", true, "Busy", false, "Healthy", "OK",
                    4_000_000_000L, 1_000_000_000, "subsystem:1", ["physical:p2"])
            ],
            StorageTiers =
            [
                new StorageTierInfo(
                    "tier:ssd1", true, "SSD1", "SSD", "Simple", 100_000_000, 100_000_000,
                    "pool:busy", null, ["physical:p2"]),
                new StorageTierInfo(
                    "tier:ssd2", true, "SSD2", "SSD", "Simple", 100_000_000, 100_000_000,
                    "pool:busy", null, ["physical:p2"])
            ],
            VirtualDisks =
            [
                new VirtualDiskInfo(
                    "vdisk:1", true, "V1", "Healthy", "OK", "Simple", "Fixed",
                    1, 65536, 500_000_000, 500_000_000, "pool:busy", [], [7]),
                new VirtualDiskInfo(
                    "vdisk:2", true, "V2", "Healthy", "OK", "Simple", "Fixed",
                    1, 65536, 500_000_000, 500_000_000, "pool:busy", [], [8])
            ],
            OsDisks =
            [
                CreateDocument().Snapshot.OsDisks[0] with { PartitionStyle = "MBR" }
            ]
        };

        var findings = StorageFindingInspector.Evaluate(snapshot);
        Assert.Contains(findings, x => x.Kind == StorageFindingKind.MultiplePerformanceTiers);
        Assert.Contains(findings, x => x.Kind == StorageFindingKind.MultipleVirtualDisks);
        Assert.Contains(findings, x => x.Kind == StorageFindingKind.MbrDisk);
        Assert.DoesNotContain(findings, x => x.Kind == StorageFindingKind.MultipleCapacityTiers);
    }

    [Fact]
    public void CreateTieredPoolCreatesTiersVirtualDiskAndFormattedPartition()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            PerformanceResiliency: "Simple",
            PerformanceInterleaveBytes: 65536,
            PerformanceDataCopies: 1,
            CapacityResiliency: "Simple",
            CapacityInterleaveBytes: 65536,
            CapacityColumns: 1,
            CapacityToleratedFailures: 1,
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = Assert.Single(document.Snapshot.StoragePools, item => !item.IsPrimordial);
        Assert.Equal("PoolA", pool.FriendlyName);
        Assert.Contains(document.Snapshot.StorageTiers, tier =>
            tier.PoolStableId == pool.StableId && tier.MediaType == "SSD");
        Assert.Contains(document.Snapshot.StorageTiers, tier =>
            tier.PoolStableId == pool.StableId && tier.MediaType == "HDD");
        var vdisk = Assert.Single(document.Snapshot.VirtualDisks);
        Assert.Equal("SpaceA", vdisk.FriendlyName);
        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("NTFS", partition.FileSystem);
        Assert.Equal(65536, partition.AllocationUnitSize);
    }

    [Fact]
    public void CreateTieredPoolCanSkipUserPartition()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            PerformanceResiliency: "Simple",
            CapacityResiliency: "Simple",
            FileSystem: "NTFS",
            AllocationUnitSize: 65536,
            CreatePartition: false));
        var pool = Assert.Single(document.Snapshot.StoragePools, item => !item.IsPrimordial);
        Assert.Single(document.Snapshot.VirtualDisks);
        Assert.Empty(document.Snapshot.Partitions);
        Assert.Contains(document.Snapshot.OsDisks, disk => disk.VirtualDiskStableId is not null);
    }

    [Fact]
    public void MovePhysicalDiskLeavesSourceTierAndEntersMatchingTargetTier()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.MovePhysicalDisk,
            "physical:p2",
            Name: pool.StableId));
        var hddTier = document.Snapshot.StorageTiers.Single(tier =>
            tier.PoolStableId == pool.StableId && tier.MediaType == "HDD");
        Assert.Contains("physical:p2", hddTier.MemberPhysicalDiskIds);
        Assert.Contains(
            "physical:p2",
            document.Snapshot.StoragePools.Single(item => item.StableId == pool.StableId).MemberPhysicalDiskIds);
    }

    [Fact]
    public void MovePhysicalDiskOntoSamePoolAssignsUnallocatedDiskToMatchingTier()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.EvictPhysicalDiskFromTiers,
            "physical:p1"));
        Assert.False(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.MovePhysicalDisk,
            "physical:p1",
            Name: pool.StableId));
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        var ssdTier = document.Snapshot.StorageTiers.Single(tier =>
            tier.PoolStableId == pool.StableId && tier.MediaType == "SSD");
        Assert.Contains("physical:p1", ssdTier.MemberPhysicalDiskIds);
        Assert.Equal(
            pool.StableId,
            document.Snapshot.PhysicalDisks.Single(item => item.StableId == "physical:p1").PoolStableId);
    }

    [Fact]
    public void EvictPhysicalDiskFromTiersKeepsPoolMembership()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.EvictPhysicalDiskFromTiers,
            "physical:p1"));
        Assert.False(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        Assert.Contains(
            "physical:p1",
            document.Snapshot.StoragePools.Single(item => item.StableId == pool.StableId).MemberPhysicalDiskIds);
        Assert.Equal(
            pool.StableId,
            document.Snapshot.PhysicalDisks.Single(item => item.StableId == "physical:p1").PoolStableId);
    }

    [Fact]
    public void DissolveStoragePoolReturnsDisksToPrimordial()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS"));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.DissolveStoragePool,
            pool.StableId));
        Assert.DoesNotContain(document.Snapshot.StoragePools, item => !item.IsPrimordial);
        Assert.Empty(document.Snapshot.VirtualDisks);
        Assert.Empty(document.Snapshot.StorageTiers);
        var primordial = document.Snapshot.StoragePools.Single(item => item.IsPrimordial);
        Assert.Contains("physical:p1", primordial.MemberPhysicalDiskIds);
        Assert.Contains("physical:p2", primordial.MemberPhysicalDiskIds);
        // Freed member disks regain an uninitialized OS-disk view so the
        // Disk partition editor can initialize and partition them.
        var freed = document.Snapshot.OsDisks.Single(item => item.PhysicalDiskStableId == "physical:p2");
        Assert.Equal("RAW", freed.PartitionStyle);
    }

    [Fact]
    public void MovingAMemberBackToPrimordialRestoresItsOsDiskView()
    {
        var document = Apply(CreateDocument(), new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS"));
        var primordial = document.Snapshot.StoragePools.Single(item => item.IsPrimordial);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.MovePhysicalDisk,
            "physical:p2",
            Name: primordial.StableId));
        Assert.Equal(
            primordial.StableId,
            document.Snapshot.PhysicalDisks.Single(item => item.StableId == "physical:p2").PoolStableId);
        var restored = document.Snapshot.OsDisks.Single(item => item.PhysicalDiskStableId == "physical:p2");
        Assert.Equal("RAW", restored.PartitionStyle);
    }

    [Fact]
    public void UpdateStoragePoolRejectsMultipleVirtualDisks()
    {
        var document = CreateDocument();
        var busy = CreateBusyPoolDocument();
        var result = new SimulationOperationService().Apply(
            busy,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                "pool:busy",
                Name: "Renamed"));
        Assert.False(result.Succeeded);
        Assert.Contains("more than one virtual disk", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteVirtualDiskRemovesOneVirtualDiskAndKeepsTheOther()
    {
        var document = CreateBusyPoolDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.DeleteVirtualDisk,
            "vdisk:2"));
        var pool = document.Snapshot.StoragePools.Single(item => item.StableId == "pool:busy");
        var remaining = Assert.Single(document.Snapshot.VirtualDisks, item => item.PoolStableId == "pool:busy");
        Assert.Equal("vdisk:1", remaining.StableId);
        Assert.Equal(500_000_000, pool.AllocatedSize);
    }

    private static StorageSystemDocument CreateBusyPoolDocument()
    {
        var document = CreateDocument();
        return document with
        {
            Snapshot = document.Snapshot with
            {
                StoragePools =
                [
                    document.Snapshot.StoragePools[0],
                    new StoragePoolInfo(
                        "pool:busy", true, "Busy", false, "Healthy", "OK",
                        4_000_000_000L, 1_000_000_000, "subsystem:1", ["physical:p2"])
                ],
                VirtualDisks =
                [
                    new VirtualDiskInfo(
                        "vdisk:1", true, "V1", "Healthy", "OK", "Simple", "Fixed",
                        1, 65536, 500_000_000, 500_000_000, "pool:busy", [], [7]),
                    new VirtualDiskInfo(
                        "vdisk:2", true, "V2", "Healthy", "OK", "Simple", "Fixed",
                        1, 65536, 500_000_000, 500_000_000, "pool:busy", [], [8])
                ]
            }
        };
    }
}

public sealed class SetDiskUsageTests
{
    private static StorageSystemDocument CreateDocument()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(
                "physical:p1", true, "Free Disk One", "Model", "AA0001", "SATA", "SSD",
                100L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                false, false, false, false, "pool:primordial"),
            new PhysicalDiskInfo(
                "physical:p2", true, "Free Disk Two", "Model", "AA0002", "SATA", "HDD",
                200L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 6,
                false, false, false, false, "pool:primordial")
        };
        var primordial = new StoragePoolInfo(
            "pool:primordial", true, "Primordial", true, "Healthy", "OK",
            300L * 1024 * 1024 * 1024, 0, "subsystem:1", ["physical:p1", "physical:p2"]);
        var osDisk = new OsDiskInfo(
            "osdisk:5", "Free Disk One", 5, "RAW", 100L * 1024 * 1024 * 1024, false, false, false, "physical:p1", null);
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            disks,
            [primordial],
            [],
            [],
            [osDisk],
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
            HardwareInventoryReport.Empty(DateTimeOffset.Now),
            [],
            DateTimeOffset.Now);
    }

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationOperationRequest request)
    {
        var result = new SimulationOperationService().Apply(document, request);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Commands);
        return result.Document;
    }

    [Fact]
    public void SetDiskUsagePersistsLayerRolesOnACommittedPool()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "pool:primordial",
            Name: "Pool01",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            VirtualDiskName: "Pool01"));
        var pool = document.Snapshot.StoragePools.First(item => item.FriendlyName == "Pool01");
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.SetDiskUsage,
            "physical:p1",
            Name: "Retired"));
        var retiredDisk = document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p1");
        Assert.True(retiredDisk.IsRetired);
        Assert.False(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p2"));

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.SetDiskUsage,
            "physical:p1",
            Name: "HotSpare"));
        var hotDisk = document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p1");
        Assert.False(hotDisk.IsRetired);
        Assert.True(hotDisk.IsHotSpare);

        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.SetDiskUsage,
            "physical:p1",
            Name: string.Empty));
        var clearDisk = document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p1");
        Assert.False(clearDisk.IsRetired);
        Assert.False(clearDisk.IsHotSpare);
        Assert.Equal(pool.StableId, clearDisk.PoolStableId);
    }

    [Fact]
    public void SetDiskUsageRefusesPrimordialMembersAndUnknownTargets()
    {
        var document = CreateDocument();
        var primordialResult = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskUsage,
                "physical:p1",
                Name: "Retired"));
        Assert.False(primordialResult.Succeeded);
        Assert.Contains("Primordial", primordialResult.Error, StringComparison.OrdinalIgnoreCase);

        var missing = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskUsage,
                "physical:missing",
                Name: "Retired"));
        Assert.False(missing.Succeeded);

        var invalid = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskUsage,
                "physical:p1",
                Name: "Journal"));
        Assert.False(invalid.Succeeded);
    }

    [Fact]
    public void SetDiskUsageClearsPageFileRoleAfterConfirmationGate()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreateTieredPool,
            "pool:primordial",
            Name: "Pool01",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            VirtualDiskName: "Pool01"));
        var withRole = document.Snapshot with
        {
            PhysicalDisks = document.Snapshot.PhysicalDisks
                .Select(item => item.StableId == "physical:p2" ? item with { IsPageFile = true } : item)
                .ToArray()
        };
        document = document with { Snapshot = withRole };
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.SetDiskUsage,
                "physical:p2",
                Name: "HotSpare"));
        Assert.True(result.Succeeded, result.Error);
        var disk = result.Document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p2");
        Assert.True(disk.IsHotSpare);
        Assert.False(disk.IsPageFile);
    }
}

public sealed class CreateTieredPoolSkipVdiskTests
{
    private static StorageSystemDocument CreateDocument()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(
                "physical:p1", true, "Free Disk One", "Model", "AA0001", "SATA", "SSD",
                100L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                false, false, false, false, "pool:primordial"),
            new PhysicalDiskInfo(
                "physical:p2", true, "Free Disk Two", "Model", "AA0002", "SATA", "HDD",
                200L * 1024 * 1024 * 1024, 512, 4096, "Healthy", "OK", true, string.Empty, 6,
                false, false, false, false, "pool:primordial")
        };
        var primordial = new StoragePoolInfo(
            "pool:primordial", true, "Primordial", true, "Healthy", "OK",
            300L * 1024 * 1024 * 1024, 0, "subsystem:1", ["physical:p1", "physical:p2"]);
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            disks,
            [primordial],
            [],
            [],
            [],
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
            HardwareInventoryReport.Empty(DateTimeOffset.Now),
            [],
            DateTimeOffset.Now);
    }

    [Fact]
    public void CreateTieredPoolCanSkipTheVirtualDisk()
    {
        var document = CreateDocument();
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.CreateTieredPool,
                "pool:primordial",
                Name: "Pool01",
                MemberDiskIds: ["physical:p1", "physical:p2"],
                CreateVirtualDisk: false));
        Assert.True(result.Succeeded, result.Error);
        var pool = result.Document.Snapshot.StoragePools.First(item => item.FriendlyName == "Pool01");
        Assert.DoesNotContain(result.Document.Snapshot.VirtualDisks, item => item.PoolStableId == pool.StableId);
        Assert.Contains(result.Document.Snapshot.StorageTiers, item => item.PoolStableId == pool.StableId);
        Assert.Empty(result.Document.Snapshot.Partitions);
    }
}

public sealed class UpdateStoragePoolSizeTests
{
    private static StorageSystemDocument CreateDocument()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(
                "physical:p1", true, "Disk A", "M", "S1", "SATA", "SSD",
                2_000_000_000_000, 512, 4096, "Healthy", "OK", false, string.Empty, 1,
                false, false, false, false, "pool:1"),
            new PhysicalDiskInfo(
                "physical:p2", true, "Disk B", "M", "S2", "SATA", "HDD",
                4_000_000_000_000, 512, 4096, "Healthy", "OK", false, string.Empty, 2,
                false, false, false, false, "pool:1")
        };
        var pool = new StoragePoolInfo(
            "pool:1", true, "Pool01", false, "Healthy", "OK",
            6_000_000_000_000, 5_000_000_000_000, "subsystem:1",
            ["physical:p1", "physical:p2"]);
        var ssdTier = new StorageTierInfo(
            "pool:1:tier:ssd", true, "Performance", "SSD", "Simple",
            2_000_000_000_000, 2_000_000_000_000, "pool:1", null,
            ["physical:p1"], null, 65536, 1, 0);
        var hddTier = new StorageTierInfo(
            "pool:1:tier:hdd", true, "Capacity", "HDD", "Parity",
            4_000_000_000_000, 4_000_000_000_000, "pool:1", null,
            ["physical:p2"], 1, 65536, 1, 1);
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows", "10.0", "19045", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            disks,
            [pool],
            [ssdTier, hddTier],
            [],
            [],
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
            HardwareInventoryReport.Empty(DateTimeOffset.Now),
            [],
            DateTimeOffset.Now);
    }

    private static StorageSystemDocument WithStoredData(StorageSystemDocument document)
    {
        const string virtualDiskId = "virtual:1";
        const string osDiskId = "osdisk:3";
        var tiers = document.Snapshot.StorageTiers
            .Select(item => item with
            {
                Size = item.MediaType == "SSD" ? 1_000_000_000_000 : item.Size,
                FootprintOnPool = item.MediaType == "SSD" ? 1_000_000_000_000 : item.FootprintOnPool,
                VirtualDiskStableId = virtualDiskId
            })
            .ToArray();
        var virtualDisk = new VirtualDiskInfo(
            virtualDiskId, true, "Virtual01", "Healthy", "OK", "Simple", "Fixed",
            1, 65536, 5_000_000_000_000, 5_000_000_000_000, "pool:1",
            tiers.Select(item => item.StableId).ToArray(), [3]);
        var osDisk = new OsDiskInfo(
            osDiskId, "Virtual01", 3, "GPT", virtualDisk.Size,
            false, false, false, null, virtualDiskId);
        var partition = new PartitionInfo(
            "partition:1", true, 3, 1, "Basic", 1_048_576, 900_000_000_000,
            false, false, "D", "Data", "NTFS", 4096, 400_000_000_000,
            "Healthy", "OK", "D:\\", osDiskId);
        return document with
        {
            Snapshot = document.Snapshot with
            {
                StorageTiers = tiers,
                VirtualDisks = [virtualDisk],
                OsDisks = [osDisk],
                Partitions = [partition]
            }
        };
    }

    [Fact]
    public void UpdateStoragePoolResizesThePerformanceTier()
    {
        var document = CreateDocument();
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                "pool:1",
                Name: "Pool01",
                PerformanceSizeBytes: 1_500_000_000_000));
        Assert.True(result.Succeeded, result.Error);
        var tier = result.Document.Snapshot.StorageTiers.Single(item => item.StableId == "pool:1:tier:ssd");
        Assert.Equal(1_500_000_000_000, tier.Size);
        var hdd = result.Document.Snapshot.StorageTiers.Single(item => item.StableId == "pool:1:tier:hdd");
        Assert.Equal(4_000_000_000_000, hdd.Size);
    }

    [Fact]
    public void UpdateStoragePoolMaximumModeUsesTheSharedCapacityEstimate()
    {
        var document = CreateDocument();
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                "pool:1",
                Name: "Pool01",
                PerformanceSizeBytes: 1,
                PerformanceUseMaximum: true));

        Assert.True(result.Succeeded, result.Error);
        var tier = result.Document.Snapshot.StorageTiers.Single(item => item.StableId == "pool:1:tier:ssd");
        var expected = ConservativeCapacity.PlanLogicalUpperBound(
            [2_000_000_000_000], "Simple").AlignedLogicalBytes;
        Assert.Equal(expected, tier.Size);
        Assert.Equal(expected, tier.FootprintOnPool);
    }

    [Fact]
    public void UpdateStoragePoolMaximumModeCanExpandATierContainingData()
    {
        var document = WithStoredData(CreateDocument());
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationOperationRequest(
                SimulationOperationKind.UpdateStoragePool,
                "pool:1",
                Name: "Pool01",
                PerformanceSizeBytes: 1,
                PerformanceUseMaximum: true));

        Assert.True(result.Succeeded, result.Error);
        var tier = result.Document.Snapshot.StorageTiers.Single(item => item.StableId == "pool:1:tier:ssd");
        Assert.True(tier.Size > 1_000_000_000_000);
        Assert.Equal(tier.Size + 4_000_000_000_000,
            result.Document.Snapshot.VirtualDisks.Single().Size);
        Assert.Equal(result.Document.Snapshot.VirtualDisks.Single().Size,
            result.Document.Snapshot.OsDisks.Single().Size);
    }

    [Fact]
    public void UpdateStoragePoolStillBlocksManualCapacityAndLayoutChangesWhenDataExists()
    {
        var document = WithStoredData(CreateDocument());
        var service = new SimulationOperationService();
        var manualRequest = new SimulationOperationRequest(
            SimulationOperationKind.UpdateStoragePool,
            "pool:1",
            PerformanceSizeBytes: 1_500_000_000_000);
        var layoutRequest = new SimulationOperationRequest(
            SimulationOperationKind.UpdateStoragePool,
            "pool:1",
            PerformanceUseMaximum: true,
            PerformanceResiliency: "Mirror");
        var manualExpansion = service.Apply(document, manualRequest);
        var layoutChange = service.Apply(document, layoutRequest);

        Assert.False(manualExpansion.Succeeded);
        Assert.Equal("storage.rule.update-pool.existing-data",
            StorageEditRules.Evaluate(document.Snapshot, manualRequest).Code);
        Assert.False(layoutChange.Succeeded);
        Assert.Equal("storage.rule.update-pool.existing-data",
            StorageEditRules.Evaluate(document.Snapshot, layoutRequest).Code);
    }
}
