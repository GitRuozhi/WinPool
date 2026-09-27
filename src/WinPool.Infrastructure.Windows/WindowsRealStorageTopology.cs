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

        foreach (var item in connected)
        {
            if (!item.HasReliableIdentity || !HasIdentity(item))
            {
                throw new InvalidDataException(
                    "A related storage object has an unreliable Windows identity.");
            }
            if (item.ObjectType == FactObjectType.Disk
                && string.IsNullOrWhiteSpace(RequiredText(item, "Path")))
            {
                throw new InvalidDataException("A related OS disk lacks its provider path.");
            }
            if (item.ObjectType == FactObjectType.Partition
                && string.IsNullOrWhiteSpace(RequiredText(item, "Guid")))
            {
                throw new InvalidDataException("A related partition lacks its GUID.");
            }
            if (Snapshot.FieldIssues.Any(issue =>
                    StringComparer.Ordinal.Equals(issue.ObjectId, item.Id)
                    && issue.State != FieldReadState.Returned))
            {
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
            physicalFingerprint);
    }

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
    string PhysicalMemberFingerprint);

public interface IWindowsRealStorageFactSource
{
    Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken);
}

public sealed class WindowsRealStorageFactSource : IWindowsRealStorageFactSource
{
    private readonly IHardwareInventoryProvider provider;

    public WindowsRealStorageFactSource(IHardwareInventoryProvider? provider = null) =>
        this.provider = provider ?? new WindowsHardwareInventoryProvider(purpose: CollectionPurpose.Storage);

    public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken) =>
        provider.CollectLocalAsync(cancellationToken);
}

public sealed class WindowsRealStorageTopologyReader
{
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
}
