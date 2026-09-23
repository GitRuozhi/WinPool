using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationOperationTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

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
            [],
            DateTimeOffset.Now);
    }

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationEditRequest request)
    {
        var result = new SimulationOperationService().Apply(document, request);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Commands);
        return result.Document;
    }

    private static StorageSystemDocument CreateFormattedPartitionDocument(string fileSystem = "NTFS") =>
        Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 20 * GiB,
            FileSystem: fileSystem));

    private static StorageSystemDocument CreateSystemDiskWithTwoDataPartitions(
        out string systemPartitionId,
        out string ordinaryPartitionId)
    {
        var document = CreateFormattedPartitionDocument();
        var systemPartition = Assert.Single(document.Snapshot.Partitions);
        var systemVolume = document.Snapshot.VolumeForPartition(systemPartition.StableId)!;
        systemPartitionId = systemPartition.StableId;
        ordinaryPartitionId = "sim:partition:ordinary";
        var ordinaryPartition = new PartitionInfo(
            ordinaryPartitionId,
            true,
            5,
            2,
            "BasicData",
            40 * GiB,
            10 * GiB,
            false,
            false,
            "D",
            "Ordinary data",
            "NTFS",
            65536,
            10 * GiB,
            "Healthy",
            "OK",
            "D:\\",
            "osdisk:5");
        var ordinaryVolume = new VolumeInfo(
            "sim:volume:ordinary",
            true,
            ordinaryPartitionId,
            "NTFS",
            "Ordinary data",
            10 * GiB,
            10 * GiB,
            65536,
            "Healthy",
            "OK",
            ["D:\\"]);

        return document.WithCandidate(document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item.StableId == "osdisk:5"
                    ? item with { IsBoot = true, IsSystem = true }
                    : item)
                .ToArray(),
            Partitions =
            [
                systemPartition with { IsBoot = true, IsSystem = true },
                ordinaryPartition
            ],
            Volumes = [systemVolume, ordinaryVolume]
        });
    }

    [Fact]
    public void InitializeDiskCreatesMsrAndClearsPartitions()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 476 * MiB));
        Assert.Single(document.Snapshot.Partitions);

        document = document.WithCandidate(document.Snapshot with
            {
                OsDisks = document.Snapshot.OsDisks
                    .Select(item => item.StableId == "osdisk:5"
                        ? item with { PartitionStyle = "RAW" }
                        : item)
                    .ToArray()
            });

        var initialize = new SimulationOperationService().Apply(document, new SimulationEditRequest(
            SimulationEditKind.InitializeDisk,
            "osdisk:5",
            PartitionStyle: "GPT",
            CreateMsr: true));
        Assert.True(initialize.Succeeded, initialize.Error);
        Assert.Contains(initialize.Commands, command =>
            command.Contains("-Offset 1048576 -Size 16777216", StringComparison.Ordinal));
        document = initialize.Document;

        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("MicrosoftReserved", partition.Type);
        Assert.Equal(1024L * 1024, partition.Offset);
        Assert.Equal(16 * 1024 * 1024, partition.Size);
        Assert.Equal(17L * 1024 * 1024, partition.Offset + partition.Size);
        Assert.Equal("GPT", document.Snapshot.OsDisks.Single(x => x.StableId == "osdisk:5").PartitionStyle);

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 1024L * 1024,
            OffsetBytes: 17L * 1024 * 1024));
        var data = Assert.Single(document.Snapshot.Partitions, item => item.Type == "BasicData");
        Assert.Equal(17L * 1024 * 1024, data.Offset);
    }

    [Fact]
    public void SystemPartitionCannotBeDeletedOrFormattedButItsLabelCanBeChanged()
    {
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 476 * MiB,
            FileSystem: "NTFS"));
        var partition = Assert.Single(document.Snapshot.Partitions);
        document = document.WithCandidate(document.Snapshot with
        {
            Partitions = [partition with { IsBoot = true, IsSystem = true }]
        });
        var service = new SimulationOperationService();

        var delete = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.DeletePartition,
            partition.StableId));
        var format = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.FormatPartition,
            partition.StableId,
            FileSystem: "NTFS"));
        var rename = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.Rename,
            partition.StableId,
            Name: "System volume"));

        Assert.False(delete.Succeeded);
        Assert.Contains("cannot be deleted", delete.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(format.Succeeded);
        Assert.Contains("cannot be formatted", format.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(rename.Succeeded, rename.Error);
        Assert.Equal(
            "System volume",
            rename.Document.Snapshot.VolumeForPartition(partition.StableId)!.FileSystemLabel);
    }

    [Fact]
    public void SystemDiskCanBeRenamedWithoutPermittingDestructiveChanges()
    {
        var document = CreateDocument();
        var systemDisk = document.Snapshot.OsDisks.Single(item => item.StableId == "osdisk:5")
            with { IsBoot = true, IsSystem = true };
        document = document.WithCandidate(document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item.StableId == systemDisk.StableId ? systemDisk : item)
                .ToArray()
        });

        var rename = new SimulationOperationService().Apply(document, new SimulationEditRequest(
            SimulationEditKind.Rename,
            systemDisk.StableId,
            Name: "Renamed system disk"));

        Assert.True(rename.Succeeded, rename.Error);
        Assert.Equal(
            "Renamed system disk",
            rename.Document.Snapshot.OsDisks.Single(item => item.StableId == systemDisk.StableId).FriendlyName);
    }

    [Fact]
    public void SystemDiskDataPartitionsCanResizeWhileSystemBasicDataStaysDestructiveProtected()
    {
        var document = CreateSystemDiskWithTwoDataPartitions(out var systemPartitionId, out var ordinaryPartitionId);
        var service = new SimulationOperationService();

        var systemResize = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            systemPartitionId,
            SizeBytes: 24 * GiB));
        Assert.True(systemResize.Succeeded, systemResize.Error);
        var systemShrink = service.Apply(systemResize.Document, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            systemPartitionId,
            SizeBytes: 16 * GiB));
        Assert.True(systemShrink.Succeeded, systemShrink.Error);
        var ordinaryResize = service.Apply(systemShrink.Document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            ordinaryPartitionId,
            SizeBytes: 12 * GiB));
        Assert.True(ordinaryResize.Succeeded, ordinaryResize.Error);

        var snapshot = ordinaryResize.Document.Snapshot;
        var systemPartition = Assert.Single(snapshot.Partitions, item => item.StableId == systemPartitionId);
        var ordinaryPartition = Assert.Single(snapshot.Partitions, item => item.StableId == ordinaryPartitionId);
        Assert.True(snapshot.OsDisks.Single(item => item.StableId == "osdisk:5").IsSystem);
        Assert.True(systemPartition.IsBoot);
        Assert.True(systemPartition.IsSystem);
        Assert.Equal(16 * GiB, systemPartition.Size);
        Assert.Equal(12 * GiB, ordinaryPartition.Size);

        var delete = service.Apply(ordinaryResize.Document, new SimulationEditRequest(
            SimulationEditKind.DeletePartition,
            systemPartitionId));
        var format = service.Apply(ordinaryResize.Document, new SimulationEditRequest(
            SimulationEditKind.FormatPartition,
            systemPartitionId,
            FileSystem: "NTFS"));
        Assert.False(delete.Succeeded);
        Assert.False(format.Succeeded);
        Assert.Same(ordinaryResize.Document, delete.Document);
        Assert.Same(ordinaryResize.Document, format.Document);
    }

    [Fact]
    public void ResizeSynchronizesLinkedPartitionAndVolumeToTheTargetCapacity()
    {
        var document = CreateFormattedPartitionDocument();
        var partition = Assert.Single(document.Snapshot.Partitions);
        var service = new SimulationOperationService();

        var extended = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB));
        Assert.True(extended.Succeeded, extended.Error);
        Assert.Contains(extended.Commands, command => command.Contains(
            "Resize-Partition -InputObject $targetPartition -Size 25769803776",
            StringComparison.Ordinal));

        var extendedPartition = Assert.Single(extended.Document.Snapshot.Partitions);
        var extendedVolume = Assert.Single(extended.Document.Snapshot.Volumes);
        Assert.Equal(24 * GiB, extendedPartition.Size);
        Assert.Equal(extendedPartition.Size, extendedVolume.Size);
        Assert.Equal(extendedPartition.SizeRemaining, extendedVolume.SizeRemaining);

        var shrunk = service.Apply(extended.Document, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            partition.StableId,
            SizeBytes: 16 * GiB));
        Assert.True(shrunk.Succeeded, shrunk.Error);
        var shrunkPartition = Assert.Single(shrunk.Document.Snapshot.Partitions);
        var shrunkVolume = Assert.Single(shrunk.Document.Snapshot.Volumes);
        Assert.Equal(16 * GiB, shrunkPartition.Size);
        Assert.Equal(shrunkPartition.Size, shrunkVolume.Size);
        Assert.Equal(shrunkPartition.SizeRemaining, shrunkVolume.SizeRemaining);
    }

    [Fact]
    public void ResizeRejectsAdjacentPartitionAndDiskBoundariesWithoutMutatingDocument()
    {
        var document = CreateSystemDiskWithTwoDataPartitions(out var systemPartitionId, out var ordinaryPartitionId);
        var service = new SimulationOperationService();

        var adjacentPartition = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            systemPartitionId,
            SizeBytes: 40 * GiB));
        var diskBoundary = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            ordinaryPartitionId,
            SizeBytes: 100 * GiB));

        Assert.False(adjacentPartition.Succeeded);
        Assert.False(diskBoundary.Succeeded);
        Assert.Same(document, adjacentPartition.Document);
        Assert.Same(document, diskBoundary.Document);
        Assert.Contains("boundary", adjacentPartition.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("boundary", diskBoundary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResizeFileSystemSupportIsDirectionSpecific()
    {
        var service = new SimulationOperationService();
        var refs = CreateFormattedPartitionDocument("ReFS");
        var refsPartition = Assert.Single(refs.Snapshot.Partitions);
        var refsExtend = service.Apply(refs, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            refsPartition.StableId,
            SizeBytes: 24 * GiB));
        var refsShrink = service.Apply(refs, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            refsPartition.StableId,
            SizeBytes: 16 * GiB));

        var exfat = CreateFormattedPartitionDocument("exFAT");
        var exfatPartition = Assert.Single(exfat.Snapshot.Partitions);
        var exfatExtend = service.Apply(exfat, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            exfatPartition.StableId,
            SizeBytes: 24 * GiB));
        var exfatShrink = service.Apply(exfat, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            exfatPartition.StableId,
            SizeBytes: 16 * GiB));

        Assert.True(refsExtend.Succeeded, refsExtend.Error);
        Assert.False(refsShrink.Succeeded);
        Assert.Same(refs, refsShrink.Document);
        Assert.False(exfatExtend.Succeeded);
        Assert.False(exfatShrink.Succeeded);
        Assert.Same(exfat, exfatExtend.Document);
        Assert.Same(exfat, exfatShrink.Document);

        var raw = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            "osdisk:5",
            SizeBytes: 20 * GiB));
        var rawPartition = Assert.Single(raw.Snapshot.Partitions);
        var rawExtend = service.Apply(raw, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            rawPartition.StableId,
            SizeBytes: 24 * GiB));
        Assert.True(rawExtend.Succeeded, rawExtend.Error);
        var rawShrink = service.Apply(rawExtend.Document, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            rawPartition.StableId,
            SizeBytes: 16 * GiB));
        Assert.True(rawShrink.Succeeded, rawShrink.Error);
        Assert.Empty(rawShrink.Document.Snapshot.Volumes);
        var rawPartitionShrunk = Assert.Single(rawShrink.Document.Snapshot.Partitions);
        Assert.Equal(16 * GiB, rawPartitionShrunk.Size);
        Assert.Equal(0L, rawPartitionShrunk.SizeRemaining);
    }

    [Fact]
    public void ResizeRejectsUnalignedTargetsAndPreservesLegitimatePartitionVolumeDifferences()
    {
        var document = CreateFormattedPartitionDocument();
        var partition = Assert.Single(document.Snapshot.Partitions);
        var service = new SimulationOperationService();
        var unaligned = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB + 1));

        var volume = document.Snapshot.VolumeForPartition(partition.StableId)!;
        var differentVolume = document.WithCandidate(document.Snapshot with
        {
            Volumes = [volume with { Size = 19 * GiB, SizeRemaining = 18 * GiB }]
        });
        var differentPartition = Assert.Single(differentVolume.Snapshot.Partitions);
        var differentVolumeBefore = Assert.Single(differentVolume.Snapshot.Volumes);
        var differentResult = service.Apply(differentVolume, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB));

        Assert.False(unaligned.Succeeded);
        Assert.Contains("aligned", unaligned.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Same(document, unaligned.Document);
        Assert.True(differentResult.Succeeded, differentResult.Error);
        var differentPartitionAfter = Assert.Single(differentResult.Document.Snapshot.Partitions);
        var differentVolumeAfter = Assert.Single(differentResult.Document.Snapshot.Volumes);
        Assert.Equal(24 * GiB, differentPartitionAfter.Size);
        Assert.Equal(23 * GiB, differentVolumeAfter.Size);
        Assert.Equal(22 * GiB, differentVolumeAfter.SizeRemaining);
        Assert.Equal(differentVolumeAfter.SizeRemaining, differentPartitionAfter.SizeRemaining);
        Assert.Equal(
            differentPartition.Size - differentVolumeBefore.Size,
            differentPartitionAfter.Size - differentVolumeAfter.Size);
        Assert.Equal(
            differentVolumeBefore.Size - differentVolumeBefore.SizeRemaining,
            differentVolumeAfter.Size - differentVolumeAfter.SizeRemaining);

        var differentShrink = service.Apply(differentResult.Document, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            differentPartition.StableId,
            SizeBytes: 16 * GiB));
        Assert.True(differentShrink.Succeeded, differentShrink.Error);
        var differentPartitionShrunk = Assert.Single(differentShrink.Document.Snapshot.Partitions);
        var differentVolumeShrunk = Assert.Single(differentShrink.Document.Snapshot.Volumes);
        Assert.Equal(16 * GiB, differentPartitionShrunk.Size);
        Assert.Equal(15 * GiB, differentVolumeShrunk.Size);
        Assert.Equal(14 * GiB, differentVolumeShrunk.SizeRemaining);
        Assert.Equal(
            differentPartition.Size - differentVolumeBefore.Size,
            differentPartitionShrunk.Size - differentVolumeShrunk.Size);
        Assert.Equal(
            differentVolumeBefore.Size - differentVolumeBefore.SizeRemaining,
            differentVolumeShrunk.Size - differentVolumeShrunk.SizeRemaining);

        var independentlyReportedFreeSpace = differentVolume.Snapshot with
        {
            Partitions = [differentPartition with { SizeRemaining = 17 * GiB }]
        };
        var independentDecision = StorageEditRules.Evaluate(independentlyReportedFreeSpace,
            new SimulationEditRequest(
                SimulationEditKind.ExtendPartition,
                differentPartition.StableId,
                SizeBytes: 24 * GiB));
        Assert.Equal(StorageRuleVerdict.Allow, independentDecision.Verdict);

        var oversizedVolume = document.WithCandidate(document.Snapshot with
        {
            Volumes = [volume with { Size = 21 * GiB, SizeRemaining = 20 * GiB }]
        });
        var oversizedResult = service.Apply(oversizedVolume, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB));
        Assert.False(oversizedResult.Succeeded);
        Assert.Same(oversizedVolume, oversizedResult.Document);
        Assert.Contains("linked volume", oversizedResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResizeDeniesUnavailableSourceAndOverflowingGeometryWithoutThrowing()
    {
        var document = CreateFormattedPartitionDocument();
        var partition = Assert.Single(document.Snapshot.Partitions);
        var unavailable = document.Snapshot with
        {
            FieldIssues = [new StorageFieldIssue(partition.StableId, "Offset", FieldReadState.Failed, "test")]
        };
        var unavailableDecision = StorageEditRules.Evaluate(unavailable, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB));
        var overflowing = document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item.StableId == "osdisk:5" ? item with { Size = long.MaxValue } : item)
                .ToArray(),
            Partitions = [partition with { Offset = long.MaxValue - MiB, Size = 2 * MiB }]
        };
        var overflowDecision = StorageEditRules.Evaluate(overflowing, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 3 * MiB));
        var volume = document.Snapshot.VolumeForPartition(partition.StableId)!;
        var overflowingVolume = document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item.StableId == "osdisk:5" ? item with { Size = long.MaxValue } : item)
                .ToArray(),
            Volumes = [volume with { Size = long.MaxValue - MiB, SizeRemaining = MiB }]
        };
        var volumeOverflowDecision = StorageEditRules.Evaluate(overflowingVolume,
            new SimulationEditRequest(
                SimulationEditKind.ExtendPartition,
                partition.StableId,
                SizeBytes: 24 * GiB));

        Assert.Equal(StorageRuleVerdict.InsufficientInfo, unavailableDecision.Verdict);
        Assert.Equal("storage.rule.source-field-unavailable", unavailableDecision.Code);
        Assert.Equal(StorageRuleVerdict.Deny, overflowDecision.Verdict);
        Assert.Equal("storage.rule.resize.partition-geometry", overflowDecision.Code);
        Assert.Equal(StorageRuleVerdict.Deny, volumeOverflowDecision.Verdict);
        Assert.Equal("storage.rule.resize.volume-geometry", volumeOverflowDecision.Code);
    }

    [Fact]
    public void ResizeRejectsMissingOfflineAndNonpositiveRequestsWithoutMutatingDocument()
    {
        var document = CreateFormattedPartitionDocument();
        var partition = Assert.Single(document.Snapshot.Partitions);
        var service = new SimulationOperationService();

        var missing = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            "sim:partition:missing",
            SizeBytes: 24 * GiB));
        var missingTarget = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId));
        var nonpositiveTarget = service.Apply(document, new SimulationEditRequest(
            SimulationEditKind.ShrinkPartition,
            partition.StableId,
            SizeBytes: 0));
        var offline = document.WithCandidate(document.Snapshot with
        {
            OsDisks = document.Snapshot.OsDisks
                .Select(item => item.StableId == "osdisk:5" ? item with { IsOffline = true } : item)
                .ToArray()
        });
        var offlineRequest = service.Apply(offline, new SimulationEditRequest(
            SimulationEditKind.ExtendPartition,
            partition.StableId,
            SizeBytes: 24 * GiB));

        Assert.False(missing.Succeeded);
        Assert.False(missingTarget.Succeeded);
        Assert.False(nonpositiveTarget.Succeeded);
        Assert.False(offlineRequest.Succeeded);
        Assert.Same(document, missing.Document);
        Assert.Same(document, missingTarget.Document);
        Assert.Same(document, nonpositiveTarget.Document);
        Assert.Same(offline, offlineRequest.Document);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", missingTarget.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", nonpositiveTarget.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", offlineRequest.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreatePoolVirtualDiskAndPartitionChainProducesUsableVolume()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreateStoragePool,
            "pool:primordial",
            Name: "Pool03",
            MemberDiskIds: ["physical:p1"]));

        var pool = Assert.Single(document.Snapshot.StoragePools, x => !x.IsPrimordial);
        Assert.Equal("Pool03", pool.FriendlyName);
        Assert.Equal(["physical:p1"], pool.MemberPhysicalDiskIds);
        Assert.DoesNotContain(
            "physical:p1",
            document.Snapshot.StoragePools.Single(x => x.IsPrimordial).MemberPhysicalDiskIds);

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreateVirtualDisk,
            pool.StableId,
            Name: "Pool03",
            Resiliency: "Simple",
            InterleaveBytes: 65536,
            AllocationUnitSize: 65536));
        var vdisk = Assert.Single(document.Snapshot.VirtualDisks);
        Assert.Equal(65536, vdisk.Interleave);

        var osDisk = Assert.Single(
            document.Snapshot.OsDisks, x => x.VirtualDiskStableId == vdisk.StableId);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreatePartition,
            osDisk.StableId));
        var partition = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("BasicData", partition.Type);

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.FormatPartition,
            partition.StableId,
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var formatted = Assert.Single(document.Snapshot.Partitions);
        Assert.Equal("NTFS", formatted.FileSystem);
        Assert.Equal(65536, formatted.AllocationUnitSize);
    }

    [Fact]
    public void ShrinkBelowModeledUsedSpaceIsRejectedWithoutMutation()
    {
        var document = CreateFormattedPartitionDocument();
        var partition = Assert.Single(document.Snapshot.Partitions);
        var volume = document.Snapshot.VolumeForPartition(partition.StableId)!;
        document = document.WithCandidate(document.Snapshot with
        {
            Volumes = [volume with { SizeRemaining = 5 * GiB }]
        });
        var usedPartition = Assert.Single(document.Snapshot.Partitions);

        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.ShrinkPartition,
                usedPartition.StableId,
                SizeBytes: 10 * GiB));
        Assert.False(result.Succeeded);
        Assert.Same(document, result.Document);
        Assert.Contains("used data", result.Error, StringComparison.OrdinalIgnoreCase);
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.MovePhysicalDisk,
            "physical:p2",
            DestinationGroupId: pool.StableId));
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.EvictPhysicalDiskFromTiers,
            "physical:p1"));
        Assert.False(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.MovePhysicalDisk,
            "physical:p1",
            DestinationGroupId: pool.StableId));
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            VirtualDiskName: "SpaceA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS",
            AllocationUnitSize: 65536));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.EvictPhysicalDiskFromTiers,
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS"));
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.DissolveStoragePool,
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
        var document = Apply(CreateDocument(), new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "primordial",
            Name: "PoolA",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            FileSystem: "NTFS"));
        var primordial = document.Snapshot.StoragePools.Single(item => item.IsPrimordial);
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.MovePhysicalDisk,
            "physical:p2",
            DestinationGroupId: primordial.StableId));
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
            new SimulationEditRequest(
                SimulationEditKind.UpdateStoragePool,
                "pool:busy",
                Name: "Renamed"));
        Assert.False(result.Succeeded);
        Assert.Contains("more than one virtual disk", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteVirtualDiskRemovesOneVirtualDiskAndKeepsTheOther()
    {
        var document = CreateBusyPoolDocument();
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.DeleteVirtualDisk,
            "vdisk:2"));
        var pool = document.Snapshot.StoragePools.Single(item => item.StableId == "pool:busy");
        var remaining = Assert.Single(document.Snapshot.VirtualDisks, item => item.PoolStableId == "pool:busy");
        Assert.Equal("vdisk:1", remaining.StableId);
        Assert.Equal(500_000_000, pool.AllocatedSize);
    }

    private static StorageSystemDocument CreateBusyPoolDocument()
    {
        var document = CreateDocument();
        return document.WithCandidate(document.Snapshot with
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
            });
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
            [],
            DateTimeOffset.Now);
    }

    private static StorageSystemDocument Apply(
        StorageSystemDocument document,
        SimulationEditRequest request)
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
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
            "pool:primordial",
            Name: "Pool01",
            MemberDiskIds: ["physical:p1", "physical:p2"],
            VirtualDiskName: "Pool01"));
        var pool = document.Snapshot.StoragePools.First(item => item.FriendlyName == "Pool01");
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.SetDiskUsage,
            "physical:p1",
            DiskUsage: "Retired"));
        var retiredDisk = document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p1");
        Assert.True(retiredDisk.IsRetired);
        Assert.False(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p1"));
        Assert.True(EditWorkspace.DiskIsAssignedToTier(document.Snapshot, "physical:p2"));

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.SetDiskUsage,
            "physical:p1",
            DiskUsage: "HotSpare"));
        var hotDisk = document.Snapshot.PhysicalDisks.First(item => item.StableId == "physical:p1");
        Assert.False(hotDisk.IsRetired);
        Assert.True(hotDisk.IsHotSpare);

        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.SetDiskUsage,
            "physical:p1",
            DiskUsage: string.Empty));
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
            new SimulationEditRequest(
                SimulationEditKind.SetDiskUsage,
                "physical:p1",
                DiskUsage: "Retired"));
        Assert.False(primordialResult.Succeeded);
        Assert.Contains("Primordial", primordialResult.Error, StringComparison.OrdinalIgnoreCase);

        var missing = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.SetDiskUsage,
                "physical:missing",
                DiskUsage: "Retired"));
        Assert.False(missing.Succeeded);

        var invalid = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.SetDiskUsage,
                "physical:p1",
                DiskUsage: "Journal"));
        Assert.False(invalid.Succeeded);
    }

    [Fact]
    public void SetDiskUsageClearsPageFileRoleAfterConfirmationGate()
    {
        var document = CreateDocument();
        document = Apply(document, new SimulationEditRequest(
            SimulationEditKind.CreateTieredPool,
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
        document = document.WithCandidate(withRole);
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.SetDiskUsage,
                "physical:p2",
                DiskUsage: "HotSpare"));
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
            [],
            DateTimeOffset.Now);
    }

    [Fact]
    public void CreateTieredPoolCanSkipTheVirtualDisk()
    {
        var document = CreateDocument();
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.CreateTieredPool,
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
        return document.WithCandidate(document.Snapshot with
            {
                StorageTiers = tiers,
                VirtualDisks = [virtualDisk],
                OsDisks = [osDisk],
                Partitions = [partition],
                Volumes = [new VolumeInfo("volume:1", true, partition.StableId, "NTFS", "Data",
                    partition.Size, 400_000_000_000, 4096, "Healthy", "OK", ["D:\\"])]
            });
    }

    [Fact]
    public void UpdateStoragePoolResizesThePerformanceTier()
    {
        var document = CreateDocument();
        var result = new SimulationOperationService().Apply(
            document,
            new SimulationEditRequest(
                SimulationEditKind.UpdateStoragePool,
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
            new SimulationEditRequest(
                SimulationEditKind.UpdateStoragePool,
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
            new SimulationEditRequest(
                SimulationEditKind.UpdateStoragePool,
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
        var manualRequest = new SimulationEditRequest(
            SimulationEditKind.UpdateStoragePool,
            "pool:1",
            PerformanceSizeBytes: 1_500_000_000_000);
        var layoutRequest = new SimulationEditRequest(
            SimulationEditKind.UpdateStoragePool,
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
