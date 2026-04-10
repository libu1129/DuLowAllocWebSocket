using BenchmarkDotNet.Attributes;
using DuLowAllocWebSocket.Benchmarks.Helpers;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>FrameReader.ReadHeader / ReadPayloadInto 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class FrameReaderBenchmarks
{
    private LoopingMemoryStream _stream = null!;
    private FrameReader _reader = null!;
    private NullPayloadSink _nullSink = null!;
    private MessageAssembler _assembler = null!;
    private byte[] _frameBytes = null!;

    [Params(64, 1024, 16384, 65536)]
    public int PayloadSize;

    [Params(false, true)]
    public bool Masked;

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);

        _frameBytes = Masked
            ? FrameBuilder.BuildMaskedTextFrame(payload, 0xCAFEBABE)
            : FrameBuilder.BuildUnmaskedTextFrame(payload);

        _stream = new LoopingMemoryStream(_frameBytes);

        var options = new WebSocketClientOptions
        {
            ReceiveScratchBufferSize = 256 * 1024,
            RejectMaskedServerFrames = false,
            MaxMessageBytes = 4 * 1024 * 1024,
        };
        _reader = new FrameReader(_stream, options);
        _nullSink = new NullPayloadSink();
        _assembler = new MessageAssembler(Math.Max(PayloadSize * 2, 16 * 1024));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        _assembler.Dispose();
    }

    [Benchmark]
    public FrameHeader ReadHeader()
    {
        return _reader.ReadHeader();
    }

    [Benchmark]
    public void ReadPayloadInto_NullSink()
    {
        var header = _reader.ReadHeader();
        _reader.ReadPayloadInto(header, _nullSink);
    }

    [Benchmark]
    public void ReadPayloadInto_Assembler()
    {
        _assembler.Reset();
        var header = _reader.ReadHeader();
        _reader.ReadPayloadInto(header, _assembler);
    }
}
