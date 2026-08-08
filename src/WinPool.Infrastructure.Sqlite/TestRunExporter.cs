using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed record TestRunExportResult(
    string DestinationPath,
    string Sha256,
    long ItemCount);

public sealed class TestRunExporter(
    WinPoolSqliteStore store,
    TestRunRepository runs,
    TestArtifactStore artifacts)
{
    public async Task<TestRunExportResult> ExportAsync(
        TestRunId runId,
        TestExportFormat format,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var expectedExtension = format switch
        {
            TestExportFormat.Csv => ".csv",
            TestExportFormat.Json => ".json",
            TestExportFormat.Markdown => ".md",
            TestExportFormat.EvidencePackage => ".zip",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        if (!string.Equals(
                Path.GetExtension(destination),
                expectedExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The selected test export format requires a {expectedExtension} destination.",
                nameof(destinationPath));
        }

        if (string.Equals(destination, store.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A test export cannot overwrite the WinPool database.");
        }

        var run = await runs.GetAsync(runId, cancellationToken)
                  ?? throw new KeyNotFoundException(
                      $"Test run {runId.Value:N} was not found.");
        var steps = await runs.ListStepsAsync(runId, cancellationToken);
        var metrics = await runs.ListStepMetricsAsync(runId, cancellationToken);
        var evidence = await artifacts.ListRunArtifactsAsync(runId, cancellationToken);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidOperationException(
                         "The test export path has no parent directory.");
        Directory.CreateDirectory(parent);
        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException(
                "The selected test export already exists and overwrite was not confirmed.");
        }

        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            switch (format)
            {
                case TestExportFormat.Csv:
                    await WriteUtf8Async(
                        temporary,
                        BuildCsv(metrics),
                        cancellationToken);
                    break;
                case TestExportFormat.Json:
                    await WriteUtf8Async(
                        temporary,
                        BuildJson(run, steps, metrics, evidence),
                        cancellationToken);
                    break;
                case TestExportFormat.Markdown:
                    await WriteUtf8Async(
                        temporary,
                        BuildMarkdown(run, steps, metrics, evidence),
                        cancellationToken);
                    break;
                case TestExportFormat.EvidencePackage:
                    await WriteEvidencePackageAsync(
                        temporary,
                        run,
                        steps,
                        metrics,
                        evidence,
                        cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format));
            }

            var sha256 = await HashAsync(temporary, cancellationToken);
            File.Move(temporary, destination, overwrite);
            return new(destination, sha256, metrics.Count + evidence.Count);
        }
        catch
        {
            TryRemoveTemporary(temporary);
            throw;
        }
    }

    private async Task WriteEvidencePackageAsync(
        string path,
        PersistedTestRun run,
        IReadOnlyList<PersistedTestStep> steps,
        IReadOnlyList<PersistedStepMetric> metrics,
        IReadOnlyList<PersistedArtifact> evidence,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteZipTextAsync(
            archive,
            "manifest.json",
            BuildJson(run, steps, metrics, evidence),
            cancellationToken);
        await WriteZipTextAsync(
            archive,
            "metrics.csv",
            BuildCsv(metrics),
            cancellationToken);
        await WriteZipTextAsync(
            archive,
            "report.md",
            BuildMarkdown(run, steps, metrics, evidence),
            cancellationToken);
        var dataRoot = Path.GetDirectoryName(store.DatabasePath)
                       ?? throw new InvalidOperationException(
                           "The WinPool data root is unavailable.");
        foreach (var artifact in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(Path.Combine(dataRoot, artifact.RelativePath));
            var expectedRoot = Path.GetFullPath(
                Path.Combine(dataRoot, "artifacts")) + Path.DirectorySeparatorChar;
            if (!source.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(source))
            {
                throw new IOException(
                    $"Evidence artifact '{artifact.RelativePath}' is missing or outside the private evidence root.");
            }

            var entry = archive.CreateEntry(
                $"attachments/{artifact.ArtifactId:N}-{Path.GetFileName(source)}",
                CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await using var sourceStream = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await sourceStream.CopyToAsync(entryStream, cancellationToken);
        }

        archive.Dispose();
        await output.FlushAsync(cancellationToken);
    }

    private static string BuildCsv(IReadOnlyList<PersistedStepMetric> metrics)
    {
        var builder = new StringBuilder(
            "StepId,MetricId,Value,Unit,Aggregation\r\n");
        foreach (var metric in metrics)
        {
            builder.Append(Csv(metric.StepId ?? string.Empty)).Append(',')
                .Append(Csv(metric.MetricId)).Append(',')
                .Append(metric.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(metric.Unit)).Append(',')
                .Append(Csv(metric.Aggregation)).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string BuildJson(
        PersistedTestRun run,
        IReadOnlyList<PersistedTestStep> steps,
        IReadOnlyList<PersistedStepMetric> metrics,
        IReadOnlyList<PersistedArtifact> evidence) =>
        JsonSerializer.Serialize(
            new
            {
                format = "WinPool.TestRunExport",
                version = 1,
                run,
                steps,
                metrics,
                artifacts = evidence
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });

    private static string BuildMarkdown(
        PersistedTestRun run,
        IReadOnlyList<PersistedTestStep> steps,
        IReadOnlyList<PersistedStepMetric> metrics,
        IReadOnlyList<PersistedArtifact> evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WinPool test result")
            .AppendLine()
            .AppendLine($"- Run: `{run.RunId.Value:N}`")
            .AppendLine($"- State: `{run.State}`")
            .AppendLine($"- Started (UTC): `{run.StartedAtUtc:O}`")
            .AppendLine($"- Ended (UTC): `{run.EndedAtUtc:O}`")
            .AppendLine()
            .AppendLine("## Steps")
            .AppendLine()
            .AppendLine("| Step | State | Tool |")
            .AppendLine("| --- | --- | --- |");
        foreach (var step in steps)
        {
            builder.AppendLine(
                $"| {Markdown(step.StepId)} | {step.State} | {Markdown(step.ToolId?.Value ?? "-")} |");
        }

        builder.AppendLine()
            .AppendLine("## Metrics")
            .AppendLine()
            .AppendLine("| Step | Metric | Value | Unit | Aggregation |")
            .AppendLine("| --- | --- | ---: | --- | --- |");
        foreach (var metric in metrics)
        {
            builder.AppendLine(
                $"| {Markdown(metric.StepId ?? "-")} | {Markdown(metric.MetricId)} | {metric.Value.ToString("R", CultureInfo.InvariantCulture)} | {Markdown(metric.Unit)} | {Markdown(metric.Aggregation)} |");
        }

        builder.AppendLine()
            .AppendLine("## Evidence")
            .AppendLine();
        foreach (var artifact in evidence)
        {
            builder.AppendLine(
                $"- `{Markdown(artifact.RelativePath)}` — {artifact.ByteLength} bytes — SHA-256 `{artifact.Sha256}`");
        }

        return builder.ToString();
    }

    private static async Task WriteUtf8Async(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteZipTextAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string Csv(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.IndexOfAny([',', '"']) < 0
            ? sanitized
            : $"\"{sanitized.Replace("\"", "\"\"")}\"";
    }

    private static string Markdown(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static async Task<string> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static void TryRemoveTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
