using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using WinPool.Ipc;

namespace WinPool.Ipc.Tests;

public sealed class IpcPayloadTransferTests
{
    [Fact]
    public async Task CancellationBeforeOwningAFrameDoesNotReportAStartedRequest()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        await using var wire = new MemoryStream();
        var started = false;
        var envelope = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(),
            AgentControlMessageTypes.GetSnapshot, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }));
        var writing = IpcPayloadTransfer.WriteAsync(wire, envelope, gate, cancellation.Token, () => started = true);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.False(started);
        Assert.Equal(0, wire.Length);
    }

    [Fact]
    public async Task Exact64MiBStructuredBodyLeavesRoomForItsWireEnvelope()
    {
        const int bodyBytes = 64 * 1024 * 1024;
        static object Body(string value) => new
        {
            documentId = "simulation:boundary", revision = 17, commitId = "immutable-boundary",
            rows = new[] { new { index = 17, target = "physical:boundary", value } }
        };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(Body(string.Empty)).Length;
        var valueLength = bodyBytes - overhead;
        var payload = JsonSerializer.SerializeToElement(Body(new string('x', valueLength)));
        Assert.Equal(bodyBytes, Encoding.UTF8.GetByteCount(payload.GetRawText()));
        var expected = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(),
            AgentControlMessageTypes.CommitSimulationEdit, DateTimeOffset.UtcNow, payload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var gate = new SemaphoreSlim(1, 1);
        await using var wire = new FileStream(Path.Combine(Path.GetTempPath(), "WinPool-IpcBoundary-wire-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        await IpcPayloadTransfer.WriteAsync(wire, expected, gate, timeout.Token);
        Assert.True(wire.Length > bodyBytes);
        wire.Position = 0;
        await using var reader = new IpcPayloadTransfer();
        var actual = await reader.ReadAsync(wire, timeout.Token);
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(valueLength, actual.Payload.GetProperty("rows")[0].GetProperty("value").GetString()!.Length);
    }

    [Fact]
    public async Task EscapedLargeBodyRoundTripsInBoundedFrames()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var gate = new SemaphoreSlim(1, 1);
        var expected = Body();
        await using var wire = new MemoryStream();
        await IpcPayloadTransfer.WriteAsync(wire, expected, gate, timeout.Token);
        var bytes = wire.ToArray();
        var frames = 0;
        for (var offset = 0; offset < bytes.Length; frames++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            Assert.InRange(length, 1, IpcProtocol.MaximumFrameBytes);
            offset += sizeof(int) + length;
        }
        Assert.True(frames > 1);
        wire.Position = 0;
        await using var reader = new IpcPayloadTransfer();
        var actual = await reader.ReadAsync(wire, timeout.Token);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.MessageType, actual.MessageType);
        Assert.Equal(expected.Payload.GetRawText(), actual.Payload.GetRawText());
    }

    [Fact]
    public async Task LightResponseDoesNotWaitForLargeBodyParsing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var gate = new SemaphoreSlim(1, 1);
        using var parseGate = new SemaphoreSlim(0, 1);
        await using var wire = new MemoryStream();
        var body = Body();
        await IpcPayloadTransfer.WriteAsync(wire, body, gate, timeout.Token);
        var light = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(),
            AgentControlMessageTypes.Response, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { state = "finished" }));
        await IpcFrameCodec.WriteAsync(wire, light, timeout.Token);
        wire.Position = 0;
        await using var reader = new IpcPayloadTransfer(parseGate);
        var first = await reader.ReadAsync(wire, timeout.Token);
        Assert.Equal(light.CorrelationId, first.CorrelationId);
        parseGate.Release();
        Assert.Equal(body.CorrelationId, (await reader.ReadAsync(wire, timeout.Token)).CorrelationId);
    }

    [Fact]
    public async Task AbandonedPartialTransferReleasesItsReservation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var gate = new SemaphoreSlim(1, 1);
        await using var wire = new MemoryStream();
        var body = Body();
        await IpcPayloadTransfer.WriteAsync(wire, body, gate, timeout.Token);
        var bytes = wire.ToArray();
        var firstLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var partial = new MemoryStream(bytes, 0, firstLength + sizeof(int));
            await using var reader = new IpcPayloadTransfer();
            await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(partial, timeout.Token));
        }
        wire.Position = 0;
        await using var recovered = new IpcPayloadTransfer();
        Assert.Equal(body.CorrelationId, (await recovered.ReadAsync(wire, timeout.Token)).CorrelationId);
    }

    private static IpcEnvelope Body() => new(IpcProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(),
        AgentControlMessageTypes.CommitSimulationEdit, DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(new
        {
            documentId = "simulation:large", revision = 7, commitId = "immutable",
            rows = Enumerable.Range(0, 14_000).Select(index => new
                { index, target = "physical:" + index, value = new string('"', 48) + "\\line\n" + index })
        }));
}
