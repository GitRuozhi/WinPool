using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent;

public sealed record AgentControlRequestDecodeResult(
    bool IsAccepted,
    AgentRequest? Request,
    CorrelationId CorrelationId,
    string Code);

public sealed record AgentControlHandshakeDecision(
    HandshakeValidation Validation,
    AgentHandshakeReply? Reply);

public sealed record AgentControlResponsePayload(
    ApplicationStatus Status,
    CorrelationId CorrelationId,
    IReadOnlyList<ApplicationMessage> Messages,
    string? ResponseType,
    JsonElement? Response);

/// <summary>
/// Converts framed JSON only to the closed AgentRequest hierarchy. Message type strings are
/// protocol discriminators, never executable names or command lines.
/// </summary>
public sealed class AgentControlProtocolCodec
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public AgentHandshakeRequest DecodeHandshake(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(
                envelope.MessageType,
                AgentControlMessageTypes.HandshakeRequest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The first control message must be a handshake.");
        }

        return envelope.Payload.Deserialize<AgentHandshakeRequest>(SerializerOptions)
               ?? throw new InvalidDataException("The handshake payload is empty.");
    }

    public AgentControlRequestDecodeResult DecodeRequest(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var correlationId = new CorrelationId(envelope.CorrelationId);
        if (correlationId.Value == Guid.Empty)
        {
            return Reject(correlationId, "ipc.request.missing_correlation");
        }

        try
        {
            AgentRequest? request = envelope.MessageType switch
            {
                AgentControlMessageTypes.GetSnapshot =>
                    Deserialize<GetAgentSnapshotRequest>(envelope),
                AgentControlMessageTypes.GetDevelopmentDiagnostics =>
                    Deserialize<GetDevelopmentDiagnosticsRequest>(envelope),
                AgentControlMessageTypes.OpenMainWindow =>
                    Deserialize<OpenMainWindowRequest>(envelope),
                AgentControlMessageTypes.OpenNativeProperties =>
                    Deserialize<OpenAgentNativePropertiesRequest>(envelope),
                AgentControlMessageTypes.StartMonitoring =>
                    Deserialize<StartAgentMonitoringRequest>(envelope),
                AgentControlMessageTypes.StopMonitoring =>
                    Deserialize<StopAgentMonitoringRequest>(envelope),
                AgentControlMessageTypes.StartTest =>
                    Deserialize<StartAgentTestRequest>(envelope),
                AgentControlMessageTypes.CancelTest =>
                    Deserialize<CancelAgentTestRequest>(envelope),
                AgentControlMessageTypes.GetTestResult =>
                    Deserialize<GetAgentTestResultRequest>(envelope),
                AgentControlMessageTypes.ListTestRuns =>
                    Deserialize<ListAgentTestRunsRequest>(envelope),
                AgentControlMessageTypes.ListUserTestPresets =>
                    Deserialize<ListUserTestPresetsRequest>(envelope),
                AgentControlMessageTypes.SaveUserTestPreset =>
                    Deserialize<SaveUserTestPresetRequest>(envelope),
                AgentControlMessageTypes.DeleteUserTestPreset =>
                    Deserialize<DeleteUserTestPresetRequest>(envelope),
                AgentControlMessageTypes.LoadWorkspaceState =>
                    Deserialize<LoadAgentWorkspaceStateRequest>(envelope),
                AgentControlMessageTypes.SaveWorkspaceState =>
                    Deserialize<SaveAgentWorkspaceStateRequest>(envelope),
                AgentControlMessageTypes.ListSimulationDocuments =>
                    Deserialize<ListAgentSimulationDocumentsRequest>(envelope),
                AgentControlMessageTypes.SaveSimulationDocument =>
                    Deserialize<SaveAgentSimulationDocumentRequest>(envelope),
                AgentControlMessageTypes.DeleteSimulationDocument =>
                    Deserialize<DeleteAgentSimulationDocumentRequest>(envelope),
                AgentControlMessageTypes.CommitSimulationEdit =>
                    Deserialize<CommitAgentSimulationEditRequest>(envelope),
                AgentControlMessageTypes.PersistDiteLegacyImport =>
                    Deserialize<PersistDiteLegacyImportRequest>(envelope),
                AgentControlMessageTypes.ListDiteLegacyImports =>
                    Deserialize<ListDiteLegacyImportsRequest>(envelope),
                AgentControlMessageTypes.GetDiteLegacyImportSummary =>
                    Deserialize<GetDiteLegacyImportSummaryRequest>(envelope),
                AgentControlMessageTypes.ExportTestRun =>
                    Deserialize<ExportAgentTestRunRequest>(envelope),
                AgentControlMessageTypes.CaptureInventory =>
                    Deserialize<CaptureAgentInventoryRequest>(envelope),
                AgentControlMessageTypes.CaptureManageInventory =>
                    Deserialize<CaptureAgentManageInventoryRequest>(envelope),
                AgentControlMessageTypes.LoadManageInventory =>
                    Deserialize<LoadAgentManageInventoryRequest>(envelope),
                AgentControlMessageTypes.DetectTool =>
                    Deserialize<DetectAgentToolRequest>(envelope),
                AgentControlMessageTypes.InstallMsiTool =>
                    Deserialize<InstallAgentMsiToolRequest>(envelope),
                AgentControlMessageTypes.ExportMonitorCsv =>
                    Deserialize<ExportAgentMonitorCsvRequest>(envelope),
                AgentControlMessageTypes.ReviewSystemSupport =>
                    Deserialize<ReviewAgentSystemSupportRequest>(envelope),
                AgentControlMessageTypes.ExecuteSystemSupport =>
                    Deserialize<ExecuteAgentSystemSupportRequest>(envelope),
                AgentControlMessageTypes.Shutdown =>
                    Deserialize<RequestAgentShutdownRequest>(envelope),
                _ => null
            };

            if (request is null)
            {
                return Reject(correlationId, "ipc.request.unsupported_type");
            }

            if (request.CorrelationId != correlationId)
            {
                return Reject(correlationId, "ipc.request.correlation_mismatch");
            }

            return new(true, request, correlationId, "ipc.request.accepted");
        }
        catch (JsonException)
        {
            return Reject(correlationId, "ipc.request.invalid_payload");
        }
        catch (NotSupportedException)
        {
            return Reject(correlationId, "ipc.request.invalid_payload");
        }
    }

    public IpcEnvelope EncodeHandshakeDecision(
        AgentControlHandshakeDecision decision,
        Guid correlationId,
        DateTimeOffset sentAtUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var payload = decision.Validation.IsAccepted
            ? JsonSerializer.SerializeToElement(decision.Reply, SerializerOptions)
            : JsonSerializer.SerializeToElement(decision.Validation, SerializerOptions);
        return new(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            correlationId,
            decision.Validation.IsAccepted
                ? AgentControlMessageTypes.HandshakeAccepted
                : AgentControlMessageTypes.HandshakeRejected,
            sentAtUtc,
            payload);
    }

    public IpcEnvelope EncodeResponse(
        ApplicationResult<AgentResponse> result,
        DateTimeOffset sentAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        JsonElement? response = result.Value is null
            ? null
            : JsonSerializer.SerializeToElement(
                result.Value,
                result.Value.GetType(),
                SerializerOptions);
        var payload = new AgentControlResponsePayload(
            result.Status,
            result.CorrelationId,
            result.Messages,
            result.Value?.GetType().Name,
            response);
        return new(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            result.CorrelationId.Value,
            AgentControlMessageTypes.Response,
            sentAtUtc,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));
    }

    public ApplicationResult<AgentResponse> CreateDecodeRejection(
        AgentControlRequestDecodeResult decoded) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            decoded.CorrelationId,
            new ApplicationMessage(
                decoded.Code,
                decoded.Code,
                string.Empty,
                ApplicationMessageSeverity.Warning,
                []));

    private static T Deserialize<T>(IpcEnvelope envelope)
        where T : AgentRequest =>
        envelope.Payload.Deserialize<T>(SerializerOptions)
        ?? throw new JsonException("The request payload is empty.");

    private static AgentControlRequestDecodeResult Reject(
        CorrelationId correlationId,
        string code) =>
        new(false, null, correlationId, code);
}

