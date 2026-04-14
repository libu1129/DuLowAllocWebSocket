using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace DuLowAllocWebSocket;

/// <summary>
/// WebSocket 프레임을 직렬화하여 전송합니다.
/// 클라이언트→서버 마스킹(RFC6455)을 적용하며, 헤더와 첫 페이로드 청크를
/// 단일 write syscall로 병합하여 전송 횟수를 줄입니다.
/// </summary>
public sealed class FrameWriter : IDisposable
{
    private readonly Stream _transport;
    private byte[]? _maskScratch;

    /// <summary>
    /// <see cref="FrameWriter"/>의 새 인스턴스를 생성하고 마스킹용 스크래치 버퍼를 할당합니다.
    /// </summary>
    /// <param name="transport">프레임을 기록할 전송 스트림.</param>
    /// <param name="options">송신 버퍼 크기 등 클라이언트 옵션.</param>
    public FrameWriter(Stream transport, WebSocketClientOptions options)
    {
        _transport = transport;
        _maskScratch = ArrayPool<byte>.Shared.Rent(options.SendScratchBufferSize);
    }

    /// <summary>
    /// 마스킹 스크래치 버퍼를 <see cref="ArrayPool{T}.Shared"/>에 반환합니다.
    /// </summary>
    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _maskScratch, null);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// 마스킹된 WebSocket 프레임을 비동기적으로 전송합니다 (RFC 6455 클라이언트→서버 마스킹).
    /// </summary>
    /// <param name="payload">프레임 페이로드.</param>
    /// <param name="opcode">프레임 opcode.</param>
    /// <param name="fin">최종 프래그먼트 여부.</param>
    /// <param name="ct">취소 토큰.</param>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, bool fin, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[14];
        int headerLen = 0;

        header[headerLen++] = (byte)((fin ? 0b1000_0000 : 0) | ((byte)opcode & 0x0F));

        if (payload.Length <= 125)
        {
            header[headerLen++] = (byte)(0b1000_0000 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[headerLen++] = 0b1000_0000 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header[headerLen..], (ushort)payload.Length);
            headerLen += 2;
        }
        else
        {
            header[headerLen++] = 0b1000_0000 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header[headerLen..], (ulong)payload.Length);
            headerLen += 8;
        }

        uint maskKey;
        {
            Span<byte> mask = header[headerLen..(headerLen + 4)];
            RandomNumberGenerator.Fill(mask);
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(mask);
            headerLen += 4;
        }

        // 페이로드가 없으면 헤더만 전송
        if (payload.Length == 0)
        {
            _transport.Write(header[..headerLen]);
            return;
        }

        // 헤더를 스크래치 버퍼 앞쪽에 복사하여 첫 번째 페이로드 청크와 단일 write syscall로 병합
        var scratch = _maskScratch!;
        header[..headerLen].CopyTo(scratch);

        int sent = 0;
        while (sent < payload.Length)
        {
            int offset = sent == 0 ? headerLen : 0;
            int chunkLen = Math.Min(scratch.Length - offset, payload.Length - sent);
            payload.Span.Slice(sent, chunkLen).CopyTo(scratch.AsSpan(offset));
            ApplyMask(scratch.AsSpan(offset, chunkLen), maskKey, sent);
            await _transport.WriteAsync(scratch.AsMemory(0, offset + chunkLen), ct).ConfigureAwait(false);
            sent += chunkLen;
        }
    }

    /// <summary>
    /// 프레임을 동기적으로 전송합니다. 전용 수신 스레드에서 Pong/Close 응답 시
    /// async 상태 머신과 Task 힙 할당을 회피하기 위해 사용합니다.
    /// </summary>
    public void SendSync(ReadOnlySpan<byte> payload, WebSocketOpcode opcode, bool fin)
    {
        Span<byte> header = stackalloc byte[14];
        int headerLen = 0;

        header[headerLen++] = (byte)((fin ? 0b1000_0000 : 0) | ((byte)opcode & 0x0F));

        if (payload.Length <= 125)
        {
            header[headerLen++] = (byte)(0b1000_0000 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[headerLen++] = 0b1000_0000 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header[headerLen..], (ushort)payload.Length);
            headerLen += 2;
        }
        else
        {
            header[headerLen++] = 0b1000_0000 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header[headerLen..], (ulong)payload.Length);
            headerLen += 8;
        }

        uint maskKey;
        {
            Span<byte> mask = header[headerLen..(headerLen + 4)];
            RandomNumberGenerator.Fill(mask);
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(mask);
            headerLen += 4;
        }

        // 페이로드가 없으면 헤더만 전송
        if (payload.Length == 0)
        {
            _transport.Write(header[..headerLen]);
            return;
        }

        // 헤더를 스크래치 버퍼 앞쪽에 복사하여 첫 번째 페이로드 청크와 단일 write syscall로 병합
        var scratch = _maskScratch!;
        header[..headerLen].CopyTo(scratch);

        int sent = 0;
        while (sent < payload.Length)
        {
            int offset = sent == 0 ? headerLen : 0;
            int chunkLen = Math.Min(scratch.Length - offset, payload.Length - sent);
            payload.Slice(sent, chunkLen).CopyTo(scratch.AsSpan(offset));
            ApplyMask(scratch.AsSpan(offset, chunkLen), maskKey, sent);
            _transport.Write(scratch.AsSpan(0, offset + chunkLen));
            sent += chunkLen;
        }
    }

    /// <summary>
    /// 클라이언트→서버 마스킹 XOR을 적용합니다.
    /// SIMD 하드웨어 가속이 가능하면 <see cref="Vector{T}"/> 단위로 처리하고,
    /// 나머지 바이트는 스칼라 루프로 처리합니다.
    /// </summary>
    private static void ApplyMask(Span<byte> data, uint maskKey, int streamOffset)
    {
        Span<byte> mask4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mask4, maskKey);

        int offset = streamOffset & 3;
        int i = 0;

        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            // Vector<byte>.Count는 항상 4의 배수(16/32/64)이므로 4바이트 마스크 패턴이 정확히 반복됨
            Span<byte> maskRepeated = stackalloc byte[Vector<byte>.Count];
            for (int j = 0; j < Vector<byte>.Count; j++)
            {
                maskRepeated[j] = mask4[(offset + j) & 3];
            }

            var maskVec = new Vector<byte>(maskRepeated);

            while (i + Vector<byte>.Count <= data.Length)
            {
                var chunk = new Vector<byte>(data.Slice(i, Vector<byte>.Count));
                (chunk ^ maskVec).CopyTo(data.Slice(i));
                i += Vector<byte>.Count;
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] ^= mask4[(offset + i) & 3];
        }
    }
}
