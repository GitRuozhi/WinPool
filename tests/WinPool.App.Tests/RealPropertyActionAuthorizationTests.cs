using WinPool.App.Services;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealPropertyActionAuthorizationTests
{
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
