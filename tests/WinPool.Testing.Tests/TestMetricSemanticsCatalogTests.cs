using WinPool.Application;
using WinPool.Domain;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class TestMetricSemanticsCatalogTests
{
    [Fact]
    public void DiskSpdAndFioShareIdentityOnlyForExactWorkloadContext()
    {
        Assert.Equal("ALG-METRIC-003", TestMetricSemanticsCatalog.Algorithm.Id);
        var read = Step(IoAccessPattern.Random, 0, 4096, 32, 4);
        var same = TestMetricSemanticsCatalog.Describe(
            read,
            "throughput.total",
            "MiB/s");
        var fioSame = TestMetricSemanticsCatalog.Describe(
            read with { ToolId = new ToolId("fio") },
            "throughput.total",
            "MiB/s");
        var differentQueue = TestMetricSemanticsCatalog.Describe(
            Step(IoAccessPattern.Random, 0, 4096, 1, 4),
            "throughput.total",
            "MiB/s");

        Assert.Equal("throughput.read", same.CanonicalMetricId);
        Assert.True(TestMetricSemanticsCatalog.CanCompare(same, fioSame));
        Assert.False(TestMetricSemanticsCatalog.CanCompare(
            same,
            differentQueue));
        var decimalMegabytes = TestMetricSemanticsCatalog.Describe(
            read,
            "throughput.total",
            "MB/s");
        Assert.False(TestMetricSemanticsCatalog.CanCompare(
            same,
            decimalMegabytes));
        Assert.Equal(
            "metric.unit_conversion_required",
            decimalMegabytes.LimitationCode);
    }

    [Fact]
    public void DiteHeaderMapsWorkloadButStaysFencedWithoutCacheProfile()
    {
        var legacy = TestMetricSemanticsCatalog.DescribeLegacy(
            "Read_SEQ1M_Q8T1_MiB/s",
            "MiB/s");
        var modern = TestMetricSemanticsCatalog.Describe(
            Step(IoAccessPattern.Sequential, 0, 1024 * 1024, 8, 1),
            "throughput.total",
            "MiB/s");

        Assert.Equal("throughput.read", legacy.CanonicalMetricId);
        Assert.Contains("block=1048576", legacy.WorkloadKey);
        Assert.Equal(
            "metric.legacy_cache_profile_unknown",
            legacy.LimitationCode);
        Assert.False(TestMetricSemanticsCatalog.CanCompare(legacy, modern));
    }

    [Fact]
    public void CopySpeedNeverMasqueradesAsIoBenchmarkThroughput()
    {
        var copy = TestMetricSemanticsCatalog.DescribeLegacy(
            "Speed_MiB/s",
            "MiB/s");
        var io = TestMetricSemanticsCatalog.Describe(
            Step(IoAccessPattern.Sequential, 0, 1024 * 1024, 8, 1),
            "throughput.total",
            "MiB/s");

        Assert.Equal("throughput.copy", copy.CanonicalMetricId);
        Assert.NotEqual(copy.CanonicalMetricId, io.CanonicalMetricId);
        Assert.False(TestMetricSemanticsCatalog.CanCompare(copy, io));
    }

    private static TestStep Step(
        IoAccessPattern pattern,
        int writePercentage,
        int blockSize,
        int queueDepth,
        int threads) =>
        new(
            "io",
            TestActionKind.RunIo,
            new ToolId("microsoft.diskspd"),
            new(
                1024 * 1024,
                blockSize,
                threads,
                queueDepth,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                pattern,
                writePercentage,
                SoftwareCacheMode.Enabled,
                WriteThroughMode.Disabled,
                true),
            new Dictionary<string, TestParameter>(),
            [],
            true);
}
