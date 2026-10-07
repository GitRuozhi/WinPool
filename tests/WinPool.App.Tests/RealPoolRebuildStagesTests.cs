using System.Collections.Immutable;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealPoolRebuildStagesTests
{
    [Fact]
    public async Task ReleasedGptIsClearedInSeparatePlanBeforeFreshRawCreatesRequestedReplacement()
    {
        var fixture = new Fixture();
        var inventories = new Queue<WinPoolFacts>([fixture.Released, fixture.Raw]);
        var submissions = new List<RealOperationIntentRequest>();
        var clearConfirmations = 0;
        var completed = await fixture.Execute(request =>
        {
            RealOperationValidator.Validate(request);
            submissions.Add(request);
            return Task.FromResult(true);
        }, inventories.Dequeue, (facts, disk) =>
        {
            clearConfirmations++;
            Assert.Same(fixture.Released, facts);
            Assert.Equal("new-direct-osdisk", disk.StableId);
            Assert.Equal("GPT", disk.PartitionStyle);
            Assert.Single(WinPoolStorageProjection.Project(facts).Partitions);
            return Task.FromResult(true);
        });

        Assert.True(completed);
        Assert.Equal(1, clearConfirmations);
        Assert.Equal(3, submissions.Count);
        Assert.Collection(submissions[0].Steps,
            step => Assert.IsType<DeleteVirtualDiskCommand>(step.Command),
            step => Assert.IsType<DeletePoolCommand>(step.Command));
        Assert.DoesNotContain(submissions[0].Steps, step => step.Command is
            ClearDiskCommand or CreatePoolCommand or CreateVirtualDiskCommand);
        var clear = Assert.IsType<ClearDiskCommand>(Assert.Single(submissions[1].Steps).Command);
        Assert.Equal(new StorageObjectId(fixture.System, StorageObjectKind.OsDisk,
            "new-direct-osdisk"), clear.Disk.Existing);
        Assert.False(clear.RemoveOem);
        var createPool = Assert.IsType<CreatePoolCommand>(submissions[2].Steps[0].Command);
        Assert.Equal(fixture.Physical, createPool.PhysicalDisk.Existing);
        Assert.Equal("NewPool", createPool.Name);
        var createVd = Assert.IsType<CreateVirtualDiskCommand>(submissions[2].Steps[1].Command);
        Assert.Null(createVd.Pool.Existing);
        Assert.Equal("create-pool", createVd.Pool.CreatedByStep);
        Assert.Equal("NewDisk", createVd.Name);
        Assert.Equal(32L * 1024 * 1024 * 1024, createVd.SizeBytes);
        Assert.Equal(65536, createVd.InterleaveBytes);
        Assert.Equal(1, createVd.DataColumns);
        Assert.IsType<InitializeGptCommand>(submissions[2].Steps[2].Command);
        Assert.DoesNotContain(submissions[2].Steps, step => step.Command is ClearDiskCommand
            or DeletePoolCommand or DeleteVirtualDiskCommand);
        Assert.Empty(inventories);
    }

    [Theory]
    [InlineData(0)] // Deletion cancelled/failed/unknown: no release scan or next plan.
    [InlineData(1)] // Separate clear cancelled/failed/unknown: no RAW scan or creation.
    [InlineData(2)] // Creation cancelled/failed/unknown: no layout continuation.
    public async Task AnyUnconfirmedStageStopsWithoutPreparingLaterStage(int stoppedStage)
    {
        var fixture = new Fixture();
        var calls = new List<RealOperationIntentRequest>();
        var reads = 0;
        var confirmations = 0;
        var completed = await fixture.Execute(request =>
        {
            var index = calls.Count;
            calls.Add(request);
            return Task.FromResult(index != stoppedStage);
        }, () => ++reads == 1 ? fixture.Released : fixture.Raw,
        (_, _) => { confirmations++; return Task.FromResult(true); });
        Assert.False(completed);
        Assert.Equal(stoppedStage + 1, calls.Count);
        Assert.Equal(stoppedStage, reads);
        Assert.Equal(stoppedStage == 0 ? 0 : 1, confirmations);
    }

    [Fact]
    public async Task SeparateClearLossConfirmationCancellationDoesNotPrepareClearOrReplacement()
    {
        var fixture = new Fixture();
        var calls = 0;
        Assert.False(await fixture.Execute(_ => { calls++; return Task.FromResult(true); },
            () => fixture.Released, (_, _) => Task.FromResult(false)));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("missing-scan")]
    [InlineData("old-inventory")]
    [InlineData("failed-disk-source")]
    [InlineData("changed-serial")]
    [InlineData("changed-physical-object-id")]
    [InlineData("retained-association")]
    [InlineData("missing-disk-path")]
    [InlineData("ambiguous-association")]
    [InlineData("disk-readonly-unknown")]
    [InlineData("disk-count-unknown")]
    public async Task UnprovenReleasedMemberNeverPreparesClearOrReplacement(string defect)
    {
        var fixture = new Fixture();
        WinPoolFacts? observed = fixture.Released;
        observed = defect switch
        {
            "missing-scan" => null,
            "old-inventory" => observed with { InventoryVersion = fixture.Original.InventoryVersion },
            "failed-disk-source" => observed with { Sources = observed.Sources.Select(source =>
                source.ClassName == "MSFT_Disk" ? source with { ReadState = FieldReadState.Failed } : source).ToImmutableArray() },
            "changed-serial" => ReplaceField(observed, fixture.Physical.ProviderKey, "SerialNumber", "another-serial"),
            "changed-physical-object-id" => ReplaceField(observed, fixture.Physical.ProviderKey, "ObjectId", "another-object"),
            "retained-association" => observed with { Relationships = observed.Relationships.Select(relation =>
                relation.Kind == "same-device" ? relation with { IsRetained = true } : relation).ToImmutableArray() },
            "missing-disk-path" => observed with { Objects = observed.Objects.Select(item =>
                item.ObjectType == FactObjectType.Disk ? item with { Fields = item.Fields.Where(field =>
                    field.Name != "Path").ToImmutableArray() } : item).ToImmutableArray() },
            "ambiguous-association" => observed with { Relationships = observed.Relationships.Add(
                observed.Relationships.Single(relation => relation.Kind == "same-device")) },
            "disk-count-unknown" => observed with { Objects = observed.Objects.Select(item =>
                item.ObjectType == FactObjectType.Disk ? item with { Fields = item.Fields.Where(field =>
                    field.Name != "NumberOfPartitions").ToImmutableArray() } : item).ToImmutableArray() },
            "disk-readonly-unknown" => observed with { Objects = observed.Objects.Select(item =>
                item.ObjectType == FactObjectType.Disk ? item with { Fields = item.Fields.Select(field =>
                    field.Name == "IsReadOnly" ? WinPoolSourceField.Returned<bool?>("IsReadOnly", null,
                        FactValueType.Boolean, item.SourceRef) : field).ToImmutableArray() } : item).ToImmutableArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var calls = 0;
        var clearConfirmations = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Execute(
            _ => { calls++; return Task.FromResult(true); }, () => observed,
            (_, _) => { clearConfirmations++; return Task.FromResult(true); }));
        Assert.Equal(1, calls);
        Assert.Equal(0, clearConfirmations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearSuccessWithoutNewPartitionFreeRawFactsNeverPreparesReplacement(bool stale)
    {
        var fixture = new Fixture();
        var invalid = stale ? fixture.Released : fixture.Raw with
        {
            Objects = fixture.Raw.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                ? item with { Fields = item.Fields.Select(field => field.Name == "PartitionStyle"
                    ? WinPoolSourceField.Returned("PartitionStyle", 2UL, FactValueType.UInt64, item.SourceRef)
                    : field).ToImmutableArray() } : item).ToImmutableArray()
        };
        var calls = 0;
        var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Execute(
            _ => { calls++; return Task.FromResult(true); },
            () => ++reads == 1 ? fixture.Released : invalid, (_, _) => Task.FromResult(true)));
        Assert.Equal(2, calls);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task AlreadyReleasedPartitionFreeRawSkipsUnnecessaryClearAndStillPreparesSeparateCreation()
    {
        var fixture = new Fixture();
        var submissions = new List<RealOperationIntentRequest>();
        Assert.True(await fixture.Execute(request =>
        {
            submissions.Add(request);
            return Task.FromResult(true);
        }, () => fixture.Raw, (_, _) => throw new InvalidOperationException("No clearing is needed.")));
        Assert.Equal(2, submissions.Count);
        Assert.Equal(OperationIntent.RebuildStoragePool, submissions[0].Intent);
        Assert.Equal(OperationIntent.CreateStoragePool, submissions[1].Intent);
        Assert.DoesNotContain(submissions.SelectMany(request => request.Steps),
            step => step.Command is ClearDiskCommand);
    }

    private static WinPoolFacts ReplaceField(WinPoolFacts facts, string id, string name, string value) =>
        facts with { Objects = facts.Objects.Select(item => item.Id == id
            ? item with { Fields = item.Fields.Select(field => field.Name == name
                ? WinPoolSourceField.Returned(name, value, FactValueType.String, item.SourceRef)
                : field).ToImmutableArray() } : item).ToImmutableArray() };

    private sealed class Fixture
    {
        public SystemId System { get; } = SystemId.New();
        public StorageObjectId Physical { get; }
        public WinPoolFacts Original { get; }
        public WinPoolFacts Released { get; }
        public WinPoolFacts Raw { get; }
        public Fixture()
        {
            Physical = new(System, StorageObjectKind.PhysicalDisk, "exact-member");
            var time = DateTimeOffset.UtcNow;
            Original = Facts("original", time, "pooled");
            Released = Facts("released", time.AddSeconds(1), "GPT");
            Raw = Facts("raw", time.AddSeconds(2), "RAW");
        }

        public Task<bool> Execute(Func<RealOperationIntentRequest, Task<bool>> submit,
            Func<WinPoolFacts?> fresh, Func<WinPoolFacts, OsDiskInfo, Task<bool>> clearConfirmation) =>
            RealOperationProposalFactory.ExecuteSingleMemberPoolRebuildAsync(Original, Physical,
                new StorageObjectId(System, StorageObjectKind.StoragePool, "old-pool"),
                new StorageObjectId(System, StorageObjectKind.VirtualDisk, "old-vdisk"),
                "NewPool", new("NewDisk", 32L * 1024 * 1024 * 1024,
                    true, true, true, "NewData", 'W'), [], submit, fresh, clearConfirmation);

        private WinPoolFacts Facts(string version, DateTimeOffset time, string style)
        {
            const long capacity = 64L * 1024 * 1024 * 1024;
            var pooled = style == "pooled";
            var physical = new PhysicalDiskInfo("exact-member", true, "member", "WDC test",
                "exact-serial", "SATA", "HDD", capacity, 512, 4096, "Healthy", "OK",
                !pooled, "", 0, false, false, false, false, pooled ? "old-pool" : null);
            var disk = new OsDiskInfo(pooled ? "old-vdisk-osdisk" : "new-direct-osdisk",
                "direct", 0, pooled ? "RAW" : style, capacity, false, false, false,
                pooled ? null : physical.StableId, pooled ? "old-vdisk" : null);
            var snapshot = StorageSnapshot.Empty("test-machine") with
            {
                SnapshotVersion = version, ScannedAt = time, PhysicalDisks = [physical], OsDisks = [disk],
                StoragePools = pooled ? [new("old-pool", true, "old pool", false, "Healthy", "OK",
                    capacity, 0, null, [physical.StableId])] : [],
                VirtualDisks = pooled ? [new("old-vdisk", true, "old disk", "Healthy", "OK",
                    "Simple", "Fixed", 1, 65536, capacity, capacity, "old-pool", [], [0])] : [],
                Partitions = style == "GPT" ? [new("fresh-auto-msr", true, 0, 1,
                    "MicrosoftReserved", 17408, 16759808, false, false, "", "", "", null, 0,
                    "Healthy", "OK", "", disk.StableId, true,
                    "e3c9e316-0b5c-4db8-817d-f92df00215ae", "9511cbc2-c21b-11f1-a385-103d1c5a58da",
                    "e3c9e316-0b5c-4db8-817d-f92df00215ae")] : []
            };
            var facts = WinPoolSimulationFacts.Create(snapshot, System);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[] { "MSFT_StoragePool", "MSFT_StorageTier", "MSFT_VirtualDisk",
                         "MSFT_Disk", "MSFT_Partition", "MSFT_Volume" })
                if (!sources.Any(source => source.ClassName == className))
                    sources = sources.Add(new("empty:" + className, FactOrigin.StorageCim,
                        "root/microsoft/windows/storage", className, time, CollectionPurpose.Storage));
            return facts with
            {
                IsSimulation = false, Sources = sources,
                Objects = facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                    ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("Path",
                        "exact-provider-path:" + item.Id, FactValueType.String, item.SourceRef)) }
                    : item).ToImmutableArray()
            };
        }
    }
}
