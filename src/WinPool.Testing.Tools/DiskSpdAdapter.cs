using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed class DiskSpdAdapter : IExternalToolAdapter
{
    private readonly string _executablePath;

    public DiskSpdAdapter(string executablePath)
    {
        _executablePath = ToolAdapterSupport.ValidateExecutable(
            executablePath,
            "diskspd.exe");
    }

    public ToolId ToolId => ToolIds.DiskSpd;

    public ToolCapabilities Capabilities =>
        ToolCapabilities.SequentialIo
        | ToolCapabilities.RandomIo
        | ToolCapabilities.MixedIo
        | ToolCapabilities.FileGeneration
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
                    "DiskSpd only accepts typed file I/O or generation steps.");
            }

            var workload = step.Workload
                ?? throw new ToolAdapterValidationException(
                    "tool.adapter.workload.required",
                    "DiskSpd requires a typed workload.");
            ValidateWorkload(workload);

            var relativePath = ToolAdapterSupport.RequireParameter(
                step,
                "targetRelativePath");
            var targetPath = ToolAdapterSupport.ResolveRegisteredFile(
                workspace,
                relativePath);
            var workingDirectory =
                ToolAdapterSupport.ValidateWorkingDirectory(workspace);

            var duration = ToolAdapterSupport.WholeSeconds(
                workload.Duration,
                "duration");
            var warmup = ToolAdapterSupport.WholeSeconds(
                workload.Warmup,
                "warmup");
            var cooldown = ToolAdapterSupport.WholeSeconds(
                workload.Cooldown,
                "cooldown");

            var arguments = new List<string>
            {
                $"-c{workload.FileSizeBytes.ToString(CultureInfo.InvariantCulture)}",
                $"-b{workload.BlockSizeBytes.ToString(CultureInfo.InvariantCulture)}"
            };
            if (workload.AccessPattern is IoAccessPattern.Random or IoAccessPattern.Mixed)
            {
                arguments.Add("-r");
            }

            arguments.Add($"-o{workload.QueueDepth.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add($"-t{workload.ThreadCount.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add($"-d{duration.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add(MapCacheFlag(workload));
            arguments.Add($"-w{workload.WritePercentage.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add($"-W{warmup.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add($"-C{cooldown.ToString(CultureInfo.InvariantCulture)}");
            if (workload.CollectLatency)
            {
                arguments.Add("-L");
            }

            arguments.Add("-Rxml");
            arguments.Add(targetPath);

            return ApplicationResult<ToolInvocation>.Succeeded(
                new ToolInvocation(
                    ToolId,
                    _executablePath,
                    arguments.AsReadOnly(),
                    workingDirectory,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ToolOutputEncoding.Utf8,
                    workload.Warmup
                    + workload.Duration
                    + workload.Cooldown
                    + TimeSpan.FromMinutes(2)),
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
            DiskSpdXmlParser.Parse,
            cancellationToken);

    private static string MapCacheFlag(TestWorkload workload) =>
        (workload.SoftwareCache, workload.WriteThrough) switch
        {
            (SoftwareCacheMode.Enabled, WriteThroughMode.Disabled) => "-Sb",
            (SoftwareCacheMode.Disabled, WriteThroughMode.Disabled) => "-Su",
            (SoftwareCacheMode.Enabled, WriteThroughMode.Enabled) => "-Sw",
            (SoftwareCacheMode.Disabled, WriteThroughMode.Enabled) => "-Sh",
            _ => throw new ToolAdapterValidationException(
                "tool.adapter.workload.cache_mode_invalid",
                "The DiskSpd cache mode is unsupported.")
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
                "The DiskSpd workload is outside the supported range.");
        }
    }
}

public static class DiskSpdXmlParser
{
    public static ParsedToolOutput Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(
            stringReader,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
        var document = XDocument.Load(xmlReader, LoadOptions.None);

        var seconds = FirstDouble(document, "TestTimeSeconds");
        if (seconds is null or <= 0)
        {
            throw new FormatException("DiskSpd XML has no positive measurement duration.");
        }

        var targets = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase)
                && element.Parent?.Name.LocalName.Equals(
                    "Thread",
                    StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (targets.Length == 0)
        {
            targets = document
                .Descendants()
                .Where(element =>
                    element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase)
                    && ChildDouble(element, "ReadBytes").HasValue)
                .ToArray();
        }

        if (targets.Length == 0)
        {
            throw new FormatException("DiskSpd XML contains no thread target results.");
        }

        var readBytes = Sum(targets, "ReadBytes");
        var writeBytes = Sum(targets, "WriteBytes");
        var readOperations = Sum(targets, "ReadCount");
        var writeOperations = Sum(targets, "WriteCount");
        var metrics = new List<NormalizedToolMetric>
        {
            new("bytes.read", readBytes, "B"),
            new("bytes.write", writeBytes, "B"),
            new(
                "throughput.total",
                (readBytes + writeBytes) / seconds.Value / 1048576d,
                "MiB/s"),
            new(
                "iops.total",
                (readOperations + writeOperations) / seconds.Value,
                "IOPS")
        };

        AddWeightedAverageLatency(metrics, targets);
        AddLatencyPercentiles(metrics, document);
        return new ParsedToolOutput(metrics, [], []);
    }

    private static void AddWeightedAverageLatency(
        ICollection<NormalizedToolMetric> metrics,
        IReadOnlyList<XElement> targets)
    {
        double weightedMilliseconds = 0;
        double operationCount = 0;
        foreach (var target in targets)
        {
            var readCount = ChildDouble(target, "ReadCount") ?? 0;
            var writeCount = ChildDouble(target, "WriteCount") ?? 0;
            var readLatency = ChildDouble(
                target,
                "AverageReadLatencyMilliseconds");
            var writeLatency = ChildDouble(
                target,
                "AverageWriteLatencyMilliseconds");
            if (readLatency.HasValue && readCount > 0)
            {
                weightedMilliseconds += readLatency.Value * readCount;
                operationCount += readCount;
            }

            if (writeLatency.HasValue && writeCount > 0)
            {
                weightedMilliseconds += writeLatency.Value * writeCount;
                operationCount += writeCount;
            }
        }

        if (operationCount > 0)
        {
            metrics.Add(
                new NormalizedToolMetric(
                    "latency.average",
                    weightedMilliseconds / operationCount,
                    "ms"));
        }
    }

    private static void AddLatencyPercentiles(
        ICollection<NormalizedToolMetric> metrics,
        XContainer document)
    {
        var requested = new Dictionary<double, string>
        {
            [50d] = "latency.p50",
            [90d] = "latency.p90",
            [95d] = "latency.p95",
            [99d] = "latency.p99",
            [99.9d] = "latency.p99.9"
        };
        foreach (var bucket in document.Descendants().Where(
                     element => element.Name.LocalName.Equals(
                         "Bucket",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var percentile = AttributeDouble(bucket, "Percentile");
            var latency = AttributeDouble(bucket, "TotalMilliseconds");
            if (percentile.HasValue
                && latency.HasValue
                && requested.TryGetValue(percentile.Value, out var metricId)
                && !metrics.Any(metric => metric.MetricId == metricId))
            {
                metrics.Add(new NormalizedToolMetric(metricId, latency.Value, "ms"));
            }
        }
    }

    private static double Sum(
        IEnumerable<XElement> elements,
        string childName) =>
        elements.Sum(element => ChildDouble(element, childName) ?? 0);

    private static double? FirstDouble(XContainer element, string localName) =>
        element.Descendants()
            .Where(candidate => candidate.Name.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))
            .Select(candidate => ParseDouble(candidate.Value))
            .FirstOrDefault(value => value.HasValue);

    private static double? ChildDouble(XContainer element, string localName) =>
        element.Elements()
            .Where(candidate => candidate.Name.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))
            .Select(candidate => ParseDouble(candidate.Value))
            .FirstOrDefault(value => value.HasValue);

    private static double? AttributeDouble(XElement element, string localName) =>
        element.Attributes()
            .Where(attribute => attribute.Name.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))
            .Select(attribute => ParseDouble(attribute.Value))
            .FirstOrDefault(value => value.HasValue);

    private static double? ParseDouble(string value) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
}
