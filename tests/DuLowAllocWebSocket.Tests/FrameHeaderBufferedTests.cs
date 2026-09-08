using System.Buffers.Binary;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class FrameHeaderBufferedTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1024)]
    public void BufferedAndPartialFields_PreserveHeaderBoundaries(int readLimit)
    {
        var expected = new List<FrameHeader>();
        using var wire = new MemoryStream();
        foreach (int size in new[] { 0, 64, 125, 126, 255, 256, 1024, 65535, 65536 })
        foreach (bool masked in new[] { false, true })
        foreach (byte flags in new byte[] { 0x01, 0x82, 0xC1, 0x89 })
        {
            byte[] header = Header(size, masked, flags);
            wire.Write(header);
            expected.Add(new((flags & 0x80) != 0, (flags & 0x40) != 0, (WebSocketOpcode)(flags & 15), masked,
                size, masked ? 0xCAFEBABE : 0, flags, header[1]));
        }
        // Header-only wire intentionally tests successive ReadHeader calls without payload reads.
        byte[] bytes = wire.ToArray();
        foreach (int initialCount in new[] { 0, 1, 3, bytes.Length })
        {
            using var stream = new LimitedReadStream(bytes[initialCount..], readLimit);
            using var reader = new FrameReader(stream, new() { RejectMaskedServerFrames = false, ReceiveScratchBufferSize = 31 }, bytes.AsSpan(0, initialCount));
            foreach (var header in expected) Assert.Equal(header, reader.ReadHeader());
            Assert.Throws<WebSocketProtocolException>(() => reader.ReadHeader());
        }
    }

    [Theory]
    [InlineData(64, 2)]
    [InlineData(1024, 4)]
    [InlineData(65536, 10)]
    public void RejectedBufferedHeader_ConsumesLengthBeforePolicyAndNoMask(int size, int consumed)
    {
        byte[] header = Header(size, true, 0x82);
        using var maskedReader = new FrameReader(Stream.Null, new() { RejectMaskedServerFrames = true }, header);
        Assert.Throws<WebSocketProtocolException>(() => maskedReader.ReadHeader());
        Assert.Equal(consumed, maskedReader.DiagBufferOffset);
        using var oversizedReader = new FrameReader(Stream.Null, new() { RejectMaskedServerFrames = false, MaxMessageBytes = size - 1 }, header);
        Assert.Throws<WebSocketProtocolException>(() => oversizedReader.ReadHeader());
        Assert.Equal(consumed, oversizedReader.DiagBufferOffset);
    }

    private static byte[] Header(int size, bool masked, byte flags)
    {
        int prefix = size < 126 ? 2 : size <= 65535 ? 4 : 10;
        byte[] result = new byte[prefix + (masked ? 4 : 0)];
        result[0] = flags;
        result[1] = (byte)((masked ? 128 : 0) | (size < 126 ? size : size <= 65535 ? 126 : 127));
        if (prefix == 4) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)size);
        if (prefix == 10) BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(2), (ulong)size);
        if (masked) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(prefix), 0xCAFEBABE);
        return result;
    }

    private sealed class LimitedReadStream(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(limit, buffer.Length)]);
    }
}
