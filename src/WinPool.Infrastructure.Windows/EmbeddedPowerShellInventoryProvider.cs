using System.Globalization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Compatibility provider for the fixed embedded read-only PowerShell collector.
/// No caller-supplied script or command text crosses this boundary.
/// </summary>
public sealed class EmbeddedPowerShellInventoryProvider : IInventoryProvider
{
    private readonly IHardwareInventoryProvider provider;

    public EmbeddedPowerShellInventoryProvider(
        IHardwareInventoryProvider? provider = null)
    {
        this.provider = provider ?? new WindowsHardwareInventoryProvider();
    }

    public InventoryProviderKind Kind =>
        InventoryProviderKind.EmbeddedReadOnlyPowerShell;

    public async Task<ApplicationResult<InventorySnapshot>> CaptureAsync(
        InventoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlation = CorrelationId.New();
        if (request.SystemId.Value == Guid.Empty)
        {
            return Failure(
                ApplicationStatus.Rejected,
                correlation,
                "inventory.request.missing_system");
        }

        try
        {
            var document = await provider.CollectLocalAsync(cancellationToken);
            var snapshot = Project(
                request.SystemId,
                document.Snapshot,
                request.IncludeSensitiveValuesInMemory);
            if (!string.IsNullOrWhiteSpace(request.ExpectedInventoryVersion)
                && !string.Equals(
                    request.ExpectedInventoryVersion,
                    snapshot.InventoryVersion,
                    StringComparison.Ordinal))
            {
                return new(
                    ApplicationStatus.Rejected,
                    snapshot,
                    [Message("inventory.version.changed")],
                    correlation);
            }

            return ApplicationResult<InventorySnapshot>.Succeeded(
                snapshot,
                correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ApplicationStatus.Cancelled,
                correlation,
                "inventory.capture.cancelled");
        }
        catch (Exception exception) when (
            exception is InventoryScanException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return Failure(
                ApplicationStatus.Failed,
                correlation,
                "inventory.capture.failed");
        }
    }

