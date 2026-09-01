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
            using var context = new TrayApplicationContext(preferencesService);
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
                expectedClientExecutablePath: mainApplicationExecutablePath);
            var serverTask = Task.Run(() => server.RunAsync(pipeCancellation.Token));

            var store = new WinPoolSqliteStore(Path.Combine(dataRoot, "winpool.db"));
            store.InitializeAsync().GetAwaiter().GetResult();
            using var writeOwner = AgentWriteOwnerLease.Acquire(
                store,
                $"agent-{agentSessionId:N}");
            var agentSessions = new AgentSessionRepository(store, writeOwner);
            agentSessions.RecoverOpenSessionsAsync(startedAtUtc)
                .GetAwaiter()
                .GetResult();
            agentSessions.StartAsync(
                    instanceId,
                    Environment.ProcessId,
                    startedAtUtc)
                .GetAwaiter()
                .GetResult();
            var monitoring = new MonitoringSessionCoordinator(
                new PdhDiskMonitorSource(),
                new SqliteMonitorSessionPersistenceFactory(store, writeOwner));
            workerProcesses = new WorkerProcessRepository(store, writeOwner);
            var storageHealthEvents = new StorageHealthEventRepository(store, writeOwner);
            var initialStorageHealthEvents = storageHealthEvents
                .ListRecentAsync(200, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var runtime = new DesktopAgentRuntime(
                context,
                instanceId,
                monitoring,
                new MonitorCsvExporter(store),
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
                preferencesService);
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
                TryRemoveEndpoint(endpointPath);
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

    private static string PublishEndpoint(AgentEndpointRecord endpoint, string dataRoot)
    {
        var path = DataRootLayout.AgentEndpointPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(endpoint));
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private static void TryRemoveEndpoint(string path)
    {
        try
        {
            if (File.Exists(path))
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
    }
}
