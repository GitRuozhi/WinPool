using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentClientProcessVerifierTests
{
    [Fact]
    public void AcceptsOnlyTheExactExpectedExecutableImage()
    {
        var currentImage = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current test image unavailable.");

        Assert.True(
            AgentClientProcessVerifier.IsExpectedExecutable(
                Environment.ProcessId,
                currentImage));
        Assert.False(
            AgentClientProcessVerifier.IsExpectedExecutable(
                Environment.ProcessId,
                Path.Combine(
                    Path.GetDirectoryName(currentImage)!,
                    "WinPool.App.exe")));
    }

    [Fact]
    public void RejectsInvalidOrExitedProcessIdentity()
    {
        Assert.False(
            AgentClientProcessVerifier.IsExpectedExecutable(
                -1,
                Path.GetFullPath("WinPool.App.exe")));
        Assert.False(
            AgentClientProcessVerifier.IsExpectedExecutable(
                int.MaxValue,
                Path.GetFullPath("WinPool.App.exe")));
    }

    [Fact]
    public void IncarnationMatcherRejectsPidReuseImageAndStartWitnessMismatches()
    {
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var registration = new AgentManagedProcess(
            ProcessInstanceId.New(),
            42,
            AgentManagedProcessKind.MainApplication,
            CorrelationId.New(),
            started,
            started,
            SupervisedProcessState.Running,
            OwnsJobObject: false,
            ShutdownDeadlineUtc: null);
        var expectedImage = Path.GetFullPath("WinPool.App.exe");
        var matching = new ProcessIncarnation(42, expectedImage, started);

        Assert.True(ProcessIncarnationMatcher.Matches(matching, registration, expectedImage));
        Assert.False(ProcessIncarnationMatcher.Matches(
            matching with { ProcessId = 43 }, registration, expectedImage));
        Assert.False(ProcessIncarnationMatcher.Matches(
            matching with { ImagePath = Path.GetFullPath("Other.exe") }, registration, expectedImage));
        Assert.False(ProcessIncarnationMatcher.Matches(
            matching with { StartedAtUtc = started.AddSeconds(1) }, registration, expectedImage));
    }
}
