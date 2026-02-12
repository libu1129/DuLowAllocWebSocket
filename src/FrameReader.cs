using System.Buffers;
using System.Buffers.Binary;

namespace DuLowAllocWebSocket;

public readonly record struct FrameHeader(
    bool Fin,
    bool Rsv1,
    WebSocketOpcode Opcode,
    bool Masked,
    int PayloadLength,
    uint MaskKey);

public sealed class FrameReader : IDisposable
{
    private readonly Stream _transport;
    private readonly byte[] _scratch;
    private readonly WebSocketClientOptions _options;
    private int _bufferOffset;
    private int _bufferCount;

    public FrameReader(Stream transport, WebSocketClientOptions options)
    {
        _transport = transport;
        _options = options;
        _scratch = ArrayPool<byte>.Shared.Rent(options.ReceiveScratchBufferSize);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_scratch);

    public ValueTask<FrameHeader> ReadHeaderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<FrameHeader>(ReadHeader());
    }

    public FrameHeader ReadHeader()
    {
        ReadExactlySync(_scratch.AsSpan(0, 2));

        byte b0 = _scratch[0];
        byte b1 = _scratch[1];

        bool fin = (b0 & 0b1000_0000) != 0;
        bool rsv1 = (b0 & 0b0100_0000) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);

        bool masked = (b1 & 0b1000_0000) != 0;
        ulong len7 = (uint)(b1 & 0x7F);

        ulong payloadLen = len7;
        if (len7 == 126)
        {
            ReadExactlySync(_scratch.AsSpan(0, 2));
            payloadLen = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(0, 2));
        }
        else if (len7 == 127)
        {
            ReadExactlySync(_scratch.AsSpan(0, 8));
            payloadLen = BinaryPrimitives.ReadUInt64BigEndian(_scratch.AsSpan(0, 8));
        }

        if (payloadLen > (ulong)_options.MaxMessageBytes)
        {
            throw new WebSocketProtocolException($"Payload exceeds configured max ({_options.MaxMessageBytes} bytes).");
        }

        if (IsControl(opcode) && payloadLen > 125)
        {
            throw new WebSocketProtocolException("Control frame payload must be <= 125 bytes (RFC6455 5.5).");
        }

        uint maskKey = 0;
        if (masked)
        {
            if (_options.RejectMaskedServerFrames)
            {
                throw new WebSocketProtocolException("Masked server frame rejected by policy.");
            }

            ReadExactlySync(_scratch.AsSpan(0, 4));
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(_scratch.AsSpan(0, 4));
        }

        return new FrameHeader(fin, rsv1, opcode, masked, (int)payloadLen, maskKey);
    }

    public void ReadPayloadInto(FrameHeader header, MessageAssembler target)
    {
        int remaining = header.PayloadLength;
        uint maskKey = header.MaskKey;
        int maskOffset = 0;

        if (remaining == 0)
        {
            return;
        }

        while (remaining > 0)
        {
            int n;
            int chunkOffset;

            int buffered = _bufferCount - _bufferOffset;
            if (buffered > 0)
            {
                n = Math.Min(remaining, buffered);
                chunkOffset = _bufferOffset;
                _bufferOffset += n;
            }
            else
            {
                int toRead = Math.Min(remaining, _scratch.Length);
                n = _transport.Read(_scratch.AsSpan(0, toRead));
                if (n == 0) throw new WebSocketProtocolException("Connection closed while reading payload.");
                chunkOffset = 0;
            }

            var chunk = _scratch.AsSpan(chunkOffset, n);

            if (header.Masked)
            {
                Unmask(chunk, maskKey, ref maskOffset);
            }

            target.Append(chunk);
            remaining -= n;
        }
    }

    public ValueTask ReadPayloadIntoAsync(FrameHeader header, MessageAssembler target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadPayloadInto(header, target);
        return ValueTask.CompletedTask;
    }

    private void ReadExactlySync(Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int buffered = _bufferCount - _bufferOffset;
            if (buffered == 0)
            {
                _bufferCount = _transport.Read(_scratch);
                if (_bufferCount == 0) throw new WebSocketProtocolException("Connection closed.");
                _bufferOffset = 0;
                buffered = _bufferCount;
            }

            int toCopy = Math.Min(destination.Length - read, buffered);
            _scratch.AsSpan(_bufferOffset, toCopy).CopyTo(destination[read..]);
            _bufferOffset += toCopy;
            read += toCopy;
        }
    }

    private static bool IsControl(WebSocketOpcode opcode) => ((byte)opcode & 0x08) != 0;

    private static void Unmask(Span<byte> data, uint key, ref int offset)
    {
        Span<byte> mask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mask, key);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= mask[(offset + i) & 3];
        }

        offset = (offset + data.Length) & 3;
    }
}
