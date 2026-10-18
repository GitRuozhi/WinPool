using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Ipc;

namespace WinPool.Agent.Tests;

public sealed class DocumentWireIntegrityTests
{
    [Theory]
    [InlineData("save")]
    [InlineData("load")]
    [InlineData("inventory")]
    public async Task DocumentWirePreservesOriginalJsonAndSha256(string entry)
    {
        // Different valid escape spellings, literal Unicode, and quoted paths must
        // remain byte-identical; semantic JSON equivalence is insufficient for SHA.
        const string body = """{"Id":"simulation:wire","SchemaVersion":3,"DisplayName":"wire","Kind":"Simulation","Revision":1,"quoted":"a\"b","path":"C:\\data\\新目录","escapes":"\u4e2d\u6587\u0022\u005c","html":"<>&+'"}""";
        var hash = Hash(body);
        var payload = new SimulationDocumentPayload("simulation:wire", 3, "wire", body, hash, 1, DateTimeOffset.UtcNow);
        var options = IpcJson.CreateOptions();
        var codec = new AgentControlProtocolCodec();
        var correlation = CorrelationId.New();
        IpcEnvelope outgoing;
        if (entry == "save")
        {
            var request = new SaveAgentSimulationDocumentRequest(payload, null, correlation);
            outgoing = new(IpcProtocol.CurrentVersion, Guid.NewGuid(), correlation.Value,
                AgentControlMessageTypes.SaveSimulationDocument, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(request, options));
        }
        else
        {
            AgentResponse response = entry == "load"
                ? new SimulationDocumentLoadedResponse(payload)
                : new ManageInventoryLoadedResponse(null,
                    new LocalInventoryDocumentPayload(payload.DocumentId, payload.DocumentSchemaVersion,
                        payload.DisplayName, body, hash, payload.UpdatedAtUtc));
            outgoing = codec.EncodeResponse(ApplicationResult<AgentResponse>.Succeeded(response, correlation), DateTimeOffset.UtcNow);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var frameGate = new SemaphoreSlim(1, 1);
        await using var wire = new MemoryStream();
        await IpcPayloadTransfer.WriteAsync(wire, outgoing, frameGate, timeout.Token);
        wire.Position = 0;
        await using var reader = new IpcPayloadTransfer();
        var received = await reader.ReadAsync(wire, timeout.Token);
        string actualBody;
        string suppliedHash;
        if (entry == "save")
        {
            var decoded = codec.DecodeRequest(received);
            Assert.True(decoded.IsAccepted);
            var saved = Assert.IsType<SaveAgentSimulationDocumentRequest>(decoded.Request).Document;
            (actualBody, suppliedHash) = (saved.Json, saved.Sha256);
        }
        else
        {
            var response = received.Payload.Deserialize<AgentControlResponsePayload>(options)!;
            Assert.Equal(ApplicationStatus.Succeeded, response.Status);
            var responseBody = response.Response ?? throw new InvalidDataException("Missing typed response.");
            if (entry == "load")
            {
                var loaded = responseBody.Deserialize<SimulationDocumentLoadedResponse>(options)!.Document!;
                (actualBody, suppliedHash) = (loaded.Json, loaded.Sha256);
            }
            else
            {
                var loaded = responseBody.Deserialize<ManageInventoryLoadedResponse>(options)!.Document!;
                (actualBody, suppliedHash) = (loaded.Json, loaded.Sha256);
            }
        }
        Assert.Equal(body, actualBody);
        Assert.Equal(hash, suppliedHash);
        Assert.Equal(hash, Hash(actualBody));
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}
