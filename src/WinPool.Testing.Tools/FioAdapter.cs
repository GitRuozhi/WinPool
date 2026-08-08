using System.Globalization;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed class FioAdapter : IExternalToolAdapter
{
    private readonly string _executablePath;

    public FioAdapter(string executablePath)
    {
        _executablePath = ToolAdapterSupport.ValidateExecutable(
            executablePath,
            "fio.exe");
    }

    public ToolId ToolId => ToolIds.Fio;

    public ToolCapabilities Capabilities =>
        ToolCapabilities.SequentialIo
        | ToolCapabilities.RandomIo
        | ToolCapabilities.MixedIo
        | ToolCapabilities.FileGeneration
        | ToolCapabilities.FileVerification
        | ToolCapabilities.LatencyMetrics
        | ToolCapabilities.StructuredOutput;

    public ApplicationResult<ToolInvocation> BuildInvocation(
        TestStep step,
        AuthorizedTestWorkspace workspace,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            if (step.ToolId != ToolId
                || step.Action is not (TestActionKind.RunIo or TestActionKind.GenerateFile))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.action.unsupported",
                    "fio only accepts typed file I/O or generation steps.");
            }

            var workload = step.Workload
                ?? throw new ToolAdapterValidationException(
                    "tool.adapter.workload.required",
                    "fio requires a typed workload.");
            ValidateWorkload(workload);
            if (workload.Cooldown != TimeSpan.Zero)
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.workload.cooldown_unsupported",
                    "fio cooldown must be represented as a separate schedule step.");
            }

            var targetPath = ToolAdapterSupport.ResolveRegisteredFile(
                workspace,
                ToolAdapterSupport.RequireParameter(step, "targetRelativePath"));
            var duration = ToolAdapterSupport.WholeSeconds(
                workload.Duration,
                "duration");
            var warmup = ToolAdapterSupport.WholeSeconds(
                workload.Warmup,
                "warmup");

            var arguments = new List<string>
            {
                "--name=winpool",
                $"--filename={targetPath}",
                $"--size={workload.FileSizeBytes.ToString(CultureInfo.InvariantCulture)}",
                $"--bs={workload.BlockSizeBytes.ToString(CultureInfo.InvariantCulture)}",
                $"--numjobs={workload.ThreadCount.ToString(CultureInfo.InvariantCulture)}",
                $"--iodepth={workload.QueueDepth.ToString(CultureInfo.InvariantCulture)}",
                $"--runtime={duration.ToString(CultureInfo.InvariantCulture)}",
                $"--ramp_time={warmup.ToString(CultureInfo.InvariantCulture)}",
                "--time_based=1",
                "--ioengine=windowsaio",
                "--group_reporting=1",
                "--eta=always",
                "--eta-interval=1s",
                "--output-format=json+",
                $"--direct={(workload.SoftwareCache is SoftwareCacheMode.Disabled ? 1 : 0)}",
                $"--sync={(workload.WriteThrough is WriteThroughMode.Enabled ? 1 : 0)}",
                $"--rw={MapAccessPattern(workload)}"
            };
            if (workload.WritePercentage is > 0 and < 100)
            {
                arguments.Add(
                    $"--rwmixwrite={workload.WritePercentage.ToString(CultureInfo.InvariantCulture)}");
            }

            arguments.Add(
                $"--clat_percentiles={(workload.CollectLatency ? 1 : 0)}");
            if (workload.CollectLatency)
            {
                arguments.Add("--percentile_list=50:90:95:99:99.9");
            }

            return ApplicationResult<ToolInvocation>.Succeeded(
                new ToolInvocation(
                    ToolId,
                    _executablePath,
                    arguments.AsReadOnly(),
                    ToolAdapterSupport.ValidateWorkingDirectory(workspace),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ToolOutputEncoding.Utf8,
                    workload.Warmup + workload.Duration + TimeSpan.FromMinutes(2)),
                correlationId);
        }
        catch (ToolAdapterValidationException exception)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                exception.Code,
                exception.Message);
        }
    }

    public IAsyncEnumerable<ToolEvent> ParseAsync(
        ToolProcessStreams streams,
        CancellationToken cancellationToken) =>
        ToolAdapterSupport.ParseStructuredAsync(
            ToolId,
            streams,
            FioJsonPlusParser.Parse,
            cancellationToken);

    private static string MapAccessPattern(TestWorkload workload) =>
        (workload.AccessPattern, workload.WritePercentage) switch
        {
            (IoAccessPattern.Sequential, 0) => "read",
            (IoAccessPattern.Sequential, 100) => "write",
            (IoAccessPattern.Sequential, _) => "rw",
            (IoAccessPattern.Random, 0) => "randread",
            (IoAccessPattern.Random, 100) => "randwrite",
            (IoAccessPattern.Random, _) => "randrw",
            (IoAccessPattern.Mixed, _) => "randrw",
            _ => throw new ToolAdapterValidationException(
                "tool.adapter.workload.access_pattern_invalid",
                "The fio access pattern is unsupported.")
        };

    private static void ValidateWorkload(TestWorkload workload)
    {
        if (workload.FileSizeBytes <= 0
            || workload.BlockSizeBytes <= 0
            || workload.ThreadCount is < 1 or > 256
            || workload.QueueDepth is < 1 or > 1024
            || workload.Duration <= TimeSpan.Zero
            || workload.WritePercentage is < 0 or > 100)
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.workload.invalid",
                "The fio workload is outside the supported range.");
        }
    }
}

