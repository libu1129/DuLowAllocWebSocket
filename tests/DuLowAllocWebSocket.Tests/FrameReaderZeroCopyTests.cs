using System.Buffers;
using System.Buffers.Binary;
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
    public void TryReadPayloadAsMemory_WhenPayloadIsPartiallyBuffered_FillsScratchAndKeepsNextFrameAligned()
    {
        byte[] expected = [10, 11, 12, 13];
        byte[] nextExpected = [14, 15, 16];
        byte[] data = Concat(
            BuildUnmaskedFrame(WebSocketOpcode.Binary, expected),
            BuildUnmaskedFrame(WebSocketOpcode.Text, nextExpected));
        using var reader = new FrameReader(new ChunkedReadStream(data, maxChunkSize: 5), Options());

        FrameHeader header = reader.ReadHeader();

        Assert.True(reader.TryReadPayloadAsMemory(header, out ReadOnlyMemory<byte> payload));
        Assert.Equal(expected, payload.ToArray());

        FrameHeader next = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Text, next.Opcode);
        Assert.True(reader.TryReadPayloadAsMemory(next, out ReadOnlyMemory<byte> nextPayload));
        Assert.Equal(nextExpected, nextPayload.ToArray());
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenPartialPayloadDoesNotFitTail_CompactsAndKeepsNextFrameAligned()
    {
        byte[] firstExpected = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] secondExpected = [11, 12, 13, 14, 15, 16, 17, 18];
        byte[] thirdExpected = [21, 22, 23];
        byte[] data = Concat(
            BuildUnmaskedFrame(WebSocketOpcode.Binary, firstExpected),
            BuildUnmaskedFrame(WebSocketOpcode.Text, secondExpected),
            BuildUnmaskedFrame(WebSocketOpcode.Binary, thirdExpected));
        using var reader = new FrameReader(new MemoryStream(data), Options(scratchSize: 16));

        FrameHeader first = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(first, out ReadOnlyMemory<byte> firstPayload));
        Assert.Equal(firstExpected, firstPayload.ToArray());

        FrameHeader second = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(second, out ReadOnlyMemory<byte> secondPayload));
        Assert.Equal(secondExpected, secondPayload.ToArray());

        FrameHeader third = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(third, out ReadOnlyMemory<byte> thirdPayload));
        Assert.Equal(thirdExpected, thirdPayload.ToArray());
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenPayloadExceedsScratch_ReturnsFalseWithoutConsumingPayload()
    {
        byte[] expected = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        byte[] data = BuildUnmaskedFrame(WebSocketOpcode.Binary, expected);
        using var reader = new FrameReader(new MemoryStream(data), Options(scratchSize: 16));

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

    [Fact]
    public void Constructor_WhenInitialBufferedBytesPresent_ReadsThemBeforeTransport()
    {
        byte[] initialPayload = [41, 42, 43];
        byte[] streamPayload = [51, 52];
        byte[] initialFrame = BuildUnmaskedFrame(WebSocketOpcode.Text, initialPayload);
        byte[] streamFrame = BuildUnmaskedFrame(WebSocketOpcode.Binary, streamPayload);

        using var reader = new FrameReader(new MemoryStream(streamFrame), Options(), initialFrame);

        FrameHeader initial = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Text, initial.Opcode);
        Assert.Equal(initialPayload, ReadPayload(reader, initial));

        FrameHeader fromStream = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Binary, fromStream.Opcode);
        Assert.Equal(streamPayload, ReadPayload(reader, fromStream));
    }

    [Fact]
    public void Constructor_WithLargeConfiguredMaximum_RentsOnlyHandshakeSizedScratch()
    {
        var pool = new TrackingByteArrayPool();
        var options = Options(scratchSize: 256 * 1024, handshakeSize: 16 * 1024, maxMessageBytes: 512 * 1024);

        var reader = new FrameReader(new MemoryStream(), options, ReadOnlySpan<byte>.Empty, pool);

        Assert.Equal([16 * 1024], pool.RentRequests);
        Assert.Equal(16 * 1024, reader.DiagScratchCapacity);
        Assert.Empty(pool.Returned);

        reader.Dispose();
        Assert.Single(pool.Returned);
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenLargeFrameArrives_GrowsOnceAndKeepsNextFrameAligned()
    {
        byte[] expected = Enumerable.Range(0, 40 * 1024).Select(static value => (byte)value).ToArray();
        byte[] nextExpected = [91, 92, 93];
        byte[] data = Concat(
            BuildUnmaskedFrame(WebSocketOpcode.Binary, expected),
            BuildUnmaskedFrame(WebSocketOpcode.Text, nextExpected));
        var pool = new TrackingByteArrayPool();
        var options = Options(scratchSize: 64 * 1024, handshakeSize: 16 * 1024, maxMessageBytes: 128 * 1024);
        var reader = new FrameReader(new MemoryStream(data), options, ReadOnlySpan<byte>.Empty, pool);

        FrameHeader header = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(header, out ReadOnlyMemory<byte> payload));
        Assert.Equal(expected, payload.ToArray());
        Assert.Equal([16 * 1024, 40 * 1024], pool.RentRequests);
        Assert.Single(pool.Returned);
        Assert.Equal(40 * 1024, reader.DiagScratchCapacity);

        FrameHeader next = reader.ReadHeader();
        Assert.Equal(nextExpected, ReadPayload(reader, next));
        Assert.Equal([16 * 1024, 40 * 1024, 64 * 1024], pool.RentRequests);

        reader.Dispose();
        Assert.Equal(3, pool.Returned.Count);
        Assert.Equal(3, pool.UniqueReturnCount);
    }

    [Fact]
    public void TryReadPayloadAsMemory_WhenFrameExceedsConfiguredMaximum_DoesNotGrowBeyondMaximumAndFallbackConsumesIt()
    {
        byte[] expected = Enumerable.Range(0, 32 * 1024).Select(static value => (byte)value).ToArray();
        var pool = new TrackingByteArrayPool();
        var options = Options(scratchSize: 16 * 1024, handshakeSize: 4 * 1024, maxMessageBytes: 64 * 1024);
        var reader = new FrameReader(
            new MemoryStream(BuildUnmaskedFrame(WebSocketOpcode.Binary, expected)),
            options,
            ReadOnlySpan<byte>.Empty,
            pool);

        FrameHeader header = reader.ReadHeader();
        Assert.False(reader.TryReadPayloadAsMemory(header, out _));
        Assert.Equal(expected, ReadPayload(reader, header));
        Assert.Equal([4 * 1024, 8 * 1024, 16 * 1024], pool.RentRequests);

        reader.Dispose();
        Assert.Equal(3, pool.Returned.Count);
    }

    [Fact]
    public void OwnedHandshakeBuffer_IsReusedWithoutRent_AndReturnedExactlyOnce()
    {
        byte[] initialPayload = [41, 42, 43];
        byte[] nextPayload = [51, 52];
        byte[] initialFrame = BuildUnmaskedFrame(WebSocketOpcode.Text, initialPayload);
        byte[] owned = new byte[16 * 1024];
        const int initialOffset = 257;
        initialFrame.CopyTo(owned.AsSpan(initialOffset));
        var pool = new TrackingByteArrayPool();
        var reader = new FrameReader(
            new MemoryStream(BuildUnmaskedFrame(WebSocketOpcode.Binary, nextPayload)),
            Options(scratchSize: 256 * 1024, handshakeSize: 16 * 1024),
            owned,
            initialOffset,
            initialFrame.Length,
            pool);

        Assert.Empty(pool.RentRequests);
        FrameHeader initial = reader.ReadHeader();
        Assert.Equal(initialPayload, ReadPayload(reader, initial));
        FrameHeader next = reader.ReadHeader();
        Assert.Equal(nextPayload, ReadPayload(reader, next));

        reader.Dispose();
        reader.Dispose();
        Assert.Single(pool.Returned);
        Assert.Same(owned, pool.Returned[0]);
    }

    [Fact]
    public void OwnedHandshakeBuffer_WhenConfiguredMaximumIsSmaller_ReplacesOversizedLease()
    {
        byte[] payload = [61, 62, 63];
        byte[] initialFrame = BuildUnmaskedFrame(WebSocketOpcode.Text, payload);
        byte[] owned = new byte[16 * 1024];
        const int initialOffset = 257;
        initialFrame.CopyTo(owned.AsSpan(initialOffset));
        var pool = new TrackingByteArrayPool();
        var reader = new FrameReader(
            new MemoryStream(),
            Options(scratchSize: 64, handshakeSize: 16 * 1024),
            owned,
            initialOffset,
            initialFrame.Length,
            pool);

        Assert.Equal([64], pool.RentRequests);
        Assert.Single(pool.Returned);
        Assert.Same(owned, pool.Returned[0]);
        Assert.Equal(64, reader.DiagScratchCapacity);
        FrameHeader header = reader.ReadHeader();
        Assert.Equal(payload, ReadPayload(reader, header));

        reader.Dispose();
        Assert.Equal(2, pool.Returned.Count);
        Assert.Equal(2, pool.UniqueReturnCount);
    }

    [Fact]
    public void OwnedHandshakeBuffer_WhenLargeFrameIsPartial_GrowsAndPreservesNextFrameAlignment()
    {
        byte[] payload = Enumerable.Range(0, 40 * 1024).Select(static value => (byte)value).ToArray();
        byte[] nextPayload = [71, 72, 73];
        byte[] frame = BuildUnmaskedFrame(WebSocketOpcode.Binary, payload);
        const int initialFrameBytes = 2 * 1024;
        const int initialOffset = 257;
        byte[] owned = new byte[16 * 1024];
        frame.AsSpan(0, initialFrameBytes).CopyTo(owned.AsSpan(initialOffset));
        byte[] transportBytes = Concat(
            frame.AsSpan(initialFrameBytes).ToArray(),
            BuildUnmaskedFrame(WebSocketOpcode.Text, nextPayload));
        var pool = new TrackingByteArrayPool();
        var reader = new FrameReader(
            new MemoryStream(transportBytes),
            Options(scratchSize: 64 * 1024, handshakeSize: 16 * 1024, maxMessageBytes: 128 * 1024),
            owned,
            initialOffset,
            initialFrameBytes,
            pool);

        FrameHeader header = reader.ReadHeader();
        Assert.True(reader.TryReadPayloadAsMemory(header, out ReadOnlyMemory<byte> actual));
        Assert.Equal(payload, actual.ToArray());
        FrameHeader next = reader.ReadHeader();
        Assert.Equal(nextPayload, ReadPayload(reader, next));

        Assert.Equal([40 * 1024, 64 * 1024], pool.RentRequests);
        Assert.Equal(2, pool.Returned.Count);
        Assert.Same(owned, pool.Returned[0]);
        reader.Dispose();
        Assert.Equal(3, pool.Returned.Count);
        Assert.Equal(3, pool.UniqueReturnCount);
    }

    [Fact]
    public void SustainedSmallFrameBacklog_GrowsReadAheadToConfiguredMaximum()
    {
        byte[] payload = Enumerable.Range(0, 100).Select(static value => (byte)value).ToArray();
        byte[][] frames = Enumerable.Range(0, 6_000)
            .Select(_ => BuildUnmaskedFrame(WebSocketOpcode.Binary, payload))
            .ToArray();
        var pool = new TrackingByteArrayPool();
        var reader = new FrameReader(
            new MemoryStream(Concat(frames)),
            Options(scratchSize: 256 * 1024, handshakeSize: 16 * 1024, maxMessageBytes: 1024),
            ReadOnlySpan<byte>.Empty,
            pool);

        for (int i = 0; i < frames.Length; i++)
        {
            FrameHeader header = reader.ReadHeader();
            Assert.True(reader.TryReadPayloadAsMemory(header, out ReadOnlyMemory<byte> actual));
            Assert.True(actual.Span.SequenceEqual(payload));
        }

        Assert.Equal([16 * 1024, 32 * 1024, 64 * 1024, 128 * 1024, 256 * 1024], pool.RentRequests);
        Assert.Equal(256 * 1024, reader.DiagScratchCapacity);
        Assert.Equal(4, pool.Returned.Count);

        reader.Dispose();
        Assert.Equal(5, pool.Returned.Count);
    }

    private static WebSocketClientOptions Options(
        bool rejectMaskedServerFrames = true,
        int scratchSize = 64,
        int handshakeSize = 16 * 1024,
        int maxMessageBytes = 1024) => new()
    {
        ReceiveScratchBufferSize = scratchSize,
        HandshakeBufferSize = handshakeSize,
        MaxMessageBytes = maxMessageBytes,
        RejectMaskedServerFrames = rejectMaskedServerFrames,
    };

    private static byte[] ReadPayload(FrameReader reader, FrameHeader header)
    {
        using var assembler = new MessageAssembler(
            initialCapacity: Math.Min(16, Math.Max(1, header.PayloadLength)),
            maxMessageBytes: Math.Max(1024, header.PayloadLength));
        reader.ReadPayloadInto(header, assembler);
        return assembler.WrittenMemory.ToArray();
    }

    private static byte[] BuildUnmaskedFrame(WebSocketOpcode opcode, ReadOnlySpan<byte> payload)
    {
        int headerLength;
        if (payload.Length <= 125)
        {
            headerLength = 2;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            headerLength = 4;
        }
        else
        {
            headerLength = 10;
        }

        byte[] frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0b1000_0000 | ((byte)opcode & 0x0F));
        if (headerLength == 2)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (headerLength == 4)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
        }

        payload.CopyTo(frame.AsSpan(headerLength));
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

    private sealed class TrackingByteArrayPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _returnedSet = new(ReferenceEqualityComparer.Instance);

        public List<int> RentRequests { get; } = [];

        public List<byte[]> Returned { get; } = [];

        public int UniqueReturnCount => _returnedSet.Count;

        public override byte[] Rent(int minimumLength)
        {
            RentRequests.Add(minimumLength);
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!_returnedSet.Add(array))
            {
                throw new InvalidOperationException("Buffer returned more than once.");
            }

            Returned.Add(array);
        }
    }
}
