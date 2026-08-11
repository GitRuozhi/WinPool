using System.Text;
using WinPool.Application;

namespace WinPool.Testing.Tools.Tests;

public sealed class ToolNativeProgressParserTests
{
    [Fact]
    public void StatefulDecoderPreservesDbcsAcrossChunksAndFlushesInvalidTail()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(936);
        var bytes = encoding.GetBytes("复制 42.5%");
        var split = Array.FindIndex(bytes, value => value >= 0x80) + 1;
        var decoder = new ToolOutputTextDecoder(936);

        var text = decoder.Decode(bytes.AsSpan(0, split))
                   + decoder.Decode(bytes.AsSpan(split))
                   + decoder.Complete();

        Assert.Equal("复制 42.5%", text);

        var invalid = new ToolOutputTextDecoder(936);
        Assert.Equal(string.Empty, invalid.Decode([0xB8]));
        Assert.Contains('\uFFFD', invalid.Complete());
    }

    [Fact]
    public void ParsesDbcsProgressSplitInsideChineseCharacter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var parser = new ToolNativeProgressParser();
        var runId = TestRunId.New();
        var now = DateTimeOffset.UtcNow;
        var bytes = Encoding.GetEncoding(936).GetBytes("已复制 42.5%");
        var split = Array.FindIndex(bytes, value => value >= 0x80) + 1;
        var first = RawEvent(runId, bytes[..split], now, 936);
        var second = RawEvent(runId, bytes[split..], now.AddSeconds(1), 936);

        Assert.Null(parser.Consume(
            first,
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Oem));
        var progress = parser.Consume(
            second,
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Oem);

        Assert.NotNull(progress);
        Assert.Equal(0.425, progress.Fraction, 6);
    }

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

    [Fact]
    public void DoesNotJoinTokensAcrossStandardOutputAndStandardError()
    {
        var parser = new ToolNativeProgressParser();
        var runId = TestRunId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(parser.Consume(
            StreamEvent(runId, "step", WorkerEventKind.StandardOutput, "50", now),
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Utf8));
        Assert.Null(parser.Consume(
            StreamEvent(runId, "step", WorkerEventKind.StandardError, "%", now.AddSeconds(1)),
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Utf8));
    }

    [Fact]
    public void CompleteFlushesBothDecodersAndRemovesInvocationState()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var parser = new ToolNativeProgressParser();
        var runId = TestRunId.New();
        var now = DateTimeOffset.UtcNow;
        var bytes = Encoding.GetEncoding(936).GetBytes("已复制 42.5%");

        Assert.Null(parser.Consume(
            RawEvent(runId, bytes[..1], now, 936),
            new ToolId("windows.robocopy"),
            ToolOutputEncoding.Oem));
        Assert.Null(parser.Complete(
            runId,
            "step",
            new ToolId("windows.robocopy"),
            now.AddSeconds(1)));
        Assert.Null(parser.Consume(
            Event(runId, "step", "%", now.AddSeconds(2)),
            new ToolId("windows.robocopy"),
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

    private static WorkerEvent StreamEvent(
        TestRunId runId,
        string stepId,
        WorkerEventKind kind,
        string text,
        DateTimeOffset occurredAtUtc) =>
        new(
            runId,
            stepId,
            kind,
            WorkerEventImportance.Progress,
            occurredAtUtc,
            kind == WorkerEventKind.StandardOutput
                ? "tool.process.stdout"
                : "tool.process.stderr",
            Encoding.UTF8.GetBytes(text));

    private static WorkerEvent RawEvent(
        TestRunId runId,
        byte[] bytes,
        DateTimeOffset occurredAtUtc,
        int codePage) =>
        new(
            runId,
            "step",
            WorkerEventKind.StandardOutput,
            WorkerEventImportance.Progress,
            occurredAtUtc,
            "tool.process.stdout",
            bytes,
            OutputCodePage: codePage);
}
