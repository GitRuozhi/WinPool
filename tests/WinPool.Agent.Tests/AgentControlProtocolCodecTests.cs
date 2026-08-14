using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Ipc;
using WinPool.Testing;

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
    public void CodecDecodesClosedToolPathConfigurationRequest()
    {
        var codec = new AgentControlProtocolCodec();
        var correlationId = CorrelationId.New();
        var request = new ConfigureAgentToolPathRequest(
            new ToolId("fio"),
            @"D:\Tools\fio.exe",
            correlationId);
        var envelope = Envelope(
            AgentControlMessageTypes.ConfigureToolPath,
            correlationId,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = codec.DecodeRequest(envelope);

        Assert.True(decoded.IsAccepted);
        Assert.Equal(request, Assert.IsType<ConfigureAgentToolPathRequest>(decoded.Request));
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

    [Fact]
    public void CodecDecodesTestPlanAndConfirmationWithoutClientAuthorizationObject()
    {
        var systemId = SystemId.New();
        var task = new TestTaskDefinition(
            TestTaskId.New(),
            "read",
            TestActionKind.RunIo,
            new ToolId("microsoft.diskspd"),
            new(
                1024 * 1024,
                4096,
                1,
                1,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                IoAccessPattern.Sequential,
                0,
                SoftwareCacheMode.Enabled,
                WriteThroughMode.Disabled,
                false),
            new Dictionary<string, TestParameter>());
        var definition = new TestDefinition(
            TestDefinitionId.New(),
            "read",
            "1",
            new Dictionary<string, TestParameter>(),
            [task],
            [new("io", task.Id, [], true)],
            AlgorithmConfidence.Derived);
        var plan = Assert.IsType<TestPlan>(
            new TestPlanCompiler().Compile(
                definition,
                new(
                    systemId,
                    new(
                        systemId,
                        StorageObjectKind.Partition,
                        "test-volume"),
                    Path.GetFullPath(Path.GetTempPath()),
                    long.MaxValue,
                    true),
                CorrelationId.New()).Value);
        plan = plan with
        {
            SupportActions =
            [
                new ClearSystemFileCacheAction(
                    RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                    new RamMapToolIdentity(
                        new string('a', 64),
                        "1.0",
                        "Microsoft Corporation",
                        new string('b', 64),
                        true)),
                new FlushVolumeAction(
                    plan.Target.VolumeId,
                    new(
                        plan.Target.VolumeId,
                        @"\\?\Volume{11111111-1111-1111-1111-111111111111}",
                        Path.GetFullPath(Path.GetTempPath()))),
                new TrimOrOptimizeVolumeAction(plan.Target.VolumeId),
                new AdjustProcessSchedulingAction(
                    [123],
                    TestProcessPriority.High,
                    [0, 1]),
                new TestProcessSchedulingPolicyAction(
                    TestProcessPriority.AboveNormal,
                    [0, 1]),
                new UseTemporaryPowerPlanAction(Guid.NewGuid()),
                new CleanTemporaryFilesAction(
                    [TemporaryFileScope.WinPoolTemporaryFiles])
            ]
        };
        var correlationId = CorrelationId.New();
        var request = new StartAgentTestRequest(
            definition,
            plan,
            UserConfirmedWrite: true,
            correlationId);
        var envelope = Envelope(
            AgentControlMessageTypes.StartTest,
            correlationId,
            JsonSerializer.SerializeToElement(request, SerializerOptions));

        var decoded = new AgentControlProtocolCodec().DecodeRequest(envelope);

        var typed = Assert.IsType<StartAgentTestRequest>(decoded.Request);
        Assert.True(decoded.IsAccepted);
        Assert.Equal(plan.PlanHash, typed.Plan.PlanHash);
        Assert.True(typed.UserConfirmedWrite);
        Assert.Collection(
            typed.Plan.SupportActions,
            item => Assert.IsType<ClearSystemFileCacheAction>(item),
            item => Assert.IsType<FlushVolumeAction>(item),
            item => Assert.IsType<TrimOrOptimizeVolumeAction>(item),
            item => Assert.IsType<AdjustProcessSchedulingAction>(item),
            item => Assert.IsType<TestProcessSchedulingPolicyAction>(item),
            item => Assert.IsType<UseTemporaryPowerPlanAction>(item),
            item => Assert.IsType<CleanTemporaryFilesAction>(item));
    }

    [Fact]
    public void CodecDecodesTypedTestResultQuery()
    {
        var correlationId = CorrelationId.New();
        var request = new GetAgentTestResultRequest(
            TestRunId.New(),
            correlationId);
        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.GetTestResult,
                correlationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        var typed = Assert.IsType<GetAgentTestResultRequest>(decoded.Request);
        Assert.True(decoded.IsAccepted);
        Assert.Equal(request.RunId, typed.RunId);
    }

    [Fact]
    public void CodecDecodesTypedTestPauseAndResumeRequests()
    {
        AgentRequest[] requests =
        [
            new PauseAgentTestRequest(TestRunId.New(), CorrelationId.New()),
            new ResumeAgentTestRequest(TestRunId.New(), CorrelationId.New())
        ];
        var messageTypes = new[]
        {
            AgentControlMessageTypes.PauseTest,
            AgentControlMessageTypes.ResumeTest
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
            Assert.Equal(requests[index], decoded.Request);
        }
    }

    [Fact]
    public void CodecDecodesClosedUserPresetRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var preset = new UserTestPreset(
            Guid.NewGuid(),
            "My preset",
            TestPresetScenario.IoBenchmark,
            new ToolId("microsoft.diskspd"),
            TestPresetVerificationMode.FullHash,
            50_505,
            IoAccessPattern.Random,
            30,
            1024L * 1024 * 1024,
            4096,
            4,
            32,
            60,
            5,
            2,
            3,
            true,
            now,
            now);
        AgentRequest[] requests =
        [
            new ListUserTestPresetsRequest(CorrelationId.New()),
            new SaveUserTestPresetRequest(preset, CorrelationId.New()),
            new DeleteUserTestPresetRequest(preset.PresetId, CorrelationId.New())
        ];
        var messageTypes = new[]
        {
            AgentControlMessageTypes.ListUserTestPresets,
            AgentControlMessageTypes.SaveUserTestPreset,
            AgentControlMessageTypes.DeleteUserTestPreset
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
    public void CodecDecodesTypedHistoryAndExportRequests()
    {
        var historyCorrelation = CorrelationId.New();
        var history = new ListAgentTestRunsRequest(
            TestRunHistoryFilter.Failed,
            25,
            historyCorrelation);
        var historyDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.ListTestRuns,
                historyCorrelation,
                JsonSerializer.SerializeToElement(history, SerializerOptions)));
        var exportCorrelation = CorrelationId.New();
        var export = new ExportAgentTestRunRequest(
            TestRunId.New(),
            TestExportFormat.EvidencePackage,
            Path.Combine(Path.GetTempPath(), "evidence.zip"),
            true,
            exportCorrelation);
        var exportDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.ExportTestRun,
                exportCorrelation,
                JsonSerializer.SerializeToElement(export, SerializerOptions)));

        Assert.IsType<ListAgentTestRunsRequest>(historyDecoded.Request);
        Assert.True(historyDecoded.IsAccepted);
        Assert.IsType<ExportAgentTestRunRequest>(exportDecoded.Request);
        Assert.True(exportDecoded.IsAccepted);

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

        var propertiesCorrelation = CorrelationId.New();
        var properties = new OpenAgentNativePropertiesRequest(
            new StorageObjectId(
                SystemId.New(),
                StorageObjectKind.PhysicalDisk,
                "physical:disk-0"),
            0,
            propertiesCorrelation);
        var propertiesDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.OpenNativeProperties,
                propertiesCorrelation,
                JsonSerializer.SerializeToElement(properties, SerializerOptions)));
        var propertiesRequest = Assert.IsType<OpenAgentNativePropertiesRequest>(propertiesDecoded.Request);
        Assert.True(propertiesDecoded.IsAccepted);
        Assert.Equal(0, propertiesRequest.DiskNumber);
    }

    [Fact]
    public void CodecDecodesHashBoundDitePersistenceRequest()
    {
        var correlationId = CorrelationId.New();
        var request = new PersistDiteLegacyImportRequest(
            Path.Combine(Path.GetTempPath(), "dite-results.csv"),
            new string('a', 64),
            correlationId);

        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.PersistDiteLegacyImport,
                correlationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        var typed = Assert.IsType<PersistDiteLegacyImportRequest>(decoded.Request);
        Assert.True(decoded.IsAccepted);
        Assert.Equal(request.SourcePath, typed.SourcePath);
        Assert.Equal(request.ExpectedSha256, typed.ExpectedSha256);

        var listCorrelation = CorrelationId.New();
        var list = new ListDiteLegacyImportsRequest(25, listCorrelation);
        var listDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.ListDiteLegacyImports,
                listCorrelation,
                JsonSerializer.SerializeToElement(list, SerializerOptions)));
        Assert.IsType<ListDiteLegacyImportsRequest>(listDecoded.Request);
        Assert.True(listDecoded.IsAccepted);

        var summaryCorrelation = CorrelationId.New();
        var summary = new GetDiteLegacyImportSummaryRequest(
            Guid.NewGuid(),
            summaryCorrelation);
        var summaryDecoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.GetDiteLegacyImportSummary,
                summaryCorrelation,
                JsonSerializer.SerializeToElement(summary, SerializerOptions)));
        Assert.IsType<GetDiteLegacyImportSummaryRequest>(summaryDecoded.Request);
        Assert.True(summaryDecoded.IsAccepted);
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
    public void CodecDecodesClosedDevelopmentDiagnosticsRequest()
    {
        var request = new GetDevelopmentDiagnosticsRequest(
            10,
            CorrelationId.New());

        var decoded = new AgentControlProtocolCodec().DecodeRequest(
            Envelope(
                AgentControlMessageTypes.GetDevelopmentDiagnostics,
                request.CorrelationId,
                JsonSerializer.SerializeToElement(request, SerializerOptions)));

        var typed = Assert.IsType<GetDevelopmentDiagnosticsRequest>(decoded.Request);
        Assert.True(decoded.IsAccepted);
        Assert.Equal(10, typed.RecentRunLimit);
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
