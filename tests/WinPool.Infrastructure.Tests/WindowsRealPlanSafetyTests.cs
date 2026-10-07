using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealPlanSafetyTests
{
    private const string PhysicalId = "physical:wdc";
    private const string DiskId = "osdisk:wdc";
    private const string PoolId = "pool:old";
    private const string NewPoolId = "pool:new";
    private const string SubsystemId = "subsystem:local";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");

    [Fact]
    public async Task H01RequiresOneGptDiskWithKnownPartitionsAndFreezesRawResult()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitions();
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var proposal = fixture.Proposal(OperationIntent.ClearDisk, [disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        Assert.Equal(RealOperationValidator.ClearDiskExpectedFinalState,
            plan.RealOperation!.ExpectedFinalState);
        Assert.Equal(2, plan.Targets.Count); // The Agent freezes the physical identity.
        Assert.Contains("serial SERIAL-WDC", plan.RealOperation.Steps[0].BeforeCondition);
        Assert.Contains("OS disk 7", plan.RealOperation.Steps[0].BeforeCondition);
        Assert.Contains("step target OsDisk ID " + DiskId,
            plan.RealOperation.Steps[0].BeforeCondition);
        _ = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(0, fixture.Adapter.CallCount);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Prepare(proposal with
        {
            Targets = [disk, fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]
        }));
        fixture.SetRawDisk();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("de94bba4-06d1-4d40-a16a-bfd50179d6ac")]
    [InlineData("unknown")]
    public async Task ClearRejectsUnknownAndRecoveryPartitionRoles(string role)
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionsWithUnsupportedRole(role);
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var proposal = fixture.Proposal(OperationIntent.ClearDisk, [disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task ClearAllowsHiddenGptMsrButRejectsHiddenBasicData()
    {
        var fixture = new Fixture();
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var proposal = fixture.Proposal(OperationIntent.ClearDisk, [disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);

        fixture.SetGptPartitions(hiddenMsr: true);
        var plan = await fixture.Prepare(proposal);
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        Assert.Equal(0, fixture.Adapter.CallCount);

        fixture.SetGptPartitions(hiddenMsr: true, hiddenData: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RealFinalGapMaximumPreparesWhileOldDisplayMaximumExceedsGptTailReserve()
    {
        const long diskSize = 4_000_787_030_016;
        const long offset = 17L << 20;
        var fixture = new Fixture();
        fixture.SetGptWithCanonicalMsr(diskSize);
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var geometry = EditWorkspace.GetRealPartitionCreateGeometry(diskSize, offset, diskSize - offset);
        Assert.True(geometry.CanCreate);
        Assert.Equal(3_815_429L << 20, geometry.MaximumSizeBytes);
        var proposal = fixture.Proposal(OperationIntent.CreatePartition, [disk],
            [Step("create", new CreatePartitionCommand(RealTargetReference.ForExisting(disk),
                RealPartitionRole.BasicData, offset, geometry.MaximumSizeBytes!.Value))],
            "Maximum whole-MiB data partition in the exact observed GPT gap");

        var plan = await fixture.Prepare(proposal);
        var frozen = Assert.IsType<CreatePartitionCommand>(Assert.Single(plan.RealOperation!.Steps).Command);
        Assert.Equal(geometry.MaximumSizeBytes, frozen.SizeBytes);
        Assert.Equal(offset, frozen.OffsetBytes);
        var rejected = proposal with { Steps = [Step("create", frozen with { SizeBytes = 3_815_430L << 20 })] };
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(rejected));
        Assert.Equal("The partition does not fit an observed usable GPT gap.", exception.Message);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task UnverifiedRefsCreationIsRejectedBeforeAnyWindowsCall()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitions();
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var partition = RealTargetReference.FromStep(StorageObjectKind.Partition, "create");
        var proposal = fixture.Proposal(OperationIntent.CreatePartition, [disk],
            [Step("create", new CreatePartitionCommand(RealTargetReference.ForExisting(disk),
                RealPartitionRole.BasicData, 145L << 20, 64L << 20)),
             Step("format", new FormatVolumeCommand(partition, RealFileSystem.ReFs,
                 65536, false, "UNVERIFIED"), ["create"])],
            "New ReFS data partition");

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task OmittedFormatLabelIsPreviewedAsEmpty()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitions();
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");
        var proposal = fixture.Proposal(OperationIntent.FormatVolume, [partition],
            [Step("format", new FormatVolumeCommand(
                RealTargetReference.ForExisting(partition), RealFileSystem.Ntfs,
                65536, false, null))], "Selected partition formatted");

        var plan = await fixture.Prepare(proposal);
        Assert.Contains("label (empty)", plan.RealOperation!.Steps[0].AfterCondition);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task ResizeRangeUsesFreshProviderBoundsForExactDataPartition()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitions();
        fixture.PartitionSizes.MinimumBytes = 64L << 20;
        fixture.PartitionSizes.MaximumBytes = 200L << 20;
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");

        var range = await fixture.ReadResizeRange(partition);

        Assert.Equal(partition, range.Partition);
        Assert.Equal(128L << 20, range.CurrentSizeBytes);
        Assert.Equal(64L << 20, range.AllowedMinBytes);
        Assert.Equal(200L << 20, range.AllowedMaxBytes);
        Assert.Equal(1, fixture.PartitionSizes.ReadCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadResizeRange(
            new StorageObjectId(SystemId.New(), StorageObjectKind.Partition, "partition:data")));

        fixture.PartitionSizes.OnRead = fixture.SetRawDisk;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadResizeRange(partition));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DirectResizeProposalCannotExceedAdjacentPartitionGeometry()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionsWithFollowingPartition();
        fixture.PartitionSizes.MinimumBytes = 64L << 20;
        fixture.PartitionSizes.MaximumBytes = 512L << 20;
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");
        var range = await fixture.ReadResizeRange(partition);
        Assert.Equal(283L << 20, range.AllowedMaxBytes);

        var proposal = fixture.Proposal(OperationIntent.ResizePartition, [partition],
            [Step("resize", new ResizePartitionCommand(
                RealTargetReference.ForExisting(partition), 400L << 20))],
            "Selected partition resized");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task ResizeRejectsAnUnsupportedAssociatedVolumeEvenWhenPartitionFormatIsBlank()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionWithRefsVolume();
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.ReadResizeRange(partition));
        var proposal = fixture.Proposal(OperationIntent.ResizePartition, [partition],
            [Step("resize", new ResizePartitionCommand(
                RealTargetReference.ForExisting(partition), 96L << 20))],
            "Selected partition resized");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.PartitionSizes.ReadCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task PrepareRejectsALogicalDriveLetterMissingFromStorageSnapshot()
    {
        var fixture = new Fixture { LogicalDrives = [@"W:\"] };
        fixture.SetGptPartitions();
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");
        var proposal = fixture.Proposal(OperationIntent.SetDriveLetter, [partition],
            [Step("letter", new SetDriveLetterCommand(
                RealTargetReference.ForExisting(partition), null, 'W'))],
            "Selected partition assigned W:");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task A11RebuildCanPreflightExactPoolRemovalThenSameMemberCreation()
    {
        var fixture = new Fixture();
        fixture.SetOldPool();
        var oldPool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var proposal = fixture.Proposal(OperationIntent.RebuildStoragePool, [oldPool, physical],
            [Step("remove", new DeletePoolCommand(RealTargetReference.ForExisting(oldPool))),
             Step("create", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "New Pool"), ["remove"])],
            "Old pool removed; new pool created");

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        _ = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);

        fixture.SetRawDisk();
        var verified = await fixture.VerifiedEvidence(NewlyCreatedObjectId: null);
        var next = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation.Steps[1],
            new Dictionary<string, string> { ["remove"] = verified }, CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(next.TargetEvidenceJson);
        Assert.Equal(PhysicalId, target!.UniqueId);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task VirtualDiskRemovalPreviewNamesItsExactChildOsDisk()
    {
        var fixture = new Fixture();
        fixture.SetOldPool(withChild: true);
        var virtualDisk = fixture.Id(StorageObjectKind.VirtualDisk, "vd:child");
        var proposal = fixture.Proposal(OperationIntent.DeleteVirtualDisk, [virtualDisk],
            [Step("remove", new DeleteVirtualDiskCommand(
                RealTargetReference.ForExisting(virtualDisk)))],
            "Selected virtual disk removed");

        var plan = await fixture.Prepare(proposal);
        Assert.Contains("OS disk ID osdisk:child", plan.RealOperation!.Steps[0].BeforeCondition);
        Assert.Contains("OS disk osdisk:child (#9)", plan.RealOperation.Steps[0].DataLoss);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task A12CreationUsesCurrentSizeBoundsAndVerifiedCreatedPoolIdentity()
    {
        var fixture = new Fixture();
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var created = RealTargetReference.FromStep(StorageObjectKind.StoragePool, "pool");
        var proposal = fixture.Proposal(OperationIntent.CreateStoragePool, [physical],
            [Step("pool", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "Pool")),
             Step("vd", new CreateVirtualDiskCommand(created, "Data", 256L << 20, 65536, 1), ["pool"])],
            "Pool and virtual disk created");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Prepare(proposal with
        {
            Steps = [proposal.Steps[0], proposal.Steps[1] with
            {
                Command = new CreateVirtualDiskCommand(created, "Data", 256L << 20, 262144, 1)
            }]
        }));

        var plan = await fixture.Prepare(proposal);
        Assert.Equal(0, fixture.Sizes.ReadCount); // The new pool is not queried before it exists.
        fixture.SetNewPool();
        var verified = await fixture.VerifiedEvidence(NewPoolId);
        var step = plan.RealOperation!.Steps[1];
        _ = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string> { ["pool"] = verified }, CancellationToken.None);
        Assert.Equal(1, fixture.Sizes.ReadCount);
        Assert.Equal(0, fixture.Adapter.CallCount);

        var wrongIdentity = await fixture.VerifiedEvidence("pool:other");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            step, new Dictionary<string, string> { ["pool"] = wrongIdentity }, CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);

        fixture.Sizes.MaximumBytes = 128L << 20;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            step, new Dictionary<string, string> { ["pool"] = verified }, CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DeleteWithUnlistedChildAndUnchangedRenameStopBeforeAdapter()
    {
        var fixture = new Fixture();
        fixture.SetOldPool(withChild: true);
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var delete = fixture.Proposal(OperationIntent.DeleteStoragePool, [pool],
            [Step("remove", new DeletePoolCommand(RealTargetReference.ForExisting(pool)))],
            "Pool removed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(delete));

        fixture.SetOldPool();
        var rename = fixture.Proposal(OperationIntent.RenameStorageObject, [pool],
            [Step("rename", new RenamePoolCommand(RealTargetReference.ForExisting(pool), "Old Pool"))],
            "Pool renamed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(rename));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static RealOperationStep Step(string id, RealStorageCommand command,
        IReadOnlyList<string>? dependencies = null) => new(id, command, dependencies ?? [],
        "fresh identity", "typed result", "explicit data loss", "synthetic provider facts");

    [Fact]
    public async Task ExistingRefsFormatRequiresCurrentCapabilityAtPrepareAndPreflight()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionWithRefsVolume();
        fixture.Capabilities.RefsSupported = true;
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");
        var proposal = fixture.Proposal(OperationIntent.FormatVolume, [partition],
            [Step("format", new FormatVolumeCommand(RealTargetReference.ForExisting(partition),
                RealFileSystem.ReFs, 65536, false, "REFS"))], "ReFS quick format");
        var plan = await fixture.Prepare(proposal);
        Assert.Contains("live-refs-capability", plan.RealOperation!.Steps[0].SupportEvidence);
        var preflight = await fixture.Backend.PreflightStepAsync(plan,
            plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        Assert.Equal("volume:refs", target.RelatedUniqueId);
        fixture.Capabilities.RefsSupported = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Backend.PreflightStepAsync(plan,
            plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task RefsRequiresBothSuccessfulProviderMethodsAnd64KiB(int returnValue, bool hasCluster)
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionWithRefsVolume();
        fixture.Capabilities.RefsSupported = true;
        fixture.Capabilities.ReturnValue = (uint)returnValue;
        fixture.Capabilities.Has64KiB = hasCluster;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.ReadResizeRange(
            fixture.Id(StorageObjectKind.Partition, "partition:data")));
        Assert.Equal(0, fixture.PartitionSizes.ReadCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task VerifiedRefsAllowsExtensionButNeverShrink()
    {
        var fixture = new Fixture();
        fixture.SetGptPartitionWithRefsVolume();
        fixture.Capabilities.RefsSupported = true;
        var partition = fixture.Id(StorageObjectKind.Partition, "partition:data");
        var range = await fixture.ReadResizeRange(partition);
        Assert.Equal(range.CurrentSizeBytes, range.AllowedMinBytes);
        var extend = fixture.Proposal(OperationIntent.ResizePartition, [partition],
            [Step("resize", new ResizePartitionCommand(RealTargetReference.ForExisting(partition), 192L << 20))], "ReFS larger");
        _ = await fixture.Prepare(extend);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(extend with
        {
            Steps = [Step("resize", new ResizePartitionCommand(RealTargetReference.ForExisting(partition), 96L << 20))]
        }));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task HddTemplateCreationRequiresCurrentSubsystemSupportAndEmptyPool()
    {
        var fixture = new Fixture();
        fixture.SetOldPool();
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var proposal = fixture.Proposal(OperationIntent.CreateStorageTier, [pool],
            [Step("tier", new CreateTierCommand(RealTargetReference.ForExisting(pool), "HDD", 65536, 1))], "HDD template");
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Prepare(proposal));
        fixture.Capabilities.TierSupported = true;
        var plan = await fixture.Prepare(proposal);
        Assert.Contains("no allocated size", plan.RealOperation!.Steps[0].AfterCondition);
        fixture.Capabilities.TierSupported = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Backend.PreflightStepAsync(plan,
            plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        fixture.Capabilities.TierSupported = true;
        fixture.SetOldPool(withChild: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(proposal));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task TieredCreationUsesExactUnusedTemplateRangeAndRetainsC05Barrier()
    {
        var fixture = new Fixture();
        fixture.SetHddTemplate();
        fixture.Capabilities.TierSupported = true;
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:hdd");
        var proposal = fixture.Proposal(OperationIntent.CreateVirtualDisk, [pool, tier],
            [Step("vd", new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool),
                RealTargetReference.ForExisting(tier), "Data", 256L << 20))], "Tiered VD");
        var plan = await fixture.Prepare(proposal);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal("tier:hdd", JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!.RelatedObjectId);
        fixture.Capabilities.TierMaximum = 128L << 20;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        var resize = fixture.Proposal(OperationIntent.ResizeStorageTier, [tier],
            [Step("resize", new ResizeTierCommand(RealTargetReference.ForExisting(tier), 512L << 20))], "Larger tier");
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Prepare(resize));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TieredCreationPreparesAndRechecksLegalEnumerationOrOffsetRange(bool enumeration)
    {
        var fixture = new Fixture();
        fixture.SetHddTemplate();
        fixture.Capabilities.TierSupported = true;
        fixture.Capabilities.TierCreationSize = WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U,
            enumeration ? new ulong[] { 160UL << 20, 224UL << 20 } : null,
            enumeration ? null : 96UL << 20, enumeration ? null : 224UL << 20,
            enumeration ? null : 64UL << 20);
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:hdd");
        var proposal = fixture.Proposal(OperationIntent.CreateVirtualDisk, [pool, tier],
            [Step("vd", new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool),
                RealTargetReference.ForExisting(tier), "Data", 160L << 20))], "Tiered VD");
        var plan = await fixture.Prepare(proposal);
        _ = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        fixture.Capabilities.TierCreationSize = WindowsRealStorageCapabilityReader.ParseTierCreationSize(0U,
            new ulong[] { 224UL << 20 }, null, null, null);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan,
            plan.RealOperation.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        var resize = fixture.Proposal(OperationIntent.ResizeStorageTier, [tier],
            [Step("resize", new ResizeTierCommand(RealTargetReference.ForExisting(tier), 256L << 20))], "Larger tier");
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Prepare(resize));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task HddRenameAndRemovalAreSeparateExactSupportedPlans()
    {
        var fixture = new Fixture();
        fixture.SetHddTemplate();
        fixture.Capabilities.TierSupported = true;
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:hdd");
        var rename = fixture.Proposal(OperationIntent.RenameStorageObject, [tier],
            [Step("rename", new RenameTierCommand(RealTargetReference.ForExisting(tier), "Renamed"))], "Renamed tier");
        _ = await fixture.Prepare(rename);
        var delete = fixture.Proposal(OperationIntent.DeleteStorageTier, [tier],
            [Step("remove", new DeleteTierCommand(RealTargetReference.ForExisting(tier)))], "Template removed");
        _ = await fixture.Prepare(delete);
        fixture.Capabilities.TierSupported = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Prepare(delete));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DissolveRequiresExplicitVdThenTierThenPoolRemoval()
    {
        var fixture = new Fixture();
        fixture.SetHddTemplate(withVirtualDisk: true);
        fixture.Capabilities.TierSupported = true;
        var pool = fixture.Id(StorageObjectKind.StoragePool, PoolId);
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:hdd");
        var vd = fixture.Id(StorageObjectKind.VirtualDisk, "vd:child");
        var missingTier = fixture.Proposal(OperationIntent.DeleteStoragePool, [pool, vd],
            [Step("vd", new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(vd))),
             Step("pool", new DeletePoolCommand(RealTargetReference.ForExisting(pool)), ["vd"])], "Dissolved");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(missingTier));
        var deleteTierFirst = fixture.Proposal(OperationIntent.DeleteStorageTier, [tier],
            [Step("tier", new DeleteTierCommand(RealTargetReference.ForExisting(tier)))], "Removed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(deleteTierFirst));
        var complete = missingTier with
        {
            Targets = [pool, vd, tier],
            Steps = [missingTier.Steps[0],
                Step("tier", new DeleteTierCommand(RealTargetReference.ForExisting(tier)), ["vd"]),
                Step("pool", new DeletePoolCommand(RealTargetReference.ForExisting(pool)), ["vd", "tier"])]
        };
        _ = await fixture.Prepare(complete);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private sealed class Fixture
    {
        private readonly SystemId system = SystemId.New();
        private StorageSnapshot snapshot;
        private readonly WindowsRealStorageTopologyReader reader;
        public Fixture()
        {
            snapshot = BaseSnapshot();
            reader = new WindowsRealStorageTopologyReader(
                new SyntheticFactSource(() => Document()), new SyntheticMachineIdentity(),
                new FixedTimeProvider());
            Adapter = new ForbiddenAdapter();
            Sizes = new SyntheticSizeReader();
            PartitionSizes = new SyntheticPartitionSizeReader();
            Capabilities = new SyntheticCapabilities();
            var planner = new WindowsRealOperationPlanner(reader, PartitionSizes,
                new AdministratorPrivilege(), new FixedTimeProvider(), new SyntheticSafetyInspector(), Sizes,
                () => LogicalDrives, Capabilities);
            Backend = new WindowsRealStorageBackend(Adapter, planner, reader, new FixedTimeProvider());
        }

        public WindowsRealStorageBackend Backend { get; }
        public ForbiddenAdapter Adapter { get; }
        public SyntheticSizeReader Sizes { get; }
        public SyntheticPartitionSizeReader PartitionSizes { get; }
        public SyntheticCapabilities Capabilities { get; }
        public IReadOnlyList<string> LogicalDrives { get; set; } = [];
        public StorageObjectId Id(StorageObjectKind kind, string key) => new(system, kind, key);
        public RealOperationIntentRequest Proposal(OperationIntent intent,
            IReadOnlyList<StorageObjectId> targets, IReadOnlyList<RealOperationStep> steps,
            string final) => new(intent, system, targets, steps, final);
        public Task<OperationPlan> Prepare(RealOperationIntentRequest proposal) => Backend.PrepareAsync(
            proposal, new TrustedRealSession(SessionId.New(), "synthetic-product", "synthetic-process",
                1234, Now.AddMinutes(-1), @"C:\Synthetic\WinPool.Agent.exe", true),
            OperationId.New(), CancellationToken.None);
        public Task<RealPartitionResizeRange> ReadResizeRange(StorageObjectId partition) =>
            Backend.ReadPartitionResizeRangeAsync(partition,
                new TrustedRealSession(SessionId.New(), "synthetic-product", "synthetic-process",
                    1234, Now.AddMinutes(-1), @"C:\Synthetic\WinPool.Agent.exe", true),
                CancellationToken.None);

        public async Task<string> VerifiedEvidence(string? NewlyCreatedObjectId)
        {
            var topology = await reader.CaptureAsync(CancellationToken.None);
            var closure = topology.RequireSinglePhysicalClosure([Id(StorageObjectKind.PhysicalDisk, PhysicalId)]);
            return JsonSerializer.Serialize(new WindowsVerifiedStepEvidence(
                closure.Fingerprint, closure.PhysicalMemberFingerprint,
                NewlyCreatedObjectId, "synthetic-provider-return"));
        }

        public void SetRawDisk() => snapshot = BaseSnapshot();
        public void SetGptWithCanonicalMsr(long diskSize)
        {
            SetGptPartitions();
            snapshot = snapshot with
            {
                OsDisks = [snapshot.OsDisks[0] with { Size = diskSize }],
                PhysicalDisks = [snapshot.PhysicalDisks[0] with { Size = diskSize }],
                StoragePools = [snapshot.StoragePools[0] with { Size = diskSize }],
                Partitions = [snapshot.Partitions[0]]
            };
        }
        public void SetGptPartitions(bool hiddenMsr = false, bool hiddenData = false)
        {
            var msr = Partition("partition:msr", 1, 1L << 20, 16L << 20,
                "e3c9e316-0b5c-4db8-817d-f92df00215ae") with
                { IsHidden = hiddenMsr };
            var data = Partition("partition:data", 2, 17L << 20, 128L << 20,
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7") with
                { IsHidden = hiddenData };
            snapshot = BaseSnapshot() with
            {
                OsDisks = [BaseSnapshot().OsDisks[0] with { PartitionStyle = "GPT" }],
                Partitions = [msr, data]
            };
        }
        public void SetGptPartitionsWithFollowingPartition()
        {
            SetGptPartitions();
            snapshot = snapshot with
            {
                Partitions = [..snapshot.Partitions,
                    Partition("partition:following", 3, 300L << 20, 64L << 20,
                        "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")]
            };
        }

        public void SetGptPartitionsWithUnsupportedRole(string role)
        {
            SetGptPartitions();
            snapshot = snapshot with
            {
                Partitions = [snapshot.Partitions[0],
                    snapshot.Partitions[1] with { PartitionTypeId = role, GptType = role }]
            };
        }

        public void SetGptPartitionWithRefsVolume()
        {
            SetGptPartitions();
            snapshot = snapshot with
            {
                Computer = snapshot.Computer with { WindowsProductName = "Windows 10 Pro for Workstations" },
                Volumes = [new VolumeInfo("volume:refs", true, "partition:data",
                    "ReFS", "", 128L << 20, 128L << 20, 65536,
                    "Healthy", "OK", [@"E:\"])]
            };
        }

        public void SetOldPool(bool withChild = false) => SetPool(PoolId, "Old Pool", withChild);
        public void SetNewPool() => SetPool(NewPoolId, "Pool", false);
        public void SetHddTemplate(bool withVirtualDisk = false)
        {
            SetOldPool(withChild: withVirtualDisk);
            snapshot = snapshot with
            {
                StorageTiers = withVirtualDisk
                    ? [new StorageTierInfo("tier:hdd", true, "HDD", "HDD", "Simple", 0, 0, PoolId, null,
                            [PhysicalId], NumberOfColumns: 1, Interleave: 65536),
                        new StorageTierInfo("tier:instance", true, "HDD", "HDD", "Simple", 256L << 20, 256L << 20,
                            PoolId, "vd:child", [PhysicalId], NumberOfColumns: 1, Interleave: 65536)]
                    : [new StorageTierInfo("tier:hdd", true, "HDD", "HDD", "Simple", 0, 0, PoolId, null,
                        [PhysicalId], NumberOfColumns: 1, Interleave: 65536)],
                VirtualDisks = withVirtualDisk ? [snapshot.VirtualDisks[0] with { TierStableIds = ["tier:instance"] }] : []
            };
        }
        private void SetPool(string id, string name, bool withChild)
        {
            var baseline = BaseSnapshot();
            var physical = baseline.PhysicalDisks[0] with
            {
                CanPool = false, CannotPoolReason = "In a pool", PoolStableId = id
            };
            var pool = new StoragePoolInfo(id, true, name, false, "Healthy", "OK",
                1L << 30, 0, SubsystemId, [PhysicalId]);
            var child = new VirtualDiskInfo("vd:child", true, "Child", "Healthy", "OK",
                "Simple", "Fixed", 1, 65536, 256L << 20, 256L << 20,
                id, [], []);
            snapshot = baseline with
            {
                PhysicalDisks = [physical],
                StoragePools = [baseline.StoragePools[0], pool],
                VirtualDisks = withChild ? [child] : [],
                OsDisks = withChild
                    ? [..baseline.OsDisks, new OsDiskInfo("osdisk:child", "Child", 9,
                        "RAW", 256L << 20, false, false, false, null, child.StableId)]
                    : baseline.OsDisks
            };
        }

        private StorageSystemDocument Document()
        {
            var facts = WinPoolSimulationFacts.Create(snapshot, system);
            facts = facts with { Relationships = facts.Relationships.AddRange(snapshot.StorageTiers
                .Where(tier => tier.VirtualDiskStableId is null).SelectMany(tier => tier.MemberPhysicalDiskIds
                    .Select(member => new WinPoolFactRelationship(tier.StableId, member, "template-pool-member", Now)))) };
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[]
                     { "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage));
            }
            var objects = facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned(
                    "Path", @"\\.\PHYSICALDRIVE7", FactValueType.String, item.SourceRef)) }
                : item).ToImmutableArray();
            facts = facts with
            {
                IsSimulation = false, InventoryVersion = "synthetic-inventory",
                InventoryCapturedAt = Now, Sources = sources, Objects = objects
            };
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
                "local:synthetic", StorageSystemKind.Local, "Synthetic Storage", facts, [], Now)
            { SystemId = system };
        }

        private static StorageSnapshot BaseSnapshot() => StorageSnapshot.Empty(Environment.MachineName) with
        {
            SnapshotVersion = "synthetic-inventory", ScannedAt = Now,
            Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
                "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
            StorageSubsystems = [new StorageSubsystemInfo(SubsystemId, "Storage Spaces", "Healthy", "OK")],
            PhysicalDisks = [new PhysicalDiskInfo(PhysicalId, true, "WDC Test", "Synthetic",
                "SERIAL-WDC", "SATA", "HDD", 1L << 30, 512, 4096,
                "Healthy", "OK", true, "", 7, false, false, false, false, "pool:primordial")],
            StoragePools = [new StoragePoolInfo("pool:primordial", true, "Primordial", true,
                "Healthy", "OK", 1L << 30, 0, SubsystemId, [PhysicalId])],
            OsDisks = [new OsDiskInfo(DiskId, "WDC Test", 7, "RAW", 1L << 30,
                false, false, false, PhysicalId, null)]
        };

        private static PartitionInfo Partition(string id, int number, long offset, long size,
            string type) => new(id, true, 7, number, "GPT", offset, size,
            false, false, "", "", "", null, size, "Healthy", "OK", "", DiskId,
            PartitionTypeId: type,
            Guid: number switch
            {
                1 => "2f8ae502-1e4e-4d94-b190-6284ccb62bea",
                3 => "35392fea-1075-4a47-b490-2bde5fb5c19d",
                _ => "8c4b7c34-04ba-46b1-80d7-9d967441a02d"
            },
            GptType: type);
    }

    private sealed class SyntheticFactSource(Func<StorageSystemDocument> capture) : IWindowsRealStorageFactSource
    {
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken token) => Task.FromResult(capture());
    }
    private sealed class SyntheticMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken token) => Task.FromResult("synthetic-machine-binding");
    }
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class SyntheticPartitionSizeReader : IPartitionSupportedSizeReader
    {
        public long MinimumBytes { get; set; } = 64L << 20;
        public long MaximumBytes { get; set; } = 512L << 20;
        public Action? OnRead { get; set; }
        public int ReadCount { get; private set; }
        public Task<PartitionSupportedSize> ReadAsync(WindowsStorageCommandTarget target,
            CancellationToken token)
        {
            ReadCount++;
            OnRead?.Invoke();
            return Task.FromResult(new PartitionSupportedSize(MinimumBytes, MaximumBytes));
        }
    }
    private sealed class SyntheticSizeReader : IVirtualDiskCreationSizeReader
    {
        public long MaximumBytes { get; set; } = 512L << 20;
        public int ReadCount { get; private set; }
        public Task<VirtualDiskCreationSize> ReadAsync(WindowsStorageCommandTarget target,
            CancellationToken token)
        {
            ReadCount++;
            return Task.FromResult(new VirtualDiskCreationSize(64L << 20, MaximumBytes, 64L << 20, []));
        }
    }
    private sealed class SyntheticSafetyInspector : IWindowsRealStorageSafetyInspector
    {
        public Task ValidateAsync(WindowsRealStorageTopology topology, RealTargetClosure closure,
            RealStorageCommand command, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class SyntheticCapabilities : IWindowsRealStorageCapabilityReader
    {
        public bool RefsSupported { get; set; }
        public bool TierSupported { get; set; }
        public bool Has64KiB { get; set; } = true;
        public uint ReturnValue { get; set; }
        public long TierMaximum { get; set; } = 512L << 20;
        public VirtualDiskCreationSize? TierCreationSize { get; set; }
        public Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(
            WindowsRealStorageTopology topology, StorageObjectId volume, CancellationToken token)
        {
            var closure = topology.RequireSinglePhysicalClosure([volume]);
            var partition = topology.Snapshot.Volumes.Single(item => item.StableId == volume.ProviderKey).PartitionStableId;
            return Task.FromResult(new WindowsVolumeFormatCapability("queried", volume.ProviderKey,
                partition, closure.PhysicalDiskId, volume.ProviderKey, volume.ProviderKey, Now,
                closure.PhysicalMemberFingerprint,
                new WindowsCapabilityMethodResult("returned", ReturnValue,
                    new Dictionary<string, JsonElement?> { ["SupportedFileSystems"] = JsonSerializer.SerializeToElement(RefsSupported ? new[] { "NTFS", "ReFS" } : ["NTFS"]) }, null),
                new WindowsCapabilityMethodResult("returned", ReturnValue,
                    new Dictionary<string, JsonElement?> { ["SupportedClusterSizes"] = JsonSerializer.SerializeToElement(Has64KiB ? new[] { 4096, 65536 } : [4096]) }, null), null));
        }
        public Task<WindowsTierCapability> ReadTierAsync(WindowsRealStorageTopology topology,
            StorageObjectId physical, CancellationToken token) => Task.FromResult(new WindowsTierCapability(
                "queried", physical.ProviderKey, SubsystemId, SubsystemId, SubsystemId, Now, [],
                new[] { "SupportsStorageTierCreation", "SupportsStorageTieredVirtualDiskCreation",
                    "SupportsStorageTierDeletion", "SupportsStorageTierFriendlyNameModification" }
                    .Select(name => WinPoolSourceField.Returned(name, TierSupported, FactValueType.Boolean, SubsystemId))
                    .Append(WinPoolSourceField.Returned("PhysicalDisksPerStoragePoolMin", 1UL, FactValueType.UInt64, SubsystemId)).ToArray(), null));
        public Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(WindowsRealStorageTopology topology,
            StorageObjectId tier, CancellationToken token) => Task.FromResult(TierCreationSize
                ?? new VirtualDiskCreationSize(64L << 20, TierMaximum, 64L << 20, []));
    }
    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }
    private sealed class ForbiddenAdapter : IWindowsRealStorageCommandAdapter
    {
        public int CallCount { get; private set; }
        public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command,
            WindowsStorageCommandTarget target, CancellationToken token)
        {
            CallCount++;
            throw new InvalidOperationException("Synthetic tests may not dispatch storage commands.");
        }
    }
}
