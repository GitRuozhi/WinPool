using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Testing;

public enum RegisteredTestFileExecutionStatus
{
    Succeeded,
    PartiallyCompleted,
    Cancelled,
    Conflict
}

public enum RegisteredTestFileVerificationMode
{
    Metadata,
    SampledContent,
    FullHash,
    PatternReplay
}

public sealed record DeterministicTestFilePattern(
    ulong Seed,
    int RecoveryBlockSizeBytes);

public sealed record CreateRegisteredTestFileRequest(
    string RelativePath,
    DeterministicTestFilePattern Pattern);

public sealed record RegisteredTestFileRecoveryEntry(
    string RelativePath,
    string IdentityToken,
    long PlannedLength,
    long ConfirmedLength,
    ulong PatternSeed,
    int RecoveryBlockSizeBytes,
    string? Sha256,
    DateTimeOffset UpdatedAtUtc);

public sealed record WriteRegisteredTestFileRequest(
    RegisteredTestFileRecoveryEntry Recovery,
    long? MaximumBytesThisCall = null);

public sealed record RegisteredTestFileWriteResult(
    RegisteredTestFileExecutionStatus Status,
    RegisteredTestFileRecoveryEntry Recovery,
    long BytesWrittenThisCall);

public sealed record ReadRegisteredTestFileRequest(
    string RelativePath,
    long Offset,
    int Count);

public sealed record RegisteredTestFileReadResult(
    string RelativePath,
    long Offset,
    byte[] Data);

public sealed record RegisteredTestFileHashResult(
    string RelativePath,
    long Length,
    string Sha256,
    long BytesRead,
    TimeSpan Elapsed);

public sealed record VerifyRegisteredTestFileRequest(
    RegisteredTestFileRecoveryEntry Recovery,
    RegisteredTestFileVerificationMode Mode,
    int SampleCount = 8);

public sealed record RegisteredTestFileVerificationResult(
    string RelativePath,
    RegisteredTestFileVerificationMode Mode,
    bool IsMatch,
    long VerifiedBytes,
    long? FirstMismatchOffset,
    string? ActualSha256);

public sealed record RegisteredTestFileCleanupResult(
    RegisteredTestFileExecutionStatus Status,
    IReadOnlyList<string> RemovedRelativePaths,
    IReadOnlyList<string> MissingRelativePaths,
    IReadOnlyList<string> ConflictRelativePaths);

public sealed record RegisteredExternalFileEvidence(
    string RelativePath,
    string IdentityToken,
    long PlannedLength,
    long ActualLength,
    string Sha256,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc);

public sealed record VerifyRegisteredExternalFilePairRequest(
    string SourceRelativePath,
    string DestinationRelativePath,
    RegisteredTestFileVerificationMode Mode,
    int SampleCount = 16,
    bool RequirePlannedLength = true);

public sealed record RegisteredExternalFilePairVerificationResult(
    RegisteredTestFileVerificationMode Mode,
    bool IsMatch,
    long VerifiedBytes,
    long? FirstMismatchOffset,
    string? SourceSha256,
    string? DestinationSha256);

/// <summary>
/// Performs recoverable file I/O for files registered in one authorized test run.
/// </summary>
/// <remarks>
/// The executor deliberately has no free-form path or command entry point. Every
/// operation rebuilds and revalidates the root, run directory, registration and
/// existing filesystem chain before and after access.
/// </remarks>
public sealed class RegisteredTestFileExecutor
{
    private const int MaximumBufferSize = 16 * 1024 * 1024;
    private const int PatternDigestSize = 32;

    public static readonly AlgorithmIdentity PatternAlgorithm =
        new(
            "ALG-TEST-FILE-PATTERN-001",
            "1.0.0",
            AlgorithmConfidence.Derived,
            "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §5, §9");

    public async Task<RegisteredTestFileRecoveryEntry> CreateAsync(
        AuthorizedTestRun run,
        CreateRegisteredTestFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePattern(request.Pattern);
        cancellationToken.ThrowIfCancellationRequested();

        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.GetRegisteredFile(request.RelativePath);
        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        boundary.EnsureParentDirectories(path);
        path = boundary.ResolveRegisteredPath(registered.RelativePath);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        {
            throw new UnauthorizedAccessException(
                $"The registered test file already exists and will not be overwritten: '{registered.RelativePath}'.");
        }

        boundary.ValidateAfterAccess(registered.RelativePath);
        return new RegisteredTestFileRecoveryEntry(
            registered.RelativePath,
            registered.IdentityToken,
            registered.PlannedLength,
            ConfirmedLength: 0,
            request.Pattern.Seed,
            request.Pattern.RecoveryBlockSizeBytes,
            Sha256: null,
            DateTimeOffset.UtcNow);
    }

