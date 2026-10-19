using System.Collections.Immutable;
using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealDiskInitializationAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightTerminalStatusLoadsOnlyItsReceiptBeforeConfiguringReservedPartition(bool createMsr)
    {
        var fixture = new Fixture(createMsr);
        fixture.Prepare();
        var connection = new EvidenceConnection(_ => fixture.EvidencePage());
        var cache = RealOperationStatusCache.For(connection);
        cache.Remember(fixture.Prepared);
        var receipt = fixture.Receipt();
        var light = new AgentRealOperationStatusResponse(receipt.Plan.OperationId, 7, receipt.State,
            [receipt.Steps[0] with { ResultEvidence = null }], receipt.Code, false,
            SystemId: receipt.Plan.SystemId, PlanHash: receipt.Plan.PlanHash, Targets: receipt.Plan.Targets);
        var terminal = Assert.IsType<AgentRealOperationResponse>(await cache.ResolveAsync(light, CancellationToken.None));
        Assert.Null(terminal.Steps[0].ResultEvidence);

        var continuation = Assert.IsType<RealOperationIntentRequest>(await fixture.Authorization.ObserveInitializedAsync(
            terminal, fixture.Fresh(), connection, CancellationToken.None));

        var load = Assert.IsType<LoadAgentRealOperationEvidenceRequest>(Assert.Single(connection.Requests));
        Assert.Equal(receipt.Plan.OperationId, load.OperationId);
        Assert.Equal(0, load.Offset);
        Assert.Equal(1, load.Limit);
        Assert.Null(load.StepId);
        Assert.Null(load.Field);
        Assert.Equal(new[] { fixture.Disk, fixture.MsrTarget }, continuation.Targets);
        Assert.Equal(fixture.MsrTarget, Assert.IsType<DeletePartitionCommand>(continuation.Steps[0].Command).Partition.Existing);
        Assert.Equal(createMsr ? 2 : 1, continuation.Steps.Count);
        if (createMsr)
        {
            var replacement = Assert.IsType<CreatePartitionCommand>(continuation.Steps[1].Command);
            Assert.Equal(fixture.Disk, replacement.Disk.Existing);
            Assert.Equal(RealPartitionRole.Msr, replacement.Role);
            Assert.Equal(1L << 20, replacement.OffsetBytes);
            Assert.Equal(16L << 20, replacement.SizeBytes);
        }
        fixture.Authorization.ValidateFrozen(continuation, fixture.Frozen(continuation, "after-initialize"));
        // Receipt loading is explicit and does not turn ordinary terminal cache entries into history carriers.
        Assert.Null((await cache.ResolveAsync(light, CancellationToken.None))!.Steps[0].ResultEvidence);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("plan-hash")]
    [InlineData("step")]
    [InlineData("unverified")]
    [InlineData("reconciliation")]
    [InlineData("missing-response")]
    [InlineData("missing-result")]
    [InlineData("missing-reference")]
    [InlineData("chunked")]
    [InlineData("oversized")]
    [InlineData("byte-count")]
    [InlineData("next-page")]
    [InlineData("wrong-member-proof")]
    public async Task ContinuationRejectsForeignIncompleteOrUnboundedReceiptPages(string change)
    {
        var fixture = new Fixture(true);
        fixture.Prepare();
        var terminal = fixture.Receipt();
        var receipt = change switch
        {
            "operation" => terminal with { Plan = terminal.Plan with { OperationId = OperationId.New() } },
            "plan-hash" => terminal with { Plan = terminal.Plan with { PlanHash = "foreign" } },
            "step" => terminal with { Steps = [terminal.Steps[0] with { StepId = "other-step" }] },
            "unverified" => terminal with { Steps = [terminal.Steps[0] with { State = RealOperationStepState.Failed }] },
            "reconciliation" => terminal with { RequiresReconciliation = true },
            "missing-result" => terminal with { Steps = [terminal.Steps[0] with { ResultEvidence = null }] },
            "oversized" => terminal with { Steps = [terminal.Steps[0] with
                { ResultEvidence = terminal.Steps[0].ResultEvidence + new string(' ', 65537) }] },
            "wrong-member-proof" => fixture.Receipt(change: "member"),
            _ => terminal
        };
        var page = fixture.EvidencePage(receipt);
        page = change switch
        {
            "missing-reference" => page with { EvidenceReferences = [] },
            "chunked" => page with { EvidenceReferences = [page.EvidenceReferences![0] with { InlineComplete = false }] },
            "byte-count" => page with { EvidenceReferences = [page.EvidenceReferences![0] with { TotalBytes = 1 }] },
            "next-page" => page with { NextOffset = 1 },
            _ => page
        };
        var connection = new EvidenceConnection(_ => change == "missing-response" ? new AgentAcknowledgement() : page);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Authorization.ObserveInitializedAsync(
            terminal with { Steps = [terminal.Steps[0] with { ResultEvidence = null }] },
            fixture.Fresh(), connection, CancellationToken.None));

        Assert.Single(connection.Requests);
        fixture.Authorization.ValidateProposal(fixture.Initialization);
    }

    [Theory]
    [InlineData(RealOperationState.Running)]
    [InlineData(RealOperationState.OutcomeUnknown)]
    [InlineData(RealOperationState.Failed)]
    public async Task NonterminalOrFailedInitializationDoesNotLoadAReceiptOrAuthorizeContinuation(RealOperationState state)
    {
        var fixture = new Fixture(true);
        fixture.Prepare();
        var connection = new EvidenceConnection(_ => throw new IOException("Receipt must not be requested"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Authorization.ObserveInitializedAsync(
            fixture.Receipt() with { State = state }, fixture.Fresh(), connection, CancellationToken.None));
        Assert.Empty(connection.Requests);
        fixture.Authorization.ValidateProposal(fixture.Initialization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedInitializationAuthorizesOnlyItsProviderMsrNormalization(bool createMsr)
    {
        var fixture = new Fixture(createMsr);
        fixture.Prepare();
        var continuation = Assert.IsType<RealOperationIntentRequest>(fixture.Authorization.ObserveInitialized(
            fixture.Receipt(), fixture.Fresh()));

        Assert.Equal(OperationIntent.InitializeDisk, continuation.Intent);
        Assert.Equal(new[] { fixture.Disk, fixture.MsrTarget }, continuation.Targets);
        var remove = Assert.IsType<DeletePartitionCommand>(continuation.Steps[0].Command);
        Assert.Equal(fixture.MsrTarget, remove.Partition.Existing);
        Assert.Equal(createMsr ? 2 : 1, continuation.Steps.Count);
        if (createMsr)
        {
            var replacement = Assert.IsType<CreatePartitionCommand>(continuation.Steps[1].Command);
            Assert.Equal(fixture.Disk, replacement.Disk.Existing);
            Assert.Equal(RealPartitionRole.Msr, replacement.Role);
            Assert.Equal(1L << 20, replacement.OffsetBytes);
            Assert.Equal(16L << 20, replacement.SizeBytes);
            Assert.Equal(new[] { continuation.Steps[0].Id }, continuation.Steps[1].DependsOn);
        }
        fixture.Authorization.ValidateProposal(continuation);
        fixture.Authorization.ValidateFrozen(continuation, fixture.Frozen(continuation, "after-initialize"));
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.Initialization));
        Assert.Throws<InvalidOperationException>(() => fixture.Authorization.ObserveInitialized(fixture.Receipt(), fixture.Fresh()));
        fixture.Authorization.Stop();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(continuation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoProviderMsrCreatesOnlyTheRequestedReservedPartition(bool createMsr)
    {
        var fixture = new Fixture(createMsr);
        fixture.Prepare();
        var continuation = fixture.Authorization.ObserveInitialized(
            fixture.Receipt(providerMsr: false), fixture.Fresh(partitions: []));
        if (createMsr)
        {
            var proposal = Assert.IsType<RealOperationIntentRequest>(continuation);
            Assert.Single(proposal.Targets);
            Assert.Equal(fixture.Disk, proposal.Targets[0]);
            Assert.IsType<CreatePartitionCommand>(Assert.Single(proposal.Steps).Command);
            fixture.Authorization.ValidateFrozen(proposal, fixture.Frozen(proposal, "after-initialize"));
        }
        else
        {
            Assert.Null(continuation);
            Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(fixture.Initialization));
        }
    }

    [Fact]
    public void AnotherDiskAndAnExpandedInitializationCannotUseTheConfirmedScope()
    {
        var fixture = new Fixture();
        var other = new StorageObjectId(fixture.System, StorageObjectKind.OsDisk, "os:other");
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(
            RealOperationProposalFactory.InitializeGpt(fixture.System, other)));
        var original = fixture.Initialization;
        var extra = RealOperationProposalFactory.ConfigureInitializedDisk(fixture.System, fixture.Disk, null, true)!;
        var expanded = original with { Steps = original.Steps.Append(extra.Steps[0] with
        { DependsOn = [original.Steps[0].Id] }).ToArray() };
        RealOperationValidator.Validate(expanded);
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(expanded));
    }

    [Theory]
    [InlineData("create-msr")]
    [InlineData("data-layout")]
    [InlineData("other-msr")]
    public void VerifiedReceiptCannotExpandTheRequestedContinuation(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        fixture.Authorization.ObserveInitialized(fixture.Receipt(), fixture.Fresh());
        var foreignMsr = new StorageObjectId(fixture.System, StorageObjectKind.Partition, "partition:other");
        var changed = RealOperationProposalFactory.ConfigureInitializedDisk(fixture.System, fixture.Disk,
            change == "other-msr" ? foreignMsr : fixture.MsrTarget, change == "create-msr",
            virtualDiskSizeBytes: change == "data-layout" ? 32L << 30 : null,
            formatNtfs: change == "data-layout", label: "Unconfirmed data", letter: change == "data-layout" ? 'E' : null)!;
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateProposal(changed));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("command")]
    [InlineData("hash")]
    [InlineData("state")]
    public void FrozenInitializationCannotSubstituteTargetsCommandsOrUnverifiedPlans(string change)
    {
        var fixture = new Fixture();
        var frozen = fixture.Frozen(fixture.Initialization);
        var plan = frozen.Plan;
        if (change == "target")
            plan = plan with { Targets = plan.Targets.Append(fixture.MsrTarget).ToArray() };
        if (change == "command")
        {
            var extra = RealOperationProposalFactory.ConfigureInitializedDisk(fixture.System, fixture.Disk, null, true)!;
            var expanded = fixture.Initialization with { Steps = fixture.Initialization.Steps.Append(extra.Steps[0] with
            { DependsOn = [fixture.Initialization.Steps[0].Id] }).ToArray() };
            plan = fixture.Frozen(expanded).Plan;
        }
        plan = plan with { PlanHash = change == "hash" ? "tampered" : OperationPlanHasher.Compute(plan) };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(fixture.Initialization,
            frozen with { Plan = plan, State = change == "state" ? RealOperationState.Succeeded : RealOperationState.Prepared }));
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("machine")]
    [InlineData("session")]
    [InlineData("member-fingerprint")]
    [InlineData("topology")]
    public void ContinuationMustRetainTheVerifiedDiskAndLiveBinding(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        var continuation = fixture.Authorization.ObserveInitialized(fixture.Receipt(), fixture.Fresh())!;
        var frozen = fixture.Frozen(continuation, change == "topology" ? "external-change" : "after-initialize");
        var real = frozen.Plan.RealOperation!;
        real = change switch
        {
            "machine" => real with { MachineBinding = "other-machine" },
            "session" => real with { SessionBinding = "other-session" },
            "member-fingerprint" => real with { PhysicalMemberFingerprint = "other-member" },
            _ => real
        };
        var plan = frozen.Plan with
        {
            RealOperation = real,
            Targets = change == "physical" ? frozen.Plan.Targets.Select(target => target == fixture.Physical
                ? new StorageObjectId(fixture.System, StorageObjectKind.PhysicalDisk, "physical:other") : target).ToArray() : frozen.Plan.Targets
        };
        plan = plan with { PlanHash = OperationPlanHasher.Compute(plan) };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(continuation, frozen with { Plan = plan }));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("plan-hash")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("reconciliation")]
    [InlineData("missing-step")]
    [InlineData("step-id")]
    [InlineData("unverified-step")]
    public void IncompleteOrForeignReceiptsCannotAuthorizeAutomaticDeletion(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        var receipt = fixture.Receipt();
        receipt = change switch
        {
            "operation" => receipt with { Plan = receipt.Plan with { OperationId = OperationId.New() } },
            "plan-hash" => receipt with { Plan = receipt.Plan with { PlanHash = "foreign-hash" } },
            "failed" => receipt with { State = RealOperationState.PartiallyCompleted },
            "unknown" => receipt with { State = RealOperationState.OutcomeUnknown },
            "reconciliation" => receipt with { RequiresReconciliation = true },
            "missing-step" => receipt with { Steps = [] },
            "step-id" => receipt with { Steps = [receipt.Steps[0] with { StepId = "other-step" }] },
            "unverified-step" => receipt with { Steps = [receipt.Steps[0] with { State = RealOperationStepState.Failed }] },
            _ => receipt
        };
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveInitialized(receipt, fixture.Fresh()));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("post")]
    [InlineData("disk")]
    [InlineData("partition")]
    [InlineData("partition-guid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("missing-gpt")]
    [InlineData("missing-evidence")]
    public void ReceiptMustProveTheExactInitializedDiskAndProviderMsrGeometry(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        var receipt = fixture.Receipt(change: change);
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveInitialized(receipt, fixture.Fresh()));
    }

    [Theory]
    [InlineData("extra-partition")]
    [InlineData("extra-msr")]
    [InlineData("wrong-id")]
    [InlineData("unstable")]
    [InlineData("role")]
    [InlineData("type-guid")]
    [InlineData("partition-guid")]
    [InlineData("boot")]
    [InlineData("system")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("volume")]
    public void FreshInventoryMustProveOneReservedPartitionWithoutAnyVolume(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        var msr = change switch
        {
            "wrong-id" => fixture.Msr with { StableId = "partition:other" },
            "role" => fixture.Msr with { Type = "BasicData", GptType = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" },
            "type-guid" => fixture.Msr with { GptType = "00000000-0000-0000-0000-000000000000" },
            "partition-guid" => fixture.Msr with { Guid = "d8906584-cf43-43ee-999a-e75d35370eee" },
            "boot" => fixture.Msr with { IsBoot = true },
            "system" => fixture.Msr with { IsSystem = true },
            "offset" => fixture.Msr with { Offset = 2L << 20 },
            "size" => fixture.Msr with { Size = 32L << 20 },
            _ => fixture.Msr
        };
        var partitions = change is "extra-partition" or "extra-msr"
            ? new[] { msr, fixture.Msr with { StableId = "partition:extra", PartitionNumber = 2,
                Type = change == "extra-msr" ? "MicrosoftReserved" : "BasicData",
                GptType = change == "extra-msr" ? fixture.Msr.GptType : "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" } }
            : new[] { msr };
        var fresh = fixture.Fresh(partitions);
        if (change == "unstable")
        {
            var facts = fresh.SourceFacts!;
            fresh = fresh with { SourceFacts = facts with { Objects = facts.Objects.Select(item => item.Id == msr.StableId
                ? item with { HasReliableIdentity = false } : item).ToImmutableArray() } };
        }
        if (change == "volume")
            fresh = fixture.Project(fresh.Snapshot with { Volumes = [new VolumeInfo("volume:unexpected", true,
                msr.StableId, "NTFS", "Unexpected data", msr.Size, 0, 4096, "Healthy", "OK", ["E:\\"])] });

        // Source facts are authoritative; prove the invalid observation survived projection.
        var actual = fresh.Snapshot.Partitions;
        if (change is "extra-partition" or "extra-msr")
        {
            Assert.Equal(2, actual.Count);
            Assert.Equal(change == "extra-msr" ? "MicrosoftReserved" : "BasicData", actual[1].Type);
        }
        else
        {
            var partition = Assert.Single(actual);
            switch (change)
            {
                case "wrong-id": Assert.NotEqual(fixture.Msr.StableId, partition.StableId); break;
                case "unstable": Assert.False(partition.IsStable); break;
                case "role": Assert.Equal("BasicData", partition.Type); break;
                case "type-guid": Assert.Equal("00000000-0000-0000-0000-000000000000", partition.PartitionTypeId); break;
                case "partition-guid": Assert.NotEqual(fixture.Msr.Guid, partition.Guid); break;
                case "boot": Assert.True(partition.IsBoot); break;
                case "system": Assert.True(partition.IsSystem); break;
                case "offset": Assert.NotEqual(fixture.Msr.Offset, partition.Offset); break;
                case "size": Assert.NotEqual(fixture.Msr.Size, partition.Size); break;
                case "volume":
                    Assert.Equal(msr.StableId, Assert.Single(fresh.Snapshot.Volumes).PartitionStableId);
                    Assert.Equal("NTFS", partition.FileSystem);
                    Assert.Equal("Unexpected data", partition.FileSystemLabel);
                    Assert.Equal("E", partition.DriveLetter);
                    break;
            }
        }
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveInitialized(fixture.Receipt(), fresh));
    }

    [Theory]
    [InlineData("not-gpt")]
    [InlineData("missing-disk")]
    [InlineData("missing-identity")]
    [InlineData("wrong-system")]
    [InlineData("unreported-partition")]
    public void MissingFreshDiskProofOrUnexpectedLayoutStopsContinuation(string change)
    {
        var fixture = new Fixture();
        fixture.Prepare();
        var fresh = fixture.Fresh();
        if (change == "not-gpt") fresh = fixture.Project(fresh.Snapshot with
        { OsDisks = [fresh.Snapshot.OsDisks[0] with { PartitionStyle = "RAW" }] });
        if (change == "missing-disk")
        {
            var facts = fresh.SourceFacts!;
            fresh = fresh with { SourceFacts = facts with
            {
                Objects = facts.Objects.Where(item => item.Id != fixture.Disk.ProviderKey).ToImmutableArray(),
                Relationships = facts.Relationships.Where(item => item.FromId != fixture.Disk.ProviderKey
                    && item.ToId != fixture.Disk.ProviderKey).ToImmutableArray(),
                Identities = facts.Identities.Where(item => item.ObjectId != fixture.Disk.ProviderKey).ToImmutableArray()
            } };
            Assert.DoesNotContain(fresh.Snapshot.OsDisks, item => item.StableId == fixture.Disk.ProviderKey);
        }
        if (change == "missing-identity") fresh = fresh with { SourceFacts = null };
        if (change == "wrong-system") fresh = fresh with { SystemId = SystemId.New() };
        if (change == "not-gpt") Assert.Equal("RAW", Assert.Single(fresh.Snapshot.OsDisks).PartitionStyle);
        if (change == "missing-identity") Assert.Null(fresh.SourceFacts);
        if (change == "wrong-system") Assert.NotEqual(fixture.System, fresh.SystemId);
        if (change == "unreported-partition") Assert.Single(fresh.Snapshot.Partitions);
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveInitialized(
            fixture.Receipt(providerMsr: change != "unreported-partition"), fresh));
    }

    [Fact]
    public void ReceiptRequiresPreparationAndASecondPreparationCannotReplaceTheFrozenOperation()
    {
        var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ObserveInitialized(fixture.Receipt(), fixture.Fresh()));
        fixture.Prepare();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(fixture.Initialization,
            fixture.Frozen(fixture.Initialization)));
        fixture.Authorization.Stop();
        Assert.Throws<InvalidDataException>(() => fixture.Authorization.ValidateFrozen(fixture.Initialization, fixture.Prepared));
    }

    private sealed class EvidenceConnection(Func<AgentRequest, AgentResponse> reply) : IAgentConnection
    {
        public List<AgentRequest> Requests { get; } = [];
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(reply(request), request.CorrelationId));
        }
    }

    private sealed class Fixture
    {
        private readonly StorageSnapshot snapshot;
        private readonly TrustedRealSession trusted;
        private readonly EnvironmentProfile environment;
        public SystemId System { get; } = SystemId.New();
        public StorageObjectId Disk { get; }
        public StorageObjectId Physical { get; }
        public StorageObjectId MsrTarget { get; }
        public PartitionInfo Msr { get; }
        public RealDiskInitializationAuthorization Authorization { get; }
        public RealOperationIntentRequest Initialization { get; }
        public AgentRealOperationResponse Prepared { get; private set; } = null!;

        public Fixture(bool createMsr = false)
        {
            var source = SimulationLayouts.StandardTiered();
            var physical = source.PhysicalDisks.First() with { PoolStableId = null, IsBoot = false, IsSystem = false };
            var disk = new OsDiskInfo("os:direct", physical.FriendlyName, 7, "RAW", physical.Size,
                false, false, false, physical.StableId, null);
            snapshot = source with { PhysicalDisks = [physical], StoragePools = [], StorageTiers = [],
                VirtualDisks = [], OsDisks = [disk], Partitions = [], Volumes = [] };
            Disk = new(System, StorageObjectKind.OsDisk, disk.StableId);
            Physical = new(System, StorageObjectKind.PhysicalDisk, physical.StableId);
            Msr = new("partition:provider-msr", true, 7, 1, "MicrosoftReserved", 17408, 16759808,
                false, false, "", "", "", null, 0, "Healthy", "OK", "", disk.StableId,
                PartitionTypeId: "e3c9e316-0b5c-4db8-817d-f92df00215ae",
                Guid: "811d7062-204b-45ec-a3b6-43a74506a9a6",
                GptType: "e3c9e316-0b5c-4db8-817d-f92df00215ae");
            MsrTarget = new(System, StorageObjectKind.Partition, Msr.StableId);
            Authorization = new(Disk, createMsr);
            Initialization = Authorization.Initialization;
            var now = DateTimeOffset.UtcNow;
            trusted = new(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"), 42,
                now, Path.GetFullPath("WinPool.App.exe"), true);
            environment = new(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine",
                ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        }

        public AgentRealOperationResponse Frozen(RealOperationIntentRequest proposal, string fingerprint = "before-initialize")
        {
            var now = DateTimeOffset.UtcNow;
            var normalized = proposal with { Targets = proposal.Targets.Append(Physical).Distinct().ToArray() };
            var plan = RealOperationPlanFactory.Create(normalized, OperationId.New(), environment, trusted,
                "inventory", fingerprint, "physical-fingerprint", "fresh-support", now, now.AddMinutes(2));
            return new(plan, RealOperationState.Prepared, [], null, false);
        }

        public void Prepare()
        {
            Prepared = Frozen(Initialization);
            Authorization.ValidateFrozen(Initialization, Prepared);
        }

        public StorageSystemDocument Fresh(IReadOnlyList<PartitionInfo>? partitions = null) => Project(snapshot with
        { OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }], Partitions = partitions ?? [Msr] });

        public StorageSystemDocument Project(StorageSnapshot value) => new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion, "local:initialization-tests", StorageSystemKind.Local,
            "Initialization tests", value, [], DateTimeOffset.UtcNow) with
        { SystemId = System, SourceFacts = WinPoolSimulationFacts.Create(value, System) with { IsSimulation = false } };

        public AgentRealOperationResponse Receipt(bool providerMsr = true, string? change = null)
        {
            var frozen = Prepared ?? Frozen(Initialization);
            var evidence = new Dictionary<string, object?>
            {
                ["PhysicalMemberFingerprint"] = change == "member" ? "other-member" : "physical-fingerprint",
                ["PostFingerprint"] = change == "post" ? "" : "after-initialize",
                ["GptInitialization"] = new
                {
                    OsDiskStableId = change == "disk" ? "os:other" : Disk.ProviderKey,
                    ProviderMsrStableId = providerMsr ? change == "partition" ? "partition:other" : Msr.StableId : null,
                    ProviderMsrGuid = change == "partition-guid" ? "d8906584-cf43-43ee-999a-e75d35370eee" : Msr.Guid,
                    ProviderMsrOffsetBytes = change == "offset" ? Msr.Offset + (1L << 20) : Msr.Offset,
                    ProviderMsrSizeBytes = change == "size" ? Msr.Size + (1L << 20) : Msr.Size
                }
            };
            if (change == "missing-gpt") evidence.Remove("GptInitialization");
            return frozen with { State = RealOperationState.Succeeded, Steps = [new(Initialization.Steps[0].Id,
                RealOperationStepState.Verified, null, null,
                change == "missing-evidence" ? null : JsonSerializer.Serialize(evidence))] };
        }

        public AgentRealOperationEvidenceResponse EvidencePage(AgentRealOperationResponse? receipt = null)
        {
            receipt ??= Receipt();
            var step = receipt.Steps[0];
            var bytes = global::System.Text.Encoding.UTF8.GetByteCount(step.ResultEvidence ?? "");
            return new(receipt, 0, null, [new(step.StepId, RealEvidenceField.Result, bytes, 7, bytes <= 65536)]);
        }
    }
}
