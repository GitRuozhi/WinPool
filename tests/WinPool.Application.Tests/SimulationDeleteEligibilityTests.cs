using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class SimulationDeleteEligibilityTests
{
    [Fact]
    public void UnmarkedBasicDataPartitionOnSystemDiskCanBeDeleted()
    {
        var source = TestSnapshotFactory.Create();
        var partition = Assert.Single(source.Partitions) with
        {
            Type = "BasicData",
            IsBoot = false,
            IsSystem = false
        };
        var protectedPartition = partition with
        {
            StableId = "partition:protected",
            PartitionNumber = partition.PartitionNumber + 1,
            IsBoot = true,
            IsSystem = true
        };
        var snapshot = source with
        {
            OsDisks = source.OsDisks.Select(item => item with { IsBoot = true, IsSystem = true }).ToArray(),
            Partitions = [protectedPartition, partition]
        };
        var document = SimulationDocument(snapshot);
        var request = new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId);

        Assert.True(document.Snapshot.OsDisks.Single().IsBoot);
        Assert.True(document.Snapshot.OsDisks.Single().IsSystem);
        Assert.False(StorageEditRules.CanDeleteSimulatedPartition(protectedPartition));
        Assert.True(StorageEditRules.CanDeleteSimulatedPartition(partition));
        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(snapshot, request).Verdict);

        var result = new SimulationOperationService().Apply(document, request);

        Assert.True(result.Succeeded, result.Error);
        Assert.DoesNotContain(result.Document.Snapshot.Partitions, item => item.StableId == partition.StableId);
        Assert.Contains(result.Document.Snapshot.Partitions, item => item.StableId == protectedPartition.StableId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BootOrSystemMarkedPartitionCannotBeDeleted(bool isBoot, bool isSystem)
    {
        var source = TestSnapshotFactory.Create();
        var partition = Assert.Single(source.Partitions) with { IsBoot = isBoot, IsSystem = isSystem };
        var snapshot = source with { Partitions = [partition] };
        var document = SimulationDocument(snapshot);
        var request = new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId);

        Assert.False(StorageEditRules.CanDeleteSimulatedPartition(null));
        Assert.False(StorageEditRules.CanDeleteSimulatedPartition(partition));
        Assert.Equal(StorageRuleVerdict.Deny, StorageEditRules.Evaluate(snapshot, request).Verdict);

        var result = new SimulationOperationService().Apply(document, request);

        Assert.False(result.Succeeded);
        Assert.Same(document, result.Document);
        Assert.Contains("cannot be deleted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EfiSystem", "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}")]
    [InlineData("MicrosoftReserved", "{e3c9e316-0b5c-4db8-817d-f92df00215ae}")]
    [InlineData("WindowsRecovery", "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}")]
    public void UnmarkedSpecialPartitionTypesRemainDeleteEligible(string type, string gptType)
    {
        var source = TestSnapshotFactory.Create();
        var partition = Assert.Single(source.Partitions) with
        {
            Type = type,
            IsBoot = false,
            IsSystem = false,
            PartitionTypeId = string.Empty,
            GptType = gptType,
            MbrType = string.Empty
        };
        var snapshot = source with { Partitions = [partition] };
        var document = SimulationDocument(snapshot);
        var request = new SimulationEditRequest(SimulationEditKind.DeletePartition, partition.StableId);

        Assert.True(StorageEditRules.CanDeleteSimulatedPartition(partition));
        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(snapshot, request).Verdict);
        Assert.Equal(type, Assert.Single(document.Snapshot.Partitions).Type);

        var result = new SimulationOperationService().Apply(document, request);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Document.Snapshot.Partitions);
    }

    private static StorageSystemDocument SimulationDocument(StorageSnapshot snapshot) =>
        new(
            StorageSystemDocument.CurrentSchemaVersion,
            "simulation:delete-eligibility",
            StorageSystemKind.Simulation,
            "Delete eligibility",
            snapshot,
            [],
            DateTimeOffset.UtcNow);
}