    public async Task<RegisteredTestFileWriteResult> WriteAsync(
        AuthorizedTestRun run,
        RegisteredTestFileRecoveryEntry recovery,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                run,
                new WriteRegisteredTestFileRequest(recovery),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<RegisteredTestFileWriteResult> WriteAsync(
        AuthorizedTestRun run,
        WriteRegisteredTestFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Recovery);
        var recovery = request.Recovery;
        if (request.MaximumBytesThisCall is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The per-call write boundary must be positive when specified.");
        }

        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.ValidateRecovery(recovery);
        ValidateRecoveryBoundary(recovery);
        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The registered file must be created before it can be written.",
                path);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            boundary.ValidateAfterAccess(registered.RelativePath);
            return new RegisteredTestFileWriteResult(
                RegisteredTestFileExecutionStatus.Cancelled,
                recovery,
                BytesWrittenThisCall: 0);
        }

        try
        {
            await VerifyRecoveryPrefixAsync(
                    path,
                    recovery,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            boundary.ValidateAfterAccess(registered.RelativePath);
            return new RegisteredTestFileWriteResult(
                RegisteredTestFileExecutionStatus.Cancelled,
                recovery,
                BytesWrittenThisCall: 0);
        }

        boundary.ValidateAfterAccess(registered.RelativePath);

        var confirmedLength = recovery.ConfirmedLength;
        var bytesWritten = 0L;
        var status = RegisteredTestFileExecutionStatus.Succeeded;
        var callTargetLength = recovery.PlannedLength;
        if (request.MaximumBytesThisCall.HasValue)
        {
            callTargetLength = Math.Min(
                recovery.PlannedLength,
                checked(confirmedLength + request.MaximumBytesThisCall.Value));
            if (callTargetLength != recovery.PlannedLength)
            {
                callTargetLength -=
                    (callTargetLength - confirmedLength)
                    % recovery.RecoveryBlockSizeBytes;
                if (callTargetLength == confirmedLength)
                {
                    throw new ArgumentException(
                        "The per-call write boundary must permit at least one complete recovery block.",
                        nameof(request));
                }
            }
        }

        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.Read,
                         recovery.RecoveryBlockSizeBytes,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length != confirmedLength)
            {
                throw new UnauthorizedAccessException(
                    $"The registered file length changed after recovery validation: '{registered.RelativePath}'.");
            }

            stream.Position = confirmedLength;
            var buffer = new byte[recovery.RecoveryBlockSizeBytes];
            try
            {
                while (confirmedLength < callTargetLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(
                        buffer.Length,
                        callTargetLength - confirmedLength);
                    FillPattern(
                        buffer.AsSpan(0, count),
                        confirmedLength,
                        recovery.IdentityToken,
                        recovery.PatternSeed);
                    await stream.WriteAsync(
                            buffer.AsMemory(0, count),
                            cancellationToken)
                        .ConfigureAwait(false);
                    confirmedLength += count;
                    bytesWritten += count;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (stream.Length != confirmedLength)
                {
                    stream.SetLength(confirmedLength);
                }

                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                status = confirmedLength == 0
                    ? RegisteredTestFileExecutionStatus.Cancelled
                    : RegisteredTestFileExecutionStatus.PartiallyCompleted;
            }
        }

        boundary.ValidateAfterAccess(registered.RelativePath);
        string? sha256 = null;
        if (confirmedLength == recovery.PlannedLength)
        {
            try
            {
                var hash = await HashFileAsync(
                        path,
                        registered.RelativePath,
                        recovery.PlannedLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                sha256 = hash.Sha256;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = RegisteredTestFileExecutionStatus.PartiallyCompleted;
            }

            boundary.ValidateAfterAccess(registered.RelativePath);
            if (sha256 is not null)
            {
                status = RegisteredTestFileExecutionStatus.Succeeded;
            }
        }
        else if (status == RegisteredTestFileExecutionStatus.Succeeded)
        {
            status = RegisteredTestFileExecutionStatus.PartiallyCompleted;
        }

        return new RegisteredTestFileWriteResult(
            status,
            recovery with
            {
                ConfirmedLength = confirmedLength,
                Sha256 = sha256,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            bytesWritten);
    }

    public async Task<RegisteredTestFileReadResult> ReadAsync(
        AuthorizedTestRun run,
        ReadRegisteredTestFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.Count < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Read offset and count must be non-negative.");
        }

        if (request.Count > MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A single typed read cannot exceed {MaximumBufferSize} bytes.");
        }

        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.GetRegisteredFile(request.RelativePath);
        if (request.Offset > registered.PlannedLength
            || request.Count > registered.PlannedLength - request.Offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The requested read exceeds the registered planned length.");
        }

        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        var data = new byte[request.Count];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         Math.Max(1, Math.Min(request.Count, 1024 * 1024)),
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            if (request.Offset > stream.Length
                || request.Count > stream.Length - request.Offset)
            {
                throw new EndOfStreamException(
                    "The requested range has not been written completely.");
            }

            stream.Position = request.Offset;
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }

        boundary.ValidateAfterAccess(registered.RelativePath);
        return new RegisteredTestFileReadResult(
            registered.RelativePath,
            request.Offset,
            data);
    }

    public async Task<RegisteredTestFileHashResult> HashAsync(
        AuthorizedTestRun run,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.GetRegisteredFile(relativePath);
        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        var result = await HashFileAsync(
                path,
                registered.RelativePath,
                registered.PlannedLength,
                cancellationToken)
            .ConfigureAwait(false);
        boundary.ValidateAfterAccess(registered.RelativePath);
        return result;
    }

    public async Task<RegisteredTestFileVerificationResult> VerifyAsync(
        AuthorizedTestRun run,
        VerifyRegisteredTestFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SampleCount is <= 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Sample count must be between 1 and 1024.");
        }

        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.ValidateRecovery(request.Recovery);
        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        var result = request.Mode switch
        {
            RegisteredTestFileVerificationMode.Metadata =>
                VerifyMetadata(path, request.Recovery),
            RegisteredTestFileVerificationMode.SampledContent =>
                await VerifySamplesAsync(
                        path,
                        request.Recovery,
                        request.SampleCount,
                        cancellationToken)
                    .ConfigureAwait(false),
            RegisteredTestFileVerificationMode.FullHash =>
                await VerifyHashAsync(
                        path,
                        request.Recovery,
                        cancellationToken)
                    .ConfigureAwait(false),
            RegisteredTestFileVerificationMode.PatternReplay =>
                await VerifyPatternAsync(
                        path,
                        request.Recovery,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Mode,
                "Unknown verification mode.")
        };

        boundary.ValidateAfterAccess(registered.RelativePath);
        return result;
    }

    public async Task<RegisteredExternalFileEvidence> CaptureExternalEvidenceAsync(
        AuthorizedTestRun run,
        string relativePath,
        bool requirePlannedLength,
        CancellationToken cancellationToken)
    {
        var boundary = WorkspaceBoundary.Create(run);
        var registered = boundary.GetRegisteredFile(relativePath);
        var path = boundary.ResolveRegisteredPath(registered.RelativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(
                "The registered external-tool output does not exist.",
                path);
        }

        if (info.Length > registered.PlannedLength
            || requirePlannedLength && info.Length != registered.PlannedLength)
        {
            throw new UnauthorizedAccessException(
                $"The registered external-tool output length is outside its authorized boundary: '{registered.RelativePath}'.");
        }

        var hash = await HashFileAsync(
                path,
                registered.RelativePath,
                registered.PlannedLength,
                cancellationToken)
            .ConfigureAwait(false);
        boundary.ValidateAfterAccess(registered.RelativePath);
        info.Refresh();
        return new(
            registered.RelativePath,
            registered.IdentityToken,
            registered.PlannedLength,
            info.Length,
            hash.Sha256,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc);
    }

    public async Task<RegisteredExternalFilePairVerificationResult> VerifyExternalPairAsync(
        AuthorizedTestRun run,
        VerifyRegisteredExternalFilePairRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SampleCount is <= 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Sample count must be between 1 and 1024.");
        }

        if (request.Mode is RegisteredTestFileVerificationMode.PatternReplay)
        {
            throw new InvalidOperationException(
                "PatternReplay requires a deterministic generator recovery entry and cannot be inferred from arbitrary external-tool output.");
        }

        var boundary = WorkspaceBoundary.Create(run);
        var sourceRegistration = boundary.GetRegisteredFile(request.SourceRelativePath);
        var destinationRegistration = boundary.GetRegisteredFile(
            request.DestinationRelativePath);
        if (StringComparer.OrdinalIgnoreCase.Equals(
                sourceRegistration.RelativePath,
                destinationRegistration.RelativePath))
        {
            throw new ArgumentException(
                "External source and destination must be different registered files.",
                nameof(request));
        }

        var sourcePath = boundary.ResolveRegisteredPath(sourceRegistration.RelativePath);
        var destinationPath = boundary.ResolveRegisteredPath(
            destinationRegistration.RelativePath);
        var source = new FileInfo(sourcePath);
        var destination = new FileInfo(destinationPath);
        if (!source.Exists || !destination.Exists)
        {
            return new(
                request.Mode,
                IsMatch: false,
                VerifiedBytes: 0,
                FirstMismatchOffset: 0,
                SourceSha256: null,
                DestinationSha256: null);
        }

        var lengthsMatch = source.Length == destination.Length
                           && source.Length <= sourceRegistration.PlannedLength
                           && destination.Length <= destinationRegistration.PlannedLength
                           && (!request.RequirePlannedLength
                               || source.Length == sourceRegistration.PlannedLength
                               && destination.Length
                               == destinationRegistration.PlannedLength);
        if (!lengthsMatch)
        {
            boundary.ValidateAfterAccess(sourceRegistration.RelativePath);
            boundary.ValidateAfterAccess(destinationRegistration.RelativePath);
            return new(
                request.Mode,
                IsMatch: false,
                VerifiedBytes: 0,
                FirstMismatchOffset: 0,
                SourceSha256: null,
                DestinationSha256: null);
        }

        if (request.Mode is RegisteredTestFileVerificationMode.Metadata)
        {
            var metadataMatch = source.LastWriteTimeUtc == destination.LastWriteTimeUtc
                                && source.Attributes == destination.Attributes;
            boundary.ValidateAfterAccess(sourceRegistration.RelativePath);
            boundary.ValidateAfterAccess(destinationRegistration.RelativePath);
            return new(
                request.Mode,
                metadataMatch,
                VerifiedBytes: 0,
                FirstMismatchOffset: metadataMatch ? null : 0,
                SourceSha256: null,
                DestinationSha256: null);
        }

        RegisteredExternalFilePairVerificationResult result;
        if (request.Mode is RegisteredTestFileVerificationMode.SampledContent)
        {
            result = await CompareExternalSamplesAsync(
                    sourcePath,
                    destinationPath,
                    source.Length,
                    sourceRegistration.IdentityToken,
                    destinationRegistration.IdentityToken,
                    request.SampleCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var sourceHash = await HashFileAsync(
                    sourcePath,
                    sourceRegistration.RelativePath,
                    sourceRegistration.PlannedLength,
                    cancellationToken)
                .ConfigureAwait(false);
            var destinationHash = await HashFileAsync(
                    destinationPath,
                    destinationRegistration.RelativePath,
                    destinationRegistration.PlannedLength,
                    cancellationToken)
                .ConfigureAwait(false);
            var match = StringComparer.OrdinalIgnoreCase.Equals(
                sourceHash.Sha256,
                destinationHash.Sha256);
            result = new(
                RegisteredTestFileVerificationMode.FullHash,
                match,
                checked(sourceHash.BytesRead + destinationHash.BytesRead),
                match ? null : 0,
                sourceHash.Sha256,
                destinationHash.Sha256);
        }

        boundary.ValidateAfterAccess(sourceRegistration.RelativePath);
        boundary.ValidateAfterAccess(destinationRegistration.RelativePath);
        return result;
    }

    public async Task<RegisteredTestFileCleanupResult> CleanupExternalEvidenceAsync(
        AuthorizedTestRun run,
        IReadOnlyList<RegisteredExternalFileEvidence> evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var boundary = WorkspaceBoundary.Create(run);
        if (run.Plan.Workspace.CleanupPolicy == TestWorkspaceCleanupPolicy.KeepAll)
        {
            throw new UnauthorizedAccessException(
                "The authorized test plan does not permit cleanup.");
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(RegisteredExternalFileEvidence Evidence, string Path)>();
        var missing = new List<string>();
        var conflicts = new List<string>();
        foreach (var item in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registered = boundary.GetRegisteredFile(item.RelativePath);
            if (!uniquePaths.Add(registered.RelativePath)
                || !StringComparer.Ordinal.Equals(
                    registered.IdentityToken,
                    item.IdentityToken)
                || registered.PlannedLength != item.PlannedLength)
            {
                throw new UnauthorizedAccessException(
                    $"External file evidence does not match the registered identity: '{item.RelativePath}'.");
            }

            var path = boundary.ResolveRegisteredPath(registered.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                missing.Add(registered.RelativePath);
                continue;
            }

            if (info.Length != item.ActualLength
                || info.CreationTimeUtc != item.CreationTimeUtc
                || info.LastWriteTimeUtc != item.LastWriteTimeUtc)
            {
                conflicts.Add(registered.RelativePath);
                continue;
            }

            var hash = await HashFileAsync(
                    path,
                    registered.RelativePath,
                    registered.PlannedLength,
                    cancellationToken)
                .ConfigureAwait(false);
            boundary.ValidateAfterAccess(registered.RelativePath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(hash.Sha256, item.Sha256))
            {
                conflicts.Add(registered.RelativePath);
                continue;
            }

            candidates.Add((item, path));
        }

        if (conflicts.Count > 0)
        {
            return new(
                RegisteredTestFileExecutionStatus.Conflict,
                [],
                missing,
                conflicts);
        }

        var removed = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(candidate.Path);
            removed.Add(candidate.Evidence.RelativePath);
        }

        return new(
            RegisteredTestFileExecutionStatus.Succeeded,
            removed,
            missing,
            []);
    }

    public async Task<RegisteredTestFileCleanupResult> CleanupAsync(
        AuthorizedTestRun run,
        IReadOnlyList<RegisteredTestFileRecoveryEntry> recoveryEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryEntries);

        var boundary = WorkspaceBoundary.Create(run);
        if (run.Plan.Workspace.CleanupPolicy == TestWorkspaceCleanupPolicy.KeepAll)
        {
            throw new UnauthorizedAccessException(
                "The authorized test plan does not permit cleanup.");
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(RegisteredTestFileRecoveryEntry Recovery, string Path)>();
        var missing = new List<string>();
        foreach (var recovery in recoveryEntries)
        {
            ArgumentNullException.ThrowIfNull(recovery);
            var registered = boundary.ValidateRecovery(recovery);
            if (!uniquePaths.Add(registered.RelativePath))
            {
                throw new ArgumentException(
                    $"The cleanup list contains a duplicate registered file: '{registered.RelativePath}'.",
                    nameof(recoveryEntries));
            }

            ValidateRecoveryBoundary(recovery);
            var path = boundary.ResolveRegisteredPath(registered.RelativePath);
            if (!File.Exists(path))
            {
                missing.Add(registered.RelativePath);
                continue;
            }

            candidates.Add((recovery, path));
        }

        // Validate every candidate before deleting any file. A conflict leaves
        // the complete run untouched for user inspection and manual recovery.
        var conflicts = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verification = await VerifyPatternAsync(
                    candidate.Path,
                    candidate.Recovery,
                    cancellationToken)
                .ConfigureAwait(false);
            boundary.ValidateAfterAccess(candidate.Recovery.RelativePath);
            if (!verification.IsMatch
                || candidate.Recovery.ConfirmedLength
                   != candidate.Recovery.PlannedLength)
            {
                conflicts.Add(candidate.Recovery.RelativePath);
            }
        }

        if (conflicts.Count > 0)
        {
            return new RegisteredTestFileCleanupResult(
                RegisteredTestFileExecutionStatus.Conflict,
                [],
                missing,
                conflicts);
        }

        var removed = new List<string>();
        var status = RegisteredTestFileExecutionStatus.Succeeded;
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = removed.Count == 0
                    ? RegisteredTestFileExecutionStatus.Cancelled
                    : RegisteredTestFileExecutionStatus.PartiallyCompleted;
                break;
            }

            var resolved = boundary.ResolveRegisteredPath(candidate.Recovery.RelativePath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(resolved, candidate.Path))
            {
                throw new UnauthorizedAccessException(
                    "The registered cleanup target changed after verification.");
            }

            File.Delete(resolved);
            boundary.ValidateAfterAccess(candidate.Recovery.RelativePath);
            if (File.Exists(resolved))
            {
                throw new IOException(
                    $"The registered test file still exists after cleanup: '{candidate.Recovery.RelativePath}'.");
            }

            removed.Add(candidate.Recovery.RelativePath);
        }

        return new RegisteredTestFileCleanupResult(
            status,
            removed,
            missing,
            []);
    }

    private static void ValidatePattern(DeterministicTestFilePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.RecoveryBlockSizeBytes <= 0
            || pattern.RecoveryBlockSizeBytes > MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pattern),
                $"Recovery block size must be between 1 and {MaximumBufferSize} bytes.");
        }
    }

    private static async Task<RegisteredExternalFilePairVerificationResult>
        CompareExternalSamplesAsync(
            string sourcePath,
            string destinationPath,
            long length,
            string sourceIdentity,
            string destinationIdentity,
            int sampleCount,
            CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return new(
                RegisteredTestFileVerificationMode.SampledContent,
                IsMatch: true,
                VerifiedBytes: 0,
                FirstMismatchOffset: null,
                SourceSha256: null,
                DestinationSha256: null);
        }

        const int maximumSampleLength = 64 * 1024;
        var sampleLength = (int)Math.Min(maximumSampleLength, length);
        var offsets = BuildExternalPairSampleOffsets(
            length,
            sampleLength,
            sampleCount,
            sourceIdentity,
            destinationIdentity);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            maximumSampleLength,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            maximumSampleLength,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var sourceBuffer = new byte[sampleLength];
        var destinationBuffer = new byte[sampleLength];
        var verifiedBytes = 0L;
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(sampleLength, length - offset);
            source.Position = offset;
            destination.Position = offset;
            await source.ReadExactlyAsync(
                    sourceBuffer.AsMemory(0, count),
                    cancellationToken)
                .ConfigureAwait(false);
            await destination.ReadExactlyAsync(
                    destinationBuffer.AsMemory(0, count),
                    cancellationToken)
                .ConfigureAwait(false);
            verifiedBytes = checked(verifiedBytes + count * 2L);
            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(
                    destinationBuffer.AsSpan(0, count)))
            {
                return new(
                    RegisteredTestFileVerificationMode.SampledContent,
                    IsMatch: false,
                    verifiedBytes,
                    offset + FirstMismatch(
                        sourceBuffer.AsSpan(0, count),
                        destinationBuffer.AsSpan(0, count)),
                    SourceSha256: null,
                    DestinationSha256: null);
            }
        }

        return new(
            RegisteredTestFileVerificationMode.SampledContent,
            IsMatch: true,
            verifiedBytes,
            FirstMismatchOffset: null,
            SourceSha256: null,
            DestinationSha256: null);
    }

    private static IReadOnlyList<long> BuildExternalPairSampleOffsets(
        long length,
        int sampleLength,
        int sampleCount,
        string sourceIdentity,
        string destinationIdentity)
    {
        var maximumOffset = length - sampleLength;
        if (maximumOffset <= 0)
        {
            return [0];
        }

        var offsets = new SortedSet<long> { 0, maximumOffset };
        var seed = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{sourceIdentity}|{destinationIdentity}|{length}|{sampleCount}"));
        var material = new byte[seed.Length + sizeof(int)];
        seed.CopyTo(material, 0);
        for (var index = 0; offsets.Count < sampleCount && index < sampleCount * 8; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                material.AsSpan(seed.Length),
                index);
            var digest = SHA256.HashData(material);
            var candidate = (long)(BinaryPrimitives.ReadUInt64LittleEndian(digest)
                                   % (ulong)(maximumOffset + 1));
            offsets.Add(candidate);
        }

        return offsets.Take(sampleCount).ToArray();
    }

    private static void ValidateRecoveryBoundary(
        RegisteredTestFileRecoveryEntry recovery)
    {
        ValidatePattern(
            new DeterministicTestFilePattern(
                recovery.PatternSeed,
                recovery.RecoveryBlockSizeBytes));
        if (recovery.PlannedLength < 0
            || recovery.ConfirmedLength < 0
            || recovery.ConfirmedLength > recovery.PlannedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recovery),
                "Recovery lengths are outside the registered file boundary.");
        }

        if (recovery.ConfirmedLength != recovery.PlannedLength
            && recovery.ConfirmedLength % recovery.RecoveryBlockSizeBytes != 0)
        {
            throw new ArgumentException(
                "The confirmed recovery length must end at a complete recovery block.",
                nameof(recovery));
        }

        if (recovery.Sha256 is not null
            && (recovery.Sha256.Length != 64
                || !recovery.Sha256.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException(
                "A recovery SHA-256 value must be 64 hexadecimal characters.",
                nameof(recovery));
        }
    }

    private static async Task VerifyRecoveryPrefixAsync(
        string path,
        RegisteredTestFileRecoveryEntry recovery,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryBoundary(recovery);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            recovery.RecoveryBlockSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != recovery.ConfirmedLength)
        {
            throw new UnauthorizedAccessException(
                $"The registered file length does not match its recovery entry: '{recovery.RelativePath}'.");
        }

        var mismatch = await FindFirstPatternMismatchAsync(
                stream,
                recovery,
                recovery.ConfirmedLength,
                cancellationToken)
            .ConfigureAwait(false);
        if (mismatch.HasValue)
        {
            throw new UnauthorizedAccessException(
                $"The registered file identity or deterministic content does not match at offset {mismatch.Value}: '{recovery.RelativePath}'.");
        }
    }

    private static RegisteredTestFileVerificationResult VerifyMetadata(
        string path,
        RegisteredTestFileRecoveryEntry recovery)
    {
        var file = new FileInfo(path);
        var isMatch = file.Exists
                      && file.Length == recovery.PlannedLength
                      && recovery.ConfirmedLength == recovery.PlannedLength;
        return new RegisteredTestFileVerificationResult(
            recovery.RelativePath,
            RegisteredTestFileVerificationMode.Metadata,
            isMatch,
            VerifiedBytes: 0,
            FirstMismatchOffset: isMatch ? null : 0,
            ActualSha256: null);
    }

    private static async Task<RegisteredTestFileVerificationResult> VerifySamplesAsync(
        string path,
        RegisteredTestFileRecoveryEntry recovery,
        int sampleCount,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryBoundary(recovery);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            recovery.RecoveryBlockSizeBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != recovery.PlannedLength
            || recovery.ConfirmedLength != recovery.PlannedLength)
        {
            return new RegisteredTestFileVerificationResult(
                recovery.RelativePath,
                RegisteredTestFileVerificationMode.SampledContent,
                IsMatch: false,
                VerifiedBytes: 0,
                FirstMismatchOffset: Math.Min(stream.Length, recovery.PlannedLength),
                ActualSha256: null);
        }

        if (stream.Length == 0)
        {
            return new RegisteredTestFileVerificationResult(
                recovery.RelativePath,
                RegisteredTestFileVerificationMode.SampledContent,
                IsMatch: true,
                VerifiedBytes: 0,
                FirstMismatchOffset: null,
                ActualSha256: null);
        }

        var sampleLength = Math.Min(recovery.RecoveryBlockSizeBytes, 64 * 1024);
        var offsets = BuildSampleOffsets(
            recovery,
            sampleCount,
            sampleLength);
        var actual = new byte[sampleLength];
        var expected = new byte[sampleLength];
        var verifiedBytes = 0L;
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(sampleLength, stream.Length - offset);
            stream.Position = offset;
            await stream.ReadExactlyAsync(
                    actual.AsMemory(0, count),
                    cancellationToken)
                .ConfigureAwait(false);
            FillPattern(
                expected.AsSpan(0, count),
                offset,
                recovery.IdentityToken,
                recovery.PatternSeed);
            var mismatchIndex = actual.AsSpan(0, count)
                .SequenceCompareTo(expected.AsSpan(0, count));
            verifiedBytes += count;
            if (mismatchIndex != 0)
            {
                var first = FirstMismatch(
                    actual.AsSpan(0, count),
                    expected.AsSpan(0, count));
                return new RegisteredTestFileVerificationResult(
                    recovery.RelativePath,
                    RegisteredTestFileVerificationMode.SampledContent,
                    IsMatch: false,
                    verifiedBytes,
                    offset + first,
                    ActualSha256: null);
            }
        }

        return new RegisteredTestFileVerificationResult(
            recovery.RelativePath,
            RegisteredTestFileVerificationMode.SampledContent,
            IsMatch: true,
            verifiedBytes,
            FirstMismatchOffset: null,
            ActualSha256: null);
    }

    private static async Task<RegisteredTestFileVerificationResult> VerifyHashAsync(
        string path,
        RegisteredTestFileRecoveryEntry recovery,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryBoundary(recovery);
        var hash = await HashFileAsync(
                path,
                recovery.RelativePath,
                recovery.PlannedLength,
                cancellationToken)
            .ConfigureAwait(false);
        var isMatch = recovery.Sha256 is not null
                      && recovery.ConfirmedLength == recovery.PlannedLength
                      && hash.Length == recovery.PlannedLength
                      && StringComparer.OrdinalIgnoreCase.Equals(
                          recovery.Sha256,
                          hash.Sha256);
        return new RegisteredTestFileVerificationResult(
            recovery.RelativePath,
            RegisteredTestFileVerificationMode.FullHash,
            isMatch,
            hash.BytesRead,
            FirstMismatchOffset: isMatch ? null : 0,
            hash.Sha256);
    }

    private static async Task<RegisteredTestFileVerificationResult> VerifyPatternAsync(
        string path,
        RegisteredTestFileRecoveryEntry recovery,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryBoundary(recovery);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            recovery.RecoveryBlockSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != recovery.PlannedLength
            || recovery.ConfirmedLength != recovery.PlannedLength)
        {
            return new RegisteredTestFileVerificationResult(
                recovery.RelativePath,
                RegisteredTestFileVerificationMode.PatternReplay,
                IsMatch: false,
                VerifiedBytes: 0,
                FirstMismatchOffset: Math.Min(stream.Length, recovery.PlannedLength),
                ActualSha256: null);
        }

        var mismatch = await FindFirstPatternMismatchAsync(
                stream,
                recovery,
                recovery.PlannedLength,
                cancellationToken)
            .ConfigureAwait(false);
        return new RegisteredTestFileVerificationResult(
            recovery.RelativePath,
            RegisteredTestFileVerificationMode.PatternReplay,
            IsMatch: !mismatch.HasValue,
            mismatch ?? recovery.PlannedLength,
            mismatch,
            ActualSha256: null);
    }

    private static async Task<long?> FindFirstPatternMismatchAsync(
        FileStream stream,
        RegisteredTestFileRecoveryEntry recovery,
        long length,
        CancellationToken cancellationToken)
    {
        var actual = new byte[recovery.RecoveryBlockSizeBytes];
        var expected = new byte[recovery.RecoveryBlockSizeBytes];
        var offset = 0L;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(actual.Length, length - offset);
            await stream.ReadExactlyAsync(
                    actual.AsMemory(0, count),
                    cancellationToken)
                .ConfigureAwait(false);
            FillPattern(
                expected.AsSpan(0, count),
                offset,
                recovery.IdentityToken,
                recovery.PatternSeed);
            if (!actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count)))
            {
                return offset + FirstMismatch(
                    actual.AsSpan(0, count),
                    expected.AsSpan(0, count));
            }

            offset += count;
        }

        return null;
    }

    private static IReadOnlyList<long> BuildSampleOffsets(
        RegisteredTestFileRecoveryEntry recovery,
        int sampleCount,
        int sampleLength)
    {
        var maximumOffset = Math.Max(0, recovery.PlannedLength - sampleLength);
        var offsets = new SortedSet<long> { 0, maximumOffset };
        var identityBytes = Encoding.UTF8.GetBytes(recovery.IdentityToken);
        var identityDigest = SHA256.HashData(identityBytes);
        var state = recovery.PatternSeed
                    ^ BinaryPrimitives.ReadUInt64LittleEndian(identityDigest);
        while (offsets.Count < Math.Min(sampleCount, maximumOffset + 1))
        {
            // SplitMix64 gives stable, well-distributed sample positions. This
            // is deterministic verification sampling, not a security primitive.
            state += 0x9E3779B97F4A7C15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            offsets.Add((long)(value % (ulong)(maximumOffset + 1)));
        }

        return offsets.ToArray();
    }

    private static int FirstMismatch(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected)
    {
        for (var index = 0; index < actual.Length; index++)
        {
            if (actual[index] != expected[index])
            {
                return index;
            }
        }

        return actual.Length;
    }

    private static void FillPattern(
        Span<byte> destination,
        long absoluteOffset,
        string identityToken,
        ulong seed)
    {
        var identityDigest = SHA256.HashData(Encoding.UTF8.GetBytes(identityToken));
        Span<byte> material = stackalloc byte[PatternDigestSize + sizeof(ulong) + sizeof(long)];
        identityDigest.CopyTo(material);
        BinaryPrimitives.WriteUInt64LittleEndian(
            material.Slice(PatternDigestSize, sizeof(ulong)),
            seed);
        Span<byte> digest = stackalloc byte[PatternDigestSize];

        var written = 0;
        while (written < destination.Length)
        {
            var currentOffset = checked(absoluteOffset + written);
            var chunkIndex = currentOffset / PatternDigestSize;
            var chunkOffset = (int)(currentOffset % PatternDigestSize);
            BinaryPrimitives.WriteInt64LittleEndian(
                material.Slice(PatternDigestSize + sizeof(ulong), sizeof(long)),
                chunkIndex);
            SHA256.HashData(material, digest);
            var count = Math.Min(
                PatternDigestSize - chunkOffset,
                destination.Length - written);
            digest.Slice(chunkOffset, count)
                .CopyTo(destination.Slice(written, count));
            written += count;
        }
    }

    private static async Task<RegisteredTestFileHashResult> HashFileAsync(
        string path,
        string relativePath,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumLength)
        {
            throw new UnauthorizedAccessException(
                $"The registered test file exceeds its authorized length: '{relativePath}'.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var bytesRead = 0L;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, count);
            bytesRead += count;
        }

        stopwatch.Stop();
        return new RegisteredTestFileHashResult(
            relativePath,
            stream.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            bytesRead,
            stopwatch.Elapsed);
    }

    private sealed class WorkspaceBoundary
    {
        private static readonly char[] DirectorySeparators =
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        private static readonly HashSet<string> ReservedWindowsNames = new(
            [
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "CLOCK$",
                "CONIN$",
                "CONOUT$",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9"
            ],
            StringComparer.OrdinalIgnoreCase);

        private readonly IReadOnlyDictionary<string, RegisteredTestFile> _registered;

        private WorkspaceBoundary(
            string rootDirectory,
            string runDirectory,
            IReadOnlyDictionary<string, RegisteredTestFile> registered,
            DateTimeOffset expiresAtUtc)
        {
            RootDirectory = rootDirectory;
            RunDirectory = runDirectory;
            _registered = registered;
            ExpiresAtUtc = expiresAtUtc;
        }

        private string RootDirectory { get; }

        private string RunDirectory { get; }

        private DateTimeOffset ExpiresAtUtc { get; }

        public static WorkspaceBoundary Create(AuthorizedTestRun run)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (!ReferenceEquals(run.Workspace.Plan, run.Plan.Workspace))
            {
                throw new UnauthorizedAccessException(
                    "The authorized workspace is not bound to the test plan.");
            }

            if (run.Workspace.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new UnauthorizedAccessException(
                    "The authorized test workspace has expired.");
            }

            if (!run.Plan.Target.IsWriteAllowed
                || run.Plan.Risk < RiskLevel.R2RecoverableFileWrite)
            {
                throw new UnauthorizedAccessException(
                    "The authorized test plan does not permit recoverable file writes.");
            }

            var root = NormalizeAbsoluteDirectory(
                run.Plan.Workspace.NormalizedRootDirectory,
                "test root");
            var targetRoot = NormalizeAbsoluteDirectory(
                run.Plan.Target.TestRootDirectory,
                "target test root");
            if (!StringComparer.OrdinalIgnoreCase.Equals(root, targetRoot))
            {
                throw new UnauthorizedAccessException(
                    "The authorized test root does not match the planned target root.");
            }

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(
                    $"The authorized test root does not exist: '{root}'.");
            }

            ValidateExistingDirectoryChain(root);
            var runDirectory = NormalizeAbsoluteDirectory(
                run.Plan.Workspace.RunDirectory,
                "run directory");
            if (!IsDescendant(root, runDirectory))
            {
                throw new UnauthorizedAccessException(
                    "The planned run directory must be a descendant of the authorized test root.");
            }

            ValidateRelativePath(Path.GetRelativePath(root, runDirectory));
            ValidateExistingTargetChain(root, runDirectory, expectFile: false);

            var registered = new Dictionary<string, RegisteredTestFile>(
                StringComparer.OrdinalIgnoreCase);
            var totalLength = 0L;
            foreach (var file in run.Plan.Workspace.RegisteredFiles)
            {
                ArgumentNullException.ThrowIfNull(file);
                var normalizedRelative = NormalizeRegisteredRelativePath(
                    root,
                    file.RelativePath);
                var absolute = Path.GetFullPath(
                    Path.Combine(root, normalizedRelative));
                if (!IsDescendant(runDirectory, absolute))
                {
                    throw new UnauthorizedAccessException(
                        $"A registered test file is outside the planned run directory: '{file.RelativePath}'.");
                }

                if (file.PlannedLength <= 0
                    || string.IsNullOrWhiteSpace(file.IdentityToken))
                {
                    throw new UnauthorizedAccessException(
                        $"A registered test file has invalid length or identity: '{file.RelativePath}'.");
                }

                if (!registered.TryAdd(
                        normalizedRelative,
                        file with { RelativePath = normalizedRelative }))
                {
                    throw new UnauthorizedAccessException(
                        $"The test plan registers the same file more than once: '{file.RelativePath}'.");
                }

                totalLength = checked(totalLength + file.PlannedLength);
                ValidateExistingTargetChain(root, absolute, expectFile: true);
            }

            if (totalLength > run.Plan.Workspace.MaximumWriteBytes
                || totalLength > run.Plan.EstimatedWriteBytes)
            {
                throw new UnauthorizedAccessException(
                    "The registered file lengths exceed the authorized write boundary.");
            }

            return new WorkspaceBoundary(
                root,
                runDirectory,
                registered,
                run.Workspace.ExpiresAtUtc);
        }

        public RegisteredTestFile GetRegisteredFile(string relativePath)
        {
            ValidateCurrentBoundary();
            var normalized = NormalizeRegisteredRelativePath(
                RootDirectory,
                relativePath);
            if (!_registered.TryGetValue(normalized, out var registered))
            {
                throw new UnauthorizedAccessException(
                    $"The test file is not registered for this run: '{relativePath}'.");
            }

            return registered;
        }

        public RegisteredTestFile ValidateRecovery(
            RegisteredTestFileRecoveryEntry recovery)
        {
            var registered = GetRegisteredFile(recovery.RelativePath);
            if (!StringComparer.Ordinal.Equals(
                    registered.IdentityToken,
                    recovery.IdentityToken)
                || registered.PlannedLength != recovery.PlannedLength)
            {
                throw new UnauthorizedAccessException(
                    $"The recovery entry does not match the registered file identity: '{recovery.RelativePath}'.");
            }

            return registered;
        }

        public string ResolveRegisteredPath(string relativePath)
        {
            var registered = GetRegisteredFile(relativePath);
            var absolute = Path.GetFullPath(
                Path.Combine(RootDirectory, registered.RelativePath));
            if (!IsDescendant(RunDirectory, absolute))
            {
                throw new UnauthorizedAccessException(
                    $"The registered test path escapes its run directory: '{relativePath}'.");
            }

            ValidateExistingTargetChain(
                RootDirectory,
                absolute,
                expectFile: true);
            return absolute;
        }

        public void EnsureParentDirectories(string absoluteFilePath)
        {
            ValidateCurrentBoundary();
            var parent = Path.GetDirectoryName(absoluteFilePath)
                         ?? throw new UnauthorizedAccessException(
                             "A registered test file has no parent directory.");
            if (!IsDescendantOrSame(RunDirectory, parent))
            {
                throw new UnauthorizedAccessException(
                    "A registered test file parent is outside the run directory.");
            }

            var relativeParent = Path.GetRelativePath(RootDirectory, parent);
            var current = RootDirectory;
            foreach (var segment in relativeParent.Split(
                         DirectorySeparators,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                ValidateExistingTargetChain(
                    RootDirectory,
                    current,
                    expectFile: false);
                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                }

                var attributes = File.GetAttributes(current);
                RejectReparsePoint(current, attributes);
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new UnauthorizedAccessException(
                        $"A test run path component is not a directory: '{current}'.");
                }
            }

            ValidateCurrentBoundary();
        }

        public void ValidateAfterAccess(string relativePath)
        {
            ValidateCurrentBoundary();
            var registered = GetRegisteredFile(relativePath);
            var absolute = Path.GetFullPath(
                Path.Combine(RootDirectory, registered.RelativePath));
            ValidateExistingTargetChain(
                RootDirectory,
                absolute,
                expectFile: true);
        }

        private void ValidateCurrentBoundary()
        {
            if (ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new UnauthorizedAccessException(
                    "The authorized test workspace expired during file access.");
            }

            if (!Directory.Exists(RootDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"The authorized test root no longer exists: '{RootDirectory}'.");
            }

            ValidateExistingDirectoryChain(RootDirectory);
            ValidateExistingTargetChain(
                RootDirectory,
                RunDirectory,
                expectFile: false);
        }

        private static string NormalizeAbsoluteDirectory(
            string path,
            string description)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(path))
            {
                throw new UnauthorizedAccessException(
                    $"The authorized {description} must be a fully qualified path.");
            }

            var normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)
                || normalized.StartsWith(@"\\.\", StringComparison.Ordinal)
                || normalized.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Windows device and extended namespaces cannot be an authorized {description}.");
            }

            return normalized;
        }

        private static string NormalizeRegisteredRelativePath(
            string root,
            string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath))
            {
                throw new UnauthorizedAccessException(
                    $"A registered test path must be relative: '{relativePath}'.");
            }

            ValidateRelativePath(relativePath);
            var absolute = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!IsDescendant(root, absolute))
            {
                throw new UnauthorizedAccessException(
                    $"A registered test path escapes the authorized root: '{relativePath}'.");
            }

            var normalized = Path.GetRelativePath(root, absolute);
            ValidateRelativePath(normalized);
            return normalized;
        }

        private static void ValidateRelativePath(string relativePath)
        {
            var segments = relativePath.Split(
                DirectorySeparators,
                StringSplitOptions.None);
            if (segments.Any(IsUnsafePathSegment))
            {
                throw new UnauthorizedAccessException(
                    $"The test path is invalid or traverses directories: '{relativePath}'.");
            }
        }

        private static bool IsUnsafePathSegment(string segment)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.'))
            {
                return true;
            }

            var firstDot = segment.IndexOf('.');
            var deviceBaseName = firstDot < 0 ? segment : segment[..firstDot];
            return ReservedWindowsNames.Contains(deviceBaseName);
        }

        private static void ValidateExistingDirectoryChain(string directory)
        {
            for (DirectoryInfo? current = new(directory);
                 current is not null;
                 current = current.Parent)
            {
                var attributes = File.GetAttributes(current.FullName);
                RejectReparsePoint(current.FullName, attributes);
            }
        }

        private static void ValidateExistingTargetChain(
            string root,
            string absolutePath,
            bool expectFile)
        {
            if (!IsDescendantOrSame(root, absolutePath))
            {
                throw new UnauthorizedAccessException(
                    $"The test path escapes the authorized root: '{absolutePath}'.");
            }

            var relativePath = Path.GetRelativePath(root, absolutePath);
            if (relativePath == ".")
            {
                return;
            }

            var segments = relativePath.Split(
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

                RejectReparsePoint(current, attributes);
                var isLast = index == segments.Length - 1;
                if ((!isLast || !expectFile)
                    && !attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new UnauthorizedAccessException(
                        $"A test path component is not a directory: '{current}'.");
                }

                if (isLast
                    && expectFile
                    && attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new UnauthorizedAccessException(
                        $"A registered test file resolves to a directory: '{current}'.");
                }
            }
        }

        private static bool IsDescendant(string root, string candidate) =>
            !StringComparer.OrdinalIgnoreCase.Equals(root, candidate)
            && IsDescendantOrSame(root, candidate);

        private static bool IsDescendantOrSame(string root, string candidate)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(root, candidate))
            {
                return true;
            }

            var prefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePoint(
            string path,
            FileAttributes attributes)
        {
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Reparse points are forbidden in an authorized test path: '{path}'.");
            }
        }
    }
}
