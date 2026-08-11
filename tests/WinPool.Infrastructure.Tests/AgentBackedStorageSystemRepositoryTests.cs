using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class AgentBackedStorageSystemRepositoryTests
{
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
                UpdatedAt = document.UpdatedAt.AddSeconds(1)
            });

        var second = Assert.IsType<SaveAgentSimulationDocumentRequest>(
            connection.Requests[^1]);
        Assert.Equal(firstHash, second.ExpectedPreviousSha256);
        Assert.NotEqual(firstHash, second.Document.Sha256);
    }

    [Fact]
    public async Task ManageInventoryUsesAgentAndValidatesTheSanitizedDocumentEnvelope()
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
            request => Assert.IsType<LoadAgentManageInventoryRequest>(request));
        var payload = LocalInventoryDocumentCodec.Encode(document);
        Assert.Equal(64, payload.Sha256.Length);
        Assert.Throws<InvalidDataException>(
            () => LocalInventoryDocumentCodec.Decode(
                payload with { SanitizedJson = payload.SanitizedJson + " " }));
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
            HardwareInventoryReport.Empty(DateTimeOffset.MinValue),
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
    }

    private sealed class RecordingConnection : IAgentConnection
    {
        private SimulationDocumentPayload? current;
        private LocalInventoryDocumentPayload? localInventory;
        public List<AgentRequest> Requests { get; } = [];

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
            AgentResponse response = request switch
            {
                ListAgentSimulationDocumentsRequest =>
                    new SimulationDocumentListResponse(current is null ? [] : [current]),
                SaveAgentSimulationDocumentRequest save => Save(save),
                CaptureAgentManageInventoryRequest => CaptureLocal(),
                LoadAgentManageInventoryRequest =>
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
            return new(Guid.NewGuid(), localInventory);
        }

        private SimulationDocumentSavedResponse Save(
            SaveAgentSimulationDocumentRequest request)
        {
            Assert.Equal(current?.Sha256, request.ExpectedPreviousSha256);
            current = request.Document with { Revision = (current?.Revision ?? 0) + 1 };
            return new(current);
        }
    }
}
