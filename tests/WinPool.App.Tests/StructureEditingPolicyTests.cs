using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

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
    [InlineData(true, 'C')]
    [InlineData(false, 'D')]
    public void AutomaticDriveLetterStartsAtThePartitionEditorsFirstAvailableLetter(bool simulation, char expected)
    {
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("letters"), "New");
        var pool = Assert.Single(snapshot.StoragePools);
        var intent = StructureCreationPolicy.NewPool("New", createMsr: true);

        Assert.Equal(expected, StructureCreationPolicy.WithDefaultDriveLetter(snapshot, pool.StableId,
            intent, new Dictionary<string, PoolEditIntent>(), simulation).DriveLetter);
    }

    [Fact]
    public void AutomaticDriveLetterSkipsPartitionVolumeAccessPathsAndNetworkLetters()
    {
        var template = SimulationLayouts.StandardTiered();
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("letters"), "New") with
        {
            Partitions = [template.Partitions.First() with
                { DriveLetter = "d:", Path = "", StableId = "partition:occupied" }],
            Volumes = [template.Volumes.First() with
                { PartitionStableId = null, AccessPaths = ["c:\\", "e:/", "C:\\MountedFolder\\"] }],
            NetworkDisks = [new("network:occupied", true, "Share", "f:", "\\\\server\\share", "NTFS", 0, 0)]
        };
        var pool = Assert.Single(snapshot.StoragePools);

        Assert.Equal('G', StructureCreationPolicy.WithDefaultDriveLetter(snapshot, pool.StableId,
            StructureCreationPolicy.NewPool("New", true), new Dictionary<string, PoolEditIntent>(),
            simulation: true).DriveLetter);
    }

    [Fact]
    public void AutomaticDriveLetterReservesOtherNewTargetsButIgnoresDeletedVdsAndPools()
    {
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("letters"), "First");
        var pool = Assert.Single(snapshot.StoragePools);
        var first = pool.StableId;
        var second = first + ":second";
        var deleted = first + ":deleted";
        snapshot = snapshot with { StoragePools = [pool,
            pool with { StableId = second, FriendlyName = "Second" },
            pool with { StableId = deleted, FriendlyName = "Deleted VD" }] };
        var intent = StructureCreationPolicy.NewPool("New", true);
        var intents = new Dictionary<string, PoolEditIntent>
        {
            [first] = StructureCreationPolicy.WithDefaultDriveLetter(snapshot, first, intent,
                new Dictionary<string, PoolEditIntent>(), simulation: true),
            [second] = intent with { DriveLetter = 'Z' },
            [deleted] = StructureCreationPolicy.ChooseVirtualDisk(intent with { DriveLetter = 'D' }, false),
            ["removed:pool"] = intent with { DriveLetter = 'E' }
        };

        var selected = StructureCreationPolicy.WithDefaultDriveLetter(snapshot, second, intent, intents, simulation: true);
        Assert.Equal('C', intents[first].DriveLetter);
        Assert.Equal('D', selected.DriveLetter);
        intents[second] = selected;
        Assert.Equal('E', StructureCreationPolicy.WithDefaultDriveLetter(snapshot, deleted, intent,
            intents, simulation: true).DriveLetter);
    }

    [Fact]
    public void AutomaticDriveLetterPreservesChosenFrozenAndDisabledLayoutIntents()
    {
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("letters"), "New");
        var pool = Assert.Single(snapshot.StoragePools);
        var intent = StructureCreationPolicy.NewPool("New", true);
        var preserved = new[]
        {
            intent with { DriveLetter = 'W' },
            intent with { PendingAutomaticLayout = true },
            intent with { AutoCreateVirtualDisk = false },
            intent with { AutoCreatePartition = false }
        };
        foreach (var original in preserved)
            Assert.Same(original, StructureCreationPolicy.WithDefaultDriveLetter(snapshot, pool.StableId,
                original, new Dictionary<string, PoolEditIntent>(), simulation: true));
    }

    [Fact]
    public void AutomaticDriveLetterReachesTheSimulatedNewPartitionAndAppliedVolume()
    {
        var snapshot = SimulationLayouts.SingleDiskPool();
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var intent = StructureCreationPolicy.WithDefaultDriveLetter(snapshot, pool.StableId,
            StructureCreationPolicy.NewPool("New volume", true),
            new Dictionary<string, PoolEditIntent>(), simulation: true);
        var create = new SimulationEditRequest(SimulationEditKind.CreateVirtualDisk, pool.StableId,
            Name: "New VD", SizeBytes: 32L << 30, Resiliency: "Simple", PerformanceDataCopies: 1,
            AllocatedVirtualDiskId: "sim:vd:auto-letter", AllocatedOsDiskId: "sim:os:auto-letter");
        var steps = new List<SimulationEditRequest> { create };
        SimulationDraftPlanner.ConfigureAutomaticPartitionIntent(steps, create, intent,
            "sim:partition:auto-letter", "sim:volume:auto-letter", "sim:msr:auto-letter");
        var data = Assert.Single(steps, step => step.PartitionKind == PartitionKind.BasicData);
        Assert.NotNull(intent.DriveLetter);
        Assert.Equal(intent.DriveLetter.ToString(), data.DriveLetter);

        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "simulation:auto-letter", StorageSystemKind.Simulation, "Automatic letter", snapshot, [], DateTimeOffset.UtcNow);
        var result = new SimulationOperationService().ApplyPlan(document, SimulationDraftPlanner.Precheck(snapshot, steps));
        Assert.True(result.Succeeded, result.Error);
        var volume = result.Document.Snapshot.VolumeForPartition("sim:partition:auto-letter");
        Assert.NotNull(volume);
        Assert.Equal(data.DriveLetter, volume.DriveLetter);
    }

    [Fact]
    public void AutomaticDriveLetterReachesTheRealNewPartitionAssignmentCommand()
    {
        var snapshot = EditWorkspace.InsertDraftPool(StorageSnapshot.Empty("letters"), "New");
        var pool = Assert.Single(snapshot.StoragePools);
        var intent = StructureCreationPolicy.WithDefaultDriveLetter(snapshot, pool.StableId,
            StructureCreationPolicy.NewPool("New volume", true),
            new Dictionary<string, PoolEditIntent>(), simulation: false);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os:new-vd");
        var proposal = RealOperationProposalFactory.ConfigureInitializedDisk(system, disk, null,
            intent.CreateMsr, 32L << 30, true, intent.VolumeName, intent.DriveLetter,
            checked((int)intent.AllocationUnitSize), intent.FileSystem)!;
        var assignment = Assert.Single(proposal.Steps, step => step.Command is SetDriveLetterCommand);
        var command = Assert.IsType<SetDriveLetterCommand>(assignment.Command);
        Assert.Equal('D', command.NewLetter);
        Assert.Equal(intent.DriveLetter, command.NewLetter);
        Assert.Null(command.PreviousLetter);
        Assert.Null(command.Partition.Existing);
        Assert.Contains(command.Partition.CreatedByStep!, assignment.DependsOn);
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
