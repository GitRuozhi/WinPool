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
        var primary = item.Primary.Field(name);
        var candidates = new List<WinPoolSourceField>();
        if (primary is not null) candidates.Add(primary);
        if (item.ObjectType == FactObjectType.PhysicalDisk && name is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump")
            candidates.AddRange(item.Sources.Where(x => x.ObjectType == FactObjectType.Disk)
                .Select(x => x.Field(name)).OfType<WinPoolSourceField>());
        var available = candidates.Where(x => x is { ReadState: FieldReadState.Returned, IsRedacted: false,
            Value: { ValueKind: not JsonValueKind.Null } }).ToArray();
        var conflict = available.Skip(1).Any(x => !JsonElement.DeepEquals(x.Value!.Value, available[0].Value!.Value));
        // The primary provider owns this semantic field; fallback requires exactly one usable observation.
        var chosen = primary is { ReadState: FieldReadState.Returned, IsRedacted: false, Value: { ValueKind: not JsonValueKind.Null } }
            ? primary : available.Length == 1 ? available[0] : primary;
        return new(chosen, candidates.ToImmutableArray(), conflict,
            conflict ? "SourceConflict" : chosen is null ? "NotCollected" : ReferenceEquals(chosen, primary) ? "PrimaryProvider" : "AssociatedOsDiskFallback");
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
