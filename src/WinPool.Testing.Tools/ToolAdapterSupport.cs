using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using WinPool.Application;

namespace WinPool.Testing.Tools;

internal static class ToolAdapterSupport
{
    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static ApplicationResult<ToolInvocation> Reject(
        CorrelationId correlationId,
        string code,
        string diagnostic) =>
        ApplicationResult<ToolInvocation>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                diagnostic,
                ApplicationMessageSeverity.Error,
                []));

    public static string ValidateExecutable(
        string executablePath,
        params string[] allowedFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)
            || IsDeviceNamespace(executablePath))
        {
            throw new ArgumentException(
                "The tool executable path must be a fully qualified ordinary file path.",
                nameof(executablePath));
        }

        var normalized = Path.GetFullPath(executablePath);
        var fileName = Path.GetFileName(normalized);
        if (!allowedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The configured executable name does not match the adapter.",
                nameof(executablePath));
        }

        return normalized;
    }

    public static string RequireParameter(TestStep step, string key)
    {
        if (!step.Parameters.TryGetValue(key, out var parameter)
            || parameter.Kind is not TestParameterKind.Text
            || string.IsNullOrWhiteSpace(parameter.SerializedValue))
        {
            throw new ToolAdapterValidationException(
                $"tool.adapter.parameter.{ToCode(key)}_required",
                $"The required typed parameter '{key}' is missing.");
        }

        return parameter.SerializedValue;
    }

    public static string OptionalChoice(
        TestStep step,
        string key,
        string defaultValue)
    {
        if (!step.Parameters.TryGetValue(key, out var parameter))
        {
            return defaultValue;
        }

        if (parameter.Kind is not TestParameterKind.Choice
            || string.IsNullOrWhiteSpace(parameter.SerializedValue))
        {
            throw new ToolAdapterValidationException(
                $"tool.adapter.parameter.{ToCode(key)}_invalid",
                $"The typed parameter '{key}' is invalid.");
        }

        return parameter.SerializedValue;
    }

    public static bool OptionalBoolean(TestStep step, string key, bool defaultValue)
    {
        if (!step.Parameters.TryGetValue(key, out var parameter))
        {
            return defaultValue;
        }

        if (parameter.Kind is not TestParameterKind.Boolean
            || !bool.TryParse(parameter.SerializedValue, out var value))
        {
            throw new ToolAdapterValidationException(
                $"tool.adapter.parameter.{ToCode(key)}_invalid",
                $"The typed parameter '{key}' is invalid.");
        }

        return value;
    }

    public static int OptionalInteger(
        TestStep step,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (!step.Parameters.TryGetValue(key, out var parameter))
        {
            return defaultValue;
        }

        if (parameter.Kind is not TestParameterKind.Integer
            || !int.TryParse(
                parameter.SerializedValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || value > maximum)
        {
            throw new ToolAdapterValidationException(
                $"tool.adapter.parameter.{ToCode(key)}_invalid",
                $"The typed parameter '{key}' is outside its supported range.");
        }

        return value;
    }

    public static int WholeSeconds(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero
            || value.TotalSeconds > int.MaxValue
            || value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ToolAdapterValidationException(
                $"tool.adapter.workload.{ToCode(parameterName)}_invalid",
                $"The workload value '{parameterName}' must be a whole number of seconds.");
        }

        return checked((int)value.TotalSeconds);
    }

    public static string ResolveRegisteredFile(
        AuthorizedTestWorkspace workspace,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = NormalizeRoot(workspace.Plan.NormalizedRootDirectory);
        ValidateExistingChain(root, root, allowFinalDirectory: true);

        if (Path.IsPathRooted(relativePath)
            || IsDeviceNamespace(relativePath)
            || relativePath.Split(DirectorySeparators, StringSplitOptions.None)
                .Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.path.invalid",
                "A tool target must be an ordinary relative path.");
        }

        var registered = workspace.Plan.RegisteredFiles
            .Select(file => NormalizeRelative(root, file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedRelative = NormalizeRelative(root, relativePath);
        if (!registered.Contains(normalizedRelative))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.path.not_registered",
                "The requested tool target is not registered for this test run.");
        }

        var absolute = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        EnsureDescendant(root, absolute);

        var runDirectory = Path.GetFullPath(workspace.Plan.RunDirectory);
        EnsureDescendant(root, runDirectory, allowSame: true);
        EnsureDescendant(runDirectory, absolute);
        ValidateExistingChain(root, absolute, allowFinalDirectory: false);
        return absolute;
    }

    public static string ResolveRegisteredDirectory(
        AuthorizedTestWorkspace workspace,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = NormalizeRoot(workspace.Plan.NormalizedRootDirectory);
        ValidateExistingChain(root, root, allowFinalDirectory: true);
        if (Path.IsPathRooted(relativePath)
            || IsDeviceNamespace(relativePath)
            || relativePath.Split(DirectorySeparators, StringSplitOptions.None)
                .Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.directory.invalid",
                "A tool directory target must be an ordinary relative path.");
        }

        var registered = workspace.Plan.RegisteredDirectories
            .Select(directory => NormalizeRelative(root, directory.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedRelative = NormalizeRelative(root, relativePath);
        if (!registered.Contains(normalizedRelative))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.directory.not_registered",
                "The requested tool directory is not registered for this test run.");
        }

        var absolute = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        EnsureDescendant(root, absolute);
        var runDirectory = Path.GetFullPath(workspace.Plan.RunDirectory);
        EnsureDescendant(root, runDirectory, allowSame: true);
        EnsureDescendant(runDirectory, absolute);
        ValidateExistingChain(root, absolute, allowFinalDirectory: true);
        return absolute;
    }

    public static string ValidateWorkingDirectory(AuthorizedTestWorkspace workspace)
    {
        var root = NormalizeRoot(workspace.Plan.NormalizedRootDirectory);
        var runDirectory = Path.GetFullPath(workspace.Plan.RunDirectory);
        EnsureDescendant(root, runDirectory, allowSame: false);
        ValidateExistingChain(root, runDirectory, allowFinalDirectory: true);
        return runDirectory;
    }

    public static async Task<(string StandardOutput, string StandardError, int ExitCode)>
        ReadProcessOutputAsync(
            ToolProcessStreams streams,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var codePage = streams.ResolvedOutputCodePage
            ?? new SystemToolOutputCodePageResolver()
                .Resolve(streams.OutputEncoding).CodePage;
        var standardOutputDecoder = new ToolOutputTextDecoder(codePage);
        var standardErrorDecoder = new ToolOutputTextDecoder(codePage);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        await foreach (var chunk in streams.Chunks.WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Stream is ToolOutputStream.StandardOutput)
            {
                standardOutput.Append(
                    standardOutputDecoder.Decode(chunk.Bytes.Span));
            }
            else
            {
                standardError.Append(
                    standardErrorDecoder.Decode(chunk.Bytes.Span));
            }
        }

        var exitCode = await streams.ExitCode.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (
            standardOutput.Append(standardOutputDecoder.Complete()).ToString(),
            standardError.Append(standardErrorDecoder.Complete()).ToString(),
            exitCode);
    }

    public static async IAsyncEnumerable<ToolEvent> ParseStructuredAsync(
        ToolId toolId,
        ToolProcessStreams streams,
        Func<string, ParsedToolOutput> parser,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (string stdout, string stderr, int exitCode) process;
        try
        {
            process = await ReadProcessOutputAsync(streams, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        var now = DateTimeOffset.UtcNow;
        if (process.exitCode != 0)
        {
            yield return new ToolEvent(
                toolId,
                ToolEventKind.Failed,
                now,
                "tool.process.exit_failure",
                $"The tool exited with code {process.exitCode}. stderr length: {process.stderr.Length}.");
            yield break;
        }

        ParsedToolOutput? parsed = null;
        ToolEvent? parseFailure = null;
        try
        {
            parsed = parser(process.stdout);
        }
        catch (Exception exception) when (
            exception is FormatException
                or InvalidDataException
                or System.Xml.XmlException
                or System.Text.Json.JsonException)
        {
            parseFailure = new ToolEvent(
                toolId,
                ToolEventKind.Failed,
                now,
                "tool.output.invalid",
                $"The structured output could not be parsed: {exception.GetType().Name}.");
        }

        if (parseFailure is not null)
        {
            yield return parseFailure;
            yield break;
        }

        foreach (var metric in parsed!.Metrics)
        {
            yield return new ToolEvent(
                toolId,
                ToolEventKind.Metric,
                now,
                "tool.metric.normalized",
                string.Empty,
                new TestMetric(metric.MetricId, metric.Value, metric.Unit, now));
        }

        foreach (var limitation in parsed.Limitations)
        {
            yield return new ToolEvent(
                toolId,
                ToolEventKind.Evidence,
                now,
                limitation,
                string.Empty);
        }

        foreach (var bucket in parsed.LatencyHistogram)
        {
            yield return new ToolEvent(
                toolId,
                ToolEventKind.Metric,
                now,
                "tool.metric.latency_histogram",
                string.Empty,
                HistogramBucket: bucket);
        }

        yield return new ToolEvent(
            toolId,
            ToolEventKind.Completed,
            now,
            "tool.process.completed",
            string.Empty);
    }

    private static string NormalizeRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || IsDeviceNamespace(root))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.workspace.invalid",
                "The authorized workspace root is invalid.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string NormalizeRelative(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || IsDeviceNamespace(relativePath))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.path.invalid",
                "A registered path is invalid.");
        }

        var absolute = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureDescendant(root, absolute);
        return Path.GetRelativePath(root, absolute);
    }

    private static void EnsureDescendant(
        string root,
        string candidate,
        bool allowSame = false)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (allowSame
            && string.Equals(
                normalizedRoot,
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.path.outside_workspace",
                "A tool path is outside the authorized test workspace.");
        }
    }

    private static void ValidateExistingChain(
        string root,
        string target,
        bool allowFinalDirectory)
    {
        if (!Directory.Exists(root))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.workspace.missing",
                "The authorized workspace root does not exist.");
        }

        for (DirectoryInfo? directory = new(root);
             directory is not null;
             directory = directory.Parent)
        {
            RejectReparse(directory.FullName, File.GetAttributes(directory.FullName));
        }

        var relative = Path.GetRelativePath(root, target);
        var segments = relative.Split(
            DirectorySeparators,
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            RejectReparse(current, attributes);
            var final = index == segments.Length - 1;
            if (!final && !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.path.component_not_directory",
                    "A tool path component is not a directory.");
            }

            if (final
                && !allowFinalDirectory
                && attributes.HasFlag(FileAttributes.Directory))
            {
                throw new ToolAdapterValidationException(
                    "tool.adapter.path.target_is_directory",
                    "A registered tool file resolves to a directory.");
            }
        }
    }

    private static void RejectReparse(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ToolAdapterValidationException(
                "tool.adapter.path.reparse_point",
                $"A reparse point was found at depth {path.Count(character => character == Path.DirectorySeparatorChar)}.");
        }
    }

    private static bool IsDeviceNamespace(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.Ordinal);

    private static string ToCode(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

}

internal sealed class ToolAdapterValidationException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}
