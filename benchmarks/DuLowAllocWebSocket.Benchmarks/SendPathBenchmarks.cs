using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>송신 경로의 async/sync 직렬화 비용 비교.</summary>
[Config(typeof(DefaultConfig))]
public class SendPathBenchmarks
{
    private byte[] _payload = null!;
    private FrameWriter _writer = null!;
    private SemaphoreSlim _sendLock = null!;

    [Params(0, 16, 256, 4096)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[Math.Max(PayloadSize, 1)];
        Random.Shared.NextBytes(_payload);
        _writer = new FrameWriter(new NullWriteStream(), new WebSocketClientOptions());
        _sendLock = new SemaphoreSlim(1, 1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        _sendLock.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void SendAsyncWithSemaphore()
    {
        SendAsyncWithSemaphoreCore().GetAwaiter().GetResult();
    }

    [Benchmark]
    public void SendSyncWithSemaphore()
    {
        _sendLock.Wait();
        try
        {
            _writer.SendSync(_payload.AsSpan(0, PayloadSize), WebSocketOpcode.Text, fin: true);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    [Benchmark]
    public void FrameWriterSendAsyncOnly()
    {
        _writer.SendAsync(_payload.AsMemory(0, PayloadSize), WebSocketOpcode.Text, fin: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public void FrameWriterSendSyncOnly()
    {
        _writer.SendSync(_payload.AsSpan(0, PayloadSize), WebSocketOpcode.Text, fin: true);
    }

    private async ValueTask SendAsyncWithSemaphoreCore()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.SendAsync(_payload.AsMemory(0, PayloadSize), WebSocketOpcode.Text, fin: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private sealed class NullWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
