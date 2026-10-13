using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Agent.Tests;

public sealed class AgentMaximumCapacityBoundaryProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndMixedTierProofUsesOnlyRequestedMaximumPolicies(bool mixed)
    {
        var (step, rows) = Fixture(mixed);
        Assert.True(AgentRealOperationService.CapacityJournalProvesMaximum(step, rows));
        Assert.False(AgentRealOperationService.CapacityJournalProvesMaximum(step, rows[..^1]));
        Assert.False(AgentRealOperationService.CapacityJournalProvesMaximum(step,
            rows.Select((row, index) => index == rows.Length - 1 ? row with { State = MaximumCapacityAttemptState.CallIssued, Result = null } : row).ToArray()));
    }

    [Theory]
    [InlineData("explicit-seed")]
    [InlineData("explicit-search")]
    [InlineData("missing-explicit")]
    [InlineData("changed-seed-total")]
    public void MixedTierProofRejectsChangesToTheFixedLayerAndSeedReceipt(string change)
    {
        var (step, rows) = Fixture(true);
        var seed = rows[0];
        var sizes = new Dictionary<string, long>(seed.Result!.SeedSuccessfulBytes!);
        if (change == "explicit-seed") sizes["explicit"] += MaximumCapacityAlgorithm.GiB;
        if (change == "missing-explicit") sizes.Remove("explicit");
        rows[0] = seed with { Result = seed.Result with { SeedSuccessfulBytes = sizes } };
        if (change == "changed-seed-total") rows[0] = rows[0] with { Attempt = seed.Attempt with { CandidateBytes = seed.Attempt.CandidateBytes + 1 } };
        if (change == "explicit-search") rows[1] = rows[1] with { Attempt = rows[1].Attempt with { SearchTargetKey = "explicit" } };
        Assert.False(AgentRealOperationService.CapacityJournalProvesMaximum(step, rows));
    }

    [Fact]
    public void ExplicitOnlyTierCommandDoesNotHaveAMaximumBoundaryProof()
    {
        var (step, _) = Fixture(false);
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        command = command with { UseMaximumSize = false, SizeBytes = 3 * MaximumCapacityAlgorithm.GiB,
            CapacityTiers = [command.CapacityTiers![0] with { MaximumCapacity = null, UseMaximumSize = false, SizeBytes = 3 * MaximumCapacityAlgorithm.GiB }] };
        Assert.False(AgentRealOperationService.CapacityJournalProvesMaximum(step with { Command = command }, []));
    }

    private static (RealOperationStep Step, MaximumCapacityAttemptRecord[] Rows) Fixture(bool mixed)
    {
        const long gib = MaximumCapacityAlgorithm.GiB;
        var now = DateTimeOffset.UtcNow;
        var system = SystemId.New();
        RealTargetReference Reference(StorageObjectKind kind, string id) => RealTargetReference.ForExisting(new(system, kind, id));
        var maximum = Reference(StorageObjectKind.StorageTier, "maximum");
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, 6 * gib, 5 * gib, 20 * gib, 40, "provider", "fingerprint", now);
        var inputs = new List<MaximumCapacityTier> { new(maximum, policy, UseMaximumSize: true) };
        if (mixed) inputs.Add(new(Reference(StorageObjectKind.StorageTier, "explicit"), null, 3 * gib, false));
        var step = new RealOperationStep("create", new CreateTieredVirtualDiskCommand(Reference(StorageObjectKind.StoragePool, "pool"),
            maximum, "VD", 0, true, CapacityTiers: inputs), [], "before", "after", "loss", "provider");
        var rows = new List<MaximumCapacityAttemptRecord>();
        void Add(string key, MaximumCapacityAttemptPhase phase, long candidate, long prior, bool success, IReadOnlyDictionary<string, long>? seeds = null)
        {
            var state = success ? MaximumCapacityAttemptState.Verified : MaximumCapacityAttemptState.CapacityRejectedUnchanged;
            rows.Add(new(new(rows.Count + 1, key, phase, candidate, prior, "target", "fingerprint", "members", "before"), state,
                new(state, success ? candidate : prior, "proof", "receipt", seeds), now));
        }
        if (mixed) Add("seed", MaximumCapacityAttemptPhase.Seed, 11 * gib / 2, 0, true,
            new Dictionary<string, long> { ["maximum"] = 5 * gib / 2, ["explicit"] = 3 * gib });
        Add("maximum", mixed ? MaximumCapacityAttemptPhase.Resize : MaximumCapacityAttemptPhase.Create, 5 * gib, mixed ? 5 * gib / 2 : 0, true);
        Add("maximum", MaximumCapacityAttemptPhase.Resize, 6 * gib, 5 * gib, false);
        return (step, rows.ToArray());
    }
}
