using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class AgentBackedStorageSystemRepositoryTests
{
    [Fact]
    public async Task CatalogLoadsOnlyMetadataUntilTheSelectedDocumentIsRequested()
    {
        var connection = new RecordingConnection();
        var repository = new AgentBackedStorageSystemRepository(connection);
        var document = Document(StorageSystemKind.Simulation);
        await repository.SaveSimulationAsync(document);
        connection.Requests.Clear();
        var entries = await repository.LoadSimulationsAsync();
        Assert.Equal(document.Id, Assert.Single(entries).DocumentId);
        Assert.IsType<ListAgentSimulationDocumentsRequest>(Assert.Single(connection.Requests));
        var selected = await repository.LoadSimulationAsync(document.Id);
        Assert.Equal(document.Id, selected!.Id);
        Assert.IsType<LoadAgentSimulationDocumentRequest>(connection.Requests[1]);
    }

    [Fact]
    public void CodecRoundTripsSimulationAndRejectsLocalDocument()
    {
        var simulation = Document(StorageSystemKind.Simulation);
        var payload = SimulationDocumentCodec.Encode(simulation);
        var decoded = SimulationDocumentCodec.Decode(payload);

        Assert.Equal(simulation.Id, decoded.Id);
        Assert.Equal(simulation.SystemId, decoded.SystemId);
        Assert.Equal(simulation.Revision, decoded.Revision);
        Assert.Equal(simulation.DisplayName, decoded.DisplayName);
        Assert.Equal(64, payload.Sha256.Length);
        Assert.Throws<InvalidOperationException>(
            () => SimulationDocumentCodec.Encode(Document(StorageSystemKind.Local)));
        Assert.Throws<InvalidDataException>(
            () => SimulationDocumentCodec.Decode(payload with { Revision = 2 }));
    }

    [Fact]
    public async Task RepositoryUsesLoadedHashAsOptimisticSavePrecondition()
    {
        var connection = new RecordingConnection();
        var repository = new AgentBackedStorageSystemRepository(connection);
        var document = Document(StorageSystemKind.Simulation);

        Assert.Empty(await repository.LoadSimulationsAsync());
        await repository.SaveSimulationAsync(document);
        var firstHash = Assert.IsType<SaveAgentSimulationDocumentRequest>(
            connection.Requests[^1]).Document.Sha256;
        await repository.SaveSimulationAsync(
            document with
            {
                DisplayName = "Changed",
                Revision = 2,
                UpdatedAt = document.UpdatedAt.AddSeconds(1)
            });

        var second = Assert.IsType<SaveAgentSimulationDocumentRequest>(
            connection.Requests[^1]);
        Assert.Equal(firstHash, second.ExpectedPreviousSha256);
        Assert.NotEqual(firstHash, second.Document.Sha256);
    }

    [Fact]
    public async Task ManageInventoryUsesAgentAndValidatesTheDocumentEnvelope()
    {
        var connection = new RecordingConnection();
        var provider = new AgentBackedHardwareInventoryProvider(connection);

        var document = await provider.CollectLocalAsync(CancellationToken.None);
        var cached = await new AgentBackedMachineRecordService(connection)
            .LoadLocalScanAsync(CancellationToken.None);

        Assert.Equal(StorageSystemKind.Local, document.Kind);
        Assert.Equal(document.Id, cached!.Id);
        Assert.Collection(
            connection.Requests,
            request => Assert.IsType<CaptureAgentManageInventoryRequest>(request),
            request => Assert.IsType<LoadAgentInventoryCacheRequest>(request),
            request => Assert.IsType<LoadAgentInventoryCacheRequest>(request));
        var payload = LocalInventoryDocumentCodec.Encode(document);
        Assert.Equal(64, payload.Sha256.Length);
        Assert.Throws<InvalidDataException>(
            () => LocalInventoryDocumentCodec.Decode(
                payload with { Json = payload.Json + " " }));
    }

    [Fact]
    public async Task ManageInventoryFailurePreservesDiagnosticTextAndFallsBackToCode()
    {
        var connection = new RecordingConnection
        {
            CaptureFailure = new ApplicationMessage(
                "agent.inventory.manage_capture_failed", "agent.inventory.manage_capture_failed",
                "Stage: inventory.local_document.encode\nInvalidDataException: escaped payload limit",
                ApplicationMessageSeverity.Error, [])
        };
        var provider = new AgentBackedHardwareInventoryProvider(connection);

        var detailed = await Assert.ThrowsAsync<InventoryScanException>(
            () => provider.CollectLocalAsync(CancellationToken.None));
        Assert.Equal("Stage: inventory.local_document.encode\nInvalidDataException: escaped payload limit", detailed.Diagnostic);

        connection.CaptureFailure = new ApplicationMessage(
            "agent.inventory.manage_capture_failed", "agent.inventory.manage_capture_failed",
            string.Empty, ApplicationMessageSeverity.Error, []);
        var fallback = await Assert.ThrowsAsync<InventoryScanException>(
            () => provider.CollectLocalAsync(CancellationToken.None));
        Assert.Equal("agent.inventory.manage_capture_failed", fallback.Diagnostic);
    }

    [Fact]
    public void LocalInventoryCodecAcceptsUnixMillisecondCapturedAtWithADifferentOffset()
    {
        var document = Document(StorageSystemKind.Local);
        var payload = LocalInventoryDocumentCodec.Encode(document);
        var shifted = payload with
        {
            CapturedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                    payload.CapturedAtUtc.ToUnixTimeMilliseconds())
                .ToOffset(TimeSpan.FromHours(8))
        };

        var decoded = LocalInventoryDocumentCodec.Decode(shifted);
        Assert.Equal(document.Id, decoded.Id);
        Assert.Equal(
            payload.CapturedAtUtc.ToUnixTimeMilliseconds(),
            decoded.UpdatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void TryDecodeCachedRejectsDocumentWhenTheEnvelopeHashDoesNotMatch()
    {
        var document = Document(StorageSystemKind.Local);
        var payload = LocalInventoryDocumentCodec.Encode(document) with
        {
            Sha256 = new string('a', 64)
        };

        Assert.Throws<InvalidDataException>(() => LocalInventoryDocumentCodec.Decode(payload));
        var cached = LocalInventoryDocumentCodec.TryDecodeCached(payload);
        Assert.Null(cached);
    }

    private static StorageSystemDocument Document(StorageSystemKind kind)
    {
        var snapshot = StorageSnapshot.Empty("Test");
        return new(
            StorageSystemDocument.CurrentSchemaVersion,
            kind == StorageSystemKind.Simulation ? "simulation:test" : "local:test",
            kind,
            "Test",
            snapshot,
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
    }

    private sealed class RecordingConnection : IAgentConnection
    {
        private SimulationDocumentPayload? current;
        private LocalInventoryDocumentPayload? localInventory;
        public List<AgentRequest> Requests { get; } = [];
        public ApplicationMessage? CaptureFailure { get; set; }

        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentEvent> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<ApplicationResult<AgentResponse>> SendAsync(
            AgentRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request is CaptureAgentManageInventoryRequest && CaptureFailure is { } failure)
            {
                return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(
                    ApplicationStatus.Failed, request.CorrelationId, failure));
            }
            AgentResponse response = request switch
            {
                ListAgentSimulationDocumentsRequest =>
                    new SimulationDocumentListResponse(
                        current is null
                            ? []
                            : [new SimulationDocumentMetadata(
                                current.DocumentId,
                                current.DocumentSchemaVersion,
                                current.DisplayName,
                                current.Sha256,
                                current.Revision,
                                current.UpdatedAtUtc)],
                        null),
                LoadAgentSimulationDocumentRequest =>
                    new SimulationDocumentLoadedResponse(current),
                SaveAgentSimulationDocumentRequest save => Save(save),
                CaptureAgentManageInventoryRequest => CaptureLocal(),
                LoadAgentInventoryCacheRequest =>
                    new ManageInventoryLoadedResponse(
                        localInventory is null ? null : Guid.NewGuid(),
                        localInventory),
                _ => throw new NotSupportedException()
            };
            return Task.FromResult(
                ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
        }

        private ManageInventoryCaptureResponse CaptureLocal()
        {
            localInventory = LocalInventoryDocumentCodec.Encode(
                Document(StorageSystemKind.Local));
            return new(Guid.NewGuid(), localInventory.DocumentId, localInventory.Sha256, 1);
        }

        private SimulationDocumentSavedResponse Save(
            SaveAgentSimulationDocumentRequest request)
        {
            Assert.Equal(current?.Sha256, request.ExpectedPreviousSha256);
            current = request.Document;
            return new(new SimulationDocumentMetadata(current.DocumentId, current.DocumentSchemaVersion, current.DisplayName, current.Sha256, current.Revision, current.UpdatedAtUtc));
        }
    }
}
