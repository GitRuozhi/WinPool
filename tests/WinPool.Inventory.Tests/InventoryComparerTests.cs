using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Inventory.Tests;

public sealed class InventoryComparerTests
{
    [Fact]
    public void ReportsPropertyIdentityParentAndRelationshipDifferences()
    {
        var system = SystemId.New();
        var root = Id(system, StorageObjectKind.System, "system");
        var disk = Id(system, StorageObjectKind.PhysicalDisk, "disk");
        var pool = Id(system, StorageObjectKind.StoragePool, "pool");
        var reference = Snapshot(
            system,
            "reference",
            [
                View(root, null, IdentityStability.Stable, ("name", "System")),
                View(disk, root, IdentityStability.Stable, ("size", "100"))
            ],
            [new StorageRelationshipView(pool, disk, "contains")]);
        var candidate = Snapshot(
            system,
            "candidate",
            [
                View(root, null, IdentityStability.Stable, ("name", "System")),
                View(disk, pool, IdentityStability.Temporary, ("size", "200"))
            ],
            []);

        var result = new InventoryComparer().Compare(reference, candidate);

        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences,
            item => item.Kind == InventoryDifferenceKind.PropertyMismatch);
        Assert.Contains(result.Differences,
            item => item.Kind == InventoryDifferenceKind.IdentityMismatch);
        Assert.True(result.Differences.Count(
            item => item.Kind == InventoryDifferenceKind.RelationshipMismatch) >= 2);
    }

    [Fact]
    public void ReportsAddedAndMissingObjectsAndAcceptsEquivalentSnapshot()
    {
        var system = SystemId.New();
        var root = Id(system, StorageObjectKind.System, "system");
        var disk = Id(system, StorageObjectKind.PhysicalDisk, "disk");
        var partition = Id(system, StorageObjectKind.Partition, "partition");
        var baseline = Snapshot(
            system,
            "v1",
            [View(root, null), View(disk, root)],
            []);
        var changed = Snapshot(
            system,
            "v2",
            [View(root, null), View(partition, root)],
            []);

        var equivalent = new InventoryComparer().Compare(baseline, baseline);
        var result = new InventoryComparer().Compare(baseline, changed);

        Assert.True(equivalent.IsEquivalent);
        Assert.Contains(result.Differences,
            item => item.Kind == InventoryDifferenceKind.MissingFromCandidate);
        Assert.Contains(result.Differences,
            item => item.Kind == InventoryDifferenceKind.AddedByCandidate);
    }

    private static InventorySnapshot Snapshot(
        SystemId system,
        string version,
        IReadOnlyList<StorageObjectView> objects,
        IReadOnlyList<StorageRelationshipView> relationships) =>
        new(
            system,
            InventoryProviderKind.NativeWindows,
            version,
            new string('a', 64),
            DateTimeOffset.UnixEpoch,
            objects,
            [],
            relationships);

    private static StorageObjectView View(
        StorageObjectId id,
        StorageObjectId? parent,
        IdentityStability stability = IdentityStability.Stable,
        params (string Key, string Value)[] properties) =>
        new(
            id,
            parent,
            id.ProviderKey,
            stability,
            properties.ToDictionary(item => item.Key, item => (string?)item.Value));

    private static StorageObjectId Id(
        SystemId system,
        StorageObjectKind kind,
        string key) =>
        new(system, kind, key);
}
