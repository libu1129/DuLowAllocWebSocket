using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>DeflateInflater 처리량 벤치마크.</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class DeflateInflaterBenchmarks
{
    private DeflateInflater _inflater = null!;
    private byte[] _compressed = null!;

    [Params(256, 4096, 65536)]
    public int OriginalSize;

    [GlobalSetup]
    public void Setup()
    {
        if (!DeflateInflater.IsSupported)
            throw new InvalidOperationException("zlib not available — cannot benchmark DeflateInflater.");

        // Binance bookTicker 형태의 반복 JSON 생성
        var sb = new StringBuilder(OriginalSize + 256);
        while (sb.Length < OriginalSize)
        {
            sb.Append("""{"stream":"btcusdt@bookTicker","data":{"u":12345678,"s":"BTCUSDT","b":"67890.12","B":"1.234","a":"67890.56","A":"0.567"}}""");
            sb.Append('\n');
        }

        var original = Encoding.UTF8.GetBytes(sb.ToString(0, Math.Min(sb.Length, OriginalSize)));
        _compressed = RawDeflate(original);
        // 동일 압축 데이터 반복 inflate이므로 context takeover off로 매 반복 독립 inflate
        _inflater = new DeflateInflater(noContextTakeover: true, initialOutputSize: OriginalSize * 2);

        // native zlib 코드 패스 warmup
        for (int i = 0; i < 10; i++)
            _inflater.Inflate(_compressed);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _inflater.Dispose();
    }

    [Benchmark]
    public ReadOnlyMemory<byte> Inflate_SingleShot()
    {
        return _inflater.Inflate(_compressed);
    }

    [Benchmark]
    public ReadOnlyMemory<byte> Inflate_Streaming()
    {
        _inflater.BeginMessage();

        // 4KB 청크로 분할 공급 (FrameReader의 실제 동작 시뮬레이션)
        int chunkSize = 4096;
        int offset = 0;
        while (offset < _compressed.Length)
        {
            int len = Math.Min(chunkSize, _compressed.Length - offset);
            _inflater.AppendCompressed(_compressed.AsSpan(offset, len));
            offset += len;
        }

        return _inflater.FinishMessage();
    }

    private static byte[] RawDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            ds.Write(data);
        }

        var result = ms.ToArray();
        // RFC7692 tail 제거
        if (result.Length >= 4 &&
            result[^4] == 0x00 && result[^3] == 0x00 &&
            result[^2] == 0xFF && result[^1] == 0xFF)
        {
            return result[..^4];
        }

        return result;
    }
}
