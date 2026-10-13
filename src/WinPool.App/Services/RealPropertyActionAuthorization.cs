using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>The Check button authorizes one property only, with unchanged Agent preparation/preflight.</summary>
public sealed class RealPropertyActionAuthorization : IRealOperationAuthorization
{
    private readonly RealOperationIntentRequest expected;

    public RealPropertyActionAuthorization(RealOperationIntentRequest proposal)
    {
        RealOperationValidator.Validate(proposal);
        var target = proposal.Steps.Count == 1 ? proposal.Steps[0].Command switch
        {
            SetDriveLetterCommand value => value.Partition,
            RenameVolumeCommand value => value.Volume,
            RenamePoolCommand value => value.Pool,
            RenameVirtualDiskCommand value => value.VirtualDisk,
            RenameTierCommand value => value.Tier,
            _ => null
        } : null;
        if (target?.Existing is not { } exact || proposal.Targets.Count(item => item == exact) != 1
            || proposal.Steps[0].DependsOn.Count != 0)
            throw new InvalidDataException("A property Check authorizes only one exact existing name or drive-letter target.");
        var declaredMembers = proposal.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        var parentPools = proposal.Targets.Where(item => item != exact && item.Kind == StorageObjectKind.StoragePool).ToArray();
        if (declaredMembers.Length == 0 ? proposal.Targets.Count != 1
            : proposal.Targets.Any(item => item != exact && item.Kind != StorageObjectKind.PhysicalDisk
                && !(proposal.Steps[0].Command is RenameTierCommand && item.Kind == StorageObjectKind.StoragePool))
              || (proposal.Steps[0].Command is RenameTierCommand ? parentPools.Length != 1 : parentPools.Length != 0))
            throw new InvalidDataException("A property Check may include only its exact pool member set and a tier's parent pool.");
        expected = proposal;
    }

    public void ValidateProposal(RealOperationIntentRequest proposal)
    {
        RealOperationValidator.Validate(proposal);
        if (proposal.SystemId != expected.SystemId || proposal.Intent != expected.Intent
            || !proposal.Targets.SequenceEqual(expected.Targets) || !SameSteps(proposal.Steps, expected.Steps))
            throw new InvalidDataException("The property action changed its requested target or value.");
    }

    public void ValidateFrozen(RealOperationIntentRequest proposal, AgentRealOperationResponse frozen)
    {
        ValidateProposal(proposal);
        var plan = frozen.Plan;
        var anchors = plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        var declaredMembers = proposal.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        var exactTargets = declaredMembers.Length > 0
            ? plan.Targets.ToHashSet().SetEquals(proposal.Targets)
            : anchors.Length == 1 && plan.Targets.ToHashSet().SetEquals(proposal.Targets.Append(anchors[0]));
        if (frozen.State != RealOperationState.Prepared || plan.RealOperation is not { } real
            || anchors.Length == 0 || !RealOperationValidator.IsValid(plan)
            || plan.PlanHash != OperationPlanHasher.Compute(plan) || plan.SystemId != proposal.SystemId
            || plan.Intent != proposal.Intent || !exactTargets
            || !SameSteps(real.Steps, proposal.Steps))
            throw new InvalidDataException("The frozen property action changed its target, value or command.");
    }

    private static bool SameSteps(IReadOnlyList<RealOperationStep> left, IReadOnlyList<RealOperationStep> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.DependsOn.SequenceEqual(pair.Second.DependsOn)
            && JsonSerializer.Serialize<RealStorageCommand>(pair.First.Command) == JsonSerializer.Serialize<RealStorageCommand>(pair.Second.Command));
}
