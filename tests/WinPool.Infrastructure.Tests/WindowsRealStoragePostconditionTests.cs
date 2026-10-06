using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStoragePostconditionTests
{
    private const string DiskId = "disk:7";
    private const string PhysicalId = "physical:7";
    private const string PoolId = "pool:7";
    private const string PartitionGuid = "2f8ae502-1e4e-4d94-b190-6284ccb62bea";
    private const string BasicData = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";
    private const long Offset = 1L << 20;
    private const long Size = 128L << 20;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
    private static readonly SystemId System = SystemId.New();

    [Fact]
    public void DeletedPartitionMustBeAbsentByGuidEvenWhenProviderIdentityChanges()
    {
        var before = Topology(Snapshot() with { Partitions = [Partition("partition:old")] });
        var rekeyed = Topology(Snapshot() with { Partitions = [Partition("partition:new")] });
        var absent = Topology(Snapshot());
        var reference = RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, "partition:old"));
        var command = new DeletePartitionCommand(reference);
        var target = Target(StorageObjectKind.Partition, "partition:old", PartitionGuid);

        Assert.Null(Verify(command, target, Returned(), before, rekeyed));
        Assert.NotNull(Verify(command, target, Returned(), before, absent));
    }

    [Theory]
    [InlineData("NTFS", false)]
    [InlineData("RAW", true)]
    public void CreatedPartitionRequiresUnformattedLetterlessPostState(
        string fileSystem, bool volumeHasLetter)
    {
        var before = Topology(Snapshot());
        var part = Partition("partition:new") with
        {
            FileSystem = fileSystem,
            AllocationUnitSize = fileSystem == "NTFS" ? 65536 : null
        };
        var volume = new VolumeInfo("volume:new", true, part.StableId, fileSystem, "",
            Size, Size, part.AllocationUnitSize, "Healthy", "OK",
            volumeHasLetter ? [@"R:\"] : []);
        var after = Topology(Snapshot() with { Partitions = [part], Volumes = [volume] });
        var command = new CreatePartitionCommand(
            RealTargetReference.ForExisting(Id(StorageObjectKind.OsDisk, DiskId)),
            RealPartitionRole.BasicData, Offset, Size);
        var result = Returned(partitionGuid: PartitionGuid);

        Assert.Null(Verify(command, Target(StorageObjectKind.OsDisk, DiskId),
            result, before, after));

        var cleanPart = part with { FileSystem = "RAW", DriveLetter = "", AllocationUnitSize = null };
        var clean = Topology(Snapshot() with { Partitions = [cleanPart] });
        Assert.NotNull(Verify(command, Target(StorageObjectKind.OsDisk, DiskId),
            result, before, clean));
    }

    [Fact]
    public void DriveLetterChangeRejectsAnExtraLetterButPreservesOtherMountPaths()
    {
        var beforePart = Partition("partition:data");
        var before = Topology(Snapshot() with
        {
            Partitions = [beforePart],
            Volumes = [Volume("volume:data", beforePart.StableId, [@"C:\mount\data\"])]
        });
        var afterPart = beforePart with { DriveLetter = "G" };
        var expected = Topology(Snapshot() with
        {
            Partitions = [afterPart],
            Volumes = [Volume("volume:data", afterPart.StableId,
                [@"C:\mount\data\", @"G:\"])]
        });
        var extra = Topology(Snapshot() with
        {
            Partitions = [afterPart],
            Volumes = [Volume("volume:data", afterPart.StableId,
                [@"C:\mount\data\", @"G:\", @"F:\"])]
        });
        var command = new SetDriveLetterCommand(
            RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, beforePart.StableId)),
            null, 'G');
        var target = Target(StorageObjectKind.Partition, beforePart.StableId, PartitionGuid);

        Assert.NotNull(Verify(command, target, Returned(), before, expected));
        Assert.Null(Verify(command, target, Returned(), before, extra));
    }

    [Fact]
    public void FormatWithoutLabelRequiresAnEmptyPartitionAndVolumeLabel()
    {
        var oldPart = Partition("partition:data") with
        {
            FileSystem = "NTFS", FileSystemLabel = "OLD", AllocationUnitSize = 65536
        };
        var before = Topology(Snapshot() with
        {
            Partitions = [oldPart],
            Volumes = [Volume("volume:data", oldPart.StableId, []) with
            {
                FileSystem = "NTFS", FileSystemLabel = "OLD", AllocationUnitSize = 65536
            }]
        });
        var formatted = oldPart with { FileSystemLabel = "" };
        var after = Topology(Snapshot() with
        {
            Partitions = [formatted],
            Volumes = [Volume("volume:data", formatted.StableId, []) with
            {
                FileSystem = "NTFS", AllocationUnitSize = 65536
            }]
        });
        var staleLabel = Topology(Snapshot() with
        {
            Partitions = [oldPart],
            Volumes = [Volume("volume:data", oldPart.StableId, []) with
            {
                FileSystem = "NTFS", FileSystemLabel = "OLD", AllocationUnitSize = 65536
            }]
        });
        var mismatchedVolume = Topology(Snapshot() with
        {
            Partitions = [formatted],
            Volumes = [Volume("volume:data", formatted.StableId, []) with
            {
                FileSystem = "exFAT", AllocationUnitSize = 4096
            }]
        });
        var unexpectedLetter = Topology(Snapshot() with
        {
            Partitions = [formatted with { DriveLetter = "G" }],
            Volumes = [Volume("volume:data", formatted.StableId, [@"G:\"]) with
            {
                FileSystem = "NTFS", AllocationUnitSize = 65536
            }]
        });
        var command = new FormatVolumeCommand(
            RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, oldPart.StableId)),
            RealFileSystem.Ntfs, 65536, false, null);
        var target = Target(StorageObjectKind.Partition, oldPart.StableId, PartitionGuid);

        Assert.NotNull(Verify(command, target, Returned(), before, after));
        Assert.Null(Verify(command, target, Returned(), before, staleLabel));
        Assert.Null(Verify(command, target, Returned(), before, mismatchedVolume));
        Assert.Null(Verify(command, target, Returned(), before, unexpectedLetter));
    }

    [Fact]
    public void RenamedVolumeWithoutGuidIsVerifiedThroughItsExactParentPartition()
    {
        var part = Partition("partition:data");
        var volume = Volume("volume:data", part.StableId, [@"E:\"])
            with { FileSystemLabel = "OLD" };
        var before = Topology(Snapshot() with { Partitions = [part], Volumes = [volume] });
        var renamed = volume with { FileSystemLabel = "NEW" };
        var after = Topology(Snapshot() with { Partitions = [part], Volumes = [renamed] });
        var reference = RealTargetReference.ForExisting(Id(StorageObjectKind.Volume, volume.StableId));
        var target = WindowsRealStorageTargetBuilder.Build(before, reference,
            new Dictionary<string, string>());

        Assert.Null(after.Facts.Objects.Single(item => item.Id == volume.StableId).Field("Guid"));
        Assert.Equal(PartitionGuid, target.PartitionGuid);
        Assert.NotNull(Verify(new RenameVolumeCommand(reference, "NEW"), target,
            Returned(volume.StableId, volume.StableId), before, after));
        Assert.Null(Verify(new RenameVolumeCommand(reference, "NEW"), target,
            Returned(volume.StableId, volume.StableId), before, before));
    }

    [Theory]
    [InlineData("parent-guid")]
    [InlineData("partition-number")]
    [InlineData("disk-number")]
    [InlineData("disk-identity")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("other-parent")]
    [InlineData("missing-parent")]
    public void RenamedVolumeRejectsChangedParentOrGeometry(string change)
    {
        var part = Partition("partition:data");
        var volume = Volume("volume:data", part.StableId, []) with { FileSystemLabel = "OLD" };
        var baseline = Snapshot() with { Partitions = [part], Volumes = [volume] };
        var before = Topology(baseline);
        var reference = RealTargetReference.ForExisting(Id(StorageObjectKind.Volume, volume.StableId));
        var target = WindowsRealStorageTargetBuilder.Build(before, reference,
            new Dictionary<string, string>());
        var renamed = volume with { FileSystemLabel = "NEW" };
        var changedPart = change switch
        {
            "parent-guid" => part with { Guid = "8263f3a4-bcce-4ff7-ade7-0d4aa0419f7b" },
            "partition-number" => part with { PartitionNumber = 2 },
            "disk-number" => part with { DiskNumber = 8 },
            "disk-identity" => part with { OsDiskStableId = "disk:replacement" },
            "offset" => part with { Offset = Offset + (1L << 20) },
            "size" => part with { Size = Size + (1L << 20) },
            _ => part
        };
        var changed = baseline with { Partitions = [changedPart], Volumes = [renamed] };
        if (change == "disk-identity")
            changed = changed with { OsDisks = [baseline.OsDisks[0] with { StableId = "disk:replacement" }] };
        if (change == "other-parent")
        {
            var other = part with
            {
                StableId = "partition:other", PartitionNumber = 2,
                Guid = "8263f3a4-bcce-4ff7-ade7-0d4aa0419f7b", Offset = Offset + Size
            };
            changed = changed with
            {
                Partitions = [part, other],
                Volumes = [renamed with { PartitionStableId = other.StableId }]
            };
        }
        if (change == "missing-parent")
            changed = changed with { Volumes = [renamed with { PartitionStableId = null }] };

        Assert.Null(Verify(new RenameVolumeCommand(reference, "NEW"), target,
            Returned(volume.StableId, volume.StableId), before, Topology(changed)));
    }

    [Theory]
    [InlineData("UniqueId")]
    [InlineData("ObjectId")]
    public void RenamedVolumeRejectsChangedProviderIdentity(string changedField)
    {
        var part = Partition("partition:data");
        var volume = Volume("volume:data", part.StableId, []) with { FileSystemLabel = "OLD" };
        var baseline = Snapshot() with { Partitions = [part], Volumes = [volume] };
        var before = Topology(baseline);
        var reference = RealTargetReference.ForExisting(Id(StorageObjectKind.Volume, volume.StableId));
        var target = WindowsRealStorageTargetBuilder.Build(before, reference,
            new Dictionary<string, string>());
        var after = Topology(baseline with
        {
            Volumes = [volume with { FileSystemLabel = "NEW" }]
        }, item => item.Id == volume.StableId ? item with
        {
            Fields = item.Fields.Select(field => field.Name == changedField
                ? WinPoolSourceField.Returned(field.Name, "replacement-identity",
                    FactValueType.String, field.SourceRef)
                : field).ToImmutableArray()
        } : item);

        Assert.Null(Verify(new RenameVolumeCommand(reference, "NEW"), target,
            Returned(volume.StableId, volume.StableId), before, after));
    }

    [Fact]
    public void NewVirtualDiskMustExposeOneRawPartitionFreeOsDisk()
    {
        var old = Snapshot() with
        {
            PhysicalDisks = [Physical() with { CanPool = false, PoolStableId = PoolId }],
            StoragePools = [Primordial(), Pool()],
            OsDisks = []
        };
        var virtualDisk = new VirtualDiskInfo("vd:new", true, "Data", "Healthy", "OK",
            "Simple", "Fixed", 1, 65536, 256L << 20, 256L << 20,
            PoolId, [], []);
        var osDisk = new OsDiskInfo("disk:vd", "Data", 9, "RAW", 256L << 20,
            false, false, false, null, virtualDisk.StableId);
        var clean = old with { VirtualDisks = [virtualDisk], OsDisks = [osDisk] };
        var partitioned = clean with
        {
            OsDisks = [osDisk with { PartitionStyle = "GPT" }],
            Partitions = [Partition("partition:unexpected") with
            {
                DiskNumber = 9, OsDiskStableId = osDisk.StableId
            }]
        };
        var command = new CreateVirtualDiskCommand(
            RealTargetReference.ForExisting(Id(StorageObjectKind.StoragePool, PoolId)),
            "Data", 256L << 20, 65536, 1);
        var target = Target(StorageObjectKind.StoragePool, PoolId);
        var result = Returned(uniqueId: virtualDisk.StableId, objectId: virtualDisk.StableId);

        Assert.NotNull(Verify(command, target, result, Topology(old), Topology(clean)));
        Assert.Null(Verify(command, target, result, Topology(old), Topology(partitioned)));
    }

    [Fact]
    public void DeletedVirtualDiskRequiresItsPartitionsAndVolumesToDisappear()
    {
        var virtualDisk = new VirtualDiskInfo("vd:old", true, "Old", "Healthy", "OK",
            "Simple", "Fixed", 1, 65536, 256L << 20, 256L << 20,
            PoolId, [], []);
        var osDisk = new OsDiskInfo("disk:vd", "Old", 9, "GPT", 256L << 20,
            false, false, false, null, virtualDisk.StableId);
        var part = Partition("partition:old") with
        {
            DiskNumber = 9, OsDiskStableId = osDisk.StableId
        };
        var volume = Volume("volume:old", part.StableId, [@"W:\"]) with
        {
            VolumeIdentity = "volume-identity-old"
        };
        var baseline = Snapshot() with
        {
            PhysicalDisks = [Physical() with { CanPool = false, PoolStableId = PoolId }],
            StoragePools = [Primordial(), Pool()],
            OsDisks = [osDisk], VirtualDisks = [virtualDisk],
            Partitions = [part], Volumes = [volume]
        };
        var clean = baseline with
        {
            OsDisks = [Snapshot().OsDisks[0]], VirtualDisks = [],
            Partitions = [], Volumes = []
        };
        var residual = clean with
        {
            Partitions = [part with { StableId = "partition:rekeyed",
                DiskNumber = 7, OsDiskStableId = DiskId }],
            Volumes = [volume with { StableId = "volume:rekeyed",
                PartitionStableId = "partition:rekeyed" }]
        };
        var command = new DeleteVirtualDiskCommand(
            RealTargetReference.ForExisting(Id(StorageObjectKind.VirtualDisk, virtualDisk.StableId)));
        var target = Target(StorageObjectKind.VirtualDisk, virtualDisk.StableId);

        Assert.NotNull(Verify(command, target, Returned(), Topology(baseline), Topology(clean)));
        Assert.Null(Verify(command, target, Returned(), Topology(baseline), Topology(residual)));
    }

    private static WindowsRealStorageBackend.VerifiedPostcondition? Verify(
        RealStorageCommand command, WindowsStorageCommandTarget target,
        WindowsStorageCommandResult result, WindowsRealStorageTopology before,
        WindowsRealStorageTopology after) =>
        WindowsRealStorageBackend.VerifyAfter(command, target, result, before, after);

    private static StorageObjectId Id(StorageObjectKind kind, string key) => new(System, kind, key);

    private static WindowsStorageCommandResult Returned(
        string? uniqueId = null, string? objectId = null, string? partitionGuid = null) =>
        new(true, "provider.returned", uniqueId, objectId, partitionGuid,
            null, null, null, null);

    private static WindowsStorageCommandTarget Target(
        StorageObjectKind kind, string uniqueId, string guid = "") =>
        new(kind, uniqueId, uniqueId, "SERIAL-7", DiskId,
            @"\\.\PHYSICALDRIVE7", 7, 1, guid, Offset, Size,
            DiskId, PhysicalId, "subsystem:7", "synthetic-fingerprint");

    private static VolumeInfo Volume(string id, string partitionId, IReadOnlyList<string> paths) =>
        new(id, true, partitionId, "RAW", "", Size, Size, null,
            "Healthy", "OK", paths);

    private static PartitionInfo Partition(string id) =>
        new(id, true, 7, 1, "GPT", Offset, Size, false, false,
            "", "", "RAW", null, Size, "Healthy", "OK", "", DiskId,
            PartitionTypeId: BasicData, Guid: PartitionGuid, GptType: BasicData);

    private static PhysicalDiskInfo Physical() =>
        new(PhysicalId, true, "Synthetic", "Synthetic", "SERIAL-7",
            "SATA", "HDD", 1L << 30, 512, 4096, "Healthy", "OK",
            true, "", 7, false, false, false, false, "pool:primordial");

    private static StoragePoolInfo Primordial() =>
        new("pool:primordial", true, "Primordial", true, "Healthy", "OK",
            1L << 30, 0, "subsystem:7", [PhysicalId]);

    private static StoragePoolInfo Pool() =>
        new(PoolId, true, "Pool", false, "Healthy", "OK",
            1L << 30, 0, "subsystem:7", [PhysicalId]);

    private static StorageSnapshot Snapshot() => StorageSnapshot.Empty(Environment.MachineName) with
    {
        SnapshotVersion = "synthetic", ScannedAt = Now,
        Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
            "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
        StorageSubsystems = [new StorageSubsystemInfo("subsystem:7", "Storage Spaces", "Healthy", "OK")],
        PhysicalDisks = [Physical()], StoragePools = [Primordial()],
        OsDisks = [new OsDiskInfo(DiskId, "Synthetic", 7, "GPT", 1L << 30,
            false, false, false, PhysicalId, null)]
    };

    private static WindowsRealStorageTopology Topology(StorageSnapshot snapshot,
        Func<WinPoolSourceObject, WinPoolSourceObject>? transform = null)
    {
        var facts = WinPoolSimulationFacts.Create(snapshot, System);
        var sources = facts.Sources.Select(source => source with
        {
            Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                ? FactOrigin.StorageCim : FactOrigin.Win32
        }).ToImmutableArray();
        foreach (var name in new[] { "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_Disk",
                     "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk" })
        {
            if (sources.Any(source => source.ClassName == name)) continue;
            sources = sources.Add(new WinPoolSource("synthetic-empty:" + name,
                FactOrigin.StorageCim, "root/microsoft/windows/storage", name,
                Now, CollectionPurpose.Storage));
        }
        var objects = facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
            ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned(
                "Path", @"\\.\PHYSICALDRIVE7", FactValueType.String, item.SourceRef)) }
            : item).ToImmutableArray();
        if (transform is not null) objects = objects.Select(transform).ToImmutableArray();
        facts = facts with
        {
            IsSimulation = false, InventoryVersion = "synthetic",
            InventoryCapturedAt = Now, Sources = sources, Objects = objects
        };
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "local:synthetic", StorageSystemKind.Local, "Synthetic", facts, [], Now)
        { SystemId = System };
        return new WindowsRealStorageTopology(document, "machine:synthetic", Now);
    }
}
