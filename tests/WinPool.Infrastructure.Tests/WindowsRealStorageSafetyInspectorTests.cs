using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageSafetyInspectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
    private const string PhysicalId = "physical:synthetic";
    private const string DiskId = "osdisk:synthetic";
    private const string DataId = "partition:data";
    private const string EfiId = "partition:efi";

    [Fact]
    public async Task A05ChecksSelectedBasicDataWithoutTreatingSiblingEfiAsDeletionTarget()
    {
        var fixture = new Fixture();
        var observed = new List<string>();
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
            observed.AddRange(volumes.Select(item => item.StableId)));
        var topology = await fixture.Capture();
        Assert.True(topology.Snapshot.FieldIssues.Count == 0,
            string.Join("; ", topology.Snapshot.FieldIssues.Select(issue =>
                $"{issue.ObjectId}:{issue.FieldName}:{issue.Reason}")));
        var closure = fixture.Closure(topology);

        await inspector.ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);

        Assert.Equal(["volume:data"], observed);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(EfiId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, new ClearDiskCommand(RealTargetReference.ForExisting(
                fixture.Id(StorageObjectKind.OsDisk, DiskId)), false), CancellationToken.None));

        fixture.MarkTargetShadowCopy();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData("EfiSystem", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b")]
    [InlineData("MicrosoftReserved", "e3c9e316-0b5c-4db8-817d-f92df00215ae")]
    [InlineData("WindowsRecovery", "de94bba4-06d1-4d40-a16a-bfd50179d6ac")]
    public async Task H04CanDeleteExactNewNonSystemPartitionOfSupportedRole(
        string roleName, string roleGuid)
    {
        var fixture = new Fixture();
        fixture.SetTargetRole(roleName, roleGuid);
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { });

        await inspector.ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);

        fixture.MarkTargetBoot();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task A05RejectsUnlistedGptRoleBeforeSafetyInspection()
    {
        var fixture = new Fixture();
        fixture.SetTargetRole("Unknown", "11111111-2222-3333-4444-555555555555");
        var topology = await fixture.Capture();
        Assert.Throws<InvalidDataException>(() => fixture.Closure(topology));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task H04RejectsReadOnlyOrUnknownSelectedPartitionState(bool? isReadOnly)
    {
        var fixture = new Fixture();
        fixture.SetTargetRole("MicrosoftReserved", "e3c9e316-0b5c-4db8-817d-f92df00215ae");
        fixture.SetTargetReadOnlyState(isReadOnly);
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);

        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(
            null, (_, _) => { }).ValidateAsync(
                topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task A05StillRejectsTargetBitLockerUnknownRuntimeUseAndProtectedPhysicalMember()
    {
        var fixture = new Fixture();
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var deletion = fixture.Delete(DataId);

        var unknownEncryption = new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
        {
            Assert.Single(volumes);
            throw new InvalidDataException("Synthetic BitLocker state unknown.");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => unknownEncryption.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        var activePath = new WindowsRealStorageSafetyInspector([@"E:\WinPool"], (_, _) => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => activePath.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        fixture.RemoveTargetVolume();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => activePath.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        fixture.ProtectPhysicalMember();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(
            null, (_, _) => { }).ValidateAsync(topology, closure, deletion, CancellationToken.None));
    }

    private sealed class Fixture
    {
        private readonly SystemId system = SystemId.New();
        private StorageSnapshot snapshot = InitialSnapshot();
        private bool targetShadowCopy;
        private bool replaceTargetReadOnly;
        private bool? targetReadOnlyState;
        private readonly WindowsRealStorageTopologyReader reader;

        public Fixture() => reader = new WindowsRealStorageTopologyReader(
            new SyntheticFactSource(() => Document()), new SyntheticMachineIdentity(),
            new FixedTimeProvider());

        public StorageObjectId Id(StorageObjectKind kind, string key) => new(system, kind, key);
        public DeletePartitionCommand Delete(string id) => new(
            RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, id)));
        public Task<WindowsRealStorageTopology> Capture() => reader.CaptureAsync(CancellationToken.None);
        public RealTargetClosure Closure(WindowsRealStorageTopology topology) =>
            topology.RequireSinglePhysicalClosure([Id(StorageObjectKind.Partition, DataId)]);
        public void ProtectPhysicalMember() => snapshot = snapshot with
        {
            PhysicalDisks = [snapshot.PhysicalDisks[0] with { HealthStatus = "Unhealthy" }]
        };
        public void RemoveTargetVolume() => snapshot = snapshot with
        {
            Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != DataId).ToArray()
        };
        public void MarkTargetShadowCopy() => targetShadowCopy = true;
        public void SetTargetReadOnlyState(bool? value)
        {
            replaceTargetReadOnly = true;
            targetReadOnlyState = value;
        }
        public void SetTargetRole(string type, string guid)
        {
            snapshot = snapshot with
            {
                Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                    ? item with
                    {
                        Type = type, PartitionTypeId = guid, GptType = guid,
                        IsHidden = true, DriveLetter = "", Path = "", FileSystem = "",
                        FileSystemLabel = ""
                    }
                    : item).ToArray(),
                Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != DataId).ToArray()
            };
        }
        public void MarkTargetBoot() => snapshot = snapshot with
        {
            Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                ? item with { IsBoot = true } : item).ToArray()
        };

        private StorageSystemDocument Document()
        {
            var facts = WinPoolSimulationFacts.Create(snapshot, system);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[] { "MSFT_StorageTier", "MSFT_VirtualDisk" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage));
            }
            var objects = facts.Objects.Select(item =>
            {
                if (item.ObjectType == FactObjectType.Disk)
                    return item with { Fields = item.Fields
                        .Add(WinPoolSourceField.Returned("Path", @"\\.\PHYSICALDRIVE7",
                            FactValueType.String, item.SourceRef))
                        .Add(WinPoolSourceField.Returned("IsClustered", false,
                            FactValueType.Boolean, item.SourceRef)) };
                if (item.Id == DataId && (targetShadowCopy || replaceTargetReadOnly))
                {
                    var fields = item.Fields;
                    if (targetShadowCopy)
                    {
                        var selectedShadow = item.Field("IsShadowCopy")!;
                        fields = fields.Replace(selectedShadow,
                            WinPoolSourceField.Returned("IsShadowCopy", true,
                                FactValueType.Boolean, item.SourceRef));
                    }
                    if (replaceTargetReadOnly)
                    {
                        var selectedReadOnly = item.Field("IsReadOnly")!;
                        fields = fields.Replace(selectedReadOnly,
                            WinPoolSourceField.Returned("IsReadOnly", targetReadOnlyState,
                                FactValueType.Boolean, item.SourceRef));
                    }
                    return item with { Fields = fields };
                }
                if (item.Id != EfiId) return item;
                var shadow = item.Field("IsShadowCopy")!;
                return item with { Fields = item.Fields.Replace(shadow,
                    WinPoolSourceField.Returned("IsShadowCopy", true,
                        FactValueType.Boolean, item.SourceRef)) };
            }).ToImmutableArray();
            facts = facts with
            {
                IsSimulation = false, InventoryVersion = "synthetic-inventory",
                InventoryCapturedAt = Now, Sources = sources, Objects = objects
            };
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
                "local:synthetic", StorageSystemKind.Local, "Synthetic Storage", facts, [], Now)
            { SystemId = system };
        }

        private static StorageSnapshot InitialSnapshot()
        {
            var data = new PartitionInfo(DataId, true, 7, 2, "Primary",
                17L << 20, 128L << 20, false, false, "E", "Data", "NTFS",
                65536, 64L << 20, "Healthy", "OK", @"E:\", DiskId,
                PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                Guid: "8c4b7c34-04ba-46b1-80d7-9d967441a02d");
            var efi = new PartitionInfo(EfiId, true, 7, 1, "EfiSystem",
                1L << 20, 16L << 20, false, false, "S", "EFI", "FAT32",
                4096, 8L << 20, "Healthy", "OK", @"S:\", DiskId,
                PartitionTypeId: "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                Guid: "2f8ae502-1e4e-4d94-b190-6284ccb62bea");
            return StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "synthetic-inventory", ScannedAt = Now,
                Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
                    "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
                StorageSubsystems = [new StorageSubsystemInfo("subsystem:local", "Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = [new PhysicalDiskInfo(PhysicalId, true, "Synthetic Candidate", "Model",
                    "SERIAL-1", "SATA", "HDD", 1L << 30, 512, 4096, "Healthy", "OK",
                    false, "Partitioned", 7, false, false, false, false, "pool:primordial")],
                StoragePools = [new StoragePoolInfo("pool:primordial", true, "Primordial", true,
                    "Healthy", "OK", 1L << 30, 0, "subsystem:local", [PhysicalId])],
                OsDisks = [new OsDiskInfo(DiskId, "Synthetic Candidate", 7, "GPT", 1L << 30,
                    false, false, false, PhysicalId, null)],
                Partitions = [efi, data],
                Volumes = [new VolumeInfo("volume:efi", true, EfiId, "FAT32", "EFI",
                        16L << 20, 8L << 20, 4096, "Healthy", "OK", [@"S:\"]),
                    new VolumeInfo("volume:data", true, DataId, "NTFS", "Data",
                        128L << 20, 64L << 20, 65536, "Healthy", "OK", [@"E:\"])]
            };
        }
    }

    private sealed class SyntheticFactSource(Func<StorageSystemDocument> capture) : IWindowsRealStorageFactSource
    {
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken token) => Task.FromResult(capture());
    }
    private sealed class SyntheticMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken token) => Task.FromResult("synthetic-machine-binding");
    }
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
