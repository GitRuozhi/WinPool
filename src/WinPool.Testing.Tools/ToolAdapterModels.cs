using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed record NormalizedToolMetric(
    string MetricId,
    double Value,
    string Unit);

public sealed record ParsedToolOutput(
    IReadOnlyList<NormalizedToolMetric> Metrics,
    IReadOnlyList<TestLatencyHistogramBucket> LatencyHistogram,
    IReadOnlyList<string> Limitations)
{
    public static ParsedToolOutput Empty { get; } = new([], [], []);
}

public sealed record CopyVerificationEvidence(
    bool DestinationExists,
    bool SizeMatches,
    bool ContentValidationPassed);

public sealed record RoboCopyExitCode(
    int Value,
    bool FilesCopied,
    bool ExtraFilesOrDirectoriesDetected,
    bool MismatchedFilesOrDirectoriesDetected,
    bool ToolReportedFailure)
{
    public bool IsAcceptable => !ToolReportedFailure;
}

public sealed record RoboCopyParsedOutput(
    long TotalFiles,
    long CopiedFiles,
    long FailedFiles,
    long TotalBytes,
    long CopiedBytes,
    double ElapsedSeconds,
    double? ReportedBytesPerSecond);

public sealed record RoboCopyEvaluation(
    bool IsSuccessful,
    RoboCopyExitCode ExitCode,
    RoboCopyParsedOutput Output,
    IReadOnlyList<string> FailureCodes);

internal static class ToolIds
{
    // These values intentionally match ToolManagement.KnownToolIds without
    // introducing a reverse dependency from adapters to the registry.
    public static readonly ToolId DiskSpd = new("microsoft.diskspd");
    public static readonly ToolId Fio = new("fio");
    public static readonly ToolId RoboCopy = new("windows.robocopy");
    public static readonly ToolId RamMap = new("microsoft.sysinternals.rammap");
    public static readonly ToolId DiteFileGen = new("dite.filegen");
}
