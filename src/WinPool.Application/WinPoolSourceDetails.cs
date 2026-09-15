using System.Collections.Immutable;
using System.Text.Json;

namespace WinPool.Application;

public sealed record WinPoolFieldSelection(WinPoolSourceField? Value, ImmutableArray<WinPoolSourceField> Candidates,
    bool HasConflict, string Reason);

/// <summary>Only equivalent source semantics participate in selection. Raw observations remain separate.</summary>
public static class WinPoolSourceDetails
{
    public static WinPoolFieldSelection Select(WinPoolObject item, string name)
    {
        WinPoolSourceField? Read(WinPoolSourceObject source)
        {
            var fieldName = source.ObjectType is FactObjectType.LogicalDisk or FactObjectType.NetworkDisk
                ? name switch { "SizeRemaining" => "FreeSpace", "FileSystemLabel" => "VolumeName", "DriveLetter" => "DeviceID", "ProviderPath" => "ProviderName", _ => name }
                : name == "PnpDeviceId" ? "PNPDeviceID" : name;
            return source.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? source.Fields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        }
        var primary = Read(item.Primary);
        var candidates = new List<WinPoolSourceField>();
        IEnumerable<WinPoolSourceObject> owners = [item.Primary];
        if (item is WinPoolPartition && name is "FileSystem" or "FileSystemLabel" or "AllocationUnitSize" or "SizeRemaining" or "HealthStatus" or "OperationalStatus" or "DriveLetter" or "AccessPaths")
            owners = item.Sources.OrderBy(x => x.ObjectType switch { FactObjectType.Volume => 0, FactObjectType.LogicalDisk => 1, FactObjectType.Partition => 2, _ => 3 });
        if (item is WinPoolDisk && name is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump" or "PnpDeviceId" or "InterfaceType" or "ProvisioningType")
            owners = item.Sources;
        if (item is WinPoolPartition && name is "HealthStatus" or "OperationalStatus" && item.Sources.Any(x => x.ObjectType == FactObjectType.Volume))
            owners = item.Sources.Where(x => x.ObjectType == FactObjectType.Volume);
        candidates.AddRange(owners.Select(Read).OfType<WinPoolSourceField>());
        primary = candidates.FirstOrDefault();
        var available = candidates.Where(x => x is { ReadState: FieldReadState.Returned,
            Value: { ValueKind: not JsonValueKind.Null } }).ToArray();
        bool Equivalent(WinPoolSourceField left, WinPoolSourceField right) => name == "DriveLetter"
            ? left.DisplayValue().TrimEnd(':', '\0').Equals(right.DisplayValue().TrimEnd(':', '\0'), StringComparison.OrdinalIgnoreCase)
            : JsonElement.DeepEquals(left.Value!.Value, right.Value!.Value);
        var conflict = available.Skip(1).Any(x => !Equivalent(x, available[0]));
        // Preference is semantic-field specific; conflicting observations remain visible.
        var chosen = primary is { ReadState: FieldReadState.Returned, Value: { ValueKind: not JsonValueKind.Null } }
            ? primary : available.FirstOrDefault() ?? primary;
        return new(chosen, candidates.ToImmutableArray(), conflict,
            conflict ? "SourceConflict" : chosen is null ? "NotCollected" : ReferenceEquals(chosen, primary) ? "PrimaryProvider" : "AssociatedSourceFallback");
    }

    public static string Describe(WinPoolSystem system, WinPoolObject item)
    {
        var lines = new List<string>();
        foreach (var observation in item.Sources)
        foreach (var field in observation.Fields)
        {
            var source = system.Sources.First(x => x.Id == field.SourceRef);
            var selection = Select(item, field.Name);
            lines.Add($"{source.ClassName}.{field.Name}: {field.DisplayValue()} {field.Unit}\n{field.ValueType} / {field.ReadState} / {field.ReasonCode}"
                + $"\n{source.Origin}: {source.Namespace} / {source.CapturedAt:O}"
                + (selection.Candidates.Length > 1 ? $"\nWinPool.{field.Name}: {selection.Reason}" : ""));
        }
        return string.Join("\n\n", lines);
    }
}
