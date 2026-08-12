using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public enum PersistedSystemKind
{
    Local = 0,
    Simulation = 1,
    Imported = 2,
    Replay = 3
}

public sealed record PersistedInventorySnapshot(
    Guid SnapshotId,
    InventorySnapshot Snapshot);

/// <summary>
/// Persists only a defense-in-depth sanitized projection. Provider keys are
/// deterministically hashed, sensitive property keys are omitted, and free-form
/// identity diagnostics are not stored.
/// </summary>
public sealed class InventorySnapshotRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        InventoryPersistenceJson.Options;

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public InventorySnapshotRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public InventorySnapshotRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<PersistedInventorySnapshot> SaveAsync(
        InventorySnapshot snapshot,
        PersistedSystemKind systemKind,
        string displayName,
        CancellationToken cancellationToken = default,
        string? canonicalLocalSystemBinding = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (snapshot.SystemId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshot.InventoryVersion)
            || string.IsNullOrWhiteSpace(snapshot.MachineBinding))
        {
            throw new ArgumentException("采集快照身份不完整。", nameof(snapshot));
        }

        AssertWriteOwnership();
        var sanitized = InventoryPersistenceSanitizer.Sanitize(snapshot);
        var persistedMachineBinding = systemKind == PersistedSystemKind.Local
            ? !string.IsNullOrWhiteSpace(canonicalLocalSystemBinding)
                ? canonicalLocalSystemBinding
                : sanitized.MachineBinding
            : sanitized.MachineBinding;
        var snapshotId = Guid.NewGuid();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var systemCommand = connection.CreateCommand())
        {
            systemCommand.Transaction = transaction;
            systemCommand.CommandText = """
                INSERT INTO systems(
                    system_id, kind, display_name, machine_binding_hash,
                    created_at_utc_ms)
                VALUES($system, $kind, $name, $binding, $created)
                ON CONFLICT(system_id) DO UPDATE SET
                    kind = excluded.kind,
                    display_name = excluded.display_name,
                    machine_binding_hash = excluded.machine_binding_hash;
                """;
            systemCommand.Parameters.AddWithValue("$system", Id(snapshot.SystemId.Value));
            systemCommand.Parameters.AddWithValue("$kind", (int)systemKind);
            systemCommand.Parameters.AddWithValue("$name", displayName.Trim());
            systemCommand.Parameters.AddWithValue("$binding", persistedMachineBinding);
            systemCommand.Parameters.AddWithValue(
                "$created",
                sanitized.CapturedAtUtc.ToUnixTimeMilliseconds());
            await systemCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var snapshotCommand = connection.CreateCommand())
        {
            snapshotCommand.Transaction = transaction;
            snapshotCommand.CommandText = """
                INSERT INTO inventory_snapshots(
                    snapshot_id, system_id, inventory_version,
                    captured_at_utc_ms, provider_kind, sanitized_json)
                VALUES($snapshot, $system, $version, $captured, $provider, $json);
                """;
            snapshotCommand.Parameters.AddWithValue("$snapshot", Id(snapshotId));
            snapshotCommand.Parameters.AddWithValue("$system", Id(snapshot.SystemId.Value));
            snapshotCommand.Parameters.AddWithValue("$version", sanitized.InventoryVersion);
            snapshotCommand.Parameters.AddWithValue(
                "$captured",
                sanitized.CapturedAtUtc.ToUnixTimeMilliseconds());
            snapshotCommand.Parameters.AddWithValue(
                "$provider",
                (int)sanitized.ProviderKind);
            snapshotCommand.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(sanitized, JsonOptions));
            await snapshotCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var persistedIds = sanitized.Objects.ToDictionary(
            item => item.Id,
            item => ObjectId(item.Id));
        await using (var objectCommand = connection.CreateCommand())
        {
            objectCommand.Transaction = transaction;
            objectCommand.CommandText = """
                INSERT INTO storage_objects(
                    snapshot_id, object_id, object_kind,
                    provider_key_hash, sanitized_json)
                VALUES($snapshot, $object, $kind, $provider, $json);
                """;
            var snapshotParameter =
                objectCommand.Parameters.Add("$snapshot", SqliteType.Text);
            var objectParameter =
                objectCommand.Parameters.Add("$object", SqliteType.Text);
            var kindParameter =
                objectCommand.Parameters.Add("$kind", SqliteType.Integer);
            var providerParameter =
                objectCommand.Parameters.Add("$provider", SqliteType.Text);
            var jsonParameter =
                objectCommand.Parameters.Add("$json", SqliteType.Text);
            objectCommand.Prepare();
            foreach (var item in sanitized.Objects)
            {
                snapshotParameter.Value = Id(snapshotId);
                objectParameter.Value = persistedIds[item.Id];
                kindParameter.Value = (int)item.Id.Kind;
                providerParameter.Value = item.Id.ProviderKey;
                jsonParameter.Value = JsonSerializer.Serialize(item, JsonOptions);
                await objectCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var relationshipCommand = connection.CreateCommand())
        {
            relationshipCommand.Transaction = transaction;
            relationshipCommand.CommandText = """
                INSERT INTO storage_relationships(
                    snapshot_id, from_object_id, to_object_id, relationship_kind)
                VALUES($snapshot, $from, $to, $kind);
                """;
            var snapshotParameter =
                relationshipCommand.Parameters.Add("$snapshot", SqliteType.Text);
            var fromParameter =
                relationshipCommand.Parameters.Add("$from", SqliteType.Text);
            var toParameter =
                relationshipCommand.Parameters.Add("$to", SqliteType.Text);
            var kindParameter =
                relationshipCommand.Parameters.Add("$kind", SqliteType.Text);
            relationshipCommand.Prepare();
            foreach (var relationship in sanitized.Relationships ?? [])
            {
                snapshotParameter.Value = Id(snapshotId);
                fromParameter.Value = persistedIds[relationship.FromObjectId];
                toParameter.Value = persistedIds[relationship.ToObjectId];
                kindParameter.Value = relationship.RelationshipKind;
                await relationshipCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PersistedInventorySnapshot(snapshotId, sanitized);
    }

    public async Task<PersistedInventorySnapshot?> GetAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sanitized_json
            FROM inventory_snapshots
            WHERE snapshot_id = $snapshot;
            """;
        command.Parameters.AddWithValue("$snapshot", Id(snapshotId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json)
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<InventorySnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("持久化采集快照为空。");
        return new PersistedInventorySnapshot(snapshotId, snapshot);
    }

    public async Task<IReadOnlyList<PersistedInventorySnapshot>> ListAsync(
        SystemId systemId,
        int take,
        DateTimeOffset? beforeCapturedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, sanitized_json
            FROM inventory_snapshots
            WHERE system_id = $system
              AND ($before IS NULL OR captured_at_utc_ms < $before)
            ORDER BY captured_at_utc_ms DESC, snapshot_id DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$system", Id(systemId.Value));
        command.Parameters.AddWithValue(
            "$before",
            beforeCapturedAtUtc is null
                ? DBNull.Value
                : beforeCapturedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$take", take);
        var results = new List<PersistedInventorySnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = JsonSerializer.Deserialize<InventorySnapshot>(
                reader.GetString(1),
                JsonOptions)
                ?? throw new InvalidDataException("持久化采集快照为空。");
            results.Add(
                new PersistedInventorySnapshot(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    snapshot));
        }

        return results;
    }

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }

    internal static string Id(Guid value) => value.ToString("N");

    private static string ObjectId(StorageObjectId id) =>
        $"{(int)id.Kind}:{id.ProviderKey}";
}

public sealed record PersistedInventoryComparison(
    Guid ComparisonId,
    Guid ReferenceSnapshotId,
    Guid CandidateSnapshotId,
    InventoryComparison Comparison,
    DateTimeOffset CreatedAtUtc);

public sealed class InventoryComparisonRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        InventoryPersistenceJson.Options;

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public InventoryComparisonRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public InventoryComparisonRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<PersistedInventoryComparison> SaveAsync(
        Guid referenceSnapshotId,
        Guid candidateSnapshotId,
        InventoryComparison comparison,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (referenceSnapshotId == Guid.Empty
            || candidateSnapshotId == Guid.Empty
            || referenceSnapshotId == candidateSnapshotId)
        {
            throw new ArgumentException("对照快照身份无效。");
        }

        AssertWriteOwnership();
        var sanitized = InventoryPersistenceSanitizer.Sanitize(comparison);
        var comparisonId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO inventory_comparisons(
                comparison_id, reference_snapshot_id, candidate_snapshot_id,
                sanitized_json, created_at_utc_ms)
            VALUES($comparison, $reference, $candidate, $json, $created);
            """;
        command.Parameters.AddWithValue("$comparison", InventorySnapshotRepository.Id(comparisonId));
        command.Parameters.AddWithValue("$reference", InventorySnapshotRepository.Id(referenceSnapshotId));
        command.Parameters.AddWithValue("$candidate", InventorySnapshotRepository.Id(candidateSnapshotId));
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(sanitized, JsonOptions));
        command.Parameters.AddWithValue("$created", createdAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new(
            comparisonId,
            referenceSnapshotId,
            candidateSnapshotId,
            sanitized,
            createdAt);
    }

    public async Task<PersistedInventoryComparison?> GetAsync(
        Guid comparisonId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reference_snapshot_id, candidate_snapshot_id,
                   sanitized_json, created_at_utc_ms
            FROM inventory_comparisons
            WHERE comparison_id = $comparison;
            """;
        command.Parameters.AddWithValue(
            "$comparison",
            InventorySnapshotRepository.Id(comparisonId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var comparison = JsonSerializer.Deserialize<InventoryComparison>(
            reader.GetString(2),
            JsonOptions)
            ?? throw new InvalidDataException("持久化采集对照为空。");
        return new(
            comparisonId,
            Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"),
            comparison,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }
}

internal static class InventoryPersistenceSanitizer
{
    private static readonly string[] SensitivePropertyFragments =
    [
        "serial",
        "guid",
        "pnp",
        "mac",
        "hardwareid",
        "deviceid",
        "uniqueid"
    ];

    public static InventorySnapshot Sanitize(InventorySnapshot snapshot)
    {
        var idMap = snapshot.Objects
            .Select(item => item.Id)
            .Distinct()
            .ToDictionary(id => id, SanitizeId);
        StorageObjectId Map(StorageObjectId id) =>
            idMap.TryGetValue(id, out var mapped) ? mapped : SanitizeId(id);

        var objects = snapshot.Objects
            .Select(item =>
                item with
                {
                    Id = Map(item.Id),
                    ParentId = item.ParentId is { } parent ? Map(parent) : null,
                    Properties = item.Properties
                        .Where(pair => !IsSensitive(pair.Key))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal)
                })
            .ToArray();
        var diagnostics = snapshot.IdentityDiagnostics
            .Select(item =>
                item with
                {
                    ObjectId = Map(item.ObjectId),
                    DiagnosticText = string.Empty
                })
            .ToArray();
        var relationships = (snapshot.Relationships ?? [])
            .Select(item =>
                item with
                {
                    FromObjectId = Map(item.FromObjectId),
                    ToObjectId = Map(item.ToObjectId)
                })
            .ToArray();
        return snapshot with
        {
            Objects = objects,
            IdentityDiagnostics = diagnostics,
            Relationships = relationships
        };
    }

    public static InventoryComparison Sanitize(InventoryComparison comparison) =>
        comparison with
        {
            Differences = comparison.Differences
                .Select(item =>
                    IsSensitive(item.PropertyKey)
                        ? item with
                        {
                            ReferenceValue = "[redacted]",
                            CandidateValue = "[redacted]"
                        }
                        : item)
                .ToArray()
        };

    private static StorageObjectId SanitizeId(StorageObjectId id) =>
        new(id.System, id.Kind, Hash(id.ProviderKey));

    private static bool IsSensitive(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return SensitivePropertyFragments.Any(
            fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.IsNullOrWhiteSpace(value)
                            ? "<empty>"
                            : value.Trim().ToUpperInvariant())))
            .ToLowerInvariant();
}

internal static class InventoryPersistenceJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StorageObjectIdJsonConverter());
        return options;
    }

    private sealed class StorageObjectIdJsonConverter
        : JsonConverter<StorageObjectId>
    {
        public override StorageObjectId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("StorageObjectId must be a JSON object.");
            }

            Guid systemId = Guid.Empty;
            StorageObjectKind? kind = null;
            string? providerKey = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("StorageObjectId contains an invalid token.");
                }

                var property = reader.GetString();
                reader.Read();
                switch (property)
                {
                    case "system":
                        systemId = ReadSystemId(ref reader);
                        break;
                    case "kind":
                        kind = (StorageObjectKind)reader.GetInt32();
                        break;
                    case "providerKey":
                        providerKey = reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (systemId == Guid.Empty
                || kind is null
                || string.IsNullOrWhiteSpace(providerKey))
            {
                throw new JsonException("StorageObjectId is incomplete.");
            }

            return new(new SystemId(systemId), kind.Value, providerKey);
        }

        public override void Write(
            Utf8JsonWriter writer,
            StorageObjectId value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("system");
            writer.WriteStartObject();
            writer.WriteString("value", value.System.Value);
            writer.WriteEndObject();
            writer.WriteNumber("kind", (int)value.Kind);
            writer.WriteString("providerKey", value.ProviderKey);
            writer.WriteEndObject();
        }

        private static Guid ReadSystemId(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("StorageObjectId.system must be an object.");
            }

            Guid value = Guid.Empty;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("SystemId contains an invalid token.");
                }

                var property = reader.GetString();
                reader.Read();
                if (property == "value")
                {
                    value = reader.GetGuid();
                }
                else
                {
                    reader.Skip();
                }
            }

            return value;
        }
    }
}
