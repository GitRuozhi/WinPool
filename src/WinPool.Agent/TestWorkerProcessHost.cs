using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent;

public sealed record TestWorkerRunResult(
    int WorkerProcessId,
    IReadOnlyList<ToolProcessResult> ToolResults,
    IReadOnlyList<WorkerEvent> Events);

public sealed class TestWorkerProcessHost
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string workerExecutablePath;
    private readonly string userSidHash;
    private readonly int agentProcessId;
    private readonly TimeProvider timeProvider;
    private readonly long maximumCapturedOutputBytes;
    private readonly int maximumCapturedEvents;

    public TestWorkerProcessHost(
        string workerExecutablePath,
        string userSidHash,
        int agentProcessId,
        TimeProvider? timeProvider = null,
        long maximumCapturedOutputBytes = 64L * 1024 * 1024,
        int maximumCapturedEvents = 131_072)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        if (!Path.IsPathFullyQualified(workerExecutablePath)
            || !File.Exists(workerExecutablePath))
        {
            throw new FileNotFoundException(
                "The fixed TestWorker executable was not found.",
                workerExecutablePath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userSidHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(agentProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumCapturedOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapturedEvents);
        this.workerExecutablePath = Path.GetFullPath(workerExecutablePath);
        this.userSidHash = userSidHash;
        this.agentProcessId = agentProcessId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumCapturedOutputBytes = maximumCapturedOutputBytes;
        this.maximumCapturedEvents = maximumCapturedEvents;
    }

    public async Task<TestWorkerRunResult> RunAsync(
        ToolProcessRequest request,
        Func<TestWorkerEventBatch, CancellationToken, Task>? receiveBatch,
        Func<int, CancellationToken, Task>? workerStarted,
        CancellationToken cancellationToken) =>
        await RunAsync(
            [request],
            receiveBatch,
            workerStarted,
            workerCompleting: null,
            cancellationToken);

    public async Task<TestWorkerRunResult> RunAsync(
        IReadOnlyList<ToolProcessRequest> requests,
        Func<TestWorkerEventBatch, CancellationToken, Task>? receiveBatch,
        Func<int, CancellationToken, Task>? workerStarted,
        CancellationToken cancellationToken) =>
        await RunAsync(
            requests,
            receiveBatch,
            workerStarted,
            workerCompleting: null,
            cancellationToken);

    public async Task<TestWorkerRunResult> RunAsync(
        IReadOnlyList<ToolProcessRequest> requests,
        Func<TestWorkerEventBatch, CancellationToken, Task>? receiveBatch,
        Func<int, CancellationToken, Task>? workerStarted,
        Func<int, CancellationToken, Task>? workerCompleting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0
            || requests.Any(item => item.RunId != requests[0].RunId))
        {
            throw new ArgumentException(
                "A TestWorker run requires one or more requests for the same run.",
                nameof(requests));
        }

        var runId = requests[0].RunId;
        var nonce = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateTestWorkerPipeName(
            userSidHash,
            runId.Value,
            nonce);
        await using var server = CurrentUserPipeFactory.CreateServer(pipeName);
        using var worker = StartWorker(pipeName, nonce, runId);
        var completingInvoked = false;
        if (workerStarted is not null)
        {
            await workerStarted(worker.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        try
        {
            // Keep transport authentication separate from a user-requested test
            // cancellation. Once the worker is authenticated, an already-cancelled
            // token is delivered through the normal Cancel message so the worker can
            // terminate its job and return a durable cancellation audit.
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(connectTimeout.Token)
                .ConfigureAwait(false);
            await AuthenticateAsync(
                    server,
                    worker.Id,
                    nonce,
                    runId,
                    connectTimeout.Token)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await WriteAsync(
                        server,
                        TestWorkerMessageTypes.Abort,
                        runId.Value,
                        new CancelToolProcessCommand(runId),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            await WriteAsync(
                    server,
                    TestWorkerMessageTypes.Start,
                    runId.Value,
                    new StartTestWorkerCommand(requests),
                    connectTimeout.Token)
                .ConfigureAwait(false);

            var events = new List<WorkerEvent>();
            long capturedOutputBytes = 0;
            var cancellationSent = false;
            var toolProcessStarted = false;
            Task? cancellationDeadline = null;
            var cancellationSignal = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            while (true)
            {
                var readTask = IpcFrameCodec.ReadAsync(
                        server,
                        CancellationToken.None)
                    .AsTask();
                if (!cancellationSent)
                {
                    var first = await Task.WhenAny(readTask, cancellationSignal)
                        .ConfigureAwait(false);
                    if (first == cancellationSignal)
                    {
                        await WriteAsync(
                                server,
                                toolProcessStarted
                                    ? TestWorkerMessageTypes.Cancel
                                    : TestWorkerMessageTypes.Abort,
                                runId.Value,
                                new CancelToolProcessCommand(runId),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        cancellationSent = true;
                        cancellationDeadline = Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }

                if (cancellationSent && cancellationDeadline is not null)
                {
                    var next = await Task.WhenAny(readTask, cancellationDeadline)
                        .ConfigureAwait(false);
                    if (next == cancellationDeadline)
                    {
                        throw new OperationCanceledException(
                            "The TestWorker did not stop within the cancellation grace.",
                            cancellationToken);
                    }
                }

                var envelope = await readTask.ConfigureAwait(false);

                if (envelope.MessageType == TestWorkerMessageTypes.EventBatch)
                {
                    var batch =
                        envelope.Payload.Deserialize<TestWorkerEventBatch>(
                            JsonOptions)
                        ?? throw new InvalidDataException(
                            "The TestWorker event batch is empty.");
                    capturedOutputBytes = checked(
                        capturedOutputBytes
                        + batch.Events.Sum(item => (long)item.RawBytes.Length));
                    if (capturedOutputBytes > maximumCapturedOutputBytes
                        || events.Count + batch.Events.Count
                        > maximumCapturedEvents)
                    {
                        throw new InvalidDataException(
                            "The TestWorker output exceeded the bounded Agent capture limit.");
                    }

                    events.AddRange(batch.Events);
                    toolProcessStarted |= batch.Events.Any(item =>
                        item.Code == "tool.process.started");
                    if (receiveBatch is not null)
                    {
                        await receiveBatch(batch, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    if (!cancellationSent
                        && toolProcessStarted
                        && cancellationToken.IsCancellationRequested)
                    {
                        await WriteAsync(
                                server,
                                TestWorkerMessageTypes.Cancel,
                                runId.Value,
                                new CancelToolProcessCommand(runId),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        cancellationSent = true;
                        cancellationDeadline = Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }
                else if (envelope.MessageType == TestWorkerMessageTypes.Completed)
                {
                    var completed =
                        envelope.Payload.Deserialize<TestWorkerCompleted>(
                            JsonOptions)
                        ?? throw new InvalidDataException(
                            "The TestWorker completion payload is empty.");
                    if (workerCompleting is not null)
                    {
                        completingInvoked = true;
                        await workerCompleting(worker.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    await WriteAsync(
                            server,
                            TestWorkerMessageTypes.CompletionAcknowledged,
                            runId.Value,
                            new AcknowledgeTestWorkerCompletionCommand(runId),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var exit = await SupervisedProcessExitPolicy.EnsureExitedAsync(
                            worker,
                            SupervisedProcessExitPolicy.DefaultExitGrace,
                            SupervisedProcessExitPolicy.DefaultFinalWait)
                        .ConfigureAwait(false);
                    if (!exit.ExitedAfterKill)
                    {
                        throw new TimeoutException(
                            "The TestWorker did not exit after process-tree termination.");
                    }
                    return new(worker.Id, completed.Results, events);
                }
                else if (envelope.MessageType == TestWorkerMessageTypes.Failed)
                {
                    var failure = envelope.Payload.Deserialize<TestWorkerFailure>(
                        JsonOptions);
                    throw new InvalidOperationException(
                        $"{failure?.Code ?? "test_worker.failed"}: {failure?.Diagnostic}");
                }
            }
        }
        finally
        {
            if (!worker.HasExited)
            {
                Exception? completingFailure = null;
                if (!completingInvoked && workerCompleting is not null)
                {
                    completingInvoked = true;
                    try
                    {
                        await workerCompleting(worker.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        completingFailure = exception;
                    }
                }

                var exit = await SupervisedProcessExitPolicy.EnsureExitedAsync(
                        worker,
                        TimeSpan.Zero,
                        SupervisedProcessExitPolicy.DefaultFinalWait)
                    .ConfigureAwait(false);
                if (!exit.ExitedAfterKill)
                {
                    throw new TimeoutException(
                        "The TestWorker process tree did not exit after termination.");
                }
                if (completingFailure is not null)
                {
                    throw new InvalidOperationException(
                        "The TestWorker completion callback failed before process-tree termination.",
                        completingFailure);
                }
            }
        }
    }

    private async Task AuthenticateAsync(
        Stream server,
        int workerProcessId,
        Guid nonce,
        TestRunId runId,
        CancellationToken cancellationToken)
    {
        var envelope = await IpcFrameCodec.ReadAsync(server, cancellationToken)
            .ConfigureAwait(false);
        var handshake =
            envelope.Payload.Deserialize<TestWorkerHandshakeRequest>(JsonOptions)
            ?? throw new InvalidDataException(
                "The TestWorker handshake payload is empty.");
        var age = timeProvider.GetUtcNow() - handshake.SentAtUtc;
        if (envelope.MessageType != TestWorkerMessageTypes.HandshakeRequest
            || envelope.ProtocolVersion != IpcProtocol.CurrentVersion
            || handshake.ProtocolVersion != IpcProtocol.CurrentVersion
            || handshake.Nonce != nonce
            || handshake.RunId != runId.Value
            || handshake.AgentProcessId != agentProcessId
            || handshake.WorkerProcessId != workerProcessId
            || age.Duration() > IpcProtocol.MaximumHandshakeAge
            || CurrentUserPipeFactory.GetConnectedClientProcessId(
                (System.IO.Pipes.NamedPipeServerStream)server) != workerProcessId)
        {
            throw new InvalidDataException(
                "The TestWorker handshake identity is invalid.");
        }

        await WriteAsync(
                server,
                TestWorkerMessageTypes.HandshakeReply,
                runId.Value,
                new TestWorkerHandshakeReply(
                    IpcProtocol.CurrentVersion,
                    runId.Value,
                    agentProcessId,
                    workerProcessId,
                    timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Process StartWorker(
        string pipeName,
        Guid nonce,
        TestRunId runId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerExecutablePath)!
        };
        foreach (var argument in new[]
                 {
                     "--pipe",
                     pipeName,
                     "--nonce",
                     nonce.ToString("N"),
                     "--run",
                     runId.Value.ToString("N"),
                     "--agent-pid",
                     agentProcessId.ToString(
                         System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   "The fixed TestWorker process did not start.");
    }

    private static ValueTask WriteAsync<T>(
        Stream stream,
        string messageType,
        Guid correlationId,
        T payload,
        CancellationToken cancellationToken) =>
        IpcFrameCodec.WriteAsync(
            stream,
            new(
                IpcProtocol.CurrentVersion,
                Guid.NewGuid(),
                correlationId,
                messageType,
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(payload, JsonOptions)),
            cancellationToken);
}
