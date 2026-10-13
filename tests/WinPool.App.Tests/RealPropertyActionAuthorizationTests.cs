using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealPropertyActionAuthorizationTests
{
    [Theory]
    [InlineData(StorageObjectKind.StoragePool)]
    [InlineData(StorageObjectKind.VirtualDisk)]
    [InlineData(StorageObjectKind.StorageTier)]
    [InlineData(StorageObjectKind.Volume)]
    [InlineData(StorageObjectKind.Partition)]
    public void PoolBackedPropertyDeclaresEveryMemberAndRetainsOnlyItsOneCommand(StorageObjectKind kind)
    {
        var system = SystemId.New();
        var snapshot = SimulationLayouts.StandardTiered();
        snapshot = snapshot with { StoragePools = snapshot.StoragePools.Select(item => item with { IsStable = true }).ToArray() };
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var vd = snapshot.VirtualDisks.Single(item => item.PoolStableId == pool.StableId);
        var disk = snapshot.OsDisks.Single(item => item.VirtualDiskStableId == vd.StableId);
        var volume = snapshot.Volumes.First(item => snapshot.Partitions.Any(part => part.StableId == item.PartitionStableId && part.OsDiskStableId == disk.StableId));
        var key = kind switch
        {
            StorageObjectKind.StoragePool => pool.StableId,
            StorageObjectKind.VirtualDisk => vd.StableId,
            StorageObjectKind.StorageTier => snapshot.StorageTiers.First(item => item.PoolStableId == pool.StableId).StableId,
            StorageObjectKind.Volume => volume.StableId,
            _ => volume.PartitionStableId!
        };
        var target = new StorageObjectId(system, kind, key);
        var reference = RealTargetReference.ForExisting(target);
        RealStorageCommand command = kind switch
        {
            StorageObjectKind.StoragePool => new RenamePoolCommand(reference, "Renamed"),
            StorageObjectKind.VirtualDisk => new RenameVirtualDiskCommand(reference, "Renamed"),
            StorageObjectKind.StorageTier => new RenameTierCommand(reference, "Renamed"),
            StorageObjectKind.Volume => new RenameVolumeCommand(reference, "Renamed"),
            _ => new SetDriveLetterCommand(reference, null, 'W')
        };
        var intent = kind == StorageObjectKind.Volume ? OperationIntent.SetVolumeLabel
            : kind == StorageObjectKind.Partition ? OperationIntent.SetDriveLetter : OperationIntent.RenameStorageObject;
        var original = RealOperationProposalFactory.OneStep(system, intent, target, command, "property", "none");
        var proposal = RealOperationProposalFactory.DeclareCurrentPropertyPoolMembers(original, snapshot);
        Assert.Equal(original.Steps, proposal.Steps);
        Assert.True(proposal.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).Select(item => item.ProviderKey)
            .ToHashSet().SetEquals(pool.MemberPhysicalDiskIds));
        Assert.Equal(kind == StorageObjectKind.StorageTier ? 2 : 1, proposal.Targets.Count(item => item.Kind != StorageObjectKind.PhysicalDisk));
        var authorization = new RealPropertyActionAuthorization(proposal);
        authorization.ValidateProposal(proposal);
        var now = DateTimeOffset.UtcNow;
        var trusted = new TrustedRealSession(SessionId.New(), "product", Guid.NewGuid().ToString("D"), 42, now, Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), environment, trusted, "inventory", "target", "members", "support", now, now.AddMinutes(2));
        var frozen = new AgentRealOperationResponse(plan, RealOperationState.Prepared, [], null, false);
        authorization.ValidateFrozen(proposal, frozen);
        var omitted = plan with { Targets = plan.Targets.Where(item => item.Kind != StorageObjectKind.PhysicalDisk || item.ProviderKey != pool.MemberPhysicalDiskIds[0]).ToArray() };
        omitted = omitted with { PlanHash = OperationPlanHasher.Compute(omitted) };
        Assert.Throws<InvalidDataException>(() => authorization.ValidateFrozen(proposal, frozen with { Plan = omitted }));
        Assert.Throws<InvalidDataException>(() => authorization.ValidateFrozen(proposal, frozen with { Plan = plan with { PlanHash = "changed" } }));
        Assert.Throws<InvalidDataException>(() => RealOperationProposalFactory.DeclareCurrentPropertyPoolMembers(
            proposal with { Targets = omitted.Targets }, snapshot));
        var added = plan with { Targets = plan.Targets.Append(new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "foreign")).ToArray() };
        added = added with { PlanHash = OperationPlanHasher.Compute(added) };
        Assert.Throws<InvalidDataException>(() => authorization.ValidateFrozen(proposal, frozen with { Plan = added }));
    }

    [Fact]
    public void CheckAuthorizesTheOneRequestedNameButRejectsAChangedValue()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.Volume, "volume:exact");
        var proposal = RealOperationProposalFactory.OneStep(system, OperationIntent.SetVolumeLabel, target,
            new RenameVolumeCommand(RealTargetReference.ForExisting(target), "新卷"), "name", "none");
        var scope = new RealPropertyActionAuthorization(proposal);
        scope.ValidateProposal(proposal);
        Assert.Throws<InvalidDataException>(() => scope.ValidateProposal(proposal with
        { Steps = [proposal.Steps[0] with { Command = new RenameVolumeCommand(RealTargetReference.ForExisting(target), "other") }] }));
    }

    [Fact]
    public void ACheckCannotAuthorizeDestructiveOrStructuralCommands()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk:exact");
        Assert.Throws<InvalidDataException>(() => new RealPropertyActionAuthorization(
            RealOperationProposalFactory.InitializeGpt(system, target)));
        Assert.Throws<InvalidDataException>(() => new RealPropertyActionAuthorization(
            RealOperationProposalFactory.ClearToRaw(system, target)));
    }

    [Fact]
    public void APropertyCheckCannotIncludeAnotherTarget()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool:exact");
        var proposal = RealOperationProposalFactory.OneStep(system, OperationIntent.RenameStorageObject, target,
            new RenamePoolCommand(RealTargetReference.ForExisting(target), "Pool"), "name", "none");
        Assert.Throws<InvalidDataException>(() => new RealPropertyActionAuthorization(proposal with
        { Targets = [target, new(system, StorageObjectKind.StoragePool, "pool:other")] }));
    }
}
