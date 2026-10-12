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
            _ => null
        } : null;
        if (target?.Existing is not { } exact || proposal.Targets.Count != 1
            || proposal.Targets[0] != exact || proposal.Steps[0].DependsOn.Count != 0)
            throw new InvalidDataException("A property Check authorizes only one exact existing name or drive-letter target.");
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
        if (frozen.State != RealOperationState.Prepared || plan.RealOperation is not { } real
            || anchors.Length != 1 || !RealOperationValidator.IsValid(plan)
            || plan.PlanHash != OperationPlanHasher.Compute(plan) || plan.SystemId != proposal.SystemId
            || plan.Intent != proposal.Intent || !plan.Targets.ToHashSet().SetEquals(proposal.Targets.Append(anchors[0]))
            || !SameSteps(real.Steps, proposal.Steps))
            throw new InvalidDataException("The frozen property action changed its target, value or command.");
    }

    private static bool SameSteps(IReadOnlyList<RealOperationStep> left, IReadOnlyList<RealOperationStep> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.DependsOn.SequenceEqual(pair.Second.DependsOn)
            && JsonSerializer.Serialize<RealStorageCommand>(pair.First.Command) == JsonSerializer.Serialize<RealStorageCommand>(pair.Second.Command));
}
