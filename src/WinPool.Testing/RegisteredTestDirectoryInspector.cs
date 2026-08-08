using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinPool.Application;

namespace WinPool.Testing;

public sealed record RegisteredDirectoryEntryEvidence(
    string RelativePath,
    long Length,
    FileAttributes Attributes,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256);

public sealed record RegisteredDirectoryEvidence(
    string RelativePath,
    string IdentityToken,
    long MaximumBytes,
    int MaximumFileCount,
    long ActualBytes,
    int ActualFileCount,
    IReadOnlyList<RegisteredDirectoryEntryEvidence> Entries);

public sealed record VerifyRegisteredDirectoryPairRequest(
    string SourceRelativePath,
    string DestinationRelativePath,
    RegisteredTestFileVerificationMode Mode,
    int SampleCount = 32);

public sealed record RegisteredDirectoryPairVerificationResult(
    RegisteredTestFileVerificationMode Mode,
    bool IsMatch,
    int ComparedFileCount,
    long ComparedBytes,
    string? FirstMismatchRelativePath);

/// <summary>
/// Read-only validation for external generators and directory copy tools.
/// Generation and copying remain external-tool responsibilities.
/// </summary>
public sealed class RegisteredTestDirectoryInspector
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public async Task<RegisteredDirectoryEvidence> CaptureAsync(
        AuthorizedTestRun run,
        string relativePath,
        bool includeHashes,
        CancellationToken cancellationToken)
    {
        var registration = ResolveRegistration(run, relativePath);
        var root = Path.GetFullPath(run.Plan.Workspace.NormalizedRootDirectory);
        var absolute = ResolveDirectory(run, registration);
        if (!Directory.Exists(absolute))
        {
            throw new DirectoryNotFoundException(
                $"The registered external-tool directory does not exist: '{registration.RelativePath}'.");
        }

        var entries = new List<RegisteredDirectoryEntryEvidence>();
        var pending = new Stack<string>();
        pending.Push(absolute);
        var totalBytes = 0L;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            RejectReparsePoint(current);
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException(
                        $"Reparse points are not allowed in registered test directories: '{entry.FullName}'.");
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory.FullName);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw new UnauthorizedAccessException(
                        $"Unsupported filesystem entry in registered test directory: '{entry.FullName}'.");
                }

                var relativeToRegistration = Path.GetRelativePath(absolute, file.FullName);
                ValidateRelative(relativeToRegistration);
                totalBytes = checked(totalBytes + file.Length);
                if (entries.Count >= registration.MaximumFileCount
                    || totalBytes > registration.MaximumBytes)
                {
                    throw new UnauthorizedAccessException(
                        $"The registered directory exceeded its authorized file-count or byte boundary: '{registration.RelativePath}'.");
                }

                string? hash = null;
                if (includeHashes)
                {
                    await using var stream = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    hash = Convert.ToHexString(
                            await SHA256.HashDataAsync(stream, cancellationToken))
                        .ToLowerInvariant();
                }

                entries.Add(
                    new(
                        relativeToRegistration,
                        file.Length,
                        file.Attributes,
                        file.LastWriteTimeUtc,
                        hash));
            }
        }

        ValidateExistingChain(root, absolute);
        return new(
            registration.RelativePath,
            registration.IdentityToken,
            registration.MaximumBytes,
            registration.MaximumFileCount,
            totalBytes,
            entries.Count,
            entries.OrderBy(
                    item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<RegisteredDirectoryPairVerificationResult> VerifyPairAsync(
        AuthorizedTestRun run,
        VerifyRegisteredDirectoryPairRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SampleCount is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Directory sample count must be between 1 and 4096.");
        }

        if (request.Mode is RegisteredTestFileVerificationMode.PatternReplay)
        {
            throw new InvalidOperationException(
                "PatternReplay requires per-file deterministic generator recovery entries.");
        }

        var includeHashes =
            request.Mode is RegisteredTestFileVerificationMode.FullHash;
        var source = await CaptureAsync(
            run,
            request.SourceRelativePath,
            includeHashes,
            cancellationToken);
        var destination = await CaptureAsync(
            run,
            request.DestinationRelativePath,
            includeHashes,
            cancellationToken);
        if (source.ActualFileCount != destination.ActualFileCount
            || source.ActualBytes != destination.ActualBytes)
        {
            return new(
                request.Mode,
                IsMatch: false,
                ComparedFileCount: 0,
                ComparedBytes: 0,
                FirstMismatchRelativePath: string.Empty);
        }

        var destinationByPath = destination.Entries.ToDictionary(
            item => item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var sourceEntry in source.Entries)
        {
            if (!destinationByPath.TryGetValue(
                    sourceEntry.RelativePath,
                    out var destinationEntry)
                || sourceEntry.Length != destinationEntry.Length)
            {
                return new(
                    request.Mode,
                    IsMatch: false,
                    ComparedFileCount: 0,
                    ComparedBytes: 0,
                    sourceEntry.RelativePath);
            }

            if (request.Mode is RegisteredTestFileVerificationMode.Metadata
                && (sourceEntry.Attributes != destinationEntry.Attributes
                    || sourceEntry.LastWriteTimeUtc
                    != destinationEntry.LastWriteTimeUtc))
            {
                return new(
                    request.Mode,
                    IsMatch: false,
                    ComparedFileCount: 0,
                    ComparedBytes: 0,
                    sourceEntry.RelativePath);
            }

            if (includeHashes
                && !StringComparer.OrdinalIgnoreCase.Equals(
                    sourceEntry.Sha256,
                    destinationEntry.Sha256))
            {
                return new(
                    request.Mode,
                    IsMatch: false,
                    ComparedFileCount: 0,
                    ComparedBytes: 0,
                    sourceEntry.RelativePath);
            }
        }

        if (request.Mode is not RegisteredTestFileVerificationMode.SampledContent)
        {
            return new(
                request.Mode,
                IsMatch: true,
                source.ActualFileCount,
                includeHashes ? checked(source.ActualBytes * 2) : 0,
                FirstMismatchRelativePath: null);
        }

        var sampled = source.Entries
            .OrderBy(
                item => SampleOrder(
                    source.IdentityToken,
                    destination.IdentityToken,
                    item.RelativePath))
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(request.SampleCount)
            .ToArray();
        var comparedBytes = 0L;
        foreach (var sourceEntry in sampled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationEntry = destinationByPath[sourceEntry.RelativePath];
            var sourceHash = await HashEntryAsync(
                run,
                source,
                sourceEntry,
                cancellationToken);
            var destinationHash = await HashEntryAsync(
                run,
                destination,
                destinationEntry,
                cancellationToken);
            comparedBytes = checked(comparedBytes + sourceEntry.Length * 2);
            if (!StringComparer.OrdinalIgnoreCase.Equals(sourceHash, destinationHash))
            {
                return new(
                    request.Mode,
                    IsMatch: false,
                    sampled.Length,
                    comparedBytes,
                    sourceEntry.RelativePath);
            }
        }

        return new(
            request.Mode,
            IsMatch: true,
            sampled.Length,
            comparedBytes,
            FirstMismatchRelativePath: null);
    }

    private static async Task<string> HashEntryAsync(
        AuthorizedTestRun run,
        RegisteredDirectoryEvidence directory,
        RegisteredDirectoryEntryEvidence entry,
        CancellationToken cancellationToken)
    {
        var registration = ResolveRegistration(run, directory.RelativePath);
        var absoluteDirectory = ResolveDirectory(run, registration);
        var absolute = Path.GetFullPath(
            Path.Combine(absoluteDirectory, entry.RelativePath));
        EnsureDescendant(absoluteDirectory, absolute);
        ValidateExistingChain(
            Path.GetFullPath(run.Plan.Workspace.NormalizedRootDirectory),
            absolute);
        await using var stream = new FileStream(
            absolute,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static ulong SampleOrder(
        string sourceIdentity,
        string destinationIdentity,
        string relativePath)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{sourceIdentity}|{destinationIdentity}|{relativePath.ToUpperInvariant()}"));
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    private static RegisteredTestDirectory ResolveRegistration(
        AuthorizedTestRun run,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!ReferenceEquals(run.Workspace.Plan, run.Plan.Workspace)
            || run.Workspace.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || !run.Plan.Target.IsWriteAllowed
            || run.Plan.Risk < WinPool.Execution.RiskLevel.R2RecoverableFileWrite)
        {
            throw new UnauthorizedAccessException(
                "The registered directory workspace is not currently authorized.");
        }

        var root = Path.GetFullPath(run.Plan.Workspace.NormalizedRootDirectory);
        var normalized = NormalizeRelative(root, relativePath);
        var runDirectory = Path.GetFullPath(run.Plan.Workspace.RunDirectory);
        var registrations = new Dictionary<string, RegisteredTestDirectory>(
            StringComparer.OrdinalIgnoreCase);
        var totalMaximumBytes = run.Plan.Workspace.RegisteredFiles.Aggregate(
            0L,
            (current, file) => checked(current + file.PlannedLength));
        foreach (var item in run.Plan.Workspace.RegisteredDirectories)
        {
            var itemRelative = NormalizeRelative(root, item.RelativePath);
            var itemAbsolute = Path.GetFullPath(Path.Combine(root, itemRelative));
            EnsureDescendant(runDirectory, itemAbsolute);
            if (item.MaximumBytes <= 0
                || item.MaximumFileCount <= 0
                || item.MaximumFileCount > 1_000_000
                || string.IsNullOrWhiteSpace(item.IdentityToken)
                || !registrations.TryAdd(
                    itemRelative,
                    item with { RelativePath = itemRelative }))
            {
                throw new UnauthorizedAccessException(
                    $"The test plan contains an invalid or duplicate registered directory: '{item.RelativePath}'.");
            }

            totalMaximumBytes = checked(totalMaximumBytes + item.MaximumBytes);
        }

        var orderedPaths = registrations.Keys
            .OrderBy(path => path.Length)
            .ToArray();
        for (var index = 0; index < orderedPaths.Length; index++)
        {
            var parent = Path.GetFullPath(Path.Combine(root, orderedPaths[index]));
            for (var childIndex = index + 1;
                 childIndex < orderedPaths.Length;
                 childIndex++)
            {
                var child = Path.GetFullPath(
                    Path.Combine(root, orderedPaths[childIndex]));
                if (child.StartsWith(
                        Path.TrimEndingDirectorySeparator(parent)
                        + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        "Registered test directories cannot overlap.");
                }
            }

            foreach (var file in run.Plan.Workspace.RegisteredFiles)
            {
                var fileAbsolute = Path.GetFullPath(
                    Path.Combine(
                        root,
                        NormalizeRelative(root, file.RelativePath)));
                if (fileAbsolute.StartsWith(
                        Path.TrimEndingDirectorySeparator(parent)
                        + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        "A registered test file cannot overlap a registered test directory.");
                }
            }
        }

        if (totalMaximumBytes > run.Plan.Workspace.MaximumWriteBytes
            || totalMaximumBytes > run.Plan.EstimatedWriteBytes)
        {
            throw new UnauthorizedAccessException(
                "Registered directory and file limits exceed the authorized write boundary.");
        }

        registrations.TryGetValue(normalized, out var registration);
        if (registration is null
            || registration.MaximumBytes <= 0)
        {
            throw new UnauthorizedAccessException(
                $"The test directory is not validly registered: '{relativePath}'.");
        }

        return registration with { RelativePath = normalized };
    }

    private static string ResolveDirectory(
        AuthorizedTestRun run,
        RegisteredTestDirectory registration)
    {
        var root = Path.GetFullPath(run.Plan.Workspace.NormalizedRootDirectory);
        var targetRoot = Path.GetFullPath(run.Plan.Target.TestRootDirectory);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(targetRoot)))
        {
            throw new UnauthorizedAccessException(
                "The registered directory root does not match the test target.");
        }

        var runDirectory = Path.GetFullPath(run.Plan.Workspace.RunDirectory);
        var absolute = Path.GetFullPath(
            Path.Combine(root, registration.RelativePath));
        EnsureDescendant(root, runDirectory);
        EnsureDescendant(runDirectory, absolute);
        ValidateExistingChain(root, absolute);
        return absolute;
    }

    private static string NormalizeRelative(string root, string relativePath)
    {
        ValidateRelative(relativePath);
        var absolute = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureDescendant(root, absolute);
        return Path.GetRelativePath(root, absolute);
    }

    private static void ValidateRelative(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Split(Separators, StringSplitOptions.None)
                .Any(segment => string.IsNullOrWhiteSpace(segment)
                                || segment is "." or ".."
                                || segment.IndexOfAny(
                                    Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new UnauthorizedAccessException(
                $"The registered directory path is invalid: '{relativePath}'.");
        }
    }

    private static void ValidateExistingChain(string root, string target)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"The authorized test root does not exist: '{root}'.");
        }

        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, target);
        if (relative is ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     Separators,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return;
            }

            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"Reparse points are not allowed in registered test directories: '{path}'.");
        }
    }

    private static void EnsureDescendant(string parent, string child)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent));
        var prefix = normalizedParent + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(child).StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The registered directory escapes its authorized parent: '{child}'.");
        }
    }
}
