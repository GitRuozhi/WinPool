using System.Buffers.Binary;
using System.Text.Json;

namespace WinPool.Ipc;

public static class IpcFrameCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        if (payload.Length > IpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The IPC frame exceeds the configured maximum.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<IpcEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > IpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The IPC frame length is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(payload, SerializerOptions)
            ?? throw new InvalidDataException("The IPC frame has no envelope.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    private static void ValidateEnvelope(IpcEnvelope envelope)
    {
        if (envelope.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            throw new InvalidDataException("The IPC protocol version is not supported.");
        }

        if (envelope.MessageId == Guid.Empty
            || envelope.CorrelationId == Guid.Empty
            || string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            throw new InvalidDataException("The IPC envelope identity is incomplete.");
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The IPC stream ended within a frame.");
            }

            read += count;
        }
    }
}
