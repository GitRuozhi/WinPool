using System.Globalization;
using System.Text.Json;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;
using WinPool.Testing;

namespace WinPool.Agent;

/// <summary>
/// Executes coordinator-owned test actions which do not launch an external tool.
/// It intentionally has no arbitrary command or arbitrary file-write surface.
/// </summary>
internal sealed class LocalTestStepExecutor
{
    private readonly TestRunRepository repository;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly TestArtifactStore artifactStore;
    private readonly RegisteredTestFileExecutor fileExecutor = new();
    private readonly RegisteredTestDirectoryInspector directoryInspector = new();
    private readonly Dictionary<string, RegisteredExternalFileEvidence> verifiedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> verifiedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalTestStepExecutor(
        TestRunRepository repository,
        MonitoringSessionCoordinator monitoring,
        TestArtifactStore artifactStore)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public static bool IsSupported(TestActionKind action) =>
        action is TestActionKind.CheckSpace
            or TestActionKind.Repeat
            or TestActionKind.Store
            or TestActionKind.Summarize
            or TestActionKind.Verify
            or TestActionKind.Cleanup
            or TestActionKind.WaitForIdle
            or TestActionKind.CaptureHealth
            or TestActionKind.ExportArtifact;

    public async Task ExecuteAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        await repository.UpdateStepStateAsync(
            run.Plan.RunId,
            step.Id,
            ApplicationTaskState.Running,
            cancellationToken);

        try
        {
            switch (step.Action)
            {
                case TestActionKind.CheckSpace:
                    await CheckSpaceAsync(run, step, cancellationToken);
                    break;
                case TestActionKind.Repeat:
                case TestActionKind.Summarize:
                    await AggregateAsync(run.Plan.RunId, step, cancellationToken);
                    break;
                case TestActionKind.Store:
                    await StoreAsync(run.Plan.RunId, step, cancellationToken);
                    break;
                case TestActionKind.Verify:
                    await VerifyAsync(run, step, cancellationToken);
                    break;
                case TestActionKind.Cleanup:
                    await CleanupAsync(run, step, cancellationToken);
                    break;
                case TestActionKind.WaitForIdle:
                    await WaitForIdleAsync(step, cancellationToken);
                    break;
                case TestActionKind.CaptureHealth:
                    await CaptureHealthAsync(run, step, cancellationToken);
                    break;
                case TestActionKind.ExportArtifact:
                    await ExportArtifactAsync(run.Plan.RunId, step, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException(
                        $"The local test action '{step.Action}' is not connected.");
            }

            await repository.UpdateStepStateAsync(
                run.Plan.RunId,
                step.Id,
                ApplicationTaskState.Succeeded,
                CancellationToken.None);
        }
        catch
        {
            await repository.UpdateStepStateAsync(
                run.Plan.RunId,
                step.Id,
                cancellationToken.IsCancellationRequested
                    ? ApplicationTaskState.Cancelled
                    : ApplicationTaskState.Failed,
                CancellationToken.None);
            throw;
        }
    }

