using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentTestCoordinatorTests
{
    [Fact]
    public void ActiveSlotIsSingleOwnerAndCancellationIsRunScoped()
    {
        var coordinator = new AgentTestCoordinator();
        var firstRun = new TestRunId(Guid.NewGuid());
        var otherRun = new TestRunId(Guid.NewGuid());
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var active = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(coordinator.TryReserve(firstRun, firstCancellation));
        coordinator.Attach(active.Task);
        Assert.True(coordinator.HasActiveTest);
        Assert.Equal(firstRun, coordinator.ActiveRunId);
        Assert.False(coordinator.TryReserve(otherRun, secondCancellation));
        Assert.False(coordinator.TryCancel(otherRun));
        Assert.False(firstCancellation.IsCancellationRequested);

        Assert.True(coordinator.TryCancel(firstRun));
        Assert.True(firstCancellation.IsCancellationRequested);
        coordinator.Complete(firstRun);
        active.SetResult();

        Assert.False(coordinator.HasActiveTest);
        Assert.Null(coordinator.ActiveRunId);
    }

    [Fact]
    public void FailedReservationCanBeReleasedForTheNextRun()
    {
        var coordinator = new AgentTestCoordinator();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        Assert.True(coordinator.TryReserve(
            new TestRunId(Guid.NewGuid()),
            firstCancellation));
        coordinator.ReleaseReservation();

        Assert.True(coordinator.TryReserve(
            new TestRunId(Guid.NewGuid()),
            secondCancellation));
    }

    [Fact]
    public async Task PauseWaitsForBoundaryAndResumeContinuesTheSameRun()
    {
        var coordinator = new AgentTestCoordinator();
        var run = new TestRunId(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.TryReserve(run, cancellation));
        coordinator.Attach(active.Task);

        Assert.True(coordinator.TryRequestPause(run));
        var boundary = coordinator.WaitForSafePauseBoundaryAsync(run, cancellation.Token);
        await Task.Yield();
        Assert.Equal(TestPauseState.Paused, coordinator.PauseState);
        Assert.False(boundary.IsCompleted);

        Assert.True(coordinator.TryResume(run));
        await boundary;
        Assert.Equal(TestPauseState.Running, coordinator.PauseState);
    }

    [Fact]
    public async Task CancellationReleasesAWorkflowWaitingAtPauseBoundary()
    {
        var coordinator = new AgentTestCoordinator();
        var run = new TestRunId(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.TryReserve(run, cancellation));
        coordinator.Attach(active.Task);
        Assert.True(coordinator.TryRequestPause(run));
        var boundary = coordinator.WaitForSafePauseBoundaryAsync(run, cancellation.Token);
        await Task.Yield();

        Assert.True(coordinator.TryCancel(run));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await boundary);
    }
}
