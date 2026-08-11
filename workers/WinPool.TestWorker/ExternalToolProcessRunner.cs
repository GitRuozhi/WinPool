using System.Diagnostics;
using System.Security.Cryptography;
using WinPool.Application;
using WinPool.Testing.Tools;

namespace WinPool.TestWorker;

public sealed class ExternalToolProcessRunner
{
    private const uint ForcedTerminationExitCode = 0x57500002;
    private const int OutputChunkSize = 16 * 1024;

    private readonly IProcessTreeJobFactory _jobFactory;
    private readonly IGracefulToolTermination _gracefulTermination;
    private readonly TimeProvider _timeProvider;
    private readonly IToolOutputCodePageResolver _outputCodePageResolver;

    public ExternalToolProcessRunner()
        : this(
            new WindowsJobObjectFactory(),
            new WindowCloseGracefulToolTermination(),
            TimeProvider.System,
            new SystemToolOutputCodePageResolver())
    {
    }

    internal ExternalToolProcessRunner(
        IProcessTreeJobFactory jobFactory,
        IGracefulToolTermination gracefulTermination,
        TimeProvider timeProvider,
        IToolOutputCodePageResolver? outputCodePageResolver = null)
    {
        _jobFactory = jobFactory
            ?? throw new ArgumentNullException(nameof(jobFactory));
        _gracefulTermination = gracefulTermination
            ?? throw new ArgumentNullException(nameof(gracefulTermination));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _outputCodePageResolver = outputCodePageResolver
            ?? new SystemToolOutputCodePageResolver();
    }

    public async Task<ToolProcessResult> ExecuteAsync(
        ToolProcessRequest request,
        BoundedWorkerEventBuffer eventBuffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventBuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = ValidateRequest(request);
        var sha256 = await ComputeSha256Async(normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExpectedIdentity(request.ExpectedTool, sha256);
        var outputEncoding = _outputCodePageResolver.Resolve(
            request.Invocation.OutputEncoding);
        var versionInformation = FileVersionInfo.GetVersionInfo(normalizedPath);
        var fileVersion = versionInformation.FileVersion
            ?? versionInformation.ProductVersion
            ?? request.ExpectedTool.Version
            ?? "unknown";

        using var job = _jobFactory.Create();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request.Invocation, normalizedPath),
            EnableRaisingEvents = true
        };

        var started = false;
        try
        {
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("The external tool process did not start.");
            }

            try
            {
                job.Assign(process);
            }
            catch
            {
                await KillUnassignedProcessAsync(process).ConfigureAwait(false);
                throw;
            }

            var startedAt = _timeProvider.GetUtcNow();
            var identity = new ToolProcessIdentity(
                process.Id,
                normalizedPath,
                fileVersion,
                sha256,
                startedAt);

            EnqueueState(
                eventBuffer,
                request,
                identity.ProcessId,
                "tool.process.started",
                identity);

            var stdoutTask = CopyOutputAsync(
                process.StandardOutput.BaseStream,
                ToolOutputStream.StandardOutput,
                request,
                identity.ProcessId,
                eventBuffer,
                outputEncoding.CodePage);
            var stderrTask = CopyOutputAsync(
                process.StandardError.BaseStream,
                ToolOutputStream.StandardError,
                request,
                identity.ProcessId,
                eventBuffer,
                outputEncoding.CodePage);

            var outcome = await WaitForCompletionAsync(
                    request,
                    process,
                    job,
                    eventBuffer,
                    cancellationToken)
                .ConfigureAwait(false);

            // Closing a KillOnJobClose Job also reclaims descendants left behind
            // after the root process exits and closes inherited pipe handles.
            job.Dispose();

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var exitedAt = _timeProvider.GetUtcNow();
            var exitCode = process.ExitCode;
            eventBuffer.TryEnqueue(new WorkerEvent(
                request.RunId,
                request.StepId,
                WorkerEventKind.ProcessState,
                WorkerEventImportance.StateChange,
                exitedAt,
                "tool.process.exited",
                ReadOnlyMemory<byte>.Empty,
                identity.ProcessId,
                exitCode));

