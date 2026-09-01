using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Ipc;

namespace WinPool.Agent.Tests;

public sealed class AgentControlProtocolCodecTests
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void MonitorRequestRoundTripsTargetSystemIdentity()
    {
        var systemId = SystemId.New();
        var request = new MonitorRequest(
            SessionId.New(),
            systemId,
            [new MonitorTarget(
                new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "pdh-wildcard"),
                "*")],
            [MonitorMetricKind.ActiveTimePercent],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);

        var roundTripped = JsonSerializer.Deserialize<MonitorRequest>(
            JsonSerializer.Serialize(request, SerializerOptions),
            SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(roundTripped.SystemId, roundTripped.Targets[0].ObjectId.System);
        Assert.Equal(systemId, roundTripped.Targets[0].ObjectId.System);
    }

    [Fact]
    public void CodecDecodesAllowlistedTypedRequest()
    {
        var codec = new AgentControlProtocolCodec();
        var correlationId = CorrelationId.New();
        var request = new GetAgentSnapshotRequest(correlationId);
        var envelope = Envelope(
            AgentControlMessageTypes.GetSnapshot,
            correlationId,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = codec.DecodeRequest(envelope);

        Assert.True(decoded.IsAccepted);
        Assert.IsType<GetAgentSnapshotRequest>(decoded.Request);
    }

    [Fact]
    public void CodecDecodesOpenMainWindowRequest()
    {
        var codec = new AgentControlProtocolCodec();
        var correlationId = CorrelationId.New();
        var request = new OpenMainWindowRequest(WorkspacePage.Manage, correlationId);
        var envelope = Envelope(
            AgentControlMessageTypes.OpenMainWindow,
            correlationId,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = codec.DecodeRequest(envelope);

        Assert.True(decoded.IsAccepted);
        Assert.Equal(request, Assert.IsType<OpenMainWindowRequest>(decoded.Request));
    }

    [Fact]
    public void CodecDecodesOpenNativePropertiesRequest()
    {
        var codec = new AgentControlProtocolCodec();
        var correlationId = CorrelationId.New();
        var request = new OpenAgentNativePropertiesRequest(
            new StorageObjectId(SystemId.New(), StorageObjectKind.PhysicalDisk, "physical:disk-0"),
            0,
            correlationId);
        var envelope = Envelope(
            AgentControlMessageTypes.OpenNativeProperties,
            correlationId,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = codec.DecodeRequest(envelope);

        Assert.True(decoded.IsAccepted);
        Assert.Equal(request, Assert.IsType<OpenAgentNativePropertiesRequest>(decoded.Request));
    }

    [Fact]
    public void CodecDecodesMonitoringRequests()
    {
        var systemId = SystemId.New();
        var monitorRequest = new MonitorRequest(
            SessionId.New(),
            systemId,
            [new MonitorTarget(
                new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "pdh-wildcard"),
                "*")],
            [MonitorMetricKind.ActiveTimePercent],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);
        var startCorrelation = CorrelationId.New();
        AgentRequest[] requests =
        [
            new StartAgentMonitoringRequest(monitorRequest, startCorrelation),
            new StopAgentMonitoringRequest(SessionId.New(), CorrelationId.New())
        ];
        var messageTypes = new[]
        {
            AgentControlMessageTypes.StartMonitoring,
            AgentControlMessageTypes.StopMonitoring
        };

        for (var index = 0; index < requests.Length; index++)
        {
            var decoded = new AgentControlProtocolCodec().DecodeRequest(
                Envelope(
                    messageTypes[index],
                    requests[index].CorrelationId,
                    JsonSerializer.SerializeToElement(
                        requests[index],
                        requests[index].GetType(),
                        SerializerOptions)));
            Assert.True(decoded.IsAccepted);
            Assert.Equal(requests[index].GetType(), decoded.Request!.GetType());
        }
    }

    [Fact]
    public void CodecDecodesTypedWorkspacePersistenceRequests()
    {
        var state = new WorkspaceSessionState(
            WorkspaceSessionState.CurrentSchemaVersion,
            WorkspacePage.Manage,
            "simulation:test",
            ManageWorkspaceCategory.System,
            new Dictionary<ManageWorkspaceCategory, string>(),
            string.Empty,
            DateTimeOffset.UtcNow);
        AgentRequest[] requests =
        [
            new LoadAgentWorkspaceStateRequest(CorrelationId.New()),
            new SaveAgentWorkspaceStateRequest(state, CorrelationId.New())
        ];
        var messageTypes = new[]
        {
            AgentControlMessageTypes.LoadWorkspaceState,
            AgentControlMessageTypes.SaveWorkspaceState
        };

        for (var index = 0; index < requests.Length; index++)
        {
            var decoded = new AgentControlProtocolCodec().DecodeRequest(
                Envelope(
                    messageTypes[index],
                    requests[index].CorrelationId,
                    JsonSerializer.SerializeToElement(
                        requests[index],
                        requests[index].GetType(),
                        SerializerOptions)));
            Assert.True(decoded.IsAccepted);
            Assert.Equal(requests[index].GetType(), decoded.Request!.GetType());
        }
    }

    [Fact]
    public void CodecDecodesTypedSimulationPersistenceRequests()
    {
        var payload = new SimulationDocumentPayload(
            "simulation:test",
            1,
            "Test",
            "{}",
            new string('a', 64),
            1,
            DateTimeOffset.UtcNow);
        AgentRequest[] requests =
        [
            new ListAgentSimulationDocumentsRequest(CorrelationId.New()),
            new SaveAgentSimulationDocumentRequest(payload, null, CorrelationId.New()),
            new DeleteAgentSimulationDocumentRequest(
                payload.DocumentId,
                payload.Sha256,
                CorrelationId.New())
        ];
        var messageTypes = new[]
        {
            AgentControlMessageTypes.ListSimulationDocuments,
            AgentControlMessageTypes.SaveSimulationDocument,
            AgentControlMessageTypes.DeleteSimulationDocument
        };

        for (var index = 0; index < requests.Length; index++)
        {
            var decoded = new AgentControlProtocolCodec().DecodeRequest(
                Envelope(
                    messageTypes[index],
                    requests[index].CorrelationId,
                    JsonSerializer.SerializeToElement(
                        requests[index],
                        requests[index].GetType(),
                        SerializerOptions)));
            Assert.True(decoded.IsAccepted);
            Assert.Equal(requests[index].GetType(), decoded.Request!.GetType());
        }
    }

    [Fact]
    public void CodecDecodesCommitSimulationEditRequest()
    {
        var systemId = SystemId.New();
        var payload = new SimulationDocumentPayload(
            "simulation:test",
            1,
            "Test",
            "{}",
            new string('a', 64),
            1,
            DateTimeOffset.UtcNow);
        var plan = OperationPlan.Create(
            new OperationRequest(
                OperationId.New(),
                EnvironmentId.New(),
                systemId,
                OperationIntent.SimulateStorageMutation,
                [],
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow),
            ExecutionCapability.None,
            RiskLevel.R1SimulationWrite,
            "v1",
            [],
            [],
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            new AlgorithmIdentity("test", "1.0", AlgorithmConfidence.Derived, "test"),
            DateTimeOffset.UtcNow);
        var correlationId = CorrelationId.New();
        var request = new CommitAgentSimulationEditRequest(
            payload,
            new string('b', 64),
            plan,
            [],
            correlationId);
        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.CommitSimulationEdit,
                correlationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        Assert.True(decoded.IsAccepted);
        Assert.IsType<CommitAgentSimulationEditRequest>(decoded.Request);
    }

    [Fact]
    public void CodecDecodesInventoryAndManageInventoryRequests()
    {
        var inventoryCorrelation = CorrelationId.New();
        var inventory = new CaptureAgentInventoryRequest(
            IncludeLegacyComparison: true,
            inventoryCorrelation);
        var inventoryDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.CaptureInventory,
                inventoryCorrelation,
                JsonSerializer.SerializeToElement(inventory, SerializerOptions)));
        Assert.IsType<CaptureAgentInventoryRequest>(inventoryDecoded.Request);
        Assert.True(inventoryDecoded.IsAccepted);

        var manageInventoryCorrelation = CorrelationId.New();
        var manageInventory = new CaptureAgentManageInventoryRequest(
            manageInventoryCorrelation);
        var manageInventoryDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.CaptureManageInventory,
                manageInventoryCorrelation,
                JsonSerializer.SerializeToElement(
                    manageInventory,
                    SerializerOptions)));
        Assert.IsType<CaptureAgentManageInventoryRequest>(manageInventoryDecoded.Request);
        Assert.True(manageInventoryDecoded.IsAccepted);

        var loadManageInventory = new LoadAgentManageInventoryRequest(
            manageInventoryCorrelation);
        var loadManageInventoryDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.LoadManageInventory,
                manageInventoryCorrelation,
                JsonSerializer.SerializeToElement(
                    loadManageInventory,
                    SerializerOptions)));
        Assert.IsType<LoadAgentManageInventoryRequest>(loadManageInventoryDecoded.Request);
        Assert.True(loadManageInventoryDecoded.IsAccepted);
    }

    [Fact]
    public void CodecDecodesExportMonitorCsvRequest()
    {
        var correlationId = CorrelationId.New();
        var request = new ExportAgentMonitorCsvRequest(
            SessionId.New(),
            Path.Combine(Path.GetTempPath(), "export.csv"),
            true,
            correlationId);
        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.ExportMonitorCsv,
                correlationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        Assert.True(decoded.IsAccepted);
        Assert.IsType<ExportAgentMonitorCsvRequest>(decoded.Request);
    }

    [Fact]
    public void CodecDecodesShutdownRequest()
    {
        var correlationId = CorrelationId.New();
        var request = new RequestAgentShutdownRequest(
            ShutdownReason.TrayExit,
            correlationId);
        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.Shutdown,
                correlationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        Assert.True(decoded.IsAccepted);
        Assert.IsType<RequestAgentShutdownRequest>(decoded.Request);
    }

    [Fact]
    public void CodecRejectsUnknownCommandDiscriminator()
    {
        var codec = new AgentControlProtocolCodec();
        var correlationId = CorrelationId.New();
        using var document = JsonDocument.Parse(
            """{"executable":"powershell.exe","arguments":"arbitrary"}""");
        var envelope = Envelope(
            "agent.request.run_command",
            correlationId,
            document.RootElement.Clone());

        var decoded = codec.DecodeRequest(envelope);

        Assert.False(decoded.IsAccepted);
        Assert.Null(decoded.Request);
        Assert.Equal("ipc.request.unsupported_type", decoded.Code);
    }

    [Fact]
    public void CodecRejectsPayloadCorrelationMismatch()
    {
        var codec = new AgentControlProtocolCodec();
        var envelopeCorrelation = CorrelationId.New();
        var request = new GetAgentSnapshotRequest(CorrelationId.New());
        var envelope = Envelope(
            AgentControlMessageTypes.GetSnapshot,
            envelopeCorrelation,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = codec.DecodeRequest(envelope);

        Assert.False(decoded.IsAccepted);
        Assert.Equal("ipc.request.correlation_mismatch", decoded.Code);
    }

    private static IpcEnvelope Envelope(
        string messageType,
        CorrelationId correlationId,
        JsonElement payload) =>
        new(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            correlationId.Value,
            messageType,
            DateTimeOffset.UtcNow,
            payload);
}
