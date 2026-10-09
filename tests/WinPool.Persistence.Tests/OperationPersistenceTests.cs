using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class OperationPersistenceTests
{
    [Fact]
    public async Task PrepareAcceptAndStepTransitionsAreDurableAndIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var reader = new OperationPlanRepository(database.Store);
        var plan = Plan();
        var preparationId = Guid.NewGuid();

        Assert.Equal(PersistedOperationState.Prepared,
            (await writer.PrepareAsync(plan, preparationId, "intent-digest")).State);
        Assert.Equal(plan.OperationId,
            (await writer.PrepareAsync(Plan(), preparationId, "intent-digest")).Plan.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.PrepareAsync(Plan(), preparationId, "different-intent"));
        Assert.Equal(preparationId,
            (await reader.GetByPreparationIdAsync(preparationId))!.PreparationId);
        Assert.True(await reader.HasRealWriteBarrierAsync());
        Assert.Equal(PersistedOperationState.Accepted,
            (await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt));
        var steps = await reader.GetStepsAsync(plan.OperationId);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.Equal(PersistedOperationStepState.NotStarted, step.State));
        Assert.Single(await new ExecutionEventRepository(database.Store).ListAsync(plan.OperationId));

        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"disk\":\"stable-id\"}", null, Event(plan, "preparing")));
        Assert.False(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"disk\":\"stable-id\"}", null, Event(plan, "duplicate")));
        Assert.Equal(3, (await new ExecutionEventRepository(database.Store)
            .ListAsync(plan.OperationId)).Count);
        Assert.Equal("{\"disk\":\"stable-id\"}",
            Assert.Single(await reader.GetStepsAsync(plan.OperationId),
                step => step.StepId == "first").TargetJson);
        Assert.Single(await reader.ListUnfinishedAsync());
    }

    [Fact]
    public async Task FailedEventInsertRollsBackAcceptanceAndAuthorizationDigest()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_accept BEFORE INSERT ON execution_events
                BEGIN SELECT RAISE(FAIL, 'injected event write failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt));
        Assert.Equal(PersistedOperationState.Prepared,
            (await writer.GetAsync(plan.OperationId))!.State);
        Assert.All(await writer.GetStepsAsync(plan.OperationId),
            step => Assert.Null(step.UpdatedAt));
        await using var verify = await database.Store.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT authorization_digest FROM operation_plans WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", plan.OperationId.Value.ToString("N"));
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnknownStateSurvivesReopenAndBlocksNewPlan()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"id\":\"disk-a\"}", null, Event(plan, "before-call")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.OutcomeUnknown,
            Event(plan, "unknown")));

        await database.Store.InitializeAsync();
        var reader = new OperationPlanRepository(database.Store);
        Assert.True(await reader.HasRealWriteBarrierAsync());
        Assert.Equal(PersistedOperationState.OutcomeUnknown,
            Assert.Single(await reader.ListUnfinishedAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.PrepareAsync(Plan(), Guid.NewGuid(), "other-intent"));
    }

    [Fact]
    public async Task StepEventFailureRollsBackCallPreparation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_step BEFORE INSERT ON execution_events
                WHEN NEW.code = 'prepare-step'
                BEGIN SELECT RAISE(FAIL, 'injected step event failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            writer.TransitionStepAsync(plan.OperationId, "first",
                PersistedOperationStepState.NotStarted,
                PersistedOperationStepState.PreparingCall,
                "{\"disk\":\"stable-id\"}", null, Event(plan, "prepare-step")));
        var first = Assert.Single(await writer.GetStepsAsync(plan.OperationId),
            step => step.StepId == "first");
        Assert.Equal(PersistedOperationStepState.NotStarted, first.State);
        Assert.Null(first.TargetJson);
        Assert.Single(await new ExecutionEventRepository(database.Store)
            .ListAsync(plan.OperationId));
    }

    [Fact]
    public async Task ReconciledPartialCompletionReleasesWriteBarrierWithoutReplayingSteps()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"id\":\"disk-a\"}", null, Event(plan, "before-call")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.OutcomeUnknown,
            Event(plan, "unknown")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.PreparingCall, PersistedOperationStepState.Verified,
            null, "{\"observed\":\"GPT\"}", Event(plan, "reconciled")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Skipped,
            null, null, Event(plan, "skipped")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.OutcomeUnknown, PersistedOperationState.PartiallyCompleted,
            Event(plan, "partial")));
        Assert.False(await writer.HasRealWriteBarrierAsync());
        Assert.Empty(await writer.ListUnfinishedAsync());
    }

    [Fact]
    public async Task RunningCanCancelOnlyAfterEveryStepIsProvenSkippedBeforeCall()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Cancelled,
            Event(plan, "premature-cancel")));
        Assert.True(await writer.HasRealWriteBarrierAsync());

        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"disk\":\"stable-id\"}", null, Event(plan, "preparing")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.PreparingCall, PersistedOperationStepState.Skipped,
            null, "stopped_before_call", Event(plan, "skip-first")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Skipped,
            null, "stopped_before_call", Event(plan, "skip-second")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Cancelled,
            Event(plan, "cancelled")));
        Assert.False(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task RecoveredUnknownCanCancelOnlyAfterAllStepsAreSkipped()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.OutcomeUnknown,
            Event(plan, "recovery")));
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.OutcomeUnknown, PersistedOperationState.Cancelled,
            Event(plan, "premature-cancel")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Skipped,
            null, "stopped_before_call", Event(plan, "skip-first")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Skipped,
            null, "stopped_before_call", Event(plan, "skip-second")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.OutcomeUnknown, PersistedOperationState.Cancelled,
            Event(plan, "cancelled")));
        Assert.False(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task CompletedCannotReleaseBarrierUntilEveryStepHasEvidence()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Completed,
            Event(plan, "premature-success")));
        await VerifyStepAsync(writer, plan, "first");
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Completed,
            Event(plan, "incomplete-success")));
        Assert.True(await writer.HasRealWriteBarrierAsync());
        await VerifyStepAsync(writer, plan, "second");
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Completed,
            Event(plan, "complete")));
        Assert.False(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task FailedNeedsNoVerifiedStepsAndEvidenceForItsFailure()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Failed,
            null, "preflight_failed", Event(plan, "failed-before-call")));
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Failed,
            Event(plan, "unfinished-failure")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Skipped,
            null, "stopped_after_previous_step", Event(plan, "skipped")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.Failed,
            Event(plan, "failed")));
        Assert.False(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task PartialCompletionRejectsVerifiedStepAfterFailedEarlierStep()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "test-agent");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Accepted, PersistedOperationState.Running,
            Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.Failed,
            null, "preflight_failed", Event(plan, "first-failed")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"target\":\"disk\"}", null, Event(plan, "second-preparing")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "second",
            PersistedOperationStepState.PreparingCall, PersistedOperationStepState.Verified,
            null, "{\"post\":\"verified\"}", Event(plan, "second-verified")));
        Assert.True(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.Running, PersistedOperationState.OutcomeUnknown,
            Event(plan, "recovery")));
        Assert.False(await writer.TransitionAsync(plan.OperationId,
            PersistedOperationState.OutcomeUnknown, PersistedOperationState.PartiallyCompleted,
            Event(plan, "invalid-partial")));
        Assert.True(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task CapacityAttemptsPersistBoundaryFailureWithoutFailingParentStep()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-journal");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = await PrepareCapacityParentAsync(writer);
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first");
        Assert.True(await journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
        Assert.True(await journal.MarkCallIssuedAsync(1, default));
        Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified, 4,
            "verified", "{\"actualSize\":4}"), default));
        Assert.True(await journal.PrepareAsync(CapacityAttempt(2, 5, 4), default));
        Assert.True(await journal.MarkCallIssuedAsync(2, default));
        Assert.True(await journal.CompleteAsync(2, new(MaximumCapacityAttemptState.CapacityRejectedUnchanged, 4,
            "capacity_proven_unchanged", "{\"twoFreshProofs\":true}"), default));
        await database.Store.InitializeAsync();
        var rows = await new OperationPlanRepository(database.Store)
            .ReadMaximumCapacityAttemptsAsync(plan.OperationId, "first");
        Assert.Equal(2, rows.Count);
        Assert.Equal(MaximumCapacityAttemptState.CapacityRejectedUnchanged, rows[1].State);
        Assert.Equal(4, rows[1].Result!.LastSuccessfulBytes);
        Assert.Equal(PersistedOperationStepState.CallIssued,
            (await writer.GetStepsAsync(plan.OperationId))[0].State);
        Assert.True(await writer.HasRealWriteBarrierAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityAttemptCasRejectsDuplicateAndOutOfOrderCalls(bool concurrent)
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-cas");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = await PrepareCapacityParentAsync(writer);
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first");
        Assert.False(await journal.PrepareAsync(CapacityAttempt(2, 4, 0), default));
        if (concurrent)
        {
            var results = await Task.WhenAll(journal.PrepareAsync(CapacityAttempt(1, 4, 0), default),
                journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
            Assert.Single(results, accepted => accepted);
        }
        else
        {
            Assert.True(await journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
            Assert.False(await journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
        }
        Assert.False(await journal.PrepareAsync(CapacityAttempt(2, 3, 0), default));
        Assert.False(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified, 4,
            "premature", "proof"), default));
        Assert.True(await journal.MarkCallIssuedAsync(1, default));
        Assert.False(await journal.MarkCallIssuedAsync(1, default));
        Assert.False(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified, 3,
            "wrong_size", "proof"), default));
        Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified, 4,
            "verified", "proof"), default));
        Assert.False(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.OutcomeUnknown, 0,
            "late_unknown", "proof"), default));
        Assert.False(await journal.PrepareAsync(CapacityAttempt(2, 5, 0), default));
        Assert.Single(await journal.ReadAsync(default));
    }

    [Theory]
    [InlineData(MaximumCapacityAttemptState.OutcomeUnknown)]
    [InlineData(MaximumCapacityAttemptState.FailedWithoutCall)]
    public async Task UncertainOrInfrastructureAttemptCannotAuthorizeNextCandidate(MaximumCapacityAttemptState state)
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-stop");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = await PrepareCapacityParentAsync(writer);
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first");
        Assert.True(await journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
        if (state == MaximumCapacityAttemptState.OutcomeUnknown)
            Assert.True(await journal.MarkCallIssuedAsync(1, default));
        Assert.True(await journal.CompleteAsync(1, new(state, 0, "not_capacity", "proof"), default));
        Assert.False(await journal.PrepareAsync(CapacityAttempt(2, 3, 0), default));
        Assert.True(await writer.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task AttemptEventFailureRollsBackCallIssuedAndReadOnlyJournalCannotWrite()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-atomic");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = await PrepareCapacityParentAsync(writer);
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first");
        Assert.True(await journal.PrepareAsync(CapacityAttempt(1, 4, 0), default));
        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_capacity_event BEFORE INSERT ON execution_events BEGIN SELECT RAISE(FAIL,'injected'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => journal.MarkCallIssuedAsync(1, default));
        Assert.Equal(MaximumCapacityAttemptState.PreparingCall, Assert.Single(await journal.ReadAsync(default)).State);
        var reader = new OperationPlanRepository(database.Store).CreateMaximumCapacityJournal(plan.OperationId, "first");
        Assert.Single(await reader.ReadAsync(default));
        await Assert.ThrowsAsync<AgentWriteOwnershipException>(() => reader.MarkCallIssuedAsync(1, default));
    }

    [Fact]
    public async Task LayerJournalTracksEachFrozenSlotAndStopBlocksFurtherCalls()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-layers");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = await PrepareCapacityParentAsync(writer);
        var stop = false;
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first", () => stop);
        Assert.True(await journal.PrepareAsync(CapacityAttempt(1, 2, 0), default));
        Assert.True(await journal.MarkCallIssuedAsync(1, default));
        Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified, 2, "seed", "proof"), default));
        Assert.True(await journal.PrepareAsync(CapacityAttempt(2, 3, 0) with { SearchTargetKey = "second-layer" }, default));
        stop = true;
        Assert.True(journal.IsStopRequested);
        Assert.False(await journal.MarkCallIssuedAsync(2, default));
        Assert.True(await journal.CompleteAsync(2, new(MaximumCapacityAttemptState.FailedWithoutCall, 0, "stopped", "proof"), default));
        Assert.False(await journal.PrepareAsync(CapacityAttempt(3, 4, 2), default));
    }

    [Fact]
    public async Task MultiTierSeedCanOnlyInheritExactFrozenPerLayerSuccesses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "capacity-multi-seed");
        var writer = new OperationPlanRepository(database.Store, lease);
        var plan = Plan();
        var pool = RealTargetReference.ForExisting(new(plan.SystemId, StorageObjectKind.StoragePool, "pool"));
        var first = RealTargetReference.ForExisting(new(plan.SystemId, StorageObjectKind.StorageTier, "hdd-template"));
        var second = RealTargetReference.ForExisting(new(plan.SystemId, StorageObjectKind.StorageTier, "ssd-template"));
        var gib = MaximumCapacityAlgorithm.GiB;
        MaximumCapacityPolicy Policy(long candidate) => new(MaximumCapacityAlgorithm.Version,
            candidate + MaximumCapacityAlgorithm.ReserveBytes + 1, candidate, 10 * gib, 20,
            "fresh-template", "frozen-source", DateTimeOffset.UtcNow);
        var firstPolicy = Policy(gib);
        var secondPolicy = Policy(3 * gib);
        var command = new CreateTieredVirtualDiskCommand(pool, first, "multi", 0, true,
            MaximumCapacity: firstPolicy, CapacityTiers: [new(first, firstPolicy), new(second, secondPolicy)]);
        plan = plan with { RealOperation = new(1, "test", "machine", "session", "target", "physical", "support",
            "maximum", DateTimeOffset.UtcNow.AddMinutes(1),
            [new("first", command, [], "before", "after", "writes", "frozen")]) };
        plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        await PrepareCapacityParentAsync(writer, plan);
        var journal = writer.CreateMaximumCapacityJournal(plan.OperationId, "first");
        var seed = CapacityAttempt(1, 2 * gib, 0) with
            { SearchTargetKey = "seed", Phase = MaximumCapacityAttemptPhase.Seed };
        Assert.True(await journal.PrepareAsync(seed, default));
        Assert.True(await journal.MarkCallIssuedAsync(1, default));
        var valid = new MaximumCapacityAttemptResult(MaximumCapacityAttemptState.Verified, 2 * gib,
            "seed_verified", "strict_all_layer_proof", new Dictionary<string, long>
                { ["hdd-template"] = gib / 2, ["ssd-template"] = 3 * gib / 2 });
        Assert.False(await journal.CompleteAsync(1, valid with { SeedSuccessfulBytes = new Dictionary<string, long>
            { ["hdd-template"] = gib, ["ssd-template"] = gib } }, default));
        Assert.True(await journal.CompleteAsync(1, valid, default));
        Assert.False(await journal.PrepareAsync(CapacityAttempt(2, gib, gib) with
            { SearchTargetKey = "hdd-template" }, default));
        Assert.True(await journal.PrepareAsync(CapacityAttempt(2, gib, gib / 2) with
            { SearchTargetKey = "hdd-template" }, default));
        Assert.True(await journal.MarkCallIssuedAsync(2, default));
        Assert.True(await journal.CompleteAsync(2, new(MaximumCapacityAttemptState.Verified, gib,
            "first_layer_verified", "proof"), default));
        Assert.True(await journal.PrepareAsync(CapacityAttempt(3, 3 * gib, 3 * gib / 2) with
            { SearchTargetKey = "ssd-template" }, default));
        Assert.Equal(gib, (await journal.ReadAsync(default))[1].Result!.LastSuccessfulBytes);
    }

    private static MaximumCapacityAttempt CapacityAttempt(int ordinal, long candidate, long last) =>
        new(ordinal, "first-layer", ordinal == 1 ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize,
            candidate, last, "{\"exactTarget\":true}", "before-fingerprint", "physical-fingerprint", "{\"fresh\":true}");

    private static async Task<OperationPlan> PrepareCapacityParentAsync(OperationPlanRepository writer, OperationPlan? supplied = null)
    {
        var plan = supplied ?? Plan();
        await writer.PrepareAsync(plan, Guid.NewGuid(), "capacity-intent");
        await writer.AcceptAsync(plan.OperationId, plan.PlanHash, Digest, plan.CreatedAt);
        Assert.True(await writer.TransitionAsync(plan.OperationId, PersistedOperationState.Accepted,
            PersistedOperationState.Running, Event(plan, "running")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first", PersistedOperationStepState.NotStarted,
            PersistedOperationStepState.PreparingCall, "target", null, Event(plan, "preparing")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, "first", PersistedOperationStepState.PreparingCall,
            PersistedOperationStepState.CallIssued, "target", null, Event(plan, "call_issued")));
        return plan;
    }

    private static async Task VerifyStepAsync(
        OperationPlanRepository writer, OperationPlan plan, string stepId)
    {
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, stepId,
            PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall,
            "{\"target\":\"disk\"}", null, Event(plan, stepId + "-preparing")));
        Assert.True(await writer.TransitionStepAsync(plan.OperationId, stepId,
            PersistedOperationStepState.PreparingCall, PersistedOperationStepState.Verified,
            null, "{\"post\":\"verified\"}", Event(plan, stepId + "-verified")));
    }

    private static ExecutionEvent Event(OperationPlan plan, string code) =>
        new(plan.OperationId, ExecutionEventKind.Progress,
            DateTimeOffset.UtcNow, code, code);

    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static OperationPlan Plan()
    {
        var system = SystemId.New();
        var request = new OperationRequest(OperationId.New(), EnvironmentId.New(), system,
            OperationIntent.InitializeDisk,
            [new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "disk-stable-id")],
            new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        return OperationPlan.Create(request, ExecutionCapability.MutateStorageStructure,
            RiskLevel.R4StorageStructureMutation, "inventory-v1", [],
            [new PlanStep("first", "initialize", []),
             new PlanStep("second", "verify", ["first"])],
            null, "disk", "none", "GPT metadata changes",
            new AlgorithmIdentity("ALG-TEST", "1", AlgorithmConfidence.Proven, "unit-test"),
            request.RequestedAt);
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }
        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "WinPool.Persistence.Operation.Tests", Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new TemporaryDatabase(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
