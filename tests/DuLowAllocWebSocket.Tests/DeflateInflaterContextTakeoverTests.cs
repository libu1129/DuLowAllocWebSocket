using System.IO.Compression;
using System.Text;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class DeflateInflaterContextTakeoverTests
{
    // 서버가 BFINAL 로 끝나는 raw-deflate 메시지를 보내면 payload inflate 가 Z_STREAM_END 를 반환한다.
    // context-takeover 모드(noContextTakeover=false)에서 수정 전엔:
    //   ① FinishMessage 가 tail(00 00 FF FF)을 종료 스트림에 inflate → Z_STREAM_ERROR(-2, tail=True)
    //   ② 다음 메시지 BeginMessage 가 재arm 하지 않아 종료 스트림에 inflate → Z_STREAM_ERROR(-2, tail=False)
    // ②가 coinone/Gate.io 사설 WS 가 ~1.5분마다 Lost 재연결하며 계좌 거래를 토글한 직접 원인.
    // 수정: Z_STREAM_END 시 tail 생략 + 다음 메시지에서 inflateResetKeep(윈도우 보존) 재arm.
    [Fact]
    public void ContextTakeover_AfterStreamEnd_NextMessageInflatesWithoutError()
    {
        if (!DeflateInflater.IsSupported)
        {
            return; // 네이티브 zlib 미가용 환경 — 스킵
        }

        byte[] msg1 = Encoding.UTF8.GetBytes("{\"r\":\"DATA\",\"c\":\"MYORDER\",\"d\":{\"oi\":\"first-message\"}}");
        byte[] msg2 = Encoding.UTF8.GetBytes("{\"r\":\"DATA\",\"c\":\"MYORDER\",\"d\":{\"oi\":\"second-message-after-stream-end\"}}");

        byte[] c1 = RawDeflate(msg1);
        byte[] c2 = RawDeflate(msg2);

        using var inflater = new DeflateInflater(noContextTakeover: false);

        // 첫 메시지: payload 가 Z_STREAM_END(BFINAL) → tail 생략으로 정상 반환(수정 전엔 tail inflate 에서 throw).
        byte[] out1 = inflater.Inflate(c1).ToArray();
        Assert.Equal(msg1, out1);

        // 둘째 메시지: 직전이 Z_STREAM_END 로 끝났어도 재arm 후 정상 inflate (수정 전엔 -2, tail=False 로 throw).
        byte[] out2 = inflater.Inflate(c2).ToArray();
        Assert.Equal(msg2, out2);
    }

    // no_context_takeover 경로(메시지마다 inflateReset)도 Z_STREAM_END 연속 메시지에서 정상이어야 한다(회귀 가드).
    [Fact]
    public void NoContextTakeover_RepeatedStreamEnd_InflatesEachMessage()
    {
        if (!DeflateInflater.IsSupported)
        {
            return;
        }

        byte[] msg = Encoding.UTF8.GetBytes("{\"r\":\"DATA\",\"c\":\"MYORDER\",\"d\":{\"st\":\"trade_done\"}}");
        byte[] c = RawDeflate(msg);

        using var inflater = new DeflateInflater(noContextTakeover: true);

        for (int i = 0; i < 3; i++)
        {
            byte[] outBytes = inflater.Inflate(c).ToArray();
            Assert.Equal(msg, outBytes);
        }
    }

    /// <summary>raw deflate(BFINAL 종료, zlib 헤더 없음 — windowBits -15 호환).</summary>
    private static byte[] RawDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}
