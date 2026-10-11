namespace WinPool.Application.Tests;

public sealed class SimulationAutomaticPartitionIntentTests
{
    private const long MiB = 1L << 20;
    private const long Size = 32L << 30;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EditorAutomaticPolicyPreservesMsrChoiceAndLeavesDisabledLayoutRaw(bool autoPart, bool createMsr)
    {
        var document = Document();
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var create = new SimulationEditRequest(SimulationEditKind.CreateVirtualDisk, pool.StableId,
            Name: "Exact VD", SizeBytes: Size, Resiliency: "Simple", PerformanceDataCopies: 1,
            AllocatedVirtualDiskId: "sim:vd:auto",
            AllocatedOsDiskId: "sim:os:auto");
        var intent = new PoolEditIntent(true, autoPart, "NTFS", 65536, "Exact label",
            VirtualDiskName: "Exact VD", CreateMsr: createMsr, DriveLetter: 'E', QuickFormat: false);
        var steps = new List<SimulationEditRequest> { create };

        SimulationDraftPlanner.ConfigureAutomaticPartitionIntent(steps, create, intent,
            "sim:partition:data", "sim:volume:data", "sim:partition:msr");
        var plan = SimulationDraftPlanner.Precheck(document.Snapshot, steps);
        var result = new SimulationOperationService().ApplyPlan(document, plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(plan.DisplayItems.SelectMany(item => item.CommandPreview), result.Commands);
        AssertLayout(result.Document.Snapshot, autoPart, createMsr);
        Assert.Equal(autoPart ? "GPT" : "RAW", steps[0].PartitionStyle);
        Assert.Equal(createMsr, steps[0].CreateMsr);
        if (autoPart)
        {
            var data = steps.Single(step => step.PartitionKind == PartitionKind.BasicData);
            Assert.Equal("sim:partition:data", data.AllocatedPartitionId);
            Assert.Equal("sim:volume:data", data.AllocatedVolumeId);
            Assert.False(data.QuickFormat);
            Assert.Contains(result.Commands, command => command.Contains(" -Full", StringComparison.Ordinal));
        }
        else
        {
            Assert.Single(steps);
            Assert.DoesNotContain(result.Commands, command => command.Contains("Initialize-Disk", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExplicitSimulationVirtualDiskIntentUsesCanonicalAutomaticLayout(bool autoPart, bool createMsr)
    {
        var document = Document();
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var result = new SimulationOperationService().Apply(document,
            new(SimulationEditKind.CreateVirtualDisk, pool.StableId,
                Name: "Exact VD", SizeBytes: Size, Resiliency: "Simple", PerformanceDataCopies: 1,
                CreatePartition: autoPart, CreateMsr: createMsr,
                FileSystem: "NTFS", AllocationUnitSize: 65536, VolumeName: "Exact label", DriveLetter: "E",
                QuickFormat: false));

        Assert.True(result.Succeeded, result.Error);
        AssertLayout(result.Document.Snapshot, autoPart, createMsr);
        if (autoPart)
            Assert.Contains(result.Commands, command => command.Contains(" -Full", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticPolicyWithoutALetterKeepsDataVolumeUnassigned()
    {
        var document = Document();
        var pool = document.Snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var create = new SimulationEditRequest(SimulationEditKind.CreateVirtualDisk, pool.StableId,
            Name: "No letter", SizeBytes: Size, Resiliency: "Simple", PerformanceDataCopies: 1,
            AllocatedVirtualDiskId: "sim:vd:no-letter", AllocatedOsDiskId: "sim:os:no-letter");
        var steps = new List<SimulationEditRequest> { create };
        SimulationDraftPlanner.ConfigureAutomaticPartitionIntent(steps, create,
            new(true, true, "NTFS", 65536, "No letter", CreateMsr: false),
            "sim:partition:no-letter", "sim:volume:no-letter", "sim:msr:no-letter");

        var result = new SimulationOperationService().ApplyPlan(document,
            SimulationDraftPlanner.Precheck(document.Snapshot, steps));

        Assert.True(result.Succeeded, result.Error);
        var volume = result.Document.Snapshot.VolumeForPartition("sim:partition:no-letter");
        Assert.NotNull(volume);
        Assert.Empty(volume.DriveLetter);
        Assert.Empty(volume.AccessPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TieredPoolCreationForwardsExplicitMsrPreference(bool createMsr)
    {
        var source = Document();
        var empty = new SimulationOperationService().Apply(source,
            new(SimulationEditKind.DissolveStoragePool,
                source.Snapshot.StoragePools.Single(item => !item.IsPrimordial).StableId));
        Assert.True(empty.Succeeded, empty.Error);
        var members = empty.Document.Snapshot.PhysicalDisks.Where(item => !item.IsBoot && !item.IsSystem)
            .Select(item => item.StableId).ToArray();
        var result = new SimulationOperationService().Apply(empty.Document,
            new(SimulationEditKind.CreateTieredPool, "primordial", Name: "Exact pool",
                MemberDiskIds: members, VirtualDiskName: "Exact VD", SizeBytes: Size,
                PerformanceResiliency: "Simple", PerformanceDataCopies: 1,
                CreatePartition: true, CreateMsr: createMsr, FileSystem: "NTFS",
                AllocationUnitSize: 65536, VolumeName: "Exact label", DriveLetter: "E"));

        Assert.True(result.Succeeded, result.Error);
        AssertLayout(result.Document.Snapshot, true, createMsr);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 18)]
    public void AutomaticLayoutRejectsCapacityWithoutRoomForDataAndBothGptEnds(bool createMsr, int sizeMiB)
    {
        Assert.Throws<ArgumentException>(() => SimulationDraftPlanner.CreateAutomaticPartitionSteps(
            "sim:os:tiny", sizeMiB * MiB,
            new(false, true, "NTFS", 65536, "Tiny", CreateMsr: createMsr)));
    }

    private static StorageSystemDocument Document() => new(StorageSystemDocument.CurrentSchemaVersion,
        "simulation:automatic-layout", StorageSystemKind.Simulation, "Automatic layout",
        SimulationLayouts.SingleDiskPool(), [], DateTimeOffset.Now);

    private static void AssertLayout(StorageSnapshot snapshot, bool autoPart, bool createMsr)
    {
        var virtualDisk = Assert.Single(snapshot.VirtualDisks);
        Assert.Equal("Exact VD", virtualDisk.FriendlyName);
        var disk = Assert.Single(snapshot.OsDisks, item => item.VirtualDiskStableId == virtualDisk.StableId);
        Assert.Equal(Size, disk.Size);
        var partitions = snapshot.Partitions.Where(item => item.OsDiskStableId == disk.StableId).ToArray();
        if (!autoPart)
        {
            Assert.Equal("RAW", disk.PartitionStyle);
            Assert.Empty(partitions);
            Assert.DoesNotContain(snapshot.Volumes, volume => volume.FileSystemLabel == "Exact label");
            return;
        }
        Assert.Equal("GPT", disk.PartitionStyle);
        Assert.Equal(createMsr ? 2 : 1, partitions.Length);
        var reserved = partitions.Where(item => item.Type == "MicrosoftReserved").ToArray();
        if (createMsr)
        {
            var msr = Assert.Single(reserved);
            Assert.Equal(MiB, msr.Offset);
            Assert.Equal(16 * MiB, msr.Size);
            Assert.Null(snapshot.VolumeForPartition(msr.StableId));
        }
        else Assert.Empty(reserved);
        var data = Assert.Single(partitions, item => item.Type is "Primary" or "BasicData");
        var expectedStart = (createMsr ? 17 : 1) * MiB;
        Assert.Equal(expectedStart, data.Offset);
        Assert.Equal(Size - MiB, data.Offset + data.Size);
        Assert.Equal(Size - expectedStart - MiB, data.Size);
        var volume = snapshot.VolumeForPartition(data.StableId);
        Assert.NotNull(volume);
        Assert.Equal("NTFS", volume.FileSystem);
        Assert.Equal(65536, volume.AllocationUnitSize);
        Assert.Equal("Exact label", volume.FileSystemLabel);
        Assert.Equal("E", volume.DriveLetter);
    }
}
