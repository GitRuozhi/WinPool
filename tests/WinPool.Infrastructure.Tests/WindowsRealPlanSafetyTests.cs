using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealPlanSafetyTests
{
    private const string PhysicalId = "physical:wdc";
    private const string DiskId = "osdisk:wdc";
    private const string PoolId = "pool:old";
    private const string NewPoolId = "pool:new";
    private const string SubsystemId = "subsystem:local";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");

    [Fact]
    public async Task H01RequiresOneGptDiskWithKnownPartitionsAndFreezesRawResult()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitions();
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var proposal = fixture.Proposal(OperationIntent.ClearDisk, [disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        Assert.Equal(RealOperationValidator.ClearDiskExpectedFinalState,
            plan.RealOperation!.ExpectedFinalState);
        Assert.Equal(2, plan.Targets.Count); // The Agent freezes the physical identity.
        _ = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(0, fixture.Adapter.CallCount);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Prepare(proposal with
        {
            Targets = [disk, fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]
        }));
        fixture.SetRawDisk();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task A11RebuildCanPreflightExactPoolRemovalThenSameMemberCreation()
    {
        var fixture = new Fixture();
        fixture.SetOldPool();
        var oldPool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var proposal = fixture.Proposal(OperationIntent.RebuildStoragePool, [oldPool, physical],
            [Step("remove", new DeletePoolCommand(RealTargetReference.ForExisting(oldPool))),
             Step("create", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "New Pool"), ["remove"])],
            "Old pool removed; new pool created");

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        _ = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);

        fixture.SetRawDisk();
        var verified = await fixture.VerifiedEvidence(NewlyCreatedObjectId: null);
        var next = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation.Steps[1],
            new Dictionary<string, string> { ["remove"] = verified }, CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(next.TargetEvidenceJson);
        Assert.Equal(PhysicalId, target!.UniqueId);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task A12CreationUsesCurrentSizeBoundsAndVerifiedCreatedPoolIdentity()
    {
        var fixture = new Fixture();
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var created = RealTargetReference.FromStep(StorageObjectKind.StoragePool, "pool");
        var proposal = fixture.Proposal(OperationIntent.CreateStoragePool, [physical],
            [Step("pool", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "Pool")),
             Step("vd", new CreateVirtualDiskCommand(created, "Data", 256L << 20, 65536, 1), ["pool"])],
            "Pool and virtual disk created");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Prepare(proposal with
        {
            Steps = [proposal.Steps[0], proposal.Steps[1] with
            {
                Command = new CreateVirtualDiskCommand(created, "Data", 256L << 20, 262144, 1)
            }]
        }));

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(0, fixture.Sizes.ReadCount); // The new pool is not queried before it exists.
        fixture.SetNewPool();
        var verified = await fixture.VerifiedEvidence(NewPoolId);
        var step = plan.RealOperation!.Steps[1];
        _ = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string> { ["pool"] = verified }, CancellationToken.None);
        Assert.Equal(1, fixture.Sizes.ReadCount);
        Assert.Equal(0, fixture.Adapter.CallCount);

        var wrongIdentity = await fixture.VerifiedEvidence("pool:other");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            step, new Dictionary<string, string> { ["pool"] = wrongIdentity }, CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);

        fixture.Sizes.MaximumBytes = 128L << 20;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            step, new Dictionary<string, string> { ["pool"] = verified }, CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DeleteWithUnlistedChildAndUnchangedRenameStopBeforeAdapter()
    {
        var fixture = new Fixture();
        fixture.SetOldPool(withChild: true);
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var delete = fixture.Proposal(OperationIntent.DeleteStoragePool, [pool],
            [Step("remove", new DeletePoolCommand(RealTargetReference.ForExisting(pool)))],
            "Pool removed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(delete));

        fixture.SetOldPool();
        var rename = fixture.Proposal(OperationIntent.RenameStorageObject, [pool],
            [Step("rename", new RenamePoolCommand(RealTargetReference.ForExisting(pool), "Old Pool"))],
            "Pool renamed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(rename));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static RealOperationStep Step(string id, RealStorageCommand command,
        IReadOnlyList<string>? dependencies = null) => new(id, command, dependencies ?? [],
        "fresh identity", "typed result", "explicit data loss", "synthetic provider facts");

    private sealed class Fixture
    {
        private readonly SystemId system = SystemId.New();
        private StorageSnapshot snapshot;
        private readonly WindowsRealStorageTopologyReader reader;
        public Fixture()
        {
            snapshot = BaseSnapshot();
            reader = new WindowsRealStorageTopologyReader(
                new SyntheticFactSource(() => Document()), new SyntheticMachineIdentity(),
                new FixedTimeProvider());
            Adapter = new ForbiddenAdapter();
            Sizes = new SyntheticSizeReader();
            var planner = new WindowsRealOperationPlanner(reader, new ForbiddenPartitionSizeReader(),
                new AdministratorPrivilege(), new FixedTimeProvider(), new SyntheticSafetyInspector(), Sizes);
            Backend = new WindowsRealStorageBackend(Adapter, planner, reader, new FixedTimeProvider());
        }

        public WindowsRealStorageBackend Backend { get; }
        public ForbiddenAdapter Adapter { get; }
        public SyntheticSizeReader Sizes { get; }
        public StorageObjectId Id(StorageObjectKind kind, string key) => new(system, kind, key);
        public RealOperationIntentRequest Proposal(OperationIntent intent,
            IReadOnlyList<StorageObjectId> targets, IReadOnlyList<RealOperationStep> steps,
            string final) => new(intent, system, targets, steps, final);
        public Task<OperationPlan> Prepare(RealOperationIntentRequest proposal) => Backend.PrepareAsync(
            proposal, new TrustedRealSession(SessionId.New(), "synthetic-product", "synthetic-process",
                1234, Now.AddMinutes(-1), @"C:\Synthetic\WinPool.Agent.exe", true),
            OperationId.New(), CancellationToken.None);

        public async Task<string> VerifiedEvidence(string? NewlyCreatedObjectId)
        {
            var topology = await reader.CaptureAsync(CancellationToken.None);
            var closure = topology.RequireSinglePhysicalClosure([Id(StorageObjectKind.PhysicalDisk, PhysicalId)]);
            return JsonSerializer.Serialize(new WindowsVerifiedStepEvidence(
                closure.Fingerprint, closure.PhysicalMemberFingerprint,
                NewlyCreatedObjectId, "synthetic-provider-return"));
        }

        public void SetRawDisk() => snapshot = BaseSnapshot();
        public void SetGptPartitions()
        {
            var msr = Partition("partition:msr", 1, 1L << 20, 16L << 20,
                "e3c9e316-0b5c-4db8-817d-f92df00215ae");
            var data = Partition("partition:data", 2, 17L << 20, 128L << 20,
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
            snapshot = BaseSnapshot() with
            {
                OsDisks = [BaseSnapshot().OsDisks[0] with { PartitionStyle = "GPT" }],
                Partitions = [msr, data]
            };
        }

        public void SetOldPool(bool withChild = false) => SetPool(PoolId, "Old Pool", withChild);
        public void SetNewPool() => SetPool(NewPoolId, "Pool", false);
        private void SetPool(string id, string name, bool withChild)
        {
            var baseline = BaseSnapshot();
            var physical = baseline.PhysicalDisks[0] with
            {
                CanPool = false, CannotPoolReason = "In a pool", PoolStableId = id
            };
            var pool = new StoragePoolInfo(id, true, name, false, "Healthy", "OK",
                1L << 30, 0, SubsystemId, [PhysicalId]);
            var child = new VirtualDiskInfo("vd:child", true, "Child", "Healthy", "OK",
                "Simple", "Fixed", 1, 65536, 256L << 20, 256L << 20,
                id, [], []);
            snapshot = baseline with
            {
                PhysicalDisks = [physical],
                StoragePools = [baseline.StoragePools[0], pool],
                VirtualDisks = withChild ? [child] : []
            };
        }

        private StorageSystemDocument Document()
        {
            var facts = WinPoolSimulationFacts.Create(snapshot, system);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[]
                     { "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage));
            }
            var objects = facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned(
                    "Path", @"\\.\PHYSICALDRIVE7", FactValueType.String, item.SourceRef)) }
                : item).ToImmutableArray();
            facts = facts with
            {
                IsSimulation = false, InventoryVersion = "synthetic-inventory",
                InventoryCapturedAt = Now, Sources = sources, Objects = objects
            };
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
                "local:synthetic", StorageSystemKind.Local, "Synthetic Storage", facts, [], Now)
            { SystemId = system };
        }

        private static StorageSnapshot BaseSnapshot() => StorageSnapshot.Empty(Environment.MachineName) with
        {
            SnapshotVersion = "synthetic-inventory", ScannedAt = Now,
            Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
                "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
            StorageSubsystems = [new StorageSubsystemInfo(SubsystemId, "Storage Spaces", "Healthy", "OK")],
            PhysicalDisks = [new PhysicalDiskInfo(PhysicalId, true, "WDC Test", "Synthetic",
                "SERIAL-WDC", "SATA", "HDD", 1L << 30, 512, 4096,
                "Healthy", "OK", true, "", 7, false, false, false, false, "pool:primordial")],
            StoragePools = [new StoragePoolInfo("pool:primordial", true, "Primordial", true,
                "Healthy", "OK", 1L << 30, 0, SubsystemId, [PhysicalId])],
            OsDisks = [new OsDiskInfo(DiskId, "WDC Test", 7, "RAW", 1L << 30,
                false, false, false, PhysicalId, null)]
        };

        private static PartitionInfo Partition(string id, int number, long offset, long size,
            string type) => new(id, true, 7, number, "GPT", offset, size,
            false, false, "", "", "", null, size, "Healthy", "OK", "", DiskId,
            PartitionTypeId: type,
            Guid: number == 1 ? "2f8ae502-1e4e-4d94-b190-6284ccb62bea" : "8c4b7c34-04ba-46b1-80d7-9d967441a02d",
            GptType: type);
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
    private sealed class ForbiddenPartitionSizeReader : IPartitionSupportedSizeReader
    {
        public Task<PartitionSupportedSize> ReadAsync(WindowsStorageCommandTarget target,
            CancellationToken token) => throw new InvalidOperationException("No device access in synthetic tests.");
    }
    private sealed class SyntheticSizeReader : IVirtualDiskCreationSizeReader
    {
        public long MaximumBytes { get; set; } = 512L << 20;
        public int ReadCount { get; private set; }
        public Task<VirtualDiskCreationSize> ReadAsync(WindowsStorageCommandTarget target,
            CancellationToken token)
        {
            ReadCount++;
            return Task.FromResult(new VirtualDiskCreationSize(64L << 20, MaximumBytes, 64L << 20, []));
        }
    }
    private sealed class SyntheticSafetyInspector : IWindowsRealStorageSafetyInspector
    {
        public Task ValidateAsync(WindowsRealStorageTopology topology, RealTargetClosure closure,
            RealStorageCommand command, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }
    private sealed class ForbiddenAdapter : IWindowsRealStorageCommandAdapter
    {
        public int CallCount { get; private set; }
        public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command,
            WindowsStorageCommandTarget target, CancellationToken token)
        {
            CallCount++;
            throw new InvalidOperationException("Synthetic tests may not dispatch storage commands.");
        }
    }
}
