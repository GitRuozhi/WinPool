using System.Globalization;

using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsCapabilityMethodResult(
    string Status, uint? ReturnValue,
    IReadOnlyDictionary<string, JsonElement?> Output, string? Error);

public sealed record WindowsVolumeFormatCapability(
    string Status, string VolumeStableId, string? PartitionStableId,
    string? PhysicalDiskStableId, string? UniqueId, string? ObjectId,
    DateTimeOffset ObservedAtUtc, string? PhysicalMemberFingerprint,
    WindowsCapabilityMethodResult? FileSystems,
    WindowsCapabilityMethodResult? ClusterSizes, string? Error)
{
    public string RequestedFileSystem { get; init; } = "ReFS";
}

public sealed record WindowsTierCapability(
    string Status, string PhysicalDiskStableId, string? SubsystemStableId,
    string? UniqueId, string? ObjectId, DateTimeOffset ObservedAtUtc,
    IReadOnlyList<WinPoolFactRelationship> Associations,
    IReadOnlyList<WinPoolSourceField> Fields, string? Error);

/// <summary>
/// Advisory, fixed read-only provider queries. These results are evidence for
/// the exact captured objects, not permission to execute a storage operation.
/// A later candidate volume must be queried again before its plan is prepared.
/// </summary>
public interface IWindowsRealStorageCapabilityReader
{
    Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(
        WindowsRealStorageTopology topology, StorageObjectId volume, CancellationToken cancellationToken);
    Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(
        WindowsRealStorageTopology topology, StorageObjectId volume, string fileSystem,
        CancellationToken cancellationToken) => fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
            ? ReadVolumeFormatAsync(topology, volume, cancellationToken)
            : Task.FromResult(new WindowsVolumeFormatCapability("unknown", volume.ProviderKey,
                null, null, null, null, DateTimeOffset.UtcNow, null, null, null,
                "The capability reader does not implement the requested file-system query.")
                { RequestedFileSystem = fileSystem });
    Task<WindowsTierCapability> ReadTierAsync(
        WindowsRealStorageTopology topology, StorageObjectId physicalDisk, CancellationToken cancellationToken);
    Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(
        WindowsRealStorageTopology topology, StorageObjectId tier, CancellationToken cancellationToken);
}

public sealed class WindowsRealStorageCapabilityReader : IWindowsRealStorageCapabilityReader
{
    private readonly WindowsPowerShellHost host;
    public WindowsRealStorageCapabilityReader(WindowsPowerShellHost? host = null) => this.host = host ?? WindowsPowerShellHost.Shared;
    private static readonly string[] TierBooleanFields =
    [
        "SupportsStoragePoolCreation", "SupportsStorageTierCreation", "SupportsStorageTieredVirtualDiskCreation",
        "SupportsStorageTierDeletion", "SupportsStorageTierFriendlyNameModification"
    ];

