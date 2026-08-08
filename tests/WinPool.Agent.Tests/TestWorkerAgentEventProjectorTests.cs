using System.Text;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class TestWorkerAgentEventProjectorTests
{
    [Fact]
    public void ProjectsWorkerOutputToRedactedTypedProgressEvent()
    {
        var runId = new TestRunId(Guid.NewGuid());
        var toolId = new ToolId("fio");
        var request = new ToolProcessRequest(
            runId,
            "fio-step",
            new ToolInvocation(
                toolId,
                Path.Combine(Path.GetTempPath(), "fio.exe"),
                [],
                Path.GetTempPath(),
                new Dictionary<string, string>(),
                ToolOutputEncoding.Utf8,
                TimeSpan.FromMinutes(1)),
            new ToolState(
                toolId,
                ToolAvailability.Available,
                Path.Combine(Path.GetTempPath(), "fio.exe"),
                ToolPathSource.CustomPath,
                "3.42",
                new string('a', 64),
                null,
                ToolCapabilities.SequentialIo,
                false),
            TimeSpan.FromSeconds(3));
        var projector = new TestWorkerAgentEventProjector(
            runId,
            CorrelationId.New(),
            [request]);

        var projected = projector.ProjectNativeProgress(
            new WorkerEvent(
                runId,
                "fio-step",
                WorkerEventKind.StandardError,
                WorkerEventImportance.Progress,
                DateTimeOffset.UtcNow,
                "tool.process.stderr",
                Encoding.UTF8.GetBytes(
                    "C:\\private\\target [r(1)][37.5%][eta 00m:05s]")));

        Assert.NotNull(projected);
        Assert.Equal(0.375, projected.TestEvent.TaskEvent.ProgressFraction);
        Assert.Equal("fio-step", projected.TestEvent.TaskEvent.StepId);
        Assert.Equal(string.Empty, projected.TestEvent.TaskEvent.DiagnosticText);
        Assert.DoesNotContain(
            "private",
            projected.TestEvent.TaskEvent.Code,
            StringComparison.OrdinalIgnoreCase);
    }
}
