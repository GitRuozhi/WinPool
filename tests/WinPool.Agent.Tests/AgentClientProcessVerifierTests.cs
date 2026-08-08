using WinPool.Agent;

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
}
