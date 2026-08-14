using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Monitoring;
using WinPool.ToolManagement;
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
                    "..",
                    "WinPool.App.exe"));
            var coordinator = new AgentSessionCoordinator(
                processRegistry,
                lifecycle,
                () => new AgentSnapshot(
                    instanceId,
                    context.IsTrayVisible,
                    ActiveMonitoringSession: null,
                    ActiveTestRunId: null,
                    ShutdownStatus: lifecycle.Snapshot(),
                    Processes: [],
                    LatestMonitorSamples: [],
                    RecentStorageHealthEvents: [],
                    MonitorDiagnostics: new MonitorRuntimeDiagnostics(0, 0),
                    CurrentToolStates: []));
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
            var toolPathConfiguration = PreferencesToolPathConfiguration
                .CreateAsync(preferencesService)
                .GetAwaiter()
                .GetResult();
            var toolCatalog = new ToolCatalog();
            var toolRegistry = new ExternalToolRegistry(
                toolCatalog,
                new ToolPathDiscovery(
                    toolPathConfiguration,
                    new EnvironmentToolSearchPath()),
                new WindowsToolVersionProbe(),
                new Sha256ToolFileHasher());
            workerProcesses = new WorkerProcessRepository(store, writeOwner);
            var testRuns = new TestRunRepository(store, writeOwner);
            var copyBatches = new CopyBatchRepository(store, writeOwner);
            var interruptedTestRuns = testRuns
                .RecoverInterruptedRunsAsync(
                    DateTimeOffset.UtcNow,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            foreach (var interruptedRunId in interruptedTestRuns)
            {
                var interruptedPlan = testRuns
                    .GetPlanAsync(
                        interruptedRunId,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (interruptedPlan is null)
                {
                    continue;
                }

                foreach (var copyStep in interruptedPlan.Steps.Where(
                             step => step.Action is TestActionKind.Copy
                                 && step.Parameters.ContainsKey(
                                     "sourceRelativeDirectory")
                                 && step.Parameters.ContainsKey(
                                     "destinationRelativeDirectory")))
                {
                    copyBatches.MarkOpenBatchInterruptedAsync(
                            interruptedRunId,
                            copyStep.Id,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            context.ShowInterruptedTestRecoveryNotice(
                interruptedTestRuns.Count);
            var diteLegacyImports =
                new DiteLegacyImportRepository(store, writeOwner);
            var testArtifacts = new TestArtifactStore(store, writeOwner);
            var storageHealthEvents =
                new StorageHealthEventRepository(store, writeOwner);
            var systemSupportAudit =
                new SystemSupportAuditRepository(store, writeOwner);
            var systemSupportRecoveryStore =
                new SystemSupportRecoveryRepository(store, writeOwner);
            var schedulingPort = new WindowsTestProcessSchedulingPort(
                processId => IsRegisteredTestProcess(processRegistry, processId));
            var powerPlanPort = new WindowsTemporaryPowerPlanPort(
                new ProcessWindowsCommandRunner());
            var systemSupportRecovery = new SystemSupportRecoveryCoordinator(
                systemSupportRecoveryStore,
                schedulingPort,
                powerPlanPort,
                systemSupportAudit,
                processId => IsRegisteredTestProcess(processRegistry, processId),
                IsProcessAlive);
            var recoverySummary = systemSupportRecovery
                .RecoverPendingAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (recoverySummary.Failed > 0)
            {
                context.ShowSystemSupportRecoveryWarning(recoverySummary.Failed);
            }
            var initialStorageHealthEvents = storageHealthEvents
                .ListRecentAsync(200, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var testWorkerHost = new TestWorkerProcessHost(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "TestWorker",
                    "WinPool.TestWorker.exe"),
                userHash,
                Environment.ProcessId);
            var elevatedBrokerHost = new ElevatedBrokerProcessHost(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Broker",
                    "WinPool.ElevatedBroker.exe"),
                userHash,
                Environment.ProcessId,
                agentSessionId,
                dataRoot);
            var runtime = new DesktopAgentRuntime(
                context,
                instanceId,
                monitoring,
                new MonitorCsvExporter(store),
                processRegistry,
                toolRegistry,
                new ToolPathConfigurationCoordinator(
                    toolCatalog,
                    toolPathConfiguration,
                    toolRegistry),
                new ExternalToolStateRepository(store, writeOwner),
                workerProcesses,
                testRuns,
                new UserTestPresetRepository(store, writeOwner),
                new WorkspaceSessionStateRepository(store, writeOwner),
                new SimulationDocumentRepository(store, writeOwner),
                copyBatches,
                diteLegacyImports,
                testArtifacts,
                new TestRunExporter(store, testRuns, testArtifacts),
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
                testWorkerHost,
                elevatedBrokerHost,
                systemSupportAudit,
                systemSupportRecovery,
                new TestProcessSchedulingScope(
                    schedulingPort,
                    systemSupportRecoveryStore,
                    systemSupportAudit),
                powerPlanPort,
                systemSupportRecoveryStore,
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

    private static bool IsRegisteredTestProcess(
        AgentProcessRegistry registry,
        int processId) =>
        registry.TryGet(processId, out var process) &&
        process is not null &&
        (process.Kind is AgentManagedProcessKind.TestWorker
            or AgentManagedProcessKind.ExternalTool) &&
        (process.State is SupervisedProcessState.Starting
            or SupervisedProcessState.Running);

    private static string ResolveProductRoot()
    {
        var agentRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        var parent = Directory.GetParent(agentRoot)?.FullName;
        return parent is not null
               && File.Exists(Path.Combine(parent, "WinPool.App.exe"))
            ? parent
            : agentRoot;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
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
