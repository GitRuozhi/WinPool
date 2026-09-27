using System.Runtime.CompilerServices;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Tests;

public sealed class MonitoringExportSessionTests
{
    [Fact]
    public async Task StopAndLaterEmptySnapshotsRetainLastSessionForExport()
    {
        var connection = new Connection();
        using var service = new MonitoringService(connection);
        Assert.True(await service.StartAsync(5));
        var session = connection.Session!.SessionId;
        await service.StopAsync();
        Assert.False(service.IsRunning);
        await service.FlushAsync(); // MonitorPage refreshes before showing the picker.
        Assert.True(await service.ExportCsvAsync(@"C:\output\stopped.csv", true));
        Assert.Equal(session, connection.LastExport!.SessionId);
        connection.Rows = 0; // The session has since moved out of the active database.
        Assert.False(await service.ExportCsvAsync(@"C:\output\archived.csv", true));
        Assert.Equal(session, connection.LastExport.SessionId);
    }

    [Fact]
    public async Task NewSessionReplacesExportTargetAndErrorsAreNotReportedAsEmpty()
    {
        var connection = new Connection();
        using var service = new MonitoringService(connection);
        Assert.False(await service.ExportCsvAsync(@"C:\output\empty.csv", false));
        Assert.True(await service.StartAsync(5));
        var first = connection.Session!.SessionId;
        await service.StopAsync();
        Assert.True(await service.StartAsync(5));
        var second = connection.Session!.SessionId;
        Assert.NotEqual(first, second);
        await service.StopAsync();
        Assert.True(await service.ExportCsvAsync(@"C:\output\latest.csv", true));
        Assert.Equal(second, connection.LastExport!.SessionId);
        connection.ExportFails = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportCsvAsync(@"C:\output\failed.csv", true));
    }

    private sealed class Connection : IAgentConnection
    {
        public MonitoringSession? Session { get; private set; }
        public ExportAgentMonitorCsvRequest? LastExport { get; private set; }
        public long Rows { get; set; } = 2;
        public bool ExportFails { get; set; }
        private readonly AgentInstanceId instance = new(Guid.NewGuid());

        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ApplicationResult<AgentHandshake>.Succeeded(
                new(11, instance, Environment.ProcessId, (AgentCapability)0, DateTimeOffset.UtcNow), CorrelationId.New()));
        public async IAsyncEnumerable<AgentEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentResponse result;
            switch (request)
            {
                case GetAgentSnapshotRequest:
                    result = new AgentSnapshotResponse(new(instance, true, Session,
                        new(AgentLifecycleState.Running, null, [], [], false), []));
                    break;
                case StartAgentMonitoringRequest start:
                    Session = new(start.MonitorRequest.SessionId, start.MonitorRequest, MonitoringSessionState.Running, DateTimeOffset.UtcNow, null);
                    result = new MonitoringSessionResponse(Session);
                    break;
                case StopAgentMonitoringRequest:
                    result = new MonitoringSessionResponse(Session! with { State = MonitoringSessionState.Stopped, EndedAtUtc = DateTimeOffset.UtcNow });
                    Session = null;
                    break;
                case ExportAgentMonitorCsvRequest export:
                    LastExport = export;
                    if (ExportFails)
                    {
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(ApplicationStatus.Failed, request.CorrelationId));
                    }
                    result = new ExportArtifactResponse(export.DestinationPath, "test", Rows);
                    break;
                default: throw new NotSupportedException(request.GetType().Name);
            }
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(result, request.CorrelationId));
        }
    }
}
