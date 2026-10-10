using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationTierColumnsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("SSD", "Simple", 1, 3)]
    [InlineData("HDD", "Simple", 1, 3)]
    [InlineData("SCM", "Simple", 1, 3)]
    [InlineData("SSD", "Mirror", 1, 2)]
    [InlineData("HDD", "Mirror", 1, 2)]
    [InlineData("SCM", "Mirror", 1, 2)]
    [InlineData("SSD", "Parity", 3, 4)]
    [InlineData("HDD", "Parity", 3, 4)]
    [InlineData("SCM", "Parity", 3, 4)]
    public void ColumnsSurviveCreationDraftPreviewAndCommitForEveryTier(string media, string layout, int initial, int edited)
    {
        var document = Create(media);
        var service = new SimulationOperationService();
        var created = service.Apply(document, Request(media, layout, initial) with
        {
            Kind = SimulationEditKind.CreateTieredPool,
            TargetProviderKey = "primordial",
            Name = "Columns",
            MemberDiskIds = document.Snapshot.PhysicalDisks.Select(x => x.StableId).ToArray(),
            CreateVirtualDisk = false
        });
        Assert.True(created.Succeeded, created.Error);
        var tier = Assert.Single(created.Document.Snapshot.StorageTiers, x => x.MediaType == media);
        Assert.Equal(initial, tier.NumberOfColumns);
        Assert.Equal(layout == "Mirror" ? 2 : 1, tier.NumberOfDataCopies);

        var working = created.Document.Snapshot with
        {
            StorageTiers = created.Document.Snapshot.StorageTiers.Select(x => x.StableId == tier.StableId
                ? x with { NumberOfColumns = edited } : x).ToArray()
        };
        var plan = SimulationDraftPlanner.Build(created.Document.Snapshot, working);
        var request = Assert.Single(plan.Steps);
        Assert.Equal(edited, media switch
        {
            "HDD" => request.CapacityColumns,
            "SCM" => request.ScmColumns,
            _ => request.PerformanceColumns
        });
        var committed = service.ApplyPlan(created.Document, plan);
        Assert.True(committed.Succeeded, committed.Error);
        Assert.Equal(edited, committed.Document.Snapshot.StorageTiers.Single(x => x.StableId == tier.StableId).NumberOfColumns);

        var invalidColumns = layout == "Mirror" ? 3 : 5;
        var invalid = service.Apply(committed.Document, Request(media, layout, invalidColumns) with
        { TargetProviderKey = tier.PoolStableId! });
        Assert.False(invalid.Succeeded);
        Assert.Same(committed.Document, invalid.Document);
    }

    [Fact]
    public void LegacyRequestsOmitNewNullFieldsAndDeserializeWithoutChangingDefaults()
    {
        var legacy = JsonSerializer.Deserialize<SimulationEditRequest>("{\"Kind\":18,\"TargetProviderKey\":\"pool\",\"CapacityColumns\":2}")!;
        Assert.Equal(2, legacy.CapacityColumns);
        Assert.Null(legacy.PerformanceColumns);
        Assert.Null(legacy.ScmColumns);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(legacy));
        foreach (var name in new[] { "PerformanceColumns", "ScmColumns", "CapacityDataCopies", "PerformanceToleratedFailures", "ScmToleratedFailures" })
            Assert.False(serialized.RootElement.TryGetProperty(name, out _));
    }

    [Fact]
    public void NewlyFormattedSimulationRemainsDataFree()
    {
        var document = Create("SSD");
        var disk = new OsDiskInfo("os", "SSD", 1, "GPT", 100 * GiB, false, false, false, "disk0", null);
        document = document.WithCandidate(document.Snapshot with { OsDisks = [disk] });
        var result = new SimulationOperationService().Apply(document,
            new(SimulationEditKind.CreatePartition, "os", SizeBytes: 20 * GiB, FileSystem: "NTFS", AllocationUnitSize: 65536));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Assert.Single(result.Document.Snapshot.Volumes).Size, Assert.Single(result.Document.Snapshot.Volumes).SizeRemaining);
        Assert.False(EditWorkspace.DiskHoldsStoredData(result.Document.Snapshot, "disk0"));
    }

    private static SimulationEditRequest Request(string media, string layout, int columns)
    {
        var copies = layout == "Mirror" ? 2 : 1;
        var failures = layout == "Parity" ? 1 : layout == "Mirror" ? 1 : 0;
        var request = new SimulationEditRequest(SimulationEditKind.UpdateStoragePool, "pool");
        return media switch
        {
            "HDD" => request with { CapacityResiliency = layout, CapacityColumns = columns, CapacityDataCopies = copies, CapacityToleratedFailures = failures, CapacitySizeBytes = 20 * GiB },
            "SCM" => request with { ScmResiliency = layout, ScmColumns = columns, ScmDataCopies = copies, ScmToleratedFailures = failures, ScmSizeBytes = 20 * GiB },
            _ => request with { PerformanceResiliency = layout, PerformanceColumns = columns, PerformanceDataCopies = copies, PerformanceToleratedFailures = failures, PerformanceSizeBytes = 20 * GiB }
        };
    }

    private static StorageSystemDocument Create(string media)
    {
        var disks = Enumerable.Range(0, 4).Select(i => new PhysicalDiskInfo(
            $"disk{i}", true, $"Disk {i}", "Model", $"Serial{i}", "SATA", media,
            100 * GiB, 512, 4096, "Healthy", "OK", true, "", i,
            false, false, false, false, "primordial")).ToArray();
        var snapshot = StorageSnapshot.Empty("columns") with
        {
            PhysicalDisks = disks,
            StoragePools = [new("primordial", true, "Primordial", true, "Healthy", "OK", 400 * GiB, 0, null, disks.Select(x => x.StableId).ToArray())]
        };
        return new(StorageSystemDocument.CurrentSchemaVersion, "simulation:columns", StorageSystemKind.Simulation,
            "Columns", snapshot, [], DateTimeOffset.Now);
    }
}
