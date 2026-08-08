using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.TestWorker.Tests;

public sealed class TestWorkerPipeClientTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RealWorkerAuthenticatesAgentAndStreamsControlledToolResult()
    {
        var request = TestWorkerTestSupport.CreateRequest(
            ["echo-args", "alpha", "two words"],
            TimeSpan.FromSeconds(10));
        var secondRequest = request with
        {
            StepId = "controlled-step-2",
            Invocation = request.Invocation with
            {
                Arguments = ["echo-args", "second-step"]
            }
        };
        var nonce = Guid.NewGuid();
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID unavailable.");
        var pipeName = IpcIdentity.CreateTestWorkerPipeName(
            IpcIdentity.HashUserSid(sid),
            request.RunId.Value,
            nonce);
        await using var server = CurrentUserPipeFactory.CreateServer(pipeName);
        using var worker = StartWorker(pipeName, nonce, request.RunId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.WaitForConnectionAsync(timeout.Token);

        var handshakeEnvelope = await IpcFrameCodec.ReadAsync(
            server,
            timeout.Token);
        var handshake =
            handshakeEnvelope.Payload.Deserialize<TestWorkerHandshakeRequest>(
                JsonOptions);
        Assert.NotNull(handshake);
        Assert.Equal(TestWorkerMessageTypes.HandshakeRequest, handshakeEnvelope.MessageType);
        Assert.Equal(nonce, handshake.Nonce);
        Assert.Equal(request.RunId.Value, handshake.RunId);
        Assert.Equal(Environment.ProcessId, handshake.AgentProcessId);
        Assert.Equal(worker.Id, handshake.WorkerProcessId);
        Assert.Equal(
            worker.Id,
            CurrentUserPipeFactory.GetConnectedClientProcessId(server));

        await WriteAsync(
            server,
            TestWorkerMessageTypes.HandshakeReply,
            request.RunId.Value,
            new TestWorkerHandshakeReply(
                IpcProtocol.CurrentVersion,
                request.RunId.Value,
                Environment.ProcessId,
                worker.Id,
                DateTimeOffset.UtcNow),
            timeout.Token);
        await WriteAsync(
            server,
            TestWorkerMessageTypes.Start,
            request.RunId.Value,
            new StartTestWorkerCommand([request, secondRequest]),
            timeout.Token);

        var events = new List<WorkerEvent>();
        TestWorkerCompleted? completed = null;
        while (completed is null)
        {
            var envelope = await IpcFrameCodec.ReadAsync(server, timeout.Token);
            if (envelope.MessageType == TestWorkerMessageTypes.EventBatch)
            {
                var batch = envelope.Payload.Deserialize<TestWorkerEventBatch>(
                    JsonOptions);
                Assert.NotNull(batch);
                events.AddRange(batch.Events);
            }
            else if (envelope.MessageType == TestWorkerMessageTypes.Completed)
            {
                completed = envelope.Payload.Deserialize<TestWorkerCompleted>(
                    JsonOptions);
            }
            else if (envelope.MessageType == TestWorkerMessageTypes.Failed)
            {
                var failure = envelope.Payload.Deserialize<TestWorkerFailure>(
                    JsonOptions);
                Assert.Fail($"{failure?.Code}: {failure?.Diagnostic}");
            }
        }

        Assert.False(worker.HasExited);
        await WriteAsync(
            server,
            TestWorkerMessageTypes.CompletionAcknowledged,
            request.RunId.Value,
            new AcknowledgeTestWorkerCompletionCommand(request.RunId),
            timeout.Token);
        await worker.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, worker.ExitCode);
        Assert.Equal(2, completed.Results.Count);
        Assert.All(completed.Results, result => Assert.Equal(0, result.Audit.ExitCode));
        Assert.Contains(
            events,
            item => item.Kind == WorkerEventKind.StandardOutput
                    && System.Text.Encoding.UTF8.GetString(item.RawBytes.Span)
                        .Contains("two words", StringComparison.Ordinal));
        Assert.Contains(
            events,
            item => item.StepId == "controlled-step-2"
                    && item.Kind == WorkerEventKind.StandardOutput
                    && System.Text.Encoding.UTF8.GetString(item.RawBytes.Span)
                        .Contains("second-step", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchOptionsRejectFreeFormOrIncompleteArguments()
    {
        Assert.False(TestWorkerLaunchOptions.TryParse(
            ["powershell.exe", "-Command", "anything"],
            out _));
        Assert.False(TestWorkerLaunchOptions.TryParse(
            ["--pipe", "WinPool.Worker.invalid"],
            out _));
    }

    private static Process StartWorker(
        string pipeName,
        Guid nonce,
        TestRunId runId)
    {
        var workerPath = Path.Combine(
            Path.GetDirectoryName(typeof(ExternalToolProcessRunner).Assembly.Location)!,
            "WinPool.TestWorker.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true
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
                     Environment.ProcessId.ToString(
                         System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Worker did not start.");
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
