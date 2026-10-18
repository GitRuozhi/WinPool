using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Fact]
    public async Task FirstPostconditionObservationRunsImmediatelyAndReusesTheExactReturnedBatch()
    {
        var clock = new ProbeClock(forbidDelay: true);
        var fixture = new Fixture(scopedCaptures: true, backendClock: clock);
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);
        var beforeCalls = fixture.Source.Scopes.Count;
        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, default);
        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(beforeCalls + 1, fixture.Source.Scopes.Count);
        Assert.Empty(clock.Delays);
        Assert.True(fixture.Safety.LastObservedToken.CanBeCanceled);
        Assert.Equal(1, fixture.Adapter.CallCount);
        Assert.NotNull(result.VerifiedPostState);
        Assert.Equal(fixture.Source.Scopes[^1], result.VerifiedPostState.Scope);
        Assert.Equal(plan.RealOperation.EvidenceProfile, result.VerifiedPostState.Scope!.EvidenceProfile);
    }

    [Fact]
    public async Task UnavailableNarrowPostconditionUsesOneFullFallbackAndNeverReissuesTheWrite()
    {
        var clock = new ProbeClock();
        var fixture = new Fixture(scopedCaptures: true, backendClock: clock);
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        var fullBefore = fixture.Source.FullCalls;
        fixture.Source.ScopedFailure = new IOException("narrow provider evidence unavailable");
        // The full fallback returns the old state, so the overall window must expire.
        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, default);
        Assert.Equal(RealStepOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(fullBefore + 1, fixture.Source.FullCalls);
        Assert.Equal(1, fixture.Adapter.CallCount);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Delays[0]);
        Assert.All(clock.Delays, delay => Assert.InRange(delay.TotalMilliseconds, 1, 2000));
        Assert.Equal(TimeSpan.FromSeconds(60), TimeSpan.FromTicks(clock.Delays.Sum(delay => delay.Ticks)));
    }

    [Fact]
    public async Task FullFallbackMayVerifyTheOriginalCallWithoutAnotherPreflightOrWrite()
    {
        var clock = new ProbeClock();
        var fixture = new Fixture(scopedCaptures: true, backendClock: clock);
        var plan = await fixture.PrepareAsync();
        var step = Assert.Single(plan.RealOperation!.Steps);
        var preflight = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        var fullBefore = fixture.Source.FullCalls;
        fixture.Adapter.OnExecute = () => fixture.SetOffline(true);
        fixture.Source.ScopedFailure = new IOException("narrow provider evidence unavailable");
        var result = await fixture.Backend.ExecuteStepAsync(plan, step, preflight, default);
        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal(fullBefore + 1, fixture.Source.FullCalls);
        Assert.Equal(1, fixture.Adapter.CallCount);
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(clock.Delays));
        Assert.Null(result.VerifiedPostState!.Scope); // Actual whole-machine batch, without a scoped freshness claim.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OsAndMetadataPreflightDoNotRequirePoolabilityButStillRejectAnUnreadableWindowsRole(bool rename)
    {
        var fixture = new Fixture();
        var plan = rename ? await fixture.PrepareRenameAsync() : await fixture.PrepareAsync();
        Assert.Equal(rename ? WindowsRealStorageReadProfile.Metadata : WindowsRealStorageReadProfile.Os, plan.RealOperation!.EvidenceProfile);
        var step = Assert.Single(plan.RealOperation.Steps);
        fixture.FactsTransform = item => item.Id != PhysicalId ? item : item with
        {
            Fields = item.Fields.Select(field => field.Name != "CanPool" ? field
                : field with { Value = null, ReadState = FieldReadState.Failed, ReasonCode = "UnrelatedPoolabilityProbe" }).ToImmutableArray()
        };
        var accepted = await fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default);
        Assert.Equal(plan.RealOperation.TargetFingerprint, accepted.TargetFingerprint);
        fixture.FactsTransform = item => item.Id != PhysicalId ? item : item with
        {
            Fields = item.Fields.Select(field => field.Name != "IsSystem" ? field
                : field with { Value = null, ReadState = FieldReadState.Failed, ReasonCode = "UnreadableProtection" }).ToImmutableArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backend.PreflightStepAsync(plan, step, new Dictionary<string, string>(), default));
        Assert.Equal(0, fixture.Adapter.CallCount);
        var restored = JsonSerializer.Deserialize<OperationPlan>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(plan.PlanHash, OperationPlanHasher.Compute(restored));
        Assert.Equal(plan.RealOperation.EvidenceProfile, restored.RealOperation!.EvidenceProfile);
    }

    private sealed class ProbeClock(bool forbidDelay = false) : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
        public List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (forbidDelay) throw new InvalidOperationException("The first healthy observation must not wait for a timer.");
            Delays.Add(dueTime);
            return new Timer(_ => { now += dueTime; callback(state); }, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        }
    }
}