            return new ToolProcessResult(
                new ToolProcessAudit(
                    request.RunId,
                    request.StepId,
                    request.Invocation.ToolId,
                    identity,
                    exitedAt,
                    exitCode,
                    outcome.Reason,
                    outcome.GracefulRequested,
                    outcome.GracefulAccepted,
                    outcome.JobTerminationRequired),
                eventBuffer.GetStatistics());
        }
        catch (Exception exception) when (started)
        {
            eventBuffer.TryEnqueue(new WorkerEvent(
                request.RunId,
                request.StepId,
                WorkerEventKind.Error,
                WorkerEventImportance.Error,
                _timeProvider.GetUtcNow(),
                $"tool.process.runner_failure.{exception.GetType().Name}",
                ReadOnlyMemory<byte>.Empty,
                TryGetProcessId(process)));
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        ToolInvocation invocation,
        string normalizedExecutablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = normalizedExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in invocation.EnvironmentVariables)
        {
            startInfo.Environment.Add(pair.Key, pair.Value);
        }

        return startInfo;
    }

    private async Task<WaitOutcome> WaitForCompletionAsync(
        ToolProcessRequest request,
        Process process,
        IProcessTreeJob job,
        BoundedWorkerEventBuffer eventBuffer,
        CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(
            request.Invocation.Timeout,
            _timeProvider,
            CancellationToken.None);
        var cancellationSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal);

        await Task.WhenAny(exitTask, timeoutTask, cancellationSignal.Task)
            .ConfigureAwait(false);
        if (exitTask.IsCompleted)
        {
            await exitTask.ConfigureAwait(false);
            return new WaitOutcome(
                ToolProcessTerminationReason.Completed,
                false,
                false,
                false);
        }

        var reason = cancellationSignal.Task.IsCompleted
            ? ToolProcessTerminationReason.Cancelled
            : ToolProcessTerminationReason.TimedOut;
        EnqueueState(
            eventBuffer,
            request,
            process.Id,
            reason is ToolProcessTerminationReason.Cancelled
                ? "tool.process.cancellation_requested"
                : "tool.process.timeout");

        using var gracefulTimeout = new CancellationTokenSource(
            request.GracefulShutdownTimeout,
            _timeProvider);
        var graceDelay = Task.Delay(
            request.GracefulShutdownTimeout,
            _timeProvider,
            CancellationToken.None);
        Task<bool> gracefulRequest;
        try
        {
            gracefulRequest = _gracefulTermination.RequestAsync(
                    request.Invocation.ToolId,
                    process,
                    gracefulTimeout.Token)
                .AsTask();
        }
        catch (Exception)
        {
            gracefulRequest = Task.FromResult(false);
        }

        _ = gracefulRequest.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await Task.WhenAny(exitTask, gracefulRequest, graceDelay).ConfigureAwait(false);
        var gracefulAccepted = gracefulRequest.IsCompletedSuccessfully
            && gracefulRequest.Result;
        if (!exitTask.IsCompleted)
        {
            await Task.WhenAny(exitTask, graceDelay).ConfigureAwait(false);
        }

        var forced = !exitTask.IsCompleted;
        if (forced)
        {
            EnqueueState(
                eventBuffer,
                request,
                process.Id,
                "tool.process.job_termination");
            job.Terminate(ForcedTerminationExitCode);
        }

        await exitTask.ConfigureAwait(false);
        return new WaitOutcome(reason, true, gracefulAccepted, forced);
    }

    private async Task CopyOutputAsync(
        Stream source,
        ToolOutputStream stream,
        ToolProcessRequest request,
        int processId,
        BoundedWorkerEventBuffer eventBuffer,
        int outputCodePage)
    {
        var buffer = new byte[OutputChunkSize];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, CancellationToken.None)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            var copy = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, copy, 0, bytesRead);
            var isError = stream is ToolOutputStream.StandardError;
            eventBuffer.TryEnqueue(new WorkerEvent(
                request.RunId,
                request.StepId,
                isError
                    ? WorkerEventKind.StandardError
                    : WorkerEventKind.StandardOutput,
                isError
                    ? WorkerEventImportance.Error
                    : WorkerEventImportance.Output,
                _timeProvider.GetUtcNow(),
                isError
                    ? "tool.process.stderr"
                    : "tool.process.stdout",
                copy,
                processId,
                OutputCodePage: outputCodePage));
        }
    }

    private static string ValidateRequest(ToolProcessRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepId);
        ArgumentNullException.ThrowIfNull(request.Invocation);
        ArgumentNullException.ThrowIfNull(request.ExpectedTool);
        if (request.RunId.Value == Guid.Empty)
        {
            throw new ToolProcessValidationException(
                "test_worker.run_id.invalid",
                "The test run identifier must not be empty.");
        }

        if (request.GracefulShutdownTimeout < TimeSpan.Zero)
        {
            throw new ToolProcessValidationException(
                "test_worker.grace_timeout.invalid",
                "The graceful shutdown timeout must not be negative.");
        }

        if (request.Invocation.Timeout <= TimeSpan.Zero)
        {
            throw new ToolProcessValidationException(
                "test_worker.timeout.invalid",
                "The tool timeout must be positive.");
        }

        if (request.ExpectedTool.Availability is not ToolAvailability.Available)
        {
            throw new ToolProcessValidationException(
                "test_worker.tool.unavailable",
                "The expected tool is not available.");
        }

        if (request.Invocation.ToolId != request.ExpectedTool.ToolId)
        {
            throw new ToolProcessValidationException(
                "test_worker.tool.identity_mismatch",
                "The invocation and detected tool identities differ.");
        }

        var path = request.Invocation.ExecutablePath;
        if (!Path.IsPathFullyQualified(path) || IsDeviceNamespace(path))
        {
            throw new ToolProcessValidationException(
                "test_worker.executable_path.invalid",
                "The executable path must be a fully qualified ordinary file path.");
        }

        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
        {
            throw new ToolProcessValidationException(
                "test_worker.executable_path.missing",
                "The configured executable does not exist.");
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedTool.ExecutablePath)
            || !Path.IsPathFullyQualified(request.ExpectedTool.ExecutablePath)
            || IsDeviceNamespace(request.ExpectedTool.ExecutablePath)
            || !string.Equals(
                normalizedPath,
                Path.GetFullPath(request.ExpectedTool.ExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolProcessValidationException(
                "test_worker.executable_path.identity_mismatch",
                "The invocation path differs from the detected tool path.");
        }

        if (!Path.IsPathFullyQualified(request.Invocation.WorkingDirectory)
            || IsDeviceNamespace(request.Invocation.WorkingDirectory)
            || !Directory.Exists(request.Invocation.WorkingDirectory))
        {
            throw new ToolProcessValidationException(
                "test_worker.working_directory.invalid",
                "The working directory must be a fully qualified existing directory.");
        }

        if (request.Invocation.Arguments.Any(argument => argument is null))
        {
            throw new ToolProcessValidationException(
                "test_worker.argument.invalid",
                "Argument tokens must not be null.");
        }

        if (request.Invocation.EnvironmentVariables.Any(
                pair => string.IsNullOrEmpty(pair.Key)
                    || pair.Key.Contains('=', StringComparison.Ordinal)
                    || pair.Value is null))
        {
            throw new ToolProcessValidationException(
                "test_worker.environment.invalid",
                "The fixed process environment contains an invalid entry.");
        }

        return normalizedPath;
    }

    private static void ValidateExpectedIdentity(ToolState expectedTool, string sha256)
    {
        if (!string.IsNullOrWhiteSpace(expectedTool.Sha256)
            && !string.Equals(
                expectedTool.Sha256,
                sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolProcessValidationException(
                "test_worker.tool.hash_mismatch",
                "The executable SHA-256 differs from the detected tool identity.");
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task KillUnassignedProcessAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process exited before the assignment failure could be handled.
        }
    }

    private void EnqueueState(
        BoundedWorkerEventBuffer eventBuffer,
        ToolProcessRequest request,
        int processId,
        string code,
        ToolProcessIdentity? identity = null)
    {
        eventBuffer.TryEnqueue(new WorkerEvent(
            request.RunId,
            request.StepId,
            WorkerEventKind.ProcessState,
            WorkerEventImportance.StateChange,
            _timeProvider.GetUtcNow(),
            code,
            ReadOnlyMemory<byte>.Empty,
            processId,
            null,
            identity));
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsDeviceNamespace(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.Ordinal);

    private sealed record WaitOutcome(
        ToolProcessTerminationReason Reason,
        bool GracefulRequested,
        bool GracefulAccepted,
        bool JobTerminationRequired);
}
