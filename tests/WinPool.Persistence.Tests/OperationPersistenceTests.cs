using Microsoft.Data.Sqlite;
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
