using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class TestRunReconciliationTests
{
    [Theory]
    [InlineData(TestRunReconciliationState.Active, true, false, true, false)]
    [InlineData(TestRunReconciliationState.Terminal, false, true, false, false)]
    [InlineData(TestRunReconciliationState.NotFound, false, true, false, false)]
    [InlineData(TestRunReconciliationState.Unknown, false, false, false, true)]
    public void DecisionKeepsButtonsAlignedWithAuthoritativeState(
        TestRunReconciliationState state,
        bool isRunning,
        bool canStart,
        bool canCancel,
        bool keepUnknown)
    {
        var decision = TestRunReconciliation.Decide(state);

        Assert.Equal(isRunning, decision.IsRunning);
        Assert.Equal(canStart, decision.CanStart);
        Assert.Equal(canCancel, decision.CanCancel);
        Assert.Equal(keepUnknown, decision.KeepUnknown);
    }
}
