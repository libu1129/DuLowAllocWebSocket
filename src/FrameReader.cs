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

    public FrameReader(Stream transport, WebSocketClientOptions options)
    {
        _transport = transport;
        _options = options;
        _scratch = ArrayPool<byte>.Shared.Rent(options.ReceiveScratchBufferSize);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_scratch);

    public async ValueTask<FrameHeader> ReadHeaderAsync(CancellationToken ct)
    {
        await ReadExactAsync(_scratch.AsMemory(0, 2), ct).ConfigureAwait(false);
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
            await ReadExactAsync(_scratch.AsMemory(0, 2), ct).ConfigureAwait(false);
            payloadLen = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(0, 2));
        }
        else if (len7 == 127)
        {
            await ReadExactAsync(_scratch.AsMemory(0, 8), ct).ConfigureAwait(false);
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

            await ReadExactAsync(_scratch.AsMemory(0, 4), ct).ConfigureAwait(false);
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(_scratch.AsSpan(0, 4));
        }

        return new FrameHeader(fin, rsv1, opcode, masked, (int)payloadLen, maskKey);
    }

    public async ValueTask ReadPayloadIntoAsync(FrameHeader header, MessageAssembler target, CancellationToken ct)
    {
        int remaining = header.PayloadLength;
        uint maskKey = header.MaskKey;
        int maskOffset = 0;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, _scratch.Length);
            int n = await _transport.ReadAsync(_scratch.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (n == 0) throw new WebSocketProtocolException("Connection closed while reading payload.");

            if (header.Masked)
            {
                Unmask(_scratch.AsSpan(0, n), maskKey, ref maskOffset);
            }

            target.Append(_scratch.AsSpan(0, n));
            remaining -= n;
        }
    }

    private async ValueTask ReadExactAsync(Memory<byte> memory, CancellationToken ct)
    {
        int read = 0;
        while (read < memory.Length)
        {
            int n = await _transport.ReadAsync(memory[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new WebSocketProtocolException("Connection closed.");
            read += n;
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
