using WinPool.App.Services;

namespace WinPool.Application.Tests;

public sealed class MonitorIssueStateTrackerTests
{
    [Fact]
    public void UnknownSnapshotDoesNotRecoverPriorIssueOrReportItAgain()
    {
        using var tracker = new MonitorIssueStateTracker();

        var appeared = tracker.Update(
            [new MonitorIssueState("communication:agent-timeout", "Communication timed out")],
            stateIsAuthoritative: true);
        var unknown = tracker.Update([], stateIsAuthoritative: false);
        var stillUnknown = tracker.Update([], stateIsAuthoritative: false);

        Assert.Single(appeared.Transitions);
        Assert.Equal(MonitorIssueTransitionKind.Appeared, appeared.Transitions[0].Kind);
        Assert.Empty(unknown.Transitions);
        Assert.Empty(stillUnknown.Transitions);
        Assert.Contains(unknown.ActiveIssues, issue => issue.Key == "communication:agent-timeout");
    }

    [Fact]
    public void RepeatedObservedIssueDoesNotProduceDuplicateAppearance()
    {
        using var tracker = new MonitorIssueStateTracker();
        tracker.Update(
            [new MonitorIssueState("persistence-delay:session-a", "Samples are buffered")],
            stateIsAuthoritative: true);

        var repeated = tracker.Update(
            [new MonitorIssueState("persistence-delay:session-a", "Samples are buffered")],
            stateIsAuthoritative: true);

        Assert.Empty(repeated.Transitions);
    }

    [Fact]
    public void AuthoritativeRecoveryIsReportedOnce()
    {
        using var tracker = new MonitorIssueStateTracker();
        tracker.Update(
            [new MonitorIssueState("sampling:session-a", "Sampling stopped")],
            stateIsAuthoritative: true);

        var recovered = tracker.Update([], stateIsAuthoritative: true);
        var repeated = tracker.Update([], stateIsAuthoritative: true);

        var transition = Assert.Single(recovered.Transitions);
        Assert.Equal(MonitorIssueTransitionKind.Recovered, transition.Kind);
        Assert.Equal("sampling:session-a", transition.Issue.Key);
        Assert.Empty(repeated.Transitions);
    }

    [Fact]
    public void ReappearedIssueReportsOnlyAfterAnAuthoritativeRecovery()
    {
        using var tracker = new MonitorIssueStateTracker();
        var issue = new MonitorIssueState("communication:agent-timeout", "Communication timed out");

        tracker.Update([issue], stateIsAuthoritative: true);
        tracker.Update([issue], stateIsAuthoritative: true);
        var recovered = tracker.Update([], stateIsAuthoritative: true);
        var reappeared = tracker.Update([issue], stateIsAuthoritative: true);

        Assert.Single(recovered.Transitions);
        var appearance = Assert.Single(reappeared.Transitions);
        Assert.Equal(MonitorIssueTransitionKind.Appeared, appearance.Kind);
        Assert.Equal(issue.Key, appearance.Issue.Key);
    }

    [Fact]
    public void PermanentGapSurvivesAuthoritativeSessionChangeWithoutRecovery()
    {
        using var tracker = new MonitorIssueStateTracker();
        tracker.Update(
            [new MonitorIssueState("known-loss:session-a", "One sample was not saved", IsPermanentGap: true)],
            stateIsAuthoritative: true);

        var nextSession = tracker.Update(
            [new MonitorIssueState("communication:session-b", "Agent reconnecting")],
            stateIsAuthoritative: true);

        Assert.DoesNotContain(
            nextSession.Transitions,
            transition => transition.Kind == MonitorIssueTransitionKind.Recovered
                && transition.Issue.Key == "known-loss:session-a");
        Assert.Contains(nextSession.ActiveIssues, issue => issue.Key == "known-loss:session-a");
        Assert.Contains(nextSession.Transitions, transition =>
            transition.Kind == MonitorIssueTransitionKind.Appeared
            && transition.Issue.Key == "communication:session-b");
    }

    [Fact]
    public void LaterSessionIssueIsNotSilentlyDroppedWhenOldPermanentGapsReachCapacity()
    {
        using var tracker = new MonitorIssueStateTracker();
        for (var index = 0; index < 64; index++)
        {
            tracker.Update(
                [new MonitorIssueState($"known-loss:session-{index}", "Recording gap", IsPermanentGap: true)],
                stateIsAuthoritative: true);
        }

        var later = tracker.Update(
            [new MonitorIssueState("sampling:session-later", "Sampling stopped")],
            stateIsAuthoritative: true);

        Assert.Contains(later.Transitions, transition =>
            transition.Kind == MonitorIssueTransitionKind.Appeared
            && transition.Issue.Key == "sampling:session-later");
    }
}
