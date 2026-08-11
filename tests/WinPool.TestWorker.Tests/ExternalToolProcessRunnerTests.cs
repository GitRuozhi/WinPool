using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.TestWorker.Tests;

public sealed class ExternalToolProcessRunnerTests
{
    [Fact]
    public void StartInfoUsesArgumentTokensWithoutShellAndOnlyFixedEnvironment()
    {
        const string hostileToken = "value & echo NOT_A_COMMAND > injected.txt";
        var request = TestWorkerTestSupport.CreateRequest(
            ["echo-args", hostileToken],
            TimeSpan.FromSeconds(10),
            environment: new Dictionary<string, string> { ["WINPOOL_FIXED"] = "yes" });

        var startInfo = ExternalToolProcessRunner.CreateStartInfo(
            request.Invocation,
            request.Invocation.ExecutablePath);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(request.Invocation.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(["echo-args", hostileToken], startInfo.ArgumentList);
        var environment = Assert.Single(startInfo.Environment);
        Assert.Equal("WINPOOL_FIXED", environment.Key);
        Assert.Equal("yes", environment.Value);
    }

    [Fact]
    public async Task ExecutePreservesMetacharactersAsOneArgumentAndRecordsIdentity()
    {
        const string hostileToken = "value & echo NOT_A_COMMAND > injected.txt";
        var request = TestWorkerTestSupport.CreateRequest(
            ["echo-args", hostileToken],
            TimeSpan.FromSeconds(10));
        var eventBuffer = new BoundedWorkerEventBuffer(32);

        var result = await new ExternalToolProcessRunner().ExecuteAsync(
            request,
            eventBuffer,
            CancellationToken.None);

        var events = eventBuffer.Drain();
        var outputBytes = events
            .Where(item => item.Kind is WorkerEventKind.StandardOutput)
            .SelectMany(item => item.RawBytes.ToArray())
            .ToArray();
        Assert.All(
            events.Where(item => item.Kind is
                WorkerEventKind.StandardOutput or WorkerEventKind.StandardError),
            item => Assert.Equal(Encoding.UTF8.CodePage, item.OutputCodePage));
        var arguments = JsonSerializer.Deserialize<string[]>(
            Encoding.UTF8.GetString(outputBytes).Trim());

        Assert.NotNull(arguments);
        Assert.Equal([hostileToken], arguments);
        Assert.Equal(ToolProcessTerminationReason.Completed, result.Audit.TerminationReason);
        Assert.Equal(0, result.Audit.ExitCode);
        Assert.Equal(Path.GetFullPath(TestWorkerTestSupport.HelperPath), result.Audit.Identity.ExecutablePath);
        Assert.Equal(64, result.Audit.Identity.Sha256.Length);
        Assert.False(string.IsNullOrWhiteSpace(result.Audit.Identity.FileVersion));
        Assert.True(result.Audit.Identity.ProcessId > 0);
        Assert.True(result.Audit.ExitedAtUtc >= result.Audit.Identity.StartedAtUtc);
        var startedEvent = Assert.Single(
            events,
            item => item.Code is "tool.process.started");
        Assert.Equal(result.Audit.Identity, startedEvent.ProcessIdentity);
        var exitedEvent = Assert.Single(
            events,
            item => item.Code is "tool.process.exited");
        Assert.Equal(result.Audit.ExitCode, exitedEvent.ExitCode);
        Assert.False(File.Exists(Path.Combine(request.Invocation.WorkingDirectory, "injected.txt")));
    }

    [Fact]
    public async Task CancellationRequestsGracefulStopBeforeTerminatingJob()
    {
        var order = new List<string>();
        var jobFactory = new RecordingJobFactory(order);
        var graceful = new RecordingGracefulTermination(order);
        var runner = new ExternalToolProcessRunner(jobFactory, graceful, TimeProvider.System);
        var request = TestWorkerTestSupport.CreateRequest(
            ["wait"],
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(75));
        var eventBuffer = new BoundedWorkerEventBuffer(32);
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.ExecuteAsync(request, eventBuffer, cancellation.Token);
        await jobFactory.Assigned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await runTask;

        Assert.Equal(ToolProcessTerminationReason.Cancelled, result.Audit.TerminationReason);
        Assert.True(result.Audit.GracefulTerminationRequested);
        Assert.True(result.Audit.GracefulTerminationAccepted);
        Assert.True(result.Audit.JobTerminationRequired);
        Assert.Equal(["graceful", "job"], order);
    }

    [Fact]
    public async Task TimeoutTerminatesJobAfterGracePeriod()
    {
        var request = TestWorkerTestSupport.CreateRequest(
            ["wait"],
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(75));
        var eventBuffer = new BoundedWorkerEventBuffer(32);

        var result = await new ExternalToolProcessRunner().ExecuteAsync(
            request,
            eventBuffer,
            CancellationToken.None);

        Assert.Equal(ToolProcessTerminationReason.TimedOut, result.Audit.TerminationReason);
        Assert.True(result.Audit.GracefulTerminationRequested);
        Assert.True(result.Audit.JobTerminationRequired);
        Assert.NotEqual(0, result.Audit.ExitCode);
    }

    [Fact]
    public async Task KillOnJobCloseReclaimsDescendantProcessTree()
    {
        var evidenceDirectory = Path.Combine(
            Path.GetTempPath(),
            "WinPool.TestWorker.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        var childPidPath = Path.Combine(evidenceDirectory, "child.pid");
        var request = TestWorkerTestSupport.CreateRequest(
            ["spawn-child", childPidPath],
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(75));
        var eventBuffer = new BoundedWorkerEventBuffer(32);
        using var cancellation = new CancellationTokenSource();

        var runTask = new ExternalToolProcessRunner().ExecuteAsync(
            request,
            eventBuffer,
            cancellation.Token);
        var childPid = await TestWorkerTestSupport.WaitForInt32FileAsync(
            childPidPath,
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await runTask;

        Assert.Equal(ToolProcessTerminationReason.Cancelled, result.Audit.TerminationReason);
        Assert.True(result.Audit.JobTerminationRequired);
        Assert.True(
            await TestWorkerTestSupport.WaitForProcessExitAsync(
                childPid,
                TimeSpan.FromSeconds(5)),
            $"Descendant process {childPid} remained alive after Job termination.");
    }

    private sealed class RecordingGracefulTermination(List<string> order)
        : IGracefulToolTermination
    {
        public ValueTask<bool> RequestAsync(
            ToolId toolId,
            Process process,
            CancellationToken cancellationToken)
        {
            order.Add("graceful");
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingJobFactory(List<string> order)
        : IProcessTreeJobFactory
    {
        public TaskCompletionSource Assigned { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IProcessTreeJob Create() => new RecordingJob(order, Assigned);
    }

    private sealed class RecordingJob(
        List<string> order,
        TaskCompletionSource assigned) : IProcessTreeJob
    {
        private readonly WindowsJobObject _inner = new();

        public void Assign(Process process)
        {
            _inner.Assign(process);
            assigned.TrySetResult();
        }

        public void Terminate(uint exitCode)
        {
            order.Add("job");
            _inner.Terminate(exitCode);
        }

        public void Dispose() => _inner.Dispose();
    }
}
