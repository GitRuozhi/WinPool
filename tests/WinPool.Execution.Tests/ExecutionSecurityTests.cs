using WinPool.Domain;

namespace WinPool.Execution.Tests;

public sealed class ExecutionSecurityTests
{
    private static readonly ExecutionCapability AllCapabilities =
        Enum.GetValues<ExecutionCapability>().Aggregate(ExecutionCapability.None, (current, value) => current | value);

    public static IEnumerable<object[]> StorageMutationIntents() =>
        OperationSecurityCatalog.StorageStructureMutationIntents.Select(intent => new object[] { intent });

    [Fact]
    public void SecurityCatalog_ClassifiesEveryIntent_AndIncludesTheCompleteR4R5Set()
    {
        var allIntents = Enum.GetValues<OperationIntent>();
        Assert.All(allIntents, intent => Assert.NotNull(OperationSecurityCatalog.Get(intent)));

        var expected = new[]
        {
            OperationIntent.InitializeDisk,
            OperationIntent.ConvertDisk,
            OperationIntent.SetDiskOnlineState,
            OperationIntent.CreatePartition,
            OperationIntent.DeletePartition,
            OperationIntent.ResizePartition,
            OperationIntent.FormatVolume,
            OperationIntent.CreateStoragePool,
            OperationIntent.DeleteStoragePool,
            OperationIntent.CreateStorageTier,
            OperationIntent.DeleteStorageTier,
            OperationIntent.ResizeStorageTier,
            OperationIntent.CreateVirtualDisk,
            OperationIntent.DeleteVirtualDisk,
            OperationIntent.ResizeVirtualDisk,
            OperationIntent.RepairStorageObject,
            OperationIntent.ClearDisk,
            OperationIntent.RawDeviceWrite
        };

        Assert.Equal(
            expected.OrderBy(value => value),
            OperationSecurityCatalog.StorageStructureMutationIntents.OrderBy(value => value));
    }

