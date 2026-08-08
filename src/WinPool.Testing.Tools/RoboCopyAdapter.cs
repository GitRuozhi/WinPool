using System.Globalization;
using System.Text.RegularExpressions;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed class RoboCopyAdapter : IExternalToolAdapter
{
    private readonly string _executablePath;

    public RoboCopyAdapter(string executablePath)
    {
        _executablePath = ToolAdapterSupport.ValidateExecutable(
            executablePath,
            "robocopy.exe");
    }

    public ToolId ToolId => ToolIds.RoboCopy;

    public ToolCapabilities Capabilities =>
        ToolCapabilities.FileCopy | ToolCapabilities.FileVerification;

    public ApplicationResult<ToolInvocation> BuildInvocation(
        TestStep step,
        AuthorizedTestWorkspace workspace,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            if (step.ToolId != ToolId || step.Action is not TestActionKind.Copy)
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.action.unsupported",
                    "RoboCopy only accepts typed copy steps.");
            }

            var directoryMode =
                step.Parameters.ContainsKey("sourceRelativeDirectory")
                || step.Parameters.ContainsKey("destinationRelativeDirectory");
            var source = directoryMode
                ? ToolAdapterSupport.ResolveRegisteredDirectory(
                    workspace,
                    ToolAdapterSupport.RequireParameter(
                        step,
                        "sourceRelativeDirectory"))
                : ToolAdapterSupport.ResolveRegisteredFile(
                    workspace,
                    ToolAdapterSupport.RequireParameter(step, "sourceRelativePath"));
            var destination = directoryMode
                ? ToolAdapterSupport.ResolveRegisteredDirectory(
                    workspace,
                    ToolAdapterSupport.RequireParameter(
                        step,
                        "destinationRelativeDirectory"))
                : ToolAdapterSupport.ResolveRegisteredFile(
                    workspace,
                    ToolAdapterSupport.RequireParameter(
                        step,
                        "destinationRelativePath"));
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.copy.same_path",
                    "RoboCopy source and destination must be different registered files.");
            }

            var sourceName = directoryMode ? null : Path.GetFileName(source);
            var destinationName = directoryMode ? null : Path.GetFileName(destination);
            if (!directoryMode
                && (!string.Equals(
                        sourceName,
                        destinationName,
                        StringComparison.OrdinalIgnoreCase)
                    || sourceName!.IndexOfAny(['*', '?']) >= 0))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.copy.file_name_mismatch",
                    "The initial RoboCopy adapter requires identical literal source and destination file names.");
            }

            var copyMode = ToolAdapterSupport.OptionalChoice(
                step,
                "copyMode",
                "default");
            var copyFlag = copyMode.ToLowerInvariant() switch
            {
                "data" => "/COPY:D",
                "default" => "/COPY:DAT",
                _ => throw new ToolAdapterValidationException(
                    "tool.adapter.parameter.copy_mode_invalid",
                    "Only reviewed RoboCopy data and default metadata modes are supported.")
            };
            var useBuffered = ToolAdapterSupport.OptionalBoolean(
                step,
                "useBuffered",
                false);
            var threads = ToolAdapterSupport.OptionalInteger(
                step,
                "threadCount",
                step.Workload?.ThreadCount ?? 1,
                1,
                128);
            var retryCount = ToolAdapterSupport.OptionalInteger(
                step,
                "retryCount",
                0,
                0,
                100);
            var retryWaitSeconds = ToolAdapterSupport.OptionalInteger(
                step,
                "retryWaitSeconds",
                0,
                0,
                300);

            var arguments = directoryMode
                ? new List<string>
                {
                    source,
                    destination,
                    "/E"
                }
                : new List<string>
                {
                    Path.GetDirectoryName(source)!,
                    Path.GetDirectoryName(destination)!,
                    sourceName!
                };
            arguments.AddRange(
            [
                copyFlag,
                "/XJ",
                "/BYTES",
                "/NFL",
                "/NDL",
                $"/MT:{threads.ToString(CultureInfo.InvariantCulture)}",
                $"/R:{retryCount.ToString(CultureInfo.InvariantCulture)}",
                $"/W:{retryWaitSeconds.ToString(CultureInfo.InvariantCulture)}"
            ]);
            if (!useBuffered)
            {
                arguments.Add("/J");
            }

            return ApplicationResult<ToolInvocation>.Succeeded(
                new ToolInvocation(
                    ToolId,
                    _executablePath,
                    arguments.AsReadOnly(),
                    ToolAdapterSupport.ValidateWorkingDirectory(workspace),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ToolOutputEncoding.Oem,
                    step.Workload is null
                        ? TimeSpan.FromHours(1)
                        : step.Workload.Duration + TimeSpan.FromMinutes(5)),
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

    /// <summary>
    /// Builds one external RoboCopy invocation for a manifest-bound file while
    /// preserving its relative directory below the two registered roots.
    /// The caller remains responsible for binding the relative path to an
    /// immutable copy manifest and for post-copy verification.
    /// </summary>
    public ApplicationResult<ToolInvocation> BuildDirectoryEntryInvocation(
        TestStep step,
        AuthorizedTestWorkspace workspace,
        string relativeFilePath,
        CorrelationId correlationId)
    {
        var directoryInvocation = BuildInvocation(
            step,
            workspace,
            correlationId);
        if (!directoryInvocation.IsSuccess
            || directoryInvocation.Value is null)
        {
            return directoryInvocation;
        }

        try
        {
            var arguments = directoryInvocation.Value.Arguments;
            if (arguments.Count < 3
                || !string.Equals(arguments[2], "/E", StringComparison.Ordinal))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.copy.directory_entry_requires_directory_mode",
                    "A manifest entry invocation requires a typed RoboCopy directory step.");
            }

            var normalized = NormalizeManifestRelativeFile(relativeFilePath);
            var fileName = Path.GetFileName(normalized);
            if (fileName.IndexOfAny(['*', '?']) >= 0)
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.copy.directory_entry_wildcard",
                    "A manifest entry must use a literal file name.");
            }

            var relativeParent = Path.GetDirectoryName(normalized);
            var sourceParent = ResolveManifestParent(
                arguments[0],
                relativeParent);
            var destinationParent = ResolveManifestParent(
                arguments[1],
                relativeParent);
            var entryArguments = new List<string>
            {
                sourceParent,
                destinationParent,
                fileName
            };
            entryArguments.AddRange(arguments.Skip(3));
            return ApplicationResult<ToolInvocation>.Succeeded(
                directoryInvocation.Value with
                {
                    Arguments = entryArguments.AsReadOnly()
                },
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

    private static string NormalizeManifestRelativeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.copy.directory_entry_path_invalid",
                "A copy manifest entry must be a non-empty relative file path.");
        }

        var normalized = path.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        var segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(item => item is "." or ".."))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.copy.directory_entry_path_invalid",
                "A copy manifest entry cannot escape its registered directory.");
        }

        return Path.Combine(segments);
    }

    private static string ResolveManifestParent(
        string registeredRoot,
        string? relativeParent)
    {
        var root = Path.GetFullPath(registeredRoot);
        var candidate = string.IsNullOrEmpty(relativeParent)
            ? root
            : Path.GetFullPath(Path.Combine(root, relativeParent));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.copy.directory_entry_path_invalid",
                "A copy manifest entry escaped its registered directory.");
        }

        return candidate;
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
        var parsed = RoboCopyOutputParser.Parse(process.StandardOutput);
        foreach (var metric in RoboCopyOutputParser.ToMetrics(parsed))
        {
            yield return new ToolEvent(
                ToolId,
                ToolEventKind.Metric,
                now,
                "tool.metric.normalized",
                string.Empty,
                new TestMetric(metric.MetricId, metric.Value, metric.Unit, now));
        }

        var exit = RoboCopyResultEvaluator.DecodeExitCode(process.ExitCode);
        if (!exit.IsAcceptable)
        {
            yield return new ToolEvent(
                ToolId,
                ToolEventKind.Failed,
                now,
                "robocopy.exit.failure",
                $"RoboCopy exited with code {exit.Value}.");
            yield break;
        }

        yield return new ToolEvent(
            ToolId,
            ToolEventKind.Evidence,
            now,
            "robocopy.exit.accepted.verification_required",
            "RoboCopy accepted the exit code; destination verification is still required.");
    }
}

