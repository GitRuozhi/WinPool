namespace WinPool.Application;

/// <summary>
/// Checks a simulated snapshot for identity, geometry, letter, and
/// membership problems that Validate() does not cover.
/// </summary>
public static class SimulationSnapshotAuditor
{
    public static IReadOnlyList<string> Audit(StorageSnapshot snapshot, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var prefix = string.IsNullOrWhiteSpace(name) ? string.Empty : $"{name}: ";
        var findings = new List<string>();
        foreach (var error in StorageRelationshipProjector.Validate(snapshot))
        {
            findings.Add(prefix + error);
        }

        CheckDuplicateLetters(snapshot, prefix, findings);
        CheckPartitionGeometry(snapshot, prefix, findings);
        CheckMembership(snapshot, prefix, findings);
        CheckTierMedia(snapshot, prefix, findings);
        CheckVirtualDiskOsDisks(snapshot, prefix, findings);
        return findings;
    }

    private static void CheckDuplicateLetters(
        StorageSnapshot snapshot,
        string prefix,
        List<string> findings)
    {
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string letter, string owner)
        {
            var token = TopologyProjector.NormalizeDriveLetter(letter);
            if (token.Length != 1)
            {
                return;
            }

            if (used.TryGetValue(token, out var existing))
            {
                findings.Add($"{prefix}Drive letter {token}: is used by {existing} and {owner}.");
            }
            else
            {
                used[token] = owner;
            }
        }

        foreach (var volume in snapshot.Volumes)
        {
            Add(volume.DriveLetter, $"volume {volume.StableId}");
        }

        foreach (var disk in snapshot.NetworkDisks)
        {
            Add(disk.DriveLetter, $"network {disk.StableId}");
        }
    }

    private static void CheckPartitionGeometry(
        StorageSnapshot snapshot,
        string prefix,
        List<string> findings)
    {
        foreach (var group in snapshot.Partitions.GroupBy(
                     item => item.OsDiskStableId ?? $"disk-number:{item.DiskNumber}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var osDisk = snapshot.OsDisks.FirstOrDefault(item =>
                string.Equals(item.StableId, group.Key, StringComparison.OrdinalIgnoreCase));
            var ordered = group.OrderBy(item => item.Offset).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var partition = ordered[i];
                if (partition.Offset < 0 || partition.Size <= 0)
                {
                    findings.Add(
                        $"{prefix}Partition {partition.StableId} has invalid offset {partition.Offset} or size {partition.Size}.");
                }

                if (osDisk is not null && partition.Offset + partition.Size > osDisk.Size)
                {
                    findings.Add(
                        $"{prefix}Partition {partition.StableId} ends beyond disk {osDisk.StableId} ({partition.Offset + partition.Size} > {osDisk.Size}).");
                }

                if (i == 0)
                {
                    continue;
                }

                var previous = ordered[i - 1];
                if (previous.Offset + previous.Size > partition.Offset)
                {
                    findings.Add(
                        $"{prefix}Partitions {previous.StableId} and {partition.StableId} overlap.");
                }
            }
        }
    }

    private static void CheckMembership(
        StorageSnapshot snapshot,
        string prefix,
        List<string> findings)
    {
        var physicalIds = snapshot.PhysicalDisks
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in snapshot.StoragePools)
        {
            foreach (var member in pool.MemberPhysicalDiskIds)
            {
                if (!physicalIds.Contains(member))
                {
                    findings.Add($"{prefix}Pool {pool.StableId} lists missing member {member}.");
                }
            }
        }

        foreach (var tier in snapshot.StorageTiers)
        {
            var pool = snapshot.StoragePools.FirstOrDefault(item =>
                string.Equals(item.StableId, tier.PoolStableId, StringComparison.OrdinalIgnoreCase));
            foreach (var member in tier.MemberPhysicalDiskIds)
            {
                if (pool is not null
                    && !pool.MemberPhysicalDiskIds.Contains(member, StringComparer.OrdinalIgnoreCase))
                {
                    findings.Add(
                        $"{prefix}Tier {tier.StableId} member {member} is not in pool {pool.StableId}.");
                }
            }
        }

        foreach (var partition in snapshot.Partitions)
        {
            if (partition.OsDiskStableId is not null
                && snapshot.OsDisks.All(item => item.StableId != partition.OsDiskStableId))
            {
                findings.Add($"{prefix}Partition {partition.StableId} points at missing OS disk {partition.OsDiskStableId}.");
            }
        }
    }

    private static void CheckTierMedia(
        StorageSnapshot snapshot,
        string prefix,
        List<string> findings)
    {
        foreach (var tier in snapshot.StorageTiers)
        {
            var expected = EditWorkspace.NormalizeMedia(tier.MediaType);
            if (expected == "Unknown")
            {
                continue;
            }

            foreach (var memberId in tier.MemberPhysicalDiskIds)
            {
                var disk = snapshot.PhysicalDisks.FirstOrDefault(item =>
                    string.Equals(item.StableId, memberId, StringComparison.OrdinalIgnoreCase));
                if (disk is null)
                {
                    continue;
                }

                var actual = EditWorkspace.NormalizeMedia(disk.MediaType);
                if (actual != expected)
                {
                    findings.Add(
                        $"{prefix}Tier {tier.FriendlyName} is {expected} but member {disk.FriendlyName} is {actual}.");
                }
            }
        }
    }

    private static void CheckVirtualDiskOsDisks(
        StorageSnapshot snapshot,
        string prefix,
        List<string> findings)
    {
        foreach (var vdisk in snapshot.VirtualDisks)
        {
            if (snapshot.OsDisks.All(item => item.VirtualDiskStableId != vdisk.StableId))
            {
                findings.Add($"{prefix}Virtual disk {vdisk.FriendlyName} has no OS disk.");
            }
        }
    }
}