    [Theory]
    [MemberData(nameof(StorageMutationIntents))]
    public async Task ProtectedDevelopmentMachine_RejectsEveryR4R5Intent_EvenInRealModeAsAdministrator(
        OperationIntent intent)
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine, ExecutionMode.Real, PrivilegeState.Administrator);
        var plan = fixture.CreatePlan(intent);

        var decision = await fixture.Policy.EvaluateAsync(plan, fixture.Context, CancellationToken.None);
        var authorization = await fixture.Authority.AuthorizeAsync(plan, fixture.Context, true, CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Rejected, decision.Kind);
        Assert.Equal("policy.protected-machine-storage-mutation", decision.Code);
        Assert.Equal(AuthorizationIssueKind.Rejected, authorization.Kind);
        Assert.Null(authorization.Token);
    }

    [Fact]
    public async Task ProtectedMachine_RejectionCannotBeBypassedWithForgedLowRiskOrCapabilities()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine, ExecutionMode.Real, PrivilegeState.Administrator);
        var original = fixture.CreatePlan(OperationIntent.InitializeDisk);
        var forged = Rehash(original with
        {
            Risk = RiskLevel.R0ReadOnly,
            RequiredCapabilities = ExecutionCapability.ReadInventory
        });

        var decision = await fixture.Policy.EvaluateAsync(forged, fixture.Context, CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Rejected, decision.Kind);
        Assert.Equal("policy.protected-machine-storage-mutation", decision.Code);
    }

    [Fact]
    public async Task RealAndAdministrator_DoNotAddACapabilityMissingFromTheEnvironment()
    {
        var fixture = Fixture.Create(
            EnvironmentKind.UserProvidedDisposableMachine,
            ExecutionMode.Real,
            PrivilegeState.Administrator,
            AllCapabilities & ~ExecutionCapability.MutateStorageStructure);
        var plan = fixture.CreatePlan(OperationIntent.InitializeDisk);

        var decision = await fixture.Policy.EvaluateAsync(plan, fixture.Context, CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Rejected, decision.Kind);
        Assert.Equal("policy.capability-denied", decision.Code);
    }

    [Fact]
    public async Task DefaultPlanner_UsesAuthoritativeRiskCapabilityAndInventory()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var request = fixture.CreateRequest(
            OperationIntent.RunFileTest,
            new Dictionary<string, string> { ["EstimatedWriteBytes"] = "4096" });
        var planner = new DefaultOperationPlanner(new FixedOperationInventoryVersionSource(Fixture.InventoryVersion));

        var plan = await planner.BuildAsync(request, CancellationToken.None);

        Assert.Equal(RiskLevel.R2RecoverableFileWrite, plan.Risk);
        Assert.Equal(
            ExecutionCapability.ReadFileTest |
            ExecutionCapability.WriteFileTest |
            ExecutionCapability.RunExternalTestTool,
            plan.RequiredCapabilities);
        Assert.Equal(Fixture.InventoryVersion, plan.InventoryVersion);
        Assert.Equal(4096, plan.EstimatedWriteBytes);
        Assert.Equal(OperationPlanHasher.Compute(plan), plan.PlanHash);
    }

    [Fact]
    public async Task Authorization_IsBoundToPlanHash()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        var changed = Rehash(plan with
        {
            Parameters = new Dictionary<string, string> { ["changed"] = "true" }
        });

        var result = fixture.Authority.Consume(token, changed, fixture.Context);

        Assert.Equal(AuthorizationValidationKind.PlanHashMismatch, result.Kind);
    }

    [Fact]
    public async Task Authorization_IsBoundToInventoryVersion()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        var changedContext = fixture.Context with { CurrentInventoryVersion = "inventory-v2" };

        var result = fixture.Authority.Consume(token, plan, changedContext);

        Assert.Equal(AuthorizationValidationKind.InventoryMismatch, result.Kind);
    }

    [Fact]
    public async Task Authorization_IsBoundToMachine()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        var changedContext = fixture.Context with
        {
            Environment = fixture.Context.Environment with { MachineBinding = "another-machine" },
            CurrentMachineBinding = "another-machine"
        };

        var result = fixture.Authority.Consume(token, plan, changedContext);

        Assert.Equal(AuthorizationValidationKind.MachineMismatch, result.Kind);
    }

    [Fact]
    public async Task Authorization_IsBoundToEnvironment()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        var otherEnvironment = fixture.Context.Environment with { Id = EnvironmentId.New() };
        var changedContext = fixture.Context with { Environment = otherEnvironment };

        var result = fixture.Authority.Consume(token, plan, changedContext);

        Assert.Equal(AuthorizationValidationKind.EnvironmentMismatch, result.Kind);
    }

    [Fact]
    public async Task Authorization_IsBoundToTargetSet()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        var changed = Rehash(plan with
        {
            Targets = [new StorageObjectId(plan.SystemId, StorageObjectKind.PhysicalDisk, "different-target")]
        });

        var result = fixture.Authority.Consume(token, changed, fixture.Context);

        Assert.Equal(AuthorizationValidationKind.TargetMismatch, result.Kind);
    }

    [Fact]
    public async Task Authorization_ExpiresAndCannotBeConsumed()
    {
        var clock = new ManualTimeProvider(Fixture.Now);
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine, timeProvider: clock);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);
        clock.Advance(InMemoryOperationAuthority.DefaultLifetime);

        var result = fixture.Authority.Consume(token, plan, fixture.Context);

        Assert.Equal(AuthorizationValidationKind.Expired, result.Kind);
    }

    [Fact]
    public async Task Authorization_IsOneTimeAndRejectsReuse()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.ReadInventory);
        var token = await fixture.IssueAsync(plan);

        var first = fixture.Authority.Consume(token, plan, fixture.Context);
        var second = fixture.Authority.Consume(token, plan, fixture.Context);

        Assert.Equal(AuthorizationValidationKind.Valid, first.Kind);
        Assert.Equal(AuthorizationValidationKind.AlreadyUsed, second.Kind);
    }

    [Fact]
    public async Task InstallExternalTool_CannotBeAuthorizedWithoutExplicitConfirmation()
    {
        var fixture = Fixture.Create(EnvironmentKind.ProtectedDevelopmentMachine);
        var plan = fixture.CreatePlan(OperationIntent.InstallExternalTool);

        var result = await fixture.Authority.AuthorizeAsync(plan, fixture.Context, false, CancellationToken.None);

        Assert.Equal(AuthorizationIssueKind.ConfirmationRequired, result.Kind);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task ExecutorGate_RevalidatesPolicyBeforeConsumingAuthorization()
    {
        var fixture = Fixture.Create(EnvironmentKind.UserProvidedDisposableMachine);
        var plan = fixture.CreatePlan(OperationIntent.InitializeDisk);
        var token = await fixture.IssueAsync(plan);
        var protectedContext = fixture.Context with
        {
            Environment = fixture.Context.Environment with { Kind = EnvironmentKind.ProtectedDevelopmentMachine }
        };
        var gate = new ExecutorGate(fixture.Policy, fixture.Authority, fixture.Clock);

        var events = await CollectAsync(gate.ExecuteAsync(
            plan,
            protectedContext,
            token,
            new LocalStorageMutationExecutor(fixture.Clock)));

        Assert.Single(events);
        Assert.Equal(ExecutionEventKind.Rejected, events[0].Kind);
        Assert.Equal("policy.protected-machine-storage-mutation", events[0].Code);

        var stillUsable = fixture.Authority.Consume(token, plan, fixture.Context);
        Assert.Equal(AuthorizationValidationKind.Valid, stillUsable.Kind);
    }

    [Fact]
    public async Task LocalStorageMutationExecutor_AlwaysRejectsAfterAnOtherwiseValidGate()
    {
        var fixture = Fixture.Create(EnvironmentKind.UserProvidedDisposableMachine);
        var plan = fixture.CreatePlan(OperationIntent.InitializeDisk);
        var token = await fixture.IssueAsync(plan);
        var gate = new ExecutorGate(fixture.Policy, fixture.Authority, fixture.Clock);

        var events = await CollectAsync(gate.ExecuteAsync(
            plan,
            fixture.Context,
            token,
            new LocalStorageMutationExecutor(fixture.Clock)));

        Assert.Single(events);
        Assert.Equal(ExecutionEventKind.Rejected, events[0].Kind);
        Assert.Equal("executor.local-storage-mutation-unavailable", events[0].Code);
    }

    private static OperationPlan Rehash(OperationPlan plan) =>
        plan with { PlanHash = OperationPlanHasher.Compute(plan) };

    private static async Task<List<ExecutionEvent>> CollectAsync(IAsyncEnumerable<ExecutionEvent> source)
    {
        var events = new List<ExecutionEvent>();
        await foreach (var item in source)
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class Fixture
    {
        public static readonly DateTimeOffset Now = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        public const string InventoryVersion = "inventory-v1";
        public const string Machine = "machine-binding";

        private Fixture(
            SystemId systemId,
            EnvironmentId environmentId,
            ExecutionContext context,
            OperationPolicyEvaluator policy,
            InMemoryOperationAuthority authority,
            TimeProvider clock)
        {
            SystemId = systemId;
            EnvironmentId = environmentId;
            Context = context;
            Policy = policy;
            Authority = authority;
            Clock = clock;
        }

        public SystemId SystemId { get; }
        public EnvironmentId EnvironmentId { get; }
        public ExecutionContext Context { get; }
        public OperationPolicyEvaluator Policy { get; }
        public InMemoryOperationAuthority Authority { get; }
        public TimeProvider Clock { get; }

        public static Fixture Create(
            EnvironmentKind kind,
            ExecutionMode mode = ExecutionMode.Simulation,
            PrivilegeState privilege = PrivilegeState.StandardUser,
            ExecutionCapability? allowedCapabilities = null,
            TimeProvider? timeProvider = null)
        {
            var systemId = SystemId.New();
            var environmentId = EnvironmentId.New();
            var environment = new EnvironmentProfile(
                environmentId,
                kind,
                Machine,
                allowedCapabilities ?? AllCapabilities,
                kind == EnvironmentKind.UserProvidedDisposableMachine,
                Now);
            var context = new ExecutionContext(environment, mode, privilege, Machine, InventoryVersion, false);
            var policy = new OperationPolicyEvaluator();
            var clock = timeProvider ?? new ManualTimeProvider(Now);
            var authority = new InMemoryOperationAuthority(policy, clock);
            return new(systemId, environmentId, context, policy, authority, clock);
        }

        public OperationRequest CreateRequest(
            OperationIntent intent,
            IReadOnlyDictionary<string, string>? parameters = null) =>
            new(
                OperationId.New(),
                EnvironmentId,
                SystemId,
                intent,
                [new StorageObjectId(SystemId, StorageObjectKind.PhysicalDisk, "disk-0")],
                parameters ?? new Dictionary<string, string>(),
                Now);

        public OperationPlan CreatePlan(OperationIntent intent)
        {
            var request = CreateRequest(intent);
            var definition = OperationSecurityCatalog.Get(intent);
            return OperationPlan.Create(
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
        }

        public async Task<OperationAuthorizationToken> IssueAsync(OperationPlan plan)
        {
            var result = await Authority.AuthorizeAsync(plan, Context, true, CancellationToken.None);
            Assert.Equal(AuthorizationIssueKind.Issued, result.Kind);
            return Assert.IsType<OperationAuthorizationToken>(result.Token);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
