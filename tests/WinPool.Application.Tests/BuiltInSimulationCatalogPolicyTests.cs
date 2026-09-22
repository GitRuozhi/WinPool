using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class BuiltInSimulationCatalogPolicyTests
{
    [Fact]
    public void FirstLoadSeedsTheCatalogAndDefersTheMarkerUntilPersistence()
    {
        var builtIns = new[]
        {
            Document("simulation:builtin:one", "Built-in one"),
            Document("simulation:builtin:two", "Built-in two")
        };

        var plan = BuiltInSimulationCatalogPolicy.PlanLoad([], builtIns, catalogSeeded: false);

        Assert.Equal(builtIns.Select(document => document.Id), plan.Documents.Select(document => document.Id));
        Assert.Equal(builtIns.Select(document => document.Id), plan.DocumentsToPersist.Select(document => document.Id));
        Assert.True(plan.MarkCatalogSeededAfterPersist);
    }

    [Fact]
    public void ReloadAfterDeletingTheLastBuiltInDoesNotRecreateIt()
    {
        var builtIns = new[]
        {
            Document("simulation:builtin:one", "Built-in one"),
            Document("simulation:builtin:two", "Built-in two")
        };
        var custom = Document("simulation:custom", "Custom");

        var plan = BuiltInSimulationCatalogPolicy.PlanLoad(
            [custom],
            builtIns,
            catalogSeeded: true);

        var retained = Assert.Single(plan.Documents);
        Assert.Equal(custom.Id, retained.Id);
        Assert.Empty(plan.DocumentsToPersist);
        Assert.False(plan.MarkCatalogSeededAfterPersist);
    }

    [Fact]
    public void ReloadWithNoRemainingSimulationStaysEmptyAfterTheCatalogWasSeeded()
    {
        var plan = BuiltInSimulationCatalogPolicy.PlanLoad(
            [],
            [Document("simulation:builtin:one", "Built-in one")],
            catalogSeeded: true);

        Assert.Empty(plan.Documents);
        Assert.Empty(plan.DocumentsToPersist);
        Assert.False(plan.MarkCatalogSeededAfterPersist);
    }

    [Fact]
    public void ResetExplicitlyRestoresDeletedBuiltInsAndReseedsTheCatalog()
    {
        var builtIns = new[]
        {
            Document("simulation:builtin:one", "Built-in one"),
            Document("simulation:builtin:two", "Built-in two")
        };

        var plan = BuiltInSimulationCatalogPolicy.PlanReset([], builtIns);

        Assert.Equal(builtIns.Select(document => document.Id), plan.Documents.Select(document => document.Id));
        Assert.Equal(builtIns.Select(document => document.Id), plan.DocumentsToPersist.Select(document => document.Id));
        Assert.True(plan.MarkCatalogSeededAfterPersist);
    }

    [Fact]
    public void ExistingBuiltInWithChangedSnapshotIsUpdatedInsteadOfDuplicated()
    {
        var existing = Document("simulation:builtin:one", "Old", snapshotVersion: "old");
        var current = Document("simulation:builtin:one", "Current", snapshotVersion: "current");

        var plan = BuiltInSimulationCatalogPolicy.PlanLoad(
            [existing],
            [current],
            catalogSeeded: true);

        var updated = Assert.Single(plan.Documents);
        Assert.Equal(existing.Id, updated.Id);
        Assert.Equal("Current", updated.DisplayName);
        Assert.Equal(existing.Revision + 1, updated.Revision);
        Assert.Single(plan.DocumentsToPersist);
        Assert.False(plan.MarkCatalogSeededAfterPersist);
    }

    private static StorageSystemDocument Document(
        string id,
        string displayName,
        string snapshotVersion = "test")
    {
        var snapshot = TestSnapshotFactory.Create() with { SnapshotVersion = snapshotVersion };
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            id,
            StorageSystemKind.Simulation,
            displayName,
            snapshot,
            [],
            DateTimeOffset.UtcNow);
    }
}