/// <summary>
/// One-user, local named-pipe control listener. It performs handshake validation before
/// dispatching any typed request to the session coordinator.
/// </summary>
public sealed class CurrentUserAgentControlServer
{
    private readonly string pipeName;
    private readonly Guid expectedNonce;
    private readonly string expectedUserSidHash;
    private readonly Guid agentSessionId;
    private readonly int agentProcessId;
    private readonly AgentSessionCoordinator coordinator;
    private readonly AgentControlProtocolCodec codec;
    private readonly TimeProvider timeProvider;
    private readonly Func<ProcessRegistration, CancellationToken, Task>?
        persistProcess;
    private readonly Func<int, bool> verifyClientProcess;
    private readonly AgentEventHub eventHub;

    public CurrentUserAgentControlServer(
        string pipeName,
        Guid expectedNonce,
        string expectedUserSidHash,
        Guid agentSessionId,
        int agentProcessId,
        AgentSessionCoordinator coordinator,
        AgentControlProtocolCodec? codec = null,
        TimeProvider? timeProvider = null,
        Func<ProcessRegistration, CancellationToken, Task>? persistProcess = null,
        Func<int, bool>? verifyClientProcess = null,
        AgentEventHub? eventHub = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUserSidHash);
        if (expectedNonce == Guid.Empty || agentSessionId == Guid.Empty)
        {
            throw new ArgumentException("Control server nonces and session IDs cannot be empty.");
        }

