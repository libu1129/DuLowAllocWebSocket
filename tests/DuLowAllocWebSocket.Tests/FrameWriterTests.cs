using System.Buffers;
using System.Buffers.Binary;

using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class FrameWriterTests
{
    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    [InlineData(64 * 1024, 64 * 1024)]
    public void NormalizeScratchBufferSize_AlwaysLeavesPayloadProgress(
        int configuredSize,
        int expectedSize)
    {
        Assert.Equal(expectedSize, FrameWriter.NormalizeScratchBufferSize(configuredSize));
    }

    [Fact]
    public void ConstructorAndDisposeWithoutSend_RentNothing()
    {
        var pool = new TrackingByteArrayPool();
        using (var writer = CreateWriter(new RecordingWriteStream(), pool))
        {
            Assert.Equal(0, pool.RentCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.Equal(0, pool.OutstandingCount);
        }

        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
    }

    [Fact]
    public async Task EmptyPayload_SendsHeaderWithoutRent_ForSyncAndAsync()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new RecordingWriteStream();
        using var writer = CreateWriter(stream, pool);

        writer.SendSync(ReadOnlySpan<byte>.Empty, WebSocketOpcode.Ping, fin: true);
        await writer.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketOpcode.Pong, fin: true, CancellationToken.None);

        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(2, stream.SyncWriteCount);
        Assert.Equal(0, stream.AsyncWriteCount);
        Assert.Equal(2, stream.Writes.Length);
        AssertFrame(stream.Writes[0], ReadOnlySpan<byte>.Empty, WebSocketOpcode.Ping);
        AssertFrame(stream.Writes[1], ReadOnlySpan<byte>.Empty, WebSocketOpcode.Pong);
    }

    [Theory]
    [InlineData(1, 7, 1)]
    [InlineData(4 * 1024, (4 * 1024) + 8, 1)]
    [InlineData(64 * 1024, 64 * 1024, 2)]
    [InlineData(128 * 1024, 64 * 1024, 3)]
    public void SendSync_AdaptiveRentPreservesMaskingAndWriteChunks(
        int payloadLength,
        int expectedRentLength,
        int expectedWrites)
    {
        byte[] payload = CreatePayload(payloadLength);
        var pool = new TrackingByteArrayPool();
        var stream = new RecordingWriteStream();
        using var writer = CreateWriter(stream, pool);

        writer.SendSync(payload, WebSocketOpcode.Binary, fin: true);

        Assert.Equal([expectedRentLength], pool.RequestedLengths);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(expectedWrites, stream.Writes.Length);
        Assert.All(stream.Writes, static write => Assert.InRange(write.Length, 1, 64 * 1024));
        AssertFrame(stream.CombineWrites(), payload, WebSocketOpcode.Binary);
    }

    [Fact]
    public async Task SendAsync_SmallPayloadRentsForPayloadAndHeader_AndWritesOnce()
    {
        byte[] payload = CreatePayload(4 * 1024);
        var pool = new TrackingByteArrayPool();
        var stream = new RecordingWriteStream();
        using var writer = CreateWriter(stream, pool);

        await writer.SendAsync(payload, WebSocketOpcode.Text, fin: true, CancellationToken.None);

        Assert.Equal([(4 * 1024) + 8], pool.RequestedLengths);
        Assert.Equal(0, stream.SyncWriteCount);
        Assert.Equal(1, stream.AsyncWriteCount);
        AssertFrame(stream.CombineWrites(), payload, WebSocketOpcode.Text);
    }

    [Fact]
    public void IncreasingPayload_GrowsToConfiguredMaximum_AndReturnsEveryLeaseExactlyOnce()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new RecordingWriteStream();
        var writer = CreateWriter(stream, pool);

        writer.SendSync(CreatePayload(1), WebSocketOpcode.Binary, fin: true);
        Assert.Equal([7], pool.RequestedLengths);
        Assert.Equal(1, pool.OutstandingCount);

        writer.SendSync(CreatePayload(4 * 1024), WebSocketOpcode.Binary, fin: true);
        Assert.Equal([7, (4 * 1024) + 8], pool.RequestedLengths);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(1, pool.OutstandingCount);

        // Smaller frames reuse the current adaptive lease.
        writer.SendSync(CreatePayload(2 * 1024), WebSocketOpcode.Binary, fin: true);
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);

        writer.SendSync(CreatePayload(64 * 1024), WebSocketOpcode.Binary, fin: true);
        Assert.Equal([7, (4 * 1024) + 8, 64 * 1024], pool.RequestedLengths);
        Assert.Equal(3, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(1, pool.OutstandingCount);

        writer.Dispose();
        writer.Dispose();
        Assert.Equal(3, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void OversizedPoolBucket_DoesNotExceedConfiguredChunkSize()
    {
        const int configuredSize = 4 * 1024;
        byte[] payload = CreatePayload(10 * 1024);
        var pool = new TrackingByteArrayPool(extraBytesPerRent: 128 * 1024);
        var stream = new RecordingWriteStream();
        using var writer = CreateWriter(stream, pool, configuredSize);

        writer.SendSync(payload, WebSocketOpcode.Binary, fin: true);

        Assert.Equal([configuredSize], pool.RequestedLengths);
        Assert.Equal(3, stream.Writes.Length);
        Assert.All(stream.Writes, write => Assert.InRange(write.Length, 1, configuredSize));
        Assert.Equal(configuredSize, stream.Writes[0].Length);
        Assert.Equal(configuredSize, stream.Writes[1].Length);
        AssertFrame(stream.CombineWrites(), payload, WebSocketOpcode.Binary);
    }

    [Fact]
    public async Task DisposeDuringAsyncWrite_DefersReturnUntilSendCompletes()
    {
        byte[] payload = CreatePayload(4 * 1024);
        var pool = new TrackingByteArrayPool();
        var stream = new BlockingAsyncWriteStream();
        var writer = CreateWriter(stream, pool);

        Task send = writer.SendAsync(
            payload, WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask();
        await stream.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        writer.Dispose();
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(1, pool.OutstandingCount);

        stream.ReleaseWrite();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        writer.Dispose();
        Assert.Equal(1, pool.ReturnCount);
        AssertFrame(stream.CombineWrites(), payload, WebSocketOpcode.Binary);
    }

    [Fact]
    public async Task DisposeDuringFirstRent_ReturnsNewLeaseAfterInFlightSend()
    {
        byte[] payload = CreatePayload(4 * 1024);
        var pool = new TrackingByteArrayPool(blockFirstRent: true);
        var stream = new RecordingWriteStream();
        var writer = CreateWriter(stream, pool);

        Task send = Task.Run(
            () => writer.SendSync(payload, WebSocketOpcode.Binary, fin: true));
        await pool.FirstRentEntered.WaitAsync(TimeSpan.FromSeconds(5));

        writer.Dispose();
        Assert.Equal(0, pool.ReturnCount);

        pool.ReleaseFirstRent();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        AssertFrame(stream.CombineWrites(), payload, WebSocketOpcode.Binary);
    }

    [Fact]
    public async Task CompletedSendHandoffThenDispose_ReturnsScratchAfterNewSendCompletes()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new TwoWriteBlockingAsyncStream();
        var writer = CreateWriter(stream, pool);

        Task first = writer.SendAsync(
            CreatePayload(64), WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask();
        await stream.FirstWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
        stream.ReleaseFirstWrite();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Task second = writer.SendAsync(
            CreatePayload(64), WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask();
        await stream.SecondWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        writer.Dispose();
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(1, pool.OutstandingCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => writer.SendAsync(
                CreatePayload(64), WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask());

        stream.ReleaseSecondWrite();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        writer.Dispose();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task DisposeDuringFailedAsyncWrite_ReturnsScratchExactlyOnce()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new BlockingAsyncWriteStream(new IOException("write failed"));
        var writer = CreateWriter(stream, pool);

        Task send = writer.SendAsync(
            CreatePayload(64), WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask();
        await stream.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        writer.Dispose();
        Assert.Equal(0, pool.ReturnCount);
        stream.ReleaseWrite();
        await Assert.ThrowsAsync<IOException>(() => send);

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        writer.Dispose();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task DisposeDuringCanceledAsyncWrite_ReturnsScratchExactlyOnce()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new BlockingAsyncWriteStream();
        var writer = CreateWriter(stream, pool);
        using var cts = new CancellationTokenSource();

        Task send = writer.SendAsync(
            CreatePayload(64), WebSocketOpcode.Binary, fin: true, cts.Token).AsTask();
        await stream.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        writer.Dispose();
        Assert.Equal(0, pool.ReturnCount);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        writer.Dispose();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task ConcurrentDirectSend_IsRejected_ThenScratchIsReusableAcrossAsyncAndSync()
    {
        var pool = new TrackingByteArrayPool();
        var stream = new BlockingAsyncWriteStream();
        using var writer = CreateWriter(stream, pool);

        Task first = writer.SendAsync(
            CreatePayload(64), WebSocketOpcode.Binary, fin: true, CancellationToken.None).AsTask();
        await stream.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(
            () => writer.SendSync(CreatePayload(64), WebSocketOpcode.Binary, fin: true));

        stream.ReleaseWrite();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        writer.SendSync(CreatePayload(64), WebSocketOpcode.Binary, fin: true);

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, stream.AsyncWriteCount);
        Assert.Equal(1, stream.SyncWriteCount);
    }

    [Fact]
    public async Task SendAfterDispose_ThrowsWithoutRent()
    {
        var pool = new TrackingByteArrayPool();
        var writer = CreateWriter(new RecordingWriteStream(), pool);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => writer.SendSync("x"u8, WebSocketOpcode.Text, fin: true));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => writer.SendAsync(
                "x"u8.ToArray(), WebSocketOpcode.Text, fin: true, CancellationToken.None).AsTask());
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
    }

    private static FrameWriter CreateWriter(
        Stream stream,
        ArrayPool<byte> pool,
        int scratchSize = 64 * 1024)
        => new(
            stream,
            new WebSocketClientOptions { SendScratchBufferSize = scratchSize },
            pool);

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 31 + 17);
        return payload;
    }

    private static void AssertFrame(
        byte[] frame,
        ReadOnlySpan<byte> expectedPayload,
        WebSocketOpcode expectedOpcode)
    {
        Assert.True(frame.Length >= 6);
        Assert.True((frame[0] & 0x80) != 0);
        Assert.Equal((byte)expectedOpcode, (byte)(frame[0] & 0x0F));
        Assert.True((frame[1] & 0x80) != 0);

        int offset = 2;
        ulong payloadLength = (uint)(frame[1] & 0x7F);
        if (payloadLength == 126)
        {
            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset, 2));
            offset += 2;
        }
        else if (payloadLength == 127)
        {
            payloadLength = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(offset, 8));
            offset += 8;
        }

        Assert.Equal((ulong)expectedPayload.Length, payloadLength);
        ReadOnlySpan<byte> mask = frame.AsSpan(offset, 4);
        offset += 4;
        Assert.Equal(offset + expectedPayload.Length, frame.Length);
        for (int i = 0; i < expectedPayload.Length; i++)
            Assert.Equal(expectedPayload[i], (byte)(frame[offset + i] ^ mask[i & 3]));
    }

    private sealed class TrackingByteArrayPool : ArrayPool<byte>
    {
        private readonly object _gate = new();
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);
        private readonly List<int> _requestedLengths = [];
        private readonly int _extraBytesPerRent;
        private readonly bool _blockFirstRent;
        private readonly ManualResetEventSlim _firstRentRelease;
        private readonly TaskCompletionSource _firstRentEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rentCount;
        private int _returnCount;

        internal TrackingByteArrayPool(
            int extraBytesPerRent = 0,
            bool blockFirstRent = false)
        {
            _extraBytesPerRent = extraBytesPerRent;
            _blockFirstRent = blockFirstRent;
            _firstRentRelease = new ManualResetEventSlim(initialState: !blockFirstRent);
        }

        internal int RentCount => Volatile.Read(ref _rentCount);
        internal int ReturnCount => Volatile.Read(ref _returnCount);
        internal Task FirstRentEntered => _firstRentEntered.Task;

        internal int OutstandingCount
        {
            get
            {
                lock (_gate)
                    return _outstanding.Count;
            }
        }

        internal int[] RequestedLengths
        {
            get
            {
                lock (_gate)
                    return _requestedLengths.ToArray();
            }
        }

        internal void ReleaseFirstRent() => _firstRentRelease.Set();

        public override byte[] Rent(int minimumLength)
        {
            int rentNumber = Interlocked.Increment(ref _rentCount);
            lock (_gate)
                _requestedLengths.Add(minimumLength);

            if (_blockFirstRent && rentNumber == 1)
            {
                _firstRentEntered.TrySetResult();
                if (!_firstRentRelease.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The test did not release the first FrameWriter rent.");
            }

            byte[] array = new byte[checked(Math.Max(1, minimumLength) + _extraBytesPerRent)];
            lock (_gate)
            {
                if (!_outstanding.Add(array))
                    throw new InvalidOperationException("The same pool lease was issued twice.");
            }
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            lock (_gate)
            {
                if (!_outstanding.Remove(array))
                    throw new InvalidOperationException("An unknown or already returned lease was returned.");
            }

            if (clearArray)
                Array.Clear(array);
            Interlocked.Increment(ref _returnCount);
        }
    }

    private class RecordingWriteStream : Stream
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = [];
        private int _syncWriteCount;
        private int _asyncWriteCount;

        internal int SyncWriteCount => Volatile.Read(ref _syncWriteCount);
        internal int AsyncWriteCount => Volatile.Read(ref _asyncWriteCount);

        internal byte[][] Writes
        {
            get
            {
                lock (_gate)
                    return _writes.ToArray();
            }
        }

        internal byte[] CombineWrites()
        {
            byte[][] writes = Writes;
            int totalLength = 0;
            foreach (byte[] write in writes)
                totalLength = checked(totalLength + write.Length);

            var result = new byte[totalLength];
            int offset = 0;
            foreach (byte[] write in writes)
            {
                write.CopyTo(result, offset);
                offset += write.Length;
            }
            return result;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Increment(ref _syncWriteCount);
            Record(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordAsync(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected void RecordAsync(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Increment(ref _asyncWriteCount);
            Record(buffer);
        }

        protected void Record(ReadOnlySpan<byte> buffer)
        {
            byte[] copy = buffer.ToArray();
            lock (_gate)
                _writes.Add(copy);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingAsyncWriteStream : RecordingWriteStream
    {
        private readonly TaskCompletionSource _writeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? _failure;

        internal BlockingAsyncWriteStream(Exception? failure = null)
            => _failure = failure;

        internal Task WriteEntered => _writeEntered.Task;
        internal void ReleaseWrite() => _writeRelease.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeEntered.TrySetResult();
            await _writeRelease.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_failure is not null)
                throw _failure;
            RecordAsync(buffer.Span);
        }
    }

    private sealed class TwoWriteBlockingAsyncStream : RecordingWriteStream
    {
        private readonly TaskCompletionSource _firstWriteEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWriteRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWriteEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWriteRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        internal Task FirstWriteEntered => _firstWriteEntered.Task;
        internal Task SecondWriteEntered => _secondWriteEntered.Task;
        internal void ReleaseFirstWrite() => _firstWriteRelease.TrySetResult();
        internal void ReleaseSecondWrite() => _secondWriteRelease.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int writeNumber = Interlocked.Increment(ref _writeCount);
            if (writeNumber == 1)
            {
                _firstWriteEntered.TrySetResult();
                await _firstWriteRelease.Task.WaitAsync(cancellationToken);
            }
            else if (writeNumber == 2)
            {
                _secondWriteEntered.TrySetResult();
                await _secondWriteRelease.Task.WaitAsync(cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("The test expected exactly two async writes.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            RecordAsync(buffer.Span);
        }
    }
}
