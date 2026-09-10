using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class V049PartitionSemanticsTests
{
    [Theory]
    [InlineData("NTFS")]
    [InlineData("ReFS")]
    [InlineData("exFAT")]
    public void FormatPartitionAcceptsNtfsRefsAndExfatOnWorkstationSku(string fileSystem)
    {
        var document = InitializedDisk();
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: 1_000_000_000));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "Primary");
        Assert.DoesNotContain(document.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);

        var formatted = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.FormatPartition,
            partition.StableId,
            FileSystem: fileSystem,
            AllocationUnitSize: 65536));
        var volume = Assert.Single(formatted.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);
        Assert.Equal(fileSystem.ToUpperInvariant(), volume.FileSystem.ToUpperInvariant());
        Assert.Equal(fileSystem.ToUpperInvariant(), formatted.Snapshot.FileSystemOf(partition).ToUpperInvariant());
    }

    [Fact]
    public void CreatePartitionWithFileSystemCreatesVolume()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationOperationRequest(
                SimulationOperationKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "exFAT",
                AllocationUnitSize: 65536,
                DriveLetter: "E"));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "Primary");
        var volume = Assert.Single(document.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);
        Assert.Equal("EXFAT", volume.FileSystem);
        Assert.Equal("E", volume.DriveLetter);
    }

    [Fact]
    public void ClearingDriveLetterRemovesAccessPath()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationOperationRequest(
                SimulationOperationKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "NTFS",
                DriveLetter: "F"));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "Primary");
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.ChangeDriveLetter,
            partition.StableId,
            DriveLetter: string.Empty));
        var volume = Assert.Single(document.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);
        Assert.Equal(string.Empty, volume.DriveLetter);
    }

    [Fact]
    public void FormatRulesAllowExfatAndRefsWithoutServerSku()
    {
        var snapshot = Apply(
            InitializedDisk(),
            new SimulationOperationRequest(SimulationOperationKind.CreatePartition, "osdisk:ssd0")).Snapshot;
        var partition = Assert.Single(snapshot.Partitions, item => item.Type == "Primary");
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationOperationRequest(
                    SimulationOperationKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "exFAT")).Verdict);
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationOperationRequest(
                    SimulationOperationKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "ReFS")).Verdict);
        Assert.Equal(
            StorageRuleVerdict.Deny,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationOperationRequest(
                    SimulationOperationKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "FAT32")).Verdict);
    }

    [Fact]
    public void DeletePartitionRemovesMsrAndUnknownTypes()
    {
        var document = Apply(
            EmptyRawDisk(),
            new SimulationOperationRequest(
                SimulationOperationKind.InitializeDisk,
                "osdisk:ssd0",
                Name: "GPT",
                CreateMsr: true));
        var msr = Assert.Single(document.Snapshot.Partitions, item => item.Type == "MicrosoftReserved");
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                document.Snapshot,
                new SimulationOperationRequest(SimulationOperationKind.DeletePartition, msr.StableId)).Verdict);
        document = Apply(document, new SimulationOperationRequest(
            SimulationOperationKind.DeletePartition,
            msr.StableId));
        Assert.DoesNotContain(document.Snapshot.Partitions, item => item.Type == "MicrosoftReserved");
    }

    private static StorageSystemDocument InitializedDisk()
    {
        var document = Apply(
            EmptyRawDisk(),
            new SimulationOperationRequest(
                SimulationOperationKind.InitializeDisk,
                "osdisk:ssd0",
                Name: "GPT",
                CreateMsr: false));
        return document;
    }

    private static StorageSystemDocument EmptyRawDisk()
    {
        var disk = new PhysicalDiskInfo(
            "physical:ssd0", true, "SSD 0", "Model", "SS0001", "SATA", "SSD",
            2_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 1,
            false, false, false, false, "pool:primordial");
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion, "test", DateTimeOffset.UtcNow,
            new ComputerInfo("system:test", "TEST-PC", "Windows 11 Pro", "10.0", "22631", DateTimeOffset.UtcNow),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [disk],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    disk.Size, 0, "subsystem:1", ["physical:ssd0"])
            ],
            [],
            [],
            [new OsDiskInfo("osdisk:ssd0", "SSD 0", 1, "RAW", 2_000_000_000, false, false, false, "physical:ssd0", null)],
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

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationOperationRequest request)
    {
        var result = new SimulationOperationService().Apply(document, request);
        Assert.True(result.Succeeded, result.Error);
        return result.Document;
    }
}
