using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Application.Tests;

public sealed class RealOperationWaitClockTests
{
    [Fact]
    public void SilentIssuedCallUsesMonotonicElapsedAndDoesNotDeclareFailure()
    {
        var time = new Clock();
        var clock = new RealOperationWaitClock(time);
        var operation = OperationId.New();
        RealOperationStepProgress[] steps = [new("write", RealOperationStepState.WaitingForProvider, null, null, null)];
        clock.Observe(operation, RealOperationState.Running, steps, 59_000);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains("结果尚未确认", clock.Format(true), StringComparison.Ordinal);
        Assert.Contains("61", clock.Format(true), StringComparison.Ordinal);
        clock.Observe(operation, RealOperationState.Running, steps, 0); // a delayed observation cannot reset the timer
        Assert.Contains("61", clock.Format(true), StringComparison.Ordinal);
        Assert.Equal(RealOperationStepState.WaitingForProvider, steps[0].State);
        clock.Observe(operation, RealOperationState.OutcomeUnknown, steps, 61_000);
        Assert.False(clock.IsActive);
        Assert.Null(clock.Format(true));
    }

    [Fact]
    public void NextStepStartsItsOwnClockAndTerminalStopsPresentation()
    {
        var time = new Clock();
        var clock = new RealOperationWaitClock(time);
        var operation = OperationId.New();
        clock.Observe(operation, RealOperationState.Running,
            [new("first", RealOperationStepState.CallIssued, null, null, null)], 50_000);
        time.Advance(TimeSpan.FromSeconds(20));
        clock.Observe(operation, RealOperationState.Running,
            [new("second", RealOperationStepState.CallIssued, null, null, null)], 0);
        Assert.Contains("second · 0 s", clock.Format(false), StringComparison.Ordinal);
        clock.Observe(operation, RealOperationState.Succeeded,
            [new("second", RealOperationStepState.Verified, null, null, null)], 0);
        Assert.Null(clock.Format(false));
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(-ticks);
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