        if (agentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(agentProcessId));
        }

        this.pipeName = pipeName;
        this.expectedNonce = expectedNonce;
        this.expectedUserSidHash = expectedUserSidHash;
        this.agentSessionId = agentSessionId;
        this.agentProcessId = agentProcessId;
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.codec = codec ?? new AgentControlProtocolCodec();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.persistProcess = persistProcess;
        this.verifyClientProcess = verifyClientProcess ?? (_ => true);
        this.eventHub = eventHub ?? new AgentEventHub();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && coordinator.State != AgentSessionState.Stopped)
        {
            await using var server = CurrentUserPipeFactory.CreateServer(pipeName);
            await server.WaitForConnectionAsync(cancellationToken);
            try
            {
                await ServeConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is
                EndOfStreamException or InvalidDataException or JsonException or IOException)
            {
                // A malformed or disconnected client owns only this connection.
                // The listener remains available for status and shutdown retry.
            }
        }
    }

    private async Task ServeConnectionAsync(
        System.IO.Pipes.NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var handshakeEnvelope = await IpcFrameCodec.ReadAsync(stream, cancellationToken);
        var handshake = codec.DecodeHandshake(handshakeEnvelope);
        var now = timeProvider.GetUtcNow();
        var validation = AgentHandshakeValidator.Validate(
            handshake,
            expectedNonce,
            expectedUserSidHash,
            now);
        if (validation.IsAccepted
            && CurrentUserPipeFactory.GetConnectedClientProcessId(stream)
               != handshake.ProcessId)
        {
            validation = new(
                false,
                HandshakeRejection.InvalidProcess,
                "ipc.handshake.process_mismatch");
        }
        if (validation.IsAccepted && !verifyClientProcess(handshake.ProcessId))
        {
            validation = new(
                false,
                HandshakeRejection.InvalidProcess,
                "ipc.handshake.client-image-mismatch");
        }
        if (coordinator.State == AgentSessionState.Stopped)
        {
            validation = new(
                false,
                HandshakeRejection.None,
                "ipc.handshake.agent_shutting_down");
        }

        var connectionId = Guid.NewGuid();
        var eventNonce = Guid.NewGuid();
        var eventEndpoint = validation.IsAccepted
            ? new AgentEventPipeEndpoint(
                IpcIdentity.CreateAgentEventPipeName(
                    expectedUserSidHash,
                    connectionId,
                    eventNonce),
                connectionId,
                eventNonce,
                now.AddSeconds(30))
            : null;
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var eventTask = validation.IsAccepted
            ? new CurrentUserAgentEventServer(
                    eventHub,
                    eventEndpoint!,
                    agentProcessId,
                    handshake.ProcessId,
                    verifyClientProcess,
                    timeProvider)
                .RunAsync(eventCancellation.Token)
            : Task.CompletedTask;
        var reply = validation.IsAccepted
            ? new AgentHandshakeReply(
                IpcProtocol.CurrentVersion,
                connectionId,
                agentSessionId,
                agentProcessId,
                now,
                eventEndpoint)
            : null;
        await IpcFrameCodec.WriteAsync(
            stream,
            codec.EncodeHandshakeDecision(
                new(validation, reply),
                handshakeEnvelope.CorrelationId,
                now),
            cancellationToken);
        if (!validation.IsAccepted)
        {
            return;
        }

        try
        {
            var registration = new AgentManagedProcess(
                ProcessInstanceId.New(),
                handshake.ProcessId,
                AgentManagedProcessKind.MainApplication,
                new CorrelationId(handshakeEnvelope.CorrelationId),
                now,
                now,
                SupervisedProcessState.Running,
                OwnsJobObject: false,
                ShutdownDeadlineUtc: null);
            if (coordinator.TryRegisterProcess(registration))
            {
                await PersistProcessAsync(registration, cancellationToken);
            }
            else
            {
                coordinator.ProcessRegistry.TryRecordHeartbeat(
                    handshake.ProcessId,
                    now);
                if (coordinator.ProcessRegistry.TryGet(
                        handshake.ProcessId,
                        out var existingRegistration)
                    && existingRegistration is not null)
                {
                    await PersistProcessAsync(existingRegistration, cancellationToken);
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                IpcEnvelope envelope;
                try
                {
                    envelope = await IpcFrameCodec.ReadAsync(stream, cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    return;
                }

                var decoded = codec.DecodeRequest(envelope);
                coordinator.ProcessRegistry.TryRecordHeartbeat(
                    handshake.ProcessId,
                    timeProvider.GetUtcNow());
                if (coordinator.ProcessRegistry.TryGet(
                        handshake.ProcessId,
                        out var heartbeatRegistration)
                    && heartbeatRegistration is not null)
                {
                    await PersistProcessAsync(heartbeatRegistration, cancellationToken);
                }
                var result = decoded.IsAccepted
                    ? await coordinator.HandleAsync(decoded.Request!, cancellationToken)
                    : codec.CreateDecodeRejection(decoded);
                await IpcFrameCodec.WriteAsync(
                    stream,
                    codec.EncodeResponse(result, timeProvider.GetUtcNow()),
                    cancellationToken);

                if (coordinator.State == AgentSessionState.Stopped)
                {
                    return;
                }
            }
        }
        finally
        {
            eventCancellation.Cancel();
            await eventTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private Task PersistProcessAsync(
        AgentManagedProcess process,
        CancellationToken cancellationToken) =>
        persistProcess?.Invoke(
            new(
                process.ProcessInstanceId,
                process.ProcessId,
                WorkerKind.MainApplication,
                process.CorrelationId,
                process.StartedAtUtc,
                process.LastHeartbeatUtc,
                process.State,
                process.OwnsJobObject,
                process.ShutdownDeadlineUtc),
            cancellationToken)
        ?? Task.CompletedTask;
}

public static class AgentClientProcessVerifier
{
    public static bool IsExpectedExecutable(int processId, string expectedExecutablePath)
    {
        if (processId <= 0 ||
            string.IsNullOrWhiteSpace(expectedExecutablePath) ||
            !Path.IsPathFullyQualified(expectedExecutablePath))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var actual = process.MainModule?.FileName;
            return actual is not null &&
                   StringComparer.OrdinalIgnoreCase.Equals(
                       Path.GetFullPath(actual),
                       Path.GetFullPath(expectedExecutablePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return false;
        }
    }
}
