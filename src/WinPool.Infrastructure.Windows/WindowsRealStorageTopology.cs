using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// A single fresh, unmerged collector result used for real preflight. The
/// ordinary workspace may retain old observations for display; this type may
/// not. Any failed storage query makes the result unusable for mutation.
/// </summary>
public sealed class WindowsRealStorageTopology
{
    private static readonly string[] RequiredStorageClasses =
    [
        "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_Disk",
        "MSFT_Partition", "MSFT_Volume"
    ];

    private readonly IReadOnlyDictionary<string, WinPoolSourceObject> objects;
    private readonly IReadOnlyDictionary<string, string[]> neighbours;

    public WindowsRealStorageTopology(
        StorageSystemDocument document,
        string machineBinding,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Kind != StorageSystemKind.Local
            || document.SourceFacts is not { IsSimulation: false } facts
            || facts.IsMerged
            || facts.ScopedCollection is { Complete: false }
            || facts.SystemId != document.SystemId
            || string.IsNullOrWhiteSpace(facts.InventoryVersion)
            || string.IsNullOrWhiteSpace(machineBinding)
            || facts.InventoryCapturedAt == default
            || facts.InventoryCapturedAt > now.AddSeconds(10)
            || now - facts.InventoryCapturedAt > TimeSpan.FromMinutes(2)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                document.Snapshot.Computer.Name, Environment.MachineName))
        {
            throw new InvalidDataException("Real preflight requires fresh, local, unmerged Windows facts.");
        }

        facts.Validate();
        foreach (var className in RequiredStorageClasses)
            RequireSource(facts, className);

        Facts = facts;
        Snapshot = document.Snapshot;
        MachineBinding = machineBinding;
        objects = facts.Objects.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in facts.Objects)
        {
            edges[item.Id] = new HashSet<string>(StringComparer.Ordinal);
        }
        foreach (var relation in facts.Relationships.Where(relation => !relation.IsRetained))
        {
            if (!IsCoreStorageType(objects[relation.FromId].ObjectType)
                || !IsCoreStorageType(objects[relation.ToId].ObjectType)) continue;
            if (IsPrimordialPool(objects[relation.FromId])
                || IsPrimordialPool(objects[relation.ToId])) continue;
            edges[relation.FromId].Add(relation.ToId);
            edges[relation.ToId].Add(relation.FromId);
        }
        neighbours = edges.ToDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    public WinPoolFacts Facts { get; }
    public StorageSnapshot Snapshot { get; }
    public string MachineBinding { get; }
    public string InventoryVersion => Facts.InventoryVersion;
    public SystemId SystemId => Facts.SystemId;

    public WinPoolSourceObject RequireObject(StorageObjectId target)
    {
        if (target.System != SystemId
            || !objects.TryGetValue(target.ProviderKey, out var value)
            || value.ObjectType != ExpectedType(target.Kind)
            || !value.HasReliableIdentity)
        {
            throw new InvalidDataException("The real target has no unique current Windows identity.");
        }
        return value;
    }

    /// <summary>
    /// Includes the entire connected storage component: an existing pool's
    /// other members cannot be hidden by selecting only one child in the UI.
    /// </summary>
    public RealTargetClosure RequireSinglePhysicalClosure(
        IReadOnlyList<StorageObjectId> targets)
    {
        if (targets is null || targets.Count == 0)
        {
            throw new InvalidDataException("A real plan needs exact existing targets.");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var target in targets)
        {
            pending.Enqueue(RequireObject(target).Id);
        }
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id)) continue;
            foreach (var neighbour in neighbours[id]) pending.Enqueue(neighbour);
        }

        var connected = visited.Select(id => objects[id]).ToArray();
        if (connected.Any(item => item.ObjectType == FactObjectType.StoragePool))
        {
            RequireSource(Facts, "MSFT_VirtualDisk");
            RequireSource(Facts, "MSFT_StorageTier");
        }
        var physical = connected
            .Where(item => item.ObjectType == FactObjectType.PhysicalDisk)
            .ToArray();
        if (physical.Length != 1 || !physical[0].HasReliableIdentity
            || string.IsNullOrWhiteSpace(RequiredText(physical[0], "SerialNumber"))
            || !HasIdentity(physical[0]))
        {
            throw new InvalidDataException(
                "The target cannot be proven to have exactly one identified physical member.");
        }

        var offlinePartitionsNeedingNativeProof = new HashSet<string>(StringComparer.Ordinal);
        WindowsPoolMemberRoleEvidence? poolMemberRoles = null;
        foreach (var item in connected)
        {
            if (!item.HasReliableIdentity || !HasIdentity(item))
            {
                throw new InvalidDataException(
                    "A related storage object has an unreliable Windows identity.");
            }
            if (item.ObjectType == FactObjectType.StorageTier)
                RequireTierMemberEvidence(item, physical[0].Id);
            if (item.ObjectType == FactObjectType.Disk
                && string.IsNullOrWhiteSpace(RequiredText(item, "Path")))
            {
                throw new InvalidDataException("A related OS disk lacks its provider path.");
            }
            if (item.ObjectType == FactObjectType.Disk)
            {
                var deviceParents = Facts.Relationships.Where(relation => !relation.IsRetained
                    && relation.Kind == "same-device" && relation.ToId == item.Id).ToArray();
                if (deviceParents.Length != 1 || objects[deviceParents[0].FromId].ObjectType is not
                    (FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk))
                    throw new InvalidDataException("A related OS disk lacks one exact physical or virtual device parent.");
            }
            if (item.ObjectType == FactObjectType.Partition
                && string.IsNullOrWhiteSpace(RequiredText(item, "Guid")))
            {
                throw new InvalidDataException("A related partition lacks its GUID.");
            }
            foreach (var issue in Snapshot.FieldIssues.Where(issue =>
                         StringComparer.Ordinal.Equals(issue.ObjectId, item.Id)
                         && issue.State != FieldReadState.Returned))
            {
                if (item.ObjectType == FactObjectType.PhysicalDisk
                    && issue.FieldName is "InterfaceType" or "ProvisioningType" or "PnpDeviceId") continue;
                if (item.ObjectType == FactObjectType.PhysicalDisk
                    && issue.FieldName is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump")
                {
                    if (issue.State is not (FieldReadState.NotCollected or FieldReadState.Unavailable))
                        throw new InvalidDataException("A physical role observation failed.");
                    poolMemberRoles ??= RequirePoolMemberRoleEvidence(item);
                    continue;
                }
                if (IsUnallocatedTemplateCapacity(item, issue)) continue;
                if (IsOfflinePartitionHiddenUnavailable(item, issue))
                {
                    offlinePartitionsNeedingNativeProof.Add(item.Id);
                    continue;
                }
                throw new InvalidDataException(
                    "A related storage safety field could not be read.");
            }
        }

        var serialized = CanonicalComponent(connected, visited);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
        var physicalMaterial = string.Join('\n',
            "physical-member-v1", MachineBinding,
            RequiredText(physical[0], "UniqueId"),
            RequiredText(physical[0], "ObjectId"),
            RequiredText(physical[0], "SerialNumber"));
        var physicalFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(physicalMaterial))).ToLowerInvariant();
        return new RealTargetClosure(
            physical[0].Id,
            connected.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            fingerprint,
            physicalFingerprint)
        {
            OfflinePartitionIdsNeedingNativeProof = offlinePartitionsNeedingNativeProof
                .Order(StringComparer.Ordinal).ToArray(),
            PoolMemberRoleEvidence = poolMemberRoles
        };
    }

    private WindowsPoolMemberRoleEvidence RequirePoolMemberRoleEvidence(WinPoolSourceObject physical)
    {
        foreach (var name in new[] { "IsBoot", "IsSystem", "IsPageFile", "IsCrashDump" })
        {
            if (physical.Field(name) is not { } field) continue;
            if (field is not { ReadState: FieldReadState.Returned, Value: { } value }
                || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("A physical role observation is unavailable or invalid.");
            if (value.ValueKind == JsonValueKind.True)
                throw new InvalidDataException("The physical member has a protected Windows role.");
        }
        foreach (var name in new[] { "MSFT_StorageSubSystem", "MSFT_PhysicalDisk", "MSFT_StoragePool",
                     "MSFT_VirtualDisk", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume" }) RequireSource(Facts, name);
        var relations = Facts.Relationships.Where(item => !item.IsRetained).ToArray();
        var parents = relations.Where(item => item.Kind == "pool-member" && item.ToId == physical.Id
            && objects[item.FromId].ObjectType == FactObjectType.StoragePool && !IsPrimordialPool(objects[item.FromId])).ToArray();
        if (parents.Length != 1 || objects[parents[0].FromId] is not { HasReliableIdentity: true } pool
            || !HasIdentity(pool) || !IsReturnedBoolean(pool, "IsPrimordial", false))
            throw new InvalidDataException("Unknown physical roles need one exact current concrete pool membership.");
        var members = relations.Where(item => item.Kind == "pool-member" && item.FromId == pool.Id).ToArray();
        var projectedPool = Snapshot.StoragePools.SingleOrDefault(item => item.StableId == pool.Id);
        var subsystemParents = relations.Where(item => item.Kind == "subsystem-pool" && item.ToId == pool.Id).ToArray();
        if (members.Length != 1 || members[0].ToId != physical.Id || projectedPool is null
            || projectedPool.IsPrimordial || projectedPool.MemberPhysicalDiskIds.Count != 1
            || projectedPool.MemberPhysicalDiskIds[0] != physical.Id
            || subsystemParents.Length != 1
            || objects[subsystemParents[0].FromId] is not { ObjectType: FactObjectType.StorageSubsystem,
                HasReliableIdentity: true } subsystem || !HasIdentity(subsystem))
            throw new InvalidDataException("The concrete pool's exact physical member or subsystem association is incomplete.");

        // A complete class query alone does not prove complete ownership. Every
        // VD and OS disk must have a unique current provider association, so an
        // unassociated protected disk cannot disappear from this role proof.
        var virtualIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var virtualDisk in objects.Values.Where(item => item.ObjectType == FactObjectType.VirtualDisk))
        {
            var owners = relations.Where(item => item.Kind == "pool-virtual-disk" && item.ToId == virtualDisk.Id).ToArray();
            if (!virtualDisk.HasReliableIdentity || !HasIdentity(virtualDisk) || owners.Length != 1
                || objects[owners[0].FromId] is not { ObjectType: FactObjectType.StoragePool,
                    HasReliableIdentity: true } owner || !HasIdentity(owner) || !IsReturnedBoolean(owner, "IsPrimordial", false))
                throw new InvalidDataException("A current virtual disk lacks its exact concrete pool association.");
            if (owner.Id == pool.Id) virtualIds.Add(virtualDisk.Id);
        }
        var relatedDisks = new List<WinPoolSourceObject>();
        foreach (var disk in objects.Values.Where(item => item.ObjectType == FactObjectType.Disk))
        {
            var owners = relations.Where(item => item.Kind == "same-device" && item.ToId == disk.Id).ToArray();
            if (!disk.HasReliableIdentity || !HasIdentity(disk) || owners.Length != 1
                || objects[owners[0].FromId] is not { HasReliableIdentity: true } owner || !HasIdentity(owner)
                || owner.ObjectType is not (FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk))
                throw new InvalidDataException("A current OS disk lacks its exact physical or virtual device association.");
            if (owner.Id == physical.Id || virtualIds.Contains(owner.Id)) relatedDisks.Add(disk);
        }
        var isBoot = false;
        var isSystem = false;
        var isPageFile = false;
        var isCrashDump = false;
        foreach (var disk in relatedDisks)
        {
            isBoot |= RequireReturnedBoolean(disk, "IsBoot");
            isSystem |= RequireReturnedBoolean(disk, "IsSystem");
            var roles = relations.Where(item => item.Kind == "disk-supplement" && item.FromId == disk.Id)
                .Select(item => objects[item.ToId]).Where(item => Facts.Sources.Single(source => source.Id == item.SourceRef)
                    .ClassName == "Windows.DiskRoles").ToArray();
            if (roles.Length != 1
                || Facts.Sources.Single(source => source.Id == roles[0].SourceRef) is not
                    { ReadState: FieldReadState.Returned, Namespace: "winpool/native" } roleSource
                || roleSource.CapturedAt != Facts.InventoryCapturedAt
                || !SameReturnedInteger(disk.Field("Number"), roles[0].Field("DiskNumber")))
                throw new InvalidDataException("The exact current OS disk lacks complete native page-file and crash-dump roles.");
            isPageFile |= RequireReturnedBoolean(roles[0], "IsPageFile");
            isCrashDump |= RequireReturnedBoolean(roles[0], "IsCrashDump");
        }
        // Null raw fields remain null. A new proof binds the aggregate to this
        // capture; a successful empty pool/VD/disk discovery proves no roles.
        if (isBoot || isSystem || isPageFile || isCrashDump)
            throw new InvalidDataException("A current pool member backs a protected OS disk role.");
        return new(InventoryVersion, physical.Id, pool.Id,
            relatedDisks.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            isBoot, isSystem, isPageFile, isCrashDump);
    }

    private static bool IsReturnedBoolean(WinPoolSourceObject item, string name, bool expected) =>
        item.Field(name) is { ReadState: FieldReadState.Returned, Value: { } value }
        && value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static bool RequireReturnedBoolean(WinPoolSourceObject item, string name) =>
        item.Field(name) is { ReadState: FieldReadState.Returned, Value: { } value }
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : throw new InvalidDataException("A current pool-associated OS disk role is unavailable: " + name);

    private static bool SameReturnedInteger(WinPoolSourceField? left, WinPoolSourceField? right) =>
        left is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.Number } leftValue }
        && right is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.Number } rightValue }
        && leftValue.TryGetInt64(out var leftNumber) && rightValue.TryGetInt64(out var rightNumber)
        && leftNumber == rightNumber;

    private string CanonicalComponent(
        IReadOnlyList<WinPoolSourceObject> connected,
        HashSet<string> visited)
    {
        var builder = new StringBuilder();
        builder.Append("real-component-v2\n").Append(MachineBinding).Append('\n');
        foreach (var item in connected.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            builder.Append("object|").Append((int)item.ObjectType).Append('|')
                .Append(item.Id).Append('|').Append(item.SourceIdentity).Append('\n');
            foreach (var field in item.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                // File writes can change free-space counters without changing
                // identity, protection, geometry, layout, or mount ownership.
                if (field.Name.Equals("SizeRemaining", StringComparison.OrdinalIgnoreCase))
                    continue;
                // A value and its read state are both part of the safety facts.
                builder.Append("field|").Append(field.Name).Append('|')
                    .Append((int)field.ReadState).Append('|')
                    .Append(field.Value is { } value ? value.GetRawText() : "<missing>")
                    .Append('\n');
            }
        }
        foreach (var relation in Facts.Relationships
                     .Where(relation => !relation.IsRetained
                         && visited.Contains(relation.FromId)
                         && visited.Contains(relation.ToId))
                     .OrderBy(relation => relation.FromId, StringComparer.Ordinal)
                     .ThenBy(relation => relation.Kind, StringComparer.Ordinal)
                     .ThenBy(relation => relation.ToId, StringComparer.Ordinal))
        {
            builder.Append("edge|").Append(relation.FromId).Append('|')
                .Append(relation.Kind).Append('|').Append(relation.ToId).Append('\n');
        }
        return builder.ToString();
    }

    private static void RequireSource(WinPoolFacts facts, string className)
    {
        var sources = facts.Sources
            .Where(source => StringComparer.Ordinal.Equals(source.ClassName, className))
            .ToArray();
        if (sources.Length == 0 || sources.Any(source =>
                source.ReadState != FieldReadState.Returned))
        {
            throw new InvalidDataException(
                $"Real preflight lacks a complete {className} query.");
        }
    }

    private bool IsUnallocatedTemplateCapacity(WinPoolSourceObject item, StorageFieldIssue issue)
    {
        // Windows defines pool-template Size only after cloning into a VD.
        // Returned null is non-applicable here; missing/failed layout facts
        // and every VD-instance capacity remain barriers.
        if (item.ObjectType != FactObjectType.StorageTier
            || issue.FieldName is not ("Size" or "FootprintOnPool")
            || item.Field(issue.FieldName) is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: JsonValueKind.Null } }
            || Facts.Relationships.Any(relation => !relation.IsRetained
                && relation.Kind == "virtual-disk-tier" && relation.ToId == item.Id)) return false;
        var parents = Facts.Relationships.Where(relation => !relation.IsRetained
            && relation.Kind == "pool-tier" && relation.ToId == item.Id).ToArray();
        return parents.Length == 1 && objects.TryGetValue(parents[0].FromId, out var pool)
            && pool.ObjectType == FactObjectType.StoragePool && !IsPrimordialPool(pool)
            && Facts.Relationships.Any(relation => !relation.IsRetained
                && relation.Kind == "template-pool-member" && relation.FromId == item.Id);
    }

    private bool IsOfflinePartitionHiddenUnavailable(WinPoolSourceObject item, StorageFieldIssue issue)
    {
        // Windows returns IsHidden=null after a basic GPT disk is taken offline.
        // Retain that null in the fingerprint. It permits describing the exact
        // component; mutation and observed-state validation still require fresh
        // native GPT attributes for every partition listed in the closure.
        if (item.ObjectType != FactObjectType.Partition
            || issue.FieldName != "IsHidden" || issue.State != FieldReadState.Unavailable
            || issue.Reason != "ReturnedNull"
            || item.Field("IsHidden") is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: JsonValueKind.Null } }) return false;
        var parents = Facts.Relationships.Where(relation => !relation.IsRetained
            && relation.Kind == "disk-partition" && relation.ToId == item.Id).ToArray();
        if (parents.Length != 1 || !objects.TryGetValue(parents[0].FromId, out var disk)
            || disk.ObjectType != FactObjectType.Disk || !disk.HasReliableIdentity
            || !HasIdentity(disk)
            || disk.Field("IsOffline") is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: JsonValueKind.True } }
            || disk.Field("PartitionStyle") is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: JsonValueKind.Number } style }
            || !style.TryGetInt32(out var styleCode) || styleCode != 2
            || !Guid.TryParse(RequiredText(disk, "Guid"), out var diskGuid)
            || diskGuid == Guid.Empty
            || string.IsNullOrWhiteSpace(RequiredText(disk, "Path"))
            || !StringComparer.OrdinalIgnoreCase.Equals(RequiredText(item, "DiskId"), RequiredText(disk, "Path")))
            return false;
        var projectedDisk = Snapshot.OsDisks.SingleOrDefault(value => value.StableId == disk.Id);
        var projectedPartition = Snapshot.Partitions.SingleOrDefault(value => value.StableId == item.Id);
        return projectedDisk is { IsOffline: true, PartitionStyle: "GPT" }
            && projectedPartition?.OsDiskStableId == disk.Id;
    }

    private void RequireTierMemberEvidence(WinPoolSourceObject item, string physicalId)
    {
        var parents = Facts.Relationships.Where(relation => !relation.IsRetained
            && relation.Kind == "pool-tier" && relation.ToId == item.Id).ToArray();
        var owners = Facts.Relationships.Where(relation => !relation.IsRetained
            && relation.Kind == "virtual-disk-tier" && relation.ToId == item.Id).ToArray();
        if (parents.Length != 1 || owners.Length > 1)
            throw new InvalidDataException("A tier lacks one exact pool and an unambiguous template/instance role.");
        var relationKind = owners.Length == 0 ? "template-pool-member" : "tier-member";
        var members = Facts.Relationships.Where(relation => !relation.IsRetained
            && relation.Kind == relationKind && relation.FromId == item.Id).ToArray();
        if (members.Length != 1 || members[0].ToId != physicalId)
            throw new InvalidDataException("The tier's exact template eligibility or instance allocation member is unknown.");
    }

    private static bool IsCoreStorageType(FactObjectType type) => type is
        FactObjectType.PhysicalDisk or FactObjectType.StoragePool or
        FactObjectType.StorageTier or FactObjectType.VirtualDisk or
        FactObjectType.Disk or FactObjectType.Partition or FactObjectType.Volume;

    private static bool IsPrimordialPool(WinPoolSourceObject item) =>
        item.ObjectType == FactObjectType.StoragePool
        && item.Field("IsPrimordial") is
        {
            ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.True }
        };

    private static bool HasIdentity(WinPoolSourceObject item) =>
        !string.IsNullOrWhiteSpace(Text(item, "UniqueId"))
        || !string.IsNullOrWhiteSpace(Text(item, "ObjectId"))
        || item.ObjectType == FactObjectType.Partition
           && !string.IsNullOrWhiteSpace(Text(item, "Guid"));

    private static string RequiredText(WinPoolSourceObject item, string name) =>
        Text(item, name) ?? string.Empty;

    private static string? Text(WinPoolSourceObject item, string name)
    {
        var field = item.Field(name);
        return field is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
            ? value.GetString()?.Trim()
            : null;
    }

    private static FactObjectType ExpectedType(StorageObjectKind kind) => kind switch
    {
        StorageObjectKind.PhysicalDisk => FactObjectType.PhysicalDisk,
        StorageObjectKind.StoragePool => FactObjectType.StoragePool,
        StorageObjectKind.StorageTier => FactObjectType.StorageTier,
        StorageObjectKind.VirtualDisk => FactObjectType.VirtualDisk,
        StorageObjectKind.OsDisk => FactObjectType.Disk,
        StorageObjectKind.Partition => FactObjectType.Partition,
        StorageObjectKind.Volume => FactObjectType.Volume,
        _ => throw new InvalidDataException("This object kind is not a real storage target.")
    };
}

