using System.Text.Json;
using WinPool.Application;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    // Exercise the actual Backend -> reservation -> production adapter -> framed
    // host path. The fixed host fixture emits JSON only and runs no storage cmdlet.
    [Collection("PowerShellHost")]
    public sealed class HostTargetTests
    {
        private static readonly PowerShellHostTemplates Templates = new(
            "param($p); '{}'",
            """
param($p)
@{ ProviderReturned=$true; Code='provider.returned'; UniqueId=[string]$p.Target.UniqueId;
   ObjectId=[string]$p.Target.ObjectId; PartitionGuid=[string]$p.Target.PartitionGuid } | ConvertTo-Json -Compress
""", "param($kind,$p); throw 'no-fixture-query-required'", false);

        [Fact]
        public async Task CreatedEfiFormatAcceptsItsRealReservedHostCallAndPreservesNativeSafety()
        {
            await using var host = new WindowsPowerShellHost(Templates);
            var adapter = new HostFixtureAdapter(host);
            var fixture = new Fixture(commandAdapter: adapter);
            var (plan, step, preflight) = await PrepareCreatedEfiFormatAsync(fixture);
            preflight = await fixture.Backend.ReserveStepCallAsync(plan, step, preflight, default);
            await using var reservation = preflight.CallContext!;
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
            Assert.NotNull(target.HostCall);
            adapter.BeforeCall = () => fixture.SnapshotTransform = snapshot => EfiSnapshot(snapshot, formatted: true);

            var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, default);

            Assert.Equal(RealStepOutcome.Verified, result.Outcome);
            Assert.Equal(1, adapter.Calls);
            Assert.Equal(target.HostCall, adapter.LastResult!.CompletedHostCall);
            Assert.Equal(CreatedEfiNativeProof(), JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(result.ResultEvidenceJson)!.NativeMsrSafetyEvidence);
            Assert.Equal(target, adapter.LastTarget);
        }

        [Theory]
        [InlineData("valid")]
        [InlineData("serial")]
        [InlineData("partition-guid")]
        [InlineData("geometry")]
        [InlineData("layout")]
        [InlineData("physical-members")]
        [InlineData("tier-inputs")]
        public async Task ReadOnlyRecoveryAcceptsHostMetadataButRejectsChangedStorageIdentityOrParameters(string change)
        {
            await using var host = new WindowsPowerShellHost(Templates);
            var adapter = new HostFixtureAdapter(host);
            var fixture = new Fixture(commandAdapter: adapter, backendClock: new ProbeClock());
            var plan = await fixture.PrepareRenameAsync();
            var step = Assert.Single(plan.RealOperation!.Steps);
            var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
            preflight = await fixture.Backend.ReserveStepCallAsync(plan, step, preflight, default);
            await using var reservation = preflight.CallContext!;
            var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(preflight.TargetEvidenceJson)!;
            Assert.NotNull(target.HostCall);
            // A returned call whose new label is not yet observable remains unknown.
            var issued = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, default);
            Assert.Equal(RealStepOutcome.OutcomeUnknown, issued.Outcome);
            Assert.Equal(target.HostCall, adapter.LastResult!.CompletedHostCall);
            Assert.Equal(1, adapter.Calls);
            fixture.VolumeLabel = "NEW";
            target = change switch
            {
                "serial" => target with { SerialNumber = target.SerialNumber + "-changed" },
                "partition-guid" => target with { PartitionGuid = Guid.NewGuid().ToString() },
                "geometry" => target with { SizeBytes = target.SizeBytes + 512 },
                "layout" => target with { Layout = WinPool.Domain.StorageLayoutParameters.LegacyDefault },
                "physical-members" => target with { PhysicalMembers = [new("unexpected", "uid", "oid", "serial")] },
                "tier-inputs" => target with { TierInputs = [new("unexpected", "uid", "oid", "HDD", 1)] },
                _ => target
            };
            var progress = new RealOperationStepProgress(step.Id, RealOperationStepState.OutcomeUnknown,
                issued.Code, JsonSerializer.Serialize(target), issued.ResultEvidenceJson);

            var result = await fixture.Backend.ReconcileAsync(plan, [progress], default);

            Assert.Equal(change == "valid" ? RealOperationState.Succeeded : RealOperationState.OutcomeUnknown, result.State);
            Assert.Equal(change == "valid", result.CanReleaseWriteBarrier);
            Assert.Equal(1, adapter.Calls); // Recovery never dispatches again.
        }

        private sealed class HostFixtureAdapter(WindowsPowerShellHost host) : IWindowsRealStorageCommandAdapter
        {
            private readonly WindowsRealStorageCommandAdapter adapter = new(host);
            public int Calls { get; private set; }
            public Action? BeforeCall { get; set; }
            public WindowsStorageCommandTarget? LastTarget { get; private set; }
            public WindowsStorageCommandResult? LastResult { get; private set; }
            public Task<WindowsPowerShellWriteLease?> ReserveWriteAsync(CancellationToken token) => adapter.ReserveWriteAsync(token);
            public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command, WindowsStorageCommandTarget target,
                CancellationToken token) => adapter.ExecuteAsync(command, target, token);
            public async Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command, WindowsStorageCommandTarget target,
                WindowsPowerShellWriteLease? lease, CancellationToken token)
            {
                Calls++; LastTarget = target; BeforeCall?.Invoke();
                return LastResult = await adapter.ExecuteAsync(command, target, lease, token);
            }
        }
    }
}
