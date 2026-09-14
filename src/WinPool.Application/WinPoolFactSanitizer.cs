using System.Collections.Immutable;
using System.Text.Json;

namespace WinPool.Application;

public static class WinPoolFactSanitizer
{
    private static readonly HashSet<string> PublicTextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "FriendlyName", "Name", "Model", "Manufacturer", "Product", "Caption", "Description",
        "MediaType", "BusType", "Usage", "HealthStatus", "OperationalStatus", "CannotPoolReason",
        "FileSystem", "FileSystemLabel", "ResiliencySettingName", "ProvisioningType", "ProvisioningTypeDefault",
        "PartitionStyle", "Type", "GptType", "MbrType", "FirmwareVersion", "DriverVersion", "Version",
        "BuildNumber", "DisplayVersion", "WindowsProductName", "WindowsVersion", "OsBuild", "UBR",
        "InterfaceType", "DeviceLocator", "BankLabel", "PartNumber", "Purpose", "Status", "LinkSpeed",
        "Mode", "ReleaseDate", "SystemType", "MemoryErrorCorrection", "DriveLetter", "PowerPlan",
        "MUILanguages", "InstalledUICulture", "RegionName", "TimeZoneCaption", "TimeZoneStandardName"
    };

    public static WinPoolFacts Redact(WinPoolFacts facts)
    {
        facts.Validate();
        return facts with
        {
            Objects = facts.Objects.Select(item => item with
            {
                Fields = item.Fields.Select(field => RedactField(item.ObjectType, field)).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static WinPoolSourceField RedactField(FactObjectType type, WinPoolSourceField field)
    {
        // Unrecognized extension strings are private by default; numeric values retain their type and precision.
        var sensitive = field.ValueType is FactValueType.String or FactValueType.StringArray
            && (!PublicTextFields.Contains(field.Name)
                || (type == FactObjectType.Computer && field.Name is "Name" or "FriendlyName"));
        return sensitive && !field.IsRedacted && field.Value is not null
            ? field with { Value = null, IsRedacted = true }
            : field;
    }
}