public sealed record RealTargetClosure(
    string PhysicalDiskId,
    IReadOnlyList<WinPoolSourceObject> Objects,
    string Fingerprint,
    string PhysicalMemberFingerprint)
{
    public IReadOnlyList<string> OfflinePartitionIdsNeedingNativeProof { get; init; } = [];
    public WindowsPoolMemberRoleEvidence? PoolMemberRoleEvidence { get; init; }
}

public sealed record WindowsPoolMemberRoleEvidence(string InventoryVersion, string PhysicalStableId,
    string PoolStableId, IReadOnlyList<string> AssociatedOsDiskIds,
    bool IsBoot, bool IsSystem, bool IsPageFile, bool IsCrashDump)
{
    public string VerificationMethod { get; init; } = "CompleteCurrentPoolAndOsDiskAssociations";
}

public interface IWindowsRealStorageFactSource
{
    bool SupportsScopedCapture => false;
    Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken);
    Task<StorageSystemDocument> CaptureFreshAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
        Task.FromException<StorageSystemDocument>(new NotSupportedException("The real fact source does not support exact scoped collection."));
}

public sealed class WindowsRealStorageFactSource : IWindowsRealStorageFactSource
{
    private readonly IHardwareInventoryProvider provider;
    public bool SupportsScopedCapture => provider is IScopedHardwareInventoryProvider;

