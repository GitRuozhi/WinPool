using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealPoolProposalFactoryTests
{
    [Theory]
    [InlineData("initialize")]
    [InlineData("online")]
    [InlineData("create-partition")]
    [InlineData("format")]
    [InlineData("resize")]
    [InlineData("delete-partition")]
    public void ManualPoolBackedDiskActionsDeclareEveryMemberWithoutChangingTheirCommands(string action)
    {
        var snapshot = SimulationLayouts.StandardTiered();
        snapshot = snapshot with { StoragePools = snapshot.StoragePools.Select(item => item with { IsStable = true }).ToArray() };
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var vd = snapshot.VirtualDisks.Single(item => item.PoolStableId == pool.StableId);
        var disk = snapshot.OsDisks.Single(item => item.VirtualDiskStableId == vd.StableId);
        var partition = snapshot.Partitions.First(item => item.OsDiskStableId == disk.StableId);
        var diskTarget = Id(StorageObjectKind.OsDisk, disk.StableId);
        var partitionTarget = Id(StorageObjectKind.Partition, partition.StableId);
        var diskReference = RealTargetReference.ForExisting(diskTarget);
        var partitionReference = RealTargetReference.ForExisting(partitionTarget);
        var (intent, command, target) = action switch
        {
            "initialize" => (OperationIntent.InitializeDisk, (RealStorageCommand)new InitializeGptCommand(diskReference), diskTarget),
            "online" => (OperationIntent.SetDiskOnlineState, new SetDiskOnlineCommand(diskReference, true), diskTarget),
            "create-partition" => (OperationIntent.CreatePartition, new CreatePartitionCommand(diskReference, RealPartitionRole.BasicData, 1L << 20, 64L << 20), diskTarget),
            "format" => (OperationIntent.FormatVolume, new FormatVolumeCommand(partitionReference, RealFileSystem.Ntfs, 65536, false, "Label"), partitionTarget),
            "resize" => (OperationIntent.ResizePartition, new ResizePartitionCommand(partitionReference, 128L << 20), partitionTarget),
            _ => (OperationIntent.DeletePartition, new DeletePartitionCommand(partitionReference), partitionTarget)
        };
        var original = RealOperationProposalFactory.OneStep(_system, intent, target, command, "result", "impact");
        var proposal = RealOperationProposalFactory.DeclareCurrentPoolMembers(original, snapshot);
        Assert.Equal(original.Steps, proposal.Steps);
        Assert.Equal(original.Intent, proposal.Intent);
        Assert.True(proposal.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).Select(item => item.ProviderKey)
            .ToHashSet().SetEquals(pool.MemberPhysicalDiskIds));
        Assert.Single(proposal.Targets, item => item.Kind != StorageObjectKind.PhysicalDisk);
        RealOperationValidator.Validate(proposal);
    }

    private const long GiB = MaximumCapacityAlgorithm.GiB;
    private readonly SystemId _system = SystemId.New();
    private StorageObjectId Id(StorageObjectKind kind, string key) => new(_system, kind, key);

    [Fact]
    public void CreatePoolFreezesEveryOrderedPhysicalMember()
    {
        var first = Id(StorageObjectKind.PhysicalDisk, "first");
        var second = Id(StorageObjectKind.PhysicalDisk, "second");
        var input = new[] { first, second };
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(_system, input, "Pool");
        input[1] = Id(StorageObjectKind.PhysicalDisk, "changed");
        Assert.Equal(new[] { first, second }, proposal.Targets);
        var command = Assert.IsType<CreatePoolCommand>(Assert.Single(proposal.Steps).Command);
        Assert.Equal(RealTargetReference.ForExisting(first), command.PhysicalDisk);
        Assert.Equal(new[] { first, second }, command.EffectiveMembers.Select(member => member.Existing!.Value));
        RealOperationValidator.Validate(proposal);
    }

    [Theory]
    [InlineData("HDD", "Simple", 1, 1, 0)]
    [InlineData("SSD", "Mirror", 1, 2, 1)]
    [InlineData("SCM", "Parity", 3, 1, 1)]
    public void TierTemplateCarriesRequestedMediaLayoutAndCompleteAuthorizationSet(
        string media, string resiliency, int columns, int copies, int redundancy)
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        StorageObjectId[] members = [Id(StorageObjectKind.PhysicalDisk, "a"), Id(StorageObjectKind.PhysicalDisk, "b"), Id(StorageObjectKind.PhysicalDisk, "c")];
        var layout = new StorageLayoutParameters(resiliency, columns, 16384, copies, redundancy);
        var proposal = RealOperationProposalFactory.CreateTierTemplate(_system, pool, members, "Tier", media, layout);
        Assert.Equal(new[] { pool }.Concat(members), proposal.Targets);
        var command = Assert.IsType<CreateTierCommand>(Assert.Single(proposal.Steps).Command);
        Assert.Equal(media, command.EffectiveMediaType);
        Assert.Equal(layout, command.EffectiveLayout);
        Assert.Equal(16384, command.InterleaveBytes);
        Assert.Equal(columns, command.DataColumns);
    }

    [Fact]
    public void UsageProposalCarriesExactPoolAndCompleteMembersAndRejectsForeignTarget()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        StorageObjectId[] members = [Id(StorageObjectKind.PhysicalDisk, "a"), Id(StorageObjectKind.PhysicalDisk, "b")];
        var proposal = RealOperationProposalFactory.SetPhysicalDiskUsage(_system, members[1], pool, members, PhysicalDiskUsage.HotSpare);
        Assert.Equal(OperationIntent.SetPhysicalDiskUsage, proposal.Intent);
        Assert.Equal(new[] { pool }.Concat(members), proposal.Targets);
        Assert.Equal(new SetPhysicalDiskUsageCommand(RealTargetReference.ForExisting(members[1]), PhysicalDiskUsage.HotSpare),
            Assert.Single(proposal.Steps).Command);
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.SetPhysicalDiskUsage(_system,
            Id(StorageObjectKind.PhysicalDisk, "unapproved"), pool, members, PhysicalDiskUsage.Retired));
    }

    [Fact]
    public void MixedCapacityProposalRetainsPerLayerLayoutMediaAndExplicitIntent()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        StorageObjectId[] members = [Id(StorageObjectKind.PhysicalDisk, "a"), Id(StorageObjectKind.PhysicalDisk, "b")];
        var hdd = Id(StorageObjectKind.StorageTier, "hdd");
        var ssd = Id(StorageObjectKind.StorageTier, "ssd");
        var hddLayout = new StorageLayoutParameters("Simple", 1, 16384);
        var ssdLayout = new StorageLayoutParameters("Simple", 1, 32768);
        MaximumCapacityTier[] tiers =
        [
            new(RealTargetReference.ForExisting(hdd), null, Layout: hddLayout, MediaType: "HDD"),
            new(RealTargetReference.ForExisting(ssd), null, 10 * GiB, Layout: ssdLayout, MediaType: "SSD")
        ];
        var proposal = RealOperationProposalFactory.CreateTieredVirtualDisk(_system, pool, members, tiers, "VD");
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(Assert.Single(proposal.Steps).Command);
        Assert.Equal(new[] { pool }.Concat(members).Concat(new[] { hdd, ssd }), proposal.Targets);
        Assert.True(command.UseMaximumSize);
        Assert.Equal(0, command.SizeBytes);
        Assert.Equal(RealTargetReference.ForExisting(hdd), command.Tier);
        Assert.Equal(2, command.CapacityTiers!.Count);
        Assert.True(command.CapacityTiers[0].UsesMaximumSize(command.UseMaximumSize));
        Assert.False(command.CapacityTiers[1].UsesMaximumSize(command.UseMaximumSize));
        Assert.Equal(10 * GiB, command.CapacityTiers[1].SizeBytes);
        Assert.Equal(ssdLayout, command.CapacityTiers[1].Layout);
        Assert.Equal("SSD", command.CapacityTiers[1].MediaType);
        tiers[0] = tiers[1];
        Assert.Equal(hddLayout, command.CapacityTiers[0].Layout);
    }

    [Fact]
    public void SingleExplicitTierAndOrdinaryProposalKeepSixteenKiBLayout()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var member = Id(StorageObjectKind.PhysicalDisk, "member");
        var tier = Id(StorageObjectKind.StorageTier, "tier");
        var layout = new StorageLayoutParameters("Simple", 1, 16384);
        var tiered = RealOperationProposalFactory.CreateTieredVirtualDisk(_system, pool, [member],
            [new MaximumCapacityTier(RealTargetReference.ForExisting(tier), null, 10 * GiB, false, layout, "HDD")], "Tiered");
        var tieredCommand = Assert.IsType<CreateTieredVirtualDiskCommand>(Assert.Single(tiered.Steps).Command);
        Assert.False(tieredCommand.UseMaximumSize);
        Assert.Equal(10 * GiB, tieredCommand.SizeBytes);
        Assert.Equal(layout, tieredCommand.Layout);
        var ordinary = RealOperationProposalFactory.CreateFirstVirtualDisk(_system, pool, [member],
            new("Ordinary", 10 * GiB, false, false, false, null, null, Layout: layout));
        var ordinaryCommand = Assert.IsType<CreateVirtualDiskCommand>(Assert.Single(ordinary.Steps).Command);
        Assert.Equal(layout, ordinaryCommand.Layout);
        Assert.Equal(16384, ordinaryCommand.InterleaveBytes);
        Assert.Equal(new[] { pool, member }, ordinary.Targets);
    }

    [Fact]
    public void CompleteMemberFactoriesRejectDuplicateAndCrossSystemIdentities()
    {
        var member = Id(StorageObjectKind.PhysicalDisk, "member");
        var foreign = new StorageObjectId(SystemId.New(), StorageObjectKind.PhysicalDisk, "foreign");
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.CreatePoolWithMembers(_system, [member, member], "Pool"));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.CreatePoolWithMembers(_system, [member, foreign], "Pool"));
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.CreatePoolWithMembers(_system, [], "Pool"));
    }

    [Fact]
    public void RemovalAndTierRenameDeclareCompleteExistingPoolMembers()
    {
        var pool = Id(StorageObjectKind.StoragePool, "pool");
        var tier = Id(StorageObjectKind.StorageTier, "tier");
        StorageObjectId[] members = [Id(StorageObjectKind.PhysicalDisk, "a"), Id(StorageObjectKind.PhysicalDisk, "b")];
        var removal = RealOperationProposalFactory.DissolveSingleMemberPool(_system, pool, null, [tier], members);
        Assert.Equal(new[] { pool }.Concat(members).Concat(new[] { tier }), removal.Targets);
        var rename = RealOperationProposalFactory.RenameHddTier(_system, tier, "Renamed", pool, members);
        Assert.Equal(new[] { tier, pool }.Concat(members), rename.Targets);
        var deleteTier = RealOperationProposalFactory.DeleteHddTierTemplate(_system, tier, pool, members);
        Assert.Equal(rename.Targets, deleteTier.Targets);
        Assert.Throws<ArgumentException>(() => RealOperationProposalFactory.DeleteHddTierTemplate(_system, tier, members: members));
    }
}
