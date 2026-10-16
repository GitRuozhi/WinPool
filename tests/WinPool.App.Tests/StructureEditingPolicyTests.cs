using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class StructureEditingPolicyTests
{
    [Theory]
    [InlineData(4, 2, 2)]
    [InlineData(6, 3, 2)]
    public void MirrorSuggestionsFollowEligibleMembersAndCopiesUntilUserChooses(int count, int copies, int expected)
    {
        var disk = SimulationLayouts.StandardTiered().PhysicalDisks.First(item => item.MediaType == "SSD");
        var members = Enumerable.Range(0, count).Select(index => disk with
            { StableId = "data:" + index, Usage = PhysicalDiskUsage.AutoSelect }).ToArray();
        var suggestion = new StructureColumnSuggestion();
        Assert.Equal(expected, suggestion.Resolve("Mirror", members, copies));
        Assert.Equal(expected, suggestion.Resolve("Mirror", [..members,
            disk with { StableId = "spare", Usage = PhysicalDiskUsage.HotSpare },
            disk with { StableId = "retired", Usage = PhysicalDiskUsage.Retired }], copies));
        Assert.Equal(1, suggestion.Choose(1).Resolve("Mirror", members, copies));
        Assert.Equal(count * 2 / (copies + 1), suggestion.Resolve("Mirror", members.Concat(members).ToArray(), copies + 1));
    }

    [Fact]
    public void ExplicitMirrorLayoutSurvivesMemberRecommendationsWhileUntouchedLayoutFollowsThem()
    {
        var source = SimulationLayouts.StandardTiered();
        var disk = source.PhysicalDisks.First(item => item.MediaType == "HDD");
        var members = Enumerable.Range(0, 6).Select(index => disk with
            { StableId = "hdd:" + index, Usage = PhysicalDiskUsage.AutoSelect }).ToArray();
        var recommended = source.StorageTiers.First(item => item.MediaType == "HDD") with
            { ResiliencySettingName = "Parity", NumberOfDataCopies = 1, PhysicalDiskRedundancy = 1 };
        var automatic = new StructureColumnSuggestion();
        Assert.Equal("Parity", automatic.ApplyLayout(recommended, members).ResiliencySettingName);
        var chosen = automatic.ChooseResiliency("Mirror");
        var four = chosen.ApplyLayout(recommended, members.Take(4).ToArray());
        Assert.Equal("Mirror", four.ResiliencySettingName);
        Assert.Equal(2, four.NumberOfDataCopies);
        Assert.Equal(2, four.NumberOfColumns);
        var six = chosen.ApplyLayout(recommended, members);
        Assert.Equal("Mirror", six.ResiliencySettingName);
        Assert.Equal(3, six.NumberOfColumns);
        var threeCopies = chosen.ChooseCopies("Mirror", 3).ApplyLayout(recommended, members);
        Assert.Equal("Mirror", threeCopies.ResiliencySettingName);
        Assert.Equal(3, threeCopies.NumberOfDataCopies);
        Assert.Equal(2, threeCopies.NumberOfColumns);
        var manual = chosen.ChooseCopies("Mirror", 3).Choose(1).ApplyLayout(recommended, members);
        Assert.Equal("Mirror", manual.ResiliencySettingName);
        Assert.Equal(3, manual.NumberOfDataCopies);
        Assert.Equal(1, manual.NumberOfColumns);
    }

    [Fact]
    public void NewPoolTargetsVdAndPartitionAndExplicitDeleteSurvivesLaterCapture()
    {
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("new"), "New");
        var pool = Assert.Single(snapshot.StoragePools);
        var intent = StructureCreationPolicy.NewPool("New", createMsr: false);
        Assert.True(intent.AutoCreateVirtualDisk);
        Assert.True(intent.AutoCreatePartition);
        Assert.True(StructureCreationPolicy.WantsVirtualDisk(snapshot, pool.StableId, intent));
        var deleted = StructureCreationPolicy.ChooseVirtualDisk(intent, false);
        Assert.False(StructureCreationPolicy.WantsVirtualDisk(snapshot, pool.StableId, deleted));
        var recreated = SimulationEditingSession.InsertRealDraftVirtualDisk(snapshot, pool.StableId, "New");
        var chosen = StructureCreationPolicy.ChooseVirtualDisk(deleted, true);
        Assert.True(chosen.AutoCreatePartition);
        Assert.True(StructureCreationPolicy.WantsVirtualDisk(recreated, pool.StableId, chosen));
    }

    [Theory]
    [InlineData("REFS")]
    [InlineData("refs")]
    [InlineData("ReFS")]
    public void LinkedCommittedReFsIsTheFormResetAndNetDifferenceBaseline(string reported)
    {
        var source = SimulationLayouts.StandardTiered();
        var pool = source.StoragePools.Single(item => !item.IsPrimordial);
        var partition = StructurePartitionBaseline.ForPool(source, pool.StableId)!;
        var volume = source.Volumes.Single(item => item.PartitionStableId == partition.StableId);
        var fresh = source with
        {
            Partitions = source.Partitions.Select(item => item.StableId == partition.StableId
                ? item with { FileSystem = "", AllocationUnitSize = null } : item).ToArray(),
            Volumes = source.Volumes.Select(item => item.StableId == volume.StableId
                ? item with { FileSystem = reported, AllocationUnitSize = 65536 } : item).ToArray()
        };
        var actual = StructurePartitionBaseline.ForPool(fresh, pool.StableId)!;
        Assert.Equal(partition.StableId, actual.StableId);
        Assert.Equal("ReFS", actual.FileSystem);
        Assert.Equal(65536, actual.AllocationUnitSize);
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "simulation:refs",
            StorageSystemKind.Simulation, "ReFS", fresh, [], DateTimeOffset.UtcNow);
        var session = new SimulationEditingSession();
        session.Bind(document, fresh);
        session.PoolIntents[pool.StableId] = new(true, true, "ReFS", 65536, actual.FileSystemLabel);
        Assert.False(StructureDraftReentry.HasDraftContent(session));
        var capturedRevision = session.ContentRevision;
        var capturedBinding = session.BindingGeneration;
        StructureDraftReentry.Enter(session, document with { Revision = document.Revision + 1 }, fresh,
            false, out var rebound, capturedRevision, capturedBinding);
        Assert.True(rebound);
        Assert.Empty(session.PoolIntents);
        Assert.Empty(session.UndoStack);
        Assert.Equal("ReFS", StructurePartitionBaseline.ForPool(session.Baseline, pool.StableId)!.FileSystem);
        Assert.False(StructureDraftReentry.HasDraftContent(session));
    }

    [Fact]
    public void ExpandedSimulationPresentationKeepsFormatParametersAndDataLoss()
    {
        var request = new SimulationEditRequest(SimulationEditKind.FormatPartition, "partition:exact",
            Name: "Label", FileSystem: "ReFS", AllocationUnitSize: 65536, QuickFormat: true);
        var item = new SimulationPlanItem("format", "Format", request, CausesDataLoss: true);
        var model = PendingStructureAction.FromSimulation(item, "Format");
        Assert.Equal("Format", model.Title);
        Assert.Contains(new PendingStructureParameter("Target", "partition:exact"), model.Parameters);
        Assert.Contains(new PendingStructureParameter("FileSystem", "ReFS"), model.Parameters);
        Assert.Contains(new PendingStructureParameter("AllocationUnitSize", "65536"), model.Parameters);
        Assert.Contains(new PendingStructureParameter("QuickFormat", "True"), model.Parameters);
        Assert.True(model.DataLoss);
    }
}