public static class FioJsonPlusParser
{
    private static readonly IReadOnlyDictionary<string, string> PercentileIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["50.000000"] = "p50",
            ["90.000000"] = "p90",
            ["95.000000"] = "p95",
            ["99.000000"] = "p99",
            ["99.900000"] = "p99.9"
        };

    public static ParsedToolOutput Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("jobs", out var jobs)
            || jobs.ValueKind is not JsonValueKind.Array
            || jobs.GetArrayLength() == 0)
        {
            throw new FormatException("fio JSON+ contains no jobs.");
        }

        double throughputBytesPerSecond = 0;
        double iops = 0;
        double completedBytes = 0;
        double errors = 0;
        var metrics = new List<NormalizedToolMetric>();
        var buckets = new Dictionary<(string Operation, long Upper), long>();

        foreach (var job in jobs.EnumerateArray())
        {
            if (job.TryGetProperty("error", out var error)
                && error.TryGetDouble(out var errorValue)
                && errorValue != 0)
            {
                errors++;
            }

            foreach (var operationName in new[] { "read", "write", "trim" })
            {
                if (!job.TryGetProperty(operationName, out var operation)
                    || operation.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                throughputBytesPerSecond += ReadBandwidthBytes(operation);
                iops += ReadDouble(operation, "iops") ?? 0;
                completedBytes += ReadDouble(operation, "io_bytes") ?? 0;
                AddLatencyMetrics(metrics, buckets, operationName, operation);
            }
        }

        metrics.Insert(
            0,
            new NormalizedToolMetric(
                "throughput.total",
                throughputBytesPerSecond / 1048576d,
                "MiB/s"));
        metrics.Insert(1, new NormalizedToolMetric("iops.total", iops, "IOPS"));
        metrics.Insert(2, new NormalizedToolMetric("bytes.completed", completedBytes, "B"));
        metrics.Insert(3, new NormalizedToolMetric("errors.total", errors, "count"));

        var limitations = jobs.GetArrayLength() > 1
            ? new[] { "fio.latency.per_job_not_aggregated" }
            : [];
        return new ParsedToolOutput(
            metrics,
            buckets
                .OrderBy(pair => pair.Key.Operation, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Upper)
                .Select(pair => new TestLatencyHistogramBucket(
                    pair.Key.Operation,
                    pair.Key.Upper,
                    pair.Value))
                .ToArray(),
            limitations);
    }

    private static double ReadBandwidthBytes(JsonElement operation)
    {
        var bytes = ReadDouble(operation, "bw_bytes");
        if (bytes.HasValue)
        {
            return bytes.Value;
        }

        return (ReadDouble(operation, "bw") ?? 0) * 1024d;
    }

    private static void AddLatencyMetrics(
        ICollection<NormalizedToolMetric> metrics,
        IDictionary<(string Operation, long Upper), long> buckets,
        string operationName,
        JsonElement operation)
    {
        var (latency, multiplier) = FindLatencyObject(operation);
        if (latency.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var mean = ReadDouble(latency, "mean");
        if (mean.HasValue)
        {
            metrics.Add(
                new NormalizedToolMetric(
                    $"latency.{operationName}.average",
                    mean.Value * multiplier / 1_000_000d,
                    "ms"));
        }

        if (latency.TryGetProperty("percentile", out var percentiles)
            && percentiles.ValueKind is JsonValueKind.Object)
        {
            foreach (var percentile in percentiles.EnumerateObject())
            {
                if (PercentileIds.TryGetValue(percentile.Name, out var id)
                    && percentile.Value.TryGetDouble(out var value))
                {
                    metrics.Add(
                        new NormalizedToolMetric(
                            $"latency.{operationName}.{id}",
                            value * multiplier / 1_000_000d,
                            "ms"));
                }
            }
        }

        if (latency.TryGetProperty("bins", out var bins)
            && bins.ValueKind is JsonValueKind.Object)
        {
            foreach (var bin in bins.EnumerateObject())
            {
                if (long.TryParse(
                        bin.Name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var upper)
                    && bin.Value.TryGetInt64(out var count))
                {
                    var upperNanoseconds = checked((long)(upper * multiplier));
                    var key = (operationName, upperNanoseconds);
                    buckets[key] = checked(
                        buckets.TryGetValue(key, out var current)
                            ? current + count
                            : count);
                }
            }
        }
    }

    private static (JsonElement Element, double NanosecondMultiplier)
        FindLatencyObject(JsonElement operation)
    {
        foreach (var candidate in new[]
                 {
                     ("clat_ns", 1d),
                     ("lat_ns", 1d),
                     ("clat_us", 1_000d),
                     ("lat_us", 1_000d),
                     ("clat_ms", 1_000_000d),
                     ("lat_ms", 1_000_000d)
                 })
        {
            if (operation.TryGetProperty(candidate.Item1, out var element))
            {
                return (element, candidate.Item2);
            }
        }

        return (default, 1d);
    }

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.TryGetDouble(out var value)
            ? value
            : null;
}
