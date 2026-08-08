using System.Text;
using WinPool.Application;
using WinPool.Testing.Tools;

namespace WinPool.Testing.Tools.Tests;

public sealed class ToolParserTests
{
    [Fact]
    public void DiskSpdXmlNormalizesBytesThroughputIopsAndLatency()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Results>
              <TimeSpan>
                <TestTimeSeconds>2.0</TestTimeSeconds>
                <Thread>
                  <Target>
                    <ReadBytes>1048576</ReadBytes>
                    <WriteBytes>1048576</WriteBytes>
                    <ReadCount>100</ReadCount>
                    <WriteCount>100</WriteCount>
                    <AverageReadLatencyMilliseconds>1.0</AverageReadLatencyMilliseconds>
                    <AverageWriteLatencyMilliseconds>3.0</AverageWriteLatencyMilliseconds>
                  </Target>
                </Thread>
                <Latency>
                  <Bucket Percentile="50" TotalMilliseconds="1.5" />
                  <Bucket Percentile="99" TotalMilliseconds="9.5" />
                  <Bucket Percentile="99.9" TotalMilliseconds="12.5" />
                </Latency>
              </TimeSpan>
            </Results>
            """;

        var parsed = DiskSpdXmlParser.Parse(xml);

        AssertMetric(parsed, "bytes.read", 1048576, "B");
        AssertMetric(parsed, "bytes.write", 1048576, "B");
        AssertMetric(parsed, "throughput.total", 1, "MiB/s");
        AssertMetric(parsed, "iops.total", 100, "IOPS");
        AssertMetric(parsed, "latency.average", 2, "ms");
        AssertMetric(parsed, "latency.p99", 9.5, "ms");
        AssertMetric(parsed, "latency.p99.9", 12.5, "ms");
    }

    [Fact]
    public void DiskSpdXmlRejectsDtd()
    {
        const string xml = """
            <!DOCTYPE Results [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <Results><TimeSpan><TestTimeSeconds>1</TestTimeSeconds></TimeSpan></Results>
            """;

        Assert.Throws<System.Xml.XmlException>(() => DiskSpdXmlParser.Parse(xml));
    }

    [Fact]
    public void FioJsonPlusNormalizesBandwidthIopsErrorsAndNanoseconds()
    {
        const string json = """
            {
              "fio version": "fio-3.39",
              "jobs": [
                {
                  "jobname": "winpool",
                  "error": 0,
                  "read": {
                    "io_bytes": 1048576,
                    "bw_bytes": 1048576,
                    "iops": 256.0,
                    "clat_ns": {
                      "mean": 2000000,
                      "percentile": {
                        "50.000000": 1500000,
                        "99.000000": 9000000,
                        "99.900000": 12000000
                      },
                      "bins": {
                        "1000000": 10,
                        "2000000": 20
                      }
                    }
                  },
                  "write": {
                    "io_bytes": 2097152,
                    "bw_bytes": 2097152,
                    "iops": 512.0,
                    "clat_ns": {
                      "mean": 3000000,
                      "percentile": {
                        "50.000000": 2500000,
                        "99.000000": 10000000
                      }
                    }
                  }
                }
              ]
            }
            """;

        var parsed = FioJsonPlusParser.Parse(json);

        AssertMetric(parsed, "throughput.total", 3, "MiB/s");
        AssertMetric(parsed, "iops.total", 768, "IOPS");
        AssertMetric(parsed, "bytes.completed", 3145728, "B");
        AssertMetric(parsed, "errors.total", 0, "count");
        AssertMetric(parsed, "latency.read.average", 2, "ms");
        AssertMetric(parsed, "latency.read.p99.9", 12, "ms");
        AssertMetric(parsed, "latency.write.p50", 2.5, "ms");
        Assert.Contains(
            parsed.LatencyHistogram,
            bucket => bucket is
            {
                Operation: "read",
                UpperBoundNanoseconds: 2_000_000,
                SampleCount: 20
            });
    }

    [Fact]
    public async Task FioAdapterEmitsHistogramBucketsThroughSharedContract()
    {
        const string json = """
            {"jobs":[{"error":0,
              "read":{"io_bytes":4096,"bw_bytes":4096,"iops":1,
                "clat_ns":{"mean":1000,"bins":{"2000":7}}},
              "write":{"io_bytes":0,"bw_bytes":0,"iops":0,
                "clat_ns":{"mean":0}}}]}
            """;
        var adapter = new FioAdapter(
            Path.Combine(Path.GetTempPath(), "fio.exe"));
        var chunks = Yield(
            new ToolOutputChunk(
                ToolOutputStream.StandardOutput,
                System.Text.Encoding.UTF8.GetBytes(json),
                DateTimeOffset.UtcNow));

        var events = new List<ToolEvent>();
        await foreach (var item in adapter.ParseAsync(
                           new(chunks, Task.FromResult(0)),
                           CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Contains(
            events,
            item => item.HistogramBucket is
            {
                Operation: "read",
                UpperBoundNanoseconds: 2000,
                SampleCount: 7
            });
    }

    [Theory]
    [InlineData(0, true, false, false, false)]
    [InlineData(1, true, true, false, false)]
    [InlineData(3, true, true, true, false)]
    [InlineData(7, true, true, true, true)]
    [InlineData(8, false, false, false, false)]
    [InlineData(16, false, false, false, false)]
    public void RoboCopyDecodesDocumentedExitCodeBits(
        int exitCode,
        bool acceptable,
        bool copied,
        bool extra,
        bool mismatch)
    {
        var decoded = RoboCopyResultEvaluator.DecodeExitCode(exitCode);

        Assert.Equal(acceptable, decoded.IsAcceptable);
        Assert.Equal(copied, decoded.FilesCopied);
        Assert.Equal(extra, decoded.ExtraFilesOrDirectoriesDetected);
        Assert.Equal(mismatch, decoded.MismatchedFilesOrDirectoriesDetected);
    }

    [Fact]
    public void RoboCopyParsesEnglishByteSummaryAndRequiresVerification()
    {
        const string output = """
               Files :         10        10         0         0         0         0
               Bytes :    1048576   1048576         0         0         0         0
               Times :   0:00:02
               Speed :      524288 Bytes/sec.
            """;

        var parsed = RoboCopyOutputParser.Parse(output);
        var failedVerification = RoboCopyResultEvaluator.Evaluate(
            1,
            parsed,
            new CopyVerificationEvidence(true, true, false));
        var verified = RoboCopyResultEvaluator.Evaluate(
            1,
            parsed,
            new CopyVerificationEvidence(true, true, true));

        Assert.Equal(10, parsed.TotalFiles);
        Assert.Equal(1048576, parsed.CopiedBytes);
        Assert.Equal(2, parsed.ElapsedSeconds);
        Assert.Equal(524288, parsed.ReportedBytesPerSecond);
        Assert.False(failedVerification.IsSuccessful);
        Assert.Contains(
            "robocopy.verify.content_failed",
            failedVerification.FailureCodes);
        Assert.True(verified.IsSuccessful);
    }

    [Fact]
    public void RoboCopyParsesChineseSummary()
    {
        const string output = """
               文件 :          2         2         0         0         0         0
               字节 :       4096      4096         0         0         0         0
               时间 :   0:00:01
               速度 :       4096 字节/秒
            """;

        var parsed = RoboCopyOutputParser.Parse(output);

        Assert.Equal(2, parsed.CopiedFiles);
        Assert.Equal(4096, parsed.CopiedBytes);
        Assert.Equal(4096, parsed.ReportedBytesPerSecond);
    }

    [Fact]
    public async Task DiteFileGenParsesStructuredTerminalResultAfterProgressLines()
    {
        const string output = """
            Generating mixed files
              ... 1000/50505 files generated
            {"schema":"Dite.FileGenResult","version":2,"status":"completed","profile":"mixed","file_count":50505,"total_bytes":1048576,"generated_bytes":524288,"elapsed_seconds":2.0,"reused_file_count":25000}
            """;
        var streams = new ToolProcessStreams(
            Yield(
                new(
                    ToolOutputStream.StandardOutput,
                    Encoding.UTF8.GetBytes(output),
                    DateTimeOffset.UtcNow)),
            Task.FromResult(0));
        var events = new List<ToolEvent>();

        await foreach (var item in new DiteFileGenAdapter(@"C:\Tools\Dite.exe")
                           .ParseAsync(streams, CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Contains(
            events,
            item => item.Metric is
            {
                MetricId: "file_count",
                Value: 50505,
                Unit: "files"
            });
        Assert.Contains(
            events,
            item => item.Metric is
            {
                MetricId: "reused_file_count",
                Value: 25000,
                Unit: "files"
            });
        Assert.Contains(
            events,
            item => item.Metric is
            {
                MetricId: "throughput_mib_s",
                Value: 0.25,
                Unit: "MiB/s"
            });
        Assert.Contains(
            events,
            item => item.Kind is ToolEventKind.Completed);
    }

    private static void AssertMetric(
        ParsedToolOutput output,
        string metricId,
        double value,
        string unit)
    {
        var metric = Assert.Single(
            output.Metrics,
            candidate => candidate.MetricId == metricId);
        Assert.Equal(value, metric.Value, precision: 6);
        Assert.Equal(unit, metric.Unit);
    }

    private static async IAsyncEnumerable<ToolOutputChunk> Yield(
        ToolOutputChunk chunk)
    {
        yield return chunk;
        await Task.CompletedTask;
    }
}
