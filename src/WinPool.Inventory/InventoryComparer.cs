using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Inventory;

public sealed class InventoryComparer : IInventoryComparer
{
    public InventoryComparison Compare(
        InventorySnapshot reference,
        InventorySnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        if (reference.SystemId != candidate.SystemId)
        {
            throw new ArgumentException("只能比较同一个逻辑系统的采集快照。");
        }

        var differences = new List<InventoryDifference>();
        var referenceObjects = reference.Objects.ToDictionary(
            item => Key(item.Id),
            StringComparer.Ordinal);
        var candidateObjects = candidate.Objects.ToDictionary(
            item => Key(item.Id),
            StringComparer.Ordinal);
        foreach (var key in referenceObjects.Keys
                     .Union(candidateObjects.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var hasReference = referenceObjects.TryGetValue(key, out var left);
            var hasCandidate = candidateObjects.TryGetValue(key, out var right);
            if (!hasReference)
            {
                differences.Add(
                    new InventoryDifference(
                        InventoryDifferenceKind.AddedByCandidate,
                        null,
                        right!.Id,
                        string.Empty,
                        string.Empty,
                        right.DisplayName));
                continue;
            }

            if (!hasCandidate)
            {
                differences.Add(
                    new InventoryDifference(
                        InventoryDifferenceKind.MissingFromCandidate,
                        left!.Id,
                        null,
                        string.Empty,
                        left.DisplayName,
                        string.Empty));
                continue;
            }

            if (left!.IdentityStability != right!.IdentityStability)
            {
                differences.Add(
                    Difference(
                        InventoryDifferenceKind.IdentityMismatch,
                        left,
                        right,
                        "$identityStability",
                        left.IdentityStability.ToString(),
                        right.IdentityStability.ToString()));
            }

            if (left.ParentId != right.ParentId)
            {
                differences.Add(
                    Difference(
                        InventoryDifferenceKind.RelationshipMismatch,
                        left,
                        right,
                        "$parent",
                        left.ParentId is { } leftParent ? Key(leftParent) : string.Empty,
                        right.ParentId is { } rightParent ? Key(rightParent) : string.Empty));
            }

            foreach (var property in left.Properties.Keys
                         .Union(right.Properties.Keys, StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var leftValue = left.Properties.GetValueOrDefault(property);
                var rightValue = right.Properties.GetValueOrDefault(property);
                if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                {
                    differences.Add(
                        Difference(
                            InventoryDifferenceKind.PropertyMismatch,
                            left,
                            right,
                            property,
                            leftValue ?? string.Empty,
                            rightValue ?? string.Empty));
                }
            }
        }

        CompareRelationships(reference, candidate, differences);
        return new InventoryComparison(
            reference.InventoryVersion,
            candidate.InventoryVersion,
            differences.Count == 0,
            differences);
    }

    private static void CompareRelationships(
        InventorySnapshot reference,
        InventorySnapshot candidate,
        ICollection<InventoryDifference> differences)
    {
        var left = (reference.Relationships ?? [])
            .ToDictionary(RelationshipKey, StringComparer.Ordinal);
        var right = (candidate.Relationships ?? [])
            .ToDictionary(RelationshipKey, StringComparer.Ordinal);
        foreach (var key in left.Keys
                     .Union(right.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (left.ContainsKey(key) == right.ContainsKey(key))
            {
                continue;
            }

            var relationship = left.GetValueOrDefault(key)
                ?? right.GetValueOrDefault(key)!;
            differences.Add(
                new InventoryDifference(
                    InventoryDifferenceKind.RelationshipMismatch,
                    relationship.FromObjectId,
                    relationship.ToObjectId,
                    relationship.RelationshipKind,
                    left.ContainsKey(key) ? "present" : "missing",
                    right.ContainsKey(key) ? "present" : "missing"));
        }
    }

    private static InventoryDifference Difference(
        InventoryDifferenceKind kind,
        StorageObjectView left,
        StorageObjectView right,
        string property,
        string leftValue,
        string rightValue) =>
        new(
            kind,
            left.Id,
            right.Id,
            property,
            leftValue,
            rightValue);

    private static string RelationshipKey(StorageRelationshipView relationship) =>
        $"{Key(relationship.FromObjectId)}>{Key(relationship.ToObjectId)}:" +
        relationship.RelationshipKind;

    private static string Key(StorageObjectId id) =>
        $"{id.Kind}:{id.ProviderKey}";
}
