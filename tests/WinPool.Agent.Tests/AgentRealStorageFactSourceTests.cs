using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Inventory;

namespace WinPool.Agent.Tests;

public sealed class AgentRealStorageFactSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private const string DiskId = "osdisk:7";
    private const string PhysicalId = "physical:7";

    [Fact]
    public async Task InventoryAndFreshFactsSharePersistedIdentityWithoutMergingCachedFacts()
    {
        await using var harness = await Harness.CreateAsync();
        var cached = Document(SystemId.New(), serial: "CACHED-SERIAL");
        var unused = new UnusedInventoryProvider();
        var coordinator = new AgentInventoryCoordinator(unused, unused,
            new FixedHardwareProvider(cached), new InventoryComparer(),
            new InventorySnapshotRepository(harness.Store, harness.Lease),
            new InventoryComparisonRepository(harness.Store, harness.Lease),
            harness.LocalDocument, harness.Resolver, new UnusedDeviceResolver());
        var inventory = await coordinator.CaptureManageAsync(
            new(CorrelationId.New(), CollectionPurpose.Storage), CancellationToken.None);
        Assert.Equal(ApplicationStatus.Succeeded, inventory.Status);
        var saved = LocalInventoryDocumentCodec.Decode(
            Assert.IsType<ManageInventoryCaptureResponse>(inventory.Value).Document);

        var raw = Document(SystemId.New(), serial: "FRESH-SERIAL");
        var source = new FixedFactSource(() => raw);
        var rebound = await harness.Wrap(source).CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(saved.SystemId, rebound.SystemId);
        Assert.Equal(saved.SystemId, rebound.SourceFacts!.SystemId);
        Assert.Equal("FRESH-SERIAL", Assert.Single(rebound.Snapshot.PhysicalDisks).SerialNumber);
        Assert.Equal(LocalInventoryDocumentCodec.Encode(raw).Json,
            LocalInventoryDocumentCodec.Encode(rebound with
                { SystemId = raw.SystemId, SourceFacts = raw.SourceFacts }).Json);
        Assert.NotEqual(raw.SystemId, saved.SystemId);

        // New helper/resolver instances use the durable authority after restart.
        raw = Document(SystemId.New(), serial: "NEXT-FRESH-SERIAL");
        var restarted = new AgentRealStorageFactSource(source,
            new AgentLocalSystemIdentity(harness.LocalDocument,
                new LocalSystemIdentityResolver(harness.Store, harness.Lease)));
        var next = await restarted.CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(saved.SystemId, next.SystemId);
        Assert.Equal("NEXT-FRESH-SERIAL", Assert.Single(next.Snapshot.PhysicalDisks).SerialNumber);
        Assert.Equal(2, source.CaptureCount);
        Assert.Equal(saved.SystemId, LocalInventoryDocumentCodec.Decode(
            (await harness.LocalDocument.LoadAsync())!.Document).SystemId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferredLocalDocumentIsUsedOnlyForMatchingMachine(bool matchesMachine)
    {
        await using var harness = await Harness.CreateAsync();
        var authority = await harness.Identity.ResolveAsync(CancellationToken.None);
        var preferred = SystemId.New();
        var cached = Document(preferred, matchesMachine
            ? Environment.MachineName.ToLowerInvariant() : "FOREIGN-MACHINE");
        await harness.SaveAsync(cached);

        var actual = await harness.Wrap(new FixedFactSource(() => Document(SystemId.New())))
            .CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(matchesMachine ? preferred : authority.SystemId, actual.SystemId);
    }

    [Fact]
    public async Task PrepareAndPreflightUseCanonicalIdentityAndRejectForeignProposalAndChangedHardware()
    {
        await using var harness = await Harness.CreateAsync();
        var canonical = (await harness.Identity.ResolveAsync(CancellationToken.None)).SystemId;
        var serial = "SERIAL-7";
        var source = new FixedFactSource(() => Document(SystemId.New(), serial: serial));
        var reader = Reader(harness.Wrap(source));
        var safety = new CountingSafetyInspector();
        var planner = new WindowsRealOperationPlanner(reader,
            privilege: new AdministratorPrivilege(), timeProvider: new FixedTime(),
            safetyInspector: safety);
        var adapter = new ForbiddenAdapter();
        var backend = new WindowsRealStorageBackend(adapter, planner, reader, new FixedTime());
        var plan = await backend.PrepareAsync(Proposal(canonical), Session(),
            OperationId.New(), CancellationToken.None);
        Assert.Equal(canonical, plan.SystemId);
        Assert.All(plan.Targets, target => Assert.Equal(canonical, target.System));
        await backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(2, source.CaptureCount);
        Assert.Equal(2, safety.CallCount);

        var foreign = await Assert.ThrowsAsync<InvalidDataException>(() => backend.PrepareAsync(
            Proposal(SystemId.New()), Session(), OperationId.New(), CancellationToken.None));
        Assert.Equal("The proposal does not name the current local system.", foreign.Message);
        serial = "REPLACED-SERIAL";
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.PreflightStepAsync(
            plan, plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task CanonicalIdentityDoesNotBypassSafetyInspector()
    {
        await using var harness = await Harness.CreateAsync();
        var canonical = (await harness.Identity.ResolveAsync(CancellationToken.None)).SystemId;
        var safety = new CountingSafetyInspector { Reject = true };
        var planner = new WindowsRealOperationPlanner(
            Reader(harness.Wrap(new FixedFactSource(() => Document(SystemId.New())))),
            privilege: new AdministratorPrivilege(), timeProvider: new FixedTime(),
            safetyInspector: safety);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planner.PrepareAsync(
            Proposal(canonical), Session(), OperationId.New(), CancellationToken.None));
        Assert.Equal(1, safety.CallCount);
    }

    [Theory]
    [InlineData("foreign-machine")]
    [InlineData("simulation")]
    [InlineData("inconsistent-identity")]
    [InlineData("missing-facts")]
    [InlineData("stale")]
    [InlineData("failed-query")]
    [InlineData("missing-query")]
    public async Task RebindingPreservesFreshLocalFactAdmissionChecks(string defect)
    {
        await using var harness = await Harness.CreateAsync();
        var raw = Document(SystemId.New(), defect == "foreign-machine" ? "FOREIGN-MACHINE" : null);
        raw = defect switch
        {
            "simulation" => raw with { Kind = StorageSystemKind.Simulation,
                SourceFacts = raw.SourceFacts! with { IsSimulation = true } },
            "inconsistent-identity" => raw with { SystemId = SystemId.New() },
            "missing-facts" => raw with { SourceFacts = null },
            "stale" => raw with { SourceFacts = raw.SourceFacts! with { InventoryCapturedAt = Now.AddMinutes(-3) } },
            "failed-query" => raw with { SourceFacts = raw.SourceFacts! with
                { Sources = [.. raw.SourceFacts!.Sources.Select(source => source.ClassName == "MSFT_Volume"
                    ? source with { ReadState = FieldReadState.Failed, ReasonCode = "FixtureFailure" } : source)] } },
            "missing-query" => raw with { SourceFacts = raw.SourceFacts! with
                { Sources = [.. raw.SourceFacts!.Sources.Where(source => source.ClassName != "MSFT_Volume")] } },
            _ => raw
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Reader(harness.Wrap(
            new FixedFactSource(() => raw))).CaptureAsync(CancellationToken.None));
    }

    private static WindowsRealStorageTopologyReader Reader(IWindowsRealStorageFactSource source) =>
        new(source, new FixedMachineIdentity(), new FixedTime());

    private static TrustedRealSession Session() => new(SessionId.New(), "fixture-product-session",
        "fixture-process-instance", 1234, Now.AddMinutes(-1), @"C:\Fixture\WinPool.Agent.exe", true);

    private static RealOperationIntentRequest Proposal(SystemId system)
    {
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, DiskId);
        return new(OperationIntent.SetDiskOnlineState, system, [disk],
            [new RealOperationStep("offline", new SetDiskOnlineCommand(
                RealTargetReference.ForExisting(disk), false), [], "online", "offline", "", "fixture")], "offline");
    }

    private static StorageSystemDocument Document(SystemId system, string? machineName = null, string serial = "SERIAL-7")
    {
        var snapshot = StorageSnapshot.Empty(machineName ?? Environment.MachineName) with
        {
            SnapshotVersion = "fixture-fresh", ScannedAt = Now,
            Computer = new("computer", machineName ?? Environment.MachineName,
                "Fixture Windows", "10.0", "26100", Now.AddHours(-1)),
            StorageSubsystems = [new("subsystem", "Fixture Storage", "Healthy", "OK")],
            PhysicalDisks = [new(PhysicalId, true, "Fixture Disk", "Fixture Model", serial,
                "SATA", "HDD", 1_000_000_000, 512, 4096, "Healthy", "OK", true, "", 7,
                false, false, false, false, "primordial")],
            StoragePools = [new("primordial", true, "Primordial", true, "Healthy", "OK",
                1_000_000_000, 0, "subsystem", [PhysicalId])],
            OsDisks = [new(DiskId, "Fixture OS Disk", 7, "RAW", 1_000_000_000,
                false, false, false, PhysicalId, null)]
        };
        var facts = WinPoolSimulationFacts.Create(snapshot, system);
        var sources = facts.Sources.Select(source => source with
        {
            Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                ? FactOrigin.StorageCim : FactOrigin.Win32
        }).ToImmutableArray();
        foreach (var className in new[] { "MSFT_Partition", "MSFT_Volume" })
            sources = sources.Add(new("empty:" + className, FactOrigin.StorageCim,
                "root/microsoft/windows/storage", className, Now, CollectionPurpose.Storage));
        facts = facts with
        {
            IsSimulation = false, InventoryVersion = "fixture-fresh", InventoryCapturedAt = Now,
            Sources = sources,
            Objects = [.. facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("Path",
                    @"\\.\PHYSICALDRIVE7", FactValueType.String, item.SourceRef)) } : item)]
        };
        return new(StorageSystemDocument.CurrentSchemaVersion, "local:fixture", StorageSystemKind.Local,
            "Fixture Local", facts, [], Now) { SystemId = system };
    }

    private sealed class Harness : IAsyncDisposable
    {
        public WinPoolSqliteStore Store { get; }
        public AgentWriteOwnerLease Lease { get; }
        public LocalInventoryDocumentRepository LocalDocument { get; }
        public LocalSystemIdentityResolver Resolver { get; }
        public AgentLocalSystemIdentity Identity { get; }

        private Harness(WinPoolSqliteStore store, AgentWriteOwnerLease lease)
        {
            Store = store; Lease = lease;
            LocalDocument = new(store, lease); Resolver = new(store, lease);
            Identity = new(LocalDocument, Resolver);
        }

        public static async Task<Harness> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "WinPool.AgentIdentity.Tests",
                Guid.NewGuid().ToString("N"), "winpool.db");
            var store = new WinPoolSqliteStore(path);
            await store.InitializeAsync();
            return new(store, AgentWriteOwnerLease.Acquire(store, "identity-regression"));
        }

        public AgentRealStorageFactSource Wrap(IWindowsRealStorageFactSource source) => new(source, Identity);

        public async Task SaveAsync(StorageSystemDocument document)
        {
            var snapshot = EmbeddedPowerShellInventoryProvider.Project(document.SystemId, document.Snapshot);
            var saved = await new InventorySnapshotRepository(Store, Lease).SaveAsync(snapshot,
                PersistedSystemKind.Local, document.Snapshot.Computer.Name, CancellationToken.None);
            await LocalDocument.SaveAsync(saved.SnapshotId, LocalInventoryDocumentCodec.Encode(document));
        }

        // Preserve isolated fixture databases for diagnosis; never use a user data root.
        public ValueTask DisposeAsync() => Lease.DisposeAsync();
    }

    private sealed class FixedFactSource(Func<StorageSystemDocument> capture) : IWindowsRealStorageFactSource
    {
        public int CaptureCount { get; private set; }
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(capture());
        }
    }

    private sealed class FixedHardwareProvider(StorageSystemDocument document) : IHardwareInventoryProvider
    {
        public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) => Task.FromResult(document);
        public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hardware capture is outside this fixture.");
    }

    private sealed class UnusedInventoryProvider : IInventoryProvider
    {
        public InventoryProviderKind Kind => InventoryProviderKind.Replay;
        public Task<ApplicationResult<InventorySnapshot>> CaptureAsync(InventoryRequest request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No hardware query is allowed.");
    }

    private sealed class UnusedDeviceResolver : IPhysicalDiskDeviceResolver
    {
        public string? ResolvePnpDeviceId(int diskNumber) => throw new InvalidOperationException("No hardware query is allowed.");
    }

    private sealed class FixedMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken cancellationToken) => Task.FromResult("fixture-machine-binding");
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }

    private sealed class CountingSafetyInspector : IWindowsRealStorageSafetyInspector
    {
        public int CallCount { get; private set; }
        public bool Reject { get; init; }
        public Task ValidateAsync(WindowsRealStorageTopology topology, RealTargetClosure closure,
            RealStorageCommand command, CancellationToken cancellationToken)
        {
            CallCount++;
            return Reject ? Task.FromException(new UnauthorizedAccessException("Fixture safety rejection")) : Task.CompletedTask;
        }
    }

    private sealed class ForbiddenAdapter : IWindowsRealStorageCommandAdapter
    {
        public int CallCount { get; private set; }
        public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command,
            WindowsStorageCommandTarget target, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("This regression must never issue a Windows write.");
        }
    }
}
