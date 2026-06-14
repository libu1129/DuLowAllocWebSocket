using System.IO.Compression;
using System.Text;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class DeflateInflaterGcRelocationTests
{
    // z_stream 이 관리 객체 필드면 GC 압축이 객체를 이동시켜 주소가 바뀌고, zlib-ng 의
    // inflateStateCheck(state->strm == &strm)가 이를 거부해 inflate 가 Z_STREAM_ERROR(-2)를 반환한다.
    // (coinone 사설 WS 가 ~1.5분마다 "inflate failed: -2 (tail=False)" → Lost 재연결한 근본 원인, 2026-06-14.)
    // 수정: z_stream 을 비관리 메모리(고정 주소)에 둔다. 이 테스트는 inflater 아래에 갭을 만들어 압축 GC 의
    // 객체 이동을 유도한다 — zlib-ng 환경에선 수정 전 첫 inflate 에서 실패, 수정 후 통과.
    // (stock zlib 에는 inflateStateCheck 의 strm 비교가 없어 Windows 에선 항상 통과 — Linux/zlib-ng 회귀 가드.)
    [Fact]
    public void Inflate_SurvivesGcCompactionRelocatingZStream()
    {
        if (!DeflateInflater.IsSupported)
        {
            return;
        }

        byte[] msg = Encoding.UTF8.GetBytes(
            "{\"r\":\"DATA\",\"c\":\"MYORDER\",\"d\":{\"qc\":\"KRW\",\"tc\":\"BOME\",\"st\":\"trade_done\",\"ep\":\"3.001\"}}");
        byte[] raw = RawDeflate(msg);

        // filler 를 inflater 보다 먼저(낮은 주소) gen2 로 올린 뒤 해제 → inflater 아래 갭 → 압축 GC 가 inflater 이동.
        var filler = new List<byte[]>();
        for (int k = 0; k < 40000; k++) filler.Add(new byte[256]);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var inflater = new DeflateInflater(noContextTakeover: false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        filler.Clear();
        filler = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        for (int i = 0; i < 200; i++)
        {
            ReadOnlyMemory<byte> outMem = inflater.Inflate(raw);
            Assert.True(outMem.Span.SequenceEqual(msg), $"output mismatch at iteration {i}");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    private static byte[] RawDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