    public static InventorySnapshot Project(
        SystemId systemId,
        StorageSnapshot source,
        bool includeSensitiveValuesInMemory)
    {
        ArgumentNullException.ThrowIfNull(source);
        var objects = new List<StorageObjectView>();
        var diagnostics = new List<InventoryIdentityDiagnostic>();
        var identities = new Dictionary<string, StorageObjectId>(
            StringComparer.OrdinalIgnoreCase);
        var systemObject = Add(
            StorageObjectKind.System,
            source.Computer.StableId,
            source.Computer.Name,
            stable: true,
            parent: null,
            Properties(
                ("windowsProductName", source.Computer.WindowsProductName),
                ("windowsVersion", source.Computer.WindowsVersion),
                ("osBuild", source.Computer.OsBuild),
                ("lastBootTime", source.Computer.LastBootTime.ToString("O"))));

        foreach (var item in source.StorageSubsystems)
        {
            Add(
                StorageObjectKind.StorageSubsystem,
                item.StableId,
                item.FriendlyName,
                true,
                systemObject,
                Properties(
                    ("healthStatus", item.HealthStatus),
                    ("operationalStatus", item.OperationalStatus)));
        }

        foreach (var item in source.StoragePools)
        {
            Add(
                StorageObjectKind.StoragePool,
                item.StableId,
                item.FriendlyName,
                item.IsStable,
                Resolve(item.SubsystemStableId) ?? systemObject,
                Properties(
                    ("isPrimordial", Bool(item.IsPrimordial)),
                    ("healthStatus", item.HealthStatus),
                    ("operationalStatus", item.OperationalStatus),
                    ("sizeBytes", Number(item.Size)),
                    ("allocatedBytes", Number(item.AllocatedSize)),
                    ("logicalSectorSize", Number(item.LogicalSectorSize)),
                    ("physicalSectorSize", Number(item.PhysicalSectorSize)),
                    ("provisioningTypeDefault", item.ProvisioningTypeDefault)));
        }

        foreach (var item in source.StorageTiers)
        {
            Add(
                StorageObjectKind.StorageTier,
                item.StableId,
                item.FriendlyName,
                item.IsStable,
                Resolve(item.PoolStableId) ?? systemObject,
                Properties(
                    ("mediaType", item.MediaType),
                    ("resiliency", item.ResiliencySettingName),
                    ("sizeBytes", Number(item.Size)),
                    ("footprintBytes", Number(item.FootprintOnPool)),
                    ("numberOfColumns", Number(item.NumberOfColumns)),
                    ("interleaveBytes", Number(item.Interleave))));
        }

        foreach (var item in source.PhysicalDisks)
        {
            var properties = Properties(
                ("friendlyName", item.FriendlyName),
                ("model", item.Model),
                ("serialNumber", item.MaskedSerialNumber),
                ("busType", item.BusType),
                ("mediaType", item.MediaType),
                ("sizeBytes", Number(item.Size)),
                ("logicalSectorSize", Number(item.LogicalSectorSize)),
                ("physicalSectorSize", Number(item.PhysicalSectorSize)),
                ("healthStatus", item.HealthStatus),
                ("operationalStatus", item.OperationalStatus),
                ("canPool", Bool(item.CanPool)),
                ("cannotPoolReason", item.CannotPoolReason),
                ("deviceId", Number(item.DeviceId)),
                ("isBoot", Bool(item.IsBoot)),
                ("isSystem", Bool(item.IsSystem)),
                ("isPageFile", Bool(item.IsPageFile)),
                ("isCrashDump", Bool(item.IsCrashDump)),
                ("firmwareVersion", item.FirmwareVersion),
                ("interfaceType", item.InterfaceType),
                ("provisioningType", item.ProvisioningType));
            if (includeSensitiveValuesInMemory)
            {
                properties["pnpDeviceId"] = item.PnpDeviceId;
            }

            Add(
                StorageObjectKind.PhysicalDisk,
                item.StableId,
                First(item.FriendlyName, item.Model, item.StableId),
                item.IsStable,
                Resolve(item.PoolStableId) ?? systemObject,
                properties);
        }

        foreach (var item in source.VirtualDisks)
        {
            Add(
                StorageObjectKind.VirtualDisk,
                item.StableId,
                item.FriendlyName,
                item.IsStable,
                Resolve(item.PoolStableId) ?? systemObject,
                Properties(
                    ("healthStatus", item.HealthStatus),
                    ("operationalStatus", item.OperationalStatus),
                    ("resiliency", item.ResiliencySettingName),
                    ("provisioningType", item.ProvisioningType),
                    ("numberOfColumns", Number(item.NumberOfColumns)),
                    ("interleaveBytes", Number(item.Interleave)),
                    ("sizeBytes", Number(item.Size)),
                    ("footprintBytes", Number(item.FootprintOnPool))));
        }

        foreach (var item in source.OsDisks)
        {
            Add(
                StorageObjectKind.OsDisk,
                item.StableId,
                item.FriendlyName,
                stable: true,
                Resolve(item.VirtualDiskStableId)
                ?? Resolve(item.PhysicalDiskStableId)
                ?? systemObject,
                Properties(
                    ("number", Number(item.Number)),
                    ("partitionStyle", item.PartitionStyle),
                    ("sizeBytes", Number(item.Size)),
                    ("isBoot", Bool(item.IsBoot)),
                    ("isSystem", Bool(item.IsSystem)),
                    ("isOffline", Bool(item.IsOffline))));
        }

        foreach (var item in source.Partitions)
        {
            Add(
                StorageObjectKind.Partition,
                item.StableId,
                First(item.DriveLetter, item.FileSystemLabel, $"Partition {item.PartitionNumber}"),
                item.IsStable,
                Resolve(item.OsDiskStableId) ?? systemObject,
                Properties(
                    ("diskNumber", Number(item.DiskNumber)),
                    ("partitionNumber", Number(item.PartitionNumber)),
                    ("type", item.Type),
                    ("offsetBytes", Number(item.Offset)),
                    ("sizeBytes", Number(item.Size)),
                    ("isBoot", Bool(item.IsBoot)),
                    ("isSystem", Bool(item.IsSystem)),
                    ("driveLetter", item.DriveLetter),
                    ("fileSystemLabel", item.FileSystemLabel),
                    ("fileSystem", item.FileSystem),
                    ("allocationUnitSize", Number(item.AllocationUnitSize)),
                    ("sizeRemainingBytes", Number(item.SizeRemaining)),
                    ("healthStatus", item.HealthStatus),
                    ("operationalStatus", item.OperationalStatus),
                    ("isHidden", Bool(item.IsHidden))));
        }

        foreach (var item in source.NetworkDisks)
        {
            Add(
                StorageObjectKind.NetworkDisk,
                item.StableId,
                item.Name,
                item.IsStable,
                systemObject,
                Properties(
                    ("driveLetter", item.DriveLetter),
                    ("providerPath", includeSensitiveValuesInMemory
                        ? item.ProviderPath
                        : string.Empty),
                    ("fileSystem", item.FileSystem),
                    ("sizeBytes", Number(item.Size)),
                    ("sizeRemainingBytes", Number(item.SizeRemaining))));
        }

        var relationships = source.Relationships
            .Select(item =>
            {
                var from = Resolve(item.FromStableId);
                var to = Resolve(item.ToStableId);
                return from is not null && to is not null
                    ? new StorageRelationshipView(
                        from.Value,
                        to.Value,
                        item.RelationshipKind)
                    : null;
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        return new InventorySnapshot(
            systemId,
            InventoryProviderKind.EmbeddedReadOnlyPowerShell,
            source.SnapshotVersion,
            MachineBinding.Create(
                [source.Computer.StableId, source.Computer.Name]),
            source.ScannedAt,
            objects,
            diagnostics,
            relationships);

        StorageObjectId Add(
            StorageObjectKind kind,
            string stableId,
            string displayName,
            bool stable,
            StorageObjectId? parent,
            IReadOnlyDictionary<string, string?> properties)
        {
            var providerKey = string.IsNullOrWhiteSpace(stableId)
                ? $"temporary:{kind}:{objects.Count}"
                : stableId.Trim();
            var id = new StorageObjectId(systemId, kind, providerKey);
            objects.Add(
                new StorageObjectView(
                    id,
                    parent,
                    First(displayName, providerKey),
                    stable ? IdentityStability.Stable : IdentityStability.Temporary,
                    properties));
            if (!identities.TryAdd(providerKey, id))
            {
                diagnostics.Add(
                    new InventoryIdentityDiagnostic(
                        id,
                        IdentityStability.Temporary,
                        "inventory.identity.duplicate",
                        string.Empty));
            }

            if (!stable)
            {
                diagnostics.Add(
                    new InventoryIdentityDiagnostic(
                        id,
                        IdentityStability.Temporary,
                        "inventory.identity.temporary",
                        string.Empty));
            }

            return id;
        }

        StorageObjectId? Resolve(string? stableId) =>
            !string.IsNullOrWhiteSpace(stableId)
            && identities.TryGetValue(stableId.Trim(), out var id)
                ? id
                : null;
    }

    private static Dictionary<string, string?> Properties(
        params (string Key, string? Value)[] values) =>
        values.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Bool(bool value) =>
        value ? "true" : "false";

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;

    private static ApplicationResult<InventorySnapshot> Failure(
        ApplicationStatus status,
        CorrelationId correlation,
        string code) =>
        ApplicationResult<InventorySnapshot>.FromStatus(
            status,
            correlation,
            Message(code));

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);
}
