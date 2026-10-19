using System.Security.Principal;
using System.Text.Json;
using WinPool.Agent;
using WinPool.Agent.Client;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent.Client.Tests;

public sealed partial class NamedPipeAgentConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffPauseBlocksMissingAgentConnectAndSendUntilAbortResumes(bool send)
    {
        var launcher = new BlockingLauncher();
        await using var connection = new NamedPipeAgentConnection(
            Path.Combine(Path.GetTempPath(), "WinPool.Agent.Client.Tests", Guid.NewGuid().ToString("N"), "missing.json"),
            launcher, null, null, new TrueAgentProcessLiveness());
        using var pause = await connection.PauseReconnectAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = send
            ? ObserveSendAsync()
            : ObserveConnectAsync();

        Assert.False(pending.IsCompleted);
        Assert.False(launcher.Started.Task.IsCompleted);
        pause.Dispose();
        pause.Dispose();
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.Equal(ApplicationStatus.Cancelled, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(launcher.Cancelled);

        async Task<ApplicationStatus> ObserveConnectAsync() =>
            (await connection.ConnectAsync(timeout.Token)).Status;
        async Task<ApplicationStatus> ObserveSendAsync() =>
            (await connection.SendAsync(new GetAgentSnapshotRequest(CorrelationId.New()), timeout.Token)).Status;
    }

    [Fact]
    public async Task HandoffPauseDisposalCancelsBlockedConnectWithoutLaunchingAgent()
    {
        var launcher = new BlockingLauncher();
        var connection = new NamedPipeAgentConnection(
            Path.Combine(Path.GetTempPath(), "WinPool.Agent.Client.Tests", Guid.NewGuid().ToString("N"), "missing.json"),
            launcher, null, null, new TrueAgentProcessLiveness());
        using var pause = await connection.PauseReconnectAsync(CancellationToken.None);
        var pending = connection.ConnectAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.False(launcher.Started.Task.IsCompleted);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task HandoffPauseKeepsShutdownControlAndAcknowledgementsButSuppressesEventRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPool.Agent.Client.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var endpointPath = Path.Combine(directory, "agent-endpoint.json");
        var userHash = IpcIdentity.HashUserSid(WindowsIdentity.GetCurrent().User!.Value);
        var nonce = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
        var registry = new AgentProcessRegistry();
        var coordinator = new AgentSessionCoordinator(new SnapshotOperations(sessionId),
            new AgentShutdownWorkflow(new NoOpShutdownActions(), registry), registry);
        await File.WriteAllTextAsync(endpointPath, JsonSerializer.Serialize(new AgentEndpoint(
            IpcProtocol.CurrentVersion, pipeName, nonce, sessionId, Environment.ProcessId, DateTimeOffset.UtcNow)));
        using var serverCancellation = new CancellationTokenSource();
        var server = new CurrentUserAgentControlServer(pipeName, nonce, userHash, sessionId,
            Environment.ProcessId, coordinator);
        var serverTask = server.RunAsync(serverCancellation.Token);
        var launcher = new BlockingLauncher();
        await using var connection = new NamedPipeAgentConnection(endpointPath,
            launcher, null, null, new TrueAgentProcessLiveness());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True((await connection.ConnectAsync(timeout.Token)).IsSuccess);
            using var pause = await connection.PauseReconnectAsync(timeout.Token);
            Assert.True((await connection.SendAsync(new GetAgentSnapshotRequest(CorrelationId.New()), timeout.Token)).IsSuccess);
            var acknowledgement = await connection.SendAsync(new AcknowledgeAgentShutdownPreparationRequest(
                Guid.NewGuid(), ProcessInstanceId.New(), true, CorrelationId.New()), timeout.Token);
            Assert.Equal(ApplicationStatus.Rejected, acknowledgement.Status);
            var shutdown = await connection.SendAsync(new RequestAgentShutdownRequest(
                ShutdownReason.StorageLocationSwitch, CorrelationId.New()), timeout.Token);
            Assert.True(shutdown.IsSuccess);
            Assert.True(Assert.IsType<ShutdownResponse>(shutdown.Value).Result.Completed);

            serverCancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            File.Delete(endpointPath);
            // Let the real pipe EOF reach its supervisor. It must not convert
            // the intentional shutdown into a launcher/recovery attempt.
            await Task.Delay(150, timeout.Token);
            Assert.False(launcher.Started.Task.IsCompleted);
            var pending = connection.ConnectAsync(timeout.Token);
            Assert.False(pending.IsCompleted);
            pause.Dispose();
            await launcher.Started.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            Assert.Equal(ApplicationStatus.Cancelled, (await pending).Status);
        }
        finally
        {
            serverCancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            Directory.Delete(directory, recursive: true);
        }
    }
}
