using WinPool.Application;
using WinPool.App.Services;
using WinPool.Domain;

namespace WinPool.Infrastructure.Tests;

public sealed class MonitorEditAlertPresentationTests
{
    [Theory]
    [InlineData(false, MonitorEditTargetStatus.PendingVerification, "monitor.edit.recovered_endpoint_unknown", false)]
    [InlineData(true, MonitorEditTargetStatus.PendingVerification, "monitor.edit.recovered_endpoint_unknown", true)]
    [InlineData(false, MonitorEditTargetStatus.NeedsSelection, "monitor.edit.recovered_endpoint_unknown", true)]
    [InlineData(false, MonitorEditTargetStatus.Editing, "monitor.edit.recovered_endpoint_unknown", true)]
    [InlineData(false, MonitorEditTargetStatus.PendingVerification, "monitor.edit.outcome_needs_reconciliation", true)]
    [InlineData(false, MonitorEditTargetStatus.PendingVerification, "monitor.edit.expected_change_sampling_paused", true)]
    [InlineData(false, MonitorEditTargetStatus.PendingVerification, "monitor.edit.verified_without_active_sampler", true)]
    [InlineData(false, MonitorEditTargetStatus.PendingVerification, "monitor.edit.Recovered_endpoint_unknown", true)]
    public void OnlyInactiveHistoricalPendingEndpointIsExcluded(bool isRunning,
        MonitorEditTargetStatus status, string reason, bool expected) =>
        Assert.Equal(expected, MonitorEditAlertPresentation.ShouldReportCurrentIssue(isRunning, State(status, reason)));

    [Fact]
    public void HidingInactiveHistoryRetiresItsCurrentIssueWithoutChangingItsRetainedState()
    {
        var state = State(MonitorEditTargetStatus.PendingVerification, "monitor.edit.recovered_endpoint_unknown");
        var original = state with { };
        using var tracker = new MonitorIssueStateTracker();
        var issue = new MonitorIssueState("edit:historical", state.ReasonCode);
        Assert.Single(tracker.Update([issue], true).ActiveIssues);

        var visible = MonitorEditAlertPresentation.ShouldReportCurrentIssue(false, state)
            ? new[] { issue } : Array.Empty<MonitorIssueState>();
        var snapshot = tracker.Update(visible, true);

        Assert.Empty(snapshot.ActiveIssues);
        Assert.Equal(MonitorIssueTransitionKind.Recovered, Assert.Single(snapshot.Transitions).Kind);
        Assert.Equal(original, state);
        Assert.Equal(MonitorEditTargetStatus.PendingVerification, state.Status);
    }

    private static MonitorEditTargetState State(MonitorEditTargetStatus status, string reason)
    {
        var system = SystemId.New();
        return new(system, new(system, StorageObjectKind.PhysicalDisk, "exact-physical"),
            OperationId.New(), "create-pool", status, reason, DateTimeOffset.UnixEpoch, "0");
    }
}
