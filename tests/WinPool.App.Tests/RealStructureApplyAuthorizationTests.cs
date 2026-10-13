using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealStructureApplyAuthorizationTests
{
    [Fact]
    public void UnconfirmedOrEndedApplyCannotAuthorizeAProposal()
    {
        var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.CreatePool));
        fixture.Authorization.Confirm();
        fixture.Authorization.ValidateProposal(fixture.CreatePool);
        fixture.Authorization.Stop();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.CreatePool));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("session")]
    [InlineData("system")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    public void ContextChangesEndContinuation(string change)
    {
        var fixture = new Fixture();
        fixture.Authorization.Confirm();
        Assert.Throws<InvalidOperationException>(() => fixture.Authorization.ValidateContext(
            change == "system" ? new SystemId(Guid.NewGuid()) : fixture.Session.SystemId,
            change == "session" ? "other-session" : "product-session",
            fixture.Session.BindingGeneration + (change == "generation" ? 1 : 0),
            change == "conflict", change == "unknown"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("physical")]
    [InlineData("clear-oem")]
    public void ProposalCannotExpandTheOriginallyConfirmedGoal(string change)
    {
        var fixture = new Fixture();
        fixture.Authorization.Confirm();
        var proposal = fixture.CreatePool;
        if (change == "clear-oem")
            proposal = RealOperationProposalFactory.OneStep(fixture.Session.SystemId, OperationIntent.ClearDisk, fixture.DirectDisk,
                new ClearDiskCommand(RealTargetReference.ForExisting(fixture.DirectDisk), true),
                RealOperationValidator.ClearDiskExpectedFinalState, "All data lost");
        else
        {
            var original = Assert.IsType<CreatePoolCommand>(proposal.Steps[0].Command);
            var target = new StorageObjectId(fixture.Session.SystemId, StorageObjectKind.PhysicalDisk, "unconfirmed:physical");
            proposal = proposal with
            {
                Targets = change == "physical" ? [target] : proposal.Targets,
                Steps = [proposal.Steps[0] with { Command = change == "name" ? original with { Name = "Other pool" }
                    : original with { PhysicalDisk = RealTargetReference.ForExisting(target) } }]
            };
        }
        if (change == "clear-oem") Assert.Throws<ArgumentException>(() => fixture.Authorization.ValidateProposal(proposal));
        else Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(proposal));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("dependency")]
    [InlineData("target")]
    public void FrozenPlanCannotChangeTheSubmittedCommandsOrTargets(string change)
    {
        var fixture = new Fixture();
        fixture.Authorization.Confirm();
        var proposal = fixture.CreatePool;
        var frozen = fixture.Frozen(proposal);
        var real = frozen.Plan.RealOperation!;
        var plan = frozen.Plan with
        {
            Targets = change == "target" ? frozen.Plan.Targets.Append(new(fixture.Session.SystemId, StorageObjectKind.PhysicalDisk, "other")).ToArray() : frozen.Plan.Targets,
            RealOperation = real with { Steps = [real.Steps[0] with
            {
                Command = change == "command" ? Assert.IsType<CreatePoolCommand>(real.Steps[0].Command) with { Name = "Substituted" } : real.Steps[0].Command,
                DependsOn = change == "dependency" ? ["unconfirmed-step"] : real.Steps[0].DependsOn
            }] }
        };
        plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(proposal, frozen with { Plan = plan }));
    }

    [Fact]
    public void VerifiedReceiptIntroducesTheExactPoolIdentityAndMaxProofMayBeAdded()
    {
        var fixture = new Fixture();
        fixture.Authorization.Confirm();
        var guessed = fixture.CreateVd("pool:guessed");
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(guessed));
        fixture.VerifyPool();
        var proposal = fixture.CreateVd("pool:verified");
        fixture.Authorization.ValidateProposal(proposal);
        var frozen = fixture.Frozen(proposal, "after-pool", addMaxPolicy: true);
        fixture.Authorization.ValidateFrozen(proposal, frozen);
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.CreateVd("pool:guessed")));
        var changedSize = proposal with { Steps = [proposal.Steps[0] with
        { Command = Assert.IsType<CreateVirtualDiskCommand>(proposal.Steps[0].Command) with { UseMaximumSize = false, SizeBytes = 32L << 30 } }] };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(changedSize));
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.CreatePool));
    }

    [Theory]
    [InlineData("topology")]
    [InlineData("physical")]
    [InlineData("agent-session")]
    [InlineData("max-source")]
    public void FreshPreparationCannotAdoptExternalChangesOrForeignMaxProof(string change)
    {
        var fixture = new Fixture();
        fixture.Authorization.Confirm();
        fixture.VerifyPool();
        var proposal = fixture.CreateVd("pool:verified");
        var frozen = fixture.Frozen(proposal, change == "topology" ? "external-change" : "after-pool", addMaxPolicy: true);
        var real = frozen.Plan.RealOperation!;
        if (change == "physical") real = real with { PhysicalMemberFingerprint = "other-physical" };
        if (change == "agent-session") real = real with { SessionBinding = "other-agent" };
        if (change == "max-source")
        {
            var command = Assert.IsType<CreateVirtualDiskCommand>(real.Steps[0].Command);
            real = real with { Steps = [real.Steps[0] with { Command = command with
            { MaximumCapacity = command.MaximumCapacity! with { SourceFingerprint = "foreign-proof" } } }] };
        }
        var plan = frozen.Plan with { RealOperation = real };
        plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(proposal, frozen with { Plan = plan }));
    }

    [Fact]
    public void FailedOrUnknownReceiptCannotIntroduceCreatedIdentities()
    {
        foreach (var unknown in new[] { false, true })
        {
            var fixture = new Fixture();
            fixture.Authorization.Confirm();
            var response = fixture.SuccessfulPoolReceipt() with
            { State = unknown ? RealOperationState.OutcomeUnknown : RealOperationState.PartiallyCompleted, RequiresReconciliation = unknown };
            Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveVerified(fixture.CreatePool, response, fixture.WithCreatedPool()));
            Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.CreatePool));
        }
    }

    [Fact]
    public void ConfirmationNamesTheMemberAndTheEntireRequestedResult()
    {
        var fixture = new Fixture();
        var summary = fixture.Authorization.Describe(fixture.Draft, fixture.Session.BaselineSourceFacts, false);
        Assert.Contains(fixture.Session.Baseline.PhysicalDisks[0].SerialNumber, summary);
        Assert.DoesNotContain(fixture.Authorization.PhysicalTarget.ProviderKey, summary);
        Assert.Contains("Requested pool", summary);
        Assert.Contains("Requested VD", summary);
        Assert.Contains("MAX", summary);
        Assert.Contains("NTFS / 64 KiB", summary);
        Assert.Contains("16 MiB MSR", summary);
        Assert.Contains("Original label", summary);
        Assert.Contains("drive letter E", summary);
        Assert.Contains("destroy existing partitions and data", summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OneConfirmedGoalAcceptsVerifiedOrdinaryOrHddCreationAndAutomaticLayout(bool hdd, bool maximum)
    {
        var fixture = new Fixture(hdd, maximum);
        var authorization = fixture.Authorization;
        authorization.Confirm();
        fixture.VerifyPool();
        var fresh = fixture.WithCreatedPool();
        var snapshot = fresh.Snapshot;
        var fingerprint = "after-pool";

        void Complete(RealOperationIntentRequest proposal, string? output = null, string? os = null, object? gpt = null)
        {
            var frozen = fixture.Frozen(proposal, fingerprint, addMaxPolicy: proposal.Steps[0].Command is
                CreateVirtualDiskCommand { UseMaximumSize: true } or CreateTieredVirtualDiskCommand { UseMaximumSize: true });
            authorization.ValidateFrozen(proposal, frozen);
            var nextFingerprint = fingerprint + ":next";
            var receipt = frozen with { State = RealOperationState.Succeeded, Steps = [new(proposal.Steps[0].Id, RealOperationStepState.Verified,
                null, null, JsonSerializer.Serialize(new { CreatedObjectId = output, CreatedOsDiskId = os,
                    GptInitialization = gpt, PostFingerprint = nextFingerprint, PhysicalMemberFingerprint = "physical-fingerprint" }))] };
            fresh = fixture.Project(snapshot);
            authorization.ObserveVerified(proposal, receipt, fresh);
            fingerprint = nextFingerprint;
        }

        StorageObjectId Id(StorageObjectKind kind, string id) => new(fixture.Session.SystemId, kind, id);
        if (hdd)
        {
            snapshot = snapshot with { StorageTiers = [new("tier:verified", true, "Requested pool_HDD", "HDD", "Simple", 0, 0,
                "pool:verified", null, [authorization.PhysicalTarget.ProviderKey], 1, 65536)] };
            Complete(RealOperationProposalFactory.CreateHddTierTemplate(fixture.Session.SystemId, Id(StorageObjectKind.StoragePool, "pool:verified"), "Requested pool_HDD"), "tier:verified");
        }
        snapshot = snapshot with
        {
            VirtualDisks = [new("vd:verified", true, "Requested VD", "Healthy", "OK", "Simple", "Fixed", 1, 65536,
                32L << 30, 32L << 30, "pool:verified", hdd ? ["tier:verified"] : [], [8])],
            OsDisks = [new("os:verified", "Requested VD", 8, "RAW", 32L << 30, false, false, false, null, "vd:verified")]
        };
        Complete(fixture.CreateVd("pool:verified"), "vd:verified", "os:verified");
        var osTarget = Id(StorageObjectKind.OsDisk, "os:verified");
        var msr = new PartitionInfo("partition:provider-msr", true, 8, 1, "MicrosoftReserved", 1L << 20, 16L << 20,
            false, false, "", "", "", null, 0, "Healthy", "OK", "", "os:verified",
            PartitionTypeId: "e3c9e316-0b5c-4db8-817d-f92df00215ae", GptType: "e3c9e316-0b5c-4db8-817d-f92df00215ae");
        snapshot = snapshot with { OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }], Partitions = [msr] };
        Complete(RealOperationProposalFactory.InitializeGpt(fixture.Session.SystemId, osTarget), gpt: new
        { OsDiskStableId = "os:verified", ProviderMsrStableId = msr.StableId });
        var layout = RealOperationProposalFactory.ConfigureInitializedDisk(fixture.Session.SystemId, osTarget,
            Id(StorageObjectKind.Partition, msr.StableId), true, 32L << 30, true, "Original label", 'E')!;
        foreach (var step in layout.Steps)
        {
            var command = step.Command;
            string? output = null;
            var operation = OperationIntent.InitializeDisk;
            switch (command)
            {
                case DeletePartitionCommand:
                    snapshot = snapshot with { Partitions = [] };
                    break;
                case CreatePartitionCommand create:
                    output = create.Role == RealPartitionRole.Msr ? "partition:msr" : "partition:data";
                    var partition = msr with { StableId = output, Offset = create.OffsetBytes, Size = create.SizeBytes,
                        PartitionNumber = create.Role == RealPartitionRole.Msr ? 1 : 2,
                        Type = create.Role == RealPartitionRole.Msr ? "MicrosoftReserved" : "BasicData",
                        PartitionTypeId = create.Role == RealPartitionRole.Msr ? msr.PartitionTypeId : "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                        GptType = create.Role == RealPartitionRole.Msr ? msr.GptType : "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" };
                    snapshot = snapshot with { Partitions = snapshot.Partitions.Append(partition).ToArray() };
                    if (create.Role != RealPartitionRole.Msr) operation = OperationIntent.CreatePartition;
                    break;
                case FormatVolumeCommand format:
                    command = format with { Partition = RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, "partition:data")) };
                    operation = OperationIntent.FormatVolume;
                    break;
                case SetDriveLetterCommand letter:
                    command = letter with { Partition = RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, "partition:data")) };
                    operation = OperationIntent.SetDriveLetter;
                    break;
            }
            var target = command switch
            {
                DeletePartitionCommand delete => delete.Partition.Existing!.Value,
                FormatVolumeCommand format => format.Partition.Existing!.Value,
                SetDriveLetterCommand letter => letter.Partition.Existing!.Value,
                _ => osTarget
            };
            var proposal = new RealOperationIntentRequest(operation, fixture.Session.SystemId,
                command is DeletePartitionCommand ? [osTarget, target] : [target],
                [step with { Command = command, DependsOn = [] }], step.AfterCondition);
            Complete(proposal, output);
        }
    }

    [Fact]
    public void CompleteMemberGoalAuthorizesRolesPerMediaLayoutsAndMixedMaximumWithFreshProof()
    {
        var f = new GeneralFixture();
        f.Authorization.Confirm();
        f.VerifyPoolAndTemplates();
        var proposal = f.CreateVd();
        f.Authorization.ValidateProposal(proposal);
        f.Authorization.ValidateFrozen(proposal, f.Freeze(proposal, addMaximum: true));
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(proposal.Steps.Single().Command);
        Assert.True(command.CapacityTiers![0].UsesMaximumSize(command.UseMaximumSize));
        Assert.False(command.CapacityTiers[1].UsesMaximumSize(command.UseMaximumSize));
        Assert.Equal("Mirror", command.CapacityTiers[0].Layout!.ResiliencySettingName);
        Assert.Equal("Parity", command.CapacityTiers[1].Layout!.ResiliencySettingName);
        Assert.Equal(1, command.CapacityTiers[1].Layout!.NumberOfDataCopies);
    }

    [Theory]
    [InlineData("wrong-media")]
    [InlineData("dropped-member")]
    [InlineData("explicit-size")]
    [InlineData("dropped-explicit-flag")]
    [InlineData("unexpected-role")]
    public void GeneralGoalRejectsChangedMembersMediaCapacityIntentAndRole(string defect)
    {
        var f = new GeneralFixture();
        f.Authorization.Confirm();
        f.VerifyPoolAndTemplates();
        var request = f.CreateVd();
        var command = Assert.IsType<CreateTieredVirtualDiskCommand>(request.Steps.Single().Command);
        if (defect == "dropped-member") request = request with { Targets = request.Targets.Where(item => item != f.Members[^1]).ToArray() };
        else if (defect == "unexpected-role") request = RealOperationProposalFactory.SetPhysicalDiskUsage(f.Session.SystemId,
            f.Members[0], f.Pool, f.Members, PhysicalDiskUsage.Retired);
        else
        {
            var inputs = command.CapacityTiers!.ToArray();
            if (defect == "wrong-media") inputs[0] = inputs[0] with { MediaType = "HDD" };
            if (defect == "explicit-size") inputs[1] = inputs[1] with { SizeBytes = 33L << 30 };
            if (defect == "dropped-explicit-flag") inputs[1] = inputs[1] with { UseMaximumSize = null };
            request = request with { Steps = [request.Steps[0] with { Command = command with { CapacityTiers = inputs } }] };
        }
        var rejection = Record.Exception(() => f.Authorization.ValidateProposal(request));
        Assert.True(rejection is InvalidDataException or ArgumentException);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("foreign-maximum-source")]
    [InlineData("frozen-media")]
    public void GeneralGoalRejectsTamperedFrozenHashOrTierEvidence(string defect)
    {
        var f = new GeneralFixture();
        f.Authorization.Confirm();
        f.VerifyPoolAndTemplates();
        var proposal = f.CreateVd();
        var frozen = f.Freeze(proposal, addMaximum: true);
        var plan = frozen.Plan;
        if (defect == "hash") plan = plan with { PlanHash = "tampered" };
        else
        {
            var command = Assert.IsType<CreateTieredVirtualDiskCommand>(plan.RealOperation!.Steps[0].Command);
            var inputs = command.CapacityTiers!.ToArray();
            inputs[0] = defect == "frozen-media" ? inputs[0] with { MediaType = "HDD" }
                : inputs[0] with { MaximumCapacity = inputs[0].MaximumCapacity! with { SourceFingerprint = "foreign" } };
            var changed = command with { CapacityTiers = inputs };
            plan = plan with { RealOperation = plan.RealOperation with { Steps = [plan.RealOperation.Steps[0] with { Command = changed }] } };
            plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        }
        Assert.Throws<InvalidDataException>(() => f.Authorization.ValidateFrozen(proposal, frozen with { Plan = plan }));
    }

    [Fact]
    public void DistinctConfirmedPoolScopesAndNewSubsetKeepSeparateStorageProofs()
    {
        var source = new GeneralFixture().Session.Baseline;
        var system = SystemId.New();
        var firstIds = source.PhysicalDisks.Take(3).Select(item => item.StableId).ToArray();
        var secondIds = source.PhysicalDisks.Skip(3).Select(item => item.StableId).ToArray();
        var a = new StoragePoolInfo("pool:a", true, "A", false, "Healthy", "OK", 3L << 40, 0, null, firstIds);
        var b = a with { StableId = "pool:b", FriendlyName = "B", MemberPhysicalDiskIds = secondIds };
        var snapshot = source with { StoragePools = [a, b], PhysicalDisks = source.PhysicalDisks.Select(item => item with
            { PoolStableId = firstIds.Contains(item.StableId) ? a.StableId : b.StableId }).ToArray() };
        StorageSystemDocument Project() => new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:scoped-auth", StorageSystemKind.Local, "Scope auth",
            snapshot, [], DateTimeOffset.UtcNow) with { SystemId = system, SourceFacts = WinPoolSimulationFacts.Create(snapshot, system) with { IsSimulation = false } };
        var session = new SimulationEditingSession();
        session.Bind(Project(), snapshot);
        var subsetPool = a with { StableId = "draft:subset", IsStable = false, FriendlyName = "Subset", MemberPhysicalDiskIds = [firstIds[0]] };
        var draft = new RealStructureDraftPlan(new(true, [], []), [a, b], [], [],
            [new(subsetPool, firstIds[0], new(false, false, "NTFS", 65536, "Subset"), true, false)]);
        var authorization = new RealStructureApplyAuthorization(draft, session, "scope-session");
        authorization.Confirm();
        var now = DateTimeOffset.UtcNow;
        var trusted = new TrustedRealSession(SessionId.New(), "scope-session", "scope-process", 42, now, Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "scope-machine",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        AgentRealOperationResponse FreezeScope(RealOperationIntentRequest request, string before, string physical)
        {
            var plan = RealOperationPlanFactory.Create(request, OperationId.New(), environment, trusted, before, before, physical, "scope-support", now, now.AddMinutes(2));
            return new(plan, RealOperationState.Prepared, [], null, false);
        }
        foreach (var pool in new[] { a, b })
        {
            var members = pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(system, StorageObjectKind.PhysicalDisk, id)).ToArray();
            var request = RealOperationProposalFactory.DissolveSingleMemberPool(system, new(system, StorageObjectKind.StoragePool, pool.StableId), null, members: members);
            var frozen = FreezeScope(request, "before-" + pool.StableId, "physical-" + pool.StableId);
            authorization.ValidateFrozen(request, frozen);
            snapshot = snapshot with { StoragePools = snapshot.StoragePools.Where(item => item.StableId != pool.StableId).ToArray(),
                PhysicalDisks = snapshot.PhysicalDisks.Select(item => item.PoolStableId == pool.StableId ? item with { PoolStableId = null } : item).ToArray() };
            var terminal = frozen with { State = RealOperationState.Succeeded, Steps = [new(request.Steps[0].Id, RealOperationStepState.Verified, null, null,
                JsonSerializer.Serialize(new { PostFingerprint = "after-" + pool.StableId, PhysicalMemberFingerprint = "physical-" + pool.StableId }))] };
            authorization.ObserveVerified(request, terminal, Project());
        }
        var subset = RealOperationProposalFactory.CreatePoolWithMembers(system, [new(system, StorageObjectKind.PhysicalDisk, firstIds[0])], "Subset");
        authorization.ValidateFrozen(subset, FreezeScope(subset, "before-new-subset", "physical-new-subset"));
    }

    private sealed class GeneralFixture
    {
        private readonly SystemId system = SystemId.New();
        private readonly TrustedRealSession trusted;
        private readonly EnvironmentProfile environment;
        private StorageSnapshot snapshot;
        private string fingerprint = "general-before";
        public SimulationEditingSession Session { get; } = new();
        public RealStructureApplyAuthorization Authorization { get; }
        public IReadOnlyList<StorageObjectId> Members { get; }
        public StorageObjectId Pool => new(system, StorageObjectKind.StoragePool, "pool:general");
        public PoolEditIntent Intent { get; }

        public GeneralFixture()
        {
            var source = SimulationLayouts.StandardTiered();
            var first = source.PhysicalDisks[0] with { IsBoot = false, IsSystem = false, IsPageFile = false, IsCrashDump = false,
                PoolStableId = null, CanPool = true, Usage = PhysicalDiskUsage.AutoSelect };
            var physicals = Enumerable.Range(0, 6).Select(index => first with { StableId = "physical:general-" + index,
                SerialNumber = "GENERAL-" + index, DeviceId = 10 + index, MediaType = index < 2 ? "SSD" : "HDD" }).ToArray();
            snapshot = source with { PhysicalDisks = physicals, StoragePools = [], StorageTiers = [], VirtualDisks = [], OsDisks = [], Partitions = [], Volumes = [] };
            var document = Project();
            Session.Bind(document, document.Snapshot);
            Members = physicals.Select(item => new StorageObjectId(system, StorageObjectKind.PhysicalDisk, item.StableId)).ToArray();
            var pool = new StoragePoolInfo("draft:general", false, "General", false, "Healthy", "OK", physicals.Sum(item => item.Size), 0, null,
                Members.Select(item => item.ProviderKey).ToArray());
            Intent = new(true, true, "NTFS", 65536, "General label", Layout: PoolVirtualDiskLayout.HddTiered, VirtualDiskName: "General VD",
                Tiers: [new("SSD", new("Mirror", 1, 16384, 2, 1), 0, true), new("HDD", new("Parity", 3, 32768, 1, 1), 32L << 30, false)],
                MemberUsages: Members.Select((member, index) => new PoolMemberEditIntent(member.ProviderKey, index == 5 ? PhysicalDiskUsage.HotSpare : PhysicalDiskUsage.AutoSelect)).ToArray());
            var draft = new RealStructureDraftPlan(new(true, [], []), [], [], [], [new(pool, Members[0].ProviderKey, Intent, true, true)]);
            Authorization = new(draft, Session, "general-session");
            var now = DateTimeOffset.UtcNow;
            trusted = new(SessionId.New(), "general-session", "general-process", 42, now, Path.GetFullPath("WinPool.App.exe"), true);
            environment = new(EnvironmentId.New(), EnvironmentKind.LocalMachine, "general-machine", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        }

        public void VerifyPoolAndTemplates()
        {
            var pool = RealOperationProposalFactory.CreatePoolWithMembers(system, Members, "General");
            snapshot = snapshot with { StoragePools = [new(Pool.ProviderKey, true, "General", false, "Healthy", "OK", snapshot.PhysicalDisks.Sum(item => item.Size), 0, null,
                Members.Select(item => item.ProviderKey).ToArray())], PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { PoolStableId = Pool.ProviderKey }).ToArray() };
            Complete(pool, Pool.ProviderKey);
            var usage = RealOperationProposalFactory.SetPhysicalDiskUsage(system, Members[^1], Pool, Members, PhysicalDiskUsage.HotSpare);
            snapshot = snapshot with { PhysicalDisks = snapshot.PhysicalDisks.Select(item => item.StableId == Members[^1].ProviderKey ? item with { Usage = PhysicalDiskUsage.HotSpare } : item).ToArray() };
            Complete(usage);
            foreach (var goal in Intent.Tiers!)
            {
                var proposal = RealOperationProposalFactory.CreateTierTemplate(system, Pool, Members, "General_" + goal.MediaType, goal.MediaType, goal.Layout);
                var id = "tier:general-" + goal.MediaType;
                snapshot = snapshot with { StorageTiers = snapshot.StorageTiers.Append(new(id, true, "General_" + goal.MediaType, goal.MediaType,
                    goal.Layout.ResiliencySettingName, 0, 0, Pool.ProviderKey, null, [], goal.Layout.NumberOfColumns, goal.Layout.InterleaveBytes,
                    goal.Layout.NumberOfDataCopies, goal.Layout.PhysicalDiskRedundancy)).ToArray() };
                Complete(proposal, id);
            }
        }

        public RealOperationIntentRequest CreateVd() => RealOperationProposalFactory.CreateTieredVirtualDisk(system, Pool, Members,
            Intent.Tiers!.Select(goal => new MaximumCapacityTier(RealTargetReference.ForExisting(new(system, StorageObjectKind.StorageTier, "tier:general-" + goal.MediaType)),
                null, goal.UseMaximumSize ? null : goal.SizeBytes, goal.UseMaximumSize, goal.Layout, goal.MediaType)).ToArray(), "General VD");

        public AgentRealOperationResponse Freeze(RealOperationIntentRequest proposal, bool addMaximum = false)
        {
            var now = DateTimeOffset.UtcNow;
            if (addMaximum)
            {
                var command = (CreateTieredVirtualDiskCommand)proposal.Steps[0].Command;
                var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, 64L << 30,
                    MaximumCapacityAlgorithm.InitialCandidateBytes(64L << 30), 2L << 40, 100, "provider", fingerprint, now);
                proposal = proposal with { Steps = [proposal.Steps[0] with { Command = command with { MaximumCapacity = policy,
                    CapacityTiers = command.CapacityTiers!.Select(item => item.UsesMaximumSize(command.UseMaximumSize) ? item with { MaximumCapacity = policy } : item).ToArray() } }] };
            }
            var plan = RealOperationPlanFactory.Create(proposal, OperationId.New(), environment, trusted, fingerprint, fingerprint, "general-physical",
                "general-fresh-support", now, now.AddMinutes(2));
            return new(plan, RealOperationState.Prepared, [], null, false);
        }

        private void Complete(RealOperationIntentRequest proposal, string? output = null)
        {
            var frozen = Freeze(proposal);
            Authorization.ValidateFrozen(proposal, frozen);
            fingerprint += ":next";
            var terminal = frozen with { State = RealOperationState.Succeeded, Steps = [new(proposal.Steps[0].Id, RealOperationStepState.Verified,
                null, null, JsonSerializer.Serialize(new { CreatedObjectId = output, PostFingerprint = fingerprint, PhysicalMemberFingerprint = "general-physical" }))] };
            Authorization.ObserveVerified(proposal, terminal, Project());
        }

        private StorageSystemDocument Project() => new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:general-auth", StorageSystemKind.Local,
            "General auth", snapshot, [], DateTimeOffset.UtcNow) with { SystemId = system,
                SourceFacts = WinPoolSimulationFacts.Create(snapshot, system) with { IsSimulation = false } };
    }

    private sealed class Fixture
    {
        private readonly StorageSystemDocument document;
        private readonly SystemId systemId = SystemId.New();
        private readonly TrustedRealSession trusted;
        private readonly EnvironmentProfile environment;
        public SimulationEditingSession Session { get; } = new();
        public RealStructureDraftPlan Draft { get; }
        public RealStructureApplyAuthorization Authorization { get; }
        public StorageObjectId DirectDisk { get; }
        public RealOperationIntentRequest CreatePool { get; }

        public Fixture(bool hdd = false, bool maximum = true)
        {
            var source = SimulationLayouts.StandardTiered();
            var physical = source.PhysicalDisks.First() with { PoolStableId = null, IsBoot = false, IsSystem = false,
                IsPageFile = false, IsCrashDump = false, IsStable = true };
            var direct = new OsDiskInfo("os:direct", physical.FriendlyName, 7, "RAW", physical.Size, false, false, false, physical.StableId, null);
            source = source with { PhysicalDisks = [physical], StoragePools = [], StorageTiers = [], VirtualDisks = [], OsDisks = [direct], Partitions = [], Volumes = [] };
            document = Project(source);
            Session.Bind(document, document.Snapshot);
            var pool = new StoragePoolInfo("draft:pool", false, "Requested pool", false, "Healthy", "OK", physical.Size, 0, null, [physical.StableId]);
            var intent = new PoolEditIntent(true, true, "NTFS", 65536, "Original label", Layout: hdd ? PoolVirtualDiskLayout.HddTiered : PoolVirtualDiskLayout.Ordinary,
                VirtualDiskName: "Requested VD", VirtualDiskSizeBytes: maximum ? null : 32L << 30, VirtualDiskUseMaximum: maximum, DriveLetter: 'E');
            Draft = new(new(true, [], []), [], [], [], [new(pool, physical.StableId, intent, true, true)]);
            Authorization = new(Draft, Session, "product-session");
            DirectDisk = new(Session.SystemId, StorageObjectKind.OsDisk, direct.StableId);
            CreatePool = RealOperationProposalFactory.CreateSingleMemberPool(Session.SystemId, Authorization.PhysicalTarget, pool.FriendlyName, null);
            var now = DateTimeOffset.UtcNow;
            trusted = new(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"), 42, now, Path.GetFullPath("WinPool.App.exe"), true);
            environment = new(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        }

        public StorageSystemDocument Project(StorageSnapshot snapshot)
        {
            var id = systemId;
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:authorization-tests", StorageSystemKind.Local,
                "Authorization tests", snapshot, [], DateTimeOffset.UtcNow) with
            { SystemId = id, SourceFacts = WinPoolSimulationFacts.Create(snapshot, id) with { IsSimulation = false } };
        }

        public StorageSystemDocument WithCreatedPool() => Project(document.Snapshot with
        { StoragePools = [Draft.Creations[0].Pool with { StableId = "pool:verified", IsStable = true }] });

        public RealOperationIntentRequest CreateVd(string pool)
        {
            var intent = Draft.Creations[0].Intent;
            var poolTarget = new StorageObjectId(Session.SystemId, StorageObjectKind.StoragePool, pool);
            return intent.Layout == PoolVirtualDiskLayout.HddTiered
                ? RealOperationProposalFactory.CreateTieredVirtualDisk(Session.SystemId, poolTarget,
                    new(Session.SystemId, StorageObjectKind.StorageTier, "tier:verified"), "Requested VD", intent.VirtualDiskSizeBytes ?? 0, intent.VirtualDiskUseMaximum)
                : RealOperationProposalFactory.CreateFirstVirtualDisk(Session.SystemId, poolTarget,
                    new("Requested VD", intent.VirtualDiskSizeBytes ?? 0, false, true, true, "Original label", 'E', intent.VirtualDiskUseMaximum));
        }

        public AgentRealOperationResponse Frozen(RealOperationIntentRequest proposal, string fingerprint = "before-pool", bool addMaxPolicy = false)
        {
            var now = DateTimeOffset.UtcNow;
            var normalized = proposal with { Targets = proposal.Targets.Append(Authorization.PhysicalTarget).Distinct().ToArray() };
            if (addMaxPolicy)
            {
                var upper = 64L << 30;
                var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, upper, MaximumCapacityAlgorithm.InitialCandidateBytes(upper),
                    document.Snapshot.PhysicalDisks[0].Size, 100, "provider", fingerprint, now);
                normalized = normalized with { Steps = [normalized.Steps[0] with
                { Command = normalized.Steps[0].Command switch
                {
                    CreateVirtualDiskCommand ordinary => ordinary with { MaximumCapacity = policy },
                    CreateTieredVirtualDiskCommand tiered => tiered with { MaximumCapacity = policy },
                    _ => throw new InvalidOperationException()
                } }] };
            }
            var plan = RealOperationPlanFactory.Create(normalized, OperationId.New(), environment, trusted,
                "inventory", fingerprint, "physical-fingerprint", "fresh-support", now, now.AddMinutes(2));
            return new(plan, RealOperationState.Prepared, [], null, false);
        }

        public AgentRealOperationResponse SuccessfulPoolReceipt()
        {
            var frozen = Frozen(CreatePool);
            return frozen with { State = RealOperationState.Succeeded, Steps = [new("create-pool", RealOperationStepState.Verified,
                null, null, JsonSerializer.Serialize(new { CreatedObjectId = "pool:verified", PostFingerprint = "after-pool", PhysicalMemberFingerprint = "physical-fingerprint" }))] };
        }

        public void VerifyPool()
        {
            var receipt = SuccessfulPoolReceipt();
            Authorization.ValidateFrozen(CreatePool, receipt with { State = RealOperationState.Prepared });
            Authorization.ObserveVerified(CreatePool, receipt, WithCreatedPool());
        }
    }
}
