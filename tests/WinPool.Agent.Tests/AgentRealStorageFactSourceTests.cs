using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent.Tests;

public sealed class AgentRealStorageFactSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private const string DiskId = "osdisk:7";
    private const string PhysicalId = "physical:7";

    [Fact]
    public async Task ProductionStartupAndMonitorConsumeOneCompleteHardwareObservation()
    {
        await using var harness = await Harness.CreateAsync();
        var source = new FixedFactSource(() => Document(SystemId.New()));
        var collector = harness.Wrap(source);
        var primed = await collector.CollectStartupAsync(CancellationToken.None);
        var monitor = await collector.CollectForMonitorAsync(null, CancellationToken.None);
        Assert.Same(primed, monitor);
        var published = new List<AgentEvent>();
        var coordinator = new AgentInventoryCoordinator(collector, harness.LocalDocument, harness.Identity,
            new UnusedDeviceResolver(), published.Add);
        await coordinator.CaptureStartupAsync(CancellationToken.None);
        await coordinator.CaptureStartupAsync(CancellationToken.None);
        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware },
            published.OfType<AgentInventoryUpdatedEvent>().Select(value => value.Purpose));
        Assert.NotEqual(Guid.Empty, primed.SourceFacts!.ObservationId);
        await collector.CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(2, source.CaptureCount);
    }

    [Fact]
    public async Task SameGenerationCoalescesCompleteRawCaptureAndVerifiedMonitorReusesIt()
    {
        await using var harness = await Harness.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StorageSystemDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AsyncHardwareProvider(async () =>
        {
            entered.TrySetResult();
            return await release.Task;
        });
        var collector = harness.Wrap(provider);
        var generation = collector.BeginGeneration();
        var first = collector.CollectAsync(CollectionPurpose.Storage, null, generation, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = collector.CollectAsync(CollectionPurpose.Storage, null, generation, CancellationToken.None);
        Assert.Equal(1, provider.CaptureCount);
        release.TrySetResult(Document(SystemId.New()));
        var documents = await Task.WhenAll(first, second);
        Assert.Same(documents[0], documents[1]);
        var raw = documents[0];
        var target = new StorageObjectId(raw.SystemId, StorageObjectKind.PhysicalDisk, PhysicalId);
        var scope = StorageInventoryScopeFactory.Create(raw.SourceFacts!, OperationId.New(), "monitor", [target], 17);
        collector.AcceptVerifiedPostState(raw, null);
        var monitor = await collector.CollectForMonitorAsync(scope, CancellationToken.None);
        Assert.Equal(1, provider.CaptureCount);
        Assert.False(monitor.SourceFacts!.IsMerged);
        Assert.True(monitor.SourceFacts.ScopedCollection!.Complete);
        Assert.Equal(scope, monitor.SourceFacts.ScopedCollection.Scope);
        Assert.Single(monitor.Snapshot.StorageSubsystems);
        Assert.Equal("primordial", Assert.Single(monitor.Snapshot.PhysicalDisks).PoolStableId);
        Assert.Single(monitor.SourceFacts.Relationships, relation => relation.Kind == "subsystem-pool");
        monitor.SourceFacts.Validate();
    }

    [Fact]
    public async Task RealFreshReadAdvancesGenerationAndNeverReusesVerifiedPostState()
    {
        await using var harness = await Harness.CreateAsync();
        var source = new FixedFactSource(() => Document(SystemId.New()));
        var collector = harness.Wrap(source);
        var before = await collector.CaptureFreshAsync(CancellationToken.None);
        collector.AcceptVerifiedPostState(before, null);
        Assert.Same(before, await collector.CollectForMonitorAsync(null, CancellationToken.None));
        var after = await collector.CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(2, source.CaptureCount);
        Assert.True(after.SourceFacts!.Sources.Min(x => x.CaptureGeneration)
            > before.SourceFacts!.Sources.Max(x => x.CaptureGeneration));
    }

    [Fact]
    public async Task InventoryAndFreshFactsSharePersistedIdentityWithoutMergingCachedFacts()
    {
        await using var harness = await Harness.CreateAsync();
        var cached = Document(SystemId.New(), serial: "CACHED-SERIAL");
        var coordinator = new AgentInventoryCoordinator(new FixedHardwareProvider(cached),
            harness.LocalDocument, harness.Identity, new UnusedDeviceResolver());
        var inventory = await coordinator.CaptureManageAsync(
            new(CorrelationId.New(), CollectionPurpose.Storage), CancellationToken.None);
        Assert.Equal(ApplicationStatus.Succeeded, inventory.Status);
        var saved = LocalInventoryDocumentCodec.Decode(
            (await harness.LocalDocument.LoadAsync())!.Document);

        var raw = Document(SystemId.New(), serial: "FRESH-SERIAL");
        var source = new FixedFactSource(() => raw);
        var rebound = await harness.Wrap(source).CaptureFreshAsync(CancellationToken.None);
        Assert.Equal(saved.SystemId, rebound.SystemId);
        Assert.Equal(saved.SystemId, rebound.SourceFacts!.SystemId);
        Assert.Equal("FRESH-SERIAL", Assert.Single(rebound.Snapshot.PhysicalDisks).SerialNumber);
        Assert.Equal(LocalInventoryDocumentCodec.Encode(raw).Json,
            LocalInventoryDocumentCodec.Encode(rebound with
                { SystemId = raw.SystemId, SourceFacts = rebound.SourceFacts! with
                    { SystemId = raw.SystemId, Sources = raw.SourceFacts!.Sources, ObservationId = raw.SourceFacts.ObservationId } }).Json);
        Assert.NotEqual(raw.SystemId, saved.SystemId);

        // New helper/resolver instances use the durable authority after restart.
        raw = Document(SystemId.New(), serial: "NEXT-FRESH-SERIAL");
        var restarted = new AgentInventoryCollector(source,
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
        foreach (var className in new[] { "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            if (!sources.Any(x => x.ClassName == className))
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

        public AgentInventoryCollector Wrap(IHardwareInventoryProvider source) => new(source, Identity);

        public async Task SaveAsync(StorageSystemDocument document)
        {
            await LocalDocument.SaveAsync(Guid.NewGuid(), LocalInventoryDocumentCodec.Encode(document),
                document.SystemId, document.Snapshot.Computer.Name,
                LocalSystemIdentityResolver.CreateAuthorityBinding(document.Snapshot.Computer.Name));
            Identity.Invalidate();
        }

        // Preserve isolated fixture databases for diagnosis; never use a user data root.
        public ValueTask DisposeAsync() => Lease.DisposeAsync();
    }

    private sealed class FixedFactSource(Func<StorageSystemDocument> capture) : IHardwareInventoryProvider
    {
        public int CaptureCount { get; private set; }
        public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(capture());
        }
    }

    private sealed class AsyncHardwareProvider(Func<Task<StorageSystemDocument>> capture) : IHardwareInventoryProvider
    {
        public int CaptureCount { get; private set; }
        public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return capture();
        }
    }

    private sealed class FixedHardwareProvider(StorageSystemDocument document) : IHardwareInventoryProvider
    {
        public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) => Task.FromResult(document);
        public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hardware capture is outside this fixture.");
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
