using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsPoolMemberRoleEvidenceTests
{
    private const string Physical = "physical:member";
    private const string Pool = "pool:member";
    private const string Tier = "tier:member";
    private const string Tier2 = "tier:member-2";
    private const string Disk = "osdisk:virtual";
    private const string Virtual = "vd:member";
    private const string Native = "native:roles";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task EmptyConcretePoolProvesRolesAndPersistsProofWithoutChangingRawNulls()
    {
        var fixture = new Fixture();
        var topology = fixture.Topology();
        var closure = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.StoragePool, Pool)]);
        var proof = Assert.IsType<WindowsPoolMemberRoleEvidence>(closure.PoolMemberRoleEvidence);
        Assert.Equal(topology.InventoryVersion, proof.InventoryVersion);
        Assert.Equal(Physical, proof.PhysicalStableId);
        Assert.Equal(Pool, proof.PoolStableId);
        Assert.Empty(proof.AssociatedOsDiskIds);
        Assert.False(proof.IsBoot || proof.IsSystem || proof.IsPageFile || proof.IsCrashDump);
        Assert.Equal(JsonValueKind.Null, topology.RequireObject(fixture.Id(StorageObjectKind.PhysicalDisk, Physical))
            .Field("IsBoot")!.Value!.Value.ValueKind);
        Assert.Contains(topology.Snapshot.FieldIssues, item => item.ObjectId == Physical && item.FieldName == "IsBoot");
        var safety = await new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes))
            .ValidateWithEvidenceAsync(topology, closure,
                new RenamePoolCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StoragePool, Pool)), "New name"),
                CancellationToken.None);
        Assert.Equal(proof, safety!.PoolMemberRoleEvidence);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (_, _) => { })
            .ValidateAsync(topology, closure with { PoolMemberRoleEvidence = null },
                new RenamePoolCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StoragePool, Pool)), "New name"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingPhysicalRoleFieldsUsePositiveCurrentPoolEvidence()
    {
        var fixture = new Fixture();
        fixture.Facts = fixture.Facts with { Objects = fixture.Facts.Objects.Select(item => item.Id == Physical ? item with
        { Fields = item.Fields.Where(field => field.Name is not ("IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump")).ToImmutableArray() } : item).ToImmutableArray() };
        var topology = fixture.Topology();
        var closure = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.StoragePool, Pool)]);
        Assert.NotNull(closure.PoolMemberRoleEvidence);
        Assert.Null(topology.RequireObject(fixture.Id(StorageObjectKind.PhysicalDisk, Physical)).Field("IsBoot"));
        Assert.Contains(topology.Snapshot.FieldIssues, issue => issue.ObjectId == Physical
            && issue.FieldName == "IsBoot" && issue.State == FieldReadState.NotCollected);
        var safety = await new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes))
            .ValidateWithEvidenceAsync(topology, closure,
                new RenamePoolCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StoragePool, Pool)), "New name"), CancellationToken.None);
        Assert.Equal(closure.PoolMemberRoleEvidence, safety!.PoolMemberRoleEvidence);
    }

    [Fact]
    public async Task VirtualOsDiskRolesAreAggregatedFromExactCurrentNativeBinding()
    {
        var fixture = new Fixture(withVirtualDisk: true);
        var topology = fixture.Topology();
        var closure = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.StoragePool, Pool)]);
        Assert.Equal([Disk], closure.PoolMemberRoleEvidence!.AssociatedOsDiskIds);
        var safety = await new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes))
            .ValidateWithEvidenceAsync(topology, closure,
                new RenamePoolCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StoragePool, Pool)), "New name"), CancellationToken.None);
        Assert.Equal(closure.PoolMemberRoleEvidence, safety!.PoolMemberRoleEvidence);
    }

    [Fact]
    public async Task ExactPhysicalMemberSetIsSortedBoundToPoolAndCarriesEveryMemberRoleProof()
    {
        var fixture = new Fixture(withVirtualDisk: true, physicalCount: 2);
        var topology = fixture.Topology();
        var reverseTargets = fixture.PhysicalIds.Reverse()
            .Select(id => fixture.Id(StorageObjectKind.PhysicalDisk, id)).ToArray();
        var closure = topology.RequireExactPhysicalMemberSet(
            fixture.Id(StorageObjectKind.StoragePool, Pool), reverseTargets);

        Assert.Equal(fixture.PhysicalIds.Order(StringComparer.Ordinal),
            closure.Members.Select(item => item.PhysicalDiskId));
        Assert.All(closure.Members, item =>
        {
            Assert.Equal(item.PhysicalDiskId, item.UniqueId);
            Assert.Equal(item.PhysicalDiskId, item.ObjectId);
            Assert.True(item.SizeBytes > 0);
            Assert.Equal(topology.InventoryVersion, item.PoolMemberRoleEvidence.InventoryVersion);
            Assert.Equal(item.PhysicalDiskId, item.PoolMemberRoleEvidence.PhysicalStableId);
            Assert.Equal(Pool, item.PoolMemberRoleEvidence.PoolStableId);
            Assert.Equal([Disk], item.PoolMemberRoleEvidence.AssociatedOsDiskIds);
            Assert.False(item.PoolMemberRoleEvidence.IsBoot || item.PoolMemberRoleEvidence.IsSystem
                || item.PoolMemberRoleEvidence.IsPageFile || item.PoolMemberRoleEvidence.IsCrashDump);
        });

        var sameSet = topology.RequireExactPhysicalMemberSet(
            fixture.Id(StorageObjectKind.StoragePool, Pool),
            fixture.PhysicalIds.Select(id => fixture.Id(StorageObjectKind.PhysicalDisk, id)).ToArray());
        Assert.Equal(closure.Fingerprint, sameSet.Fingerprint);
        Assert.Equal(closure.PhysicalMemberFingerprint, sameSet.PhysicalMemberFingerprint);

        var evidence = await new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes))
            .InspectAsync(topology, closure,
                new ResizeTierCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StorageTier, Tier)), 256L << 20),
                CancellationToken.None);
        Assert.Null(evidence!.PoolMemberRoleEvidence);
        Assert.Equal(fixture.PhysicalIds.Order(StringComparer.Ordinal),
            evidence.PhysicalMemberRoleEvidence!.Select(item => item.PhysicalStableId));
    }

    [Fact]
    public void StableMemberSetFingerprintDoesNotChangeWhenCurrentOsDiskAssociationChanges()
    {
        var first = new Fixture(withVirtualDisk: true, physicalCount: 2, virtualOsDiskId: "osdisk:virtual-a");
        var second = new Fixture(withVirtualDisk: true, physicalCount: 2, virtualOsDiskId: "osdisk:virtual-b");
        var firstTopology = first.Topology();
        var secondTopology = second.Topology();
        var firstClosure = firstTopology.RequireExactPhysicalMemberSet(
            first.Id(StorageObjectKind.StoragePool, Pool),
            first.PhysicalIds.Select(id => first.Id(StorageObjectKind.PhysicalDisk, id)).ToArray());
        var secondClosure = secondTopology.RequireExactPhysicalMemberSet(
            second.Id(StorageObjectKind.StoragePool, Pool),
            second.PhysicalIds.Select(id => second.Id(StorageObjectKind.PhysicalDisk, id)).ToArray());

        Assert.Equal(["osdisk:virtual-a"], firstClosure.Members[0].PoolMemberRoleEvidence.AssociatedOsDiskIds);
        Assert.Equal(["osdisk:virtual-b"], secondClosure.Members[0].PoolMemberRoleEvidence.AssociatedOsDiskIds);
        Assert.NotEqual(firstClosure.Fingerprint, secondClosure.Fingerprint);
        Assert.Equal(firstClosure.PhysicalMemberFingerprint, secondClosure.PhysicalMemberFingerprint);
    }

    [Fact]
    public void ExactPhysicalMemberSetRejectsMissingOrOverlappingPoolMembership()
    {
        var incomplete = new Fixture(physicalCount: 3);
        Assert.Throws<InvalidDataException>(() => incomplete.Topology().RequireExactPhysicalMemberSet(
            incomplete.Id(StorageObjectKind.StoragePool, Pool),
            incomplete.PhysicalIds.Take(2)
                .Select(id => incomplete.Id(StorageObjectKind.PhysicalDisk, id)).ToArray()));

        var overlappingPool = new Fixture(withSecondPoolSharingMember: true, physicalCount: 2);
        Assert.Throws<InvalidDataException>(() => overlappingPool.Topology().RequireExactPhysicalMemberSet(
            overlappingPool.Id(StorageObjectKind.StoragePool, Pool),
            overlappingPool.PhysicalIds
                .Select(id => overlappingPool.Id(StorageObjectKind.PhysicalDisk, id)).ToArray()));

        var chainedDisk = new Fixture(physicalCount: 3);
        var outsiderId = chainedDisk.PhysicalIds[2];
        chainedDisk.Facts = chainedDisk.Facts with
        {
            Relationships = chainedDisk.Facts.Relationships
                .Where(item => !(item.Kind == "pool-member" && item.FromId == Pool && item.ToId == outsiderId))
                .Append(new(chainedDisk.PhysicalIds[0], outsiderId, "same-device", Now))
                .ToImmutableArray()
        };
        Assert.Throws<InvalidDataException>(() => chainedDisk.Topology().RequireExactPhysicalMemberSet(
            chainedDisk.Id(StorageObjectKind.StoragePool, Pool),
            chainedDisk.PhysicalIds.Take(2)
                .Select(id => chainedDisk.Id(StorageObjectKind.PhysicalDisk, id)).ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactPhysicalMemberClosureDoesNotImposeMaximumTemplateCount(int tierCount)
    {
        var fixture = new Fixture(withVirtualDisk: tierCount > 0, physicalCount: 2, tierCount: tierCount);
        var closure = fixture.Topology().RequireExactPhysicalMemberSet(
            fixture.Id(StorageObjectKind.StoragePool, Pool),
            fixture.PhysicalIds.Select(id => fixture.Id(StorageObjectKind.PhysicalDisk, id)).ToArray());

        Assert.Equal(tierCount, closure.Objects.Count(item => item.ObjectType == FactObjectType.StorageTier));
    }

    [Fact]
    public async Task ExactPhysicalMemberSetRejectsProtectedRoleAndInspectorRejectsChangedClosure()
    {
        var protectedMember = new Fixture(physicalCount: 2);
        protectedMember.SetField(protectedMember.PhysicalIds[1], "IsBoot", true);
        Assert.Throws<InvalidDataException>(() => protectedMember.Topology().RequireExactPhysicalMemberSet(
            protectedMember.Id(StorageObjectKind.StoragePool, Pool),
            protectedMember.PhysicalIds.Select(id => protectedMember.Id(StorageObjectKind.PhysicalDisk, id)).ToArray()));

        var fixture = new Fixture(withVirtualDisk: true, physicalCount: 2);
        var topology = fixture.Topology();
        var closure = topology.RequireExactPhysicalMemberSet(
            fixture.Id(StorageObjectKind.StoragePool, Pool),
            fixture.PhysicalIds.Select(id => fixture.Id(StorageObjectKind.PhysicalDisk, id)).ToArray());
        var modified = closure with { PhysicalMemberFingerprint = "modified" };
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (_, _) => { })
            .InspectAsync(topology, modified,
                new ResizeTierCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StorageTier, Tier)), 256L << 20),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("IsBoot")]
    [InlineData("IsSystem")]
    [InlineData("IsPageFile")]
    [InlineData("IsCrashDump")]
    public void ProtectedVirtualOsDiskRoleCannotHideBehindNullPhysicalFields(string role)
    {
        var fixture = new Fixture(withVirtualDisk: true);
        fixture.SetField(role is "IsBoot" or "IsSystem" ? Disk : Native, role, true);
        Assert.Throws<InvalidDataException>(() => fixture.Closure());
    }

    [Theory]
    [InlineData("MissingPool")]
    [InlineData("RetainedPool")]
    [InlineData("MissingSubsystem")]
    [InlineData("MissingVirtualOwner")]
    [InlineData("MissingDiskOwner")]
    [InlineData("AmbiguousDiskOwner")]
    [InlineData("MissingNativeRoles")]
    [InlineData("WrongNativeNumber")]
    [InlineData("NullNativeRole")]
    [InlineData("FailedNativeSource")]
    [InlineData("StaleNativeSource")]
    [InlineData("FailedVirtualSource")]
    [InlineData("PhysicalRoleFailed")]
    [InlineData("PhysicalRoleTrue")]
    [InlineData("PhysicalRoleMalformed")]
    [InlineData("HealthFailed")]
    public void MissingOrUnknownAssociationAndRoleEvidenceFailsClosed(string fault)
    {
        var fixture = new Fixture(withVirtualDisk: true);
        switch (fault)
        {
            case "MissingPool": fixture.RemoveRelationship("pool-member"); break;
            case "RetainedPool": fixture.Facts = fixture.Facts with { Relationships = fixture.Facts.Relationships.Select(item =>
                item.Kind == "pool-member" ? item with { IsRetained = true } : item).ToImmutableArray() }; break;
            case "MissingSubsystem": fixture.RemoveRelationship("subsystem-pool"); break;
            case "MissingVirtualOwner": fixture.RemoveRelationship("pool-virtual-disk"); break;
            case "MissingDiskOwner": fixture.RemoveRelationship("same-device"); break;
            case "AmbiguousDiskOwner": fixture.Facts = fixture.Facts with { Relationships = fixture.Facts.Relationships.Add(
                new(Physical, Disk, "same-device", Now)) }; break;
            case "MissingNativeRoles": fixture.RemoveRelationship("disk-supplement"); break;
            case "WrongNativeNumber": fixture.SetField(Native, "DiskNumber", 99L); break;
            case "NullNativeRole": fixture.SetField(Native, "IsPageFile", null); break;
            case "FailedNativeSource": fixture.ChangeSource("Windows.DiskRoles", failed: true); break;
            case "StaleNativeSource": fixture.ChangeSource("Windows.DiskRoles", stale: true); break;
            case "FailedVirtualSource": fixture.ChangeSource("MSFT_VirtualDisk", failed: true); break;
            case "PhysicalRoleFailed": fixture.SetField(Physical, "IsBoot", null, failed: true); break;
            case "PhysicalRoleTrue": fixture.SetField(Physical, "IsBoot", true); break;
            case "PhysicalRoleMalformed": fixture.SetField(Physical, "IsBoot", "Unknown"); break;
            case "HealthFailed": fixture.SetField(Physical, "HealthStatus", null, failed: true); break;
        }
        if (fault == "AmbiguousDiskOwner")
        {
            Assert.Equal(2, fixture.Facts.Relationships.Count(item => item.Kind == "same-device" && item.ToId == Disk));
            Assert.Contains(fixture.Facts.Relationships, item => item.Kind == "same-device" && item.FromId == Virtual && item.ToId == Disk);
            Assert.Contains(fixture.Facts.Relationships, item => item.Kind == "same-device" && item.FromId == Physical && item.ToId == Disk);
            // Both edges lend known role fields to the physical projection.
            // Ownership must remain a barrier even when role issues vanish.
            Assert.DoesNotContain(fixture.Topology().Snapshot.FieldIssues, issue => issue.ObjectId == Physical
                && issue.FieldName is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump");
        }
        Assert.Throws<InvalidDataException>(() => fixture.Closure());
    }

    [Fact]
    public void OptionalPhysicalPresentationFieldsDoNotSuppressMandatorySafetyFields()
    {
        var fixture = new Fixture();
        Assert.NotNull(fixture.Closure().PoolMemberRoleEvidence);
        fixture.SetField(Physical, "SerialNumber", null, failed: true);
        Assert.Throws<InvalidDataException>(() => fixture.Closure());
    }

    private sealed class Fixture
    {
        private readonly SystemId system = SystemId.New();
        public WinPoolFacts Facts { get; set; }
        public IReadOnlyList<string> PhysicalIds { get; }
        private string VirtualOsDiskId { get; }

        public Fixture(bool withVirtualDisk = false, int physicalCount = 1, bool withSecondPoolSharingMember = false,
            string? virtualOsDiskId = null, int tierCount = 2)
        {
            if (physicalCount < 1) throw new ArgumentOutOfRangeException(nameof(physicalCount));
            if (withVirtualDisk && tierCount is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(tierCount));
            VirtualOsDiskId = virtualOsDiskId ?? Disk;
            PhysicalIds = physicalCount == 1
                ? [Physical]
                : Enumerable.Range(0, physicalCount).Select(index => $"physical:member-{index}").ToArray();
            var tiers = new List<StorageTierInfo>();
            if (withVirtualDisk && tierCount > 0)
                tiers.Add(new(Tier, true, "Tier 1", "HDD", "Simple", 128L << 20, 128L << 20,
                    Pool, Virtual, PhysicalIds, 1, 65536, 1, 0));
            if (withVirtualDisk && tierCount > 1)
                tiers.Add(new(Tier2, true, "Tier 2", "HDD", "Simple", 128L << 20, 128L << 20,
                    Pool, Virtual, PhysicalIds, 1, 65536, 1, 0));
            var virtualDiskBytes = checked((long)tiers.Count * (128L << 20));
            var snapshot = StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "pool-role-inventory", ScannedAt = Now,
                Computer = new("computer:local", Environment.MachineName, "Windows", "10", "19045", Now.AddHours(-1)),
                StorageSubsystems = [new("subsystem:local", "Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = PhysicalIds.Select((id, index) => new PhysicalDiskInfo(id, true,
                    "Member " + index, "Model", "SERIAL" + index, "SATA", "HDD", 1L << 30,
                    512, 4096, "Healthy", "OK", false, "InPool", 7 + index, false, false, false, false, Pool)).ToArray(),
                StoragePools = withSecondPoolSharingMember
                    ? [
                        new(Pool, true, "Pool", false, "Healthy", "OK", physicalCount * (1L << 30), 0,
                            "subsystem:local", PhysicalIds),
                        new("pool:overlap", true, "Overlapping pool", false, "Healthy", "OK", 1L << 30, 0,
                            "subsystem:local", [PhysicalIds[0]])
                    ]
                    : [new(Pool, true, "Pool", false, "Healthy", "OK", physicalCount * (1L << 30), 0,
                        "subsystem:local", PhysicalIds)],
                StorageTiers = tiers,
                VirtualDisks = withVirtualDisk ? [new(Virtual, true, "VD", "Healthy", "OK", "Simple", "Fixed", 1,
                    65536, virtualDiskBytes, virtualDiskBytes, Pool, tiers.Select(item => item.StableId).ToArray(), [9])] : [],
                OsDisks = withVirtualDisk ? [new(VirtualOsDiskId, "VD", 9, "RAW", virtualDiskBytes, false, false, false, null, Virtual)] : []
            };
            Facts = WinPoolSimulationFacts.Create(snapshot, system);
            // A pooled physical member has no basic-disk/Win32/native view.
            // Remove the simulator's direct physical supplements so the test
            // exercises current association evidence, without known role fields.
            var physicalIds = PhysicalIds.ToHashSet(StringComparer.Ordinal);
            var physicalSupplements = Facts.Relationships.Where(item => physicalIds.Contains(item.FromId)
                && item.Kind == "disk-supplement").Select(item => item.ToId).ToHashSet();
            Facts = Facts with
            {
                IsSimulation = false, InventoryVersion = snapshot.SnapshotVersion, InventoryCapturedAt = Now,
                Objects = Facts.Objects.Where(item => !physicalSupplements.Contains(item.Id)).ToImmutableArray(),
                Relationships = Facts.Relationships.Where(item => !physicalSupplements.Contains(item.ToId)).ToImmutableArray(),
                Sources = Facts.Sources.Select(item => item with { Origin = FactOrigin.StorageCim,
                    Namespace = "root/microsoft/windows/storage", CapturedAt = Now }).ToImmutableArray()
            };
            foreach (var className in new[] { "MSFT_StorageSubSystem", "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_VirtualDisk",
                         "MSFT_StorageTier", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume" })
                if (!Facts.Sources.Any(item => item.ClassName == className))
                    Facts = Facts with { Sources = Facts.Sources.Add(new("empty:" + className, FactOrigin.StorageCim,
                        "root/microsoft/windows/storage", className, Now, CollectionPurpose.Storage)) };
            foreach (var id in PhysicalIds)
                foreach (var name in new[] { "IsBoot", "IsSystem", "IsPageFile", "IsCrashDump" }) SetField(id, name, null);
            Facts = Facts with { Objects = Facts.Objects.Select(item => physicalIds.Contains(item.Id) ? item with
            { Fields = item.Fields.Where(field => field.Name is not ("InterfaceType" or "ProvisioningType" or "PnpDeviceId")).ToImmutableArray() } : item).ToImmutableArray() };
            if (withVirtualDisk)
            {
                SetField(VirtualOsDiskId, "Path", @"\\?\storage#virtual#roles");
                SetField(VirtualOsDiskId, "IsReadOnly", false);
                SetField(VirtualOsDiskId, "IsClustered", false);
                const string sourceId = "native:source";
                var fields = new[] {
                    WinPoolSourceField.Returned("DiskNumber", 9L, FactValueType.Int64, sourceId),
                    WinPoolSourceField.Returned("IsPageFile", false, FactValueType.Boolean, sourceId),
                    WinPoolSourceField.Returned("IsCrashDump", false, FactValueType.Boolean, sourceId) };
                Facts = Facts with
                {
                    Sources = Facts.Sources.Add(new(sourceId, FactOrigin.Native, "winpool/native", "Windows.DiskRoles", Now, CollectionPurpose.Storage)),
                    Objects = Facts.Objects.Add(new(Native, FactObjectType.HardwareSupplement, sourceId, "native-id", true, fields.ToImmutableArray())),
                    Relationships = Facts.Relationships.Add(new(VirtualOsDiskId, Native, "disk-supplement", Now))
                };
            }
        }

        public StorageObjectId Id(StorageObjectKind kind, string value) => new(system, kind, value);
        public WindowsRealStorageTopology Topology() => new(new(StorageSystemDocument.CurrentSchemaVersion, "local:test",
            StorageSystemKind.Local, "Test", Facts, [], Now) { SystemId = system }, "local-machine", Now);
        public RealTargetClosure Closure() => Topology().RequireSinglePhysicalClosure([Id(StorageObjectKind.StoragePool, Pool)]);
        public void RemoveRelationship(string kind) => Facts = Facts with
        { Relationships = Facts.Relationships.Where(item => item.Kind != kind).ToImmutableArray() };
        public void ChangeSource(string className, bool failed = false, bool stale = false) => Facts = Facts with
        { Sources = Facts.Sources.Select(item => item.ClassName == className ? item with
            { ReadState = failed ? FieldReadState.Failed : item.ReadState, CapturedAt = stale ? Now.AddSeconds(-1) : item.CapturedAt } : item).ToImmutableArray() };
        public void SetField(string id, string name, object? value, bool failed = false) => Facts = Facts with
        {
            Objects = Facts.Objects.Select(item => item.Id != id ? item : item with
            {
                Fields = item.Fields.Where(field => field.Name != name).Append(failed
                    ? new WinPoolSourceField(name, name is "HealthStatus" or "SerialNumber" ? FactValueType.String : FactValueType.Boolean,
                        null, FieldReadState.Failed, item.SourceRef, ReasonCode: "ProbeFailed")
                    : WinPoolSourceField.Returned(name, value, name is "Path" ? FactValueType.String
                        : name is "DiskNumber" ? FactValueType.Int64 : FactValueType.Boolean, item.SourceRef)).ToImmutableArray()
            }).ToImmutableArray()
        };
    }
}
