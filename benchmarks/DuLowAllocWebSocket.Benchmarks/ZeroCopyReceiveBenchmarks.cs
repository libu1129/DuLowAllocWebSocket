using BenchmarkDotNet.Attributes;
using DuLowAllocWebSocket.Benchmarks.Helpers;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>비압축·비분할 수신 메시지의 복사 조립과 zero-copy fast path 비교.</summary>
[Config(typeof(DefaultConfig))]
public class ZeroCopyReceiveBenchmarks
{
    private LoopingMemoryStream _assembleStream = null!;
    private LoopingMemoryStream _zeroCopyStream = null!;
    private FrameReader _assembleReader = null!;
    private FrameReader _zeroCopyReader = null!;
    private MessageAssembler _assembler = null!;
    private MessageAssembler _fallbackAssembler = null!;

    [Params(64, 1024, 16384, 65536)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
        var frameBytes = FrameBuilder.BuildUnmaskedTextFrame(payload);

        var options = new WebSocketClientOptions
        {
            ReceiveScratchBufferSize = 256 * 1024,
            MaxMessageBytes = 4 * 1024 * 1024,
        };

        _assembleStream = new LoopingMemoryStream(frameBytes);
        _zeroCopyStream = new LoopingMemoryStream(frameBytes);
        _assembleReader = new FrameReader(_assembleStream, options);
        _zeroCopyReader = new FrameReader(_zeroCopyStream, options);
        _assembler = new MessageAssembler(Math.Max(PayloadSize * 2, 16 * 1024));
        _fallbackAssembler = new MessageAssembler(Math.Max(PayloadSize * 2, 16 * 1024));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _assembleReader.Dispose();
        _zeroCopyReader.Dispose();
        _assembler.Dispose();
        _fallbackAssembler.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int AssembleCopy()
    {
        _assembler.Reset();
        var header = _assembleReader.ReadHeader();
        _assembleReader.ReadPayloadInto(header, _assembler);
        return _assembler.Length;
    }

    [Benchmark]
    public int ZeroCopyFastPath()
    {
        var header = _zeroCopyReader.ReadHeader();
        if (_zeroCopyReader.TryReadPayloadAsMemory(header, out var payload))
        {
            return payload.Length;
        }

        _fallbackAssembler.Reset();
        _zeroCopyReader.ReadPayloadInto(header, _fallbackAssembler);
        return _fallbackAssembler.Length;
    }
}
