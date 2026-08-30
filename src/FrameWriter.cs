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
    private const int MaxFrameHeaderBytes = 14;
    private const int SendActiveState = 1;
    private const int DisposedState = 2;
    private readonly Stream _transport;
    private readonly ArrayPool<byte> _maskScratchPool;
    private readonly int _maxMaskScratchSize;
    private byte[]? _maskScratch;
    private int _maskScratchCapacity;
    private int _lifecycleState;

    /// <summary>
    /// <see cref="FrameWriter"/>의 새 인스턴스를 생성합니다.
    /// 마스킹용 스크래치 버퍼는 첫 non-empty payload를 보낼 때 필요한 크기로 빌립니다.
    /// </summary>
    /// <param name="transport">프레임을 기록할 전송 스트림.</param>
    /// <param name="options">송신 버퍼 크기 등 클라이언트 옵션.</param>
    public FrameWriter(Stream transport, WebSocketClientOptions options)
        : this(transport, options, ArrayPool<byte>.Shared)
    {
    }

    internal FrameWriter(
        Stream transport,
        WebSocketClientOptions options,
        ArrayPool<byte> maskScratchPool)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(options);
        _maskScratchPool = maskScratchPool ?? throw new ArgumentNullException(nameof(maskScratchPool));
        _maxMaskScratchSize = NormalizeScratchBufferSize(options.SendScratchBufferSize);
    }

    internal static int NormalizeScratchBufferSize(int configuredSize)
        => Math.Max(configuredSize, MaxFrameHeaderBytes + 1);

    /// <summary>
    /// 대여한 마스킹 스크래치 버퍼를 원래 <see cref="ArrayPool{T}"/>에 반환합니다.
    /// 진행 중인 send가 있으면 해당 send가 끝난 직후 반환합니다.
    /// </summary>
    public void Dispose()
    {
        int previousState = Interlocked.Or(ref _lifecycleState, DisposedState);
        if ((previousState & DisposedState) != 0)
            return;

        if ((previousState & SendActiveState) == 0)
            ReturnScratch();
    }

    /// <summary>
    /// 마스킹된 WebSocket 프레임을 비동기적으로 전송합니다 (RFC 6455 클라이언트→서버 마스킹).
    /// </summary>
    /// <param name="payload">프레임 페이로드.</param>
    /// <param name="opcode">프레임 opcode.</param>
    /// <param name="fin">최종 프래그먼트 여부.</param>
    /// <param name="ct">취소 토큰.</param>
    /// <exception cref="InvalidOperationException">다른 송신이 아직 진행 중입니다.</exception>
    /// <exception cref="ObjectDisposedException">writer가 이미 해제되었습니다.</exception>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, bool fin, CancellationToken ct)
    {
        EnterSend();
        try
        {
            Span<byte> header = stackalloc byte[14];
            int headerLen = WriteHeader(header, payload.Length, opcode, fin, out uint maskKey);

            // 페이로드가 없으면 헤더만 전송하고 scratch는 빌리지 않는다.
            if (payload.Length == 0)
            {
                _transport.Write(header[..headerLen]);
                return;
            }

            byte[] scratch = EnsureScratch(headerLen, payload.Length, out int scratchCapacity);
            header[..headerLen].CopyTo(scratch);

            int sent = 0;
            while (sent < payload.Length)
            {
                int offset = sent == 0 ? headerLen : 0;
                int chunkLen = Math.Min(scratchCapacity - offset, payload.Length - sent);
                payload.Span.Slice(sent, chunkLen).CopyTo(scratch.AsSpan(offset));
                ApplyMask(scratch.AsSpan(offset, chunkLen), maskKey, sent);
                await _transport.WriteAsync(scratch.AsMemory(0, offset + chunkLen), ct).ConfigureAwait(false);
                sent += chunkLen;
            }
        }
        finally
        {
            ExitSend();
        }
    }

    /// <summary>
    /// 프레임을 동기적으로 전송합니다.
    /// 수신 스레드의 자동 응답 및 공개 sync 송신 경로에서 async 상태 머신 비용을 피하기 위해 사용합니다.
    /// </summary>
    /// <exception cref="InvalidOperationException">다른 송신이 아직 진행 중입니다.</exception>
    /// <exception cref="ObjectDisposedException">writer가 이미 해제되었습니다.</exception>
    public void SendSync(ReadOnlySpan<byte> payload, WebSocketOpcode opcode, bool fin)
    {
        EnterSend();
        try
        {
            Span<byte> header = stackalloc byte[14];
            int headerLen = WriteHeader(header, payload.Length, opcode, fin, out uint maskKey);

            // 페이로드가 없으면 헤더만 전송하고 scratch는 빌리지 않는다.
            if (payload.Length == 0)
            {
                _transport.Write(header[..headerLen]);
                return;
            }

            byte[] scratch = EnsureScratch(headerLen, payload.Length, out int scratchCapacity);
            header[..headerLen].CopyTo(scratch);

            int sent = 0;
            while (sent < payload.Length)
            {
                int offset = sent == 0 ? headerLen : 0;
                int chunkLen = Math.Min(scratchCapacity - offset, payload.Length - sent);
                payload.Slice(sent, chunkLen).CopyTo(scratch.AsSpan(offset));
                ApplyMask(scratch.AsSpan(offset, chunkLen), maskKey, sent);
                _transport.Write(scratch.AsSpan(0, offset + chunkLen));
                sent += chunkLen;
            }
        }
        finally
        {
            ExitSend();
        }
    }

    private static int WriteHeader(
        Span<byte> header,
        int payloadLength,
        WebSocketOpcode opcode,
        bool fin,
        out uint maskKey)
    {
        int headerLen = 0;
        header[headerLen++] = (byte)((fin ? 0b1000_0000 : 0) | ((byte)opcode & 0x0F));

        if (payloadLength <= 125)
        {
            header[headerLen++] = (byte)(0b1000_0000 | payloadLength);
        }
        else if (payloadLength <= ushort.MaxValue)
        {
            header[headerLen++] = 0b1000_0000 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header[headerLen..], (ushort)payloadLength);
            headerLen += 2;
        }
        else
        {
            header[headerLen++] = 0b1000_0000 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header[headerLen..], (ulong)payloadLength);
            headerLen += 8;
        }

        Span<byte> mask = header[headerLen..(headerLen + 4)];
        RandomNumberGenerator.Fill(mask);
        maskKey = BinaryPrimitives.ReadUInt32BigEndian(mask);
        return headerLen + 4;
    }

    private byte[] EnsureScratch(
        int headerLength,
        int payloadLength,
        out int scratchCapacity)
    {
        int requiredLength = GetRequiredScratchLength(headerLength, payloadLength);
        scratchCapacity = _maskScratchCapacity;
        if (scratchCapacity >= requiredLength)
            return _maskScratch!;

        byte[] replacement = _maskScratchPool.Rent(requiredLength);
        if (replacement.Length < requiredLength)
        {
            _maskScratchPool.Return(replacement);
            throw new InvalidOperationException("The masking scratch pool returned an undersized buffer.");
        }

        byte[]? previous = _maskScratch;
        scratchCapacity = Math.Min(replacement.Length, _maxMaskScratchSize);
        _maskScratch = replacement;
        _maskScratchCapacity = scratchCapacity;
        if (previous is not null)
            _maskScratchPool.Return(previous);
        return replacement;
    }

    private int GetRequiredScratchLength(int headerLength, int payloadLength)
    {
        int maxFirstPayloadLength = _maxMaskScratchSize - headerLength;
        return payloadLength >= maxFirstPayloadLength
            ? _maxMaskScratchSize
            : headerLength + payloadLength;
    }

    private void EnterSend()
    {
        int previousState = Interlocked.CompareExchange(
            ref _lifecycleState,
            SendActiveState,
            comparand: 0);
        if (previousState == 0)
            return;

        if ((previousState & DisposedState) != 0)
            throw new ObjectDisposedException(nameof(FrameWriter));

        throw new InvalidOperationException("FrameWriter supports one active send at a time.");
    }

    private void ExitSend()
    {
        // Active 해제와 Disposed 관찰을 한 번의 RMW로 선형화한다. 둘을 따로 게시하면
        // 다음 send가 그 사이에 진입한 뒤 이전 send가 새 send의 scratch를 반환할 수 있다.
        int previousState = Interlocked.And(ref _lifecycleState, ~SendActiveState);
        if ((previousState & DisposedState) != 0)
            ReturnScratch();
    }

    private void ReturnScratch()
    {
        byte[]? buffer = Interlocked.Exchange(ref _maskScratch, null);
        _maskScratchCapacity = 0;
        if (buffer is not null)
            _maskScratchPool.Return(buffer);
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
