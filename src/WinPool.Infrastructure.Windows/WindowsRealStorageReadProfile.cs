using WinPool.Application;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

/// <summary>Closed command evidence sets. MAX keeps the historical full-component no-change proof.</summary>
public sealed class WindowsRealStorageReadProfile
{
    public const string Required = "command-required-v1";
    public const string Resize = "command-required-resize-v1";
    public const string Metadata = "command-metadata-v1";
    public const string Os = "command-os-v1";
    public const string OsResize = "command-os-resize-v1";
    public string Id { get; }
    public IReadOnlyDictionary<string, string[]> Fields { get; }

    private WindowsRealStorageReadProfile(string id)
    {
        Id = id;
        Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Win32_OperatingSystem"] = Split("Caption Version BuildNumber LastBootUpTime"),
            ["Win32_ComputerSystem"] = Split("Name"),
            ["MSFT_StorageSubSystem"] = Split("UniqueId ObjectId FriendlyName"),
            ["MSFT_PhysicalDisk"] = Split("UniqueId ObjectId FriendlyName SerialNumber DeviceId Size LogicalSectorSize PhysicalSectorSize HealthStatus Usage CanPool MediaType BusType AllocatedSize IsBoot IsSystem IsPageFile IsCrashDump"),
            ["MSFT_StoragePool"] = Split("UniqueId ObjectId FriendlyName IsPrimordial Size AllocatedSize LogicalSectorSize PhysicalSectorSize ProvisioningTypeDefault"),
            ["MSFT_VirtualDisk"] = Split("UniqueId ObjectId FriendlyName Size ResiliencySettingName ProvisioningType NumberOfColumns Interleave NumberOfDataCopies PhysicalDiskRedundancy AllocatedSize FootprintOnPool WriteCacheSize"),
            ["MSFT_StorageTier"] = Split("UniqueId ObjectId FriendlyName MediaType ResiliencySettingName ProvisioningType NumberOfColumns Interleave NumberOfDataCopies PhysicalDiskRedundancy Size FootprintOnPool AllocatedSize"),
            ["MSFT_Disk"] = Split("UniqueId ObjectId FriendlyName SerialNumber Number Path Guid BusType Size PartitionStyle LogicalSectorSize PhysicalSectorSize IsOffline IsReadOnly IsClustered IsBoot IsSystem NumberOfPartitions LargestFreeExtent AllocatedSize"),
            ["MSFT_Partition"] = Split("ObjectId Guid DiskId DiskNumber PartitionNumber Offset Size Type GptType MbrType IsBoot IsSystem IsHidden IsOffline IsReadOnly IsShadowCopy DriveLetter AccessPaths"),
            ["MSFT_Volume"] = Split("UniqueId ObjectId Path DriveLetter FileSystem FileSystemLabel AllocationUnitSize Size" + (id is Resize or OsResize ? " SizeRemaining" : ""))
        };
        if (id is Metadata or Os or OsResize)
        {
            // Metadata and OS operations do not resize the backing pool/VD.
            // Member identity, native roles and descendant protection facts remain.
            var fields = (Dictionary<string, string[]>)Fields;
            // The canonical member is also consumed by the strict single-HDD tier
            // association proof during projection, even for metadata/OS commands.
            fields["MSFT_PhysicalDisk"] = Split("UniqueId ObjectId FriendlyName SerialNumber DeviceId Size HealthStatus Usage BusType MediaType IsBoot IsSystem IsPageFile IsCrashDump"
                + (id is Os or OsResize ? " LogicalSectorSize PhysicalSectorSize" : ""));
            fields["MSFT_StoragePool"] = Split("UniqueId ObjectId FriendlyName IsPrimordial");
            // Rename-VD selects this object directly before association traversal.
            // Keep the native prerequisites of the same strict tier-member proof.
            fields["MSFT_VirtualDisk"] = Split("UniqueId ObjectId FriendlyName" + (id == Metadata
                ? " Size AllocatedSize FootprintOnPool WriteCacheSize ResiliencySettingName ProvisioningType NumberOfColumns Interleave NumberOfDataCopies PhysicalDiskRedundancy"
                : ""));
            fields["MSFT_StorageTier"] = Split("UniqueId ObjectId FriendlyName");
        }
    }

    private static string[] Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public static WindowsRealStorageReadProfile? ForId(string? id) => id switch
    {
        null => null,
        Required or Resize or Metadata or Os or OsResize => new(id),
        _ => throw new InvalidDataException("The persisted storage evidence profile is unsupported.")
    };
    public static WindowsRealStorageReadProfile? ForSteps(IReadOnlyList<RealOperationStep> steps)
    {
        if (steps.Any(step => step.Command is CreateVirtualDiskCommand { UseMaximumSize: true }
            or CreateTieredVirtualDiskCommand { UseMaximumSize: true })) return null;
        if (steps.All(step => step.Command is RenamePoolCommand or RenameVirtualDiskCommand or RenameVolumeCommand))
            return new(Metadata);
        var resize = steps.Any(step => step.Command is ResizePartitionCommand);
        if (steps.All(step => step.Command is SetDiskOnlineCommand or InitializeGptCommand or ClearDiskCommand
            or CreatePartitionCommand or DeletePartitionCommand or ResizePartitionCommand or FormatVolumeCommand
            or SetDriveLetterCommand or RenameVolumeCommand)) return new(resize ? OsResize : Os);
        return new(resize ? Resize : Required);
    }

    public static bool Covers(string? observed, string? requested) => observed is null || observed == requested
        || observed == Resize && requested is Required or Metadata or Os or OsResize
        || observed == Required && requested is Metadata or Os
        || observed == OsResize && requested is Os or Metadata
        || observed == Os && requested == Metadata;

    public static string? CoveringBoth(string? first, string? second) => Covers(first, second) ? first
        : Covers(second, first) ? second : null;

    public bool IncludesFingerprintField(string className, string field)
    {
        // Newly collected native identity/projection prerequisites do not change
        // the persisted v1 command fingerprint domain. The physical serial remains bound there.
        if (className == "MSFT_Disk" && field.Equals("SerialNumber", StringComparison.OrdinalIgnoreCase)) return false;
        if ((Id is Metadata or Os or OsResize) && className == "MSFT_PhysicalDisk"
            && field.Equals("MediaType", StringComparison.OrdinalIgnoreCase)) return false;
        if (Id == Metadata && className == "MSFT_VirtualDisk")
            return field.Equals("UniqueId", StringComparison.OrdinalIgnoreCase)
                || field.Equals("ObjectId", StringComparison.OrdinalIgnoreCase)
                || field.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase);
        return !Fields.TryGetValue(className, out var fields)
            || fields.Contains(field, StringComparer.OrdinalIgnoreCase);
    }

    public bool RequiresProjectedField(FactObjectType type, string name)
    {
        // These are derived values, optional telemetry, or fields used only by display.
        if (name is "OperationalStatus" or "InterfaceType" or "PnpDeviceId" or "Model" or "FirmwareVersion" or "CannotPoolReason" or "ProviderIdentity") return false;
        if (name == "HealthStatus") return type == FactObjectType.PhysicalDisk;
        if (name == "SizeRemaining") return (Id is Resize or OsResize) && type is FactObjectType.Partition or FactObjectType.Volume;
        if (type == FactObjectType.PhysicalDisk && name is "ProvisioningType") return false;
        if (type == FactObjectType.StoragePool && name is "HealthStatus") return false;
        if (Id is Metadata or Os or OsResize)
        {
            if (type == FactObjectType.StoragePool) return name is "FriendlyName" or "IsPrimordial";
            if (type == FactObjectType.VirtualDisk) return name == "FriendlyName";
            if (type == FactObjectType.PhysicalDisk && name is "CanPool" or "MediaType" or "AllocatedSize") return false;
            if (Id == Metadata && type == FactObjectType.PhysicalDisk && name is "LogicalSectorSize" or "PhysicalSectorSize") return false;
            if (type == FactObjectType.StorageTier) return name == "FriendlyName";
        }
        return true;
    }
}