    public Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(WindowsRealStorageTopology topology,
        StorageObjectId volume, CancellationToken cancellationToken) => ReadVolumeFormatAsync(topology, volume, "ReFS", cancellationToken);
    public async Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(WindowsRealStorageTopology topology,
        StorageObjectId tier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tier.Kind != StorageObjectKind.StorageTier) throw new InvalidDataException("An exact existing tier template is required.");
        var source = topology.RequireObject(tier);
        var projected = topology.Snapshot.StorageTiers.Single(item => item.StableId == source.Id);
        var pool = topology.Snapshot.StoragePools.Single(item => item.StableId == projected.PoolStableId && !item.IsPrimordial);
        _ = topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, pool.StableId),
            pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray());
        if (projected.VirtualDiskStableId is not null) throw new NotSupportedException("Tier size lookup requires an unused creation template.");
        var json = await host.QueryAsync(WindowsPowerShellQuery.TierSize,
            new { UniqueId = RequiredText(source, "UniqueId"), ObjectId = RequiredText(source, "ObjectId"), projected.ResiliencySettingName }, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json); var range = document.RootElement;
        static object? Number(JsonElement range, string name) => range.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : null;
        var returned = range.GetProperty("ReturnValue").GetUInt32();
        var sizes = range.GetProperty("SupportedSizes").EnumerateArray().Select(value => value.GetUInt64()).ToArray();
        return ParseTierCreationSize(returned, sizes, Number(range,"TierSizeMin"), Number(range,"TierSizeMax"), Number(range,"TierSizeDivisor"));
    }
    internal static VirtualDiskCreationSize ParseTierCreationSize(object? returnValue,
        object? supportedSizes, object? minimumValue, object? maximumValue, object? divisorValue)
    {
        // MSFT_StorageTier.GetSupportedSize explicitly permits either a complete
        // enumeration or a min/increment/max sequence. Pool divisor semantics
        // are different and remain unchanged in the pool reader.
        // https://learn.microsoft.com/windows-hardware/drivers/storage/msft-storagetier-getsupportedsize
        if (returnValue is not uint code || code != 0)
            throw new NotSupportedException("The exact HDD tier template size method did not return success.");
        // CLR primitive-array casts can treat Int64[] as UInt64[]. The exact
        // runtime type, rather than an assignability test, proves CIM's type.
        if (supportedSizes is not null && supportedSizes.GetType() != typeof(ulong[]))
            throw new InvalidDataException("The tier template supported-size enumeration has an invalid UInt64 array type.");
        var sizes = supportedSizes is ulong[] values
            ? values.Select(value => TierSizeValue(value)).ToArray() : [];
        var minimum = minimumValue is null ? 0 : TierSizeValue(minimumValue);
        var maximum = maximumValue is null ? 0 : TierSizeValue(maximumValue);
        var divisor = divisorValue is null ? 0 : TierSizeValue(divisorValue);
        if (sizes.Any(value => value <= 0) || sizes.Distinct().Count() != sizes.Length)
            throw new InvalidDataException("The tier template supported-size enumeration has zero or duplicate sizes.");
        if (sizes.Length == 0 && (minimum <= 0 || maximum < minimum || divisor <= 0))
            throw new InvalidDataException("The exact tier template returned an incomplete creation size range.");
        if (sizes.Length > 0 && minimum > 0 && maximum > 0 && divisor > 0
            && (maximum < minimum || sizes.Any(size => size < minimum || size > maximum
                || (size - minimum) % divisor != 0)))
            throw new InvalidDataException("The tier template's enumeration disagrees with its complete size range.");
        // A nonempty complete enumeration is authoritative even when the
        // optional range outputs are unspecified. No unlisted size is admitted.
        return new VirtualDiskCreationSize(minimum, maximum, divisor, sizes)
        { RangeOriginBytes = sizes.Length == 0 ? minimum : 0 };
    }

    private static long TierSizeValue(object value) => value is ulong number && number <= long.MaxValue
        ? (long)number : throw new InvalidDataException("The tier template returned an invalid or overflowing UInt64 size.");

    public async Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(
        WindowsRealStorageTopology topology, StorageObjectId volume, string fileSystem,
        CancellationToken cancellationToken)
    {
        string? partitionId = null, physicalId = null, uniqueId = null,
            objectId = null, fingerprint = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (volume.Kind != StorageObjectKind.Volume)
                throw new InvalidDataException("An exact captured volume is required.");
            var source = topology.RequireObject(volume);
            var closure = topology.RequireCapabilityClosure(volume);
            physicalId = closure.PhysicalDiskId;
            fingerprint = closure.PhysicalMemberFingerprint;
            var projected = topology.Snapshot.Volumes.Single(item =>
                StringComparer.Ordinal.Equals(item.StableId, volume.ProviderKey));
            partitionId = projected.PartitionStableId;
            var partition = topology.Snapshot.Partitions.Single(item =>
                StringComparer.Ordinal.Equals(item.StableId, partitionId));
            if (!Guid.TryParse(partition.Guid, out var expectedGuid) || expectedGuid == Guid.Empty)
                throw new InvalidDataException("The captured volume lacks an exact parent partition GUID.");
            if (!topology.Facts.Relationships.Any(item => !item.IsRetained
                && item.Kind == "partition-volume" && item.FromId == partitionId
                && item.ToId == source.Id))
                throw new InvalidDataException("The captured partition-volume association is unavailable.");
            uniqueId = RequiredText(source, "UniqueId");
            objectId = RequiredText(source, "ObjectId");

            var json = await host.QueryAsync(WindowsPowerShellQuery.VolumeFormat,
                new { UniqueId = uniqueId, ObjectId = objectId, PartitionGuid = partition.Guid, partition.DiskNumber,
                    partition.Offset, partition.Size, FileSystem = fileSystem }, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var fileSystems = document.RootElement.GetProperty("FileSystems").Deserialize<WindowsCapabilityMethodResult>(WindowsPowerShellHost.JsonOptions);
            var clusterSizes = document.RootElement.GetProperty("ClusterSizes").Deserialize<WindowsCapabilityMethodResult>(WindowsPowerShellHost.JsonOptions);
            return new("queried", source.Id, partitionId, physicalId, uniqueId, objectId, DateTimeOffset.UtcNow,
                fingerprint, fileSystems, clusterSizes, null) { RequestedFileSystem = fileSystem };
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            return new("unknown", volume.ProviderKey, partitionId, physicalId,
                uniqueId, objectId, DateTimeOffset.UtcNow, fingerprint, null, null,
                Error(exception)) { RequestedFileSystem = fileSystem };
        }
    }

    public async Task<WindowsTierCapability> ReadTierAsync(
        WindowsRealStorageTopology topology, StorageObjectId physicalDisk,
        CancellationToken cancellationToken)
    {
        string? subsystemId = null, uniqueId = null, objectId = null;
        var associations = new List<WinPoolFactRelationship>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (physicalDisk.Kind != StorageObjectKind.PhysicalDisk)
                throw new InvalidDataException("An exact captured physical disk is required.");
            _ = topology.RequireObject(physicalDisk);
            var concretePool = topology.Snapshot.StoragePools.SingleOrDefault(item => !item.IsPrimordial && item.MemberPhysicalDiskIds.Contains(physicalDisk.ProviderKey));
            if (concretePool is null) _ = topology.RequireSinglePhysicalClosure([physicalDisk]);
            else _ = topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, concretePool.StableId),
                concretePool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray());
            var members = topology.Facts.Relationships.Where(item => !item.IsRetained
                && item.Kind == "pool-member" && item.ToId == physicalDisk.ProviderKey).ToArray();
            if (members.Length == 0)
                throw new InvalidDataException("No current pool-member association identifies the physical disk's subsystem.");
            foreach (var member in members)
            {
                var parents = topology.Facts.Relationships.Where(item => !item.IsRetained
                    && item.Kind == "subsystem-pool" && item.ToId == member.FromId).ToArray();
                if (parents.Length != 1)
                    throw new InvalidDataException("A related pool has an absent or ambiguous subsystem association: " + member.FromId);
                associations.Add(member);
                associations.Add(parents[0]);
            }
            var subsystemIds = associations.Where(item => item.Kind == "subsystem-pool")
                .Select(item => item.FromId).Distinct(StringComparer.Ordinal).ToArray();
            if (subsystemIds.Length != 1)
                throw new InvalidDataException("The physical disk maps to more than one storage subsystem.");
            subsystemId = subsystemIds[0];
            var source = topology.Facts.Objects.Single(item => item.Id == subsystemId
                && item.ObjectType == FactObjectType.StorageSubsystem && item.HasReliableIdentity);
            var sourceState = topology.Facts.Sources.Single(item => item.Id == source.SourceRef);
            if (sourceState.ClassName != "MSFT_StorageSubSystem"
                || sourceState.ReadState != FieldReadState.Returned)
                throw new InvalidDataException("The exact subsystem source query is incomplete.");
            uniqueId = RequiredText(source, "UniqueId");
            objectId = RequiredText(source, "ObjectId");
            var json = await host.QueryAsync(WindowsPowerShellQuery.TierCapability,
                new { UniqueId = uniqueId, ObjectId = objectId }, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var fields = document.RootElement.GetProperty("Fields").EnumerateArray().Select(field => ReadField(field, source.Id)).ToArray();
            return new("queried", physicalDisk.ProviderKey, subsystemId, uniqueId, objectId,
                DateTimeOffset.UtcNow, associations, fields, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            return new("unknown", physicalDisk.ProviderKey, subsystemId, uniqueId,
                objectId, DateTimeOffset.UtcNow, associations, [], Error(exception));
        }
    }

    private static WinPoolSourceField ReadField(JsonElement field, string sourceRef)
    {
        var name = field.GetProperty("Name").GetString()!;
        var type = name == "PhysicalDisksPerStoragePoolMin" ? FactValueType.UInt64 : FactValueType.Boolean;
        if (!field.GetProperty("Present").GetBoolean())
            return WinPoolSourceField.Missing(name, type, sourceRef, FieldReadState.Unavailable, "Provider property is absent.");
        var value = field.GetProperty("Value");
        if (value.ValueKind == JsonValueKind.Null) return WinPoolSourceField.Returned<object?>(name, null, type, sourceRef);
        if (type == FactValueType.Boolean && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return WinPoolSourceField.Returned(name, value.GetBoolean(), type, sourceRef);
        if (type == FactValueType.UInt64 && value.TryGetUInt64(out var number))
            return WinPoolSourceField.Returned(name, number, type, sourceRef);
        return WinPoolSourceField.Missing(name, type, sourceRef, FieldReadState.Failed, "Provider property has an unexpected value type.");
    }
    private static string RequiredText(WinPoolSourceObject source, string name) =>
        source.Field(name) is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
        && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidDataException("A captured exact identity field is unavailable: " + name);

    private static string Error(Exception exception) =>
        exception.GetType().FullName + ": " + exception.Message;
}
