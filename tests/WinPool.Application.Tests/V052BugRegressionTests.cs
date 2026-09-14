using System.Collections.Immutable;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class V052BugRegressionTests
{
    [Fact]
    public void StorageEditKeepsLogicalObservationAssociation()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        var volume = snapshot.Volumes.First();
        facts = facts with
        {
            Objects = facts.Objects.Add(new("logical-observation", FactObjectType.LogicalDisk, facts.Sources[0].Id,
                "logical", true, [])),
            Relationships = facts.Relationships.Add(new(volume.StableId, "logical-observation", "same-volume"))
        };
        var candidate = snapshot with { Volumes = snapshot.Volumes.Select(x => x.StableId == volume.StableId
            ? x with { FileSystemLabel = "Renamed" } : x).ToArray() };
        var result = WinPoolSimulationFacts.ApplyCandidate(facts, snapshot, candidate, facts.SystemId);
        Assert.Contains(result.Relationships, x => x.Kind == "same-volume" && x.ToId == "logical-observation");
        Assert.Equal(2, new WinPoolSystem(result).Objects.Single(x => x.Id == volume.StableId).Sources.Length);
    }

    [Fact]
    public void MissingTierAssociationIsUnknownAndUsageTitleUsesFriendlyName()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        facts = facts with { IsSimulation = false, Sources = facts.Sources.Select(x => x with { Origin = FactOrigin.StorageCim }).ToImmutableArray(),
            Relationships = facts.Relationships.Where(x => x.Kind != "tier-member").ToImmutableArray() };
        var view = WinPoolStorageProjection.Project(facts);
        Assert.All(view.StoragePools.Where(x => !x.IsPrimordial), x => Assert.Equal("Membership unknown", view.DirectGroupName(x.StableId)));
        var target = snapshot.PhysicalDisks.First(x => !x.IsBoot && !x.IsSystem && x.PoolStableId is not null);
        var plan = SimulationDraftPlanner.Precheck(snapshot, [new(SimulationEditKind.SetDiskUsage, target.StableId, DiskUsage: "Retired")]);
        var item = Assert.Single(plan.DisplayItems);
        Assert.Contains(target.FriendlyName, item.Title);
        Assert.DoesNotContain(target.StableId, item.Title);
    }

    [Fact]
    public void EquivalentRoleConflictIsVisibleAndBlocksStructuralEdit()
    {
        var facts = new WinPoolFacts(1, SystemId.New(), 0,
            [new("p", FactOrigin.StorageCim, "storage", "MSFT_PhysicalDisk", DateTimeOffset.UtcNow, CollectionPurpose.Storage),
             new("d", FactOrigin.StorageCim, "storage", "MSFT_Disk", DateTimeOffset.UtcNow, CollectionPurpose.Storage)],
            [new("physical", FactObjectType.PhysicalDisk, "p", "p", true,
                [WinPoolSourceField.Returned("IsSystem", false, FactValueType.Boolean, "p")]),
             new("disk", FactObjectType.Disk, "d", "d", true,
                [WinPoolSourceField.Returned("IsSystem", true, FactValueType.Boolean, "d")])],
            [new("physical", "disk", "same-device")], [], []);
        var system = new WinPoolSystem(facts);
        var disk = Assert.Single(system.Objects);
        var selected = WinPoolSourceDetails.Select(disk, "IsSystem");
        Assert.True(selected.HasConflict);
        Assert.Contains("SourceConflict", WinPoolSourceDetails.Describe(system, disk));
        Assert.Contains(WinPoolStorageProjection.Project(facts).FieldIssues, x => x.ObjectId == "physical" && x.Reason == "SourceConflict");
        var fallback = facts with { Objects = facts.Objects.Select(x => x.Id == "physical" ? x with { Fields = [] } : x).ToImmutableArray() };
        var resolved = WinPoolSourceDetails.Select(Assert.Single(new WinPoolSystem(fallback).Objects), "IsSystem");
        Assert.Equal("AssociatedOsDiskFallback", resolved.Reason);
        Assert.Equal("true", resolved.Value!.DisplayValue());
    }

    [Fact]
    public void EmptyAndFailedSourcesRemainVisibleWithoutDevices()
    {
        var facts = new WinPoolFacts(1, SystemId.New(), 0,
            [new("failed", FactOrigin.Win32, "root/cimv2", "Win32_Battery", DateTimeOffset.UtcNow, CollectionPurpose.Hardware, FieldReadState.Failed, "AccessDenied"),
             new("empty", FactOrigin.Win32, "root/cimv2", "Win32_Processor", DateTimeOffset.UtcNow, CollectionPurpose.Hardware)], [], [], [], []);
        var rows = WinPoolHardwarePresentation.SourceRows(new WinPoolSystem(facts));
        Assert.Equal(2, rows.Count);
        Assert.Equal("AccessDenied", rows.Single(x => x.Source.Id == "failed").Source.ReasonCode);
        Assert.Equal(FieldReadState.Returned, rows.Single(x => x.Source.Id == "empty").Source.ReadState);
        Assert.All(rows, x => Assert.Equal(0, x.ObjectCount));
        Assert.NotEqual("Size", WinPoolHardwarePresentation.FieldName("Size", true));
        Assert.Equal("VendorExtension", WinPoolHardwarePresentation.FieldName("VendorExtension", true));
    }

    [Theory]
    [InlineData(FactObjectType.Disk, FactObjectType.Partition, "disk-partition")]
    [InlineData(FactObjectType.StoragePool, FactObjectType.VirtualDisk, "pool-virtual-disk")]
    public void PartialRefreshRetainsAssociationUntilSuccessfulReplacement(FactObjectType parent, FactObjectType child, string kind)
    {
        var system = SystemId.New();
        var now = DateTimeOffset.UtcNow;
        WinPoolFacts Capture(DateTimeOffset time, FieldReadState childState, bool empty) => new(1, system, 0,
            [new("parent:" + time.Ticks, FactOrigin.StorageCim, "storage", parent.ToString(), time, CollectionPurpose.Storage),
             new("child:" + time.Ticks, FactOrigin.StorageCim, "storage", child.ToString(), time, CollectionPurpose.Storage, childState)],
            empty ? [new("parent", parent, "parent:" + time.Ticks, "p", true, [])]
                : [new("parent", parent, "parent:" + time.Ticks, "p", true, []), new("child", child, "child:" + time.Ticks, "c", true, [])],
            empty || childState != FieldReadState.Returned ? [] : [new("parent", "child", kind)], [], []);
        var baseline = Capture(now, FieldReadState.Returned, false);
        var merged = WinPoolFactRefresh.Merge(baseline, Capture(now.AddSeconds(1), FieldReadState.Failed, true));
        Assert.Equal(2, merged.Objects.Length);
        var retained = Assert.Single(merged.Relationships);
        Assert.True(retained.IsRetained);
        Assert.Equal(now, retained.ObservedAt);
        Assert.Same(merged, WinPoolFactRefresh.Merge(merged, baseline));
        var removed = WinPoolFactRefresh.Merge(merged, Capture(now.AddSeconds(2), FieldReadState.Returned, true));
        Assert.Single(removed.Objects);
        Assert.Empty(removed.Relationships);
    }

    [Theory]
    [InlineData(FieldReadState.Returned)]
    [InlineData(FieldReadState.NotCollected)]
    [InlineData(FieldReadState.Failed)]
    [InlineData(FieldReadState.Unavailable)]
    public void MissingSystemRoleSurvivesCopyAndCannotAuthorizeFormat(FieldReadState state)
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var partition = snapshot.Partitions.First(x => x.Type is "BasicData" or "Primary" && !x.IsBoot && !x.IsSystem);
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        facts = facts with { Objects = facts.Objects.Select(x => x.Id != partition.StableId ? x : x with
        {
            Fields = x.Fields.Select(f => f.Name != "IsSystem" ? f : f with
                { ReadState = state, Value = null, ReasonCode = "TestMissing" }).ToImmutableArray()
        }).ToImmutableArray() };
        var copy = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(facts.CopyTo(SystemId.New())));
        var view = WinPoolStorageProjection.Project(copy);
        var target = view.Partitions.Single(x => x.PartitionNumber == partition.PartitionNumber && x.Size == partition.Size);
        var decision = StorageEditRules.Evaluate(view, new(SimulationEditKind.FormatPartition, target.StableId, FileSystem: "NTFS"));
        Assert.Equal(StorageRuleVerdict.InsufficientInfo, decision.Verdict);
        Assert.Equal("storage.rule.source-field-unavailable", decision.Code);
    }

    [Fact]
    public void ActualFalseAndZeroAndUnrelatedMissingDescriptionDoNotBlockFormat()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolSimulationFacts.Create(snapshot, SystemId.New());
        facts = facts with { Objects = facts.Objects.Select(x => x with
        {
            Fields = x.Fields.Add(WinPoolSourceField.Missing("Description", FactValueType.String, x.SourceRef, FieldReadState.Failed))
        }).ToImmutableArray() };
        var view = WinPoolStorageProjection.Project(facts);
        var target = view.Partitions.First(x => x.Type is "BasicData" or "Primary" && !x.IsBoot && !x.IsSystem);
        Assert.Equal(StorageRuleVerdict.Allow, StorageEditRules.Evaluate(view,
            new(SimulationEditKind.FormatPartition, target.StableId, FileSystem: "NTFS")).Verdict);
    }
}
