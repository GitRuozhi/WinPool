using WinPool.Core;

namespace WinPool.Core.Tests;

public sealed class SimulationOperationTests
{
    private static StorageSystemDocument CreateDocument()
    {
        var primordialDisks = new[]
        {
            new PhysicalDiskInfo(
                "physical:p1", true, "Free Disk One", "Model", "AA0001", "SATA", "SSD",
                1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                false, false, false, false, "pool:primordial"),
            new PhysicalDiskInfo(
                "physical:p2", true, "Free Disk Two", "Model", "AA0002", "SATA", "HDD",
                2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 6,
                false, false, false, false, "pool:primordial")
        };
        var primordial = new StoragePoolInfo(
            "pool:primordial", true, "Primordial", true, "Healthy", "OK",
            3_000_000_000L, 0, "subsystem:1", ["physical:p1", "physical:p2"]);
        var osDisk = new OsDiskInfo(
            "osdisk:5", "Free Disk One", 5, "RAW", 1_000_000_000, false, false, false, "physical:p1", null);
        var snapshot = new StorageSnapshot(
            2, "test", DateTimeOffset.UtcNow,
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
        Assert.Equal("Primary", partition.Type);

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
}
