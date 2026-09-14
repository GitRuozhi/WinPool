using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

public enum FieldReadState { Returned, NotCollected, Unavailable, Failed }
public enum FactValueType { String, Boolean, Int64, UInt64, Decimal, StringArray, Int64Array, UInt64Array }
public enum FactOrigin { StorageCim, Win32, Native, Simulation, Import }
public enum FactObjectType
{
    Computer, OperatingSystem, StorageSubsystem, StoragePool, StorageTier,
    PhysicalDisk, VirtualDisk, Disk, Partition, Volume, NetworkDisk,
    BaseBoard, Bios, Processor, CpuCache, MemoryArray, MemoryModule,
    PageFileSetting, PageFileUsage, VideoController, Monitor, NetworkAdapter, Battery
}
public enum CollectionPurpose { Storage, Hardware }

/// <summary>A returned null is different from a missing or failed query. Values are never formatted for storage.</summary>
public sealed record WinPoolSourceField(
    string Name,
    FactValueType ValueType,
    JsonElement? Value,
    FieldReadState ReadState,
    string SourceRef,
    string? Unit = null,
    string? ReasonCode = null,
    bool IsRedacted = false)
{
    public static WinPoolSourceField Returned<T>(string name, T value, FactValueType type,
        string sourceRef, string? unit = null) =>
        new(name, type, JsonSerializer.SerializeToElement(value), FieldReadState.Returned, sourceRef, unit);

    public static WinPoolSourceField Missing(string name, FactValueType type, string sourceRef,
        FieldReadState state, string? reason = null, string? unit = null)
    {
        if (state == FieldReadState.Returned)
            throw new ArgumentException("Use Returned for an actual null value.", nameof(state));
        return new(name, type, null, state, sourceRef, unit, reason);
    }

    public bool TryGetInt64(out long value)
    {
        value = 0;
        return ReadState == FieldReadState.Returned && !IsRedacted
            && Value is { ValueKind: JsonValueKind.Number } element && element.TryGetInt64(out value);
    }

    public string DisplayValue() => IsRedacted ? "••••" : Value switch
    {
        null => ReadState == FieldReadState.Returned ? "null" : string.Empty,
        { ValueKind: JsonValueKind.Null } => "null",
        { ValueKind: JsonValueKind.String } item => item.GetString() ?? string.Empty,
        { } item => item.GetRawText()
    };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(SourceRef)
            || !Enum.IsDefined(ValueType) || !Enum.IsDefined(ReadState))
            throw new InvalidDataException("Invalid source field metadata.");
        if (ReadState != FieldReadState.Returned && Value is not null)
            throw new InvalidDataException("A failed or uncollected field cannot contain a returned value.");
        if (Value is null || Value.Value.ValueKind == JsonValueKind.Null || IsRedacted)
            return;
        var v = Value.Value;
        var valid = ValueType switch
        {
            FactValueType.String => v.ValueKind == JsonValueKind.String,
            FactValueType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FactValueType.Int64 => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            FactValueType.UInt64 => v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out _),
            FactValueType.Decimal => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _),
            FactValueType.StringArray => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind is JsonValueKind.String or JsonValueKind.Null),
            FactValueType.Int64Array => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out _)),
            FactValueType.UInt64Array => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetUInt64(out _)),
            _ => false
        };
        if (!valid) throw new InvalidDataException("A source field does not match its declared value type.");
    }
}

public sealed record WinPoolSource(
    string Id, FactOrigin Origin, string Namespace, string ClassName,
    DateTimeOffset CapturedAt, CollectionPurpose Purpose,
    FieldReadState ReadState = FieldReadState.Returned, string? ReasonCode = null);

/// <summary>Identity contains only an opaque source key; display names and disk numbers are never identity inputs.</summary>
public sealed record WinPoolSourceObject(
    string Id, FactObjectType ObjectType, string SourceRef, string SourceIdentity,
    bool HasReliableIdentity, ImmutableArray<WinPoolSourceField> Fields)
{
    public WinPoolSourceField? Field(string name) =>
        Fields.FirstOrDefault(x => x.Name.Equals(name, StringComparison.Ordinal));
}

public sealed record WinPoolFactRelationship(string FromId, string ToId, string Kind);
public sealed record WinPoolIdentityBinding(FactObjectType ObjectType, string SourceIdentity, string ObjectId);
public sealed record WinPoolCollectionState(
    CollectionPurpose Purpose, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    FieldReadState State, string? ReasonCode = null);

