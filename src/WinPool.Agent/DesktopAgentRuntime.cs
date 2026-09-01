using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;
using WinPool.Monitoring;

namespace WinPool.Agent;

internal sealed class DesktopAgentRuntime :
    IAgentRequestOperations,
    IAgentShutdownActions,
    IAgentShutdownTerminalActions
{
    private readonly TrayApplicationContext tray;
    private readonly AgentInstanceId instanceId;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly MonitorCsvExporter monitorCsvExporter;
    private readonly AgentProcessRegistry processRegistry;
    private readonly WorkspaceSessionStateRepository workspaceState;
    private readonly SimulationDocumentRepository simulationDocuments;
    private readonly AgentInventoryCoordinator inventoryCoordinator;
    private readonly IStorageHealthEventSource storageHealthEventSource;
    private readonly StorageHealthEventRepository storageHealthEventRepository;
    private readonly AgentEventHub agentEvents;
    private readonly AgentLifecycleStateStore lifecycle;
    private readonly IUserPreferencesService preferencesService;
    private readonly IProcessIncarnationVerifier processIncarnationVerifier;
    private readonly string mainApplicationExecutablePath;
    private readonly CancellationTokenSource storageHealthEventCancellation = new();
    private readonly object storageHealthEventSync = new();
    private readonly Queue<StorageHealthEvent> recentStorageHealthEvents = new();
    private readonly Task storageHealthEventTask;

    public DesktopAgentRuntime(
        TrayApplicationContext tray,
        AgentInstanceId instanceId,
        MonitoringSessionCoordinator monitoring,
        MonitorCsvExporter monitorCsvExporter,
        AgentProcessRegistry processRegistry,
        WorkspaceSessionStateRepository workspaceState,
        SimulationDocumentRepository simulationDocuments,
        IInventoryProvider nativeInventoryProvider,
        IInventoryProvider legacyInventoryProvider,
        IHardwareInventoryProvider manageInventoryProvider,
        IInventoryComparer inventoryComparer,
        InventorySnapshotRepository inventorySnapshots,
        InventoryComparisonRepository inventoryComparisons,
        LocalInventoryDocumentRepository localInventoryDocument,
        LocalSystemIdentityResolver localSystemIdentity,
        IProcessIncarnationVerifier processIncarnationVerifier,
        string mainApplicationExecutablePath,
        IStorageHealthEventSource storageHealthEventSource,
        StorageHealthEventRepository storageHealthEventRepository,
        IReadOnlyList<StorageHealthEvent> initialStorageHealthEvents,
        AgentEventHub agentEvents,
        AgentLifecycleStateStore lifecycle,
        IUserPreferencesService preferencesService,
        IPhysicalDiskDeviceResolver? physicalDiskDeviceResolver = null)
    {
        this.tray = tray ?? throw new ArgumentNullException(nameof(tray));
        this.instanceId = instanceId;
        this.monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        this.monitorCsvExporter = monitorCsvExporter
            ?? throw new ArgumentNullException(nameof(monitorCsvExporter));
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.workspaceState = workspaceState
            ?? throw new ArgumentNullException(nameof(workspaceState));
        this.simulationDocuments = simulationDocuments
            ?? throw new ArgumentNullException(nameof(simulationDocuments));
        this.processIncarnationVerifier = processIncarnationVerifier
            ?? throw new ArgumentNullException(nameof(processIncarnationVerifier));
        if (string.IsNullOrWhiteSpace(mainApplicationExecutablePath)
            || !Path.IsPathFullyQualified(mainApplicationExecutablePath))
        {
            throw new ArgumentException(
                "The expected Main App executable path is required.",
                nameof(mainApplicationExecutablePath));
        }

        this.mainApplicationExecutablePath = Path.GetFullPath(mainApplicationExecutablePath);
        inventoryCoordinator = new(
            nativeInventoryProvider,
            legacyInventoryProvider,
            manageInventoryProvider,
            inventoryComparer,
            inventorySnapshots,
            inventoryComparisons,
            localInventoryDocument,
            localSystemIdentity,
            physicalDiskDeviceResolver ?? new WindowsPhysicalDiskDeviceResolver());
        this.storageHealthEventSource = storageHealthEventSource
            ?? throw new ArgumentNullException(nameof(storageHealthEventSource));
        this.storageHealthEventRepository = storageHealthEventRepository
            ?? throw new ArgumentNullException(nameof(storageHealthEventRepository));
        this.agentEvents = agentEvents ?? throw new ArgumentNullException(nameof(agentEvents));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.preferencesService = preferencesService
            ?? throw new ArgumentNullException(nameof(preferencesService));
        ArgumentNullException.ThrowIfNull(initialStorageHealthEvents);
        foreach (var storageEvent in initialStorageHealthEvents.TakeLast(200))
        {
            recentStorageHealthEvents.Enqueue(storageEvent);
        }

        storageHealthEventTask = Task.Run(CaptureStorageHealthEventsAsync);
    }

    public async Task<ApplicationResult<AgentResponse>> GetSnapshotAsync(
        GetAgentSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new AgentSnapshot(
            instanceId,
            tray.IsTrayVisible,
            ActiveMonitoringSession: monitoring.CurrentSession,
            ShutdownStatus: lifecycle.Snapshot(),
            Processes: processRegistry.Snapshot()
                .Select(AgentProcessProjection.ToRegistration)
                .ToArray(),
            LatestMonitorSamples: monitoring.CurrentSamples,
            RecentStorageHealthEvents: GetRecentStorageHealthEvents(),
            MonitorDiagnostics: monitoring.CurrentDiagnostics);
        return await SuccessAsync(new AgentSnapshotResponse(snapshot), request.CorrelationId);
    }

    public Task<ApplicationResult<AgentResponse>> OpenMainWindowAsync(
        OpenMainWindowRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tray.OpenMainApplication(request.Destination?.ToString());
        return SuccessAsync(new AgentAcknowledgement(), request.CorrelationId);
    }

    public Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync(
        OpenAgentNativePropertiesRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DiskNumber < 0)
        {
            return Task.FromResult(
                Reject(request.CorrelationId, "agent.native-properties.disk-number-invalid"));
        }

        var physicalDeviceId = inventoryCoordinator.ResolvePhysicalDeviceId(
            request.DiskNumber);

        if (!string.IsNullOrWhiteSpace(physicalDeviceId))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("devmgr.dll,DeviceProperties_RunDLL");
            startInfo.ArgumentList.Add("/DeviceID");
            startInfo.ArgumentList.Add(physicalDeviceId);
            Process.Start(startInfo);
            return SuccessAsync(new AgentAcknowledgement(), request.CorrelationId);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "diskmgmt.msc",
            UseShellExecute = true
        });
        return Task.FromResult<ApplicationResult<AgentResponse>>(new(
            ApplicationStatus.PartiallyCompleted,
            new AgentAcknowledgement(),
            [new ApplicationMessage(
                "agent.native-properties.disk-management-fallback",
                "agent.native-properties.disk-management-fallback",
                string.Empty,
                ApplicationMessageSeverity.Warning,
                [])],
            request.CorrelationId));
    }

    public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken) =>
        StartMonitoringCoreAsync(request, cancellationToken);

    public async Task RestoreContinuousMonitoringAsync(
        CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.LoadAsync(cancellationToken);
        if (!preferences.ContinuousMonitoringEnabled)
        {
            return;
        }

        var result = await StartMonitoringCoreAsync(
            new StartAgentMonitoringRequest(
                CreateDefaultMonitorRequest(preferences.MonitoringSampleRateHz),
                CorrelationId.New()),
            cancellationToken);
        if (!result.IsSuccess)
        {
            tray.SetMonitoringSession(null);
        }
    }

    public Task<ApplicationResult<AgentResponse>> StopMonitoringAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken) =>
        StopMonitoringCoreAsync(request, cancellationToken);

    public async Task<ApplicationResult<AgentResponse>> LoadWorkspaceStateAsync(
        LoadAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken)
    {
        var state = await workspaceState.LoadAsync(cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new WorkspaceStateLoadedResponse(state),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> SaveWorkspaceStateAsync(
        SaveAgentWorkspaceStateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.State is null
            || !WorkspaceSessionStateValidator.IsValid(request.State))
        {
            return Reject(request.CorrelationId, "agent.persistence.workspace_state_invalid");
        }
        try
        {
            var state = await workspaceState.SaveAsync(request.State, cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new WorkspaceStateSavedResponse(state),
                request.CorrelationId);
        }
        catch (ArgumentException)
        {
            return Reject(request.CorrelationId, "agent.persistence.workspace_state_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> ListSimulationDocumentsAsync(
        ListAgentSimulationDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var documents = await simulationDocuments.ListAsync(cancellationToken);
        return ApplicationResult<AgentResponse>.Succeeded(
            new SimulationDocumentListResponse(documents),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> SaveSimulationDocumentAsync(
        SaveAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await simulationDocuments.SaveAsync(
                request.Document,
                request.ExpectedPreviousSha256,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentSavedResponse(document),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or SimulationDocumentConflictException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_save_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> DeleteSimulationDocumentAsync(
        DeleteAgentSimulationDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await simulationDocuments.DeleteAsync(
                request.DocumentId,
                request.ExpectedSha256,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentDeletedResponse(request.DocumentId, deleted),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or SimulationDocumentConflictException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_delete_rejected");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> CommitSimulationEditAsync(
        CommitAgentSimulationEditRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await simulationDocuments.CommitEditAsync(
                request.Document,
                request.ExpectedPreviousSha256,
                request.Plan,
                request.Events,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new SimulationDocumentSavedResponse(document),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or InvalidDataException
            or SimulationDocumentConflictException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Reject(request.CorrelationId, "agent.persistence.simulation_edit_rejected");
        }
    }

    public Task<ApplicationResult<AgentResponse>> CaptureManageInventoryAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.CaptureManageAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> LoadManageInventoryAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.LoadManageAsync(request, cancellationToken);

    public Task<ApplicationResult<AgentResponse>> CaptureInventoryAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken) =>
        inventoryCoordinator.CaptureComparisonAsync(request, cancellationToken);

    public async Task<ApplicationResult<AgentResponse>> ExportMonitorCsvAsync(
        ExportAgentMonitorCsvRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (monitoring.CurrentSession?.SessionId == request.SessionId)
            {
                var flush = await monitoring.FlushAsync(
                    request.SessionId,
                    cancellationToken);
                if (!flush.IsSuccess)
                {
                    return new(
                        flush.Status,
                        null,
                        flush.Messages,
                        request.CorrelationId);
                }
            }

            var export = await monitorCsvExporter.ExportAsync(
                request.SessionId,
                request.DestinationPath,
                request.UserConfirmedOverwrite,
                cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ExportArtifactResponse(
                    export.DestinationPath,
                    export.Sha256,
                    export.RowCount),
                request.CorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId,
                Message("agent.monitor.export_cancelled"));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                Message("agent.monitor.export_failed"));
        }
    }

    public Task NotifyClientsAsync(
        ShutdownReason reason,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task StopMonitoringAsync(CancellationToken cancellationToken)
    {
        var session = monitoring.CurrentSession;
        if (session is not null)
        {
            await monitoring.StopAsync(session.SessionId, cancellationToken);
            tray.SetMonitoringSession(null);
        }
    }

    public Task<bool> RestoreTemporarySystemStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<int> FlushSqliteQueuesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(0);

    public Task CloseNamedPipesAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task CloseMainApplicationAsync(CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(
                    AppExitSignal.CreateName(IpcIdentity.HashUserSid(sid)));
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
        }

        var registrations = processRegistry.Snapshot()
            .Where(item => item.Kind == AgentManagedProcessKind.MainApplication)
            .ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        foreach (var registration in registrations)
        {
            processRegistry.TryBeginStopping(
                registration.ProcessInstanceId,
                registration.ProcessId,
                deadline);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            var anyLive = false;
            foreach (var registration in registrations)
            {
                if (processIncarnationVerifier.Matches(
                        registration,
                        mainApplicationExecutablePath))
                {
                    anyLive = true;
                }
                else
                {
                    processRegistry.TryMarkExited(
                        registration.ProcessInstanceId,
                        registration.ProcessId,
                        DateTimeOffset.UtcNow,
                        out _);
                }
            }

            if (!anyLive)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        foreach (var registration in registrations)
        {
            processRegistry.TryMarkExited(
                registration.ProcessInstanceId,
                registration.ProcessId,
                DateTimeOffset.UtcNow,
                out _);
        }
    }

    public Task RemoveTrayIconAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tray.HideTrayIcon();
        return Task.CompletedTask;
    }

    public async Task ExitAgentAsync(CancellationToken cancellationToken)
    {
        storageHealthEventCancellation.Cancel();
        try
        {
            await storageHealthEventTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }

        tray.ExitAgentThread();
    }

    Task IAgentShutdownTerminalActions.CloseNamedPipesAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        return CloseNamedPipesAsync(cancellationToken);
    }

    Task IAgentShutdownTerminalActions.RemoveTrayIconAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        return RemoveTrayIconAsync(cancellationToken);
    }

    async Task IAgentShutdownTerminalActions.ExitAgentAsync(
        AgentShutdownAttempt attempt,
        CancellationToken cancellationToken)
    {
        storageHealthEventCancellation.Cancel();
        try
        {
            await storageHealthEventTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }

        attempt.ThrowIfTerminalEffectIsNotAllowed(cancellationToken);
        tray.ExitAgentThread();
    }

    private async Task CaptureStorageHealthEventsAsync()
    {
        try
        {
            await foreach (var storageEvent in storageHealthEventSource.WatchAsync(
                               storageHealthEventCancellation.Token))
            {
                try
                {
                    await storageHealthEventRepository.AddAsync(
                        storageEvent,
                        storageHealthEventCancellation.Token);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidOperationException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    // Keep the live warning even if persistence is temporarily unavailable.
                }

                lock (storageHealthEventSync)
                {
                    recentStorageHealthEvents.Enqueue(storageEvent);
                    while (recentStorageHealthEvents.Count > 200)
                    {
                        recentStorageHealthEvents.Dequeue();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            storageHealthEventCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or System.Diagnostics.Eventing.Reader.EventLogException)
        {
        }
    }

    private IReadOnlyList<StorageHealthEvent> GetRecentStorageHealthEvents()
    {
        lock (storageHealthEventSync)
        {
            return recentStorageHealthEvents.ToArray();
        }
    }

    private static Task<ApplicationResult<AgentResponse>> SuccessAsync(
        AgentResponse response,
        CorrelationId correlationId) =>
        Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, correlationId));

    private async Task<ApplicationResult<AgentResponse>> StartMonitoringCoreAsync(
        StartAgentMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.StartAsync(
            request.MonitorRequest,
            cancellationToken);
        tray.SetMonitoringSession(result.Value);
        if (result.Value is { } session)
        {
            return new(
                result.Status,
                new MonitoringSessionResponse(session),
                result.Messages,
                request.CorrelationId);
        }

        return new(
            result.Status,
            null,
            result.Messages,
            request.CorrelationId);
    }

    private async Task<ApplicationResult<AgentResponse>> StopMonitoringCoreAsync(
        StopAgentMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.StopAsync(
            request.SessionId,
            cancellationToken);
        tray.SetMonitoringSession(result.Value);
        if (result.Value is { } session)
        {
            return new(
                result.Status,
                new MonitoringSessionResponse(session),
                result.Messages,
                request.CorrelationId);
        }

        return new(
            result.Status,
            null,
            result.Messages,
            request.CorrelationId);
    }

    private static MonitorRequest CreateDefaultMonitorRequest(double rateHz)
    {
        var systemId = SystemId.New();
        return new MonitorRequest(
            SessionId.New(),
            systemId,
            [
                new MonitorTarget(
                    new StorageObjectId(
                        systemId,
                        StorageObjectKind.PhysicalDisk,
                        "pdh-wildcard"),
                    "*"),
                new MonitorTarget(
                    new StorageObjectId(
                        systemId,
                        StorageObjectKind.VirtualDisk,
                        "pdh-storage-spaces-wildcard"),
                    "*")
            ],
            [
                MonitorMetricKind.ActiveTimePercent,
                MonitorMetricKind.ReadBytesPerSecond,
                MonitorMetricKind.WriteBytesPerSecond,
                MonitorMetricKind.AverageQueueLength,
                MonitorMetricKind.VirtualDiskActiveBytes,
                MonitorMetricKind.VirtualDiskMissingBytes,
                MonitorMetricKind.VirtualDiskStaleBytes,
                MonitorMetricKind.VirtualDiskNeedRegenerationBytes,
                MonitorMetricKind.VirtualDiskRegeneratingBytes,
                MonitorMetricKind.VirtualDiskPendingDeletionBytes
            ],
            TimeSpan.FromSeconds(1 / Math.Clamp(rateHz, 0.2, 20)),
            ContinueWhenUiCloses: true);
    }

    private static ApplicationResult<AgentResponse> Reject(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code));

    private static Task<ApplicationResult<AgentResponse>> RejectAsync(
        CorrelationId correlationId,
        string code) =>
        Task.FromResult(Reject(correlationId, code));

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);
}

internal sealed record AgentEndpointRecord(
    int ProtocolVersion,
    string PipeName,
    Guid Nonce,
    Guid AgentSessionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc);
