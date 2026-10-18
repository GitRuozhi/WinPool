using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

public sealed partial class AgentRealOperationService
{
    private static void RequireFullStructureBaseline(StorageSystemDocument document, SystemId system)
    {
        if (document.SystemId != system || document.Kind != StorageSystemKind.Local
            || document.SourceFacts is not { IsSimulation: false, IsMerged: false, ScopedCollection: null } facts
            || facts.SystemId != system)
            throw new InvalidDataException("The confirmed structural baseline must contain complete full raw storage evidence.");
    }

    private static bool ReturnedBoolean(WinPoolSourceObject? item, string name, bool expected) =>
        item?.Field(name) is { ReadState: FieldReadState.Returned, Value: { } value }
        && value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static bool HasReturnedPoolableMembers(StorageSystemDocument document, IReadOnlyList<StorageObjectId> members) =>
        members.All(member => ReturnedBoolean(document.SourceFacts?.Objects.SingleOrDefault(item => item.Id == member.ProviderKey), "CanPool", true));

    private static bool NeedsPoolabilityDiscovery(StorageSystemDocument document, IReadOnlyList<StorageObjectId> members)
    {
        if (members.Count == 0 || document.SourceFacts is not { IsSimulation: false, IsMerged: false } facts
            || facts.ScopedCollection is { Complete: false }) return false;
        var snapshot = document.Snapshot;
        var explicitlyNonPoolable = false;
        foreach (var member in members)
        {
            if (member.System != document.SystemId || member.Kind != StorageObjectKind.PhysicalDisk
                || snapshot.PhysicalDisks.SingleOrDefault(item => item.StableId == member.ProviderKey) is not { IsStable: true } physical
                || physical.IsBoot || physical.IsSystem || physical.IsPageFile || physical.IsCrashDump
                || snapshot.StoragePools.Any(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Contains(member.ProviderKey))) return false;
            var source = facts.Objects.SingleOrDefault(item => item.Id == member.ProviderKey);
            if (!ReturnedBoolean(source, "CanPool", true) && !ReturnedBoolean(source, "CanPool", false)) return false;
            explicitlyNonPoolable |= ReturnedBoolean(source, "CanPool", false);
            var disks = snapshot.OsDisks.Where(candidate => candidate.PhysicalDiskStableId == member.ProviderKey && candidate.VirtualDiskStableId is null).ToArray();
            if (disks.Length != 1 || disks[0] is not { IsBoot: false, IsSystem: false, IsOffline: false } disk
                || !disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                || snapshot.Partitions.Any(partition => partition.OsDiskStableId == disk.StableId)
                || !ReturnedBoolean(facts.Objects.SingleOrDefault(item => item.Id == disk.StableId), "IsReadOnly", false)) return false;
        }
        return explicitlyNonPoolable;
    }

    private async Task<StorageSystemDocument> DiscoverPoolabilityAsync(StorageSystemDocument before,
        IReadOnlyList<StorageObjectId> members, OperationId operation, string reason, string? profile,
        string machineBinding, CancellationToken ct)
    {
        if (readStructureScoped is null)
            throw new InvalidDataException("Provider-cache discovery requires an exact scoped inventory reader.");
        var scope = StorageInventoryScopeFactory.Create(before.SourceFacts!, operation, reason, members)
            with { EvidenceProfile = profile, DiscoverProviderCache = true };
        var raw = await StorageOperationTiming.MeasureAsync("real.poolability.provider_discovery",
            () => readStructureScoped(scope, ct), operation, reason, "RAW_members_CanPool_false", captureCount: 1);
        if (raw.Kind != StorageSystemKind.Local || raw.SourceFacts is not { IsSimulation: false, IsMerged: false } facts
            || facts.ScopedCollection is not { Complete: true } observed
            || !HasStructureResultScope(raw, operation, before.SystemId) || observed.Scope.StepId != reason
            || !observed.Scope.DiscoverProviderCache || !observed.Scope.Targets.ToHashSet().SetEquals(members)
            || !WindowsRealStorageReadProfile.Covers(observed.Scope.EvidenceProfile, profile)
            || facts.Sources.Any(source => source.ClassName == "Windows.StorageProviderDiscovery" && source.ReadState == FieldReadState.Failed))
            throw new InvalidDataException("Provider-cache discovery did not return complete raw evidence for the exact member scope.");
        // OS evidence deliberately excludes the cached CanPool/allocation fields,
        // while retaining exact provider identities, physical geometry, roles,
        // relationships and the partition-free RAW structure on every member.
        var now = timeProvider.GetUtcNow();
        var previous = new WindowsRealStorageTopology(before, machineBinding, now, WindowsRealStorageReadProfile.Os)
            .RequireSelectedPhysicalMemberSet(members, requirePoolable: false);
        var refreshed = new WindowsRealStorageTopology(raw, machineBinding, now, WindowsRealStorageReadProfile.Os)
            .RequireSelectedPhysicalMemberSet(members, requirePoolable: false);
        if (previous.Fingerprint != refreshed.Fingerprint || previous.PhysicalMemberFingerprint != refreshed.PhysicalMemberFingerprint)
            throw new InvalidDataException("The exact RAW members changed during provider-cache discovery.");
        return raw;
    }
}
