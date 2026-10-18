using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent.Tests;

public sealed partial class AgentRealOperationServiceTests
{
    [Theory]
    [InlineData("alive", "none", false)]
    [InlineData("unverifiable", "none", false)]
    [InlineData("alive", "other-request", false)]
    [InlineData("alive", "other-incarnation", false)]
    [InlineData("exited", "none", true)]
    [InlineData("reused", "none", true)]
    [InlineData("reused-same-ms", "none", true)]
    [InlineData("alive", "root", true)]
    [InlineData("alive", "Provider", true)]
    [InlineData("alive", "ProviderResult", true)]
    public async Task OrdinaryRecoveryRequiresExactRequestCompletionOrOldProcessExit(
        string processState, string completionShape, bool mayReconcile)
    {
        var call = new PowerShellHostCall(Guid.NewGuid(), 62001, DateTimeOffset.UtcNow, Guid.NewGuid());
        var verifier = new HostRecoveryProcessVerifier(call, processState);
        var backend = new RecordingBackend
        {
            HostCall = call,
            ExecuteOverride = (_, _) => Task.FromResult(new RealStepResult(RealStepOutcome.OutcomeUnknown,
                "fake.postcondition_unknown", CompletionEvidence(call, completionShape))),
            Reconciliation = (plan, steps) => Task.FromResult(ObservedNoEffect(plan, steps))
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()), verifier);
        await using var lease = fixture.Lease;
        var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, mayReconcile
            ? PersistedOperationState.Failed : PersistedOperationState.OutcomeUnknown);
        await AwaitWorkerStoppedAsync(fixture.Service);
        Assert.Equal(mayReconcile ? 1 : 0, backend.ReconciliationCalls);
        Assert.Equal(!mayReconcile, await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(1, backend.ExecuteCalls);
        if (completionShape is "root" or "Provider" or "ProviderResult") Assert.Equal(0, verifier.Observations);

        if (!mayReconcile)
        {
            verifier.State = "exited";
            var restarted = new AgentRealOperationService(fixture.Plans, fixture.Events, backend,
                new FixedMachineIdentity(), null, null, _ => true, () => true, verifier);
            await restarted.InitializeRecoveryAsync();
            await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.Failed);
            Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
            Assert.Equal(1, backend.ExecuteCalls);
        }
    }

    [Theory]
    [InlineData("alive", "none", false, false)]
    [InlineData("unverifiable", "none", false, true)]
    [InlineData("alive", "other-request", false, true)]
    [InlineData("exited", "none", true, false)]
    [InlineData("reused", "none", true, false)]
    [InlineData("alive", "Provider", true, true)]
    public async Task MaximumRecoveryAppliesHostGuardToTheIssuedChildJournal(
        string processState, string completionShape, bool mayReconcile, bool hasResult)
    {
        var call = new PowerShellHostCall(Guid.NewGuid(), 62002, DateTimeOffset.UtcNow, Guid.NewGuid());
        var verifier = new HostRecoveryProcessVerifier(call, processState);
        var backend = new RecordingBackend { MaximumSearch = async (_, _, journal) =>
        {
            var attempt = MacroAttempt(1, 4, 0) with { TargetEvidenceJson = JsonSerializer.Serialize(new { HostCall = call }) };
            Assert.True(await journal.PrepareAsync(attempt, default));
            Assert.True(await journal.MarkCallIssuedAsync(1, default));
            if (hasResult)
                Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.OutcomeUnknown,
                    0, "fake.postcondition_unknown", CompletionEvidence(call, completionShape)), default));
            throw new IOException("Simulated parent loss after the durable child boundary.");
        }};
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()), verifier);
        await using var lease = fixture.Lease;
        var plan = await PrepareAndAcceptAsync(fixture.Service, MacroProposal(fixture.Proposal), fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.OutcomeUnknown);
        await AwaitWorkerStoppedAsync(fixture.Service);
        var liveRecoveryCalls = backend.MaximumRecoveryCalls;
        Assert.Equal(mayReconcile ? 1 : 0, liveRecoveryCalls);
        // Startup must apply the same child guard without replaying its writer.
        var restarted = new AgentRealOperationService(fixture.Plans, fixture.Events, backend,
            new FixedMachineIdentity(), null, null, _ => true, () => true, verifier);
        await restarted.InitializeRecoveryAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var status = Assert.IsType<AgentRealOperationResponse>((await restarted.QueryWithEvidenceAsync(
                new(plan.OperationId, CorrelationId.New()), fixture.Session, timeout.Token)).Value);
            if (status.ReconciliationDiagnostic is not null) break;
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(liveRecoveryCalls + (mayReconcile ? 1 : 0), backend.MaximumRecoveryCalls);
        Assert.Equal(1, backend.MaximumSearchCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
    }

    private static string CompletionEvidence(PowerShellHostCall call, string shape)
    {
        var completed = shape switch
        {
            "none" => null,
            "other-request" => call with { RequestId = Guid.NewGuid() },
            "other-incarnation" => call with { Incarnation = Guid.NewGuid() },
            _ => call
        };
        var provider = new WindowsStorageCommandResult(true, "provider.returned", null, null, null,
            null, null, null, null) { CompletedHostCall = completed };
        return shape switch
        {
            "Provider" => JsonSerializer.Serialize(new { Provider = provider, Diagnostic = "postcondition timeout" }),
            "ProviderResult" => JsonSerializer.Serialize(new { ProviderResult = provider }),
            _ => JsonSerializer.Serialize(provider)
        };
    }

    private sealed class HostRecoveryProcessVerifier(PowerShellHostCall call, string state) : IProcessIncarnationVerifier
    {
        public string State { get; set; } = state;
        public int Observations { get; private set; }
        public ProcessIncarnationObservation Observe(int processId)
        {
            Observations++;
            Assert.Equal(call.ProcessId, processId);
            return State switch
            {
                "exited" => new(ProcessIncarnationState.Exited, null, null),
                "unverifiable" => new(ProcessIncarnationState.Unverifiable, null, "access denied"),
                _ => new(ProcessIncarnationState.Alive, new(processId, WindowsPowerShellRunner.ExecutablePath,
                    State == "reused" ? call.StartedAtUtc.AddSeconds(1) : State == "reused-same-ms"
                        ? call.StartedAtUtc.AddTicks(1) : call.StartedAtUtc), null)
            };
        }
        public ProcessIncarnation? TryRead(int processId) => throw new NotSupportedException();
        public bool IsExpectedExecutable(int processId, string path) => throw new NotSupportedException();
        public bool Matches(AgentManagedProcess process, string path) => throw new NotSupportedException();
    }
}
