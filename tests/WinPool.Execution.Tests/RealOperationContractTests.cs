using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Execution.Tests;

public sealed class RealOperationContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly ExecutionCapability Capabilities = ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure;

    [Fact]
    public async Task RealPlan_BindsTypedStepsSessionMachineAndTargetFacts()
    {
        var fixture = Fixture.Create();
        var plan = fixture.Plan;
        var authority = fixture.Authority;

        Assert.Equal(RiskLevel.R4StorageStructureMutation, plan.Risk);
        Assert.Equal(OperationPlanHasher.Compute(plan), plan.PlanHash);
        Assert.Equal(PolicyDecisionKind.RequiresConfirmation,
            (await fixture.Policy.EvaluateAsync(plan, fixture.Context, CancellationToken.None)).Kind);
        Assert.Equal(AuthorizationIssueKind.ConfirmationRequired,
            (await authority.AuthorizeAsync(plan, fixture.Context, true, CancellationToken.None)).Kind);
        Assert.Equal(AuthorizationIssueKind.Rejected,
            (await authority.AuthorizeConfirmedRealAsync(plan, fixture.Context, "wrong hash", CancellationToken.None)).Kind);

        var issued = await authority.AuthorizeConfirmedRealAsync(plan, fixture.Context, plan.PlanHash, CancellationToken.None);
        var token = Assert.IsType<OperationAuthorizationToken>(issued.Token);
        Assert.Equal(AuthorizationValidationKind.SessionMismatch,
            authority.Consume(token, plan, fixture.Context with
            {
                RealSession = fixture.Context.RealSession! with { ProductSessionId = "other" }
            }).Kind);
        Assert.Equal(AuthorizationValidationKind.TargetMismatch,
            authority.Consume(token, plan, fixture.Context with { CurrentTargetFingerprint = "changed" }).Kind);
        Assert.Equal(AuthorizationValidationKind.TargetMismatch,
            authority.Consume(token, plan, fixture.Context with { CurrentPhysicalMemberFingerprint = "other disk" }).Kind);
        Assert.Equal(AuthorizationValidationKind.Valid, authority.Consume(token, plan, fixture.Context).Kind);
        Assert.Equal(AuthorizationValidationKind.AlreadyUsed, authority.Consume(token, plan, fixture.Context).Kind);
    }

    [Fact]
    public void ChangingCommandParameterInvalidatesFrozenHash()
    {
        var fixture = Fixture.Create();
        var altered = fixture.Plan with
        {
            RealOperation = fixture.Plan.RealOperation! with
            {
                Steps = [fixture.Plan.RealOperation.Steps[0] with
                {
                    Command = new CreatePartitionCommand(
                        RealTargetReference.ForExisting(fixture.Disk), RealPartitionRole.BasicData,
                        2L * 1024 * 1024, 2048L * 1024 * 1024)
                }]
            }
        };

        Assert.NotEqual(fixture.Plan.PlanHash, OperationPlanHasher.Compute(altered));
        Assert.Equal("policy.plan-hash-invalid", fixture.Policy.Evaluate(altered, fixture.Context).Code);
    }

    [Fact]
    public void PreparationIntentHash_UsesTypedParametersAndTrustedSession()
    {
        var fixture = Fixture.Create();
        var original = new RealOperationIntentRequest(fixture.Plan.Intent, fixture.SystemId,
            fixture.Plan.Targets, fixture.Plan.RealOperation!.Steps, fixture.Plan.RealOperation.ExpectedFinalState);
        var first = RealOperationIntentHasher.Compute(original, fixture.Context.RealSession!,
            fixture.Context.CurrentMachineBinding);
        var again = RealOperationIntentHasher.Compute(original, fixture.Context.RealSession!,
            fixture.Context.CurrentMachineBinding);
        var changed = original with
        {
            Steps = [original.Steps[0] with
            {
                Command = new CreatePartitionCommand(RealTargetReference.ForExisting(fixture.Disk),
                    RealPartitionRole.BasicData, 2L << 20, 2048L << 20)
            }]
        };

        Assert.Equal(first, again);
        Assert.NotEqual(first, RealOperationIntentHasher.Compute(changed, fixture.Context.RealSession!,
            fixture.Context.CurrentMachineBinding));
        Assert.NotEqual(first, RealOperationIntentHasher.Compute(original,
            fixture.Context.RealSession! with { ProductSessionId = "next" },
            fixture.Context.CurrentMachineBinding));
    }

    [Fact]
    public void RealPlan_RoundTripsPolymorphicCommands()
    {
        var fixture = Fixture.Create();
        var restored = JsonSerializer.Deserialize<OperationPlan>(JsonSerializer.Serialize(fixture.Plan));

        Assert.NotNull(restored);
        Assert.IsType<CreatePartitionCommand>(Assert.Single(restored.RealOperation!.Steps).Command);
        Assert.Equal(fixture.Plan.PlanHash, OperationPlanHasher.Compute(restored));
    }

    [Theory]
    [InlineData(32768)]
    [InlineData(262144)]
    public void VirtualDisk_RejectsUnverifiedInterleave(int interleave)
    {
        var fixture = Fixture.Create();
        var pool = new StorageObjectId(fixture.SystemId, StorageObjectKind.StoragePool, "pool-id");
        var proposal = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk, fixture.SystemId, [pool],
            [Step("create-vd", new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool), "VD", 16L << 30, interleave, 1))],
            "VD exists");

        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(proposal));
    }

    [Fact]
    public void ExistingEfiFormatAndCreatedRecoveryWrongClusterAreRejected()
    {
        var fixture = Fixture.Create();
        var existing = new RealOperationIntentRequest(OperationIntent.FormatVolume, fixture.SystemId, [fixture.Partition],
            [Step("format", new FormatVolumeCommand(RealTargetReference.ForExisting(fixture.Partition), RealFileSystem.Fat32, 4096, false, "EFI"))],
            "Formatted");
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(existing));

        var create = new RealOperationIntentRequest(OperationIntent.CreatePartition, fixture.SystemId, [fixture.Disk],
            [Step("create", new CreatePartitionCommand(RealTargetReference.ForExisting(fixture.Disk),
                RealPartitionRole.Recovery, 2L << 20, 1024L << 20)),
             Step("format", new FormatVolumeCommand(RealTargetReference.FromStep(StorageObjectKind.Partition, "create"),
                RealFileSystem.Ntfs, 65536, false, "REC"), ["create"])],
            "Recovery exists");
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(create));

        RealOperationValidator.Validate(create with
        {
            Steps = [create.Steps[0], create.Steps[1] with
            {
                Command = new FormatVolumeCommand(
                    RealTargetReference.FromStep(StorageObjectKind.Partition, "create"),
                    RealFileSystem.Ntfs, 4096, false, "REC")
            }]
        });
    }

    [Fact]
    public void TieredVirtualDiskRequiresExactTierAndPriorOutputDependency()
    {
        var fixture = Fixture.Create();
        var pool = new StorageObjectId(fixture.SystemId, StorageObjectKind.StoragePool, "pool-id");
        var tier = new StorageObjectId(fixture.SystemId, StorageObjectKind.StorageTier, "tier-id");
        var proposal = new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk, fixture.SystemId, [pool, tier],
            [Step("create", new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool),
                RealTargetReference.ForExisting(tier), "HDD VD", 16L << 30))], "Tiered VD exists");
        RealOperationValidator.Validate(proposal);

        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(proposal with
        {
            Targets = [pool]
        }));
    }

    [Fact]
    public void CreatedTargetRequiresEarlierStepDependencyAndExistingTargetMustBeListed()
    {
        var fixture = Fixture.Create();
        var proposal = new RealOperationIntentRequest(OperationIntent.CreatePartition, fixture.SystemId, [fixture.Disk],
            [Step("format", new FormatVolumeCommand(RealTargetReference.FromStep(StorageObjectKind.Partition, "create"),
                RealFileSystem.Ntfs, 65536, false, "Data"))], "Data exists");

        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(proposal));
    }

    [Fact]
    public void RealPolicyRejectsUnarmedNonAdminAndDisposable()
    {
        var fixture = Fixture.Create();
        Assert.Equal("policy.real-session-required",
            fixture.Policy.Evaluate(fixture.Plan, fixture.Context with { Privilege = PrivilegeState.StandardUser }).Code);
        Assert.Equal("policy.real-session-required",
            fixture.Policy.Evaluate(fixture.Plan, fixture.Context with
            {
                RealSession = fixture.Context.RealSession! with { IsArmed = false }
            }).Code);
        Assert.Equal("policy.local-machine-required",
            fixture.Policy.Evaluate(fixture.Plan, fixture.Context with
            {
                Environment = fixture.Context.Environment with
                {
                    Kind = EnvironmentKind.UserProvidedDisposableMachine,
                    IsUserProvidedDisposableEnvironment = true
                }
            }).Code);
    }

    [Fact]
    public void RealPolicyIgnoresRawScanVersionButRejectsChangedTargetClosure()
    {
        var fixture = Fixture.Create();
        var rescan = fixture.Context with { RawInventorySnapshotVersion = "new scan time and unrelated disk" };
        Assert.Equal(PolicyDecisionKind.RequiresConfirmation, fixture.Policy.Evaluate(fixture.Plan, rescan).Kind);

        var targetChanged = rescan with { CurrentInventoryVersion = "changed target closure" };
        Assert.Equal("policy.inventory-changed", fixture.Policy.Evaluate(fixture.Plan, targetChanged).Code);
        Assert.Equal("policy.real-binding-mismatch", fixture.Policy.Evaluate(fixture.Plan,
            rescan with { CurrentPhysicalMemberFingerprint = "different physical disk" }).Code);
    }

    [Fact]
    public void StandaloneClearDiskIsSingleExactR5Plan()
    {
        var fixture = Fixture.Create();
        var physical = new StorageObjectId(fixture.SystemId, StorageObjectKind.PhysicalDisk, "physical-member");
        var proposal = new RealOperationIntentRequest(OperationIntent.ClearDisk, fixture.SystemId, [fixture.Disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(fixture.Disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);
        var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), fixture.Context.Environment,
            fixture.Context.RealSession!, fixture.Context.CurrentInventoryVersion,
            fixture.Context.CurrentTargetFingerprint!, fixture.Context.CurrentPhysicalMemberFingerprint!,
            "fresh provider evidence", Now, Now.AddMinutes(2));

        Assert.Equal(RiskLevel.R5IrreversibleOrBroadDestruction, plan.Risk);
        Assert.Equal(PolicyDecisionKind.RequiresConfirmation, fixture.Policy.Evaluate(plan, fixture.Context).Kind);
        RealOperationValidator.Validate(proposal with { Targets = [fixture.Disk, physical] });
        var downgraded = plan with { Risk = RiskLevel.R4StorageStructureMutation };
        downgraded = downgraded with { PlanHash = OperationPlanHasher.Compute(downgraded) };
        Assert.Equal("policy.risk-downgrade", fixture.Policy.Evaluate(downgraded, fixture.Context).Code);
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(proposal with
        {
            Steps = [proposal.Steps[0] with
            {
                Command = new ClearDiskCommand(RealTargetReference.ForExisting(fixture.Disk), true)
            }]
        }));
    }

    [Fact]
    public void StandaloneClearDiskRejectsMixedTargetsStepsAndUnclearOutcome()
    {
        var fixture = Fixture.Create();
        var otherDisk = new StorageObjectId(fixture.SystemId, StorageObjectKind.OsDisk, "other-disk");
        var partition = fixture.Partition;
        var proposal = new RealOperationIntentRequest(OperationIntent.ClearDisk, fixture.SystemId, [fixture.Disk],
            [Step("clear", new ClearDiskCommand(RealTargetReference.ForExisting(fixture.Disk), false))],
            RealOperationValidator.ClearDiskExpectedFinalState);

        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            proposal with { Targets = [fixture.Disk, otherDisk] }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            proposal with { Targets = [fixture.Disk, partition] }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            proposal with { ExpectedFinalState = "Cleared" }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            proposal with { Steps = [proposal.Steps[0], Step("gpt",
                new InitializeGptCommand(RealTargetReference.ForExisting(fixture.Disk)), ["clear"])] }));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            proposal with { Steps = [proposal.Steps[0] with
            {
                Command = new ClearDiskCommand(RealTargetReference.FromStep(StorageObjectKind.OsDisk, "previous"), false)
            }] }));
    }

    [Fact]
    public void RealPlanRejectsExtraFreeFormParameters()
    {
        var fixture = Fixture.Create();
        var altered = fixture.Plan with
        {
            Parameters = new Dictionary<string, string> { ["AdditionalScript"] = "Get-Disk | Clear-Disk" }
        };
        altered = altered with { PlanHash = OperationPlanHasher.Compute(altered) };

        Assert.Equal("policy.real-plan-invalid", fixture.Policy.Evaluate(altered, fixture.Context).Code);
    }

    private static RealOperationStep Step(string id, RealStorageCommand command, IReadOnlyList<string>? depends = null) =>
        new(id, command, depends ?? [], "Fresh identity and capability checked", "Postcondition checked", "Data loss listed", "Provider evidence");

    private sealed class Fixture
    {
        private Fixture(SystemId systemId, StorageObjectId disk, StorageObjectId partition,
            OperationPlan plan, ExecutionContext context, OperationPolicyEvaluator policy,
            InMemoryOperationAuthority authority)
        {
            SystemId = systemId;
            Disk = disk;
            Partition = partition;
            Plan = plan;
            Context = context;
            Policy = policy;
            Authority = authority;
        }

        public SystemId SystemId { get; }
        public StorageObjectId Disk { get; }
        public StorageObjectId Partition { get; }
        public OperationPlan Plan { get; }
        public ExecutionContext Context { get; }
        public OperationPolicyEvaluator Policy { get; }
        public InMemoryOperationAuthority Authority { get; }

        public static Fixture Create()
        {
            var systemId = SystemId.New();
            var disk = new StorageObjectId(systemId, StorageObjectKind.OsDisk, "disk-unique-id");
            var partition = new StorageObjectId(systemId, StorageObjectKind.Partition, "partition-guid");
            var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine,
                "stable-machine-binding", Capabilities, false, Now);
            var session = new TrustedRealSession(SessionId.New(), "product-session", "process-instance", 1234,
                Now.AddMinutes(-1), "C:\\WinPool\\App.exe", true);
            var context = new ExecutionContext(environment, ExecutionMode.Real, PrivilegeState.Administrator,
                environment.MachineBinding, "fresh-inventory-version", false)
            {
                RealSession = session,
                CurrentTargetFingerprint = "full-topology-fingerprint",
                CurrentPhysicalMemberFingerprint = "physical-member-fingerprint"
            };
            var proposal = new RealOperationIntentRequest(OperationIntent.CreatePartition, systemId, [disk],
                [Step("create", new CreatePartitionCommand(RealTargetReference.ForExisting(disk),
                    RealPartitionRole.BasicData, 2L << 20, 1024L << 20))], "BasicData partition exists");
            var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), environment, session,
                context.CurrentInventoryVersion, context.CurrentTargetFingerprint,
                context.CurrentPhysicalMemberFingerprint, "fresh provider capability",
                Now, Now.AddMinutes(2));
            var policy = new OperationPolicyEvaluator();
            return new(systemId, disk, partition, plan, context, policy,
                new InMemoryOperationAuthority(policy, new FixedTimeProvider()));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
