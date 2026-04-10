using System.Text;
using BenchmarkDotNet.Attributes;
using DuLowAllocWebSocket.Benchmarks.Helpers;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>수신 파이프라인 E2E 벤치마크 (header → payload → inflate/assemble).</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class ReceivePipelineBenchmarks
{
    private LoopingMemoryStream _stream = null!;
    private FrameReader _reader = null!;
    private MessageAssembler _assembler = null!;
    private DeflateInflater? _inflater;

    [Params(1024, 16384)]
    public int PayloadSize;

    [Params(false, true)]
    public bool Compressed;

    [GlobalSetup]
    public void Setup()
    {
        // JSON 형태의 페이로드 생성
        var sb = new StringBuilder(PayloadSize + 256);
        while (sb.Length < PayloadSize)
        {
            sb.Append("""{"s":"BTCUSDT","b":"67890.12","B":"1.234","a":"67890.56","A":"0.567"}""");
            sb.Append('\n');
        }

        var payload = Encoding.UTF8.GetBytes(sb.ToString(0, Math.Min(sb.Length, PayloadSize)));

        byte[] frameBytes;
        if (Compressed)
        {
            if (!DeflateInflater.IsSupported)
                throw new InvalidOperationException("zlib not available.");
            frameBytes = FrameBuilder.BuildCompressedFrame(payload);
            // 동일 압축 데이터 반복 공급 시 context takeover off로 매 메시지 독립 inflate
            _inflater = new DeflateInflater(noContextTakeover: true, initialOutputSize: PayloadSize * 2);
        }
        else
        {
            frameBytes = FrameBuilder.BuildUnmaskedTextFrame(payload);
            _inflater = null;
        }

        _stream = new LoopingMemoryStream(frameBytes);

        var options = new WebSocketClientOptions
        {
            ReceiveScratchBufferSize = 256 * 1024,
            MaxMessageBytes = 4 * 1024 * 1024,
        };
        _reader = new FrameReader(_stream, options);
        _assembler = new MessageAssembler(PayloadSize * 2);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        _assembler.Dispose();
        _inflater?.Dispose();
    }

    [Benchmark]
    public int ReceiveMessage()
    {
        _assembler.Reset();
        var header = _reader.ReadHeader();

        if (Compressed && _inflater is not null)
        {
            _inflater.BeginMessage();
            _reader.ReadPayloadInto(header, _inflater);
            var result = _inflater.FinishMessage();
            return result.Length;
        }
        else
        {
            _reader.ReadPayloadInto(header, _assembler);
            return _assembler.Length;
        }
    }
}
