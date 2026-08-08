using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

internal sealed record NativeStorageManagementResult(
    IReadOnlyList<StorageObjectView> Objects,
    IReadOnlyList<StorageRelationshipView> Relationships,
    string? DiagnosticCode);

/// <summary>
/// Read-only projection of the Windows Storage Management CIM provider.
/// Queries are fixed in source and cannot invoke methods or accept WQL over IPC.
/// </summary>
internal static class NativeStorageManagementInventoryReader
{
    private const string StorageNamespace =
        @"\\.\ROOT\Microsoft\Windows\Storage";

    public static NativeStorageManagementResult Read(
        SystemId systemId,
        StorageObjectId systemObject,
        IReadOnlyDictionary<uint, StorageObjectId> physicalDisks)
    {
        var objects = new List<StorageObjectView>();
        var relationships = new List<StorageRelationshipView>();
        var references = new Dictionary<string, StorageObjectId>(
            StringComparer.OrdinalIgnoreCase);
        string? diagnostic = null;
        try
        {
            var scope = new ManagementScope(StorageNamespace);
            scope.Connect();
            ReadClass(
                scope,
                systemId,
                systemObject,
                StorageObjectKind.StoragePool,
                "MSFT_StoragePool",
                [
                    "ObjectId",
                    "FriendlyName",
                    "HealthStatus",
                    "OperationalStatus",
                    "Size",
                    "AllocatedSize",
                    "IsPrimordial"
                ],
                objects,
                references);
            ReadClass(
                scope,
                systemId,
                systemObject,
                StorageObjectKind.StorageTier,
                "MSFT_StorageTier",
                [
                    "ObjectId",
                    "FriendlyName",
                    "MediaType",
                    "HealthStatus",
                    "OperationalStatus",
                    "Size",
                    "FootprintOnPool",
                    "ResiliencySettingName",
                    "ProvisioningType"
                ],
                objects,
                references);
            ReadClass(
                scope,
                systemId,
                systemObject,
                StorageObjectKind.VirtualDisk,
                "MSFT_VirtualDisk",
                [
                    "ObjectId",
                    "FriendlyName",
                    "HealthStatus",
                    "OperationalStatus",
                    "Size",
                    "FootprintOnPool",
                    "ResiliencySettingName",
                    "ProvisioningType",
                    "NumberOfDataCopies",
                    "PhysicalDiskRedundancy",
                    "Interleave"
                ],
                objects,
                references);
            ReadPhysicalDiskReferences(scope, physicalDisks, references);
            ReadAssociation(
                scope,
                "MSFT_StoragePoolToPhysicalDisk",
                "StoragePool",
                "PhysicalDisk",
                "contains-physical-disk",
                references,
                relationships);
            ReadAssociation(
                scope,
                "MSFT_StoragePoolToVirtualDisk",
                "StoragePool",
                "VirtualDisk",
                "contains-virtual-disk",
                references,
                relationships);
            ReadAssociation(
                scope,
                "MSFT_StoragePoolToStorageTier",
                "StoragePool",
                "StorageTier",
                "contains-storage-tier",
                references,
                relationships);
            ReadAssociation(
                scope,
                "MSFT_VirtualDiskToStorageTier",
                "VirtualDisk",
                "StorageTier",
                "uses-storage-tier",
                references,
                relationships);
        }
        catch (Exception exception) when (
            exception is ManagementException
                or UnauthorizedAccessException
                or COMException)
        {
            diagnostic = "inventory.native.storage_management_unavailable";
        }

        return new(objects, relationships, diagnostic);
    }

    private static void ReadClass(
        ManagementScope scope,
        SystemId systemId,
        StorageObjectId systemObject,
        StorageObjectKind kind,
        string className,
        IReadOnlyList<string> properties,
        ICollection<StorageObjectView> destination,
        IDictionary<string, StorageObjectId> references)
    {
        var projection = string.Join(", ", properties);
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT {projection} FROM {className}"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                var objectId = Text(item, "ObjectId");
                var name = Text(item, "FriendlyName");
                var fallbackIdentity = string.Join(
                    '|',
                    className,
                    name,
                    Text(item, "Size"),
                    Text(item, "FootprintOnPool"));
                var stable = !string.IsNullOrWhiteSpace(objectId);
                var id = new StorageObjectId(
                    systemId,
                    kind,
                    Hash(stable ? objectId : fallbackIdentity));
                if (stable)
                {
                    references[ReferenceKey(className, objectId)] = id;
                }
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var property in properties.Where(value =>
                             value is not ("ObjectId" or "FriendlyName")))
                {
                    values[ToCamelCase(property)] = Text(item, property);
                }

                values["provider"] = "Windows Storage Management CIM";
                destination.Add(
                    new(
                        id,
                        systemObject,
                        string.IsNullOrWhiteSpace(name)
                            ? $"{kind} {id.ProviderKey[..8]}"
                            : name,
                        stable
                            ? IdentityStability.Stable
                            : IdentityStability.Temporary,
                        values));
            }
        }
    }

    private static void ReadPhysicalDiskReferences(
        ManagementScope scope,
        IReadOnlyDictionary<uint, StorageObjectId> physicalDisks,
        IDictionary<string, StorageObjectId> references)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery(
                "SELECT ObjectId, DeviceId FROM MSFT_PhysicalDisk"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                if (uint.TryParse(
                        Text(item, "DeviceId"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var number)
                    && physicalDisks.TryGetValue(number, out var id)
                    && Text(item, "ObjectId") is { Length: > 0 } objectId)
                {
                    references[ReferenceKey("MSFT_PhysicalDisk", objectId)] = id;
                }
            }
        }
    }

    private static void ReadAssociation(
        ManagementScope scope,
        string className,
        string fromProperty,
        string toProperty,
        string relationshipKind,
        IReadOnlyDictionary<string, StorageObjectId> references,
        ICollection<StorageRelationshipView> destination)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery(
                $"SELECT {fromProperty}, {toProperty} FROM {className}"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                var from = Reference(item.Properties[fromProperty]?.Value);
                var to = Reference(item.Properties[toProperty]?.Value);
                if (from is null
                    || to is null
                    || !references.TryGetValue(from.Value.Key, out var fromId)
                    || !references.TryGetValue(to.Value.Key, out var toId))
                {
                    continue;
                }

                destination.Add(
                    new(
                        fromId,
                        toId,
                        relationshipKind));
            }
        }
    }

    private static (string Key, string ClassName)? Reference(object? value)
    {
        if (value is not ManagementBaseObject reference)
        {
            return null;
        }

        var className = reference.ClassPath?.ClassName;
        var objectId = Text(reference, "ObjectId");
        return string.IsNullOrWhiteSpace(className)
               || string.IsNullOrWhiteSpace(objectId)
            ? null
            : (ReferenceKey(className, objectId), className);
    }

    private static string Text(ManagementBaseObject item, string property)
    {
        var value = item.Properties[property]?.Value;
        return value switch
        {
            null => string.Empty,
            Array array => string.Join(
                ',',
                array.Cast<object?>().Select(ToInvariant)),
            _ => ToInvariant(value)
        };
    }

    private static string ToInvariant(object? value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    private static string ToCamelCase(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())))
            .ToLowerInvariant();

    private static string ReferenceKey(string className, string objectId) =>
        $"{className}|{objectId}";
}
