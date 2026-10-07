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

    [Theory]
    [InlineData(40)]
    [InlineData(4096)]
    public async Task AdapterPreCallRejectionKeepsBoundedDiagnosticAndNoEffectProof(int diagnosticLength)
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var diagnostic = new string('x', diagnosticLength);
        fixture.Adapter.ResultTransform = result => result with
        {
            ProviderReturned = false, Code = "adapter.preflight-rejected",
            ProviderError = diagnostic
        };

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight,
            CancellationToken.None);

        Assert.Equal(RealStepOutcome.FailedWithoutEffect, result.Outcome);
        var evidence = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(result.ResultEvidenceJson);
        Assert.NotNull(evidence);
        Assert.True(evidence.NoWindowsCall);
        Assert.Equal(diagnostic[..Math.Min(diagnosticLength, 2048)], evidence.Diagnostic);
        var reconciliation = await fixture.Backend.ReconcileAsync(plan,
            [new RealOperationStepProgress(step.Id, RealOperationStepState.Failed,
                result.Code, preflight.TargetEvidenceJson, result.ResultEvidenceJson)],
            CancellationToken.None);
        Assert.Equal(RealOperationState.Failed, reconciliation.State);
        Assert.True(reconciliation.CanReleaseWriteBarrier);
    }

    [Fact]
    public async Task SafetyComFailureImmediatelyBeforeCallIsProvenNoEffect()
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        fixture.Safety.ProbeFailure = new System.Runtime.InteropServices.COMException(
            "Read-only VDS access was denied.", unchecked((int)0x80070005));

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight,
            CancellationToken.None);

        Assert.Equal(RealStepOutcome.FailedWithoutEffect, result.Outcome);
        Assert.Equal(0, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(result.ResultEvidenceJson);
        Assert.NotNull(evidence);
        Assert.True(evidence.NoWindowsCall);
        Assert.Contains("COMException", evidence.Diagnostic);
        var reconciliation = await fixture.Backend.ReconcileAsync(plan,
            [new RealOperationStepProgress(step.Id, RealOperationStepState.Failed,
                result.Code, preflight.TargetEvidenceJson, result.ResultEvidenceJson)],
            CancellationToken.None);
        Assert.Equal(RealOperationState.Failed, reconciliation.State);
        Assert.True(reconciliation.CanReleaseWriteBarrier);
    }

    [Fact]
    public async Task CreatedEfiFormatRechecksExactCreatedTargetAndPersistsFreshNativeApplicabilityProof()
    {
        var fixture = new Fixture();
        var (plan, step, preflight) = await PrepareCreatedEfiFormatAsync(fixture);
        var native = CreatedEfiNativeProof();
        var applicability = new WindowsVolumeSafetyEvidence("synthetic-inventory", "volume:efi",
            "partition:efi", @"\\?\Volume{bb7fc4ad-1a8e-4f81-90de-d2aa179373ca}\", true,
            "NotApplicableNativeEfiSystemPartition", null, null, native.VerifiedAt);
        fixture.Safety.CreatedFormatEvidence = new(native, [applicability]);
        fixture.Safety.Reject = true;
        var callsBeforeExecute = fixture.Safety.CreatedFormatCallCount;
        fixture.Adapter.OnExecute = () => fixture.SnapshotTransform = snapshot => EfiSnapshot(snapshot, formatted: true);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(callsBeforeExecute + 1, fixture.Safety.CreatedFormatCallCount);
        Assert.Equal("partition:efi", fixture.Safety.LastCreatedFormatTarget!.Value.ProviderKey);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!;
        Assert.Equal(native, evidence.NativeMsrSafetyEvidence);
        Assert.Equal(applicability, Assert.Single(evidence.VolumeSafetyEvidence!));
    }

    [Theory]
    [InlineData("created-flag")]
    [InlineData("guid")]
    [InlineData("object-id")]
    [InlineData("unique-id")]
    [InlineData("parent-path")]
    [InlineData("role")]
    [InlineData("missing-native")]
    [InlineData("stale-native")]
    [InlineData("wrong-native-partition")]
    [InlineData("native-read-only")]
    [InlineData("native-read-failed")]
    public async Task CreatedEfiFormatNeverCallsProviderWithWrongTargetOrUnprovenNativeSafety(string change)
    {
        var fixture = new Fixture();
        var (plan, step, preflight) = await PrepareCreatedEfiFormatAsync(fixture);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        var native = CreatedEfiNativeProof();
        switch (change)
        {
            case "created-flag": target = target with { CreatedInThisPlan = false }; break;
            case "guid": target = target with { PartitionGuid = Guid.NewGuid().ToString() }; break;
            case "object-id": target = target with { ObjectId = "replacement" }; break;
            case "unique-id": target = target with { UniqueId = "replacement" }; break;
            case "parent-path": target = target with { OsDiskPath = "replacement" }; break;
            case "role": target = target with { PartitionTypeGuid = WindowsNativeMsrAttributesReader.BasicDataRole.ToString() }; break;
            case "stale-native": native = native with { InventoryVersion = "old-observation" }; break;
            case "wrong-native-partition": native = native with { PartitionStableId = "replacement" }; break;
            case "native-read-only": native = native with { Attributes = WindowsNativeMsrAttributes.ReadOnly }; break;
            case "native-read-failed": fixture.Safety.CreatedFormatFailure = new IOException("native read failed"); break;
        }
        fixture.Safety.CreatedFormatEvidence = new(change == "missing-native" ? null : native, []);
        preflight = preflight with { TargetEvidenceJson = JsonSerializer.Serialize(target) };
        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.FailedWithoutEffect, result.Outcome);
        Assert.Equal("real.pre_call_safety_unverified", result.Code);
        Assert.Equal(0, fixture.Adapter.CallCount);
        Assert.True(JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(result.ResultEvidenceJson)!.NoWindowsCall);
    }

    private static WindowsNativeMsrSafetyEvidence CreatedEfiNativeProof() => new(
        "synthetic-inventory", "partition:efi", @"\\.\PHYSICALDRIVE7", DiskId, DiskId,
        Guid.Parse("a60865f6-af7f-46d4-8e8f-d375816be42c"), PhysicalId, PhysicalId, "SERIAL-7",
        Guid.Parse("7389d26f-9da4-403c-87e7-0ac707428dd5"), WindowsNativeMsrAttributesReader.EfiRole,
        1048576, 260L << 20, 1, 0, DateTimeOffset.Parse("2026-09-27T08:00:00Z"));

    private static StorageSnapshot EfiSnapshot(StorageSnapshot snapshot, bool formatted = false) => snapshot with
    {
        OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
        Partitions = [new PartitionInfo("partition:efi", true, 7, 1, "GPT", 1048576, 260L << 20,
            false, false, "", formatted ? "WP_S1_EFI" : "", formatted ? "FAT32" : "", formatted ? 4096 : 0,
            260L << 20, "Healthy", "OK", "", DiskId,
            PartitionTypeId: WindowsNativeMsrAttributesReader.EfiRole.ToString(),
            Guid: "7389d26f-9da4-403c-87e7-0ac707428dd5", GptType: WindowsNativeMsrAttributesReader.EfiRole.ToString())],
        Volumes = [new VolumeInfo("volume:efi", true, "partition:efi", formatted ? "FAT32" : "",
            formatted ? "WP_S1_EFI" : "", 260L << 20, 260L << 20, formatted ? 4096 : 0, "Healthy", "OK",
            [@"\\?\Volume{bb7fc4ad-1a8e-4f81-90de-d2aa179373ca}\"])]
    };

    private static async Task<(OperationPlan Plan, RealOperationStep Step, RealStepPreflight Preflight)>
        PrepareCreatedEfiFormatAsync(Fixture fixture)
    {
        fixture.SnapshotTransform = snapshot => snapshot with
            { OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }] };
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var create = new RealOperationStep("create-efi", new CreatePartitionCommand(
            RealTargetReference.ForExisting(disk), RealPartitionRole.Efi, 1048576, 260L << 20), [],
            "Free exact GPT gap", "Exact EFI created", "", "synthetic source facts");
        var format = new RealOperationStep("format-efi", new FormatVolumeCommand(
            RealTargetReference.FromStep(StorageObjectKind.Partition, create.Id), RealFileSystem.Fat32,
            4096, false, "WP_S1_EFI"), [create.Id], "Exact new EFI", "FAT32 4096",
            "Formatting erases all data on the newly created EFI partition.", "synthetic source facts");
        var plan = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.CreatePartition,
            disk.System, [disk], [create, format], "New formatted EFI"), fixture.Session, OperationId.New(), CancellationToken.None);
        fixture.SnapshotTransform = snapshot => EfiSnapshot(snapshot);
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var closure = topology.RequireSinglePhysicalClosure([disk]);
        var created = new WindowsVerifiedStepEvidence(closure.Fingerprint, closure.PhysicalMemberFingerprint,
            "partition:efi", "provider.returned");
        fixture.Safety.CreatedFormatEvidence = new(CreatedEfiNativeProof(), []);
        var step = plan.RealOperation!.Steps[1];
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string> { [create.Id] = JsonSerializer.Serialize(created) }, CancellationToken.None);
        return (plan, step, preflight);
    }

    [Fact]
    public async Task VolumeSafetyProofIsFrozenAndFreshPreCallProofIsPersisted()
    {
        var fixture = new Fixture();
        var frozen = new WindowsVolumeSafetyEvidence("snapshot-one", "volume:synthetic",
            "partition:synthetic", @"\\?\Volume{bb7fc4ad-1a8e-4f81-90de-d2aa179373ca}\",
            true, "NotApplicableUnformatted", 1, 512,
            DateTimeOffset.Parse("2026-09-27T08:00:00Z"));
        fixture.Safety.Evidence = new(null, [frozen]);
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        Assert.Contains(";volume-safety:" + JsonSerializer.Serialize(new[] { frozen }),
            step.SupportEvidence);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var live = frozen with { InventoryVersion = "snapshot-two", VerifiedAt = frozen.VerifiedAt.AddSeconds(1) };
        fixture.Safety.Evidence = new(null, [live]);
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight,
            CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson);
        Assert.NotNull(evidence);
        Assert.Equal(live, Assert.Single(evidence.VolumeSafetyEvidence!));
        Assert.Null(evidence.NativeMsrSafetyEvidence);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryVerifiesSingleReturnedGptDiskStateWithoutReplayingCall(bool online)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainDiskStateAsync(fixture, online);
        fixture.SetOffline(!online);
        var safetyCalls = fixture.Safety.CallCount;
        var liveProof = new WindowsVolumeSafetyEvidence("current-observation", "volume:proof",
            "partition:proof", @"\\?\Volume{bb7fc4ad-1a8e-4f81-90de-d2aa179373ca}\",
            true, "NotApplicableUnformatted", 1, 512, DateTimeOffset.Parse("2026-09-27T08:00:00Z"));
        fixture.Safety.Evidence = new(null, [liveProof]);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, Assert.Single(result.Steps).State);
        Assert.Equal("real.reconciliation_verified_observed_disk_online_state", result.Steps[0].Code);
        Assert.Equal(safetyCalls + 1, fixture.Safety.CallCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.Steps[0].ResultEvidence!);
        Assert.NotNull(evidence);
        Assert.Equal(plan.RealOperation!.PhysicalMemberFingerprint, evidence.PhysicalMemberFingerprint);
        Assert.NotEqual(plan.RealOperation.TargetFingerprint, evidence.PostFingerprint);
        Assert.Equal(liveProof, Assert.Single(evidence.VolumeSafetyEvidence!));
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-provider")]
    [InlineData("invalid-provider")]
    [InlineData("provider-not-returned")]
    [InlineData("provider-error-code")]
    [InlineData("provider-error")]
    [InlineData("provider-job")]
    [InlineData("provider-unique-id")]
    [InlineData("provider-object-id")]
    [InlineData("provider-disk-number")]
    [InlineData("provider-child-partition")]
    [InlineData("missing-disk-guid")]
    [InlineData("wrong-disk-guid")]
    [InlineData("current-disk-guid")]
    [InlineData("current-disk-guid-missing")]
    [InlineData("target-fingerprint")]
    [InlineData("target-path")]
    [InlineData("plan-hash")]
    [InlineData("state")]
    [InlineData("physical-member")]
    [InlineData("safety")]
    [InlineData("safety-read-failed")]
    public async Task RecoveryKeepsUnprovenDiskStateUnknownWithoutCallingAdapter(string change)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainDiskStateAsync(fixture, false);
        fixture.SetOffline(true);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        switch (change)
        {
            case "missing-target": progress = progress with { TargetEvidence = null }; break;
            case "missing-provider": progress = progress with { ResultEvidence = null }; break;
            case "invalid-provider": progress = progress with { ResultEvidence = "invalid-json" }; break;
            case "provider-not-returned": provider = provider with { ProviderReturned = false }; break;
            case "provider-error-code": provider = provider with { Code = "provider.error-outcome-unknown" }; break;
            case "provider-error": provider = provider with { ProviderError = "failed" }; break;
            case "provider-job": provider = provider with { ProviderJobId = 1 }; break;
            case "provider-unique-id": provider = provider with { UniqueId = "replacement" }; break;
            case "provider-object-id": provider = provider with { ObjectId = "replacement" }; break;
            case "provider-disk-number": provider = provider with { DiskNumber = 8 }; break;
            case "provider-child-partition": provider = provider with { PartitionNumber = 1 }; break;
            case "missing-disk-guid": provider = provider with { PartitionGuid = null }; break;
            case "wrong-disk-guid": provider = provider with { PartitionGuid = Guid.NewGuid().ToString() }; break;
            case "current-disk-guid":
            case "current-disk-guid-missing":
                var originalTransform = fixture.FactsTransform!;
                fixture.FactsTransform = item =>
                {
                    item = originalTransform(item);
                    return item.ObjectType == FactObjectType.Disk ? item with
                    {
                        Fields = item.Fields.Where(field => field.Name != "Guid").ToImmutableArray()
                            .Add(WinPoolSourceField.Returned("Guid",
                                change == "current-disk-guid" ? Guid.NewGuid().ToString() : string.Empty,
                                FactValueType.String, item.SourceRef))
                    } : item;
                };
                break;
            case "target-fingerprint": target = target with { ExpectedFingerprint = "replacement" }; break;
            case "target-path": target = target with { OsDiskPath = "replacement" }; break;
            case "plan-hash": plan = plan with { PlanHash = "replacement" }; break;
            case "state": fixture.SetOffline(false); break;
            case "physical-member": fixture.ChangeSerial("REPLACEMENT"); break;
            case "safety": fixture.Safety.Reject = true; break;
            case "safety-read-failed": fixture.Safety.ProbeFailure = new IOException("native read failed"); break;
        }
        if (change.StartsWith("provider-", StringComparison.Ordinal)
            || change is "missing-disk-guid" or "wrong-disk-guid")
            progress = progress with { ResultEvidence = JsonSerializer.Serialize(provider) };
        if (change.StartsWith("target-", StringComparison.Ordinal))
            progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.OutcomeUnknown, Assert.Single(result.Steps).State);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryDoesNotUseReturnedDiskStateEvidenceToResumeAMultiStepPlan()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainDiskStateAsync(fixture, false, twoSteps: true);
        fixture.SetOffline(true);

        var result = await fixture.Backend.ReconcileAsync(plan,
            [progress, Progress("online", RealOperationStepState.StoppedBeforeCall)], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DiskOfflinePostconditionUsesObservedSafetyAndPersistsCurrentNativeProof()
    {
        var fixture = new Fixture();
        var (plan, _) = await PrepareUncertainDiskStateAsync(fixture, false, offlinePartition: true);
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var proof = OfflinePartitionProof();
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);
        fixture.Safety.ObservedEvidence = new(null, [], [proof]);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(1, fixture.Safety.ObservedCallCount);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var current = await fixture.Reader.CaptureAsync(CancellationToken.None);
        Assert.Equal(["partition:offline"], current.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId)]).OfflinePartitionIdsNeedingNativeProof);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!;
        Assert.Equal(proof, Assert.Single(evidence.OfflinePartitionAttributes!));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("duplicate")]
    [InlineData("wrong-partition")]
    [InlineData("stale")]
    [InlineData("online-proof")]
    [InlineData("failed")]
    public async Task DiskOfflinePostconditionAndRecoveryKeepBarrierWithoutCurrentNativeProof(string change)
    {
        var fixture = new Fixture(expirePostCallWindow: true);
        var (plan, progress) = await PrepareUncertainDiskStateAsync(fixture, false, offlinePartition: true);
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var proof = OfflinePartitionProof();
        WindowsNativeMsrSafetyEvidence[]? proofs = change switch
        {
            "absent" => null,
            "duplicate" => new[] { proof, proof },
            "wrong-partition" => [proof with { PartitionStableId = "partition:replacement" }],
            "stale" => [proof with { InventoryVersion = "old-observation" }],
            "online-proof" => [proof with { DiskIsOffline = false }],
            _ => new[] { proof }
        };
        fixture.Safety.ObservedEvidence = new(null, [], proofs);
        fixture.Safety.ObservedFailure = change == "failed" ? new IOException("Native read failed") : null;
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("real.postcondition_unverified", result.Code);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var current = await fixture.Reader.CaptureAsync(CancellationToken.None);
        Assert.Equal(["partition:offline"], current.RequireSinglePhysicalClosure(
            [fixture.Id(StorageObjectKind.OsDisk, DiskId)]).OfflinePartitionIdsNeedingNativeProof);

        var recovered = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);
        Assert.Equal(RealOperationState.OutcomeUnknown, recovered.State);
        Assert.False(recovered.CanReleaseWriteBarrier);
        Assert.Equal(1, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task DiskStateRecoveryPersistsObservedOfflineProofWithoutUsingOrdinaryMutationValidation()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainDiskStateAsync(fixture, false, offlinePartition: true);
        fixture.SetOffline(true);
        var proof = OfflinePartitionProof();
        fixture.Safety.ObservedEvidence = new(null, [], [proof]);
        fixture.Safety.Reject = true; // Fresh no-op mutation validation must not run.

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(1, fixture.Safety.ObservedCallCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.Steps[0].ResultEvidence!)!;
        Assert.Equal(proof, Assert.Single(evidence.OfflinePartitionAttributes!));
    }

    private static WindowsNativeMsrSafetyEvidence OfflinePartitionProof() => new(
        "synthetic-inventory", "partition:offline", @"\\.\PHYSICALDRIVE7", DiskId, DiskId,
        Guid.Parse("a60865f6-af7f-46d4-8e8f-d375816be42c"), PhysicalId, PhysicalId, "SERIAL-7",
        Guid.Parse("2a812f6d-d3d2-4ac6-834d-1b8980b98df6"), WindowsNativeMsrAttributesReader.MsrRole,
        1048576, 16777216, 1, 0, DateTimeOffset.Parse("2026-09-27T08:00:00Z")) { DiskIsOffline = true };

    private static async Task<(OperationPlan Plan, RealOperationStepProgress Progress)>
        PrepareUncertainDiskStateAsync(Fixture fixture, bool online, bool twoSteps = false, bool offlinePartition = false)
    {
        const string diskGuid = "a60865f6-af7f-46d4-8e8f-d375816be42c";
        fixture.SnapshotTransform = snapshot => snapshot with
        {
            OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
            Partitions = offlinePartition ? [new PartitionInfo("partition:offline", true, 7, 1, "GPT",
                1048576, 16777216, false, false, "", "", "", 0, 0, "Healthy", "OK", "", DiskId,
                PartitionTypeId: WindowsNativeMsrAttributesReader.MsrRole.ToString(),
                Guid: "2a812f6d-d3d2-4ac6-834d-1b8980b98df6",
                GptType: WindowsNativeMsrAttributesReader.MsrRole.ToString())] : snapshot.Partitions
        };
        fixture.FactsTransform = item =>
        {
            if (item.ObjectType == FactObjectType.Disk)
                return item with { Fields = item.Fields.Where(field => field.Name != "Guid").ToImmutableArray()
                    .Add(WinPoolSourceField.Returned("Guid", diskGuid, FactValueType.String, item.SourceRef)) };
            if (offlinePartition && item.ObjectType == FactObjectType.Partition)
                return item with { Fields = item.Fields.Select(field => field.Name switch
                {
                    "DiskId" => WinPoolSourceField.Returned("DiskId", @"\\.\PHYSICALDRIVE7", FactValueType.String, field.SourceRef),
                    "IsHidden" when fixture.IsOffline => WinPoolSourceField.Returned<object?>("IsHidden", null, FactValueType.Boolean, field.SourceRef),
                    _ => field
                }).ToImmutableArray() };
            return item;
        };
        fixture.SetOffline(online);
        OperationPlan plan;
        if (twoSteps) plan = await fixture.PrepareAsync(twoSteps: true);
        else
        {
            var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
            var step = new RealOperationStep("disk-state",
                new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), online), [],
                "Exact current disk state", "Exact requested disk state", "", "synthetic source facts");
            plan = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(
                OperationIntent.SetDiskOnlineState, disk.System, [disk], [step], "Exact requested disk state"),
                fixture.Session, OperationId.New(), CancellationToken.None);
        }
        var preflight = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        var provider = new WindowsStorageCommandResult(true, "provider.returned", target.UniqueId,
            target.ObjectId, diskGuid, target.DiskNumber, null, null, null);
        return (plan, Progress(plan.RealOperation.Steps[0].Id, RealOperationStepState.OutcomeUnknown,
            JsonSerializer.Serialize(provider)) with { TargetEvidence = preflight.TargetEvidenceJson });
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeGptVerifiesZeroPartitionsOrExactNativeUnformattedMsr(bool nativeMsr)
    {
        var fixture = new Fixture();
        var plan = await fixture.PrepareInitializeAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        fixture.Adapter.OnExecute = () => fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, nativeMsr);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!;
        Assert.NotNull(evidence.GptInitialization);
        Assert.Equal(DiskId, evidence.GptInitialization.OsDiskStableId);
        Assert.Equal(nativeMsr ? "partition:native-msr" : null, evidence.GptInitialization.ProviderMsrStableId);
        Assert.Equal(nativeMsr ? (long?)17408 : null, evidence.GptInitialization.ProviderMsrOffsetBytes);
        Assert.Equal(nativeMsr ? (long?)16759808 : null, evidence.GptInitialization.ProviderMsrSizeBytes);
        var reconciled = await fixture.Backend.ReconcileAsync(plan,
            [Progress(step.Id, RealOperationStepState.Verified, result.ResultEvidenceJson)], CancellationToken.None);
        Assert.Equal(RealOperationState.Succeeded, reconciled.State);
        Assert.True(reconciled.CanReleaseWriteBarrier);
        Assert.Equal(1, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("extra-partition")]
    [InlineData("role")]
    [InlineData("gpt-role")]
    [InlineData("guid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("file-system")]
    [InlineData("letter")]
    [InlineData("volume")]
    [InlineData("boot")]
    [InlineData("system")]
    [InlineData("disk-offline")]
    [InlineData("disk-boot")]
    [InlineData("disk-system")]
    [InlineData("provider-error")]
    [InlineData("provider-unique-id")]
    [InlineData("provider-object-id")]
    [InlineData("provider-disk-number")]
    public async Task InitializeGptRejectsUnexpectedProviderResultOrPostLayout(string change)
    {
        var fixture = new Fixture(expirePostCallWindow: true);
        var plan = await fixture.PrepareInitializeAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        fixture.Adapter.OnExecute = () => fixture.SnapshotTransform = snapshot =>
            MutateInitializedSnapshot(InitializedSnapshot(snapshot, true), change);
        if (change == "provider-error") fixture.Adapter.ResultCode = "provider.error-outcome-unknown";
        fixture.Adapter.ResultTransform = result => change switch
        {
            "provider-unique-id" => result with { UniqueId = "replacement" },
            "provider-object-id" => result with { ObjectId = "replacement" },
            "provider-disk-number" => result with { DiskNumber = 8 },
            _ => result
        };

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        AssertInitializationMutationReachedProjection(
            (await fixture.Reader.CaptureAsync(CancellationToken.None)).Snapshot, change);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryVerifiesReturnedInitializationPrefixWithoutCallingOrResumingMsrCreation()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainInitializeAsync(fixture);
        fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, true);
        var safetyCalls = fixture.Safety.CallCount;
        var stopped = Progress("create-msr", RealOperationStepState.StoppedBeforeCall);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress, stopped], CancellationToken.None);

        Assert.Equal(RealOperationState.PartiallyCompleted, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, result.Steps[0].State);
        Assert.Equal(stopped, result.Steps[1]);
        Assert.Null(result.Steps[1].TargetEvidence);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.Steps[0].ResultEvidence!)!;
        Assert.Equal("partition:native-msr", evidence.GptInitialization!.ProviderMsrStableId);
        Assert.True(fixture.Safety.CallCount > safetyCalls);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task RecoveryAcceptsHistoricalEmptyAuxiliaryIdsWhileRetainingExactDiskAndPhysicalEvidence()
    {
        var fixture = new Fixture();
        fixture.FactsTransform = WithDistinctPhysicalObjectId;
        var (plan, progress) = await PrepareUncertainInitializeAsync(fixture);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        Assert.Equal("physical-object:exact", target.PhysicalMemberObjectId);
        progress = progress with { TargetEvidence = JsonSerializer.Serialize(target with
        {
            PhysicalMemberObjectId = "", StorageSubsystemObjectId = ""
        }) };
        fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, true);
        var stopped = Progress("create-msr", RealOperationStepState.StoppedBeforeCall);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress, stopped], CancellationToken.None);

        Assert.Equal(RealOperationState.PartiallyCompleted, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, result.Steps[0].State);
        Assert.Equal(stopped, result.Steps[1]);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("subsystem")]
    public async Task RecoveryRejectsPopulatedAuxiliaryIdentityMismatch(string changedAuxiliary)
    {
        var fixture = new Fixture();
        fixture.FactsTransform = WithDistinctPhysicalObjectId;
        var (plan, progress) = await PrepareUncertainInitializeAsync(fixture);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        Assert.Equal("physical-object:exact", target.PhysicalMemberObjectId);
        target = changedAuxiliary == "physical"
            ? target with { PhysicalMemberObjectId = "physical-object:replacement" }
            : target with { StorageSubsystemObjectId = "subsystem-object:replacement" };
        progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };
        fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, true);
        var stopped = Progress("create-msr", RealOperationStepState.StoppedBeforeCall);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress, stopped], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(progress, result.Steps[0]);
        Assert.Equal(stopped, result.Steps[1]);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannerPreparesMsrContinuationUsingEmptyGapOrExplicitNativeMsrRemoval(bool nativeMsr)
    {
        var fixture = new Fixture();
        fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, nativeMsr);

        var plan = await fixture.PrepareInitializationContinuationAsync(nativeMsr);

        Assert.Equal(OperationIntent.InitializeDisk, plan.Intent);
        Assert.Equal(nativeMsr ? 2 : 1, plan.RealOperation!.Steps.Count);
        var create = Assert.IsType<CreatePartitionCommand>(plan.RealOperation.Steps[^1].Command);
        Assert.Equal(RealPartitionRole.Msr, create.Role);
        Assert.Equal(1048576, create.OffsetBytes);
        Assert.Equal(16777216, create.SizeBytes);
        Assert.Equal(fixture.Id(StorageObjectKind.OsDisk, DiskId), create.Disk.Existing);
        if (nativeMsr)
        {
            var remove = Assert.IsType<DeletePartitionCommand>(plan.RealOperation.Steps[0].Command);
            Assert.Equal(fixture.Id(StorageObjectKind.Partition, "partition:native-msr"), remove.Partition.Existing);
            Assert.Contains(plan.RealOperation.Steps[0].Id, plan.RealOperation.Steps[1].DependsOn);
        }
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("extra-partition")]
    public async Task PlannerRejectsNormalizationOfOrdinaryOrAdditionalPartitionsBeforeAnyProviderCall(string change)
    {
        var fixture = new Fixture();
        fixture.SnapshotTransform = snapshot => MutateInitializedSnapshot(InitializedSnapshot(snapshot, true), change);

        AssertInitializationMutationReachedProjection(
            (await fixture.Reader.CaptureAsync(CancellationToken.None)).Snapshot, change);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareInitializationContinuationAsync(true));

        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static WinPoolSourceObject WithDistinctPhysicalObjectId(WinPoolSourceObject item) =>
        item.ObjectType == FactObjectType.PhysicalDisk ? item with
        {
            Fields = item.Fields.Select(field => field.Name == "ObjectId"
                ? WinPoolSourceField.Returned(field.Name, "physical-object:exact", FactValueType.String, field.SourceRef)
                : field).ToImmutableArray()
        } : item;

    [Fact]
    public async Task PlannerRejectsNewCombinedRawInitializationAndMsrCreationProposalBeforeProviderCall()
    {
        var fixture = new Fixture();
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var proposal = new RealOperationIntentRequest(OperationIntent.InitializeDisk, disk.System, [disk],
            [new RealOperationStep("initialize", new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                [], "RAW zero partitions", "GPT initialized", "", "synthetic source facts"),
             new RealOperationStep("create-msr", new CreatePartitionCommand(RealTargetReference.ForExisting(disk),
                RealPartitionRole.Msr, 1048576, 16777216), ["initialize"], "GPT gap", "MSR exists", "", "synthetic source facts")],
            "GPT and MSR");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Backend.PrepareAsync(
            proposal, fixture.Session, OperationId.New(), CancellationToken.None));

        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("zero-msr")]
    [InlineData("extra-partition")]
    [InlineData("role")]
    [InlineData("gpt-role")]
    [InlineData("guid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("file-system")]
    [InlineData("letter")]
    [InlineData("volume")]
    [InlineData("boot")]
    [InlineData("system")]
    [InlineData("disk-offline")]
    [InlineData("disk-boot")]
    [InlineData("disk-system")]
    [InlineData("missing-target")]
    [InlineData("missing-provider")]
    [InlineData("invalid-provider")]
    [InlineData("provider-not-returned")]
    [InlineData("provider-error")]
    [InlineData("provider-job")]
    [InlineData("provider-error-field")]
    [InlineData("provider-unique-id")]
    [InlineData("provider-object-id")]
    [InlineData("provider-disk-number")]
    [InlineData("target-fingerprint")]
    [InlineData("target-disk-number")]
    [InlineData("target-path")]
    [InlineData("target-size")]
    [InlineData("plan-hash")]
    [InlineData("physical-member")]
    [InlineData("safety")]
    [InlineData("remaining-target")]
    [InlineData("remaining-call-issued")]
    [InlineData("remaining-preparing")]
    [InlineData("remaining-pending")]
    [InlineData("initial-call-issued")]
    [InlineData("initial-verifying")]
    public async Task RecoveryKeepsInitializationBarrierForChangedEvidenceOrStartedRemainingStep(string change)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainInitializeAsync(fixture);
        fixture.SnapshotTransform = snapshot => MutateInitializedSnapshot(
            InitializedSnapshot(snapshot, change != "zero-msr"), change);
        var remaining = Progress("create-msr", RealOperationStepState.StoppedBeforeCall);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        switch (change)
        {
            case "missing-target": progress = progress with { TargetEvidence = null }; break;
            case "missing-provider": progress = progress with { ResultEvidence = null }; break;
            case "invalid-provider": progress = progress with { ResultEvidence = "invalid-json" }; break;
            case "provider-not-returned": provider = provider with { ProviderReturned = false }; break;
            case "provider-error": provider = provider with { Code = "provider.error-outcome-unknown" }; break;
            case "provider-job": provider = provider with { ProviderJobId = 7 }; break;
            case "provider-error-field": provider = provider with { ProviderError = "provider failure" }; break;
            case "provider-unique-id": provider = provider with { UniqueId = "replacement" }; break;
            case "provider-object-id": provider = provider with { ObjectId = "replacement" }; break;
            case "provider-disk-number": provider = provider with { DiskNumber = 8 }; break;
            case "target-fingerprint": target = target with { ExpectedFingerprint = "replacement" }; break;
            case "target-disk-number": target = target with { DiskNumber = 8 }; break;
            case "target-path": target = target with { OsDiskPath = @"\\.\PHYSICALDRIVE8" }; break;
            case "target-size": target = target with { SizeBytes = target.SizeBytes + 1048576 }; break;
            case "plan-hash": plan = plan with { PlanHash = "replacement" }; break;
            case "physical-member": fixture.ChangeSerial("REPLACEMENT"); break;
            case "safety": fixture.Safety.Reject = true; break;
            case "remaining-target": remaining = remaining with { TargetEvidence = progress.TargetEvidence }; break;
            case "remaining-call-issued": remaining = remaining with { State = RealOperationStepState.CallIssued }; break;
            case "remaining-preparing": remaining = remaining with { State = RealOperationStepState.PreparingCall }; break;
            case "remaining-pending": remaining = remaining with { State = RealOperationStepState.Pending }; break;
            case "initial-call-issued": progress = progress with { State = RealOperationStepState.CallIssued }; break;
            case "initial-verifying": progress = progress with { State = RealOperationStepState.Verifying }; break;
        }
        if (change.StartsWith("provider-", StringComparison.Ordinal))
            progress = progress with { ResultEvidence = JsonSerializer.Serialize(provider) };
        if (change.StartsWith("target-", StringComparison.Ordinal))
            progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };

        AssertInitializationMutationReachedProjection(
            (await fixture.Reader.CaptureAsync(CancellationToken.None)).Snapshot, change);
        var result = await fixture.Backend.ReconcileAsync(plan, [progress, remaining], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(progress.State, result.Steps[0].State);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static async Task<(OperationPlan Plan, RealOperationStepProgress Progress)>
        PrepareUncertainInitializeAsync(Fixture fixture)
    {
        var plan = await fixture.PrepareInitializeAsync(twoSteps: true);
        var step = plan.RealOperation!.Steps[0];
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step,
            new Dictionary<string, string>(), CancellationToken.None);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        var provider = new WindowsStorageCommandResult(true, "provider.returned", target.UniqueId,
            target.ObjectId, null, target.DiskNumber, null, null, null);
        return (plan, new RealOperationStepProgress(step.Id, RealOperationStepState.OutcomeUnknown,
            "real.postcondition_unverified", preflight.TargetEvidenceJson, JsonSerializer.Serialize(provider)));
    }

    private static StorageSnapshot InitializedSnapshot(StorageSnapshot snapshot, bool nativeMsr) => snapshot with
    {
        OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
        Partitions = nativeMsr ? [new PartitionInfo("partition:native-msr", true, 7, 1, "GPT",
            17408, 16759808, false, false, "", "", "", null, 0, "Healthy", "OK", "", DiskId,
            PartitionTypeId: "e3c9e316-0b5c-4db8-817d-f92df00215ae",
            Guid: "ddc147db-0c90-482e-bff2-516cb10a7d00",
            GptType: "e3c9e316-0b5c-4db8-817d-f92df00215ae")] : []
    };

    private static StorageSnapshot MutateInitializedSnapshot(StorageSnapshot snapshot, string change)
    {
        if (change.StartsWith("disk-", StringComparison.Ordinal))
            return snapshot with { OsDisks = [snapshot.OsDisks[0] with
            {
                IsOffline = change == "disk-offline", IsBoot = change == "disk-boot", IsSystem = change == "disk-system"
            }] };
        if (snapshot.Partitions.Count == 0) return snapshot;
        var msr = snapshot.Partitions[0];
        if (change == "extra-partition") return snapshot with
        {
            Partitions = [msr, msr with { StableId = "partition:extra", PartitionNumber = 2,
                Guid = "ca90b3b6-31c0-4a76-aaf2-4b4fa9c70292", Offset = 17L << 20, Size = 16L << 20 }]
        };
        // FS and access paths belong to the associated MSFT_Volume source.
        // Snapshot-only partition fields are rebuilt from that source during
        // projection, so malformed provider states need real volume facts.
        if (change is "volume" or "file-system" or "letter") return snapshot with
        {
            Volumes = [new VolumeInfo("volume:msr", true, msr.StableId,
                change == "file-system" ? "NTFS" : "RAW", "",
                msr.Size, msr.Size, null, "Healthy", "OK", change == "letter" ? [@"E:\"] : [])]
        };
        return snapshot with { Partitions = [msr with
        {
            PartitionTypeId = change == "role" ? "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" : msr.PartitionTypeId,
            GptType = change is "role" or "gpt-role" ? "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" : msr.GptType,
            Guid = change == "guid" ? "invalid" : msr.Guid,
            Offset = msr.Offset + (change == "offset" ? 512 : 0), Size = msr.Size + (change == "size" ? 512 : 0),
            FileSystem = change == "file-system" ? "NTFS" : msr.FileSystem,
            DriveLetter = change == "letter" ? "E" : msr.DriveLetter,
            IsBoot = change == "boot", IsSystem = change == "system"
        }] };
    }

    private static void AssertInitializationMutationReachedProjection(StorageSnapshot snapshot, string change)
    {
        if (change is not ("role" or "file-system" or "letter")) return;
        var partition = Assert.Single(snapshot.Partitions, item => item.StableId == "partition:native-msr");
        switch (change)
        {
            case "role":
                Assert.Equal("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", partition.GptType.Trim('{', '}'));
                Assert.Equal("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", partition.PartitionTypeId.Trim('{', '}'));
                break;
            case "file-system": Assert.Equal("NTFS", partition.FileSystem); break;
            case "letter": Assert.Equal("E", partition.DriveLetter); break;
        }
    }

    private static RealOperationStepProgress Progress(
        string id, RealOperationStepState state, string? evidence = null) =>
        new(id, state, null, null, evidence);

    [Fact]
    public async Task SingleReturnedPoolCreationRecoversExactEmptyPoolWithoutAnotherCall()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainPoolCreationAsync(fixture);
        fixture.SnapshotTransform = CreatedPoolSnapshot;
        var safetyCalls = fixture.Safety.CallCount;
        Assert.Empty((await fixture.Reader.CaptureAsync(CancellationToken.None)).Snapshot.OsDisks);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        var verified = Assert.Single(result.Steps);
        Assert.Equal(RealOperationStepState.Verified, verified.State);
        Assert.Equal("real.reconciliation_verified_observed_pool_creation", verified.Code);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(verified.ResultEvidence!);
        Assert.Equal("pool:returned", evidence!.CreatedObjectId);
        var current = await fixture.Reader.CaptureAsync(CancellationToken.None);
        Assert.Equal(current.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]).Fingerprint,
            evidence.PostFingerprint);
        Assert.Equal(safetyCalls + 1, fixture.Safety.CallCount);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("missing-target")]
    [InlineData("missing-return")]
    [InlineData("not-issued")]
    [InlineData("provider-failure")]
    [InlineData("provider-error")]
    [InlineData("provider-job")]
    [InlineData("return-uid")]
    [InlineData("return-object-id")]
    [InlineData("return-child")]
    [InlineData("target-id")]
    [InlineData("target-fingerprint")]
    [InlineData("physical-replacement")]
    [InlineData("pool-name")]
    [InlineData("pool-health")]
    [InlineData("pool-state")]
    [InlineData("member")]
    [InlineData("poolable-member")]
    [InlineData("unexpected-disk")]
    [InlineData("unexpected-vd")]
    [InlineData("unexpected-tier")]
    [InlineData("missing-source-fact")]
    [InlineData("safety")]
    [InlineData("safety-read-failed")]
    public async Task PoolCreationRecoveryKeepsBarrierForIncompleteOrMismatchedEvidence(string change)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainPoolCreationAsync(fixture);
        fixture.SnapshotTransform = CreatedPoolSnapshot;
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        switch (change)
        {
            case "hash": plan = plan with { PlanHash = "changed" }; break;
            case "missing-target": progress = progress with { TargetEvidence = null }; break;
            case "missing-return": progress = progress with { ResultEvidence = null }; break;
            case "not-issued": progress = progress with { State = RealOperationStepState.PreparingCall }; break;
            case "provider-failure": provider = provider with { ProviderReturned = false }; break;
            case "provider-error": provider = provider with { ProviderError = "rejected" }; break;
            case "provider-job": provider = provider with { ProviderJobId = 1 }; break;
            case "return-uid": provider = provider with { UniqueId = "pool:unreturned" }; break;
            case "return-object-id": provider = provider with { ObjectId = "different-object" }; break;
            case "return-child": provider = provider with { PartitionNumber = 1 }; break;
            case "target-id": target = target with { ObjectId = "replacement-object" }; break;
            case "target-fingerprint": target = target with { ExpectedFingerprint = "different" }; break;
            case "physical-replacement": fixture.ChangeSerial("REPLACEMENT"); break;
            case "pool-name":
            case "pool-health":
            case "pool-state":
            case "member":
                fixture.SnapshotTransform = snapshot =>
                {
                    var pooled = CreatedPoolSnapshot(snapshot);
                    return pooled with { StoragePools = pooled.StoragePools.Select(pool => pool.IsPrimordial ? pool : change switch
                    {
                        "pool-name" => pool with { FriendlyName = "Other" },
                        "pool-health" => pool with { HealthStatus = "Unhealthy" },
                        "pool-state" => pool with { OperationalStatus = "Degraded" },
                        _ => pool with { MemberPhysicalDiskIds = [] }
                    }).ToArray() };
                };
                break;
            case "poolable-member":
                fixture.SnapshotTransform = snapshot =>
                {
                    var pooled = CreatedPoolSnapshot(snapshot);
                    return pooled with { PhysicalDisks = pooled.PhysicalDisks.Select(disk => disk with { CanPool = true }).ToArray() };
                };
                break;
            case "unexpected-disk": fixture.SnapshotTransform = snapshot => CreatedPoolSnapshot(snapshot) with { OsDisks = snapshot.OsDisks }; break;
            case "unexpected-vd": fixture.SnapshotTransform = snapshot => CreatedPoolSnapshot(snapshot) with
            {
                VirtualDisks = [new VirtualDiskInfo("vd:unexpected", true, "Other", "Healthy", "OK", "Simple", "Fixed",
                    1, 65536, 256L << 20, 256L << 20, "pool:returned", [], [])]
            }; break;
            case "unexpected-tier": fixture.SnapshotTransform = snapshot => CreatedPoolSnapshot(snapshot) with
            {
                StorageTiers = [new StorageTierInfo("tier:unexpected", true, "HDD", "HDD", "Simple", 0, 0,
                    "pool:returned", null, [PhysicalId], NumberOfColumns: 1, Interleave: 65536)]
            }; break;
            case "missing-source-fact": fixture.FactsTransform = item => item.Id != PhysicalId ? item : item with
            {
                Fields = item.Fields.Where(field => field.Name != "HealthStatus").ToImmutableArray()
            }; break;
            case "safety": fixture.Safety.Reject = true; break;
            case "safety-read-failed": fixture.Safety.ProbeFailure = new IOException("Safety read failed"); break;
        }
        if (change.StartsWith("target-", StringComparison.Ordinal)) progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };
        if (change.StartsWith("provider-", StringComparison.Ordinal) || change.StartsWith("return-", StringComparison.Ordinal))
            progress = progress with { ResultEvidence = JsonSerializer.Serialize(provider) };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task PoolCreationRecoveryNeverContinuesAFollowingStep()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainPoolCreationAsync(fixture, twoSteps: true);
        fixture.SnapshotTransform = CreatedPoolSnapshot;
        var result = await fixture.Backend.ReconcileAsync(plan,
            [progress, Progress("create-vdisk", RealOperationStepState.Pending)], CancellationToken.None);
        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("wrong-member")]
    [InlineData("protected")]
    public async Task PoolCreationRecoveryRequiresAndPersistsFreshAggregateForNullPhysicalRoles(string proofState)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareUncertainPoolCreationAsync(fixture);
        fixture.SnapshotTransform = CreatedPoolSnapshot;
        fixture.FactsTransform = WithNullPhysicalRoles;
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var roles = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]).PoolMemberRoleEvidence;
        Assert.NotNull(roles);
        Assert.Empty(roles.AssociatedOsDiskIds);
        var proof = proofState switch
        {
            "missing" => null,
            "stale" => roles with { InventoryVersion = "old-capture" },
            "wrong-member" => roles with { PhysicalStableId = "replacement" },
            "protected" => roles with { IsBoot = true },
            _ => roles
        };
        fixture.Safety.Evidence = new(null, [], PoolMemberRoleEvidence: proof);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(proofState == "valid" ? RealOperationState.Succeeded : RealOperationState.OutcomeUnknown, result.State);
        Assert.Equal(proofState == "valid", result.CanReleaseWriteBarrier);
        if (proofState == "valid")
        {
            var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(Assert.Single(result.Steps).ResultEvidence!);
            Assert.NotNull(evidence!.PoolMemberRoleEvidence);
            Assert.Equal(roles.InventoryVersion, evidence.PoolMemberRoleEvidence.InventoryVersion);
            Assert.Equal("pool:returned", evidence.PoolMemberRoleEvidence.PoolStableId);
            Assert.Empty(evidence.PoolMemberRoleEvidence.AssociatedOsDiskIds);
        }
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static WinPoolSourceObject WithNullPhysicalRoles(WinPoolSourceObject item) => item.Id != PhysicalId + ":disk-roles"
        ? item : item with
        {
            Fields = item.Fields.Select(field => field.Name is "IsBoot" or "IsSystem" or "IsPageFile" or "IsCrashDump"
                ? WinPoolSourceField.Returned<object?>(field.Name, null, FactValueType.Boolean, field.SourceRef) : field).ToImmutableArray()
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalPoolCommandPersistsImmediateAndVerifiedRoleProof(bool unknownProvider)
    {
        var fixture = new Fixture();
        var name = "Exact Pool";
        fixture.SnapshotTransform = snapshot =>
        {
            var pooled = CreatedPoolSnapshot(snapshot);
            return pooled with { StoragePools = pooled.StoragePools.Select(pool => pool.IsPrimordial ? pool
                : pool with { FriendlyName = name }).ToArray() };
        };
        fixture.FactsTransform = WithNullPhysicalRoles;
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var roles = topology.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]).PoolMemberRoleEvidence;
        Assert.NotNull(roles);
        fixture.Safety.Evidence = new(null, [], PoolMemberRoleEvidence: roles);
        var pool = fixture.Id(StorageObjectKind.StoragePool, "pool:returned");
        var step = new RealOperationStep("rename-pool", new RenamePoolCommand(RealTargetReference.ForExisting(pool), "Renamed Pool"),
            [], "Exact existing pool", "Renamed pool", "", "fresh pool identity");
        var plan = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.RenameStorageObject,
            pool.System, [pool], [step], "Renamed pool"), fixture.Session, OperationId.New(), CancellationToken.None);
        step = plan.RealOperation!.Steps[0];
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), CancellationToken.None);
        fixture.Adapter.OnExecute = () => name = "Renamed Pool";
        if (unknownProvider) fixture.Adapter.ResultCode = "provider.error-outcome-unknown";

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        WindowsPoolMemberRoleEvidence? persisted;
        if (unknownProvider)
        {
            Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
            persisted = JsonSerializer.Deserialize<WindowsStorageCommandResult>(result.ResultEvidenceJson)!.PoolMemberRoleEvidence;
        }
        else
        {
            Assert.Equal(RealStepOutcome.Verified, result.Outcome);
            persisted = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!.PoolMemberRoleEvidence;
        }
        Assert.NotNull(persisted);
        Assert.Equal(roles.InventoryVersion, persisted.InventoryVersion);
        Assert.Equal(PhysicalId, persisted.PhysicalStableId);
        Assert.Equal(pool.ProviderKey, persisted.PoolStableId);
        Assert.Empty(persisted.AssociatedOsDiskIds);
        Assert.Equal(1, fixture.Adapter.CallCount);
    }

    private static StorageSnapshot CreatedPoolSnapshot(StorageSnapshot snapshot) => snapshot with
    {
        PhysicalDisks = snapshot.PhysicalDisks.Select(disk => disk.StableId != PhysicalId ? disk : disk with
            { CanPool = false, CannotPoolReason = "In a Pool", PoolStableId = "pool:returned" }).ToArray(),
        // The simulator synthesizes an OS-disk view for primordial free
        // members. The created pool owns this member now, so retaining it in
        // that free-member list would invent a second, unexpected direct disk.
        StoragePools = [.. snapshot.StoragePools.Select(pool => !pool.IsPrimordial ? pool : pool with
            { MemberPhysicalDiskIds = pool.MemberPhysicalDiskIds.Where(id => id != PhysicalId).ToArray() }),
            new StoragePoolInfo("pool:returned", true, "Exact Pool", false,
            "Healthy", "OK", 1_000_000_000, 0, "subsystem:synthetic", [PhysicalId])],
        OsDisks = [], Partitions = [], Volumes = [], VirtualDisks = [], StorageTiers = []
    };

    private static async Task<(OperationPlan Plan, RealOperationStepProgress Progress)>
        PrepareUncertainPoolCreationAsync(Fixture fixture, bool twoSteps = false)
    {
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var create = new RealOperationStep("create-pool", new CreatePoolCommand(RealTargetReference.ForExisting(physical), "Exact Pool"),
            [], "Exact poolable RAW member", "Exact empty pool", "", "fresh poolability evidence");
        RealOperationStep[] steps = twoSteps ? [create, new RealOperationStep("create-vdisk",
            new CreateVirtualDiskCommand(RealTargetReference.FromStep(StorageObjectKind.StoragePool, create.Id), "New VD", 256L << 20, 65536, 1),
            [create.Id], "Created pool", "New VD", "Requested pool capacity allocated", "fresh pool evidence")] : [create];
        var plan = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.CreateStoragePool,
            physical.System, [physical], steps, "Exact empty pool"), fixture.Session, OperationId.New(), CancellationToken.None);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, plan.RealOperation!.Steps[0],
            new Dictionary<string, string>(), CancellationToken.None);
        var returned = new WindowsStorageCommandResult(true, "provider.returned", "pool:returned", "pool:returned",
            "", null, null, null, null);
        return (plan, new(create.Id, RealOperationStepState.OutcomeUnknown, "real.postcondition_unverified",
            preflight.TargetEvidenceJson, JsonSerializer.Serialize(returned)));
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("unused-template", true)]
    [InlineData("pool", false)]
    [InlineData("vd", false)]
    [InlineData("member", false)]
    [InlineData("media", false)]
    [InlineData("layout", false)]
    [InlineData("size", false)]
    [InlineData("footprint", false)]
    [InlineData("retained-association", false)]
    public async Task TierRenamePostconditionPreservesExactAssociationsLayoutAndCapacity(string change, bool verified)
    {
        var fixture = new Fixture();
        static StorageSnapshot TierSnapshot(StorageSnapshot snapshot) => snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { PoolStableId = "pool:tier" }).ToArray(),
            StoragePools = [new StoragePoolInfo("pool:tier", true, "Tier Pool", false, "Healthy", "OK",
                1_000_000_000, 0, "subsystem:synthetic", [PhysicalId])],
            StorageTiers = [new StorageTierInfo("tier:exact", true, "Old name", "HDD", "Simple", 64L << 20, 64L << 20,
                "pool:tier", "vd:original", [PhysicalId], NumberOfColumns: 1, Interleave: 65536)],
            VirtualDisks = [new VirtualDiskInfo("vd:original", true, "VD", "Healthy", "OK", "Simple", "Fixed",
                1, 65536, 64L << 20, 64L << 20, "pool:tier", ["tier:exact"], [])]
        };
        StorageSnapshot CurrentTierSnapshot(StorageSnapshot snapshot)
        {
            var tiered = TierSnapshot(snapshot);
            return change == "unused-template" ? tiered with
            {
                StorageTiers = [tiered.StorageTiers[0] with
                { VirtualDiskStableId = null, MemberPhysicalDiskIds = [], Size = 0, FootprintOnPool = 0 }],
                VirtualDisks = []
            } : tiered;
        }
        fixture.SnapshotTransform = CurrentTierSnapshot;
        var before = await fixture.Reader.CaptureAsync(CancellationToken.None);
        fixture.SnapshotTransform = snapshot =>
        {
            var source = CurrentTierSnapshot(snapshot);
            var renamed = source.StorageTiers[0] with { FriendlyName = "New name" };
            renamed = change switch
            {
                "pool" => renamed with { PoolStableId = null },
                "vd" => renamed with { VirtualDiskStableId = "vd:changed" },
                "member" => renamed with { MemberPhysicalDiskIds = [] },
                "media" => renamed with { MediaType = "SSD" },
                "layout" => renamed with { Interleave = 262144 },
                "size" => renamed with { Size = 128L << 20 },
                "footprint" => renamed with { FootprintOnPool = 128L << 20 },
                _ => renamed
            };
            return source with
            {
                StorageTiers = [renamed],
                VirtualDisks = change == "vd"
                    ? [new VirtualDiskInfo("vd:changed", true, "VD", "Healthy", "OK", "Simple", "Fixed",
                        1, 65536, 64L << 20, 64L << 20, "pool:tier", ["tier:exact"], [])] : source.VirtualDisks
            };
        };
        var after = await fixture.Reader.CaptureAsync(CancellationToken.None);
        if (change == "retained-association")
        {
            var facts = after.Facts with { Relationships = after.Facts.Relationships.Select(item =>
                item.Kind == "tier-member" ? item with { IsRetained = true } : item).ToImmutableArray() };
            after = new WindowsRealStorageTopology(new StorageSystemDocument(
                StorageSystemDocument.CurrentSchemaVersion, "local:synthetic", StorageSystemKind.Local,
                "Synthetic Storage", facts, [], facts.InventoryCapturedAt) { SystemId = facts.SystemId },
                MachineBinding, facts.InventoryCapturedAt);
        }
        var target = new WindowsStorageCommandTarget(StorageObjectKind.StorageTier, "tier:exact", "tier:exact",
            "", "", "", null, null, "", null, null, "pool:tier", PhysicalId, "subsystem:synthetic", "before");
        var command = new RenameTierCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.StorageTier,
            "tier:exact")), "New name");
        var result = WindowsRealStorageBackend.VerifyAfter(command, target,
            new WindowsStorageCommandResult(true, "provider.returned", "tier:exact", "tier:exact", null, null, null, null, null),
            before, after);
        Assert.Equal(verified, result is not null);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("missing-volume", false)]
    [InlineData("new-volume-id", false)]
    [InlineData("new-volume-unique-id", false)]
    [InlineData("filesystem", false)]
    [InlineData("cluster", false)]
    [InlineData("label", false)]
    [InlineData("letter", false)]
    [InlineData("parent", false)]
    [InlineData("unknown-filesystem", false)]
    public async Task RefsResizePostconditionPreservesCurrentVolumeAndContentMetadata(string change, bool verified)
    {
        var fixture = new Fixture();
        static StorageSnapshot ResizeSnapshot(StorageSnapshot snapshot, long size) => snapshot with
        {
            OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
            Partitions = [new PartitionInfo("partition:resize", true, 7, 1, "BasicData", 1048576, size,
                false, false, "W", "REFS", "ReFS", 65536, size, "Healthy", "OK", @"W:\", DiskId,
                PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                Guid: "2f8ae502-1e4e-4d94-b190-6284ccb62bea", GptType: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")],
            Volumes = [new VolumeInfo("volume:resize", true, "partition:resize", "ReFS", "REFS", size, size,
                65536, "Healthy", "OK", [@"W:\"], "volume:unique")]
        };
        fixture.SnapshotTransform = snapshot => ResizeSnapshot(snapshot, 128L << 20);
        var before = await fixture.Reader.CaptureAsync(CancellationToken.None);
        fixture.SnapshotTransform = snapshot =>
        {
            var resized = ResizeSnapshot(snapshot, 192L << 20);
            var volume = resized.Volumes[0];
            volume = change switch
            {
                "new-volume-id" => volume with { StableId = "volume:other" },
                "new-volume-unique-id" => volume with { VolumeIdentity = "volume:replacement-unique" },
                "filesystem" => volume with { FileSystem = "NTFS" },
                "cluster" => volume with { AllocationUnitSize = 4096 },
                "label" => volume with { FileSystemLabel = "Changed" },
                "letter" => volume with { AccessPaths = [@"Q:\"] },
                "parent" => volume with { PartitionStableId = null },
                _ => volume
            };
            return resized with { Volumes = change == "missing-volume" ? [] : [volume] };
        };
        if (change == "unknown-filesystem") fixture.FactsTransform = item => item.ObjectType == FactObjectType.Volume
            ? item with { Fields = item.Fields.Select(field => field.Name == "FileSystem"
                ? field with { ReadState = FieldReadState.Unavailable, Value = null, ReasonCode = "ReadFailed" } : field).ToImmutableArray() }
            : item;
        var after = await fixture.Reader.CaptureAsync(CancellationToken.None);
        if (change == "unknown-filesystem")
        {
            Assert.Equal(FieldReadState.Unavailable,
                after.Facts.Objects.Single(item => item.Id == "volume:resize").Field("FileSystem")!.ReadState);
            // Successful Win32 fallback keeps the display useful, but cannot
            // stand in for the exact MSFT volume's failed verification read.
            Assert.Equal("ReFS", after.Snapshot.Volumes.Single().FileSystem);
        }
        var target = new WindowsStorageCommandTarget(StorageObjectKind.Partition, "partition:resize", "partition:resize",
            "", DiskId, @"\\.\PHYSICALDRIVE7", 7, 1, "2f8ae502-1e4e-4d94-b190-6284ccb62bea", 1048576,
            128L << 20, DiskId, PhysicalId, "subsystem:synthetic", "before");
        var command = new ResizePartitionCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.Partition,
            "partition:resize")), 192L << 20);
        var result = WindowsRealStorageBackend.VerifyAfter(command, target,
            new WindowsStorageCommandResult(true, "provider.returned", "partition:resize", "partition:resize", null, 7, 1, null, null),
            before, after);
        Assert.Equal(verified, result is not null);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task UnformattedResizeStillAllowsSuccessfulEmptyVolumeEnumerations()
    {
        var fixture = new Fixture();
        fixture.SnapshotTransform = snapshot => snapshot with
        {
            OsDisks = [snapshot.OsDisks[0] with { PartitionStyle = "GPT" }],
            Partitions = [new PartitionInfo("partition:raw", true, 7, 1, "BasicData", 1048576, 128L << 20,
                false, false, "", "", "", null, 0, "", "", "", DiskId,
                PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                Guid: "2f8ae502-1e4e-4d94-b190-6284ccb62bea", GptType: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")]
        };
        var before = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var original = fixture.SnapshotTransform!;
        fixture.SnapshotTransform = snapshot => original(snapshot) with
        {
            Partitions = [original(snapshot).Partitions[0] with { Size = 192L << 20 }]
        };
        var after = await fixture.Reader.CaptureAsync(CancellationToken.None);
        Assert.Empty(before.Snapshot.Volumes);
        Assert.Empty(after.Snapshot.Volumes);
        var target = new WindowsStorageCommandTarget(StorageObjectKind.Partition, "partition:raw", "partition:raw",
            "", DiskId, @"\\.\PHYSICALDRIVE7", 7, 1, "2f8ae502-1e4e-4d94-b190-6284ccb62bea", 1048576,
            128L << 20, DiskId, PhysicalId, "subsystem:synthetic", "before");
        Assert.NotNull(WindowsRealStorageBackend.VerifyAfter(new ResizePartitionCommand(
            RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.Partition, "partition:raw")), 192L << 20), target,
            new WindowsStorageCommandResult(true, "provider.returned", "partition:raw", "partition:raw", null, 7, 1, null, null),
            before, after));
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Fact]
    public async Task TieredCreationVerifiesReturnedNullAggregateLayoutAndPersistsExactInstanceFacts()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareReturnedTieredCreationAsync(fixture);
        var step = Assert.Single(plan.RealOperation!.Steps);
        var before = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var closure = before.RequireSinglePhysicalClosure([fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId)]);
        var preflight = new RealStepPreflight(progress.TargetEvidence!, closure.Fingerprint,
            closure.Fingerprint, closure.PhysicalMemberFingerprint);
        fixture.Adapter.ResultTransform = _ => JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        fixture.Adapter.OnExecute = () => fixture.SnapshotTransform = snapshot => TieredCreationSnapshot(snapshot, true);

        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);

        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(1, fixture.Adapter.CallCount);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!;
        Assert.Equal("tier:template", evidence.TieredCreation!.TemplateStableId);
        Assert.Equal("tier:instance", evidence.TieredCreation.TierInstanceStableId);
        Assert.Equal("Fixed", evidence.TieredCreation.ProvisioningType);
        // Nullable<JsonElement> deserializes a JSON null as an absent CLR value.
        // Check the serialized observation itself as well as its Returned state.
        var aggregateProvisioning = Assert.Single(evidence.TieredCreation.VirtualDiskLayoutFacts!,
            item => item.Name == "ProvisioningType");
        Assert.Equal(FieldReadState.Returned, aggregateProvisioning.ReadState);
        Assert.Null(aggregateProvisioning.Value);
        using var persisted = JsonDocument.Parse(result.ResultEvidenceJson!);
        var rawProvisioning = Assert.Single(persisted.RootElement.GetProperty("TieredCreation")
            .GetProperty("VirtualDiskLayoutFacts").EnumerateArray(),
            item => item.GetProperty("Name").GetString() == "ProvisioningType");
        Assert.Equal(JsonValueKind.Null, rawProvisioning.GetProperty("Value").ValueKind);
        Assert.Equal((int)FieldReadState.Returned, rawProvisioning.GetProperty("ReadState").GetInt32());
        Assert.Contains(evidence.TieredCreation.TierInstanceLayoutFacts!, item => item.Name == "NumberOfColumns"
            && item.Value!.Value.GetInt64() == 1);
        var after = await fixture.Reader.CaptureAsync(CancellationToken.None);
        Assert.Equal(string.Empty, Assert.Single(after.Snapshot.VirtualDisks).ProvisioningType);
    }

    [Fact]
    public async Task SingleReturnedTieredCreationRecoversExactInstanceWithoutAnotherWindowsCall()
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareReturnedTieredCreationAsync(fixture);
        fixture.SnapshotTransform = snapshot => TieredCreationSnapshot(snapshot, true);

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.Succeeded, result.State);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(RealOperationStepState.Verified, Assert.Single(result.Steps).State);
        var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.Steps[0].ResultEvidence!)!;
        Assert.Equal("vd:tiered", evidence.CreatedObjectId);
        Assert.Equal("disk:tiered", evidence.CreatedOsDiskId);
        Assert.Equal("tier:instance", evidence.TieredCreation!.TierInstanceStableId);
        Assert.Equal(0, fixture.Adapter.CallCount);
        Assert.Equal(1, fixture.Safety.CallCount);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("not-issued")]
    [InlineData("missing-target")]
    [InlineData("missing-return")]
    [InlineData("provider-failed")]
    [InlineData("provider-error")]
    [InlineData("provider-job")]
    [InlineData("returned-id")]
    [InlineData("target-template")]
    [InlineData("frozen-input")]
    [InlineData("missing-capability")]
    [InlineData("capability-return")]
    [InlineData("capability-range")]
    [InlineData("capability-subsystem")]
    [InlineData("physical-replacement")]
    [InlineData("instance-layout")]
    [InlineData("instance-copies")]
    [InlineData("instance-footprint")]
    [InlineData("vd-allocated")]
    [InlineData("vd-layout-conflict")]
    [InlineData("vd-layout-missing")]
    [InlineData("vd-layout-failed")]
    [InlineData("disk-offline")]
    [InlineData("safety")]
    public async Task TieredCreationRecoveryKeepsBarrierForIncompleteOrContradictoryProof(string change)
    {
        var fixture = new Fixture();
        var (plan, progress) = await PrepareReturnedTieredCreationAsync(fixture);
        fixture.SnapshotTransform = snapshot => TieredCreationSnapshot(snapshot, true);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(progress.TargetEvidence!)!;
        var provider = JsonSerializer.Deserialize<WindowsStorageCommandResult>(progress.ResultEvidence!)!;
        switch (change)
        {
            case "hash": plan = plan with { PlanHash = "changed" }; break;
            case "not-issued": progress = progress with { State = RealOperationStepState.PreparingCall }; break;
            case "missing-target": progress = progress with { TargetEvidence = null }; break;
            case "missing-return": progress = progress with { ResultEvidence = null }; break;
            case "provider-failed": provider = provider with { ProviderReturned = false }; break;
            case "provider-error": provider = provider with { ProviderError = "error" }; break;
            case "provider-job": provider = provider with { ProviderJobId = 1 }; break;
            case "returned-id": provider = provider with { ObjectId = "unreturned-object" }; break;
            case "target-template": target = target with { RelatedObjectId = "another-template" }; break;
            case "frozen-input": provider = provider with { TieredCreationInput = provider.TieredCreationInput! with { Interleave = 32768 } }; break;
            case "missing-capability": provider = provider with { LiveCapabilityEvidence = null }; break;
            case "capability-return": provider = provider with { LiveCapabilityEvidence = TieredLiveCapability(target, returnValue: 1) }; break;
            case "capability-range": provider = provider with { LiveCapabilityEvidence = TieredLiveCapability(target, maximum: 1L << 30) }; break;
            case "capability-subsystem": provider = provider with { LiveCapabilityEvidence = TieredLiveCapability(target with { StorageSubsystemObjectId = "replacement" }) }; break;
            case "physical-replacement": fixture.ChangeSerial("replacement"); break;
            case "safety": fixture.Safety.Reject = true; break;
            default:
                fixture.FactsTransform = item =>
                {
                    item = TieredCreationFacts(item);
                    if (item.Id == "vd:tiered" && change == "vd-layout-missing")
                        return item with { Fields = item.Fields.Where(field => field.Name != "Interleave").ToImmutableArray() };
                    return item with { Fields = item.Fields.Select(field => (item.Id, field.Name, change) switch
                    {
                        ("tier:instance", "NumberOfColumns", "instance-layout") => WinPoolSourceField.Returned(field.Name, 2, FactValueType.UInt64, item.SourceRef),
                        ("tier:instance", "NumberOfDataCopies", "instance-copies") => WinPoolSourceField.Returned(field.Name, 2, FactValueType.UInt64, item.SourceRef),
                        ("tier:instance", "FootprintOnPool", "instance-footprint") => WinPoolSourceField.Returned(field.Name, 1L << 30, FactValueType.UInt64, item.SourceRef),
                        ("vd:tiered", "AllocatedSize", "vd-allocated") => WinPoolSourceField.Returned(field.Name, 1L << 30, FactValueType.UInt64, item.SourceRef),
                        ("vd:tiered", "Interleave", "vd-layout-conflict") => WinPoolSourceField.Returned(field.Name, 32768, FactValueType.UInt64, item.SourceRef),
                        ("vd:tiered", "Interleave", "vd-layout-failed") => field with { ReadState = FieldReadState.Failed, ReasonCode = "TestReadFailed" },
                        ("disk:tiered", "IsOffline", "disk-offline") => WinPoolSourceField.Returned(field.Name, true, FactValueType.Boolean, item.SourceRef),
                        _ => field
                    }).ToImmutableArray() };
                };
                break;
        }
        if (change != "missing-target") progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };
        if (change != "missing-return") progress = progress with { ResultEvidence = JsonSerializer.Serialize(provider) };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], CancellationToken.None);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static StorageSnapshot TieredCreationSnapshot(StorageSnapshot snapshot, bool created)
    {
        const long bytes = 16L << 30;
        var pooled = CreatedPoolSnapshot(snapshot);
        var template = new StorageTierInfo("tier:template", true, "Template", "HDD", "Simple", 0, 0,
            "pool:returned", null, [PhysicalId], 1, 65536, 1, 0);
        return pooled with
        {
            StorageTiers = created ? [template, template with { StableId = "tier:instance", FriendlyName = "Provider clone",
                Size = bytes, FootprintOnPool = bytes, VirtualDiskStableId = "vd:tiered" }] : [template],
            VirtualDisks = created ? [new VirtualDiskInfo("vd:tiered", true, "Tiered VD", "Healthy", "OK", "Simple", "Fixed",
                1, 65536, bytes, bytes, "pool:returned", ["tier:instance"], [9], NumberOfDataCopies: 1,
                PhysicalDiskRedundancy: 0, AllocatedSize: bytes)] : [],
            OsDisks = created ? [new OsDiskInfo("disk:tiered", "Tiered VD", 9, "RAW", bytes, false, false, false, null, "vd:tiered")] : []
        };
    }

    private static WinPoolSourceObject TieredCreationFacts(WinPoolSourceObject item)
    {
        if (item.ObjectType == FactObjectType.StorageTier)
            item = item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("AllocatedSize",
                item.Field("Size")!.Value!.Value.GetInt64(), FactValueType.UInt64, item.SourceRef))
                .Add(WinPoolSourceField.Returned("ProvisioningType", 2, FactValueType.UInt64, item.SourceRef)) };
        if (item.ObjectType == FactObjectType.Disk)
            item = item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("IsClustered", false, FactValueType.Boolean, item.SourceRef)) };
        return item.Id != "vd:tiered" ? item : item with { Fields = item.Fields.Select(field =>
            field.Name is "ResiliencySettingName" or "ProvisioningType" or "NumberOfColumns" or "Interleave"
                or "NumberOfDataCopies" or "PhysicalDiskRedundancy"
                ? field with { Value = JsonSerializer.SerializeToElement<object?>(null) } : field).ToImmutableArray() };
    }

    private static JsonElement TieredLiveCapability(WindowsStorageCommandTarget target, uint returnValue = 0,
        long maximum = 3999956729856) => JsonSerializer.SerializeToElement(new
    {
        SubsystemUniqueId = target.StorageSubsystemUniqueId, SubsystemObjectId = target.StorageSubsystemObjectId,
        RequiredField = "SupportsStorageTieredVirtualDiskCreation", RequiredValue = true, PhysicalDisksPerStoragePoolMin = 1,
        PhysicalMemberUniqueId = target.PhysicalMemberUniqueId, PhysicalMemberObjectId = target.PhysicalMemberObjectId,
        CreationSize = new { ReturnValue = returnValue, SupportedSizes = Array.Empty<ulong>(),
            TierSizeMin = 268435456UL, TierSizeMax = (ulong)maximum, TierSizeDivisor = 268435456UL }
    });

    private static async Task<(OperationPlan Plan, RealOperationStepProgress Progress)> PrepareReturnedTieredCreationAsync(Fixture fixture)
    {
        fixture.SnapshotTransform = snapshot => TieredCreationSnapshot(snapshot, false);
        fixture.FactsTransform = TieredCreationFacts;
        var topology = await fixture.Reader.CaptureAsync(CancellationToken.None);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var pool = fixture.Id(StorageObjectKind.StoragePool, "pool:returned");
        var tier = fixture.Id(StorageObjectKind.StorageTier, "tier:template");
        var closure = topology.RequireSinglePhysicalClosure([physical]);
        var target = WindowsRealStorageTargetBuilder.Build(topology, RealTargetReference.ForExisting(pool), new Dictionary<string, string>())
            with { RelatedUniqueId = tier.ProviderKey, RelatedObjectId = tier.ProviderKey };
        var capability = new WindowsTierCapability("queried", PhysicalId, "subsystem:synthetic",
            target.StorageSubsystemUniqueId, target.StorageSubsystemObjectId, topology.Facts.InventoryCapturedAt,
            [], [WinPoolSourceField.Returned("SupportsStorageTieredVirtualDiskCreation", true, FactValueType.Boolean, "subsystem:synthetic"),
                WinPoolSourceField.Returned("PhysicalDisksPerStoragePoolMin", 1, FactValueType.UInt64, "subsystem:synthetic")], null);
        var range = new VirtualDiskCreationSize(268435456, 3999956729856, 268435456, []) { RangeOriginBytes = 268435456 };
        var step = new RealOperationStep("create-tiered", new CreateTieredVirtualDiskCommand(RealTargetReference.ForExisting(pool),
            RealTargetReference.ForExisting(tier), "Tiered VD", 16L << 30), [], "Exact unused template", "Exact allocated instance",
            "16GiB allocated", "live-tier-capability:" + JsonSerializer.Serialize(capability) + "; exact-template-new-size:" + JsonSerializer.Serialize(range));
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, topology.Facts.InventoryCapturedAt);
        var plan = RealOperationPlanFactory.Create(new RealOperationIntentRequest(OperationIntent.CreateVirtualDisk, pool.System,
            [pool, tier, physical], [step], "Single HDD tiered VD"), OperationId.New(), environment, fixture.Session,
            closure.Fingerprint, closure.Fingerprint, closure.PhysicalMemberFingerprint, "Exact frozen tier support",
            topology.Facts.InventoryCapturedAt, topology.Facts.InventoryCapturedAt.AddMinutes(5));
        var provider = new WindowsStorageCommandResult(true, "provider.returned", "vd:tiered", "vd:tiered", "", null, null, null, null,
            new(tier.ProviderKey, tier.ProviderKey, pool.ProviderKey, PhysicalId, "HDD", "Simple", "Fixed", 1, 65536, 16L << 30),
            TieredLiveCapability(target));
        return (plan, new(step.Id, RealOperationStepState.OutcomeUnknown, "real.postcondition_unverified",
            JsonSerializer.Serialize(target), JsonSerializer.Serialize(provider)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TierRenamePreflightFreezesOnlyTheExactAllocatedInstanceVirtualDiskIdentity(bool instance)
    {
        var fixture = new Fixture(tierCapabilities: new RenameTierCapabilities());
        fixture.SnapshotTransform = snapshot => TieredCreationSnapshot(snapshot, instance);
        fixture.FactsTransform = TieredCreationFacts;
        var tier = fixture.Id(StorageObjectKind.StorageTier, instance ? "tier:instance" : "tier:template");
        var step = new RealOperationStep("rename-tier", new RenameTierCommand(RealTargetReference.ForExisting(tier), "Renamed tier"),
            [], "Exact existing tier", "Same tier renamed", "Name changes", "Fresh exact tier capability");
        var plan = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.RenameStorageObject,
            tier.System, [tier], [step], "Exact tier name"), fixture.Session, OperationId.New(), CancellationToken.None);

        var preflight = await fixture.Backend.PreflightStepAsync(plan, Assert.Single(plan.RealOperation!.Steps),
            new Dictionary<string, string>(), CancellationToken.None);

        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        Assert.Equal(tier.ProviderKey, target.UniqueId);
        Assert.Equal("pool:returned", target.ParentUniqueId);
        Assert.Equal(instance ? "vd:tiered" : string.Empty, target.RelatedUniqueId);
        Assert.Equal(instance ? "vd:tiered" : string.Empty, target.RelatedObjectId);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private sealed class RenameTierCapabilities : IWindowsRealStorageCapabilityReader
    {
        public Task<WindowsTierCapability> ReadTierAsync(WindowsRealStorageTopology topology,
            StorageObjectId physicalDisk, CancellationToken cancellationToken) => Task.FromResult(new WindowsTierCapability(
                "queried", physicalDisk.ProviderKey, "subsystem:synthetic", "subsystem:synthetic", "subsystem:synthetic",
                topology.Facts.InventoryCapturedAt, [],
                [WinPoolSourceField.Returned("SupportsStorageTierFriendlyNameModification", true, FactValueType.Boolean, "subsystem:synthetic"),
                    WinPoolSourceField.Returned("PhysicalDisksPerStoragePoolMin", 1, FactValueType.UInt64, "subsystem:synthetic")], null));
        public Task<WindowsVolumeFormatCapability> ReadVolumeFormatAsync(WindowsRealStorageTopology topology,
            StorageObjectId volume, CancellationToken cancellationToken) => throw new InvalidOperationException("No real volume API in this rename fixture.");
        public Task<VirtualDiskCreationSize> ReadTierCreationSizeAsync(WindowsRealStorageTopology topology,
            StorageObjectId tier, CancellationToken cancellationToken) => throw new InvalidOperationException("No real creation-size API in this rename fixture.");
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
        private readonly SystemId systemId = SystemId.New();
        private readonly bool secondDisk;
        private readonly bool sharedOrdinaryPool;
        private string serial = "SERIAL-7";
        private bool offline;
        private bool hasVolume;

        public Fixture(bool secondDisk = false, bool sharedOrdinaryPool = false, bool expirePostCallWindow = false,
            IWindowsRealStorageCapabilityReader? tierCapabilities = null)
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
                new FixedTimeProvider(Now), Safety, capabilities: tierCapabilities);
            Backend = new WindowsRealStorageBackend(Adapter, planner, Reader,
                expirePostCallWindow ? new ExpiringPostCallTimeProvider(Now) : new FixedTimeProvider(Now));
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
        public bool IsOffline => offline;
        public void SetOffline(bool value) => offline = value;
        public void ChangeSerial(string value) => serial = value;

        public async Task<OperationPlan> PrepareInitializeAsync(bool twoSteps = false)
        {
            var disk = Id(StorageObjectKind.OsDisk, DiskId);
            var initialize = new RealOperationStep("initialize",
                new InitializeGptCommand(RealTargetReference.ForExisting(disk)), [],
                "RAW zero partitions", "GPT initialized", "", "synthetic source facts");
            var steps = twoSteps ? new[] { initialize, new RealOperationStep("create-msr",
                new CreatePartitionCommand(RealTargetReference.ForExisting(disk), RealPartitionRole.Msr,
                    1048576, 16777216), [initialize.Id], "GPT free gap", "MSR exists", "", "synthetic source facts") }
                : [initialize];
            var proposal = new RealOperationIntentRequest(OperationIntent.InitializeDisk,
                systemId, [disk], steps, "GPT initialized");
            if (!twoSteps)
                return await Backend.PrepareAsync(proposal, Session, OperationId.New(), CancellationToken.None);

            // Historical frozen records remain readable for narrow recovery;
            // new preparation rejects initialization plus MSR in one proposal.
            var topology = await Reader.CaptureAsync(CancellationToken.None);
            var closure = topology.RequireSinglePhysicalClosure([disk]);
            var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine,
                MachineBinding, ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, Now);
            return RealOperationPlanFactory.Create(proposal with
                { Targets = [disk, Id(StorageObjectKind.PhysicalDisk, PhysicalId)] },
                OperationId.New(), environment, Session, closure.Fingerprint, closure.Fingerprint,
                closure.PhysicalMemberFingerprint, "synthetic historical source facts", Now, Now.AddMinutes(5));
        }

        public async Task<OperationPlan> PrepareInitializationContinuationAsync(bool nativeMsr)
        {
            var disk = Id(StorageObjectKind.OsDisk, DiskId);
            var partition = Id(StorageObjectKind.Partition, "partition:native-msr");
            var steps = new List<RealOperationStep>();
            if (nativeMsr) steps.Add(new RealOperationStep("remove-native-msr",
                new DeletePartitionCommand(RealTargetReference.ForExisting(partition)), [],
                "Exact sole unformatted provider MSR", "Provider MSR absent", "Provider MSR metadata removed", "synthetic source facts"));
            steps.Add(new RealOperationStep("create-msr",
                new CreatePartitionCommand(RealTargetReference.ForExisting(disk), RealPartitionRole.Msr, 1048576, 16777216),
                nativeMsr ? ["remove-native-msr"] : [], "GPT usable gap", "16 MiB MSR at 1 MiB", "", "synthetic source facts"));
            return await Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.InitializeDisk,
                systemId, nativeMsr ? [disk, partition] : [disk], steps, "Canonical MSR layout"),
                Session, OperationId.New(), CancellationToken.None);
        }

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
            facts = facts with { Relationships = facts.Relationships.AddRange(snapshot.StorageTiers
                .Where(tier => tier.VirtualDiskStableId is null).SelectMany(tier => tier.MemberPhysicalDiskIds.Select(member =>
                    new WinPoolFactRelationship(tier.StableId, member, "template-pool-member", Now)))) };
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            // A successful empty enumeration still needs a source receipt.
            // In particular a pooled member has no MSFT_Disk object, which
            // must remain distinguishable from an absent or failed query.
            foreach (var className in new[] { "MSFT_StorageSubSystem", "MSFT_PhysicalDisk", "MSFT_StoragePool",
                         "MSFT_Disk", "MSFT_Partition", "MSFT_Volume", "MSFT_VirtualDisk", "MSFT_StorageTier" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage, ReadState: FieldReadState.Returned));
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
        public Func<WindowsStorageCommandResult, WindowsStorageCommandResult>? ResultTransform { get; set; }
        public int CallCount { get; private set; }
        public Task<WindowsStorageCommandResult> ExecuteAsync(
            RealStorageCommand command, WindowsStorageCommandTarget target,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OnExecute?.Invoke();
            var result = new WindowsStorageCommandResult(true, ResultCode,
                target.UniqueId, target.ObjectId, null, target.DiskNumber, null, null, null);
            return Task.FromResult(ResultTransform is null ? result : ResultTransform(result));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ExpiringPostCallTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow() => now.AddSeconds(61 * reads++);
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
        public int ObservedCallCount { get; private set; }
        public int CreatedFormatCallCount { get; private set; }
        public bool Reject { get; set; }
        public Exception? ProbeFailure { get; set; }
        public WindowsRealStorageSafetyEvidence? Evidence { get; set; }
        public WindowsRealStorageSafetyEvidence? ObservedEvidence { get; set; }
        public Exception? ObservedFailure { get; set; }
        public WindowsRealStorageSafetyEvidence? CreatedFormatEvidence { get; set; }
        public Exception? CreatedFormatFailure { get; set; }
        public StorageObjectId? LastCreatedFormatTarget { get; private set; }
        public Task ValidateAsync(WindowsRealStorageTopology topology,
            RealTargetClosure closure, RealStorageCommand command,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (ProbeFailure is not null) throw ProbeFailure;
            if (Reject) throw new InvalidDataException("Synthetic safety facts are unsafe.");
            return Task.CompletedTask;
        }

        public async Task<WindowsRealStorageSafetyEvidence?> ValidateWithEvidenceAsync(
            WindowsRealStorageTopology topology, RealTargetClosure closure,
            RealStorageCommand command, CancellationToken cancellationToken)
        {
            await ValidateAsync(topology, closure, command, cancellationToken);
            return Evidence;
        }

        public Task<WindowsRealStorageSafetyEvidence?> ValidateObservedDiskStateWithEvidenceAsync(
            WindowsRealStorageTopology topology, RealTargetClosure closure,
            SetDiskOnlineCommand command, CancellationToken cancellationToken)
        {
            ObservedCallCount++;
            if (ObservedFailure is not null) throw ObservedFailure;
            return ObservedEvidence is not null ? Task.FromResult<WindowsRealStorageSafetyEvidence?>(ObservedEvidence)
                : ValidateWithEvidenceAsync(topology, closure, command, cancellationToken);
        }

        public Task<WindowsRealStorageSafetyEvidence?> ValidateCreatedPartitionFormatWithEvidenceAsync(
            WindowsRealStorageTopology topology, RealTargetClosure closure,
            FormatVolumeCommand command, StorageObjectId exactCreatedPartition,
            CancellationToken cancellationToken)
        {
            CreatedFormatCallCount++;
            LastCreatedFormatTarget = exactCreatedPartition;
            if (CreatedFormatFailure is not null) throw CreatedFormatFailure;
            return Task.FromResult(CreatedFormatEvidence);
        }
    }

    private sealed class AdministratorPrivilege : IPrivilegeService
    {
        public PrivilegeState Current => PrivilegeState.Administrator;
    }
}
