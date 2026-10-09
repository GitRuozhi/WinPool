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

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void InitializationContinuationAcceptsExactMsrNormalizationAndNewDataDependencies(
        bool remove, bool msr, bool data)
    {
        var fixture = Fixture.Create();
        var proposal = InitializationContinuation(fixture, remove, msr, data);
        RealOperationValidator.Validate(proposal);
        var physical = new StorageObjectId(fixture.SystemId, StorageObjectKind.PhysicalDisk, "physical-member");
        RealOperationValidator.Validate(proposal with { Targets = proposal.Targets.Append(physical).ToArray() });
        var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), fixture.Context.Environment,
            fixture.Context.RealSession!, fixture.Context.CurrentInventoryVersion,
            fixture.Context.CurrentTargetFingerprint!, fixture.Context.CurrentPhysicalMemberFingerprint!,
            "observed provider initialization", Now, Now.AddMinutes(2));
        Assert.True(RealOperationValidator.IsValid(plan));
        Assert.Equal(OperationPlanHasher.Compute(plan), plan.PlanHash);
    }

    [Fact]
    public void InitializationWithoutNativeMsrAllowsMsrOrDataCreationOnlyOnExactDisk()
    {
        var fixture = Fixture.Create();
        RealOperationValidator.Validate(InitializationContinuation(fixture, false, true, false));
        RealOperationValidator.Validate(InitializationContinuation(fixture, false, false, true));
        var original = new RealOperationIntentRequest(OperationIntent.InitializeDisk, fixture.SystemId, [fixture.Disk],
            [Step("init", new InitializeGptCommand(RealTargetReference.ForExisting(fixture.Disk)))], "GPT observed");
        RealOperationValidator.Validate(original);
        RealOperationValidator.Validate(original with
        {
            Steps = [original.Steps[0], Step("msr", new CreatePartitionCommand(
                RealTargetReference.ForExisting(fixture.Disk), RealPartitionRole.Msr, 1L << 20, 16L << 20), ["init"])]
        });
    }

    [Theory]
    [InlineData("second-disk")]
    [InlineData("unrelated-target")]
    [InlineData("second-partition")]
    [InlineData("missing-delete-target")]
    [InlineData("second-delete")]
    [InlineData("delete-after-create")]
    [InlineData("initialize-and-delete")]
    [InlineData("data-on-other-disk")]
    [InlineData("second-data")]
    [InlineData("efi")]
    [InlineData("recovery")]
    [InlineData("wrong-data-offset")]
    [InlineData("format-existing")]
    [InlineData("format-msr")]
    [InlineData("letter-existing")]
    [InlineData("letter-msr")]
    [InlineData("letter-previous")]
    [InlineData("missing-data-dependency")]
    [InlineData("full-format")]
    [InlineData("second-format")]
    [InlineData("format-after-letter")]
    public void InitializationContinuationRejectsArbitraryDeletionTargetsRolesAndOrdering(string change)
    {
        var fixture = Fixture.Create();
        var proposal = InitializationContinuation(fixture, true, true, true);
        var steps = proposal.Steps.ToList();
        var other = new StorageObjectId(fixture.SystemId, StorageObjectKind.OsDisk, "other-disk");
        switch (change)
        {
            case "second-disk": proposal = proposal with { Targets = proposal.Targets.Append(other).ToArray() }; break;
            case "unrelated-target": proposal = proposal with { Targets = proposal.Targets.Append(
                new StorageObjectId(fixture.SystemId, StorageObjectKind.Volume, "volume")).ToArray() }; break;
            case "second-partition": proposal = proposal with { Targets = proposal.Targets.Append(
                new StorageObjectId(fixture.SystemId, StorageObjectKind.Partition, "other-partition")).ToArray() }; break;
            case "missing-delete-target": proposal = proposal with { Targets = [fixture.Disk] }; break;
            case "second-delete": steps.Insert(1, Step("delete-again", steps[0].Command)); break;
            case "delete-after-create": (steps[0], steps[1]) = (steps[1], steps[0]); break;
            case "initialize-and-delete": steps.Insert(0, Step("init", new InitializeGptCommand(RealTargetReference.ForExisting(fixture.Disk)))); break;
            case "data-on-other-disk":
                proposal = proposal with { Targets = proposal.Targets.Append(other).ToArray() };
                steps[2] = steps[2] with { Command = ((CreatePartitionCommand)steps[2].Command) with { Disk = RealTargetReference.ForExisting(other) } };
                break;
            case "second-data": steps.Insert(3, Step("data-again", steps[2].Command)); break;
            case "efi": steps[2] = steps[2] with { Command = ((CreatePartitionCommand)steps[2].Command) with { Role = RealPartitionRole.Efi } }; break;
            case "recovery": steps[2] = steps[2] with { Command = ((CreatePartitionCommand)steps[2].Command) with { Role = RealPartitionRole.Recovery } }; break;
            case "wrong-data-offset": steps[2] = steps[2] with { Command = ((CreatePartitionCommand)steps[2].Command) with { OffsetBytes = 1L << 20 } }; break;
            case "format-existing": steps[3] = steps[3] with { Command = ((FormatVolumeCommand)steps[3].Command) with { Partition = RealTargetReference.ForExisting(fixture.Partition) } }; break;
            case "format-msr": steps[3] = steps[3] with { Command = ((FormatVolumeCommand)steps[3].Command) with { Partition = RealTargetReference.FromStep(StorageObjectKind.Partition, "msr") } }; break;
            case "letter-existing": steps[4] = steps[4] with { Command = ((SetDriveLetterCommand)steps[4].Command) with { Partition = RealTargetReference.ForExisting(fixture.Partition) } }; break;
            case "letter-msr": steps[4] = steps[4] with { Command = ((SetDriveLetterCommand)steps[4].Command) with { Partition = RealTargetReference.FromStep(StorageObjectKind.Partition, "msr") } }; break;
            case "letter-previous": steps[4] = steps[4] with { Command = ((SetDriveLetterCommand)steps[4].Command) with { PreviousLetter = 'D' } }; break;
            case "missing-data-dependency": break;
            case "full-format": steps[3] = steps[3] with { Command = ((FormatVolumeCommand)steps[3].Command) with { Full = true } }; break;
            case "second-format": steps.Insert(4, Step("format-again", steps[3].Command)); break;
            case "format-after-letter": (steps[3], steps[4]) = (steps[4], steps[3]); break;
        }
        // Preserve a valid chain so these cases exercise the continuation
        // shape itself rather than failing only on generic step ordering.
        steps = Chain(steps);
        if (change == "missing-data-dependency") steps[4] = steps[4] with { DependsOn = ["format"] };
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(proposal with { Steps = steps }));
    }

    [Theory]
    [InlineData(OperationIntent.CreatePartition)]
    [InlineData(OperationIntent.CreateStoragePool)]
    [InlineData(OperationIntent.CreateVirtualDisk)]
    public void InitializationDeletionIsNotPermittedInOrdinaryCreationIntents(OperationIntent intent)
    {
        var fixture = Fixture.Create();
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(
            InitializationContinuation(fixture, true, true, true) with { Intent = intent }));
    }

    private static RealOperationIntentRequest InitializationContinuation(Fixture fixture, bool remove, bool msr, bool data)
    {
        var disk = RealTargetReference.ForExisting(fixture.Disk);
        var steps = new List<RealOperationStep>();
        if (remove) steps.Add(Step("delete-msr", new DeletePartitionCommand(RealTargetReference.ForExisting(fixture.Partition))));
        if (msr) steps.Add(Step("msr", new CreatePartitionCommand(disk, RealPartitionRole.Msr, 1L << 20, 16L << 20)));
        if (data)
        {
            steps.Add(Step("data", new CreatePartitionCommand(disk, RealPartitionRole.BasicData, (msr ? 17L : 1L) << 20, 128L << 20)));
            var partition = RealTargetReference.FromStep(StorageObjectKind.Partition, "data");
            steps.Add(Step("format", new FormatVolumeCommand(partition, RealFileSystem.Ntfs, 65536, false, "Data")));
            steps.Add(Step("letter", new SetDriveLetterCommand(partition, null, 'E')));
        }
        return new RealOperationIntentRequest(OperationIntent.InitializeDisk, fixture.SystemId,
            remove ? [fixture.Disk, fixture.Partition] : [fixture.Disk], Chain(steps), "Selected GPT layout");
    }

    private static List<RealOperationStep> Chain(IReadOnlyList<RealOperationStep> steps) => steps.Select((step, index) =>
        step with { DependsOn = steps.Take(index).Select(previous => previous.Id).ToArray() }).ToList();

    private static RealOperationStep Step(string id, RealStorageCommand command, IReadOnlyList<string>? depends = null) =>
        new(id, command, depends ?? [], "Fresh identity and capability checked", "Postcondition checked", "Data loss listed", "Provider evidence");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMaximumIsExclusiveAndCoveredByIntentAndPlanHash(bool tiered)
    {
        var fixture = Fixture.Create();
        var pool = new StorageObjectId(fixture.SystemId, StorageObjectKind.StoragePool, "pool");
        var tier = new StorageObjectId(fixture.SystemId, StorageObjectKind.StorageTier, "template");
        RealStorageCommand Command(long bytes, bool maximum) => tiered
            ? new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), RealTargetReference.ForExisting(tier), "VD", bytes, maximum)
            : new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool), "VD", bytes, 65536, 1, maximum);
        RealOperationIntentRequest Proposal(RealStorageCommand command) => new(OperationIntent.CreateVirtualDisk,
            fixture.SystemId, tiered ? [pool, tier] : [pool], [Step("create", command)], "Requested capacity mode");
        var native = Proposal(Command(0, true));
        RealOperationValidator.Validate(native);
        RealOperationValidator.Validate(Proposal(Command(16L << 30, false)));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(Command(16L << 30, true))));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(Command(0, false))));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(Command(-1, true))));
        Assert.NotEqual(RealOperationIntentHasher.Compute(native, fixture.Context.RealSession!, "machine"),
            RealOperationIntentHasher.Compute(Proposal(Command(0, false)), fixture.Context.RealSession!, "machine"));
        var nativePlan = fixture.Plan with { RealOperation = fixture.Plan.RealOperation! with { Steps = native.Steps } };
        var changed = nativePlan with { RealOperation = nativePlan.RealOperation! with { Steps = Proposal(Command(0, false)).Steps } };
        Assert.NotEqual(OperationPlanHasher.Compute(nativePlan), OperationPlanHasher.Compute(changed));
        Assert.Contains("\"UseMaximumSize\":true", JsonSerializer.Serialize(native));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyExplicitCreationJsonAndFrozenHashRemainCompatible(bool tiered)
    {
        var fixture = Fixture.Create();
        var reference = RealTargetReference.ForExisting(new(fixture.SystemId, StorageObjectKind.StoragePool, "pool"));
        RealStorageCommand command = tiered
            ? new CreateTieredVirtualDiskCommand(reference, RealTargetReference.ForExisting(new(fixture.SystemId, StorageObjectKind.StorageTier, "tier")), "VD", 16L << 30)
            : new CreateVirtualDiskCommand(reference, "VD", 16L << 30, 65536, 1);
        var json = JsonSerializer.Serialize(command);
        Assert.DoesNotContain("UseMaximumSize", json); // Exactly the pre-flag command shape.
        Assert.DoesNotContain("CreationMechanism", json);
        var restoredCommand = JsonSerializer.Deserialize<RealStorageCommand>(json)!;
        Assert.Equal(command, restoredCommand);
        Assert.False(tiered ? ((CreateTieredVirtualDiskCommand)restoredCommand).UseMaximumSize
            : ((CreateVirtualDiskCommand)restoredCommand).UseMaximumSize);
        var plan = fixture.Plan with { RealOperation = fixture.Plan.RealOperation! with { Steps = [Step("create", command)] } };
        plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        var legacyJson = JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("UseMaximumSize", legacyJson);
        var restoredPlan = JsonSerializer.Deserialize<OperationPlan>(legacyJson)!;
        Assert.Equal(plan.PlanHash, OperationPlanHasher.Compute(restoredPlan));
    }

    [Fact]
    public void AutomaticHddMechanismIsFrozenAndLegacyNativeMaximumRetainsExactTemplateMeaning()
    {
        var fixture = Fixture.Create();
        var pool = new StorageObjectId(fixture.SystemId, StorageObjectKind.StoragePool, "pool");
        var tier = new StorageObjectId(fixture.SystemId, StorageObjectKind.StorageTier, "template");
        var legacy = new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool), RealTargetReference.ForExisting(tier), "VD", 0, true);
        RealOperationIntentRequest Proposal(CreateTieredVirtualDiskCommand command) => new(OperationIntent.CreateVirtualDisk,
            fixture.SystemId, [pool, tier], [Step("create", command)], "Requested creation mechanism");
        var automatic = legacy with { CreationMechanism = TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd };
        RealOperationValidator.Validate(Proposal(automatic));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(automatic with { UseMaximumSize = false, SizeBytes = 16L << 30 })));
        Assert.Throws<ArgumentException>(() => RealOperationValidator.Validate(Proposal(automatic with { CreationMechanism = (TieredVirtualDiskCreationMechanism)99 })));
        var legacyJson = JsonSerializer.Serialize<RealStorageCommand>(legacy);
        Assert.DoesNotContain("CreationMechanism", legacyJson);
        Assert.Equal(legacy, Assert.IsType<CreateTieredVirtualDiskCommand>(JsonSerializer.Deserialize<RealStorageCommand>(legacyJson)));
        Assert.NotEqual(RealOperationIntentHasher.Compute(Proposal(legacy), fixture.Context.RealSession!, "machine"),
            RealOperationIntentHasher.Compute(Proposal(automatic), fixture.Context.RealSession!, "machine"));
        var oldPlan = fixture.Plan with { RealOperation = fixture.Plan.RealOperation! with { Steps = Proposal(legacy).Steps } };
        oldPlan = oldPlan with { PlanHash = OperationPlanHasher.Compute(oldPlan) };
        var restored = JsonSerializer.Deserialize<OperationPlan>(JsonSerializer.Serialize(oldPlan))!;
        Assert.Equal(oldPlan.PlanHash, OperationPlanHasher.Compute(restored));
        Assert.NotEqual(oldPlan.PlanHash, OperationPlanHasher.Compute(oldPlan with
            { RealOperation = oldPlan.RealOperation! with { Steps = Proposal(automatic).Steps } }));
    }

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
