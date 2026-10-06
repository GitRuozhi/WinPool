using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageBackendTests
{
    private const string PhysicalId = "physical:7";
    private const string OtherPhysicalId = "physical:8";
    private const string DiskId = "osdisk:7";
    private const string OtherDiskId = "osdisk:8";
    private const string MachineBinding = "synthetic-machine-binding";

    [Fact]
    public async Task PrimordialMembershipDoesNotJoinIndependentPhysicalDisks()
    {
        var fixture = new Fixture(secondDisk: true);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);

        var closure = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.OsDisk, DiskId)]);

        Assert.Equal(PhysicalId, closure.PhysicalDiskId);
        Assert.DoesNotContain(closure.Objects, item => item.Id == OtherPhysicalId);
        Assert.DoesNotContain(closure.Objects, item => item.Id == OtherDiskId);
        Assert.DoesNotContain(closure.Objects, item => item.Id == "pool:primordial");
        Assert.Throws<InvalidDataException>(() => topology.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId), fixture.Id(StorageObjectKind.OsDisk, OtherDiskId)]));
    }

    [Fact]
    public async Task OrdinaryPoolClosureRejectsHiddenSecondPhysicalMember()
    {
        var fixture = new Fixture(secondDisk: true, sharedOrdinaryPool: true);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);

        Assert.Throws<InvalidDataException>(() => topology.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId)]));
    }

    [Fact]
    public async Task PlannerPreflightAndPostconditionUseFreshExactSingleDiskFacts()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        Assert.Contains(plan.Targets, item => item.Kind == StorageObjectKind.PhysicalDisk
            && item.ProviderKey == PhysicalId);

        var preflight = await fixture.Backend.PreflightStepAsync(
            plan, step, new Dictionary<string, string>(), CancellationToken.None);
        var exact = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson);
        Assert.NotNull(exact);
        Assert.Equal(DiskId, exact.UniqueId);
        Assert.Equal(@"\\.\PHYSICALDRIVE7", exact.OsDiskPath);
        Assert.Equal(plan.RealOperation.TargetFingerprint, preflight.TargetFingerprint);

        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);
        var result = await fixture.Backend.ExecuteStepAsync(
            plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson);
        Assert.NotNull(evidence);
        Assert.NotEqual(plan.RealOperation.TargetFingerprint, evidence.PostFingerprint);
        Assert.Equal(plan.RealOperation.PhysicalMemberFingerprint, evidence.PhysicalMemberFingerprint);

        var reconciled = await fixture.Backend.ReconcileAsync(plan,
            [Progress(step.Id, RealOperationStepState.Verified, result.ResultEvidenceJson)], CancellationToken.None);
        Assert.Equal(RealOperationState.Succeeded, reconciled.State);
        Assert.True(reconciled.CanReleaseWriteBarrier);
    }

    [Fact]
    public async Task ChangedPhysicalIdentityRejectsPreflightBeforeAdapterCall()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        fixture.ChangeSerial("REPLACED-SERIAL");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(
            plan, plan.RealOperation!.Steps[0], new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);

        var reconciliation = await fixture.Backend.ReconcileAsync(plan,
            [Progress("offline", RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);
        Assert.Equal(RealOperationState.OutcomeUnknown, reconciliation.State);
        Assert.False(reconciliation.CanReleaseWriteBarrier);
    }

    [Fact]
    public async Task ChangedTopologyAfterPreflightFailsWithoutCallingProvider()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        var step = plan.RealOperation!.Steps[0];
        var preflight = await fixture.Backend.PreflightStepAsync(
            plan, step, new Dictionary<string, string>(), CancellationToken.None);
        fixture.SetOffline(true);

        var result = await fixture.Backend.ExecuteStepAsync(
            plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.FailedWithoutEffect, result.Outcome);
        Assert.Equal(0, fixture.Adapter.CallCount);
        var noEffect = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(result.ResultEvidenceJson);
        Assert.NotNull(noEffect);
        Assert.True(noEffect.NoWindowsCall);
    }

    [Fact]
    public async Task ProviderErrorCannotBecomeVerifiedFromMatchingPostState()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        fixture.Adapter.ResultCode = "provider.error-outcome-unknown";
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight,
            CancellationToken.None);

        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("real.provider_reported_uncertain_result", result.Code);
        Assert.Equal(1, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task ReconciliationSeparatesPartialUnknownCancelledAndNoCallFailure()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync(twoSteps: true);
        var steps = plan.RealOperation!.Steps;
        fixture.SetOffline(true);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var closure = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.OsDisk, DiskId)]);
        var verifiedJson = JsonSerializer.Serialize(new WindowsVerifiedStepEvidence(
            closure.Fingerprint, closure.PhysicalMemberFingerprint, null, "provider.returned"));

        var partial = await fixture.Backend.ReconcileAsync(plan,
            [Progress(steps[0].Id, RealOperationStepState.Verified, verifiedJson),
                Progress(steps[1].Id, RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);
        Assert.Equal(RealOperationState.PartiallyCompleted, partial.State);
        Assert.True(partial.CanReleaseWriteBarrier);

        var unknown = await fixture.Backend.ReconcileAsync(plan,
            [Progress(steps[0].Id, RealOperationStepState.Verified, verifiedJson),
                Progress(steps[1].Id, RealOperationStepState.CallIssued)], CancellationToken.None);
        Assert.Equal(RealOperationState.OutcomeUnknown, unknown.State);
        Assert.False(unknown.CanReleaseWriteBarrier);

        fixture.SetOffline(false);
        var cancelled = await fixture.Backend.ReconcileAsync(plan,
            [Progress(steps[0].Id, RealOperationStepState.StoppedBeforeCall),
                Progress(steps[1].Id, RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);
        Assert.Equal(RealOperationState.Cancelled, cancelled.State);
        Assert.True(cancelled.CanReleaseWriteBarrier);

        var failed = await fixture.Backend.ReconcileAsync(plan,
            [Progress(steps[0].Id, RealOperationStepState.Failed, "preflight_failed"),
                Progress(steps[1].Id, RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);
        Assert.Equal(RealOperationState.Failed, failed.State);
        Assert.True(failed.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryStopsOnlyNeverStartedTrailingStepsAfterVerifiedPrefix()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync(twoSteps: true);
        var steps = plan.RealOperation!.Steps;
        fixture.SetOffline(true);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var closure = topology.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId)]);
        var verifiedJson = JsonSerializer.Serialize(new WindowsVerifiedStepEvidence(
            closure.Fingerprint, closure.PhysicalMemberFingerprint, null,
            "provider.returned"));

        var result = await fixture.Backend.ReconcileAsync(plan,
            [Progress(steps[0].Id, RealOperationStepState.Verified, verifiedJson),
                Progress(steps[1].Id, RealOperationStepState.Pending)],
            CancellationToken.None);

        Assert.Equal(RealOperationState.PartiallyCompleted, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.StoppedBeforeCall, result.Steps[1].State);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryKeepsPreparingCallUnknownEvenWhenTopologyIsUnchanged()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();

        var result = await fixture.Backend.ReconcileAsync(plan,
            [Progress(plan.RealOperation!.Steps[0].Id,
                RealOperationStepState.PreparingCall)], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryPromotesPersistedVerifiedResultWithoutReissuingCall()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        fixture.SetOffline(true);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var closure = topology.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId)]);
        var evidence = JsonSerializer.Serialize(new WindowsVerifiedStepEvidence(
            closure.Fingerprint, closure.PhysicalMemberFingerprint, null,
            "provider.returned"));

        var recovered = await fixture.Backend.ReconcileAsync(plan,
            [Progress("offline", RealOperationStepState.Verifying, evidence)],
            CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, recovered.State);
        Assert.True(recovered.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, Assert.Single(recovered.Steps).State);
        Assert.Equal(0, fixture.Adapter.CallCount);

        var uncertain = await fixture.Backend.ReconcileAsync(plan,
            [Progress("offline", RealOperationStepState.CallIssued)],
            CancellationToken.None);
        Assert.Equal(RealOperationState.OutcomeUnknown, uncertain.State);
        Assert.False(uncertain.CanReleaseWriteBarrier);
    }

    [Fact]
    public async Task RecoveryDoesNotClearBarrierWhenNoCallRecordHasExternalTopologyChange()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        fixture.SetOffline(true);

        var result = await fixture.Backend.ReconcileAsync(plan,
            [Progress(plan.RealOperation!.Steps[0].Id,
                RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryVerifiesSingleReturnedRenameFromCurrentFactsWithoutReplayingCall()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainRenameAsync(fixture);
        fixture.VolumeLabel = "NEW";
        var safetyCalls = fixture.Safety.CallCount;

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        var verified = Assert.Single(result.Steps);
        Assert.Equal(RealOperationStepState.Verified, verified.State);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(verified.ResultEvidence!);
        Assert.NotNull(evidence);
        Assert.Equal(plan.RealOperation!.PhysicalMemberFingerprint, evidence.PhysicalMemberFingerprint);
        Assert.NotEqual(plan.RealOperation.TargetFingerprint, evidence.PostFingerprint);
        Assert.Equal(safetyCalls + 1, fixture.Safety.CallCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-provider")]
    [InlineData("invalid-provider")]
    [InlineData("provider-not-returned")]
    [InlineData("provider-error")]
    [InlineData("provider-unique-id")]
    [InlineData("provider-object-id")]
    [InlineData("target-fingerprint")]
    [InlineData("plan-hash")]
    [InlineData("label")]
    [InlineData("parent-guid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("disk-identity")]
    [InlineData("disk-path")]
    [InlineData("volume-unique-id")]
    [InlineData("volume-object-id")]
    [InlineData("physical-member")]
    [InlineData("safety")]
    public async Task RecoveryKeepsUnprovenRenameUnknownWithoutCallingAdapter(string change)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainRenameAsync(fixture);
        fixture.VolumeLabel = "NEW";
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        switch (change)
        {
            case "missing-target": progress = progress with { TargetEvidence = null }; break;
            case "missing-provider": progress = progress with { ResultEvidence = null }; break;
            case "invalid-provider": progress = progress with { ResultEvidence = "invalid-json" }; break;
            case "provider-not-returned": provider = provider with { ProviderReturned = false }; break;
            case "provider-error": provider = provider with { Code = "provider.error-outcome-unknown" }; break;
            case "provider-unique-id": provider = provider with { UniqueId = "replacement" }; break;
            case "provider-object-id": provider = provider with { ObjectId = "replacement" }; break;
            case "target-fingerprint": target = target with { ExpectedFingerprint = "replacement" }; break;
            case "plan-hash": plan = plan with { PlanHash = "replacement" }; break;
            case "label": fixture.VolumeLabel = "WRONG"; break;
            case "parent-guid":
            case "offset":
            case "size":
            case "disk-identity":
                fixture.SnapshotTransform = snapshot => snapshot with
                {
                    Partitions = [snapshot.Partitions[0] with
                    {
                        Guid = change == "parent-guid" ? "bbbf78d3-d8e8-4bfd-9b54-67c21adf11d1" : snapshot.Partitions[0].Guid,
                        Offset = snapshot.Partitions[0].Offset + (change == "offset" ? 1048576 : 0),
                        Size = snapshot.Partitions[0].Size + (change == "size" ? 1048576 : 0),
                        OsDiskStableId = change == "disk-identity" ? "osdisk:replacement" : DiskId
                    }],
                    OsDisks = change == "disk-identity"
                        ? [snapshot.OsDisks[0] with { StableId = "osdisk:replacement" }]
                        : snapshot.OsDisks
                };
                break;
            case "disk-path":
            case "volume-unique-id":
            case "volume-object-id":
                fixture.FactsTransform = item => item.ObjectType ==
                    (change == "disk-path" ? FactObjectType.Disk : FactObjectType.Volume)
                    ? item with
                    {
                        Fields = item.Fields.Select(field => field.Name ==
                            (change == "disk-path" ? "Path" : change == "volume-unique-id" ? "UniqueId" : "ObjectId")
                            ? WinPoolSourceField.Returned(field.Name, "replacement",
                                FactValueType.String, field.SourceRef) : field).ToImmutableArray()
                    } : item;
                break;
            case "physical-member": fixture.ChangeSerial("REPLACEMENT"); break;
            case "safety": fixture.Safety.Reject = true; break;
        }
        if (change.StartsWith("provider-", StringComparison.Ordinal))
            progress = progress with { ResultEvidence = JsonSerializer.Serialize(provider) };
        if (change == "target-fingerprint")
            progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.OutcomeUnknown, Assert.Single(result.Steps).State);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task NormalRenamePreparationStillRejectsAnAlreadyAppliedLabel()
    {
        var fixture = new Fixture();
        var (plan, _) = await PrepareUncertainRenameAsync(fixture);
        fixture.VolumeLabel = "NEW";
        var request = new RealOperationIntentRequest(plan.Intent, plan.SystemId,
            [fixture.Id(StorageObjectKind.Volume, "volume:7")],
            plan.RealOperation!.Steps, plan.RealOperation.ExpectedFinalState);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PrepareAsync(
            request, fixture.Session, OperationId.New(), CancellationToken.None));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryDoesNotUseReturnedRenameEvidenceToResumeAMultiStepPlan()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainRenameAsync(fixture, twoSteps: true);
        fixture.VolumeLabel = "NEW";

        var result = await fixture.Backend.ReconcileAsync(plan,
            [progress, Progress("rename-again", RealOperationStepState.Pending)], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static async Task<(OperationPlan Plan, RealOperationStepProgress Progress)>
        PrepareUncertainRenameAsync(Fixture fixture, bool twoSteps = false)
    {
        var plan = await fixture.PrepareRenameAsync(twoSteps);
        var step = plan.RealOperation!.Steps[0];
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        var provider = new WindowsStorageCommandResult(true, "provider.returned",
            target.UniqueId, target.ObjectId, "", null, null, null, null);
        return (plan, new RealOperationStepProgress(step.Id, RealOperationStepState.OutcomeUnknown,
            "real.postcondition_unverified", preflight.TargetEvidenceJson, JsonSerializer.Serialize(provider)));
    }

    private static RealOperationStepProgress Progress(
        string id, RealOperationStepState state, string? evidence = null) =>
        new(id, state, null, null, evidence);

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
        private readonly SystemId systemId = SystemId.New();
        private readonly bool secondDisk;
        private readonly bool sharedOrdinaryPool;
        private string serial = "SERIAL-7";
        private bool offline;
        private bool hasVolume;

        public Fixture(bool secondDisk = false, bool sharedOrdinaryPool = false)
        {
            this.secondDisk = secondDisk;
            this.sharedOrdinaryPool = sharedOrdinaryPool;
            Source = new SyntheticFactSource(() => CreateDocument());
            Reader = new WindowsRealStorageTopologyReader(
                Source, new SyntheticMachineIdentity(), new FixedTimeProvider(Now));
            Adapter = new SyntheticAdapter();
            Safety = new SyntheticSafetyInspector();
            var planner = new WindowsRealOperationPlanner(
                Reader, new ForbiddenPartitionSizeReader(), new AdministratorPrivilege(),
                new FixedTimeProvider(Now), Safety);
            Backend = new WindowsRealStorageBackend(Adapter, planner, Reader, new FixedTimeProvider(Now));
        }

        public SyntheticFactSource Source { get; }
        public WindowsRealStorageTopologyReader Reader { get; }
        public SyntheticAdapter Adapter { get; }
        public WindowsRealStorageBackend Backend { get; }
        public SyntheticSafetyInspector Safety { get; }
        public string VolumeLabel { get; set; } = "OLD";
        public Func<StorageSnapshot, StorageSnapshot>? SnapshotTransform { get; set; }
        public Func<WinPoolSourceObject, WinPoolSourceObject>? FactsTransform { get; set; }
        public TrustedRealSession Session => new(SessionId.New(), "synthetic-product-session",
            "synthetic-process-instance", 1234, Now.AddMinutes(-1),
            @"C:\Synthetic\WinPool.Agent.exe", true);

        public StorageObjectId Id(StorageObjectKind kind, string key) => new(systemId, kind, key);
        public void SetOffline(bool value) => offline = value;
        public void ChangeSerial(string value) => serial = value;

        public async Task<OperationPlan> PrepareRenameAsync(bool twoSteps = false)
        {
            hasVolume = true;
            var volume = Id(StorageObjectKind.Volume, "volume:7");
            var step = new RealOperationStep("rename",
                new RenameVolumeCommand(RealTargetReference.ForExisting(volume), "NEW"),
                [], "old label", "new label", "", "synthetic source facts");
            var plan = await Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.SetVolumeLabel,
                systemId, [volume], [step], "new label"), Session, OperationId.New(), CancellationToken.None);
            if (!twoSteps) return plan;
            var second = new RealOperationStep("rename-again",
                new RenameVolumeCommand(RealTargetReference.ForExisting(volume), "NEXT"),
                [step.Id], "new label", "next label", "", "synthetic source facts");
            var proposal = new RealOperationIntentRequest(OperationIntent.SetVolumeLabel,
                systemId, plan.Targets, [plan.RealOperation!.Steps[0], second], "next label");
            var environment = new EnvironmentProfile(plan.EnvironmentId,
                EnvironmentKind.LocalMachine, MachineBinding,
                ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
                false, Now);
            return RealOperationPlanFactory.Create(proposal, OperationId.New(), environment,
                Session, plan.InventoryVersion, plan.RealOperation.TargetFingerprint,
                plan.RealOperation.PhysicalMemberFingerprint, "synthetic source facts", Now, Now.AddMinutes(5));
        }

        public async Task<OperationPlan> PrepareAsync(bool twoSteps = false)
        {
            var disk = Id(StorageObjectKind.OsDisk, DiskId);
            var first = new RealOperationStep("offline",
                new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), false),
                [], "synthetic online", "synthetic offline", "", "synthetic source facts");
            var steps = twoSteps
                ? new[] { first, new RealOperationStep("online",
                    new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), true),
                    [first.Id], "synthetic offline", "synthetic online", "", "synthetic source facts") }
                : [first];
            var proposal = new RealOperationIntentRequest(
                OperationIntent.SetDiskOnlineState, systemId,
                twoSteps ? [disk, Id(StorageObjectKind.PhysicalDisk, PhysicalId)] : [disk],
                steps,
                twoSteps ? "disk online" : "disk offline");
            var session = new TrustedRealSession(SessionId.New(), "synthetic-product-session",
                "synthetic-process-instance", 1234, Now.AddMinutes(-1),
                @"C:\Synthetic\WinPool.Agent.exe", true);
            if (!twoSteps)
                return await Backend.PrepareAsync(
                    proposal, session, OperationId.New(), CancellationToken.None);

            // A later online step is valid only after the first offline step.
            // The planner validates the current step against fresh facts, so the
            // two-step reconciliation fixture freezes a valid typed plan directly.
            var topology = await Reader.CaptureAsync(CancellationToken.None);
            var closure = topology.RequireSinglePhysicalClosure([disk]);
            var environment = new EnvironmentProfile(EnvironmentId.New(),
                EnvironmentKind.LocalMachine, MachineBinding,
                ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure,
                false, Now);
            return RealOperationPlanFactory.Create(proposal, OperationId.New(),
                environment, session, "synthetic-inventory", closure.Fingerprint,
                closure.PhysicalMemberFingerprint, "synthetic source facts",
                Now, Now.AddMinutes(5));
        }

        private StorageSystemDocument CreateDocument()
        {
            var snapshot = StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "synthetic-inventory",
                ScannedAt = Now,
                Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
                    "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
                StorageSubsystems = [new StorageSubsystemInfo("subsystem:synthetic", "Synthetic Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = secondDisk
                    ? [Physical(PhysicalId, serial, 7), Physical(OtherPhysicalId, "SERIAL-8", 8)]
                    : [Physical(PhysicalId, serial, 7)],
                StoragePools = sharedOrdinaryPool
                    ? [PrimordialPool(secondDisk), new StoragePoolInfo("pool:ordinary", true, "Synthetic Pool", false,
                        "Healthy", "OK", 2_000_000_000, 0, "subsystem:synthetic",
                        [PhysicalId, OtherPhysicalId])]
                    : [PrimordialPool(secondDisk)],
                OsDisks = secondDisk
                    ? [OsDisk(DiskId, PhysicalId, 7, offline), OsDisk(OtherDiskId, OtherPhysicalId, 8, false)]
                    : [OsDisk(DiskId, PhysicalId, 7, offline)]
            };
            if (hasVolume)
                snapshot = snapshot with
                {
                    OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
                    Partitions = [new PartitionInfo("partition:7", true, 7, 1, "GPT",
                        1048576, 128L << 20, false, false, "E", VolumeLabel, "NTFS", 65536,
                        128L << 20, "Healthy", "OK", @"E:\", DiskId,
                        PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                        Guid: "2f8ae502-1e4e-4d94-b190-6284ccb62bea",
                        GptType: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")],
                    Volumes = [new VolumeInfo("volume:7", true, "partition:7", "NTFS",
                        VolumeLabel, 128L << 20, 128L << 20, 65536, "Healthy", "OK", [@"E:\"])]
                };
            if (SnapshotTransform is not null) snapshot = SnapshotTransform(snapshot);
            var facts = WinPoolSimulationFacts.Create(snapshot, systemId);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            foreach (var className in new[] { "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage));
            }
            var objects = facts.Objects.Select(item => item.ObjectType == FactObjectType.Disk
                ? item with { Fields = item.Fields.Add(WinPoolSourceField.Returned(
                    "Path", $@"\\.\PHYSICALDRIVE{(item.Id == DiskId ? 7 : 8)}",
                    FactValueType.String, item.SourceRef)) }
                : item).ToImmutableArray();
            if (FactsTransform is not null) objects = objects.Select(FactsTransform).ToImmutableArray();
            facts = facts with
            {
                IsSimulation = false,
                InventoryVersion = "synthetic-inventory",
                InventoryCapturedAt = Now,
                Sources = sources,
                Objects = objects
            };
            return new StorageSystemDocument(
                StorageSystemDocument.CurrentSchemaVersion, "local:synthetic",
                StorageSystemKind.Local, "Synthetic Storage", facts, [], Now)
            {
                SystemId = systemId
            };
        }

        private PhysicalDiskInfo Physical(string id, string value, int number) =>
            new(id, true, "Synthetic Disk", "Synthetic Model", value, "SATA", "HDD",
                1_000_000_000, 512, 4096, "Healthy", "OK", true, "", number,
                false, false, false, false,
                sharedOrdinaryPool ? "pool:ordinary" : "pool:primordial");

        private static OsDiskInfo OsDisk(string id, string physicalId, int number, bool isOffline) =>
            new(id, "Synthetic OS Disk", number, "RAW", 1_000_000_000,
                false, false, isOffline, physicalId, null);

        private static StoragePoolInfo PrimordialPool(bool secondDisk) =>
            new("pool:primordial", true, "Primordial", true, "Healthy", "OK",
                secondDisk ? 2_000_000_000 : 1_000_000_000, 0,
                "subsystem:synthetic", secondDisk ? [PhysicalId, OtherPhysicalId] : [PhysicalId]);
    }

    private sealed class SyntheticFactSource(Func<StorageSystemDocument> capture) : IWindowsRealStorageFactSource
    {
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(capture());
    }

    private sealed class SyntheticMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MachineBinding);
    }

    private sealed class SyntheticAdapter : IWindowsRealStorageCommandAdapter
    {
        public Action? OnExecute { get; set; }
        public string ResultCode { get; set; } = "provider.returned";
        public int CallCount { get; private set; }
        public Task<WindowsStorageCommandResult> ExecuteAsync(
            RealStorageCommand command, WindowsStorageCommandTarget target,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OnExecute?.Invoke();
            return Task.FromResult(new WindowsStorageCommandResult(true, ResultCode,
                target.UniqueId, target.ObjectId, null, target.DiskNumber, null, null, null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ForbiddenPartitionSizeReader : IPartitionSupportedSizeReader
    {
        public Task<PartitionSupportedSize> ReadAsync(
            WindowsStorageCommandTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This synthetic test must not query a device.");
    }

    private sealed class SyntheticSafetyInspector : IWindowsRealStorageSafetyInspector
    {
        public int CallCount { get; private set; }
        public bool Reject { get; set; }
        public Task ValidateAsync(WindowsRealStorageTopology topology,
            RealTargetClosure closure, RealStorageCommand command,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Reject) throw new InvalidDataException("Synthetic safety facts are unsafe.");
            return Task.CompletedTask;
        }
    }

    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }
}
