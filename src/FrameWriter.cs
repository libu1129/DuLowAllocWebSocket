using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DuLowAllocWebSocket;

public sealed class FrameWriter : IDisposable
{
    private readonly Stream _transport;
    private readonly byte[] _maskScratch;

    public FrameWriter(Stream transport, WebSocketClientOptions options)
    {
        _transport = transport;
        _maskScratch = ArrayPool<byte>.Shared.Rent(options.SendScratchBufferSize);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_maskScratch);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, bool fin, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[14];
        int headerLen = 0;

        header[headerLen++] = (byte)((fin ? 0b1000_0000 : 0) | ((byte)opcode & 0x0F));

        if (payload.Length <= 125)
        {
            header[headerLen++] = (byte)(0b1000_0000 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[headerLen++] = 0b1000_0000 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header[headerLen..], (ushort)payload.Length);
            headerLen += 2;
        }
        else
        {
            header[headerLen++] = 0b1000_0000 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header[headerLen..], (ulong)payload.Length);
            headerLen += 8;
        }

        uint maskKey;
        {
            Span<byte> mask = header[headerLen..(headerLen + 4)];
            RandomNumberGenerator.Fill(mask);
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(mask);
            headerLen += 4;
        }

        _transport.Write(header[..headerLen]);

        int sent = 0;
        while (sent < payload.Length)
        {
            int chunkLen = Math.Min(_maskScratch.Length, payload.Length - sent);
            payload.Span.Slice(sent, chunkLen).CopyTo(_maskScratch);
            ApplyMask(_maskScratch.AsSpan(0, chunkLen), maskKey, sent);
            await _transport.WriteAsync(_maskScratch.AsMemory(0, chunkLen), ct).ConfigureAwait(false);
            sent += chunkLen;
        }
    }

    private static void ApplyMask(Span<byte> data, uint maskKey, int streamOffset)
    {
        Span<byte> mask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mask, maskKey);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= mask[(streamOffset + i) & 3];
        }
    }
}
