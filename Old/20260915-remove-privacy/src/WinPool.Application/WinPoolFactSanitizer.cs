using System.Collections.Immutable;
using System.Text.Json;

namespace WinPool.Application;

public static class WinPoolFactSanitizer
{
    private static readonly HashSet<string> HardwareIdentifierFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "SerialNumber", "VolumeSerialNumber", "PNPDeviceID", "MacAddress"
    };

    public static WinPoolFacts Redact(WinPoolFacts facts)
    {
        facts.Validate();
        return facts with
        {
            Objects = facts.Objects.Select(item => item with
            {
                Fields = item.Fields.Select(RedactField).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static WinPoolSourceField RedactField(WinPoolSourceField field)
    {
        // Privacy is limited to the serial, PNP and MAC identifiers named by the UI privacy setting.
        // Names, descriptions, paths, storage object IDs and unknown read-only properties stay visible.
        var sensitive = field.ValueType is FactValueType.String or FactValueType.StringArray
            && HardwareIdentifierFields.Contains(field.Name);
        return sensitive && !field.IsRedacted && field.Value is not null
            ? field with { Value = null, IsRedacted = true }
            : field;
    }
}
