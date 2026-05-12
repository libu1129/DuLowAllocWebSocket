using System.Net.WebSockets;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class FrameReaderZeroCopyTests
{
    [Fact]
    public void TryReadPayloadAsMemory_WhenPayloadFullyBuffered_ReturnsPayloadAndKeepsNextFrameAligned()
    {
        byte[] firstPayload = [1, 2, 3, 4];
        byte[] secondPayload = [9, 8, 7];
        byte[] data = Concat(
            BuildUnmaskedFrame(WebSocketOpcode.Binary, firstPayload),
            BuildUnmaskedFrame(WebSocketOpcode.Text, secondPayload));

        using var reader = new FrameReader(new MemoryStream(data), Options());

        FrameHeader first = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(first, out ReadOnlyMemory<byte> payload));
        Assert.Equal(firstPayload, payload.ToArray());

        FrameHeader second = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Text, second.Opcode);
        Assert.Equal(secondPayload, ReadPayload(reader, second));
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenPayloadIsPartiallyBuffered_ReturnsFalseWithoutConsumingPayload()
    {
        byte[] expected = [10, 11, 12, 13];
        byte[] data = BuildUnmaskedFrame(WebSocketOpcode.Binary, expected);
        using var reader = new FrameReader(new ChunkedReadStream(data, maxChunkSize: 3), Options());

        FrameHeader header = reader.ReadHeader();

        Assert.False(reader.TryReadPayloadAsMemory(header, out _));
        Assert.Equal(expected, ReadPayload(reader, header));
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenFrameIsMasked_ReturnsFalseAndFallbackUnmasksPayload()
    {
        byte[] expected = [21, 22, 23, 24, 25];
        byte[] data = BuildMaskedFrame(WebSocketOpcode.Binary, expected, maskKey: [1, 2, 3, 4]);
        using var reader = new FrameReader(new MemoryStream(data), Options(rejectMaskedServerFrames: false));

        FrameHeader header = reader.ReadHeader();

        Assert.False(reader.TryReadPayloadAsMemory(header, out _));
        Assert.Equal(expected, ReadPayload(reader, header));
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenPayloadIsEmpty_ReturnsEmptyAndKeepsNextFrameAligned()
    {
        byte[] nextPayload = [31, 32];
        byte[] data = Concat(
            BuildUnmaskedFrame(WebSocketOpcode.Text, ReadOnlySpan<byte>.Empty),
            BuildUnmaskedFrame(WebSocketOpcode.Binary, nextPayload));

        using var reader = new FrameReader(new MemoryStream(data), Options());

        FrameHeader empty = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(empty, out ReadOnlyMemory<byte> payload));
        Assert.True(payload.IsEmpty);

        FrameHeader next = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Binary, next.Opcode);
        Assert.Equal(nextPayload, ReadPayload(reader, next));
    }

    private static WebSocketClientOptions Options(bool rejectMaskedServerFrames = true) => new()
    {
        ReceiveScratchBufferSize = 64,
        MaxMessageBytes = 1024,
        RejectMaskedServerFrames = rejectMaskedServerFrames,
    };

    private static byte[] ReadPayload(FrameReader reader, FrameHeader header)
    {
        using var assembler = new MessageAssembler(initialCapacity: 16, maxMessageBytes: 1024);
        reader.ReadPayloadInto(header, assembler);
        return assembler.WrittenMemory.ToArray();
    }

    private static byte[] BuildUnmaskedFrame(WebSocketOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] frame = new byte[2 + payload.Length];
        frame[0] = (byte)(0b1000_0000 | ((byte)opcode & 0x0F));
        frame[1] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(2));
        return frame;
    }

    private static byte[] BuildMaskedFrame(WebSocketOpcode opcode, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> maskKey)
    {
        if (payload.Length > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        if (maskKey.Length != 4)
        {
            throw new ArgumentException("Mask key must be 4 bytes.", nameof(maskKey));
        }

        byte[] frame = new byte[6 + payload.Length];
        frame[0] = (byte)(0b1000_0000 | ((byte)opcode & 0x0F));
        frame[1] = (byte)(0b1000_0000 | payload.Length);
        maskKey.CopyTo(frame.AsSpan(2, 4));

        for (int i = 0; i < payload.Length; i++)
        {
            frame[6 + i] = (byte)(payload[i] ^ maskKey[i & 3]);
        }

        return frame;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private sealed class ChunkedReadStream(byte[] data, int maxChunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length)
            {
                return 0;
            }

            int n = Math.Min(Math.Min(buffer.Length, maxChunkSize), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
