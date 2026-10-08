using System.Collections.Immutable;
using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealAutomaticLayoutProgressTests
{
    [Fact]
    public void CancelBeforeLayoutKeepsTheExactOriginalFirstStep()
    {
        var fixture = new Fixture();
        var next = fixture.Progress.BuildNext(fixture.Fresh(0), fixture.Intent)!;
        Assert.Equal("create-msr", Assert.Single(next.Steps).Id);
        Assert.Equal(OperationIntent.InitializeDisk, next.Intent);
        Assert.IsType<CreatePartitionCommand>(next.Steps[0].Command);
        Assert.Equal(fixture.DiskId, Assert.Single(next.Targets).ProviderKey);
    }

    [Fact]
    public void ProviderMsrDeletionRetainsStrictInitializationIntentAndBothExactTargets()
    {
        var fixture = new Fixture(providerMsr: true);
        var next = fixture.Progress.BuildNext(fixture.Fresh(0), fixture.Intent)!;
        Assert.Equal(OperationIntent.InitializeDisk, next.Intent);
        Assert.Equal("delete-auto-msr", Assert.Single(next.Steps).Id);
        Assert.IsType<DeletePartitionCommand>(next.Steps[0].Command);
        Assert.Contains(next.Targets, target => target.Kind == StorageObjectKind.OsDisk && target.ProviderKey == fixture.DiskId);
        Assert.Contains(next.Targets, target => target.Kind == StorageObjectKind.Partition && target.ProviderKey == "partition:provider-msr");
        Assert.Equal(2, next.Targets.Count);
        RealOperationValidator.Validate(next);
    }

    [Fact]
    public void VerifiedMsrIsNeverDeletedOrRecreatedWhenDataCreationRemains()
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr"));
        var next = fixture.Progress.BuildNext(fixture.Fresh(1), fixture.Intent)!;
        Assert.Equal("create-data", Assert.Single(next.Steps).Id);
        Assert.Equal(RealPartitionRole.BasicData, Assert.IsType<CreatePartitionCommand>(next.Steps[0].Command).Role);
        Assert.Empty(next.Steps[0].DependsOn);
    }

    [Fact]
    public void FailedFormatContinuesAgainstTheExactVerifiedDataPartitionOnly()
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr", "create-data"));
        var next = fixture.Progress.BuildNext(fixture.Fresh(2), fixture.Intent)!;
        Assert.Equal(OperationIntent.FormatVolume, next.Intent);
        var format = Assert.IsType<FormatVolumeCommand>(Assert.Single(next.Steps).Command);
        Assert.Equal(Fixture.DataId, format.Partition.Existing!.Value.ProviderKey);
        Assert.Null(format.Partition.CreatedByStep);
        Assert.Equal("Original label", format.Label);
        Assert.Equal(65536, format.ClusterBytes);
        RealOperationValidator.Validate(next);
    }

    [Fact]
    public void FailedLetterDoesNotReplayVerifiedFormattingAndCompletionHasNoMoreWrites()
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr", "create-data", "format-data"));
        var next = fixture.Progress.BuildNext(fixture.Fresh(2, formatted: true), fixture.Intent)!;
        Assert.Equal(OperationIntent.SetDriveLetter, next.Intent);
        var letter = Assert.IsType<SetDriveLetterCommand>(Assert.Single(next.Steps).Command);
        Assert.Equal(Fixture.DataId, letter.Partition.Existing!.Value.ProviderKey);
        Assert.Equal('E', letter.NewLetter);
        fixture.Progress.Observe(next, Response(next, "assign-data-letter"));
        Assert.Null(fixture.Progress.BuildNext(fixture.Fresh(2, formatted: true, assigned: true), fixture.Intent));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign-disk")]
    [InlineData("changed-geometry")]
    [InlineData("wrong-role")]
    [InlineData("conflicting-role")]
    [InlineData("substituted-identity")]
    [InlineData("unexpected-partition")]
    public void FreshFactsCannotSubstituteAGuessedPartitionForTheVerifiedOutput(string defect)
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr", "create-data"));
        var fresh = fixture.Fresh(2);
        var snapshot = fresh.Snapshot;
        var data = snapshot.Partitions.Single(partition => partition.StableId == Fixture.DataId);
        snapshot = snapshot with { Partitions = defect switch
        {
            "missing" => snapshot.Partitions.Where(partition => partition.StableId != data.StableId).ToArray(),
            "unexpected-partition" => snapshot.Partitions.Append(data with { StableId = "partition:unexpected", Offset = data.Offset + 1048576 }).ToArray(),
            _ => snapshot.Partitions.Select(partition => partition.StableId != data.StableId ? partition : defect switch
            {
                "foreign-disk" => partition with { OsDiskStableId = snapshot.OsDisks.First(disk => disk.StableId != fixture.DiskId).StableId },
                "changed-geometry" => partition with { Size = partition.Size - 1048576 },
                "wrong-role" => partition with { PartitionTypeId = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", GptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" },
                "conflicting-role" => partition with { PartitionTypeId = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" },
                _ => partition with { StableId = "partition:same-name-and-geometry" }
            }).ToArray()
        } };
        Assert.Throws<InvalidDataException>(() => fixture.Progress.BuildNext(fixture.Project(snapshot), fixture.Intent));
    }

    [Fact]
    public void UnknownOutcomeBlocksContinuationEvenWhenCreatedIdentitiesArePresent()
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr", "create-data") with
        { State = RealOperationState.OutcomeUnknown, RequiresReconciliation = true });
        Assert.Throws<InvalidOperationException>(() => fixture.Progress.BuildNext(fixture.Fresh(2), fixture.Intent));
    }

    [Theory]
    [InlineData("label")]
    [InlineData("letter")]
    [InlineData("vd")]
    [InlineData("marker")]
    public void OriginalRemainingParametersCannotBeReplacedDuringContinuation(string change)
    {
        var fixture = new Fixture();
        var changed = change switch
        {
            "label" => fixture.Intent with { VolumeName = "Other" },
            "letter" => fixture.Intent with { DriveLetter = 'F' },
            "vd" => fixture.Intent with { VerifiedVirtualDiskId = "other:vd" },
            _ => fixture.Intent with { PendingAutomaticLayout = false }
        };
        Assert.Throws<InvalidOperationException>(() => fixture.Progress.BuildNext(fixture.Fresh(0), changed));
    }

    [Fact]
    public void VerifiedFormatChangedExternallyIsBlockedInsteadOfReformatted()
    {
        var fixture = new Fixture();
        fixture.Progress.Observe(fixture.Layout, Response(fixture.Layout, "create-msr", "create-data", "format-data"));
        Assert.Throws<InvalidDataException>(() => fixture.Progress.BuildNext(fixture.Fresh(2), fixture.Intent));
    }

    [Fact]
    public void VerifiedCreationRequiresOutputEvidenceAndTheSameSubmittedCommand()
    {
        var fixture = new Fixture();
        var response = Response(fixture.Layout, "create-msr");
        Assert.Throws<InvalidDataException>(() => fixture.Progress.Observe(fixture.Layout, response with
        { Steps = response.Steps.Select(step => step with { ResultEvidence = "{}" }).ToArray() }));
        var next = fixture.Progress.BuildNext(fixture.Fresh(0), fixture.Intent)!;
        var otherDisk = new StorageObjectId(next.SystemId, StorageObjectKind.OsDisk, "disk:unrelated");
        var unrelated = next with { Targets = [otherDisk], Steps = [next.Steps[0] with
        { Command = Assert.IsType<CreatePartitionCommand>(next.Steps[0].Command) with
            { Disk = RealTargetReference.ForExisting(otherDisk) } }] };
        Assert.Throws<InvalidDataException>(() => fixture.Progress.Observe(next, Response(unrelated, "create-msr")));
    }

    private sealed class Fixture
    {
        public const string DataId = "partition:verified-data";
        private const string MsrId = "partition:verified-msr";
        private readonly StorageSystemDocument empty;
        private readonly PartitionInfo template;
        public string DiskId { get; }
        public PoolEditIntent Intent { get; }
        public RealOperationIntentRequest Layout { get; }
        public RealAutomaticLayoutProgress Progress { get; }
        public Fixture(bool providerMsr = false)
        {
            var source = SimulationLayouts.StandardTiered();
            var vd = source.VirtualDisks.First();
            var disk = source.OsDisks.Single(item => item.VirtualDiskStableId == vd.StableId);
            DiskId = disk.StableId;
            template = source.Partitions.First(partition => partition.OsDiskStableId == disk.StableId && partition.Size > (16L << 20));
            var removed = source.Partitions.Where(partition => partition.OsDiskStableId == disk.StableId).Select(partition => partition.StableId).ToHashSet();
            source = source with
            {
                Partitions = source.Partitions.Where(partition => !removed.Contains(partition.StableId)).Concat(providerMsr ? [template with
                {
                    StableId = "partition:provider-msr", PartitionNumber = 1, Type = "Reserved", Offset = 1L << 20, Size = 16L << 20,
                    GptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", PartitionTypeId = "", IsBoot = false, IsSystem = false,
                    DriveLetter = "", FileSystem = "", FileSystemLabel = "", AllocationUnitSize = null, Path = ""
                }] : []).ToArray(),
                Volumes = source.Volumes.Where(volume => !removed.Contains(volume.PartitionStableId ?? "")).ToArray()
            };
            empty = new(StorageSystemDocument.CurrentSchemaVersion, "local:layout-progress-tests", StorageSystemKind.Local,
                "Progress tests", source, [], DateTimeOffset.UtcNow);
            empty = empty with { SourceFacts = WinPoolSimulationFacts.Create(source, empty.SystemId) with { IsSimulation = false } };
            Intent = new(true, true, "NTFS", 65536, "Original label", VirtualDiskName: vd.FriendlyName,
                DriveLetter: 'E', VerifiedVirtualDiskId: vd.StableId, PendingAutomaticLayout: true);
            Layout = RealOperationProposalFactory.ConfigureInitializedDisk(empty.SystemId,
                new(empty.SystemId, StorageObjectKind.OsDisk, disk.StableId), providerMsr
                    ? new StorageObjectId(empty.SystemId, StorageObjectKind.Partition, "partition:provider-msr") : null,
                true, disk.Size, true, Intent.VolumeName, 'E')!;
            Progress = new(Layout, vd.StableId, Intent);
        }

        public StorageSystemDocument Fresh(int createdCount, bool formatted = false, bool assigned = false)
        {
            var snapshot = empty.Snapshot;
            var additions = Layout.Steps.Select(step => step.Command).OfType<CreatePartitionCommand>().Take(createdCount)
                .Select((command, index) => template with
                {
                    StableId = command.Role == RealPartitionRole.Msr ? MsrId : DataId, PartitionNumber = index + 1,
                    Type = command.Role == RealPartitionRole.Msr ? "Reserved" : "Basic",
                    GptType = command.Role == RealPartitionRole.Msr ? "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" : "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}",
                    PartitionTypeId = "", Offset = command.OffsetBytes, Size = command.SizeBytes, IsBoot = false, IsSystem = false,
                    DriveLetter = assigned && command.Role == RealPartitionRole.BasicData ? "E" : "",
                    FileSystem = "", FileSystemLabel = "", AllocationUnitSize = null, Path = ""
                }).ToArray();
            var volumes = formatted ? new[] { new VolumeInfo("volume:verified-data", true, DataId, "NTFS", Intent.VolumeName,
                additions.Single(partition => partition.StableId == DataId).Size, 0, 65536, "Healthy", "OK", assigned ? ["E:\\"] : []) } : [];
            return Project(snapshot with
            { Partitions = snapshot.Partitions.Concat(additions).ToArray(), Volumes = snapshot.Volumes.Concat(volumes).ToArray() });
        }

        public StorageSystemDocument Project(StorageSnapshot snapshot)
        {
            var facts = WinPoolSimulationFacts.Create(snapshot, empty.SystemId) with { IsSimulation = false };
            // Preserve explicitly supplied role evidence, including a deliberate conflict.
            facts = facts with { Objects = facts.Objects.Select(item =>
            {
                var partition = snapshot.Partitions.FirstOrDefault(partition => partition.StableId == item.Id);
                return partition is not null && !string.IsNullOrWhiteSpace(partition.PartitionTypeId)
                    ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("PartitionTypeId", partition.PartitionTypeId,
                        FactValueType.String, item.SourceRef)) } : item;
            }).ToImmutableArray() };
            return empty with { SourceFacts = facts };
        }
    }

    private static AgentRealOperationResponse Response(RealOperationIntentRequest submitted, params string[] verified)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"), 42, now,
            Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine-binding",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        var plan = RealOperationPlanFactory.Create(submitted, OperationId.New(), environment, session,
            "inventory", "target-fingerprint", "physical-fingerprint", "support", now, now.AddMinutes(2));
        return new(plan, verified.Length == submitted.Steps.Count ? RealOperationState.Succeeded : RealOperationState.PartiallyCompleted,
            submitted.Steps.Select(step => new RealOperationStepProgress(step.Id,
                verified.Contains(step.Id) ? RealOperationStepState.Verified : RealOperationStepState.StoppedBeforeCall,
                null, null, step.Command is CreatePartitionCommand create ? JsonSerializer.Serialize(new
                { CreatedObjectId = create.Role == RealPartitionRole.Msr ? "partition:verified-msr" : Fixture.DataId }) : "{}")).ToArray(), null, false);
    }
}
