using System.Buffers.Binary;
using System.IO.Compression;

namespace DuLowAllocWebSocket.Benchmarks.Helpers;

/// <summary>
/// 유효한 WebSocket 프레임 바이트를 생성하는 정적 헬퍼.
/// </summary>
public static class FrameBuilder
{
    /// <summary>서버→클라이언트 비마스킹 텍스트 프레임.</summary>
    public static byte[] BuildUnmaskedTextFrame(ReadOnlySpan<byte> payload)
        => BuildFrame(payload, opcode: 0x81, masked: false, maskKey: 0);

    /// <summary>마스킹된 텍스트 프레임 (프록시/비표준 서버 시뮬레이션).</summary>
    public static byte[] BuildMaskedTextFrame(ReadOnlySpan<byte> payload, uint maskKey)
        => BuildFrame(payload, opcode: 0x81, masked: true, maskKey: maskKey);

    /// <summary>RSV1=1인 압축 텍스트 프레임. 페이로드는 raw deflate로 압축 후 tail 제거.</summary>
    public static byte[] BuildCompressedFrame(ReadOnlySpan<byte> rawPayload)
    {
        var compressed = RawDeflate(rawPayload);
        // opcode = 0xC1 = FIN(1) + RSV1(1) + Text(1)
        return BuildFrame(compressed, opcode: 0xC1, masked: false, maskKey: 0);
    }

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload, byte opcode, bool masked, uint maskKey)
    {
        int headerLen = 2;
        if (payload.Length >= 126 && payload.Length <= 65535) headerLen += 2;
        else if (payload.Length > 65535) headerLen += 8;
        if (masked) headerLen += 4;

        var frame = new byte[headerLen + payload.Length];

        frame[0] = opcode;

        byte lenByte = masked ? (byte)0x80 : (byte)0;
        int offset;
        if (payload.Length < 126)
        {
            frame[1] = (byte)(lenByte | payload.Length);
            offset = 2;
        }
        else if (payload.Length <= 65535)
        {
            frame[1] = (byte)(lenByte | 126);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
            offset = 4;
        }
        else
        {
            frame[1] = (byte)(lenByte | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
            offset = 10;
        }

        if (masked)
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), maskKey);
            offset += 4;
        }

        payload.CopyTo(frame.AsSpan(offset));

        if (masked)
        {
            Span<byte> mask4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(mask4, maskKey);
            for (int i = 0; i < payload.Length; i++)
                frame[offset + i] ^= mask4[i & 3];
        }

        return frame;
    }

    /// <summary>raw deflate 압축 후 RFC7692 tail (00 00 FF FF) 제거.</summary>
    private static byte[] RawDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            ds.Write(data);
        }

        var result = ms.ToArray();
        // RFC7692: 마지막 4바이트(00 00 FF FF) 제거
        if (result.Length >= 4 &&
            result[^4] == 0x00 && result[^3] == 0x00 &&
            result[^2] == 0xFF && result[^1] == 0xFF)
        {
            return result[..^4];
        }

        return result;
    }
}