public static partial class RoboCopyOutputParser
{
    public static RoboCopyParsedOutput Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        long totalFiles = 0;
        long copiedFiles = 0;
        long failedFiles = 0;
        long totalBytes = 0;
        long copiedBytes = 0;
        double elapsedSeconds = 0;
        double? bytesPerSecond = null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var row = SummaryRow().Match(trimmed);
            if (row.Success)
            {
                var values = NumberToken().Matches(row.Groups["values"].Value)
                    .Select(match => ParseInteger(match.Value))
                    .ToArray();
                if (values.Length >= 2)
                {
                    if (IsFilesLabel(row.Groups["label"].Value))
                    {
                        totalFiles = values[0];
                        copiedFiles = values[1];
                        failedFiles = values.Length >= 5 ? values[4] : 0;
                    }
                    else if (IsBytesLabel(row.Groups["label"].Value))
                    {
                        totalBytes = values[0];
                        copiedBytes = values[1];
                    }
                }

                continue;
            }

            var time = TimeValue().Match(trimmed);
            if (time.Success && IsTimesLabel(time.Groups["label"].Value))
            {
                elapsedSeconds = ParseDuration(time.Groups["time"].Value);
                continue;
            }

            var speed = BytesPerSecond().Match(trimmed);
            if (speed.Success)
            {
                bytesPerSecond = ParseDouble(speed.Groups["value"].Value);
            }
        }

        return new RoboCopyParsedOutput(
            totalFiles,
            copiedFiles,
            failedFiles,
            totalBytes,
            copiedBytes,
            elapsedSeconds,
            bytesPerSecond);
    }

    public static IReadOnlyList<NormalizedToolMetric> ToMetrics(
        RoboCopyParsedOutput output)
    {
        var throughput = output.ReportedBytesPerSecond
            ?? (output.ElapsedSeconds > 0
                ? output.CopiedBytes / output.ElapsedSeconds
                : 0);
        return
        [
            new("files.total", output.TotalFiles, "count"),
            new("files.copied", output.CopiedFiles, "count"),
            new("files.failed", output.FailedFiles, "count"),
            new("bytes.copied", output.CopiedBytes, "B"),
            new("duration", output.ElapsedSeconds, "s"),
            new("throughput.total", throughput / 1048576d, "MiB/s")
        ];
    }

    private static bool IsFilesLabel(string label) =>
        label.Equals("Files", StringComparison.OrdinalIgnoreCase)
        || label.Equals("文件", StringComparison.Ordinal);

    private static bool IsBytesLabel(string label) =>
        label.Equals("Bytes", StringComparison.OrdinalIgnoreCase)
        || label.Equals("字节", StringComparison.Ordinal);

    private static bool IsTimesLabel(string label) =>
        label.Equals("Times", StringComparison.OrdinalIgnoreCase)
        || label.Equals("时间", StringComparison.Ordinal);

    private static long ParseInteger(string value) =>
        long.Parse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) =>
        double.Parse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

    private static double ParseDuration(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3)
        {
            return 0;
        }

        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600d
               + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60d
               + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(
        @"^(?<label>Files|文件|Bytes|字节)\s*:\s*(?<values>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRow();

    [GeneratedRegex(@"\d[\d,]*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();

    [GeneratedRegex(
        @"^(?<label>Times|时间)\s*:\s*(?<time>\d+:\d+:\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimeValue();

    [GeneratedRegex(
        @"(?<value>\d[\d,.]*)\s*(?:Bytes/sec|字节/秒)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BytesPerSecond();
}

public static class RoboCopyResultEvaluator
{
    public static RoboCopyExitCode DecodeExitCode(int exitCode) =>
        new(
            exitCode,
            (exitCode & 1) != 0,
            (exitCode & 2) != 0,
            (exitCode & 4) != 0,
            exitCode < 0 || exitCode >= 8);

    public static RoboCopyEvaluation Evaluate(
        int exitCode,
        RoboCopyParsedOutput output,
        CopyVerificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(evidence);
        var decoded = DecodeExitCode(exitCode);
        var failures = new List<string>();
        if (!decoded.IsAcceptable)
        {
            failures.Add("robocopy.exit.failure");
        }

        if (decoded.MismatchedFilesOrDirectoriesDetected || output.FailedFiles > 0)
        {
            failures.Add("robocopy.copy.mismatch_or_failure");
        }

        if (!evidence.DestinationExists)
        {
            failures.Add("robocopy.verify.destination_missing");
        }

        if (!evidence.SizeMatches)
        {
            failures.Add("robocopy.verify.size_mismatch");
        }

        if (!evidence.ContentValidationPassed)
        {
            failures.Add("robocopy.verify.content_failed");
        }

        return new RoboCopyEvaluation(
            failures.Count == 0,
            decoded,
            output,
            failures);
    }
}
