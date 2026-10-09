using System.Globalization;
using System.Management;
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
    WindowsCapabilityMethodResult? ReFsClusterSizes, string? Error);

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
    Task<WindowsTierCapability> ReadTierAsync(
        WindowsRealStorageTopology topology, StorageObjectId physicalDisk, CancellationToken cancellationToken);
    Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(
        WindowsRealStorageTopology topology, StorageObjectId tier, CancellationToken cancellationToken);
}

public sealed class WindowsRealStorageCapabilityReader : IWindowsRealStorageCapabilityReader
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private static readonly string[] TierBooleanFields =
    [
        "SupportsStoragePoolCreation", "SupportsStorageTierCreation", "SupportsStorageTieredVirtualDiskCreation",
        "SupportsStorageTierDeletion", "SupportsStorageTierFriendlyNameModification"
    ];

    public Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(
        WindowsRealStorageTopology topology, StorageObjectId volume,
        CancellationToken cancellationToken) => Task.Run(() =>
            ReadVolumeFormat(topology, volume, cancellationToken), cancellationToken);

    public Task<WindowsTierCapability> ReadTierAsync(
        WindowsRealStorageTopology topology, StorageObjectId physicalDisk,
        CancellationToken cancellationToken) => Task.Run(() =>
            ReadTier(topology, physicalDisk, cancellationToken), cancellationToken);

    public Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(
        WindowsRealStorageTopology topology, StorageObjectId tier,
        CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tier.Kind != StorageObjectKind.StorageTier)
                throw new InvalidDataException("An exact existing tier template is required.");
            var source = topology.RequireObject(tier);
            var projected = topology.Snapshot.StorageTiers.Single(item => item.StableId == source.Id);
            var pool = topology.Snapshot.StoragePools.Single(item => item.StableId == projected.PoolStableId && !item.IsPrimordial);
            if (pool.MemberPhysicalDiskIds.Count == 1) _ = topology.RequireSinglePhysicalClosure([tier]);
            else _ = topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, pool.StableId),
                pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(topology.SystemId, StorageObjectKind.PhysicalDisk, id)).ToArray());
            if (projected.VirtualDiskStableId is not null)
                throw new NotSupportedException("Tier size lookup is for an unused creation template, not existing-object expansion.");
            using var searcher = Search("SELECT * FROM MSFT_StorageTier");
            using var results = searcher.Get();
            var candidates = results.Cast<ManagementObject>().ToArray();
            try
            {
                var exact = RequireExact(candidates, RequiredText(source, "UniqueId"), RequiredText(source, "ObjectId"));
                using var input = exact.GetMethodParameters("GetSupportedSize");
                input["ResiliencySettingName"] = "Simple";
                using var output = exact.InvokeMethod("GetSupportedSize", input,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
                if (output is null)
                    throw new NotSupportedException("The exact HDD tier template did not return a supported Simple creation range.");
                return ParseTierCreationSize(output["ReturnValue"], output["SupportedSizes"],
                    output["TierSizeMin"], output["TierSizeMax"], output["TierSizeDivisor"]);
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }
        }, cancellationToken);

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

    private static WindowsVolumeFormatCapability ReadVolumeFormat(
        WindowsRealStorageTopology topology, StorageObjectId volume,
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
            var closure = topology.RequireSinglePhysicalClosure([volume]);
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

            using var searcher = Search("SELECT * FROM MSFT_Volume");
            using var results = searcher.Get();
            var candidates = results.Cast<ManagementObject>().ToArray();
            try
            {
                var exact = RequireExact(candidates, uniqueId, objectId);
                using var parents = exact.GetRelated("MSFT_Partition");
                var parentCandidates = parents.Cast<ManagementObject>().ToArray();
                try
                {
                    if (parentCandidates.Length != 1
                        || !Guid.TryParse(parentCandidates[0]["Guid"]?.ToString(), out var currentGuid)
                        || currentGuid != expectedGuid
                        || Convert.ToInt64(parentCandidates[0]["DiskNumber"], CultureInfo.InvariantCulture) != partition.DiskNumber
                        || Convert.ToInt64(parentCandidates[0]["Offset"], CultureInfo.InvariantCulture) != partition.Offset
                        || Convert.ToInt64(parentCandidates[0]["Size"], CultureInfo.InvariantCulture) != partition.Size)
                        throw new InvalidDataException("The live volume-parent association or geometry changed.");
                }
                finally
                {
                    foreach (var parent in parentCandidates) parent.Dispose();
                }
                var fileSystems = ReadMethod(exact, "GetSupportedFileSystems", cancellationToken);
                var clusterSizes = ReadMethod(exact, "GetSupportedClusterSizes", cancellationToken);
                return new("queried", source.Id, partitionId, physicalId, uniqueId,
                    objectId, DateTimeOffset.UtcNow, fingerprint, fileSystems, clusterSizes, null);
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            return new("unknown", volume.ProviderKey, partitionId, physicalId,
                uniqueId, objectId, DateTimeOffset.UtcNow, fingerprint, null, null,
                Error(exception));
        }
    }

    private static WindowsTierCapability ReadTier(
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
            _ = topology.RequireSinglePhysicalClosure([physicalDisk]);
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
            using var searcher = Search("SELECT * FROM MSFT_StorageSubSystem");
            using var results = searcher.Get();
            var candidates = results.Cast<ManagementObject>().ToArray();
            try
            {
                var exact = RequireExact(candidates, uniqueId, objectId);
                var fields = TierBooleanFields.Select(name => ReadField(exact, name,
                        FactValueType.Boolean, source.Id)).ToList();
                fields.Add(ReadField(exact, "PhysicalDisksPerStoragePoolMin",
                    FactValueType.UInt64, source.Id));
                return new("queried", physicalDisk.ProviderKey, subsystemId,
                    uniqueId, objectId, DateTimeOffset.UtcNow, associations, fields, null);
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            return new("unknown", physicalDisk.ProviderKey, subsystemId, uniqueId,
                objectId, DateTimeOffset.UtcNow, associations, [], Error(exception));
        }
    }

    private static WindowsCapabilityMethodResult ReadMethod(
        ManagementObject volume, string name, CancellationToken cancellationToken)
    {
        var raw = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        uint? returnValue = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = name == "GetSupportedClusterSizes"
                ? volume.GetMethodParameters(name) : null;
            if (input is not null) input["FileSystem"] = "ReFS";
            using var output = volume.InvokeMethod(name, input,
                new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
            if (output is null)
                throw new InvalidDataException("The provider returned no method result.");
            foreach (PropertyData property in output.Properties)
                raw[property.Name] = JsonSerializer.SerializeToElement(property.Value);
            if (output["ReturnValue"] is not null)
                returnValue = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
            return new(returnValue == 0 ? "returned" : "provider_error",
                returnValue, raw, returnValue is null ? "ReturnValue was not returned." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            return new("unknown", returnValue, raw, Error(exception));
        }
    }

    private static WinPoolSourceField ReadField(ManagementObject source,
        string name, FactValueType type, string sourceRef)
    {
        try
        {
            var property = source.Properties.Cast<PropertyData>().SingleOrDefault(item => item.Name == name);
            if (property is null)
                return WinPoolSourceField.Missing(name, type, sourceRef,
                    FieldReadState.Unavailable, "Provider property is absent.");
            if (property.Value is null)
                return WinPoolSourceField.Returned<object?>(name, null, type, sourceRef);
            if (type == FactValueType.Boolean && property.Value is bool boolean)
                return WinPoolSourceField.Returned(name, boolean, type, sourceRef);
            if (type == FactValueType.UInt64 && property.Value is ushort or uint or ulong)
                return WinPoolSourceField.Returned(name,
                    Convert.ToUInt64(property.Value, CultureInfo.InvariantCulture), type, sourceRef);
            return WinPoolSourceField.Missing(name, type, sourceRef,
                FieldReadState.Failed, "Provider property has an unexpected value type.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WinPoolSourceField.Missing(name, type, sourceRef,
                FieldReadState.Failed, Error(exception));
        }
    }

    private static ManagementObjectSearcher Search(string fixedQuery)
    {
        var searcher = new ManagementObjectSearcher(StorageNamespace, fixedQuery);
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        return searcher;
    }

    private static ManagementObject RequireExact(IEnumerable<ManagementObject> candidates,
        string uniqueId, string objectId)
    {
        var matches = candidates.Where(item =>
            StringComparer.Ordinal.Equals(item["UniqueId"]?.ToString(), uniqueId)
            && StringComparer.Ordinal.Equals(item["ObjectId"]?.ToString(), objectId)).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidDataException("The provider object is absent or ambiguous for its exact UniqueId/ObjectId.");
    }

    private static string RequiredText(WinPoolSourceObject source, string name) =>
        source.Field(name) is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: JsonValueKind.String } value }
        && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidDataException("A captured exact identity field is unavailable: " + name);

    private static string Error(Exception exception) =>
        exception.GetType().FullName + ": " + exception.Message;
}
