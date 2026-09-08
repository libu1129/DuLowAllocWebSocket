using BenchmarkDotNet.Attributes;
using DuLowAllocWebSocket.Benchmarks.Helpers;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>FrameReader.ReadHeader / ReadPayloadInto 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class FrameReaderBenchmarks
{
    private LoopingMemoryStream _stream = null!;
    private FrameReader _reader = null!;
    private FrameReader _headerReader = null!;
    private LoopingMemoryStream _headerStream = null!;
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
        // 헤더 전용 스트림은 선언된 payload를 싣지 않는다. 다음 호출도 반드시 헤더 경계에서 시작한다.
        var headerSize = _frameBytes.Length - PayloadSize;
        _headerStream = new LoopingMemoryStream(_frameBytes.AsSpan(0, headerSize).ToArray());
        _headerReader = new FrameReader(_headerStream, options);
        for (var i = 0; i < 4; i++)
        {
            var header = ReadHeader();
            if (!header.Fin || header.PayloadLength != PayloadSize || header.Masked != Masked)
                throw new InvalidOperationException("Header benchmark fixture lost its frame boundary.");
        }
        _nullSink = new NullPayloadSink();
        _assembler = new MessageAssembler(Math.Max(PayloadSize * 2, 16 * 1024));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        _headerReader.Dispose();
        _headerStream.Dispose();
        _stream.Dispose();
        _assembler.Dispose();
    }

    [Benchmark]
    public FrameHeader ReadHeader()
    {
        return _headerReader.ReadHeader();
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
