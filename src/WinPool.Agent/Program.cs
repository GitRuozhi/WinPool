using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Monitoring;
using WinPool.Inventory;

namespace WinPool.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            MessageBox.Show(
                "WinPool could not identify the current Windows user.",
                "WinPool Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var userHash = IpcIdentity.HashUserSid(sid);
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            $"Local\\WinPool.Agent.{userHash[..24]}",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        try
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var agentSessionId = Guid.NewGuid();
            var instanceId = new AgentInstanceId(agentSessionId);
            var processRegistry = new AgentProcessRegistry();
            var lifecycle = new AgentLifecycleStateStore(
                processRegistry,
                AgentLifecycleState.Starting);
            lifecycle.MarkRecovering();
            var productRoot = ResolveProductRoot();
            var dataRoot = StorageDataLocations.ResolveCurrentRoot(productRoot);
            var preferencesService = new LocalUserPreferencesService(dataRoot);
            var agentPreferencesStore = new LocalAgentPreferencesService(dataRoot);
            using var context = new TrayApplicationContext(
                preferencesService,
                agentPreferencesStore);
            WorkerProcessRepository workerProcesses = null!;
            var agentEvents = new AgentEventHub();
            var processIncarnationVerifier = new WindowsProcessIncarnationVerifier();
            var mainApplicationExecutablePath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "WinPool.App.exe"));
            var coordinator = new AgentSessionCoordinator(
                processRegistry,
                lifecycle,
                () => new AgentSnapshot(
                    instanceId,
                    context.IsTrayVisible,
                    ActiveMonitoringSession: null,
                    ShutdownStatus: lifecycle.Snapshot(),
                    Processes: [],
                    LatestMonitorSamples: [],
                    RecentStorageHealthEvents: [],
                    MonitorDiagnostics: new MonitorRuntimeDiagnostics(0, 0)));
            context.AttachCoordinator(coordinator);
            var nonce = Guid.NewGuid();
            var pipeName = IpcIdentity.CreateAgentControlPipeName(userHash, nonce);
            var endpoint = new AgentEndpointRecord(
                IpcProtocol.CurrentVersion,
                pipeName,
                nonce,
                agentSessionId,
                Environment.ProcessId,
                startedAtUtc);
            var endpointPath = PublishEndpoint(endpoint, dataRoot);
            using var endpointPublication = new PublishedEndpointLease(
                endpointPath,
                endpoint);
            using var pipeCancellation = new CancellationTokenSource();
            var server = new CurrentUserAgentControlServer(
                pipeName,
                nonce,
                userHash,
                agentSessionId,
                Environment.ProcessId,
                coordinator,
                persistProcess: (registration, cancellationToken) => workerProcesses is null
                    ? Task.CompletedTask
                    : workerProcesses.SaveAsync(
                        instanceId,
                        registration,
                        cancellationToken),
                eventHub: agentEvents,
                processIncarnationVerifier: processIncarnationVerifier,
                expectedClientExecutablePath: mainApplicationExecutablePath,
                reportConnectionFailure: (code, exception) =>
                {
                    Trace.TraceError("{0}: {1}", code, exception);
                    DiagnosticLog.AppendFailure(
                        dataRoot,
                        "agent-control.jsonl",
                        code,
                        exception);
                    context.NotifyControlFailure(code);
                });
            var serverTask = Task.Run(() => server.RunAsync(pipeCancellation.Token));
            _ = serverTask.ContinueWith(
                task =>
                {
                    lifecycle.MarkFailed("agent.control.listener_failed");
                    Trace.TraceError("agent.control.listener_failed: {0}", task.Exception);
                    context.NotifyControlFailure("agent.control.listener_failed");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            var store = new WinPoolSqliteStore(Path.Combine(dataRoot, "winpool.db"));
            AgentStartupTaskRunner.Complete(() => store.InitializeAsync());
            using var writeOwner = AgentWriteOwnerLease.Acquire(
                store,
                $"agent-{agentSessionId:N}");
            var agentSessions = new AgentSessionRepository(store, writeOwner);
            AgentStartupTaskRunner.Complete(
                () => agentSessions.RecoverOpenSessionsAsync(startedAtUtc));
            AgentStartupTaskRunner.Complete(
                () => agentSessions.StartAsync(
                    instanceId,
                    Environment.ProcessId,
                    startedAtUtc));
            var monitoringArchives = new MonitoringArchiveCoordinator(
                dataRoot,
                AppContext.BaseDirectory,
                () => agentPreferencesStore.LoadAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
                    .SevenZipExecutablePath);
            var monitoringPersistence = new RotatingMonitorSessionPersistenceFactory(
                dataRoot,
                $"agent-monitoring-{agentSessionId:N}",
                monitoringArchives);
            AgentStartupTaskRunner.Complete(
                () => monitoringPersistence.InitializeAsync());
            var monitoring = new MonitoringSessionCoordinator(
                new PdhDiskMonitorSource(),
                monitoringPersistence);
            workerProcesses = new WorkerProcessRepository(store, writeOwner);
            var storageHealthEvents = new StorageHealthEventRepository(store, writeOwner);
            var initialStorageHealthEvents = AgentStartupTaskRunner.Complete(
                () => storageHealthEvents.ListRecentAsync(200, CancellationToken.None));
            var runtime = new DesktopAgentRuntime(
                context,
                instanceId,
                monitoring,
                new MonitorCsvExporter(monitoringPersistence),
                processRegistry,
                new WorkspaceSessionStateRepository(store, writeOwner),
                new SimulationDocumentRepository(store, writeOwner),
                new NativeWindowsInventoryProvider(),
                new EmbeddedPowerShellInventoryProvider(),
                new WindowsHardwareInventoryProvider(),
                new InventoryComparer(),
                new InventorySnapshotRepository(store, writeOwner),
                new InventoryComparisonRepository(store, writeOwner),
                new LocalInventoryDocumentRepository(store, writeOwner),
                new LocalSystemIdentityResolver(store, writeOwner),
                processIncarnationVerifier,
                mainApplicationExecutablePath,
                new WindowsStorageHealthEventSource(),
                storageHealthEvents,
                initialStorageHealthEvents,
                agentEvents,
                lifecycle,
                agentPreferencesStore);
            var shutdown = new AgentShutdownWorkflow(runtime, processRegistry);
            coordinator.AttachRuntime(runtime, shutdown);
            context.AttachCoordinator(coordinator);
            using var recoveryCancellation = new CancellationTokenSource();
            var recoveryTask = Task.Run(async () =>
            {
                try
                {
                    await runtime.RestoreContinuousMonitoringAsync(
                        recoveryCancellation.Token).ConfigureAwait(false);
                    lifecycle.MarkReady();
                    context.AttachCoordinator(coordinator);
                    runtime.StartStartupInventory();
                }
                catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
                {
                    // Shutdown won the race with non-critical monitoring recovery.
                }
                catch
                {
                    lifecycle.MarkFailed("agent.monitoring.restore_failed");
                    context.AttachCoordinator(coordinator);
                }
            });

            try
            {
                System.Windows.Forms.Application.Run(context);
            }
            finally
            {
                recoveryCancellation.Cancel();
                recoveryTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
                    .GetAwaiter()
                    .GetResult();
                var monitoringStopped = false;
                try
                {
                    // No shutdown timeout is used here. A normal Agent exit
                    // must either wait for the monitoring writer to complete
                    // its final batch or retain a visible failure; it may not
                    // release the factory lease underneath a live writer.
                    runtime.StopMonitoringAsync(CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    monitoringStopped = true;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidOperationException
                        or TimeoutException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    lifecycle.MarkFailed("agent.monitoring.stop_failed");
                    DiagnosticLog.AppendFailure(
                        dataRoot,
                        "agent-monitoring.jsonl",
                        "agent.monitoring.stop_failed",
                        exception);
                }
                if (monitoringStopped)
                {
                    try
                    {
                        monitoringPersistence.DisposeAsync().AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or InvalidOperationException
                            or TimeoutException
                            or Microsoft.Data.Sqlite.SqliteException)
                    {
                        lifecycle.MarkFailed("agent.monitoring.dispose_failed");
                        DiagnosticLog.AppendFailure(
                            dataRoot,
                            "agent-monitoring.jsonl",
                            "agent.monitoring.dispose_failed",
                            exception);
                    }
                }
                else
                {
                    DiagnosticLog.AppendFailure(
                        dataRoot,
                        "agent-monitoring.jsonl",
                        "agent.monitoring.dispose_skipped",
                        new InvalidOperationException(
                            "Monitoring persistence disposal was skipped because its writer did not stop."));
                }
                runtime.StopInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                pipeCancellation.Cancel();
                serverTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
                    .GetAwaiter()
                    .GetResult();
                try
                {
                    agentSessions.EndAsync(
                            instanceId,
                            DateTimeOffset.UtcNow,
                            shutdownClean: coordinator.State == AgentLifecycleState.Stopped)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception) when (
                    exception is IOException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    // The next Agent start retains this row as unclean evidence.
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The WinPool tray agent could not start and will exit.\n\n{exception.Message}",
                "WinPool Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            GC.KeepAlive(singleInstance);
        }
    }

    private static string ResolveProductRoot() =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));

    /// <summary>
    /// Runs non-UI startup work before the tray message loop begins. The tray
    /// context can already have a Windows Forms synchronization context at
    /// this point, so blocking that thread directly on an async database or
    /// archive operation can deadlock its continuation permanently.
    /// </summary>
    internal static class AgentStartupTaskRunner
    {
        internal static void Complete(Func<Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            Task.Run(operation).GetAwaiter().GetResult();
        }

        internal static T Complete<T>(Func<Task<T>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return Task.Run(operation).GetAwaiter().GetResult();
        }
    }

    private static string PublishEndpoint(AgentEndpointRecord endpoint, string dataRoot)
    {
        var path = DataRootLayout.AgentEndpointPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(endpoint));
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    internal static bool IsCurrentEndpoint(
        AgentEndpointRecord candidate,
        AgentEndpointRecord expected) =>
        candidate.AgentSessionId == expected.AgentSessionId
        && candidate.Nonce == expected.Nonce
        && candidate.ProcessId == expected.ProcessId;

    private static void TryRemoveEndpoint(
        string path,
        AgentEndpointRecord expectedEndpoint)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var published = JsonSerializer.Deserialize<AgentEndpointRecord>(
                File.ReadAllText(path));
            if (published is not null && IsCurrentEndpoint(published, expectedEndpoint))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    internal sealed class PublishedEndpointLease(
        string path,
        AgentEndpointRecord endpoint) : IDisposable
    {
        public void Dispose() => TryRemoveEndpoint(path, endpoint);
    }
}