    private async Task CheckSpaceAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var drive = ResolveDrive(run.Plan.Target.TestRootDirectory);
        var requiredBytes = GetLong(
            step,
            "requiredBytes",
            run.Plan.EstimatedWriteBytes,
            minimum: 0);
        var availableBytes = drive.AvailableFreeSpace;
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "available_bytes",
            availableBytes,
            "bytes",
            "observed",
            cancellationToken);
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "required_bytes",
            requiredBytes,
            "bytes",
            "planned",
            cancellationToken);
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"The test target has {availableBytes} available bytes but {requiredBytes} are required.");
        }
    }

    private async Task CaptureHealthAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var drive = ResolveDrive(run.Plan.Target.TestRootDirectory);
        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "volume_total_bytes",
            total,
            "bytes",
            "observed",
            cancellationToken);
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "volume_available_bytes",
            free,
            "bytes",
            "observed",
            cancellationToken);
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "volume_used_percent",
            total == 0 ? 0 : (total - free) * 100d / total,
            "percent",
            "derived",
            cancellationToken);
    }

    private async Task StoreAsync(
        TestRunId runId,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var sourceStepId = GetRequired(step, "sourceStepId");
        var metricId = GetRequired(step, "metricId");
        var storeAs = GetRequired(step, "storeAs");
        var metrics = await repository.ListStepMetricsAsync(runId, cancellationToken);
        var source = metrics.LastOrDefault(item =>
            string.Equals(item.StepId, sourceStepId, StringComparison.Ordinal)
            && string.Equals(item.MetricId, metricId, StringComparison.Ordinal));
        if (source is null)
        {
            throw new InvalidOperationException(
                $"Metric '{metricId}' was not produced by step '{sourceStepId}'.");
        }

        await repository.AddMetricAsync(
            runId,
            step.Id,
            storeAs,
            source.Value,
            source.Unit,
            "stored",
            cancellationToken);
    }

    private async Task VerifyAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = GetOptional(step, "sourceRelativeDirectory");
        var destinationDirectory = GetOptional(
            step,
            "destinationRelativeDirectory");
        if ((sourceDirectory is null) != (destinationDirectory is null))
        {
            throw new InvalidOperationException(
                "Directory-pair verification requires both registered directories.");
        }

        if (sourceDirectory is not null && destinationDirectory is not null)
        {
            var mode = GetVerificationMode(step);
            var sampleCount = GetLong(
                step,
                "sampleCount",
                defaultValue: 32,
                minimum: 1);
            if (sampleCount > 4096)
            {
                throw new InvalidOperationException(
                    "The typed directory sampleCount must be between 1 and 4096.");
            }

            var comparison = await directoryInspector.VerifyPairAsync(
                run,
                new(
                    sourceDirectory,
                    destinationDirectory,
                    mode,
                    (int)sampleCount),
                cancellationToken);
            if (!comparison.IsMatch)
            {
                throw new IOException(
                    $"Registered directory copy verification failed in {mode} mode at '{comparison.FirstMismatchRelativePath ?? "unknown"}'.");
            }

            await repository.AddMetricAsync(
                run.Plan.RunId,
                step.Id,
                "verified_directory_file_count",
                comparison.ComparedFileCount,
                "files",
                mode.ToString(),
                cancellationToken);
            await repository.AddMetricAsync(
                run.Plan.RunId,
                step.Id,
                "verified_directory_bytes",
                comparison.ComparedBytes,
                "bytes",
                mode.ToString(),
                cancellationToken);
            return;
        }

        var requirePlannedLength = GetBoolean(
            step,
            "requirePlannedLength",
            defaultValue: true);
        var paths = GetRegisteredPaths(run, step);
        var sourcePath = GetOptional(step, "sourceRelativePath");
        var destinationPath = GetOptional(step, "destinationRelativePath");
        if ((sourcePath is null) != (destinationPath is null))
        {
            throw new InvalidOperationException(
                "Pair verification requires both sourceRelativePath and destinationRelativePath.");
        }

        var pairVerified = sourcePath is not null && destinationPath is not null;
        if (sourcePath is not null && destinationPath is not null)
        {
            var mode = GetVerificationMode(step);
            var sampleCount = GetLong(
                step,
                "sampleCount",
                defaultValue: 16,
                minimum: 1);
            if (sampleCount > 1024)
            {
                throw new InvalidOperationException(
                    "The typed sampleCount must be between 1 and 1024.");
            }

            var comparison = await fileExecutor.VerifyExternalPairAsync(
                run,
                new VerifyRegisteredExternalFilePairRequest(
                    sourcePath,
                    destinationPath,
                    mode,
                    SampleCount: (int)sampleCount,
                    RequirePlannedLength: requirePlannedLength),
                cancellationToken);
            if (!comparison.IsMatch)
            {
                throw new IOException(
                    $"Registered copy verification failed in {mode} mode at offset {comparison.FirstMismatchOffset?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
            }

            await repository.AddMetricAsync(
                run.Plan.RunId,
                step.Id,
                "verified_pair_bytes",
                comparison.VerifiedBytes,
                "bytes",
                mode.ToString(),
                cancellationToken);
        }

        var totalBytes = 0L;
        if (pairVerified)
        {
            var registered = run.Plan.Workspace.RegisteredFiles.ToDictionary(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                verifiedPaths.Add(path);
                totalBytes = checked(totalBytes + registered[path].PlannedLength);
            }
        }
        else
        {
            foreach (var path in paths)
            {
                var evidence = await fileExecutor.CaptureExternalEvidenceAsync(
                    run,
                    path,
                    requirePlannedLength,
                    cancellationToken);
                verifiedFiles[evidence.RelativePath] = evidence;
                verifiedPaths.Add(evidence.RelativePath);
                totalBytes = checked(totalBytes + evidence.ActualLength);
            }
        }

        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "verified_file_count",
            paths.Count,
            "files",
            "observed",
            cancellationToken);
        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "verified_bytes",
            totalBytes,
            "bytes",
            "observed",
            cancellationToken);
    }

    private static RegisteredTestFileVerificationMode GetVerificationMode(
        TestStep step)
    {
        var serialized = GetOptional(step, "verificationMode")
                         ?? RegisteredTestFileVerificationMode.FullHash.ToString();
        if (!Enum.TryParse<RegisteredTestFileVerificationMode>(
                serialized,
                ignoreCase: true,
                out var mode)
            || mode is RegisteredTestFileVerificationMode.PatternReplay)
        {
            throw new InvalidOperationException(
                "External copy verification supports Metadata, SampledContent, or FullHash.");
        }

        return mode;
    }

    private async Task CleanupAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var paths = GetRegisteredPaths(run, step);
        var evidence = new List<RegisteredExternalFileEvidence>(paths.Count);
        foreach (var path in paths)
        {
            if (!verifiedPaths.Contains(path))
            {
                throw new InvalidOperationException(
                    $"Registered file '{path}' has not passed the explicit Verify step.");
            }

            if (!verifiedFiles.TryGetValue(path, out var item))
            {
                item = await fileExecutor.CaptureExternalEvidenceAsync(
                    run,
                    path,
                    requirePlannedLength: true,
                    cancellationToken);
            }

            evidence.Add(item);
        }

        var result = await fileExecutor.CleanupExternalEvidenceAsync(
            run,
            evidence,
            cancellationToken);
        if (result.Status is not RegisteredTestFileExecutionStatus.Succeeded)
        {
            throw new IOException(
                $"Registered test cleanup stopped because {result.ConflictRelativePaths.Count} file identities changed.");
        }

        await repository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "removed_file_count",
            result.RemovedRelativePaths.Count,
            "files",
            "observed",
            cancellationToken);
    }

    private async Task ExportArtifactAsync(
        TestRunId runId,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var metrics = await repository.ListStepMetricsAsync(runId, cancellationToken);
        var existingArtifacts = await artifactStore.ListRunArtifactsAsync(
            runId,
            cancellationToken);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                format = "WinPool.TestEvidenceManifest",
                version = 1,
                runId = runId.Value.ToString("N"),
                generatedAtUtc = DateTimeOffset.UtcNow,
                metrics,
                artifacts = existingArtifacts.Select(item => new
                {
                    item.RelativePath,
                    item.Sha256,
                    item.ByteLength,
                    item.MediaType,
                    item.CreatedAtUtc
                })
            });
        var artifact = await artifactStore.SaveGeneratedArtifactAsync(
            runId,
            "test-evidence-manifest",
            "application/json",
            content,
            cancellationToken);
        await repository.AddMetricAsync(
            runId,
            step.Id,
            "evidence_manifest_bytes",
            artifact.ByteLength,
            "bytes",
            "observed",
            cancellationToken);
    }

    private async Task AggregateAsync(
        TestRunId runId,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var sourceStepIds = GetSourceStepIds(step);
        var requestedMetric = GetOptional(step, "metricId");
        var aggregation = GetOptional(step, "aggregation")
                          ?? (step.Action == TestActionKind.Repeat ? "median" : "mean");
        var metrics = (await repository.ListStepMetricsAsync(runId, cancellationToken))
            .Where(item => item.StepId is not null
                           && sourceStepIds.Contains(item.StepId))
            .Where(item => requestedMetric is null
                           || string.Equals(
                               item.MetricId,
                               requestedMetric,
                               StringComparison.Ordinal))
            .GroupBy(
                item => new { item.MetricId, item.Unit },
                item => item.Value)
            .ToArray();
        if (metrics.Length == 0)
        {
            throw new InvalidOperationException(
                "No source metrics are available for aggregation.");
        }

        foreach (var group in metrics)
        {
            var values = group.ToArray();
            var normalizedAggregation = aggregation.ToLowerInvariant();
            var value = normalizedAggregation switch
            {
                "median" => TestMetrics.Median(values),
                "mean" => values.Average(),
                "min" => values.Min(),
                "max" => values.Max(),
                _ => throw new InvalidOperationException(
                    $"Unsupported aggregation '{aggregation}'.")
            };
            await repository.AddMetricAsync(
                runId,
                step.Id,
                group.Key.MetricId,
                value,
                group.Key.Unit,
                normalizedAggregation,
                cancellationToken);
            if (step.Action == TestActionKind.Repeat)
            {
                await repository.AddMetricAsync(
                    runId,
                    step.Id,
                    group.Key.MetricId,
                    values.Min(),
                    group.Key.Unit,
                    "min",
                    cancellationToken);
                await repository.AddMetricAsync(
                    runId,
                    step.Id,
                    group.Key.MetricId,
                    values.Max(),
                    group.Key.Unit,
                    "max",
                    cancellationToken);
            }
        }
    }

    private async Task WaitForIdleAsync(
        TestStep step,
        CancellationToken cancellationToken)
    {
        var maximumActivity = GetDouble(step, "maxActivityPercent", 5, 0, 100);
        var maximumQueue = GetDouble(step, "maxQueueLength", 0.25, 0, double.MaxValue);
        var stableSeconds = GetDouble(step, "stableSeconds", 3, 0.1, 300);
        var timeoutSeconds = GetDouble(step, "timeoutSeconds", 60, 0.1, 3600);
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? idleSince = null;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(timeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = monitoring.CurrentSamples;
            var latestByTarget = samples
                .GroupBy(item => item.TargetId)
                .Select(group => group.MaxBy(item => item.SampledAtUtc)!)
                .ToArray();
            var idle = latestByTarget.Length > 0
                       && latestByTarget.All(sample =>
                           Metric(sample, MonitorMetricKind.ActiveTimePercent) <= maximumActivity
                           && Metric(sample, MonitorMetricKind.AverageQueueLength) <= maximumQueue);
            if (idle)
            {
                idleSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - idleSince >= TimeSpan.FromSeconds(stableSeconds))
                {
                    return;
                }
            }
            else
            {
                idleSince = null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException("The monitored target did not become idle before the typed timeout.");
    }

    private static double Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(item => item.Kind == kind)?.Value ?? 0;

    private static DriveInfo ResolveDrive(string root)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new IOException("The test root does not resolve to a drive.");
        }

        return new DriveInfo(driveRoot);
    }

    private static HashSet<string> GetSourceStepIds(TestStep step)
    {
        var explicitIds = GetOptional(step, "sourceStepIds");
        var ids = explicitIds is null
            ? step.DependsOn
            : explicitIds.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                "An aggregate step requires dependency or sourceStepIds inputs.");
        }

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> GetRegisteredPaths(
        AuthorizedTestRun run,
        TestStep step)
    {
        var serialized = GetOptional(step, "relativePaths");
        var requested = serialized is null
            ? run.Plan.Workspace.RegisteredFiles.Select(item => item.RelativePath).ToArray()
            : serialized.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0)
        {
            throw new InvalidOperationException(
                "Verify and Cleanup require at least one registered test file.");
        }

        var registered = run.Plan.Workspace.RegisteredFiles
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Any(path => !registered.Contains(path)))
        {
            throw new UnauthorizedAccessException(
                "Verify and Cleanup can only target files registered by the authorized test plan.");
        }

        return requested;
    }

    private static string GetRequired(TestStep step, string key) =>
        GetOptional(step, key)
        ?? throw new InvalidOperationException(
            $"The typed test parameter '{key}' is required.");

    private static string? GetOptional(TestStep step, string key) =>
        step.Parameters.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value.SerializedValue)
                ? value.SerializedValue.Trim()
                : null;

    private static long GetLong(
        TestStep step,
        string key,
        long defaultValue,
        long minimum)
    {
        var serialized = GetOptional(step, key);
        if (serialized is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum)
        {
            throw new InvalidOperationException(
                $"The typed test parameter '{key}' is invalid.");
        }

        return value;
    }

    private static double GetDouble(
        TestStep step,
        string key,
        double defaultValue,
        double minimum,
        double maximum)
    {
        var serialized = GetOptional(step, key);
        if (serialized is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(
                serialized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"The typed test parameter '{key}' is invalid.");
        }

        return value;
    }

    private static bool GetBoolean(
        TestStep step,
        string key,
        bool defaultValue)
    {
        var serialized = GetOptional(step, key);
        if (serialized is null)
        {
            return defaultValue;
        }

        if (!bool.TryParse(serialized, out var value))
        {
            throw new InvalidOperationException(
                $"The typed test parameter '{key}' is invalid.");
        }

        return value;
    }
}
