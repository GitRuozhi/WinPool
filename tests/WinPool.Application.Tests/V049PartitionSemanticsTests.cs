using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class V049PartitionSemanticsTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void PartitionCreateGeometryAlignsStartAndFloorsCapacityToWholeMebibytes()
    {
        var geometry = EditWorkspace.GetPartitionCreateGeometry(
            gapOffsetBytes: 5 * MiB + MiB / 2,
            gapSizeBytes: 3 * MiB);

        Assert.True(geometry.CanCreate);
        Assert.Equal(6 * MiB, geometry.StartOffsetBytes);
        Assert.Equal(2 * MiB, geometry.MaximumSizeBytes);
        Assert.Equal(2 * MiB, geometry.DefaultSizeBytes);
        Assert.Equal(8 * MiB, geometry.MaximumEndOffsetExclusiveBytes);
    }

    [Fact]
    public void DiskHeadGapReservesFirstMebibyteAndRejectsSmallerAlignedCapacity()
    {
        var tooSmall = EditWorkspace.GetPartitionCreateGeometry(0, MiB);
        Assert.False(tooSmall.CanCreate);
        Assert.Contains("less than 1 MiB", tooSmall.UnavailableReason, StringComparison.Ordinal);

        var oneMebibyte = EditWorkspace.GetPartitionCreateGeometry(0, 2 * MiB);
        Assert.True(oneMebibyte.CanCreate);
        Assert.Equal(MiB, oneMebibyte.StartOffsetBytes);
        Assert.Equal(MiB, oneMebibyte.MaximumSizeBytes);
    }

    [Fact]
    public void CreatePartitionPreservesImportedOffsetAndAlignsOnlyNewPartition()
    {
        var document = Apply(InitializedDisk(), new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB));
        var imported = Assert.Single(document.Snapshot.Partitions);
        var importedOffset = imported.Offset + 512;
        document = document.WithCandidate(document.Snapshot with
        {
            Partitions = [imported with { Offset = importedOffset }]
        });

        var nextGapOffset = importedOffset + imported.Size;
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB,
            OffsetBytes: nextGapOffset));

        var partitions = document.Snapshot.Partitions.OrderBy(item => item.Offset).ToArray();
        Assert.Equal(importedOffset, partitions[0].Offset);
        Assert.Equal(3 * MiB, partitions[1].Offset);
        Assert.Equal(MiB, partitions[1].Size);
    }

    [Fact]
    public void CreatePartitionRejectsGapWithoutOneAlignedMebibyteWithReason()
    {
        var document = InitializedDisk();
        document = document.WithCandidate(document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item with { Size = MiB + MiB / 2 })
                .ToArray()
        });

        var result = new SimulationOperationService().Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0"));

        Assert.False(result.Succeeded);
        Assert.Contains("less than 1 MiB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePartitionDoesNotOverlapAnExistingPartitionWhenDiskIdCasingDiffers()
    {
        var snapshot = Apply(InitializedDisk(), new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB)).Snapshot;
        var existing = Assert.Single(snapshot.Partitions);
        snapshot = snapshot with
        {
            Partitions = [existing with { OsDiskStableId = "OSDISK:SSD0" }]
        };

        var geometry = EditWorkspace.GetPartitionCreateGeometry(
            snapshot, "osdisk:ssd0", existing.Offset);
        var decision = StorageEditRules.Evaluate(snapshot, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB,
            OffsetBytes: existing.Offset));

        Assert.False(geometry.CanCreate);
        Assert.Equal(StorageRuleVerdict.Deny, decision.Verdict);
    }

    [Fact]
    public void CreatePartitionReusesAnAvailableNumberAfterDeletingAMiddlePartition()
    {
        var document = InitializedDisk();
        for (var index = 0; index < 3; index++)
        {
            document = Apply(document, new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                SizeBytes: MiB));
        }

        var middle = document.Snapshot.Partitions.Single(item => item.PartitionNumber == 2);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.DeletePartition, middle.StableId));
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB,
            OffsetBytes: middle.Offset));

        Assert.Equal(
            new[] { 1, 2, 3 },
            document.Snapshot.Partitions.Select(item => item.PartitionNumber).OrderBy(number => number));
    }

    [Fact]
    public void CreatePartitionRulesRequireWholeMebibyteLengthWithinAlignedMaximum()
    {
        var snapshot = InitializedDisk().Snapshot;
        var maximumSize = EditWorkspace.GetPartitionCreateGeometry(snapshot, "osdisk:ssd0")
            .MaximumSizeBytes!.Value;

        var unaligned = StorageEditRules.Evaluate(snapshot, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: MiB + 1));
        var oversized = StorageEditRules.Evaluate(snapshot, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: maximumSize + MiB));

        Assert.Equal(StorageRuleVerdict.Deny, unaligned.Verdict);
        Assert.Equal("storage.rule.create-partition.size-alignment", unaligned.Code);
        Assert.Equal(StorageRuleVerdict.Deny, oversized.Verdict);
        Assert.Equal("storage.rule.create-partition.size-boundary", oversized.Code);
    }

    [Fact]
    public void InitializeDiskRejectsMsrWhenDiskCannotContainItsFixedGeometry()
    {
        var document = EmptyRawDisk();
        document = document.WithCandidate(document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item with { Size = 17 * MiB - 1 })
                .ToArray()
        });

        var decision = StorageEditRules.Evaluate(document.Snapshot, new SimulationEditRequest(
            SimulationEditKind.InitializeDisk,
            "osdisk:ssd0",
            PartitionStyle: "GPT",
            CreateMsr: true));

        Assert.Equal(StorageRuleVerdict.Deny, decision.Verdict);
        Assert.Equal("storage.rule.initialize.msr-capacity", decision.Code);
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("ReFS")]
    [InlineData("exFAT")]
    public void FormatPartitionAcceptsNtfsRefsAndExfatOnWorkstationSku(string fileSystem)
    {
        var document = InitializedDisk();
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:ssd0",
            SizeBytes: 953 * MiB));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "BasicData");
        Assert.DoesNotContain(document.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);

        var formatted = Apply(document, new SimulationEditRequest(
            SimulationEditKind.FormatPartition,
            partition.StableId,
            FileSystem: fileSystem,
            AllocationUnitSize: 65536));
        var volume = Assert.Single(formatted.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);
        Assert.Equal(fileSystem.ToUpperInvariant(), volume.FileSystem.ToUpperInvariant());
        Assert.Equal(fileSystem.ToUpperInvariant(), formatted.Snapshot.FileSystemOf(partition).ToUpperInvariant());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CreatePartitionPreviewCarriesQuickOrFullFormatModeWithoutChangingSimulatedFilesystem(
        bool quickFormat,
        bool expectFullSwitch)
    {
        var result = new SimulationOperationService().Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "NTFS",
                AllocationUnitSize: 65536,
                SizeBytes: 953 * MiB,
                QuickFormat: quickFormat));

        Assert.True(result.Succeeded, result.Error);
        var preview = string.Join(Environment.NewLine, result.Commands);
        Assert.Equal(expectFullSwitch, preview.Contains(" -Full", StringComparison.Ordinal));
        Assert.Equal("NTFS", Assert.Single(result.Document.Snapshot.Volumes).FileSystem);
    }

    [Fact]
    public void CreatePartitionWithFileSystemCreatesVolume()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "exFAT",
                AllocationUnitSize: 65536,
                DriveLetter: "E"));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "BasicData");
        var volume = Assert.Single(document.Snapshot.Volumes, item => item.PartitionStableId == partition.StableId);
        Assert.Equal("EXFAT", volume.FileSystem);
        Assert.Equal("E", volume.DriveLetter);
    }

    [Fact]
    public void CreatePartitionWithEmptyDriveLetterDoesNotAssignOne()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "NTFS",
                DriveLetter: string.Empty));
        var volume = Assert.Single(document.Snapshot.Volumes);
        Assert.Equal(string.Empty, volume.DriveLetter);
    }

    [Theory]
    [InlineData(PartitionKind.BasicData, "NTFS", "BasicData", false, "ebd0a0a2")]
    [InlineData(PartitionKind.EfiSystem, "FAT32", "EfiSystem", true, "c12a7328")]
    [InlineData(PartitionKind.MicrosoftReserved, "", "MicrosoftReserved", true, "e3c9e316")]
    [InlineData(PartitionKind.WindowsRecovery, "NTFS", "WindowsRecovery", true, "de94bba4")]
    public void CreatePartitionUsesFixedGptPartitionKinds(
        PartitionKind kind,
        string fileSystem,
        string expectedType,
        bool expectedHidden,
        string expectedTypeIdPrefix)
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: fileSystem,
                SizeBytes: 95 * MiB,
                PartitionKind: kind));

        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal(expectedType, partition.Type);
        Assert.Equal(expectedHidden, partition.IsHidden);
        Assert.Contains(expectedTypeIdPrefix, partition.PartitionTypeId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fileSystem.ToUpperInvariant(), partition.FileSystem.ToUpperInvariant());
    }

    [Fact]
    public void ConvertMbrDiskToGptClearsExistingPartitions()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "NTFS",
                SizeBytes: 95 * MiB));
        document = document.WithCandidate(document.Snapshot with
            {
                OsDisks = document.Snapshot.OsDisks
                    .Select(item => item with { PartitionStyle = "MBR" })
                    .ToArray()
            });

        var converted = Apply(document, new SimulationEditRequest(
            SimulationEditKind.ConvertDisk,
            "osdisk:ssd0",
            PartitionStyle: "GPT"));

        Assert.Empty(converted.Snapshot.Partitions);
        Assert.Empty(converted.Snapshot.Volumes);
        Assert.Equal("GPT", Assert.Single(converted.Snapshot.OsDisks).PartitionStyle);
    }

    [Fact]
    public void ClearingDriveLetterRemovesAccessPath()
    {
        var document = Apply(
            InitializedDisk(),
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:ssd0",
                FileSystem: "NTFS",
                DriveLetter: "F"));
        var partition = Assert.Single(document.Snapshot.Partitions, item => item.Type == "BasicData");
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.ChangeDriveLetter,
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
            new SimulationEditRequest(SimulationEditKind.CreatePartition, "osdisk:ssd0")).Snapshot;
        var partition = Assert.Single(snapshot.Partitions, item => item.Type == "BasicData");
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationEditRequest(
                    SimulationEditKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "exFAT")).Verdict);
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationEditRequest(
                    SimulationEditKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "ReFS")).Verdict);
        Assert.Equal(
            StorageRuleVerdict.Deny,
            StorageEditRules.Evaluate(
                snapshot,
                new SimulationEditRequest(
                    SimulationEditKind.FormatPartition,
                    partition.StableId,
                    FileSystem: "FAT32")).Verdict);
    }

    [Fact]
    public void DeletePartitionRemovesMsrAndUnknownTypes()
    {
        var document = Apply(
            EmptyRawDisk(),
            new SimulationEditRequest(
                SimulationEditKind.InitializeDisk,
                "osdisk:ssd0",
                PartitionStyle: "GPT",
                CreateMsr: true));
        var msr = Assert.Single(document.Snapshot.Partitions, item => item.Type == "MicrosoftReserved");
        Assert.Equal(
            StorageRuleVerdict.Allow,
            StorageEditRules.Evaluate(
                document.Snapshot,
                new SimulationEditRequest(SimulationEditKind.DeletePartition, msr.StableId)).Verdict);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.DeletePartition,
            msr.StableId));
        Assert.DoesNotContain(document.Snapshot.Partitions, item => item.Type == "MicrosoftReserved");
    }

    private static StorageSystemDocument InitializedDisk()
    {
        var document = Apply(
            EmptyRawDisk(),
            new SimulationEditRequest(
                SimulationEditKind.InitializeDisk,
                "osdisk:ssd0",
                PartitionStyle: "GPT",
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
            [],
            DateTimeOffset.UtcNow);
    }

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationEditRequest request)
    {
        var result = new SimulationOperationService().Apply(document, request);
        Assert.True(result.Succeeded, result.Error);
        return result.Document;
    }
}
