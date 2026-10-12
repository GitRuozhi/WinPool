using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationSubmissionOrderingTests
{
    [Theory]
    [InlineData(RealOperationState.Running, false)]
    [InlineData(RealOperationState.OutcomeUnknown, true)]
    public void RV13_ReconciledTerminalRejectsLateStatusForTheSameIdentity(
        RealOperationState lateState, bool requiresReconciliation)
    {
        var plan = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        Assert.True(state.TrackPrepared(Status(plan, RealOperationState.Prepared)));
        Assert.True(state.Observe(Status(plan, RealOperationState.Succeeded)));
        Assert.False(state.IsBlocked);
        Assert.True(state.Observe(Status(plan, RealOperationState.Succeeded)));

        Assert.False(state.Observe(Status(plan, lateState, requiresReconciliation)));
        Assert.False(state.IsBlocked);
        Assert.Null(state.OperationId);
        Assert.Null(state.PlanHash);
        Assert.True(state.TryReservePreparation());
    }

    [Fact]
    public void RV14_LateTerminalIdentityCannotClaimANewerPreparationReservation()
    {
        var oldPlan = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.TryReservePreparation());
        Assert.True(state.TrackPrepared(Status(oldPlan, RealOperationState.Prepared)));
        Assert.True(state.Observe(Status(oldPlan, RealOperationState.Succeeded)));

        Assert.True(state.TryReservePreparation());
        var newReservationId = state.ReservationId;
        Assert.False(state.Observe(Status(oldPlan, RealOperationState.Running)));
        Assert.False(state.Observe(Status(oldPlan, RealOperationState.OutcomeUnknown, requiresReconciliation: true)));
        Assert.True(state.IsBlocked);
        Assert.Equal(newReservationId, state.ReservationId);
        Assert.Null(state.OperationId);
        Assert.Null(state.PlanHash);

        var newPlan = CreatePlan();
        Assert.True(state.TrackPrepared(Status(newPlan, RealOperationState.Prepared)));
        Assert.True(state.Observe(Status(newPlan, RealOperationState.Running)));
        Assert.Equal(newPlan.OperationId, state.OperationId);
        Assert.True(state.Observe(Status(newPlan, RealOperationState.Succeeded)));
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void TerminalHistoryMatchesOperationIdAndPlanHashTogether()
    {
        var originalPlan = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.Observe(Status(originalPlan, RealOperationState.Succeeded)));

        var differentHashPlan = originalPlan with { PlanHash = originalPlan.PlanHash + "-different" };
        Assert.True(state.Observe(Status(differentHashPlan, RealOperationState.Running)));
        Assert.Equal(originalPlan.OperationId, state.OperationId);
        Assert.Equal(differentHashPlan.PlanHash, state.PlanHash);

        Assert.False(state.Observe(Status(originalPlan, RealOperationState.OutcomeUnknown, requiresReconciliation: true)));
        Assert.True(state.IsBlocked);
        Assert.Equal(differentHashPlan.PlanHash, state.PlanHash);
        Assert.True(state.Observe(Status(differentHashPlan, RealOperationState.Succeeded)));
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void NewDiscoveredOperationCanStillJoinAfterAnOlderIdentityTerminates()
    {
        var oldPlan = CreatePlan();
        var discoveredPlan = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.Observe(Status(oldPlan, RealOperationState.Cancelled)));

        Assert.True(state.Observe(Status(discoveredPlan, RealOperationState.OutcomeUnknown,
            requiresReconciliation: true)));
        Assert.True(state.IsBlocked);
        Assert.Equal(discoveredPlan.OperationId, state.OperationId);
        Assert.True(state.Observe(Status(discoveredPlan, RealOperationState.Failed)));
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void TerminalIdentityHistoryIsBounded()
    {
        var oldestPlan = CreatePlan();
        var state = new RealOperationSubmissionState();
        Assert.True(state.Observe(Status(oldestPlan, RealOperationState.Succeeded)));

        // The history retains the most recent 128 exact identities.
        for (var index = 0; index < 128; index++)
            Assert.True(state.Observe(Status(CreatePlan(), RealOperationState.Succeeded)));

        Assert.True(state.Observe(Status(oldestPlan, RealOperationState.Running)));
        Assert.Equal(oldestPlan.OperationId, state.OperationId);
        Assert.True(state.IsBlocked);
    }

    private static AgentRealOperationResponse Status(
        OperationPlan plan, RealOperationState state, bool requiresReconciliation = false) =>
        new(plan, state, [], null, requiresReconciliation);

    private static OperationPlan CreatePlan()
    {
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "disk-id");
        var proposal = RealOperationProposalFactory.OneStep(system,
            OperationIntent.InitializeDisk, disk,
            new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
            "GPT", "Existing partition data is lost");
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, now,
            Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(),
            EnvironmentKind.LocalMachine, "machine-binding",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
            false, now);
        return RealOperationPlanFactory.Create(proposal,
            OperationId.New(), environment, session, "inventory",
            "target-fingerprint", "physical-fingerprint", "support", now,
            now.AddMinutes(2));
    }
}
