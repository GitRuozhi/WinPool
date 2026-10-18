using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactAgentPreflightNoCallRecoveryRecapturesTheOsProfileBeforeReleasingFailure(bool boundReceipt)
    {
        var fixture = new Fixture(scopedCaptures: true);
        var plan = await PrepareExactInitializeNoCallAsync(fixture);
        var frozen = plan.RealOperation!;
        var step = Assert.Single(frozen.Steps);
        var progress = ExactAgentPreflightFailure(plan, step, boundReceipt);
        var captures = fixture.Source.FullCalls;

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

        Assert.Equal(WindowsRealStorageReadProfile.Os, frozen.EvidenceProfile);
        Assert.StartsWith("Windows exact selected physical-member set;", frozen.SupportEvidence);
        Assert.Equal(captures + 1, fixture.Source.FullCalls);
        Assert.True(result.State == RealOperationState.Failed, result.Code);
        Assert.True(result.CanReleaseWriteBarrier);
        var failed = Assert.Single(result.Steps);
        Assert.Equal(RealOperationStepState.Failed, failed.State);
        Assert.Null(failed.TargetEvidence);
        Assert.Equal(progress.ResultEvidence, failed.ResultEvidence);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("call-not-excluded")]
    [InlineData("wrong-physical-fingerprint")]
    [InlineData("wrong-progress-code")]
    [InlineData("wrong-receipt-code")]
    [InlineData("adapter-without-target")]
    [InlineData("missing-receipt")]
    [InlineData("malformed-receipt")]
    [InlineData("null-receipt")]
    [InlineData("empty-receipt")]
    [InlineData("legacy-text-receipt")]
    [InlineData("foreign-plan")]
    [InlineData("foreign-step")]
    [InlineData("plan-binding-only")]
    [InlineData("step-binding-only")]
    [InlineData("target-present")]
    [InlineData("host-call-present")]
    [InlineData("tampered-plan-hash")]
    public async Task ExactAgentPreflightNoCallRecoveryRejectsIncompleteOrConflictingReceipts(string scenario)
    {
        var fixture = new Fixture(scopedCaptures: true);
        var plan = await PrepareExactInitializeNoCallAsync(fixture);
        var frozen = plan.RealOperation!;
        var step = Assert.Single(frozen.Steps);
        var progress = ExactAgentPreflightFailure(plan, step, boundReceipt: true);
        var receipt = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(progress.ResultEvidence!)!;
        receipt = scenario switch
        {
            "call-not-excluded" => receipt with { NoWindowsCall = false },
            "wrong-physical-fingerprint" => receipt with { PhysicalMemberFingerprint = "another-physical-member" },
            "wrong-receipt-code" => receipt with { Code = "operation.another_failure" },
            "adapter-without-target" => receipt with { Code = "adapter.preflight-rejected" },
            "foreign-plan" => receipt with { PlanHash = "another-plan" },
            "foreign-step" => receipt with { StepId = "another-step" },
            "plan-binding-only" => receipt with { StepId = null },
            "step-binding-only" => receipt with { PlanHash = null },
            _ => receipt
        };
        progress = progress with
        {
            Code = scenario == "wrong-progress-code" ? "operation.another_failure"
                : scenario == "adapter-without-target" ? receipt.Code : progress.Code,
            ResultEvidence = scenario switch
            {
                "missing-receipt" => null,
                "malformed-receipt" => "{",
                "null-receipt" => "null",
                "empty-receipt" => "{}",
                "legacy-text-receipt" => "preflight_failed",
                _ => JsonSerializer.Serialize(receipt)
            }
        };
        if (scenario is "target-present" or "host-call-present")
        {
            var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
            if (scenario == "host-call-present") target = target with
                { HostCall = new(Guid.NewGuid(), 1234, plan.CreatedAt, Guid.NewGuid()) };
            progress = progress with { TargetEvidence = JsonSerializer.Serialize(target) };
        }
        if (scenario == "tampered-plan-hash") plan = plan with { PlanHash = "tampered-plan-hash" };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(RealOperationStepState.Pending)]
    [InlineData(RealOperationStepState.PreparingCall)]
    [InlineData(RealOperationStepState.CallIssued)]
    [InlineData(RealOperationStepState.WaitingForProvider)]
    [InlineData(RealOperationStepState.Verifying)]
    [InlineData(RealOperationStepState.OutcomeUnknown)]
    [InlineData(RealOperationStepState.StoppedBeforeCall)]
    public async Task ExactAgentPreflightNoCallRecoveryRequiresTheDurableFailedState(RealOperationStepState state)
    {
        var fixture = new Fixture();
        var plan = await PrepareExactInitializeNoCallAsync(fixture);
        var progress = ExactAgentPreflightFailure(plan, Assert.Single(plan.RealOperation!.Steps), boundReceipt: false)
            with { State = state };

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("physical-role")]
    [InlineData("os-role")]
    [InlineData("topology")]
    [InlineData("capture-failure")]
    [InlineData("incomplete-capture")]
    public async Task ExactAgentPreflightNoCallRecoveryRequiresFreshUnchangedIdentityTopologyAndRoles(string scenario)
    {
        var fixture = new Fixture(scopedCaptures: true);
        var plan = await PrepareExactInitializeNoCallAsync(fixture);
        var step = Assert.Single(plan.RealOperation!.Steps);
        var progress = ExactAgentPreflightFailure(plan, step, boundReceipt: false);
        // Establish the real operation's scoped selector, then alter only the
        // subsequent read. Reconciliation must never reuse this earlier proof.
        await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        var captures = fixture.Source.Scopes.Count;
        WinPoolFacts? observedBootFacts = null;
        switch (scenario)
        {
            case "serial": fixture.ChangeSerial("replacement-serial"); break;
            case "physical-role":
                // Physical boot roles are derived from the associated MSFT_Disk.
                fixture.SnapshotTransform = snapshot => snapshot with
                {
                    PhysicalDisks = snapshot.PhysicalDisks.Select(disk => disk with { IsBoot = true }).ToArray(),
                    OsDisks = snapshot.OsDisks.Select(disk => disk with { IsBoot = true }).ToArray()
                };
                fixture.CompleteFactsTransform = facts => { observedBootFacts = facts; return facts; };
                break;
            case "os-role": fixture.SnapshotTransform = snapshot => snapshot with
                { OsDisks = snapshot.OsDisks.Select(disk => disk with { IsSystem = true }).ToArray() }; break;
            case "topology": fixture.SnapshotTransform = snapshot => InitializedSnapshot(snapshot, nativeMsr: false); break;
            case "capture-failure": fixture.Source.ScopedFailure = new TimeoutException("fresh OS collection exceeded its deadline"); break;
            case "incomplete-capture": fixture.Source.CompleteScopedCapture = false; break;
        }

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

        if (scenario == "physical-role") Assert.True(WinPoolStorageProjection.Project(Assert.IsType<WinPoolFacts>(observedBootFacts))
            .PhysicalDisks.Single(disk => disk.StableId == PhysicalId).IsBoot);
        Assert.Equal(captures + 1, fixture.Source.Scopes.Count);
        Assert.Equal(WindowsRealStorageReadProfile.Os, fixture.Source.Scopes[^1].EvidenceProfile);
        Assert.Equal(RealOperationState.OutcomeUnknown, result.State);
        Assert.False(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactAdapterNoCallReceiptStillRequiresAndAcceptsItsMatchingTarget(bool hostMetadata)
    {
        var fixture = new Fixture(scopedCaptures: true);
        var plan = await PrepareExactInitializeNoCallAsync(fixture);
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        if (hostMetadata) target = target with { HostCall = new(Guid.NewGuid(), 1234, plan.CreatedAt, Guid.NewGuid()) };
        var receipt = new WindowsNoEffectStepEvidence(true, "adapter.preflight-rejected", plan.RealOperation!.PhysicalMemberFingerprint)
            { PlanHash = plan.PlanHash, StepId = step.Id };
        var progress = new RealOperationStepProgress(step.Id, RealOperationStepState.Failed, receipt.Code,
            JsonSerializer.Serialize(target), JsonSerializer.Serialize(receipt));

        var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

        Assert.True(result.State == RealOperationState.Failed, result.Code);
        Assert.True(result.CanReleaseWriteBarrier);
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong-prefix-member", false)]
    [InlineData("wrong-prefix-target", false)]
    [InlineData("wrong-prefix-post-state", false)]
    [InlineData("verified-after-failure", false)]
    [InlineData("issued-after-failure", false)]
    public async Task ExactAgentPreflightNoCallRecoveryPreservesOnlyAValidatedVerifiedPrefix(string scenario, bool releases)
    {
        var fixture = new Fixture(scopedCaptures: true);
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var first = new RealOperationStep("offline", new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), false),
            [], "Online disk", "Offline disk", "", "Synthetic current facts");
        var prepared = await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.SetDiskOnlineState,
            disk.System, [physical, disk], [first], "Offline disk"), fixture.Session, OperationId.New(), default);
        var original = prepared.RealOperation!;
        first = Assert.Single(original.Steps);
        var second = new RealOperationStep("online", new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), true),
            [first.Id], "Offline disk", "Online disk", "", "Synthetic current facts");
        var third = new RealOperationStep("offline-again", new SetDiskOnlineCommand(RealTargetReference.ForExisting(disk), false),
            [second.Id], "Online disk", "Offline disk", "", "Synthetic current facts");
        // A later online step cannot be prepared against today's online facts.
        // Freeze the valid historical sequence to exercise read-only recovery.
        var environment = new EnvironmentProfile(prepared.EnvironmentId, EnvironmentKind.LocalMachine, MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, prepared.CreatedAt);
        var plan = RealOperationPlanFactory.Create(new RealOperationIntentRequest(OperationIntent.SetDiskOnlineState,
            disk.System, prepared.Targets, [first, second, third], "Offline disk"), OperationId.New(), environment,
            fixture.Session, prepared.InventoryVersion, original.TargetFingerprint,
            original.PhysicalMemberFingerprint, original.SupportEvidence,
            prepared.CreatedAt, original.ExpiresAt, original.EvidenceProfile);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, first, new Dictionary<string, string>(), default);
        fixture.SetOffline(true);
        var current = await fixture.Reader.CaptureAsync(default, plan.RealOperation!.EvidenceProfile);
        var closure = current.RequireSelectedPhysicalMemberSet([physical], requirePoolable: false);
        var verified = new WindowsVerifiedStepEvidence(closure.Fingerprint, closure.PhysicalMemberFingerprint, null, "provider.returned");
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
        if (scenario == "wrong-prefix-member") verified = verified with { PhysicalMemberFingerprint = "another-member" };
        if (scenario == "wrong-prefix-target") target = target with { ExpectedFingerprint = "another-before-state" };
        if (scenario == "wrong-prefix-post-state") verified = verified with { PostFingerprint = "another-post-state" };
        RealOperationStepProgress[] progress =
        [
            new(first.Id, RealOperationStepState.Verified, "real.step_verified", JsonSerializer.Serialize(target), JsonSerializer.Serialize(verified)),
            ExactAgentPreflightFailure(plan, second, boundReceipt: false),
            Progress(third.Id, scenario == "verified-after-failure" ? RealOperationStepState.Verified
                : scenario == "issued-after-failure" ? RealOperationStepState.CallIssued : RealOperationStepState.Pending)
        ];

        var result = await fixture.Backend.ReconcileAsync(plan, progress, default);

        Assert.True(releases == result.CanReleaseWriteBarrier, result.Code);
        Assert.Equal(releases ? RealOperationState.PartiallyCompleted : RealOperationState.OutcomeUnknown, result.State);
        if (releases)
        {
            Assert.Equal(progress[0], result.Steps[0]);
            Assert.Equal(RealOperationStepState.Failed, result.Steps[1].State);
            Assert.Null(result.Steps[1].TargetEvidence);
            Assert.Equal(RealOperationStepState.StoppedBeforeCall, result.Steps[2].State);
        }
        Assert.Equal(0, fixture.Adapter.CallCount);
    }

    private static async Task<OperationPlan> PrepareExactInitializeNoCallAsync(Fixture fixture)
    {
        var physical = fixture.Id(StorageObjectKind.PhysicalDisk, PhysicalId);
        var disk = fixture.Id(StorageObjectKind.OsDisk, DiskId);
        var step = new RealOperationStep("initialize-gpt", new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
            [], "RAW disk without partitions", "GPT disk", "", "Synthetic current facts");
        return await fixture.Backend.PrepareAsync(new RealOperationIntentRequest(OperationIntent.InitializeDisk,
            disk.System, [physical, disk], [step], "GPT disk"), fixture.Session, OperationId.New(), default);
    }

    private static RealOperationStepProgress ExactAgentPreflightFailure(OperationPlan plan, RealOperationStep step, bool boundReceipt)
    {
        var receipt = new WindowsNoEffectStepEvidence(true, "operation.preflight_failed", plan.RealOperation!.PhysicalMemberFingerprint,
            "Fresh capture failed before a Windows command target existed")
            { PlanHash = boundReceipt ? plan.PlanHash : null, StepId = boundReceipt ? step.Id : null };
        return new(step.Id, RealOperationStepState.Failed, receipt.Code, null, JsonSerializer.Serialize(receipt));
    }
}