/// <summary>Persisted current facts. Application identities survive projection-cache rebuilds.</summary>
public sealed record WinPoolFacts(
    int FormatVersion, SystemId SystemId, long Revision,
    ImmutableArray<WinPoolSource> Sources,
    ImmutableArray<WinPoolSourceObject> Objects,
    ImmutableArray<WinPoolFactRelationship> Relationships,
    ImmutableArray<WinPoolIdentityBinding> Identities,
    ImmutableArray<WinPoolCollectionState> Collections)
{
    public const int CurrentFormatVersion = 1;
    public bool IsSimulation { get; init; }
    public string InventoryVersion { get; init; } = string.Empty;
    public DateTimeOffset InventoryCapturedAt { get; init; }

    public static WinPoolFacts Empty(SystemId systemId) =>
        new(CurrentFormatVersion, systemId, 0, [], [], [], [], []);

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion || SystemId.Value == Guid.Empty || Revision < 0
            || Sources.IsDefault || Objects.IsDefault || Relationships.IsDefault || Identities.IsDefault || Collections.IsDefault)
            throw new InvalidDataException("Unsupported or incomplete facts document.");
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.ClassName)
                || !Enum.IsDefined(source.Origin) || !Enum.IsDefined(source.Purpose)
                || !Enum.IsDefined(source.ReadState) || !sourceIds.Add(source.Id))
                throw new InvalidDataException("Invalid or duplicate source.");
        }
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Objects)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !objectIds.Add(item.Id) || !Enum.IsDefined(item.ObjectType)
                || !sourceIds.Contains(item.SourceRef) || item.Fields.IsDefault
                || (item.HasReliableIdentity && string.IsNullOrWhiteSpace(item.SourceIdentity)))
                throw new InvalidDataException("Invalid source object or identity.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.Fields)
            {
                field.Validate();
                if (!names.Add(field.Name) || !sourceIds.Contains(field.SourceRef))
                    throw new InvalidDataException("Invalid field source or duplicate property.");
            }
        }
        foreach (var relation in Relationships)
            if (!objectIds.Contains(relation.FromId) || !objectIds.Contains(relation.ToId) || string.IsNullOrWhiteSpace(relation.Kind))
                throw new InvalidDataException("Dangling source relationship.");
        var identityKeys = new HashSet<(FactObjectType, string)>();
        foreach (var identity in Identities)
            if (!Enum.IsDefined(identity.ObjectType) || string.IsNullOrWhiteSpace(identity.SourceIdentity)
                || string.IsNullOrWhiteSpace(identity.ObjectId) || !identityKeys.Add((identity.ObjectType, identity.SourceIdentity)))
                throw new InvalidDataException("Invalid or duplicate identity binding.");
        foreach (var collection in Collections)
            if (!Enum.IsDefined(collection.Purpose) || !Enum.IsDefined(collection.State)
                || collection.CompletedAt < collection.StartedAt)
                throw new InvalidDataException("Invalid collection state.");
    }

    public WinPoolFacts CopyTo(SystemId targetSystemId)
    {
        Validate();
        if (targetSystemId.Value == Guid.Empty || targetSystemId == SystemId)
            throw new ArgumentException("A copy requires a different system identity.", nameof(targetSystemId));
        var map = Objects.ToDictionary(x => x.Id, x => WinPoolIdentityRegistry.ScopedId(targetSystemId, x.ObjectType, x.Id));
        return this with
        {
            SystemId = targetSystemId, Revision = 1, IsSimulation = true,
            Objects = Objects.Select(x => x with { Id = map[x.Id] }).ToImmutableArray(),
            Relationships = Relationships.Select(x => x with { FromId = map[x.FromId], ToId = map[x.ToId] }).ToImmutableArray(),
            Identities = Identities.Select(x => x with
            {
                ObjectId = map.GetValueOrDefault(x.ObjectId)
                    ?? WinPoolIdentityRegistry.ScopedId(targetSystemId, x.ObjectType, x.ObjectId)
            }).ToImmutableArray()
        };
    }
}

/// <summary>Scoped, persisted mappings. Unreliable observations never reuse a guessed stable identity.</summary>
public sealed class WinPoolIdentityRegistry
{
    private readonly SystemId systemId;
    private readonly Dictionary<(FactObjectType, string), WinPoolIdentityBinding> bindings;
    public WinPoolIdentityRegistry(SystemId systemId, IEnumerable<WinPoolIdentityBinding>? previous = null)
    {
        if (systemId.Value == Guid.Empty) throw new ArgumentException("System identity is required.", nameof(systemId));
        this.systemId = systemId;
        bindings = (previous ?? []).ToDictionary(x => (x.ObjectType, x.SourceIdentity));
    }

    public string Resolve(FactObjectType type, string? reliableSourceIdentity)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (string.IsNullOrWhiteSpace(reliableSourceIdentity)) return "temporary:" + Guid.NewGuid().ToString("N");
        var key = (type, reliableSourceIdentity);
        if (!bindings.TryGetValue(key, out var binding))
            bindings[key] = binding = new(type, reliableSourceIdentity, ScopedId(systemId, type, reliableSourceIdentity));
        return binding.ObjectId;
    }

    public ImmutableArray<WinPoolIdentityBinding> Snapshot() => bindings.Values
        .OrderBy(x => x.ObjectType).ThenBy(x => x.SourceIdentity, StringComparer.Ordinal).ToImmutableArray();

    public static string OpaqueSourceIdentity(string sourceNamespace, string sourceClass, string reliableIdentity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { sourceNamespace, sourceClass, reliableIdentity })))).ToLowerInvariant();

    internal static string ScopedId(SystemId systemId, FactObjectType type, string identity) =>
        "object:" + OpaqueSourceIdentity(systemId.Value.ToString("N"), type.ToString(), identity);
}

/// <summary>One bounded JSON format; no dynamic CLR types and no old-format fallback.</summary>
public static class WinPoolFactsCodec
{
    public const int MaximumUtf8Bytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32 };
    public static string Encode(WinPoolFacts facts)
    {
        facts.Validate();
        var json = JsonSerializer.Serialize(facts, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes) throw new InvalidDataException("Facts payload exceeds its transport budget.");
        return json;
    }
    public static WinPoolFacts Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes)
            throw new InvalidDataException("Invalid facts payload size.");
        var facts = JsonSerializer.Deserialize<WinPoolFacts>(json, Options)
            ?? throw new InvalidDataException("Missing facts document.");
        facts.Validate();
        return facts;
    }
}
