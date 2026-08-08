using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinPool.ToolManagement;

public enum ToolVersionSupportStatus
{
    Supported,
    Unsupported,
    Unrecognized,
    ProbeFailed
}

public static partial class ToolVersionParser
{
    [GeneratedRegex(
        @"(?<!\d)(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<build>\d+))?(?:\.(?<revision>\d+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = VersionPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        Span<int> parts = stackalloc int[4];
        var groupNames = new[] { "major", "minor", "build", "revision" };
        var foundParts = 0;

        for (var index = 0; index < groupNames.Length; index++)
        {
            var group = match.Groups[groupNames[index]];
            if (!group.Success)
            {
                break;
            }

            if (!int.TryParse(group.Value, out parts[index]))
            {
                return false;
            }

            foundParts++;
        }

        version = foundParts switch
        {
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };
        return true;
    }
}

public sealed record ToolVersionProbeRequest(
    ToolDescriptor Descriptor,
    string ExecutablePath);

public sealed record ToolVersionProbeResult(
    bool Succeeded,
    string? Version,
    /// <summary>
    /// A verified signer/publisher, when the probe implementation has performed
    /// that verification. Unverified file-description metadata must not be used.
    /// </summary>
    string? Publisher,
    string DiagnosticCode)
{
    public static ToolVersionProbeResult Success(string version, string? publisher = null) =>
        new(true, version, publisher, "tool.version.detected");

    public static ToolVersionProbeResult Failure(string diagnosticCode) =>
        new(false, null, null, diagnosticCode);
}

/// <summary>
/// Version detection is a port with no command text or argument list. A future
/// worker implementation may only use the fixed strategy registered for ToolId.
/// </summary>
public interface IToolVersionProbe
{
    Task<ToolVersionProbeResult> ProbeAsync(
        ToolVersionProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads executable metadata only. It never starts the executable or a shell.
/// </summary>
public sealed class FileMetadataToolVersionProbe : IToolVersionProbe
{
    public Task<ToolVersionProbeResult> ProbeAsync(
        ToolVersionProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var info = FileVersionInfo.GetVersionInfo(request.ExecutablePath);
            var version = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            return Task.FromResult(
                version is null
                    ? ToolVersionProbeResult.Failure("tool.version.metadata-missing")
                    : ToolVersionProbeResult.Success(version));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolVersionProbeResult.Failure("tool.version.metadata-unreadable"));
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
