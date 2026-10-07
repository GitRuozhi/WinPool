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

        public Fixture(bool withVirtualDisk = false)
        {
            var snapshot = StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "pool-role-inventory", ScannedAt = Now,
                Computer = new("computer:local", Environment.MachineName, "Windows", "10", "19045", Now.AddHours(-1)),
                StorageSubsystems = [new("subsystem:local", "Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = [new(Physical, true, "Member", "Model", "SERIAL", "SATA", "HDD", 1L << 30,
                    512, 4096, "Healthy", "OK", false, "InPool", 7, false, false, false, false, Pool)],
                StoragePools = [new(Pool, true, "Pool", false, "Healthy", "OK", 1L << 30, 0, "subsystem:local", [Physical])],
                VirtualDisks = withVirtualDisk ? [new(Virtual, true, "VD", "Healthy", "OK", "Simple", "Fixed", 1,
                    65536, 128L << 20, 128L << 20, Pool, [], [9])] : [],
                OsDisks = withVirtualDisk ? [new(Disk, "VD", 9, "RAW", 128L << 20, false, false, false, null, Virtual)] : []
            };
            Facts = WinPoolSimulationFacts.Create(snapshot, system);
            // A pooled physical member has no basic-disk/Win32/native view.
            // Remove the simulator's direct physical supplements so the test
            // exercises current association evidence, without known role fields.
            var physicalSupplements = Facts.Relationships.Where(item => item.FromId == Physical
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
            foreach (var name in new[] { "IsBoot", "IsSystem", "IsPageFile", "IsCrashDump" }) SetField(Physical, name, null);
            Facts = Facts with { Objects = Facts.Objects.Select(item => item.Id == Physical ? item with
            { Fields = item.Fields.Where(field => field.Name is not ("InterfaceType" or "ProvisioningType" or "PnpDeviceId")).ToImmutableArray() } : item).ToImmutableArray() };
            if (withVirtualDisk)
            {
                SetField(Disk, "Path", @"\\?\storage#virtual#roles");
                SetField(Disk, "IsReadOnly", false);
                SetField(Disk, "IsClustered", false);
                const string sourceId = "native:source";
                var fields = new[] {
                    WinPoolSourceField.Returned("DiskNumber", 9L, FactValueType.Int64, sourceId),
                    WinPoolSourceField.Returned("IsPageFile", false, FactValueType.Boolean, sourceId),
                    WinPoolSourceField.Returned("IsCrashDump", false, FactValueType.Boolean, sourceId) };
                Facts = Facts with
                {
                    Sources = Facts.Sources.Add(new(sourceId, FactOrigin.Native, "winpool/native", "Windows.DiskRoles", Now, CollectionPurpose.Storage)),
                    Objects = Facts.Objects.Add(new(Native, FactObjectType.HardwareSupplement, sourceId, "native-id", true, fields.ToImmutableArray())),
                    Relationships = Facts.Relationships.Add(new(Disk, Native, "disk-supplement", Now))
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
