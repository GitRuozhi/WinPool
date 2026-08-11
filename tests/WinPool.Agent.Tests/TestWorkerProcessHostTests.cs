using System.Security.Principal;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Ipc;
using WinPool.Testing.Tools;
using WinPool.TestWorker;

namespace WinPool.Agent.Tests;

public sealed class TestWorkerProcessHostTests
{
    [Fact]
    public async Task CancellationBeforeWorkerConnectDoesNotLeaveWorkerAlive()
    {
        var host = CreateHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workerPid = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.RunAsync(
                CreateCommandRequest(["/d", "/c", "echo", "MUST_NOT_RUN"]),
                null,
                (processId, _) =>
                {
                    workerPid = processId;
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.True(workerPid > 0);
        Assert.True(await WaitForExitAsync(workerPid));
    }

    [Fact]
    public async Task ExitPolicyKillsHungProcessTreeAfterBoundedGrace()
    {
        var commandPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = commandPath,
                Arguments = "/d /c ping -n 30 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Test process did not start.");

        var outcome = await SupervisedProcessExitPolicy.EnsureExitedAsync(
            process,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(5));

        Assert.False(outcome.ExitedDuringGrace);
        Assert.True(outcome.ProcessTreeKillRequested);
        Assert.True(outcome.ExitedAfterKill);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AgentHostAuthenticatesWorkerAndReceivesStreamedEvents()
    {
        var workerPath = Path.Combine(
            Path.GetDirectoryName(typeof(ExternalToolProcessRunner).Assembly.Location)!,
            "WinPool.TestWorker.exe");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID unavailable.");
        var host = new TestWorkerProcessHost(
            workerPath,
            IpcIdentity.HashUserSid(sid),
            Environment.ProcessId);
        var commandPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var toolId = new ToolId("winpool.controlled-cmd");
        var request = new ToolProcessRequest(
            TestRunId.New(),
            "echo",
            new(
                toolId,
                commandPath,
                ["/d", "/c", "echo", "WINPOOL_HOST_OK"],
                Path.GetDirectoryName(commandPath)!,
                new Dictionary<string, string>(),
                ToolOutputEncoding.Utf8,
                TimeSpan.FromSeconds(10)),
            new(
                toolId,
                ToolAvailability.Available,
                commandPath,
                ToolPathSource.WindowsComponent,
                null,
                null,
                null,
                ToolCapabilities.StructuredOutput,
                false),
            TimeSpan.FromMilliseconds(200));
        var batches = new List<TestWorkerEventBatch>();
        var workerPid = 0;

        var result = await host.RunAsync(
            request,
            (batch, _) =>
            {
                batches.Add(batch);
                return Task.CompletedTask;
            },
            (processId, _) =>
            {
                workerPid = processId;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(workerPid > 0);
        Assert.Equal(workerPid, result.WorkerProcessId);
        Assert.Equal(0, Assert.Single(result.ToolResults).Audit.ExitCode);
        Assert.Contains(
            result.Events,
            item => item.Kind == WorkerEventKind.StandardOutput
                    && System.Text.Encoding.UTF8.GetString(item.RawBytes.Span)
                        .Contains("WINPOOL_HOST_OK", StringComparison.Ordinal));
        Assert.NotEmpty(batches);
        Assert.True(await WaitForExitAsync(workerPid));
    }

    [Fact]
    public async Task AgentCancellationStopsWorkerAndItsControlledProcessJob()
    {
        var host = CreateHost();
        var request = CreateCommandRequest(
            ["/d", "/c", "ping", "-n", "30", "127.0.0.1"]);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(300));
        var workerPid = 0;

        var result = await host.RunAsync(
            request,
            null,
            (processId, _) =>
            {
                workerPid = processId;
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(
            ToolProcessTerminationReason.Cancelled,
            Assert.Single(result.ToolResults).Audit.TerminationReason);
        Assert.True(Assert.Single(result.ToolResults).Audit.GracefulTerminationRequested);
        Assert.True(workerPid > 0);
        Assert.True(await WaitForExitAsync(workerPid));
    }

    [Fact]
    public async Task AbruptWorkerTerminationClosesItsJobAndStopsExternalProcess()
    {
        var host = CreateHost();
        var request = CreateCommandRequest(
            ["/d", "/c", "ping", "-n", "30", "127.0.0.1"]);
        var workerPid = 0;
        var toolPid = 0;
        var killed = false;

        var failure = await Record.ExceptionAsync(
            () => host.RunAsync(
                request,
                (batch, _) =>
                {
                    var started = batch.Events.FirstOrDefault(item =>
                        item.Code == "tool.process.started"
                        && item.ProcessIdentity is not null);
                    if (!killed && started?.ProcessIdentity is { } identity)
                    {
                        toolPid = identity.ProcessId;
                        using var worker = System.Diagnostics.Process.GetProcessById(
                            workerPid);
                        worker.Kill(entireProcessTree: false);
                        killed = true;
                    }

                    return Task.CompletedTask;
                },
                (processId, _) =>
                {
                    workerPid = processId;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.NotNull(failure);
        Assert.True(killed);
        Assert.True(workerPid > 0);
        Assert.True(toolPid > 0);
        Assert.True(await WaitForExitAsync(workerPid));
        Assert.True(await WaitForExitAsync(toolPid));
    }

    [Fact]
    public async Task AgentRejectsWorkerOutputBeyondBoundedCaptureLimit()
    {
        var workerPath = Path.Combine(
            Path.GetDirectoryName(typeof(ExternalToolProcessRunner).Assembly.Location)!,
            "WinPool.TestWorker.exe");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID unavailable.");
        var host = new TestWorkerProcessHost(
            workerPath,
            IpcIdentity.HashUserSid(sid),
            Environment.ProcessId,
            maximumCapturedOutputBytes: 1);
        var workerPid = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => host.RunAsync(
                CreateCommandRequest(
                    ["/d", "/c", "echo", "OUTPUT_EXCEEDS_LIMIT"]),
                null,
                (processId, _) =>
                {
                    workerPid = processId;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.True(workerPid > 0);
        Assert.True(await WaitForExitAsync(workerPid));
    }

    [Fact]
    public async Task MultiStepRunShortCircuitsAfterFirstFailedTool()
    {
        var host = CreateHost();
        var first = CreateCommandRequest(["/d", "/c", "exit", "/b", "9"])
            with { StepId = "first" };
        var second = CreateCommandRequest(
                ["/d", "/c", "echo", "MUST_NOT_RUN"])
            with
            {
                RunId = first.RunId,
                StepId = "second"
            };

        var result = await host.RunAsync(
            [first, second],
            null,
            null,
            CancellationToken.None);

        var only = Assert.Single(result.ToolResults);
        Assert.Equal("first", only.Audit.StepId);
        Assert.Equal(9, only.Audit.ExitCode);
        Assert.DoesNotContain(
            result.Events,
            item => item.StepId == "second");
    }

    [Fact]
    public async Task RoboCopyDiskFullFailureStopsBatchAndPreservesDiagnostic()
    {
        var host = CreateHost();
        var toolId = new ToolId(ToolProcessExitPolicy.RoboCopyToolId);
        var failedBase = CreateCommandRequest(
            ["/d", "/c", "echo ERROR 112: There is not enough space on the disk. 1>&2 & exit /b 8"]);
        var failed = failedBase with
        {
            StepId = "copy-disk-full",
            Invocation = failedBase.Invocation with { ToolId = toolId },
            ExpectedTool = failedBase.ExpectedTool with { ToolId = toolId }
        };
        var nextBase = CreateCommandRequest(
            ["/d", "/c", "echo MUST_NOT_RUN"]);
        var next = nextBase with
        {
            RunId = failed.RunId,
            StepId = "copy-next",
            Invocation = nextBase.Invocation with { ToolId = toolId },
            ExpectedTool = nextBase.ExpectedTool with { ToolId = toolId }
        };

        var result = await host.RunAsync(
            [failed, next],
            null,
            null,
            CancellationToken.None);

        var only = Assert.Single(result.ToolResults);
        Assert.Equal(8, only.Audit.ExitCode);
        Assert.False(ToolProcessExitPolicy.IsAccepted(
            only.Audit.ToolId,
            only.Audit.ExitCode));
        Assert.DoesNotContain(result.Events, item => item.StepId == "copy-next");
        Assert.Contains(
            result.Events,
            item => item.StepId == "copy-disk-full"
                    && item.Kind == WorkerEventKind.StandardError
                    && System.Text.Encoding.UTF8.GetString(item.RawBytes.Span)
                        .Contains("ERROR 112", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiStepRunContinuesAfterAcceptedRoboCopyBitmaskExit()
    {
        var host = CreateHost();
        var toolId = new ToolId(ToolProcessExitPolicy.RoboCopyToolId);
        var firstBase = CreateCommandRequest(
            ["/d", "/c", "exit", "/b", "7"]);
        var first = firstBase with
        {
            StepId = "copy-part-1",
            Invocation = firstBase.Invocation with { ToolId = toolId },
            ExpectedTool = firstBase.ExpectedTool with { ToolId = toolId }
        };
        var secondBase = CreateCommandRequest(
            ["/d", "/c", "echo", "COPY_PART_2"]);
        var second = secondBase with
        {
            RunId = first.RunId,
            StepId = "copy-part-2",
            Invocation = secondBase.Invocation with { ToolId = toolId },
            ExpectedTool = secondBase.ExpectedTool with { ToolId = toolId }
        };

        var result = await host.RunAsync(
            [first, second],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(2, result.ToolResults.Count);
        Assert.Equal(7, result.ToolResults[0].Audit.ExitCode);
        Assert.Equal(0, result.ToolResults[1].Audit.ExitCode);
        Assert.Contains(
            result.Events,
            item => item.StepId == "copy-part-2"
                    && item.Kind == WorkerEventKind.StandardOutput);
    }

    [Fact]
    public async Task ManifestEntriesRunAsSeparateExternalRoboCopyProcesses()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WinPool.CopyBatch.RealRoboCopy",
            Guid.NewGuid().ToString("N"));
        var runDirectory = Path.Combine(root, "run");
        var sourceRoot = Path.Combine(runDirectory, "source");
        var destinationRoot = Path.Combine(runDirectory, "destination");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "nested"));
        Directory.CreateDirectory(destinationRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "first.txt"),
                "first");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "nested", "second.txt"),
                "second");
            var workspacePlan = new TestWorkspacePlan(
                root,
                runDirectory,
                [],
                1024 * 1024,
                TestWorkspaceCleanupPolicy.KeepAll,
                DateTimeOffset.UtcNow.AddHours(1))
            {
                RegisteredDirectories =
                [
                    new("run/source", 1024 * 1024, 10, new string('a', 64)),
                    new("run/destination", 1024 * 1024, 10, new string('b', 64))
                ]
            };
            var workspace = new AuthorizedTestWorkspace(workspacePlan);
            var step = new TestStep(
                "copy-directory",
                TestActionKind.Copy,
                new ToolId(ToolProcessExitPolicy.RoboCopyToolId),
                null,
                new Dictionary<string, TestParameter>
                {
                    ["sourceRelativeDirectory"] = Text(
                        "sourceRelativeDirectory",
                        "run/source"),
                    ["destinationRelativeDirectory"] = Text(
                        "destinationRelativeDirectory",
                        "run/destination")
                },
                [],
                true);
            var runId = TestRunId.New();
            var now = DateTimeOffset.UtcNow;
            var material = new CopyBatchManifest(
                runId,
                step.Id,
                new string('c', 64),
                new string('a', 64),
                new string('b', 64),
                1024,
                10,
                [
                    new(0, 1, "first.txt", 5, 0, FileAttributes.Normal, null),
                    new(1, 1, Path.Combine("nested", "second.txt"), 6, 0, FileAttributes.Normal, null)
                ],
                [new(1, 11, 2)],
                new("ALG-COPY-BATCH-TEST", "1.0.0", AlgorithmConfidence.Derived, "test"),
                now,
                string.Empty);
            var manifest = material with
            {
                ManifestHash = CopyBatchManifestHash.Compute(material)
            };
            var roboCopyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "robocopy.exe");
            var tool = new ToolState(
                step.ToolId!.Value,
                ToolAvailability.Available,
                roboCopyPath,
                ToolPathSource.WindowsComponent,
                null,
                null,
                null,
                ToolCapabilities.FileCopy,
                false);
            var group = Assert.Single(
                new CopyBatchInvocationPlanner().Build(
                    manifest,
                    [
                        new(runId, step.Id, 0, CopyBatchEntryState.Pending, 0, null, null, now),
                        new(runId, step.Id, 1, CopyBatchEntryState.Pending, 0, null, null, now)
                    ],
                    step,
                    workspace,
                    tool,
                    new RoboCopyAdapter(roboCopyPath),
                    CorrelationId.New()));

            var result = await CreateHost().RunAsync(
                group.Items.Select(item => item.Request).ToArray(),
                null,
                null,
                CancellationToken.None);

            Assert.Equal(2, result.ToolResults.Count);
            Assert.All(
                result.ToolResults,
                item => Assert.True(ToolProcessExitPolicy.IsAccepted(
                    item.Audit.ToolId,
                    item.Audit.ExitCode)));
            Assert.Equal(
                "first",
                await File.ReadAllTextAsync(
                    Path.Combine(destinationRoot, "first.txt")));
            Assert.Equal(
                "second",
                await File.ReadAllTextAsync(
                    Path.Combine(destinationRoot, "nested", "second.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionCallbackRunsWhileRegisteredWorkerIsStillAlive()
    {
        var host = CreateHost();
        var request = CreateCommandRequest(
            ["/d", "/c", "echo", "COMPLETION_SCOPE"]);
        var callbackProcessId = 0;
        var callbackObservedAlive = false;

        var result = await host.RunAsync(
            [request],
            null,
            null,
            (processId, _) =>
            {
                callbackProcessId = processId;
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                callbackObservedAlive = !process.HasExited;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(result.WorkerProcessId, callbackProcessId);
        Assert.True(callbackObservedAlive);
        Assert.True(await WaitForExitAsync(callbackProcessId));
    }

    private static TestWorkerProcessHost CreateHost()
    {
        var workerPath = Path.Combine(
            Path.GetDirectoryName(typeof(ExternalToolProcessRunner).Assembly.Location)!,
            "WinPool.TestWorker.exe");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID unavailable.");
        return new(
            workerPath,
            IpcIdentity.HashUserSid(sid),
            Environment.ProcessId);
    }

    private static ToolProcessRequest CreateCommandRequest(
        IReadOnlyList<string> arguments)
    {
        var commandPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var toolId = new ToolId("winpool.controlled-cmd");
        return new(
            TestRunId.New(),
            "command",
            new(
                toolId,
                commandPath,
                arguments,
                Path.GetDirectoryName(commandPath)!,
                new Dictionary<string, string>(),
                ToolOutputEncoding.Utf8,
                TimeSpan.FromSeconds(40)),
            new(
                toolId,
                ToolAvailability.Available,
                commandPath,
                ToolPathSource.WindowsComponent,
                null,
                null,
                null,
                ToolCapabilities.StructuredOutput,
                false),
            TimeSpan.FromMilliseconds(100));
    }

    private static TestParameter Text(string name, string value) =>
        new(name, TestParameterKind.Text, value, $"test.{name}");

    private static async Task<bool> WaitForExitAsync(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
