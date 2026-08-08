using System.Text;
using WinPool.Application;

namespace WinPool.Testing.Tools.Tests;

public sealed class ToolNativeProgressParserTests
{
    [Fact]
    public void ParsesSplitNativePercentageWithoutForwardingOriginalPath()
    {
        var parser = new ToolNativeProgressParser();
        var runId = new TestRunId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var first = Event(runId, "step", "C:\\sensitive\\file 4", now);
        var second = Event(runId, "step", "2.5% copied", now.AddSeconds(1));

        Assert.Null(parser.Consume(first, new ToolId("windows.robocopy"), ToolOutputEncoding.Oem));
        var progress = parser.Consume(
            second,
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Oem);

        Assert.NotNull(progress);
        Assert.Equal(0.425, progress.Fraction, 6);
        Assert.DoesNotContain("sensitive", progress.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("tool.progress.windows.robocopy.native", progress.Code);
    }

    [Fact]
    public void RejectsInvalidAndDeduplicatesRapidPercentages()
    {
        var parser = new ToolNativeProgressParser();
        var runId = new TestRunId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        Assert.Null(parser.Consume(
            Event(runId, "step", "101%", now),
            new ToolId("fio"),
            ToolOutputEncoding.Utf8));
        Assert.NotNull(parser.Consume(
            Event(runId, "step", "10%", now.AddSeconds(2)),
            new ToolId("fio"),
            ToolOutputEncoding.Utf8));
        Assert.Null(parser.Consume(
            Event(runId, "step", "10%", now.AddSeconds(2).AddMilliseconds(5)),
            new ToolId("fio"),
            ToolOutputEncoding.Utf8));
        Assert.Null(parser.Consume(
            Event(runId, "step", "11%", now.AddSeconds(2).AddMilliseconds(10)),
            new ToolId("fio"),
            ToolOutputEncoding.Utf8));
        Assert.NotNull(parser.Consume(
            Event(runId, "step", "100%", now.AddSeconds(2).AddMilliseconds(20)),
            new ToolId("fio"),
            ToolOutputEncoding.Utf8));
    }

    private static WorkerEvent Event(
        TestRunId runId,
        string stepId,
        string text,
        DateTimeOffset occurredAtUtc) =>
        new(
            runId,
            stepId,
            WorkerEventKind.StandardOutput,
            WorkerEventImportance.Progress,
            occurredAtUtc,
            "tool.process.stdout",
            Encoding.UTF8.GetBytes(text));
}
