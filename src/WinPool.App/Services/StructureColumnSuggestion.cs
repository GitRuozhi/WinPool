using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Services;

/// <summary>New-tier suggestions follow membership while preserving the fields explicitly chosen by the user.</summary>
public sealed record StructureColumnSuggestion(int? ExplicitColumns = null,
    string? ExplicitResiliency = null, int? ExplicitCopies = null, int? ExplicitFailures = null)
{
    public StructureColumnSuggestion Choose(int columns) => this with { ExplicitColumns = columns };
    public StructureColumnSuggestion ChooseResiliency(string resiliency) => this with { ExplicitResiliency = resiliency };
    public StructureColumnSuggestion ChooseCopies(string resiliency, int copies) => this with
        { ExplicitResiliency = resiliency, ExplicitCopies = copies };
    public StructureColumnSuggestion ChooseFailures(string resiliency, int failures) => this with
        { ExplicitResiliency = resiliency, ExplicitFailures = failures };

    public StorageTierInfo ApplyLayout(StorageTierInfo recommended, IReadOnlyList<PhysicalDiskInfo> members)
    {
        var resiliency = ExplicitResiliency ?? recommended.ResiliencySettingName;
        var mirror = resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase);
        var simple = resiliency.Equals("Simple", StringComparison.OrdinalIgnoreCase);
        var copies = mirror ? Math.Max(2, ExplicitCopies ?? recommended.NumberOfDataCopies ?? 2) : 1;
        var failures = mirror ? copies - 1 : simple ? 0
            : Math.Max(1, ExplicitFailures ?? recommended.PhysicalDiskRedundancy ?? 1);
        return recommended with { ResiliencySettingName = resiliency, NumberOfDataCopies = copies,
            PhysicalDiskRedundancy = failures, NumberOfColumns = Resolve(resiliency, members, copies) };
    }

    public int Resolve(string resiliency, IReadOnlyList<PhysicalDiskInfo> members, int copies)
    {
        if (ExplicitColumns is { } chosen) return chosen;
        var eligible = members.Where(member => PhysicalDiskUsage.ContributesDataCapacity(member.Usage)).ToArray();
        return resiliency.Equals("Mirror", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, eligible.Length / Math.Max(1, copies))
            : EditWorkspace.RecommendedTierColumns(resiliency, eligible);
    }
}
