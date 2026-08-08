using System.Text;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class DiteLegacyResultImporterTests
{
    [Fact]
    public async Task ImportsBilingualWideCsvAndComputesMetricSummary()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"Dite-{Guid.NewGuid():N}.csv");
        try
        {
            var content =
                """
                测试时间 | TestTime,盘符 | Drive,工具 | Tool,预设 | Profile,顺序读取 | Read_SEQ1M_Q8T1_MiB/s,日志文件名 | LogFileName
                2026-07-01 10:00:00,H:,DiskSpd,"Realistic, cache",100.5,first.log
                2026-07-01 11:00:00,H:,DiskSpd,Realistic,120.5,second.log
                """;
            await File.WriteAllTextAsync(
                path,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await new DiteLegacyResultImporter()
                .ImportAsync(path, CancellationToken.None);

            Assert.Equal(2, result.Runs.Count);
            Assert.Equal("Realistic, cache", result.Runs[0].Profile);
            Assert.Equal(64, result.SourceSha256.Length);
            var summary = Assert.Single(result.Summaries);
            Assert.Equal("Read_SEQ1M_Q8T1_MiB/s", summary.MetricId);
            Assert.Equal(100.5, summary.Minimum);
            Assert.Equal(110.5, summary.Median);
            Assert.Equal(120.5, summary.Maximum);
            Assert.Equal(
                "throughput.read",
                summary.Semantic?.CanonicalMetricId);
            Assert.False(summary.Semantic!.ComparableAcrossTools);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RejectsMalformedOrNonCsvInputWithoutFollowingLogReferences()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"Dite-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "TestTime,Drive,Tool,Profile\n\"unterminated",
                Encoding.UTF8);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DiteLegacyResultImporter()
                    .ImportAsync(path, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DiteLegacyResultImporter()
                    .ImportAsync(
                        Path.ChangeExtension(path, ".log"),
                        CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
