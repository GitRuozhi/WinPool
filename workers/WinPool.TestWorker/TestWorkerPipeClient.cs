using System.IO.Pipes;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.TestWorker;

internal sealed record TestWorkerLaunchOptions(
    string PipeName,
    Guid Nonce,
    TestRunId RunId,
    int AgentProcessId)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out TestWorkerLaunchOptions? options)
    {
        options = null;
        if (args.Count != 8)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--pipe", out var pipeName)
            || string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Contains('\\', StringComparison.Ordinal)
            || !values.TryGetValue("--nonce", out var nonceText)
            || !Guid.TryParseExact(nonceText, "N", out var nonce)
            || nonce == Guid.Empty
            || !values.TryGetValue("--run", out var runText)
            || !Guid.TryParseExact(runText, "N", out var runId)
            || runId == Guid.Empty
            || !values.TryGetValue("--agent-pid", out var agentPidText)
            || !int.TryParse(agentPidText, out var agentPid)
            || agentPid <= 0)
        {
            return false;
        }

        options = new(pipeName, nonce, new TestRunId(runId), agentPid);
        return true;
    }
}

internal sealed class TestWorkerPipeClient(TestWorkerLaunchOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using var pipe = CurrentUserPipeFactory.CreateClient(options.PipeName);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        if (CurrentUserPipeFactory.GetConnectedServerProcessId(pipe)
            != options.AgentProcessId)
        {
            throw new InvalidDataException(
                "The worker pipe server is not the expected Agent process.");
        }

        await SendHandshakeAsync(pipe, cancellationToken).ConfigureAwait(false);
        await ReceiveHandshakeAsync(pipe, cancellationToken).ConfigureAwait(false);
        var startEnvelope = await IpcFrameCodec.ReadAsync(pipe, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                startEnvelope.MessageType,
                TestWorkerMessageTypes.Abort,
                StringComparison.Ordinal))
        {
            var abort = startEnvelope.Payload.Deserialize<CancelToolProcessCommand>(
                JsonOptions);
            if (abort?.RunId != options.RunId)
            {
                throw new InvalidDataException(
                    "The worker Abort command does not match this run.");
            }

            return 0;
        }

        if (!string.Equals(
                startEnvelope.MessageType,
                TestWorkerMessageTypes.Start,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The first worker command must be Start.");
        }

        var start = startEnvelope.Payload.Deserialize<StartTestWorkerCommand>(
            JsonOptions)
            ?? throw new InvalidDataException("The worker Start payload is empty.");
        if (start.Requests.Count == 0
            || start.Requests.Any(request => request.RunId != options.RunId))
        {
            throw new InvalidDataException(
                "The worker Start command must contain one run's typed requests.");
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var buffer = new BoundedWorkerEventBuffer(1024);
        var commandTask = ReadCommandsAsync(
            pipe,
            runCancellation,
            runCancellation.Token);
        var pumpTask = PumpEventsAsync(
            pipe,
            buffer,
            runCancellation.Token);
        try
        {
            var results = new List<ToolProcessResult>();
            var runner = new ExternalToolProcessRunner();
            foreach (var request in start.Requests)
            {
                var result = await runner.ExecuteAsync(
                        request,
                        buffer,
                        runCancellation.Token)
                    .ConfigureAwait(false);
                results.Add(result);
                if (!ToolProcessExitPolicy.IsAccepted(
                        result.Audit.ToolId,
                        result.Audit.ExitCode)
                    || result.Audit.TerminationReason
                    is not ToolProcessTerminationReason.Completed)
                {
                    break;
                }
            }

            runCancellation.Cancel();
            await pumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await commandTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SendBatchAsync(pipe, buffer, CancellationToken.None)
                .ConfigureAwait(false);
            await WriteAsync(
                    pipe,
                    TestWorkerMessageTypes.Completed,
                    startEnvelope.CorrelationId,
                    new TestWorkerCompleted(results),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await ReceiveCompletionAcknowledgementAsync(
                    pipe,
                    startEnvelope.CorrelationId,
                    start.Requests[0].RunId,
                    cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            runCancellation.Cancel();
            await pumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await WriteAsync(
                    pipe,
                    TestWorkerMessageTypes.Failed,
                    startEnvelope.CorrelationId,
                    new TestWorkerFailure(
                        options.RunId,
                        exception is ToolProcessValidationException validation
                            ? validation.Code
                            : $"test_worker.failure.{exception.GetType().Name}",
                        exception.Message),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            runCancellation.Cancel();
            await commandTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task ReceiveCompletionAcknowledgementAsync(
        NamedPipeClientStream pipe,
        Guid correlationId,
        TestRunId runId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var envelope = await IpcFrameCodec.ReadAsync(pipe, timeout.Token)
            .ConfigureAwait(false);
        var acknowledgement = envelope.Payload
            .Deserialize<AcknowledgeTestWorkerCompletionCommand>(JsonOptions);
        if (envelope.MessageType
                != TestWorkerMessageTypes.CompletionAcknowledged
            || envelope.CorrelationId != correlationId
            || acknowledgement?.RunId != runId)
        {
            throw new InvalidDataException(
                "The Agent did not acknowledge the exact completed TestWorker run.");
        }
    }

    private async Task ReadCommandsAsync(
        NamedPipeClientStream pipe,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await IpcFrameCodec.ReadAsync(pipe, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope.MessageType is not (
                        TestWorkerMessageTypes.Cancel or
                        TestWorkerMessageTypes.Abort))
                {
                    continue;
                }

                var command = envelope.Payload.Deserialize<CancelToolProcessCommand>(
                    JsonOptions);
                if (command?.RunId == options.RunId)
                {
                    runCancellation.Cancel();
                }
            }
        }
        catch (EndOfStreamException)
        {
            runCancellation.Cancel();
        }
    }

    private static async Task PumpEventsAsync(
        NamedPipeClientStream pipe,
        BoundedWorkerEventBuffer buffer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            // Once a batch has been drained from the disconnect buffer, finish
            // writing it even if the run is concurrently cancelled. Otherwise
            // stderr or the final state can be removed from the buffer and lost.
            await SendBatchAsync(pipe, buffer, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task SendBatchAsync(
        NamedPipeClientStream pipe,
        BoundedWorkerEventBuffer buffer,
        CancellationToken cancellationToken)
    {
        var events = buffer.Drain();
        if (events.Count > 0)
        {
            await WriteAsync(
                pipe,
                TestWorkerMessageTypes.EventBatch,
                events[0].RunId.Value,
                new TestWorkerEventBatch(events, buffer.GetStatistics()),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask SendHandshakeAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken) =>
        WriteAsync(
            pipe,
            TestWorkerMessageTypes.HandshakeRequest,
            options.RunId.Value,
            new TestWorkerHandshakeRequest(
                IpcProtocol.CurrentVersion,
                options.Nonce,
                options.RunId.Value,
                options.AgentProcessId,
                Environment.ProcessId,
                DateTimeOffset.UtcNow),
            cancellationToken);

    private async Task ReceiveHandshakeAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        var envelope = await IpcFrameCodec.ReadAsync(pipe, cancellationToken)
            .ConfigureAwait(false);
        var reply = envelope.Payload.Deserialize<TestWorkerHandshakeReply>(
            JsonOptions);
        if (!string.Equals(
                envelope.MessageType,
                TestWorkerMessageTypes.HandshakeReply,
                StringComparison.Ordinal)
            || reply is null
            || reply.ProtocolVersion != IpcProtocol.CurrentVersion
            || reply.RunId != options.RunId.Value
            || reply.AgentProcessId != options.AgentProcessId
            || reply.WorkerProcessId != Environment.ProcessId)
        {
            throw new InvalidDataException("The Agent rejected the worker handshake.");
        }
    }

    private static ValueTask WriteAsync<T>(
        Stream pipe,
        string messageType,
        Guid correlationId,
        T payload,
        CancellationToken cancellationToken) =>
        IpcFrameCodec.WriteAsync(
            pipe,
            new(
                IpcProtocol.CurrentVersion,
                Guid.NewGuid(),
                correlationId,
                messageType,
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(payload, JsonOptions)),
            cancellationToken);
}
