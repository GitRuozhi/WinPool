using WinPool.Domain;

namespace WinPool.Execution.Tests;

public sealed class SafeOperationExecutorTests
{
    private static readonly ExecutionCapability AllCapabilities =
        Enum.GetValues<ExecutionCapability>().Aggregate(
            ExecutionCapability.None,
            (current, value) => current | value);

    [Fact]
    public async Task SimulationExecutor_UpdatesOnlyTheSimulationDocument_AndEmitsLifecycle()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.Simulation,
            OperationIntent.SimulateStorageMutation,
            new Dictionary<string, string>
            {
                ["FriendlyName"] = "Updated simulation",
                ["Layout"] = "Mirror"
            });
        var store = new InMemorySimulationDocumentStore(
        [
            SimulationDocumentSnapshot.Create(
                fixture.SystemId,
                4,
                new Dictionary<string, string> { ["FriendlyName"] = "Before" })
        ]);
        var executor = new SimulationOperationExecutor(store, timeProvider: fixture.Clock);

        var events = await fixture.ExecuteAsync(executor);

        Assert.Equal(
            [
                ExecutionEventKind.Accepted,
                ExecutionEventKind.Started,
                ExecutionEventKind.Progress,
                ExecutionEventKind.Completed
            ],
            events.Select(item => item.Kind));
        var document = store.Get(fixture.SystemId);
        Assert.Equal(5, document.Revision);
        Assert.Equal("Updated simulation", document.Values["FriendlyName"]);
        Assert.Equal("Mirror", document.Values["Layout"]);
    }

    [Fact]
    public async Task SimulationExecutor_FaultAfterApply_RestoresPreviousRevision()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.Simulation,
            OperationIntent.SimulateStorageMutation,
            new Dictionary<string, string> { ["FriendlyName"] = "Should be rolled back" });
        var store = new InMemorySimulationDocumentStore(
        [
            SimulationDocumentSnapshot.Create(
                fixture.SystemId,
                7,
                new Dictionary<string, string> { ["FriendlyName"] = "Original" })
        ]);
        var executor = new SimulationOperationExecutor(
            store,
            new ThrowingSimulationFaultInjector(SimulationExecutionCheckpoint.AfterApply),
            fixture.Clock);

        var events = await fixture.ExecuteAsync(executor);

        Assert.Equal(ExecutionEventKind.Failed, events[^1].Kind);
        Assert.Equal("simulation.failed", events[^1].Code);
        Assert.Contains("restored", events[^1].Message, StringComparison.OrdinalIgnoreCase);
        var document = store.Get(fixture.SystemId);
        Assert.Equal(7, document.Revision);
        Assert.Equal("Original", document.Values["FriendlyName"]);
    }

    [Fact]
    public async Task SimulationExecutor_CancellationAfterApply_RestoresPreviousRevision()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = Fixture.Create(
            EnvironmentKind.Simulation,
            OperationIntent.SimulateStorageMutation,
            new Dictionary<string, string> { ["FriendlyName"] = "Should be cancelled" });
        var store = new InMemorySimulationDocumentStore(
        [
            SimulationDocumentSnapshot.Create(
                fixture.SystemId,
                2,
                new Dictionary<string, string> { ["FriendlyName"] = "Original" })
        ]);
        var executor = new SimulationOperationExecutor(
            store,
            new CancellingSimulationFaultInjector(
                SimulationExecutionCheckpoint.AfterApply,
                cancellation),
            fixture.Clock);

        var events = await fixture.ExecuteAsync(executor, cancellation.Token);

        Assert.Equal(ExecutionEventKind.Cancelled, events[^1].Kind);
        Assert.Equal("simulation.cancelled", events[^1].Code);
        Assert.Contains("restored", events[^1].Message, StringComparison.OrdinalIgnoreCase);
        var document = store.Get(fixture.SystemId);
        Assert.Equal(2, document.Revision);
        Assert.Equal("Original", document.Values["FriendlyName"]);
    }

    [Fact]
    public async Task SimulationPlan_IsRejectedOutsideSimulationEnvironment()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.ProtectedDevelopmentMachine,
            OperationIntent.SimulateStorageMutation);

        var decision = await fixture.Policy.EvaluateAsync(
            fixture.Plan,
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Rejected, decision.Kind);
        Assert.Equal("policy.simulation-environment-required", decision.Code);
    }

    [Theory]
    [InlineData(OperationIntent.ReadInventory, ExecutionCapability.ReadInventory)]
    [InlineData(OperationIntent.ReadPerformanceCounters, ExecutionCapability.ReadPerformanceCounters)]
    [InlineData(OperationIntent.OpenNativeProperties, ExecutionCapability.OpenNativeProperties)]
    public async Task ReadOnlyWindowsExecutor_UsesOnlyTheMatchingTypedPort(
        OperationIntent intent,
        ExecutionCapability expectedCapability)
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine, intent);
        var operations = new RecordingReadOnlyWindowsOperations();
        var executor = new ReadOnlyWindowsExecutor(operations, fixture.Clock);

        var events = await fixture.ExecuteAsync(executor);

        Assert.Equal(expectedCapability, fixture.Plan.RequiredCapabilities);
        Assert.Equal(
            [ExecutionEventKind.Accepted, ExecutionEventKind.Started, ExecutionEventKind.Completed],
            events.Select(item => item.Kind));
        Assert.Equal(intent, Assert.Single(operations.Calls));
        Assert.Equal("windows.test-completed", events[^1].Code);
        Assert.DoesNotContain(
            ExecutionCapability.MutateStorageStructure,
            ExpandCapabilities(executor.Capability));
    }

    [Fact]
    public async Task ReadOnlyWindowsExecutor_RejectsSimulationEnvironmentWithoutCallingPort()
    {
        var fixture = Fixture.Create(EnvironmentKind.Simulation, OperationIntent.ReadInventory);
        var operations = new RecordingReadOnlyWindowsOperations();

        var events = await fixture.ExecuteAsync(
            new ReadOnlyWindowsExecutor(operations, fixture.Clock));

        var rejected = Assert.Single(events);
        Assert.Equal(ExecutionEventKind.Rejected, rejected.Kind);
        Assert.Equal("windows-readonly.plan-not-supported", rejected.Code);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public async Task ReadOnlyWindowsExecutor_CancellationIsReportedWithoutRawExceptionText()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = Fixture.Create(
            EnvironmentKind.ProtectedDevelopmentMachine,
            OperationIntent.ReadInventory);
        var operations = new CancellingReadOnlyWindowsOperations(cancellation);

        var events = await fixture.ExecuteAsync(
            new ReadOnlyWindowsExecutor(operations, fixture.Clock),
            cancellation.Token);

        Assert.Equal(ExecutionEventKind.Cancelled, events[^1].Kind);
        Assert.Equal("windows-readonly.cancelled", events[^1].Code);
        Assert.DoesNotContain("sensitive", events[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayExecutor_ReplaysHistoricalEventsWithoutARealTargetPort()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.Replay,
            OperationIntent.ReplayHistoricalEvents);
        var sourceOperation = OperationId.New();
        var historicalAt = Fixture.Now.AddDays(-1);
        var source = new InMemoryReplayEventSource(
        [
            new ExecutionEvent(
                sourceOperation,
                ExecutionEventKind.Progress,
                historicalAt,
                "historical.measurement",
                "A sanitized historical measurement.")
        ]);
        var executor = new ReplayExecutor(source, fixture.Clock);

        var events = await fixture.ExecuteAsync(executor);

        Assert.Equal(
            [
                ExecutionEventKind.Accepted,
                ExecutionEventKind.Started,
                ExecutionEventKind.Progress,
                ExecutionEventKind.Completed
            ],
            events.Select(item => item.Kind));
        Assert.Equal(fixture.Plan.OperationId, events[2].OperationId);
        Assert.Equal(sourceOperation, events[2].SourceOperationId);
        Assert.Equal(historicalAt, events[2].At);
        Assert.Equal("historical.measurement", events[2].Code);
        Assert.Equal(ExecutionCapability.ReplayEvidence, executor.Capability);
    }

    [Fact]
    public async Task ReplayPlan_IsRejectedOutsideReplayEnvironment()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.ProtectedDevelopmentMachine,
            OperationIntent.ReplayHistoricalEvents);

        var decision = await fixture.Policy.EvaluateAsync(
            fixture.Plan,
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Rejected, decision.Kind);
        Assert.Equal("policy.replay-environment-required", decision.Code);
    }

    private static IReadOnlyList<ExecutionCapability> ExpandCapabilities(
        ExecutionCapability capabilities) =>
        Enum.GetValues<ExecutionCapability>()
            .Where(value => value != ExecutionCapability.None && capabilities.HasFlag(value))
            .ToArray();

    private sealed class Fixture
    {
        public static readonly DateTimeOffset Now =
            new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        private const string InventoryVersion = "inventory-v1";
        private const string Machine = "machine-binding";

        private Fixture(
            SystemId systemId,
            OperationPlan plan,
            ExecutionContext context,
            OperationPolicyEvaluator policy,
            InMemoryOperationAuthority authority,
            TimeProvider clock)
        {
            SystemId = systemId;
            Plan = plan;
            Context = context;
            Policy = policy;
            Authority = authority;
            Clock = clock;
        }

        public SystemId SystemId { get; }
        public OperationPlan Plan { get; }
        public ExecutionContext Context { get; }
        public OperationPolicyEvaluator Policy { get; }
        public InMemoryOperationAuthority Authority { get; }
        public TimeProvider Clock { get; }

        public static Fixture Create(
            EnvironmentKind environmentKind,
            OperationIntent intent,
            IReadOnlyDictionary<string, string>? parameters = null)
        {
            var systemId = SystemId.New();
            var environmentId = EnvironmentId.New();
            var environment = new EnvironmentProfile(
                environmentId,
                environmentKind,
                Machine,
                AllCapabilities,
                environmentKind == EnvironmentKind.UserProvidedDisposableMachine,
                Now);
            var context = new ExecutionContext(
                environment,
                ExecutionMode.Simulation,
                PrivilegeState.StandardUser,
                Machine,
                InventoryVersion,
                false);
            var request = new OperationRequest(
                OperationId.New(),
                environmentId,
                systemId,
                intent,
                [new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "disk-0")],
                parameters ?? new Dictionary<string, string>(),
                Now);
            var definition = OperationSecurityCatalog.Get(intent);
            var plan = OperationPlan.Create(
                request,
                definition.RequiredCapabilities,
                definition.MinimumRisk,
                InventoryVersion,
                ["Refresh inventory."],
                [new PlanStep("execute", $"Execute {intent}.", [])],
                null,
                definition.ImpactScope,
                definition.RollbackDescription,
                definition.IrreversibleEffects,
                DefaultOperationPlanner.Algorithm,
                Now);
            var policy = new OperationPolicyEvaluator();
            var clock = new FixedTimeProvider(Now);
            var authority = new InMemoryOperationAuthority(policy, clock);
            return new(systemId, plan, context, policy, authority, clock);
        }

        public async Task<List<ExecutionEvent>> ExecuteAsync(
            IOperationExecutor executor,
            CancellationToken cancellationToken = default)
        {
            var authorization = await Authority.AuthorizeAsync(
                Plan,
                Context,
                true,
                CancellationToken.None);
            var token = Assert.IsType<OperationAuthorizationToken>(authorization.Token);
            var gate = new ExecutorGate(Policy, Authority, Clock);
            var events = new List<ExecutionEvent>();
            await foreach (var executionEvent in gate.ExecuteAsync(
                Plan,
                Context,
                token,
                executor,
                cancellationToken))
            {
                events.Add(executionEvent);
            }

            return events;
        }
    }

    private sealed class RecordingReadOnlyWindowsOperations : IReadOnlyWindowsOperations
    {
        public List<OperationIntent> Calls { get; } = [];

        public Task<ReadOnlyWindowsResult> ReadInventoryAsync(
            SystemId systemId,
            IReadOnlyList<StorageObjectId> targets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(OperationIntent.ReadInventory);
            return Result();
        }

        public Task<ReadOnlyWindowsResult> ReadPerformanceCountersAsync(
            SystemId systemId,
            IReadOnlyList<StorageObjectId> targets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(OperationIntent.ReadPerformanceCounters);
            return Result();
        }

        public Task<ReadOnlyWindowsResult> OpenNativePropertiesAsync(
            StorageObjectId target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(OperationIntent.OpenNativeProperties);
            return Result();
        }

        private static Task<ReadOnlyWindowsResult> Result() =>
            Task.FromResult(
                new ReadOnlyWindowsResult(
                    "windows.test-completed",
                    "The typed read-only test port completed."));
    }

    private sealed class CancellingReadOnlyWindowsOperations(
        CancellationTokenSource cancellation)
        : IReadOnlyWindowsOperations
    {
        public Task<ReadOnlyWindowsResult> ReadInventoryAsync(
            SystemId systemId,
            IReadOnlyList<StorageObjectId> targets,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException("sensitive provider detail", cancellationToken);
        }

        public Task<ReadOnlyWindowsResult> ReadPerformanceCountersAsync(
            SystemId systemId,
            IReadOnlyList<StorageObjectId> targets,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReadOnlyWindowsResult> OpenNativePropertiesAsync(
            StorageObjectId target,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSimulationFaultInjector(
        SimulationExecutionCheckpoint checkpoint)
        : ISimulationExecutionFaultInjector
    {
        public Task InspectAsync(
            SimulationExecutionCheckpoint current,
            OperationPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current == checkpoint)
            {
                throw new InvalidOperationException("Injected simulation failure.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancellingSimulationFaultInjector(
        SimulationExecutionCheckpoint checkpoint,
        CancellationTokenSource cancellation)
        : ISimulationExecutionFaultInjector
    {
        public Task InspectAsync(
            SimulationExecutionCheckpoint current,
            OperationPlan plan,
            CancellationToken cancellationToken)
        {
            if (current == checkpoint)
            {
                cancellation.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
