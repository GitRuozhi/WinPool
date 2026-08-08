namespace WinPool.Application;

public readonly record struct ToolId(string Value);

[Flags]
public enum ToolCapabilities
{
    None = 0,
    SequentialIo = 1 << 0,
    RandomIo = 1 << 1,
    MixedIo = 1 << 2,
    FileGeneration = 1 << 3,
    FileCopy = 1 << 4,
    FileVerification = 1 << 5,
    LatencyMetrics = 1 << 6,
    StructuredOutput = 1 << 7,
    SystemCacheCleanup = 1 << 8
}

public enum ToolAvailability
{
    NotFound,
    Available,
    UnsupportedVersion,
    IdentityChanged,
    InvalidSignature,
    Misconfigured
}

public enum ToolPathSource
{
    AutomaticDiscovery,
    CustomPath,
    ManagedInstallation,
    WindowsComponent
}

public sealed record ToolState(
    ToolId ToolId,
    ToolAvailability Availability,
    string? ExecutablePath,
    ToolPathSource? PathSource,
    string? Version,
    string? Sha256,
    string? Publisher,
    ToolCapabilities Capabilities,
    bool RequiresElevation);

public enum ToolOutputEncoding
{
    Utf8,
    Utf16LittleEndian,
    SystemAnsi,
    Oem
}

/// <summary>
/// An adapter-produced process description. Arguments are separate tokens; no shell
/// or complete command-line string is accepted by this contract.
/// </summary>
public sealed record ToolInvocation(
    ToolId ToolId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    ToolOutputEncoding OutputEncoding,
    TimeSpan Timeout);

public enum ToolOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record ToolOutputChunk(
    ToolOutputStream Stream,
    ReadOnlyMemory<byte> Bytes,
    DateTimeOffset ReceivedAtUtc);

public sealed record ToolProcessStreams(
    IAsyncEnumerable<ToolOutputChunk> Chunks,
    Task<int> ExitCode);

public enum ToolEventKind
{
    Started,
    Progress,
    Metric,
    Evidence,
    Completed,
    Failed
}

public sealed record TestLatencyHistogramBucket(
    string Operation,
    long UpperBoundNanoseconds,
    long SampleCount);

public sealed record ToolEvent(
    ToolId ToolId,
    ToolEventKind Kind,
    DateTimeOffset OccurredAtUtc,
    string Code,
    string DiagnosticText,
    TestMetric? Metric = null,
    string? ArtifactRelativePath = null,
    TestLatencyHistogramBucket? HistogramBucket = null);

public interface IExternalToolRegistry
{
    Task<ApplicationResult<ToolState>> DetectAsync(
        ToolId toolId,
        CancellationToken cancellationToken);

    Task<ApplicationResult<IReadOnlyList<ToolState>>> ListAsync(
        CancellationToken cancellationToken);
}

public interface IExternalToolAdapter
{
    ToolId ToolId { get; }

    ToolCapabilities Capabilities { get; }

    ApplicationResult<ToolInvocation> BuildInvocation(
        TestStep step,
        AuthorizedTestWorkspace workspace,
        CorrelationId correlationId);

    IAsyncEnumerable<ToolEvent> ParseAsync(
        ToolProcessStreams streams,
        CancellationToken cancellationToken);
}

public interface IExternalSystemSupportToolAdapter
{
    ToolId ToolId { get; }

    ToolCapabilities Capabilities { get; }

    ApplicationResult<ToolInvocation> BuildInvocation(
        AuthorizedSystemSupportAction action,
        CorrelationId correlationId);
}

public enum ToolInstallerKind
{
    PortableArchive,
    Msi,
    ExecutableInstaller,
    PackageManager
}

public enum ToolInstallLocation
{
    PerUserManagedDirectory,
    UserSelectedDirectory
}

public sealed record ToolInstallPlan(
    ToolId ToolId,
    Uri OfficialSource,
    string ExpectedSha256,
    ToolInstallerKind InstallerKind,
    ToolInstallLocation Location,
    bool RequiresElevation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PlanHash);

public sealed class AuthorizedToolInstall
{
    internal AuthorizedToolInstall(ToolInstallPlan plan)
    {
        Plan = plan;
    }

    public ToolInstallPlan Plan { get; }
}

public static class ToolInstallAuthorization
{
    public static ApplicationResult<AuthorizedToolInstall> Authorize(
        ToolInstallPlan plan,
        bool userConfirmed,
        DateTimeOffset nowUtc,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!userConfirmed)
        {
            return Reject(
                correlationId,
                "tool.install.confirmation-required",
                "The user did not confirm the exact downloaded package.");
        }

        if (plan.ExpiresAtUtc <= nowUtc ||
            plan.CreatedAtUtc > nowUtc ||
            plan.ExpiresAtUtc - plan.CreatedAtUtc > TimeSpan.FromMinutes(30))
        {
            return Reject(
                correlationId,
                "tool.install.plan-expired",
                "The tool installation plan has expired.");
        }

        if (plan.OfficialSource.Scheme != Uri.UriSchemeHttps ||
            plan.ExpectedSha256.Length != 64 ||
            plan.ExpectedSha256.Any(character => !Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(plan.PlanHash))
        {
            return Reject(
                correlationId,
                "tool.install.plan-invalid",
                "The finalized install plan has no HTTPS source or verified package SHA-256.");
        }

        return ApplicationResult<AuthorizedToolInstall>.Succeeded(
            new AuthorizedToolInstall(plan),
            correlationId);
    }

    private static ApplicationResult<AuthorizedToolInstall> Reject(
        CorrelationId correlationId,
        string code,
        string diagnostic) =>
        ApplicationResult<AuthorizedToolInstall>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(
                code,
                "ToolManagement.InstallRejected",
                diagnostic,
                ApplicationMessageSeverity.Error,
                []));
}

public sealed record ToolInstallResult(
    ToolId ToolId,
    ToolState State,
    DateTimeOffset CompletedAtUtc);

public interface IToolInstaller
{
    Task<ApplicationResult<ToolInstallPlan>> PlanAsync(
        ToolId toolId,
        ToolInstallLocation location,
        CancellationToken cancellationToken);

    Task<ApplicationResult<ToolInstallResult>> InstallAsync(
        AuthorizedToolInstall install,
        CancellationToken cancellationToken);
}