    public WindowsRealStorageFactSource(IHardwareInventoryProvider? provider = null) =>
        this.provider = provider ?? new WindowsHardwareInventoryProvider(purpose: CollectionPurpose.Storage);

    public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken) =>
        provider.CollectLocalAsync(cancellationToken);

    public Task<StorageSystemDocument> CaptureFreshAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
        provider is IScopedHardwareInventoryProvider scoped ? scoped.CollectScopedAsync(scope, cancellationToken)
            : Task.FromException<StorageSystemDocument>(new NotSupportedException("The real inventory provider does not support exact scoped collection."));
}

public sealed class WindowsRealStorageTopologyReader
{
    public bool SupportsScopedCapture => source.SupportsScopedCapture;
    private readonly IWindowsRealStorageFactSource source;
    private readonly IRealMachineIdentityProvider machineIdentity;
    private readonly TimeProvider timeProvider;

    public WindowsRealStorageTopologyReader(
        IWindowsRealStorageFactSource? source = null,
        IRealMachineIdentityProvider? machineIdentity = null,
        TimeProvider? timeProvider = null)
    {
        this.source = source ?? new WindowsRealStorageFactSource();
        this.machineIdentity = machineIdentity ?? new WindowsRealMachineIdentityProvider();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WindowsRealStorageTopology> CaptureAsync(CancellationToken cancellationToken)
    {
        var binding = await machineIdentity.ReadBindingAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = await source.CaptureFreshAsync(cancellationToken)
            .ConfigureAwait(false);
        return new WindowsRealStorageTopology(
            document, binding, timeProvider.GetUtcNow());
    }

    public async Task<WindowsRealStorageTopology> CaptureScopedAsync(StorageInventoryScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        var binding = await machineIdentity.ReadBindingAsync(cancellationToken).ConfigureAwait(false);
        var document = await source.CaptureFreshAsync(scope, cancellationToken).ConfigureAwait(false);
        if (document.SystemId != scope.SystemId || document.SourceFacts is not { IsMerged: false, ScopedCollection: { Complete: true } collected }
            || collected.Scope.SystemId != scope.SystemId || collected.Scope.OperationId != scope.OperationId
            || collected.Scope.StepId != scope.StepId || collected.Scope.Generation != scope.Generation
            || !collected.Scope.Targets.SequenceEqual(scope.Targets))
            throw new InvalidDataException("Real scoped preflight requires fresh complete evidence for the requested operation and targets.");
        return new WindowsRealStorageTopology(document, binding, timeProvider.GetUtcNow());
    }
}
