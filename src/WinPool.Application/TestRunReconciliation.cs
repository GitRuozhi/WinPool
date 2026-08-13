namespace WinPool.Application;

public enum TestRunReconciliationState
{
    Active,
    Terminal,
    NotFound,
    Unknown
}

public sealed record TestRunReconciliationDecision(
    bool IsRunning,
    bool CanStart,
    bool CanCancel,
    bool KeepUnknown);

public static class TestRunReconciliation
{
    public static TestRunReconciliationDecision Decide(
        TestRunReconciliationState state) => state switch
        {
            TestRunReconciliationState.Active => new(
                IsRunning: true,
                CanStart: false,
                CanCancel: true,
                KeepUnknown: false),
            TestRunReconciliationState.Terminal => new(
                IsRunning: false,
                CanStart: true,
                CanCancel: false,
                KeepUnknown: false),
            TestRunReconciliationState.NotFound => new(
                IsRunning: false,
                CanStart: true,
                CanCancel: false,
                KeepUnknown: false),
            _ => new(
                IsRunning: false,
                CanStart: false,
                CanCancel: false,
                KeepUnknown: true)
        };
}
