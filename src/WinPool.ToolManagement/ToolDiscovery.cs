using System.Security.Cryptography;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.ToolManagement;

public interface IToolPathConfiguration
{
    string? GetCustomExecutablePath(ToolId toolId);
}

public interface IMutableToolPathConfiguration : IToolPathConfiguration
{
    Task SetCustomExecutablePathAsync(
        ToolId toolId,
        string? executablePath,
        CancellationToken cancellationToken);
}

public sealed class ToolPathConfiguration : IToolPathConfiguration
{
    private readonly IReadOnlyDictionary<ToolId, string> customPaths;

    public ToolPathConfiguration(IReadOnlyDictionary<ToolId, string>? customPaths = null)
    {
        this.customPaths = customPaths ?? new Dictionary<ToolId, string>();
    }

    public string? GetCustomExecutablePath(ToolId toolId) =>
        customPaths.TryGetValue(toolId, out var path) ? path : null;
}

/// <summary>
/// A small cross-process configuration file shared by App and Agent. Every read
/// reopens the file so an already-running Agent observes App changes without a
/// restart. Writes use an atomic same-directory replacement.
/// </summary>
public sealed class JsonToolPathConfiguration : IMutableToolPathConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string configurationPath;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public JsonToolPathConfiguration(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        this.configurationPath = Path.GetFullPath(configurationPath);
    }

    public string ConfigurationPath => configurationPath;

    public string? GetCustomExecutablePath(ToolId toolId)
    {
        try
        {
            if (!File.Exists(configurationPath))
            {
                return null;
            }

            using var stream = new FileStream(
                configurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                stream,
                JsonOptions);
            return values is not null
                && values.TryGetValue(toolId.Value, out var path)
                && !string.IsNullOrWhiteSpace(path)
                    ? path
                    : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return null;
        }
    }

    public async Task SetCustomExecutablePathAsync(
        ToolId toolId,
        string? executablePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolId.Value))
        {
            throw new ArgumentException("A ToolId is required.", nameof(toolId));
        }

        if (!string.IsNullOrWhiteSpace(executablePath)
            && !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "A custom tool path must be fully qualified.",
                nameof(executablePath));
        }

        var normalized = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetFullPath(executablePath);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadForWriteAsync(cancellationToken);
            if (normalized is null)
            {
                values.Remove(toolId.Value);
            }
            else
            {
                values[toolId.Value] = normalized;
            }

            var directory = Path.GetDirectoryName(configurationPath)
                ?? throw new InvalidOperationException(
                    "Tool configuration has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(configurationPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        values,
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, configurationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadForWriteAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(configurationPath))
            {
                return new(StringComparer.Ordinal);
            }

            await using var stream = new FileStream(
                configurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                JsonOptions,
                cancellationToken);
            return values is null
                ? new(StringComparer.Ordinal)
                : new(values, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}

public interface IToolSearchPath
{
    IReadOnlyList<string> GetDirectories();
}

public sealed class EnvironmentToolSearchPath : IToolSearchPath
{
    public IReadOnlyList<string> GetDirectories()
    {
        var value = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public interface IToolFileHasher
{
    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken);
}

public sealed class Sha256ToolFileHasher : IToolFileHasher
{
    public async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}

public interface IToolIdentityBaseline
{
    string? GetExpectedSha256(ToolId toolId, string executablePath);
}

public sealed class EmptyToolIdentityBaseline : IToolIdentityBaseline
{
    public string? GetExpectedSha256(ToolId toolId, string executablePath) => null;
}

public sealed class ToolIdentityBaseline : IToolIdentityBaseline
{
    private readonly IReadOnlyDictionary<ToolId, string> expectedHashes;

    public ToolIdentityBaseline(IReadOnlyDictionary<ToolId, string> expectedHashes)
    {
        this.expectedHashes = expectedHashes ?? throw new ArgumentNullException(nameof(expectedHashes));
    }

    public string? GetExpectedSha256(ToolId toolId, string executablePath) =>
        expectedHashes.TryGetValue(toolId, out var hash) ? hash : null;
}

public sealed record ToolPathDiscoveryResult(
    bool Found,
    string? ExecutablePath,
    ToolPathSource? PathSource,
    bool CustomPathWasInvalid);

public sealed class ToolPathDiscovery
{
    private readonly IToolPathConfiguration configuration;
    private readonly IToolSearchPath searchPath;

    public ToolPathDiscovery(
        IToolPathConfiguration configuration,
        IToolSearchPath searchPath)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.searchPath = searchPath ?? throw new ArgumentNullException(nameof(searchPath));
    }

    public ToolPathDiscoveryResult Find(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var customPath = configuration.GetCustomExecutablePath(descriptor.Id);
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var normalized = TryNormalize(customPath);
            var isRegisteredName = normalized is not null
                && descriptor.ExecutableFileNames.Contains(
                    Path.GetFileName(normalized),
                    StringComparer.OrdinalIgnoreCase);
            return normalized is not null && isRegisteredName && File.Exists(normalized)
                ? new ToolPathDiscoveryResult(true, normalized, ToolPathSource.CustomPath, false)
                : new ToolPathDiscoveryResult(false, normalized, ToolPathSource.CustomPath, true);
        }

        foreach (var directory in searchPath.GetDirectories())
        {
            var normalizedDirectory = TryNormalize(directory);
            if (normalizedDirectory is null)
            {
                continue;
            }

            foreach (var executableName in descriptor.ExecutableFileNames)
            {
                var candidate = Path.Combine(normalizedDirectory, executableName);
                if (File.Exists(candidate))
                {
                    var source = descriptor.Id == KnownToolIds.RoboCopy
                        ? ToolPathSource.WindowsComponent
                        : ToolPathSource.AutomaticDiscovery;
                    return new ToolPathDiscoveryResult(true, Path.GetFullPath(candidate), source, false);
                }
            }
        }

        return new ToolPathDiscoveryResult(false, null, null, false);
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
