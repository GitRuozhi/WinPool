using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Inventory;

namespace WinPool.Agent.Tests;

public sealed class AgentInventoryCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly SystemId FixtureSystem = new(new Guid("e3abbd80-9305-4f9d-861d-c951a4edfd15"));
    private static readonly string[] OtherObjectIds =
        ["computer", "cpu", "gpu", "monitor", "gpu-supplement", "monitor-supplement"];

    [Fact]
    public async Task StartupCapturesStorageThenHardwareOnceAndPublishesPersistedReports()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.CaptureAsync(NetworkCapture(-1, ["history"], includeOtherObjects: true));
        harness.Events.Clear();
        harness.provider.Sequence.Enqueue((CollectionPurpose.Storage, NetworkCapture(0, [], includeOtherObjects: true)));
        harness.provider.Sequence.Enqueue((CollectionPurpose.Hardware, NetworkCapture(1, ["ethernet"])));
        await harness.coordinator.CaptureStartupAsync(CancellationToken.None);
        await harness.coordinator.CaptureStartupAsync(CancellationToken.None);
        Assert.Empty(harness.provider.Sequence);
        Assert.Collection(harness.Events,
            e => Assert.True(Assert.IsType<AgentInventoryStartedEvent>(e).IsAutomatic),
            e => Assert.True(Assert.IsType<AgentInventoryUpdatedEvent>(e).IsAutomatic),
            e => Assert.True(Assert.IsType<AgentInventoryStartedEvent>(e).IsAutomatic),
            e => Assert.True(Assert.IsType<AgentInventoryUpdatedEvent>(e).IsAutomatic));
        var reports = harness.Events.OfType<AgentInventoryUpdatedEvent>().ToArray();
        Assert.Equal(new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware }, reports.Select(x => x.Purpose));
        Assert.Equal(reports[1].Document, (await new ReadOnlyLocalInventoryReader(harness.databasePath).LoadAsync()));
        AssertMembership(LocalInventoryDocumentCodec.Decode(reports[1].Document), ["ethernet"]);

        // Manual commands still capture and publish after the one-time startup sequence.
        await harness.CaptureAsync(NetworkCapture(2, ["ethernet", "wifi"]), CollectionPurpose.Storage);
        await harness.CaptureAsync(NetworkCapture(3, ["wifi"]), CollectionPurpose.Hardware);
        Assert.Equal(4, harness.Events.OfType<AgentInventoryUpdatedEvent>().Count());
        Assert.All(harness.Events.Skip(4).OfType<AgentInventoryStartedEvent>(), e => Assert.False(e.IsAutomatic));
        Assert.All(harness.Events.Skip(4).OfType<AgentInventoryUpdatedEvent>(), e => Assert.False(e.IsAutomatic));
    }

    [Fact]
    public async Task StartupStorageFailureStillAttemptsHardwareAndCancellationStopsTheSequence()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        harness.provider.Sequence.Enqueue((CollectionPurpose.Storage, null));
        harness.provider.Sequence.Enqueue((CollectionPurpose.Hardware, NetworkCapture(1, ["ethernet"], includeOtherObjects: true)));
        await harness.coordinator.CaptureStartupAsync(CancellationToken.None);
        Assert.True(Assert.IsType<AgentInventoryStartedEvent>(harness.Events[0]).IsAutomatic);
        Assert.True(Assert.IsType<AgentInventoryFailedEvent>(harness.Events[1]).IsAutomatic);
        Assert.Equal(CollectionPurpose.Hardware, Assert.IsType<AgentInventoryStartedEvent>(harness.Events[2]).Purpose);
        Assert.Equal(CollectionPurpose.Hardware, Assert.IsType<AgentInventoryUpdatedEvent>(harness.Events[3]).Purpose);

        await using var cancelled = await InventoryHarness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.coordinator.CaptureStartupAsync(cancellation.Token));
        Assert.Empty(cancelled.Events);
    }

    [Fact]
    public async Task FailedFullCaptureKeepsTheSuccessfulStorageReportInHistory()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        harness.provider.Sequence.Enqueue((CollectionPurpose.Storage, NetworkCapture(0, ["storage"], includeOtherObjects: true)));
        harness.provider.Sequence.Enqueue((CollectionPurpose.Hardware, null));
        await harness.coordinator.CaptureStartupAsync(CancellationToken.None);
        var saved = Assert.IsType<AgentInventoryUpdatedEvent>(harness.Events[1]);
        Assert.Equal(CollectionPurpose.Hardware, Assert.IsType<AgentInventoryFailedEvent>(harness.Events[3]).Purpose);
        Assert.Equal(saved.Document, await new ReadOnlyLocalInventoryReader(harness.databasePath).LoadAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task SuccessfulNetworkRefreshReplacesCachedMembershipThroughFailureAndRestart(int count)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var oldIds = Enumerable.Range(1, 46).Select(x => $"nic-{x}").ToArray();
        // Seven is one fixture size, not a device limit. Keep some identities and add others.
        string[] candidateIds = ["nic-2", "nic-5", "new-c", "new-d", "new-e", "new-f",
            "new-g", "new-h", "new-i", "new-j", "new-k", "new-l"];
        var expectedIds = candidateIds.Take(count).ToArray();
        var original = await harness.CaptureAsync(NetworkCapture(0, oldIds, includeOtherObjects: true));
        AssertMembership(original, oldIds);
        var canonicalSystemId = original.SystemId;

        var replacement = await harness.CaptureAsync(NetworkCapture(1, expectedIds));
        AssertMembership(replacement, expectedIds);
        Assert.Equal(canonicalSystemId, replacement.SystemId);

        var repeated = await harness.CaptureAsync(NetworkCapture(2, expectedIds.Reverse().ToArray()));
        AssertMembership(repeated, expectedIds);
        Assert.Equal(Start.AddSeconds(2), Assert.Single(NetworkSources(repeated)).CapturedAt);

        // Two failures retain the last successful objects, but only the latest failed status.
        StorageSystemDocument failed = repeated;
        for (var second = 3; second <= 4; second++)
        {
            failed = await harness.CaptureAsync(NetworkCapture(second, [], FieldReadState.Failed));
            AssertMembership(failed, expectedIds);
            var latest = NetworkSources(failed).MaxBy(x => x.CapturedAt)!;
            Assert.Equal(FieldReadState.Failed, latest.ReadState);
            Assert.Equal("FixtureAccessDenied", latest.ReasonCode);
            Assert.Equal(Start.AddSeconds(second), latest.CapturedAt);
            Assert.Equal(count == 0 ? 1 : 2, NetworkSources(failed).Length);
            var sourceById = failed.SourceFacts!.Sources.ToDictionary(x => x.Id);
            Assert.All(failed.SourceFacts.Objects.Where(x => x.ObjectType == FactObjectType.NetworkAdapter),
                item => Assert.Equal(Start.AddSeconds(2), sourceById[item.SourceRef].CapturedAt));
        }

        var afterRestart = await harness.ReopenAsync();
        AssertMembership(afterRestart, expectedIds);
        Assert.Equal(canonicalSystemId, afterRestart.SystemId);
        Assert.Equal(WinPoolFactsCodec.Encode(failed.SourceFacts!), WinPoolFactsCodec.Encode(afterRestart.SourceFacts!));

        // A late success cannot resurrect the original larger set after the failed refresh.
        var late = await harness.CaptureAsync(NetworkCapture(1, oldIds));
        AssertMembership(late, expectedIds);
        Assert.Equal(WinPoolFactsCodec.Encode(failed.SourceFacts!), WinPoolFactsCodec.Encode(late.SourceFacts!));

        var empty = await harness.CaptureAsync(NetworkCapture(5, []));
        AssertMembership(empty, []);
        var emptySource = Assert.Single(NetworkSources(empty));
        Assert.Equal(FieldReadState.Returned, emptySource.ReadState);
        Assert.Equal(Start.AddSeconds(5), emptySource.CapturedAt);
        var final = await harness.ReopenAsync();
        AssertMembership(final, []);
        Assert.Equal(canonicalSystemId, final.SystemId);
        Assert.Equal(WinPoolFactsCodec.Encode(empty.SourceFacts!), WinPoolFactsCodec.Encode(final.SourceFacts!));
    }

    [Fact]
    public async Task StorageRefreshAndChangedSupplementSourcesPreserveHardwareDeviceMembership()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        string[] expectedNetworkIds = ["ethernet", "vpn"];
        var original = await harness.CaptureAsync(NetworkCapture(0, expectedNetworkIds, includeOtherObjects: true));
        AssertMembership(original, expectedNetworkIds);

        var computerSource = Source("computer:1", "Win32_ComputerSystem", 1, CollectionPurpose.Storage);
        var storage = Capture(1, CollectionPurpose.Storage, [computerSource],
            [Object("computer", FactObjectType.Computer, computerSource, "Refreshed computer")]);
        var storageResult = await harness.CaptureAsync(storage, CollectionPurpose.Storage);
        AssertMembership(storageResult, expectedNetworkIds);
        Assert.Equal("Refreshed computer", storageResult.Snapshot.Computer.Name);
        Assert.Equal(Start, Assert.Single(NetworkSources(storageResult)).CapturedAt);

        var gpuSource = Source("gpu-supplement:2", "Win32_VideoController", 2);
        var monitorSource = Source("monitor-supplement:2", "WmiMonitorID", 2);
        var supplemental = Capture(2, CollectionPurpose.Hardware, [monitorSource, gpuSource],
            [Object("monitor-supplement", FactObjectType.HardwareSupplement, monitorSource, "Different monitor description"),
             Object("extra-gpu-observation", FactObjectType.HardwareSupplement, gpuSource, "GPU"),
             Object("gpu-supplement", FactObjectType.HardwareSupplement, gpuSource, "Different GPU description")]);
        var result = await harness.CaptureAsync(supplemental);
        AssertMembership(result, expectedNetworkIds, ["extra-gpu-observation"]);
        Assert.Equal(original.SystemId, result.SystemId);
        Assert.Equal("Refreshed computer", result.Snapshot.Computer.Name);
        Assert.Equal(Start, Assert.Single(NetworkSources(result)).CapturedAt);

        var reloaded = await harness.ReopenAsync();
        AssertMembership(reloaded, expectedNetworkIds, ["extra-gpu-observation"]);
        Assert.Equal(WinPoolFactsCodec.Encode(result.SourceFacts!), WinPoolFactsCodec.Encode(reloaded.SourceFacts!));
    }

    private static void AssertMembership(StorageSystemDocument document, string[] networks, string[]? extraObjects = null)
    {
        var facts = Assert.IsType<WinPoolFacts>(document.SourceFacts);
        var expectedAll = OtherObjectIds.Concat(networks).Concat(extraObjects ?? []).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedAll, facts.Objects.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray());
        var unified = Assert.IsType<WinPoolSystem>(document.Unified);
        Assert.Equal(expectedAll, unified.Objects.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(networks.Order(StringComparer.Ordinal).ToArray(), facts.Objects
            .Where(x => x.ObjectType == FactObjectType.NetworkAdapter).Select(x => x.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(networks.Order(StringComparer.Ordinal).ToArray(), unified.Objects
            .Where(x => x.ObjectType == FactObjectType.NetworkAdapter).Select(x => x.Id).Order(StringComparer.Ordinal).ToArray());

        var report = HardwareReportProjector.Project(document, chinese: true);
        AssertReportObjects("Network", networks);
        AssertReportObjects("GPU", ["gpu"]);
        AssertReportObjects("Monitor", ["monitor"]);
        AssertReportObjects("CPU", ["cpu"]);

        void AssertReportObjects(string category, string[] expectedIds)
        {
            var rows = report.Single(x => x.Name == category).Sections.SelectMany(x => x.Rows).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                Assert.Equal(Math.Max(1, expectedIds.Length), row.Cells.Count);
                if (expectedIds.Length == 0)
                    Assert.Equal(string.Empty, Assert.Single(row.Cells).ObjectId);
                else if (category == "Network")
                    Assert.Equal(expectedIds.Order(StringComparer.Ordinal).ToArray(),
                        row.Cells.Select(x => x.ObjectId).Order(StringComparer.Ordinal).ToArray());
                else
                    Assert.All(row.Cells.Where(x => x.ObjectId.Length > 0),
                        cell => Assert.Contains(facts.Objects, item => item.Id == cell.ObjectId));
            }

            if (expectedIds.Length == 0) return;
            // Fields such as GPU driver/location and CPU cache refer to their supplement,
            // or have no source ID when unavailable. Devices are columns, not field owners.
            var identityRow = Assert.Single(rows, x => x.Label == (category == "Network" ? "名称" : "型号"));
            Assert.Equal(expectedIds.Order(StringComparer.Ordinal).ToArray(),
                identityRow.Cells.Select(x => x.ObjectId).Order(StringComparer.Ordinal).ToArray());
            var displayedColumnIds = Enumerable.Range(0, expectedIds.Length)
                .Select(column => rows.Select(row => row.Cells[column].ObjectId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty).ToArray();
            Assert.Equal(expectedIds.Order(StringComparer.Ordinal).ToArray(),
                displayedColumnIds.Order(StringComparer.Ordinal).ToArray());
        }
    }

    private static WinPoolSource[] NetworkSources(StorageSystemDocument document) => document.SourceFacts!.Sources
        .Where(x => x.Namespace == "winpool/native" && x.ClassName == "WinPool.NetworkAdapter").ToArray();

    private static StorageSystemDocument NetworkCapture(int second, string[] ids,
        FieldReadState state = FieldReadState.Returned, bool includeOtherObjects = false)
    {
        var source = Source($"network:{second}", "WinPool.NetworkAdapter", second) with
        {
            ReadState = state,
            ReasonCode = state == FieldReadState.Failed ? "FixtureAccessDenied" : null
        };
        var sources = new List<WinPoolSource> { source };
        var objects = ids.Select(id => Object(id, FactObjectType.NetworkAdapter, source, id)).ToList();
        if (includeOtherObjects)
        {
            Add("computer", FactObjectType.Computer, "Win32_ComputerSystem", Environment.MachineName);
            Add("cpu", FactObjectType.Processor, "Win32_Processor", "CPU");
            Add("gpu", FactObjectType.VideoController, "WinPool.GraphicsAdapter", "GPU");
            Add("monitor", FactObjectType.Monitor, "WinPool.GraphicsOutput", "Monitor");
            Add("gpu-supplement", FactObjectType.HardwareSupplement, "Win32_VideoController", "GPU");
            Add("monitor-supplement", FactObjectType.HardwareSupplement, "WmiMonitorID", "Monitor");
        }
        return Capture(second, CollectionPurpose.Hardware, [.. sources], [.. objects], state);

        void Add(string id, FactObjectType type, string className, string name)
        {
            var otherSource = Source($"{id}:{second}", className, second);
            sources.Add(otherSource);
            objects.Add(Object(id, type, otherSource, name));
        }
    }

    private static WinPoolSource Source(string id, string className, int second,
        CollectionPurpose purpose = CollectionPurpose.Hardware) =>
        new(id, FactOrigin.Native, "winpool/native", className, Start.AddSeconds(second), purpose);

    private static WinPoolSourceObject Object(string id, FactObjectType type, WinPoolSource source, string name) =>
        new(id, type, source.Id, $"identity:{id}", true,
            [WinPoolSourceField.Returned("Name", name, FactValueType.String, source.Id)]);

    private static StorageSystemDocument Capture(int second, CollectionPurpose purpose,
        ImmutableArray<WinPoolSource> sources, ImmutableArray<WinPoolSourceObject> objects,
        FieldReadState state = FieldReadState.Returned)
    {
        var time = Start.AddSeconds(second);
        var facts = new WinPoolFacts(WinPoolFacts.CurrentFormatVersion, FixtureSystem, 0, sources, objects, [],
            [.. objects.Select(x => new WinPoolIdentityBinding(x.ObjectType, x.SourceIdentity, x.Id))],
            [new(purpose, time, time, state)]) { InventoryVersion = $"fixture:{second}", InventoryCapturedAt = time };
        return new(StorageSystemDocument.CurrentSchemaVersion, "local:inventory-regression", StorageSystemKind.Local,
            "Inventory regression", facts, [], time) { SystemId = FixtureSystem };
    }

    private sealed class InventoryHarness : IAsyncDisposable
    {
        public readonly string databasePath;
        public readonly FixedHardwareProvider provider = new();
        private WinPoolSqliteStore store = null!;
        private AgentWriteOwnerLease lease = null!;
        public AgentInventoryCoordinator coordinator = null!;
        public List<AgentEvent> Events { get; } = [];

        private InventoryHarness(string databasePath) => this.databasePath = databasePath;

        public static async Task<InventoryHarness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WinPool.AgentInventory.Tests", Guid.NewGuid().ToString("N"));
            var harness = new InventoryHarness(Path.Combine(directory, "winpool.db"));
            await harness.InitializeAsync();
            return harness;
        }

        private async Task InitializeAsync()
        {
            store = new WinPoolSqliteStore(databasePath);
            await store.InitializeAsync();
            lease = AgentWriteOwnerLease.Acquire(store, "inventory-regression");
            var unused = new UnusedInventoryProvider();
            coordinator = new(unused, unused, provider, new InventoryComparer(),
                new InventorySnapshotRepository(store, lease), new InventoryComparisonRepository(store, lease),
                new LocalInventoryDocumentRepository(store, lease), new LocalSystemIdentityResolver(store, lease),
                new UnusedDeviceResolver(), Events.Add);
        }

        public async Task<StorageSystemDocument> CaptureAsync(StorageSystemDocument input,
            CollectionPurpose purpose = CollectionPurpose.Hardware)
        {
            provider.Next = input;
            provider.ExpectedPurpose = purpose;
            var result = await coordinator.CaptureManageAsync(new(CorrelationId.New(), purpose), CancellationToken.None);
            Assert.Equal(ApplicationStatus.Succeeded, result.Status);
            Assert.Null(provider.Next);
            var response = Assert.IsType<ManageInventoryCaptureResponse>(result.Value);
            var captured = LocalInventoryDocumentCodec.Decode(response.Document);
            var loaded = await LoadAsync();
            Assert.Equal(WinPoolFactsCodec.Encode(captured.SourceFacts!), WinPoolFactsCodec.Encode(loaded.SourceFacts!));
            var snapshot = await new InventorySnapshotRepository(store).GetAsync(response.SnapshotId);
            Assert.NotNull(snapshot);
            Assert.Equal(captured.SystemId, snapshot.Snapshot.SystemId);
            Assert.Equal(captured.InventoryVersion, snapshot.Snapshot.InventoryVersion);
            return loaded;
        }

        public async Task<StorageSystemDocument> ReopenAsync()
        {
            await lease.DisposeAsync();
            await InitializeAsync();
            return await LoadAsync();
        }

        private async Task<StorageSystemDocument> LoadAsync()
        {
            var result = await coordinator.LoadManageAsync(new(CorrelationId.New()), CancellationToken.None);
            Assert.Equal(ApplicationStatus.Succeeded, result.Status);
            var loaded = Assert.IsType<ManageInventoryLoadedResponse>(result.Value);
            Assert.NotNull(loaded.SnapshotId);
            Assert.NotNull(loaded.Document);
            return LocalInventoryDocumentCodec.Decode(loaded.Document);
        }

        // Keep the isolated fixture database for inspection; never clean a user data root.
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class FixedHardwareProvider : IHardwareInventoryProvider
    {
        public Queue<(CollectionPurpose Purpose, StorageSystemDocument? Document)> Sequence { get; } = new();
        public StorageSystemDocument? Next { get; set; }
        public CollectionPurpose ExpectedPurpose { get; set; }
        public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) => Take(CollectionPurpose.Storage);
        public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) => Take(CollectionPurpose.Hardware);
        private Task<StorageSystemDocument> Take(CollectionPurpose purpose)
        {
            if (Sequence.TryDequeue(out var capture))
            {
                Assert.Equal(capture.Purpose, purpose);
                return capture.Document is null
                    ? Task.FromException<StorageSystemDocument>(new InventoryScanException("Injected failure", "test"))
                    : Task.FromResult(capture.Document);
            }
            Assert.Equal(ExpectedPurpose, purpose);
            var document = Assert.IsType<StorageSystemDocument>(Next);
            Next = null;
            return Task.FromResult(document);
        }
    }

    private sealed class UnusedInventoryProvider : IInventoryProvider
    {
        public InventoryProviderKind Kind => InventoryProviderKind.Replay;
        public Task<ApplicationResult<InventorySnapshot>> CaptureAsync(InventoryRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This regression must use only the fixed hardware input.");
    }

    private sealed class UnusedDeviceResolver : IPhysicalDiskDeviceResolver
    {
        public string? ResolvePnpDeviceId(int diskNumber) =>
            throw new InvalidOperationException("This regression must not query real hardware.");
    }
}
