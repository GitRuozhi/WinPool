using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed class DiteFileGenAdapter : IExternalToolAdapter
{
    private readonly string executablePath;

    public DiteFileGenAdapter(string executablePath)
    {
        this.executablePath = ToolAdapterSupport.ValidateExecutable(
            executablePath,
            "Dite.exe");
    }

    public ToolId ToolId => ToolIds.DiteFileGen;

    public ToolCapabilities Capabilities =>
        ToolCapabilities.FileGeneration | ToolCapabilities.StructuredOutput;

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
                || step.Action is not TestActionKind.GenerateFile
                || step.Workload is null)
            {
                throw new ToolAdapterValidationException(
                    "dite.filegen.action.unsupported",
                    "Dite FileGen accepts only typed directory GenerateFile steps.");
            }

            var relativeDirectory = ToolAdapterSupport.RequireParameter(
                step,
                "targetRelativeDirectory");
            var output = ToolAdapterSupport.ResolveRegisteredDirectory(
                workspace,
                relativeDirectory);
            var registration = workspace.Plan.RegisteredDirectories.Single(
                item => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(
                        Path.Combine(
                            workspace.Plan.NormalizedRootDirectory,
                            item.RelativePath)),
                    output));
            if (registration.IdentityToken.Length != 64
                || registration.IdentityToken.Any(
                    character => character is not
                        (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f')))
            {
                throw new ToolAdapterValidationException(
                    "dite.filegen.identity.invalid",
                    "The registered directory does not carry a supported recovery identity.");
            }
            var profile = ToolAdapterSupport.OptionalChoice(
                    step,
                    "profile",
                    "mixed")
                .ToLowerInvariant();
            if (profile is not ("big" or "mixed"))
            {
                throw new ToolAdapterValidationException(
                    "dite.filegen.profile.invalid",
                    "Dite FileGen supports only the reviewed big and mixed profiles.");
            }

            var totalMiB = ToolAdapterSupport.OptionalInteger(
                step,
                "totalMiB",
                checked((int)Math.Max(
                    1,
                    step.Workload.FileSizeBytes / (1024L * 1024L))),
                1,
                1_048_576);
            var targetCount = ToolAdapterSupport.OptionalInteger(
                step,
                "targetCount",
                profile == "mixed" ? 50_505 : 25,
                1,
                200_000);
            var poolMiB = ToolAdapterSupport.OptionalInteger(
                step,
                "poolMiB",
                64,
                1,
                1024);
            var requestedMaximum = DiteFileGenerationBounds.CalculateMaximumBytes(
                totalMiB,
                targetCount);
            if (requestedMaximum > registration.MaximumBytes
                || targetCount + DiteFileGenerationBounds.ManifestFileCount
                    > registration.MaximumFileCount)
            {
                throw new ToolAdapterValidationException(
                    "dite.filegen.quota.exceeded",
                    "The requested FileGen workload exceeds its hash-bound directory quota.");
            }

            var arguments = new[]
            {
                "--filegen-output",
                output,
                "--filegen-profile",
                profile,
                "--filegen-total-mib",
                totalMiB.ToString(CultureInfo.InvariantCulture),
                "--filegen-target-count",
                targetCount.ToString(CultureInfo.InvariantCulture),
                "--filegen-pool-mib",
                poolMiB.ToString(CultureInfo.InvariantCulture),
                "--filegen-identity",
                registration.IdentityToken,
                "--filegen-resume",
                "--no-pause"
            };
            var timeout = step.Workload.Duration + TimeSpan.FromHours(1);
            if (timeout < TimeSpan.FromMinutes(5))
            {
                timeout = TimeSpan.FromMinutes(5);
            }
            if (timeout > TimeSpan.FromHours(24))
            {
                timeout = TimeSpan.FromHours(24);
            }

            return ApplicationResult<ToolInvocation>.Succeeded(
                new(
                    ToolId,
                    executablePath,
                    arguments,
                    ToolAdapterSupport.ValidateWorkingDirectory(workspace),
                    new Dictionary<string, string>(),
                    ToolOutputEncoding.Utf8,
                    timeout),
                correlationId);
        }
        catch (ToolAdapterValidationException exception)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                exception.Code,
                exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                "dite.filegen.registration.invalid",
                exception.Message);
        }
        catch (OverflowException exception)
        {
            return ToolAdapterSupport.Reject(
                correlationId,
                "dite.filegen.parameters.overflow",
                exception.Message);
        }
    }

    public async IAsyncEnumerable<ToolEvent> ParseAsync(
        ToolProcessStreams streams,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        (string StandardOutput, string StandardError, int ExitCode) process;
        try
        {
            process = await ToolAdapterSupport.ReadProcessOutputAsync(
                    streams,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        var now = DateTimeOffset.UtcNow;
        if (process.ExitCode != 0)
        {
            yield return new(
                ToolId,
                ToolEventKind.Failed,
                now,
                "dite.filegen.exit.failure",
                $"Dite FileGen exited with code {process.ExitCode}; stderr length: {process.StandardError.Length}.");
            yield break;
        }

        DiteFileGenResult? result = null;
        foreach (var line in process.StandardOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                var candidate = JsonSerializer.Deserialize<DiteFileGenResult>(line);
                if (candidate?.Schema == "Dite.FileGenResult")
                {
                    result = candidate;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (result is not
            {
                Version: 2,
                Status: "completed",
                FileCount: >= 0,
                TotalBytes: >= 0,
                GeneratedBytes: >= 0,
                ReusedFileCount: >= 0,
                ElapsedSeconds: >= 0
            }
            || !double.IsFinite(result.ElapsedSeconds))
        {
            yield return new(
                ToolId,
                ToolEventKind.Failed,
                now,
                "dite.filegen.output.invalid",
                "Dite FileGen did not return the supported structured result.");
            yield break;
        }

        var metrics = new[]
        {
            new TestMetric(
                "file_count",
                result.FileCount,
                "files",
                now),
            new TestMetric(
                "total_bytes",
                result.TotalBytes,
                "bytes",
                now),
            new TestMetric(
                "reused_file_count",
                result.ReusedFileCount,
                "files",
                now),
            new TestMetric(
                "generated_bytes",
                result.GeneratedBytes,
                "bytes",
                now),
            new TestMetric(
                "elapsed_seconds",
                result.ElapsedSeconds,
                "seconds",
                now),
            new TestMetric(
                "throughput_mib_s",
                result.ElapsedSeconds > 0
                    ? result.GeneratedBytes / result.ElapsedSeconds
                        / (1024d * 1024d)
                    : 0,
                "MiB/s",
                now)
        };
        foreach (var metric in metrics)
        {
            yield return new(
                ToolId,
                ToolEventKind.Metric,
                now,
                "tool.metric.normalized",
                string.Empty,
                metric);
        }

        yield return new(
            ToolId,
            ToolEventKind.Completed,
            now,
            "dite.filegen.completed",
            string.Empty);
    }

    private sealed record DiteFileGenResult(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("profile")] string? Profile,
        [property: JsonPropertyName("file_count")] int FileCount,
        [property: JsonPropertyName("total_bytes")] long TotalBytes,
        [property: JsonPropertyName("generated_bytes")] long GeneratedBytes,
        [property: JsonPropertyName("elapsed_seconds")] double ElapsedSeconds,
        [property: JsonPropertyName("reused_file_count")] int ReusedFileCount);
}
