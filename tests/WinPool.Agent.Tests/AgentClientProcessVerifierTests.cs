using System.Diagnostics;
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

    [Fact]
    public void LimitedProcessQueryReadsTheCurrentProcessWitness()
    {
        var witness = new WindowsProcessIncarnationVerifier().TryRead(Environment.ProcessId);

        Assert.NotNull(witness);
        Assert.Equal(Environment.ProcessId, witness.ProcessId);
        Assert.True(Path.IsPathFullyQualified(witness.ImagePath));
        Assert.True(witness.StartedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(259)]
    public async Task ExitedProcessWithRetainedHandleDoesNotMatchItsRegisteredIncarnation(int exitCode)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        child.StartInfo.ArgumentList.Add("/D");
        child.StartInfo.ArgumentList.Add("/Q");
        Assert.True(child.Start());
        var retainedHandle = child.SafeHandle;
        var retainedReference = false;
        try
        {
            retainedHandle.DangerousAddRef(ref retainedReference);
            var verifier = new WindowsProcessIncarnationVerifier();
            var witness = verifier.TryRead(child.Id);
            Assert.NotNull(witness);
            var registration = new AgentManagedProcess(ProcessInstanceId.New(), child.Id,
                AgentManagedProcessKind.MainApplication, CorrelationId.New(),
                witness.StartedAtUtc, witness.StartedAtUtc, SupervisedProcessState.Running,
                OwnsJobObject: false, ShutdownDeadlineUtc: null);
            Assert.True(verifier.IsExpectedExecutable(child.Id, executable));
            Assert.True(verifier.Matches(registration, executable));
            Assert.False(verifier.Matches(registration with
            {
                StartedAtUtc = registration.StartedAtUtc.AddSeconds(1)
            }, executable));

            await child.StandardInput.WriteLineAsync($"exit /b {exitCode}");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(exitCode, child.ExitCode);
            Assert.False(retainedHandle.IsClosed);
            Assert.Null(verifier.TryRead(child.Id));
            Assert.False(verifier.IsExpectedExecutable(child.Id, executable));
            Assert.False(verifier.Matches(registration, executable));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (retainedReference) retainedHandle.DangerousRelease();
        }
    }
}
