using System.Collections.Immutable;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent.Tests;

public sealed class AgentRealOperationLayoutSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingObjectResizeRejectedByWindowsPlannerNeverDispatchesDeletionOrRebuild(bool tier)
    {
        await using var fixture = await Fixture.CreateAsync(withTier: tier);
        var target = fixture.Id(tier ? StorageObjectKind.StorageTier : StorageObjectKind.VirtualDisk,
            tier ? Fixture.TierId : Fixture.VirtualDiskId);
        RealStorageCommand command = tier
            ? new ResizeTierCommand(RealTargetReference.ForExisting(target), 32L << 30)
            : new ResizeVirtualDiskCommand(RealTargetReference.ForExisting(target), 32L << 30);
        var proposal = new RealOperationIntentRequest(
            tier ? OperationIntent.ResizeStorageTier : OperationIntent.ResizeVirtualDisk,
            fixture.System, [target], [Step("resize", command)], "Existing object grows to 32 GiB");

        var rejected = await fixture.Service.PrepareAsync(new PrepareAgentRealOperationRequest(
            proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, rejected.Status);
        Assert.Null(rejected.Value);
        var message = Assert.Single(rejected.Messages);
        Assert.Equal("agent.real_operation.prepare_failed", message.Code);
        Assert.Equal("NotSupportedException: Existing-object MAX expansion lacks verified provider bounds.", message.DiagnosticText);
        Assert.True(fixture.Facts.CaptureCount > 0);
        Assert.Contains(fixture.Safety.Commands, observed => observed == command);
        Assert.Empty(await fixture.Plans.ListUnfinishedAsync());
        Assert.Equal(0, fixture.Queries.CallCount);
        AssertNoDispatch(fixture.Adapter);
    }

    [Fact]
    public async Task IndependentLegalRebuildRemainsPreparedWithoutMatchingAcceptAndCancellationHasNoCalls()
    {
        await using var fixture = await Fixture.CreateAsync(withTier: false);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, Fixture.PhysicalId);
        var pool = fixture.Id(StorageObjectKind.StoragePool, Fixture.PoolId);
        var virtualDisk = fixture.Id(StorageObjectKind.VirtualDisk, Fixture.VirtualDiskId);
        var proposal = new RealOperationIntentRequest(OperationIntent.RebuildStoragePool,
            fixture.System, [physical, pool, virtualDisk],
            [
                Step("delete-vdisk", new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(virtualDisk))),
                Step("delete-pool", new DeletePoolCommand(RealTargetReference.ForExisting(pool)), "delete-vdisk"),
                Step("create-pool", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "New Pool"), "delete-pool"),
                Step("create-vdisk", new CreateVirtualDiskCommand(
                    RealTargetReference.FromStep(StorageObjectKind.StoragePool, "create-pool"),
                    "New VD", 32L << 30, 65536, 1), "create-pool")
            ], "Explicitly replace the old pool and VD with a 32 GiB Simple/Fixed VD");

        var prepared = await fixture.Service.PrepareAsync(new PrepareAgentRealOperationRequest(
            proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None);
        Assert.True(prepared.IsSuccess);
        var response = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        Assert.Equal(RealOperationState.Prepared, response.State);
        Assert.True(RealOperationValidator.IsValid(response.Plan));
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, response.Plan.Risk);
        Assert.Collection(response.Plan.RealOperation!.Steps,
            step => Assert.IsType<DeleteVirtualDiskCommand>(step.Command),
            step => Assert.IsType<DeletePoolCommand>(step.Command),
            step => Assert.IsType<CreatePoolCommand>(step.Command),
            step => Assert.IsType<CreateVirtualDiskCommand>(step.Command));
        AssertNoDispatch(fixture.Adapter);

        // An unrelated or stale confirmation cannot authorize this separately frozen R5 plan.
        var wrongHash = response.Plan.PlanHash[0] == '0' ? '1' : '0';
        var rejectedAccept = await fixture.Service.AcceptAsync(new AcceptAgentRealOperationRequest(
            response.Plan.OperationId, wrongHash + response.Plan.PlanHash[1..],
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, CancellationToken.None);
        Assert.Equal(ApplicationStatus.Rejected, rejectedAccept.Status);
        Assert.Equal("agent.real_operation.plan_mismatch", Assert.Single(rejectedAccept.Messages).Code);

        var queried = await fixture.Service.QueryAsync(new QueryAgentRealOperationRequest(
            response.Plan.OperationId, CorrelationId.New()), fixture.Session, CancellationToken.None);
        Assert.Equal(RealOperationState.Prepared, Assert.IsType<AgentRealOperationResponse>(queried.Value).State);
        Assert.Equal(PersistedOperationState.Prepared,
            (await fixture.Plans.GetAsync(response.Plan.OperationId))!.State);
        Assert.All(await fixture.Plans.GetStepsAsync(response.Plan.OperationId),
            step => Assert.Equal(PersistedOperationStepState.NotStarted, step.State));
        Assert.DoesNotContain(await fixture.Events.ListAsync(response.Plan.OperationId),
            item => item.Event.Code == "operation.step.call_issued");
        AssertNoDispatch(fixture.Adapter);

        var cancelled = await fixture.Service.StopFollowingStepsAsync(
            new StopAgentRealOperationFollowingStepsRequest(response.Plan.OperationId,
                response.Plan.PlanHash, fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None);
        Assert.Equal(RealOperationState.Cancelled,
            Assert.IsType<AgentRealOperationResponse>(cancelled.Value).State);
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(0, fixture.Queries.CallCount);
        AssertNoDispatch(fixture.Adapter);
    }

    private static RealOperationStep Step(string id, RealStorageCommand command, params string[] dependencies) =>
        new(id, command, dependencies, "Exact current object and member are verified",
            "Explicit requested state", "Explicit approved structure and data loss",
            "Agent must derive current Windows support evidence");

    private static void AssertNoDispatch(RecordingAdapter adapter)
    {
        Assert.Empty(adapter.Commands);
        Assert.Equal(0, adapter.DeleteCalls);
        Assert.Equal(0, adapter.ReplacementCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string PhysicalId = "physical:t13";
        public const string PoolId = "pool:t13";
        public const string VirtualDiskId = "vd:t13";
        public const string TierId = "tier:t13";
        private static readonly DateTimeOffset Now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        private readonly AgentWriteOwnerLease lease;
        public SystemId System { get; } = SystemId.New();
        public TrustedRealSession Session { get; } = new(SessionId.New(), "t13-product-session",
            "t13-process-instance", 1234, Now.AddMinutes(-1), @"C:\Fixture\WinPool.App.exe", true);
        public RecordingAdapter Adapter { get; } = new();
        public RecordingSafetyInspector Safety { get; } = new();
        public ForbiddenProviderQueries Queries { get; } = new();
        public FixedFactSource Facts { get; }
        public OperationPlanRepository Plans { get; }
        public ExecutionEventRepository Events { get; }
        public AgentRealOperationService Service { get; }

        private Fixture(WinPoolSqliteStore store, AgentWriteOwnerLease lease, bool withTier)
        {
            this.lease = lease;
            Plans = new(store, lease);
            Events = new(store, lease);
            Facts = new(Document(withTier));
            var machine = new FixedMachineIdentity();
            var clock = new FixedTime();
            var reader = new WindowsRealStorageTopologyReader(Facts, machine, clock);
            var planner = new WindowsRealOperationPlanner(reader, partitionSizes: Queries,
                privilege: new AdministratorPrivilege(), timeProvider: clock, safetyInspector: Safety,
                virtualDiskSizes: Queries, logicalDriveRoots: () => [], capabilities: Queries);
            var backend = new WindowsRealStorageBackend(Adapter, planner, reader, clock);
            Service = new(Plans, Events, backend, machine, authority: null, timeProvider: clock,
                isSessionStillArmed: session => session == Session, isAdministrator: () => true);
        }

        public static async Task<Fixture> CreateAsync(bool withTier)
        {
            var path = Path.Combine(Path.GetTempPath(), "WinPool.Agent.RealOperation.Tests",
                Guid.NewGuid().ToString("N"), "winpool.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var store = new WinPoolSqliteStore(path);
            await store.InitializeAsync();
            var fixture = new Fixture(store, AgentWriteOwnerLease.Acquire(store, "t13-layout-regression"), withTier);
            await fixture.Service.InitializeRecoveryAsync();
            return fixture;
        }

        public StorageObjectId Id(StorageObjectKind kind, string key) => new(System, kind, key);

        private StorageSystemDocument Document(bool withTier)
        {
            var snapshot = StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "t13-current", ScannedAt = Now,
                Computer = new("computer:t13", Environment.MachineName, "Fixture Windows", "10.0", "26100", Now.AddHours(-1)),
                StorageSubsystems = [new("subsystem:t13", "Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = [new(PhysicalId, true, "Fixture WDC", "Fixture", "SERIAL-T13",
                    "SATA", "HDD", 64L << 30, 512, 4096, "Healthy", "OK", false, "In a pool", 7,
                    false, false, false, false, PoolId)],
                StoragePools = [new(PoolId, true, "Old Pool", false, "Healthy", "OK", 64L << 30,
                    16L << 30, "subsystem:t13", [PhysicalId])],
                VirtualDisks = [new(VirtualDiskId, true, "Old VD", "Healthy", "OK", "Simple", "Fixed",
                    1, 65536, 16L << 30, 16L << 30, PoolId, withTier ? [TierId] : [], [])],
                OsDisks = [new("osdisk:t13", "Old VD", 9, "RAW", 16L << 30,
                    false, false, false, null, VirtualDiskId)],
                StorageTiers = withTier
                    ? [new(TierId, true, "Existing HDD Instance", "HDD", "Simple", 16L << 30, 16L << 30, PoolId, VirtualDiskId,
                        [PhysicalId], NumberOfColumns: 1, Interleave: 65536)] : []
            };
            var facts = WinPoolSimulationFacts.Create(snapshot, System);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : source.Namespace == "winpool/native" ? FactOrigin.Native : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[] { "MSFT_StorageSubSystem", "MSFT_PhysicalDisk", "MSFT_StoragePool",
                         "MSFT_Disk", "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("t13-empty:" + className, FactOrigin.StorageCim,
                    "root/microsoft/windows/storage", className, Now, CollectionPurpose.Storage));
            }
            const string roleSource = "t13-osdisk-roles-source";
            const string roleObject = "t13-osdisk-roles";
            sources = sources.Add(new WinPoolSource(roleSource, FactOrigin.Native, "winpool/native",
                "Windows.DiskRoles", Now, CollectionPurpose.Storage));
            var osDiskRoles = new WinPoolSourceObject(roleObject, FactObjectType.HardwareSupplement,
                roleSource, roleObject, true,
                [WinPoolSourceField.Returned("DiskNumber", 9L, FactValueType.Int64, roleSource),
                    WinPoolSourceField.Returned("IsBoot", false, FactValueType.Boolean, roleSource),
                    WinPoolSourceField.Returned("IsSystem", false, FactValueType.Boolean, roleSource),
                    WinPoolSourceField.Returned("IsPageFile", false, FactValueType.Boolean, roleSource),
                    WinPoolSourceField.Returned("IsCrashDump", false, FactValueType.Boolean, roleSource)]);
            var relationships = facts.Relationships.Add(new WinPoolFactRelationship(
                "osdisk:t13", roleObject, "disk-supplement", Now));
            facts = facts with
            {
                IsSimulation = false, InventoryVersion = "t13-current", InventoryCapturedAt = Now,
                Sources = sources,
                Objects = [.. facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                    ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("Path",
                        @"\\.\PHYSICALDRIVE9", FactValueType.String, item.SourceRef)) } : item), osDiskRoles],
                Relationships = relationships
            };
            return new(StorageSystemDocument.CurrentSchemaVersion, "local:t13", StorageSystemKind.Local,
                "T13 Fixture", facts, [], Now) { SystemId = System };
        }

        // Keep the isolated SQLite evidence; never touch the product data root.
        public ValueTask DisposeAsync() => lease.DisposeAsync();
        private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }

    private sealed class FixedFactSource(StorageSystemDocument document) : IWindowsRealStorageFactSource
    {
        public int CaptureCount { get; private set; }
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(document);
        }
    }

    private sealed class FixedMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken cancellationToken) => Task.FromResult("t13-machine-binding");
    }

    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }

    private sealed class RecordingSafetyInspector : IWindowsRealStorageSafetyInspector
    {
        public List<RealStorageCommand> Commands { get; } = [];
        public Task ValidateAsync(WindowsRealStorageTopology topology, RealTargetClosure closure,
            RealStorageCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAdapter : IWindowsRealStorageCommandAdapter
    {
        public List<RealStorageCommand> Commands { get; } = [];
        public int DeleteCalls => Commands.Count(command => command is DeletePoolCommand or DeleteVirtualDiskCommand
            or DeleteTierCommand or DeletePartitionCommand or ClearDiskCommand);
        public int ReplacementCalls => Commands.Count(command => command is CreatePoolCommand or CreateVirtualDiskCommand
            or CreateTieredVirtualDiskCommand or CreateTierCommand or InitializeGptCommand);
        public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command,
            WindowsStorageCommandTarget target, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            throw new InvalidOperationException("T13 must never dispatch any Windows storage mutation.");
        }
    }

    private sealed class ForbiddenProviderQueries : IPartitionSupportedSizeReader,
        IVirtualDiskCreationSizeReader, IWindowsRealStorageCapabilityReader
    {
        public int CallCount { get; private set; }
        private Task<T> Reject<T>()
        {
            CallCount++;
            throw new InvalidOperationException("This admission-only fixture must never query a Windows device.");
        }
        Task<PartitionSupportedSize> IPartitionSupportedSizeReader.ReadAsync(
            WindowsStorageCommandTarget target, CancellationToken cancellationToken) => Reject<PartitionSupportedSize>();
        Task<VirtualDiskCreationSize> IVirtualDiskCreationSizeReader.ReadAsync(
            WindowsStorageCommandTarget target, CancellationToken cancellationToken) => Reject<VirtualDiskCreationSize>();
        public Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(WindowsRealStorageTopology topology,
            StorageObjectId volume, CancellationToken cancellationToken) => Reject<WindowsVolumeFormatCapability>();
        public Task<WindowsTierCapability> ReadTierAsync(WindowsRealStorageTopology topology,
            StorageObjectId physicalDisk, CancellationToken cancellationToken) => Reject<WindowsTierCapability>();
        public Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(WindowsRealStorageTopology topology,
            StorageObjectId tier, CancellationToken cancellationToken) => Reject<VirtualDiskCreationSize>();
    }
}
