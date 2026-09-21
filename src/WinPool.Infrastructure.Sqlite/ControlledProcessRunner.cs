using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// A deliberately narrow child-process runner. Callers provide an executable
/// path and already-tokenized arguments; it never consults PATH, a shell, or
/// a command string supplied by a user.
/// </summary>
public interface IControlledProcessRunner
{
    Task<ControlledProcessResult> RunAsync(
        ControlledProcessInvocation invocation,
        CancellationToken cancellationToken);

    Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
        ControlledProcessInvocation invocation,
        Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
        CancellationToken cancellationToken);
}

public sealed record ControlledProcessInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record ControlledProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long PeakWorkingSetBytes = 0,
    long ProcessorTimeMilliseconds = 0);

public sealed record ControlledProcessBinaryResult(
    int ExitCode,
    string StandardError,
    long PeakWorkingSetBytes = 0,
    long ProcessorTimeMilliseconds = 0);

public sealed class ControlledProcessRunner : IControlledProcessRunner
{
    private const int MaximumCapturedTextCharacters = 64 * 1024;
    private static readonly TimeSpan ExitConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResourceSampleInterval = TimeSpan.FromMilliseconds(100);

    public async Task<ControlledProcessResult> RunAsync(
        ControlledProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        using var process = Start(invocation);
        using var metricsCancellation = new CancellationTokenSource();
        var metrics = new ProcessResourceSampler(process);
        var metricsTask = metrics.SampleUntilStoppedAsync(metricsCancellation.Token);
        var stopRequest = new ProcessStopRequest(process);
        using var registration = cancellationToken.Register(
            static state => ((ProcessStopRequest)state!).RequestStop(),
            stopRequest);
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        Exception? operationFailure = null;
        try
        {
            // Continue draining both redirected streams after the capture limit so
            // a misbehaving custom tool cannot block itself on a full pipe.
            standardOutput = DrainLimitedTextAsync(process.StandardOutput);
            standardError = DrainLimitedTextAsync(process.StandardError);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError).WaitAsync(cancellationToken);
            var processMetrics = metrics.Snapshot();
            return new(
                process.ExitCode,
                await standardOutput,
                await standardError,
                processMetrics.PeakWorkingSetBytes,
                processMetrics.ProcessorTimeMilliseconds);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailure = await EnsureStoppedAndDrainedAsync(
                process,
                stopRequest,
                standardOutput,
                standardError);
            await StopSamplingAsync(metricsCancellation, metricsTask);
            ThrowCleanupFailureIfAny(cleanupFailure, operationFailure);
        }
    }

    public async Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
        ControlledProcessInvocation invocation,
        Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(consumeStandardOutputAsync);
        using var process = Start(invocation);
        using var metricsCancellation = new CancellationTokenSource();
        var metrics = new ProcessResourceSampler(process);
        var metricsTask = metrics.SampleUntilStoppedAsync(metricsCancellation.Token);
        var stopRequest = new ProcessStopRequest(process);
        using var registration = cancellationToken.Register(
            static state => ((ProcessStopRequest)state!).RequestStop(),
            stopRequest);
        Task? consume = null;
        Task<string>? standardError = null;
        Exception? operationFailure = null;
        try
        {
            consume = consumeStandardOutputAsync(
                process.StandardOutput.BaseStream,
                cancellationToken);
            standardError = DrainLimitedTextAsync(process.StandardError);
            var exited = process.WaitForExitAsync(cancellationToken);
            var pending = new HashSet<Task> { consume, standardError, exited };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                if (ReferenceEquals(completed, consume))
                {
                    // A faulting consumer must be observed immediately. Waiting
                    // for the child here can deadlock when it is blocked trying
                    // to write more stdout into the abandoned pipe.
                    await consume;
                    pending.Remove(consume);
                    if (!exited.IsCompleted)
                    {
                        // A successful consumer should normally finish with the
                        // process. Give a closed stdout pipe a short grace period;
                        // otherwise terminate rather than risk a pipe deadlock.
                        await exited.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
                else if (ReferenceEquals(completed, standardError))
                {
                    await standardError;
                    pending.Remove(standardError);
                }
                else
                {
                    await exited;
                    pending.Remove(exited);
                }
            }

            var processMetrics = metrics.Snapshot();
            return new(
                process.ExitCode,
                await standardError,
                processMetrics.PeakWorkingSetBytes,
                processMetrics.ProcessorTimeMilliseconds);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailure = await EnsureStoppedAndDrainedAsync(
                process,
                stopRequest,
                consume,
                standardError);
            await StopSamplingAsync(metricsCancellation, metricsTask);
            ThrowCleanupFailureIfAny(cleanupFailure, operationFailure);
        }
    }

    private static Process Start(ControlledProcessInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.ExecutablePath);
        ArgumentNullException.ThrowIfNull(invocation.Arguments);
        var executable = Path.GetFullPath(invocation.ExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The requested child executable does not exist.", executable);
        }

        if (invocation.WorkingDirectory is { } suppliedDirectory
            && !Directory.Exists(suppliedDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The child process working directory does not exist: {suppliedDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = invocation.WorkingDirectory is null
                ? Path.GetDirectoryName(executable)!
                : Path.GetFullPath(invocation.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in invocation.Arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo)
                ?? throw new Win32Exception("Windows did not start the requested child process.");
        }
        catch (Win32Exception)
        {
            throw;
        }
    }

    private static async Task StopSamplingAsync(
        CancellationTokenSource cancellation,
        Task samplingTask)
    {
        try
        {
            cancellation.Cancel();
            await samplingTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task<string> DrainLimitedTextAsync(StreamReader reader)
    {
        var buffer = new char[4_096];
        var captured = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            if (read == 0)
            {
                break;
            }

            var remaining = MaximumCapturedTextCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(remaining, read));
            }

            truncated |= read > remaining;
        }

        if (truncated)
        {
            captured.AppendLine();
            captured.Append("[WinPool process output truncated]");
        }

        return captured.ToString();
    }

    private static async Task<Exception?> EnsureStoppedAndDrainedAsync(
        Process process,
        ProcessStopRequest stopRequest,
        params Task?[] drainTasks)
    {
        try
        {
            if (!stopRequest.HasExited())
            {
                stopRequest.RequestStop();
            }

            if (!stopRequest.HasExited())
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(ExitConfirmationTimeout);
            }

            var activeDrains = drainTasks
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (activeDrains.Length > 0)
            {
                try
                {
                    await Task.WhenAll(activeDrains).WaitAsync(ExitConfirmationTimeout);
                }
                catch
                {
                    // The normal operation path observes stream-consumer and
                    // reader failures. Cleanup still waited for every task, but
                    // must not duplicate that original failure as a second
                    // cleanup error.
                }
            }

            return stopRequest.Failure;
        }
        catch (Exception exception)
        {
            return stopRequest.Failure is null
                ? exception
                : new AggregateException(stopRequest.Failure, exception);
        }
    }

    private static void ThrowCleanupFailureIfAny(
        Exception? cleanupFailure,
        Exception? operationFailure)
    {
        if (cleanupFailure is null)
        {
            return;
        }

        if (operationFailure is null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        throw new AggregateException(operationFailure, cleanupFailure);
    }

    private sealed class ProcessStopRequest(Process process)
    {
        private readonly object gate = new();
        private Exception? failure;

        public Exception? Failure
        {
            get
            {
                lock (gate)
                {
                    return failure;
                }
            }
        }

        public bool HasExited()
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Process can report this only after it has already transitioned
                // out of a usable running state; no child remains to terminate.
                return true;
            }
        }

        public void RequestStop()
        {
            lock (gate)
            {
                if (HasExited())
                {
                    return;
                }

                try
                {
                    // This process was created by this runner; a tree kill cannot
                    // touch an independently started user process.
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException exception) when (HasExited())
                {
                    _ = exception;
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException)
                {
                    if (!HasExited())
                    {
                        failure ??= exception;
                    }
                }
            }
        }
    }

    private readonly record struct ProcessMetrics(
        long PeakWorkingSetBytes,
        long ProcessorTimeMilliseconds);

    private sealed class ProcessResourceSampler(Process process)
    {
        private long peakWorkingSetBytes;
        private long processorTimeMilliseconds;

        public async Task SampleUntilStoppedAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Observe();
                try
                {
                    await Task.Delay(ResourceSampleInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        public ProcessMetrics Snapshot()
        {
            Observe();
            return new(
                Math.Max(0, Interlocked.Read(ref peakWorkingSetBytes)),
                Math.Max(0, Interlocked.Read(ref processorTimeMilliseconds)));
        }

        private void Observe()
        {
            try
            {
                process.Refresh();
                SetMaximum(ref peakWorkingSetBytes, Math.Max(0, process.WorkingSet64));
                SetMaximum(
                    ref processorTimeMilliseconds,
                    Math.Max(0, (long)process.TotalProcessorTime.TotalMilliseconds));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                // The child can disappear between the exit notification and a
                // polling sample. Previously observed values remain valid
                // evidence; telemetry must not alter process success/failure.
            }
        }

        private static void SetMaximum(ref long location, long candidate)
        {
            while (true)
            {
                var current = Interlocked.Read(ref location);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref location, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }
}

public sealed class SevenZipOperationException : IOException
{
    public SevenZipOperationException(string operation, int exitCode, string standardError)
        : base($"7-Zip {operation} failed with exit code {exitCode}: {standardError}")
    {
        Operation = operation;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public string Operation { get; }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>
/// Encapsulates exactly the 7-Zip commands used by monitoring archives. A
/// configured executable path is validated only for file existence; no
/// version, ability, installation, update, or PATH search is attempted.
/// </summary>
public sealed class SevenZipArchiveAdapter
{
    public const string BundledRelativeExecutablePath = "Tools\\7zip\\7za.exe";

    private readonly IControlledProcessRunner processRunner;

    public SevenZipArchiveAdapter(IControlledProcessRunner? processRunner = null)
    {
        this.processRunner = processRunner ?? new ControlledProcessRunner();
    }

    public static string ResolveExecutablePath(
        string? customPath,
        string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var candidate = string.IsNullOrWhiteSpace(customPath)
            ? Path.Combine(Path.GetFullPath(applicationDirectory), BundledRelativeExecutablePath)
            : customPath;
        if (!string.IsNullOrWhiteSpace(customPath) && !Path.IsPathFullyQualified(customPath))
        {
            throw new ArgumentException("The custom 7-Zip path must be absolute.", nameof(customPath));
        }

        var executable = Path.GetFullPath(candidate);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The configured 7-Zip executable does not exist.", executable);
        }

        return executable;
    }

    public async Task CreateArchiveAsync(
        string executablePath,
        string temporaryArchivePath,
        string workingDirectory,
        IReadOnlyList<string> entryFileNames,
        CancellationToken cancellationToken)
    {
        EnsureArchiveInputs(temporaryArchivePath, workingDirectory, entryFileNames);
        var arguments = new List<string>
        {
            "a",
            "-t7z",
            "-mx=9",
            "-m0=lzma2",
            "-md=64m",
            "-ms=on",
            "-mmt=2",
            "-y",
            "-bso0",
            "-bsp0",
            Path.GetFullPath(temporaryArchivePath)
        };
        arguments.AddRange(entryFileNames);
        var result = await processRunner.RunAsync(
            new ControlledProcessInvocation(executablePath, arguments, workingDirectory),
            cancellationToken);
        EnsureSuccess("create", result.ExitCode, result.StandardError);
    }

    public async Task TestArchiveAsync(
        string executablePath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ControlledProcessInvocation(
                executablePath,
                ["t", "-bso0", "-bsp0", Path.GetFullPath(archivePath)]),
            cancellationToken);
        EnsureSuccess("test", result.ExitCode, result.StandardError);
    }

    public async Task<string> HashExtractedEntryAsync(
        string executablePath,
        string archivePath,
        string entryFileName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryFileName);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var result = await processRunner.RunBinaryOutputAsync(
            new ControlledProcessInvocation(
                executablePath,
                [
                    "x",
                    "-so",
                    "-y",
                    "-bso0",
                    "-bsp0",
                    Path.GetFullPath(archivePath),
                    entryFileName
                ]),
            async (output, token) =>
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await output.ReadAsync(buffer, token);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                }
            },
            cancellationToken);
        EnsureSuccess("extract", result.ExitCode, result.StandardError);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureArchiveInputs(
        string temporaryArchivePath,
        string workingDirectory,
        IReadOnlyList<string> entryFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(entryFileNames);
        if (entryFileNames.Count == 0 || entryFileNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A monitoring archive requires named entries.", nameof(entryFileNames));
        }
    }

    private static void EnsureSuccess(string operation, int exitCode, string standardError)
    {
        if (exitCode != 0)
        {
            throw new SevenZipOperationException(operation, exitCode, standardError);
        }
    }
}
