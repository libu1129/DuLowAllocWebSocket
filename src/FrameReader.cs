using System.Buffers;
using System.Buffers.Binary;

namespace DuLowAllocWebSocket;

/// <summary>
/// 파싱된 WebSocket 프레임 헤더. RawByte0/RawByte1은 진단용 원본 바이트.
/// </summary>
public readonly record struct FrameHeader(
    bool Fin,
    bool Rsv1,
    WebSocketOpcode Opcode,
    bool Masked,
    int PayloadLength,
    uint MaskKey,
    byte RawByte0 = 0,
    byte RawByte1 = 0);

/// <summary>
/// WebSocket 프레임 헤더와 페이로드를 동기적으로 읽는 파서입니다.
/// <see cref="ArrayPool{T}.Shared"/>에서 빌린 스크래치 버퍼에 read-ahead 방식으로 데이터를 적재하여,
/// steady-state에서 힙 할당 없이 프레임을 파싱합니다.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private readonly Stream _transport;
    private byte[]? _scratch;
    private readonly WebSocketClientOptions _options;
    private int _bufferOffset;
    private int _bufferCount;

    public FrameReader(Stream transport, WebSocketClientOptions options)
    {
        _transport = transport;
        _options = options;
        _scratch = ArrayPool<byte>.Shared.Rent(options.ReceiveScratchBufferSize);
    }

    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _scratch, null);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// 진단용: 내부 수신 버퍼의 현재 읽기 오프셋입니다.
    /// </summary>
    public int DiagBufferOffset => _bufferOffset;

    /// <summary>
    /// 진단용: 내부 수신 버퍼에 적재된 데이터 총량입니다.
    /// </summary>
    public int DiagBufferCount => _bufferCount;

    public ValueTask<FrameHeader> ReadHeaderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<FrameHeader>(ReadHeader());
    }

    /// <summary>
    /// 프레임 헤더를 읽어 파싱한다.
    /// <para>
    /// _scratch 버퍼와 분리된 스택 버퍼(headerBuf)를 사용하여,
    /// ReadExactlySync 내부에서 _scratch에 새 데이터를 읽을 때
    /// 이미 복사된 헤더 바이트가 덮어씌워지는 앨리어싱 버그를 방지한다.
    /// </para>
    /// </summary>
    public FrameHeader ReadHeader()
    {
        // _scratch를 destination으로 직접 전달하면, ReadExactlySync 내부에서
        // transport 부분 읽기(partial read) 발생 시 _scratch[0..]이 덮어씌워져
        // 이전에 복사된 헤더 바이트가 소실될 수 있다. 별도 스택 버퍼 사용.
        Span<byte> headerBuf = stackalloc byte[8];

        ReadExactlySync(headerBuf[..2]);

        byte b0 = headerBuf[0];
        byte b1 = headerBuf[1];

        bool fin = (b0 & 0b1000_0000) != 0;
        bool rsv1 = (b0 & 0b0100_0000) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);

        bool masked = (b1 & 0b1000_0000) != 0;
        ulong len7 = (uint)(b1 & 0x7F);

        ulong payloadLen = len7;
        if (len7 == 126)
        {
            ReadExactlySync(headerBuf[..2]);
            payloadLen = BinaryPrimitives.ReadUInt16BigEndian(headerBuf[..2]);
        }
        else if (len7 == 127)
        {
            ReadExactlySync(headerBuf[..8]);
            payloadLen = BinaryPrimitives.ReadUInt64BigEndian(headerBuf[..8]);
        }

        if (payloadLen > (ulong)_options.MaxMessageBytes)
        {
            throw new WebSocketProtocolException($"Payload exceeds configured max ({_options.MaxMessageBytes} bytes).");
        }

        // RFC6455 5.5 규정상 125바이트 이하여야 하나, 일부 서버가 위반하므로 허용

        uint maskKey = 0;
        if (masked)
        {
            if (_options.RejectMaskedServerFrames)
            {
                throw new WebSocketProtocolException("Masked server frame rejected by policy.");
            }

            ReadExactlySync(headerBuf[..4]);
            maskKey = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[..4]);
        }

        return new FrameHeader(fin, rsv1, opcode, masked, (int)payloadLen, maskKey, b0, b1);
    }

    /// <summary>
    /// 프레임 페이로드를 읽어 <paramref name="target"/>에 추가합니다.
    /// <para>
    /// 버퍼가 비었을 때 remaining 바이트만이 아닌 _scratch 전체를 채우도록 읽는다.
    /// 후속 프레임 데이터가 동일 syscall로 함께 수신되어, 다음 ReadHeader/ReadPayloadInto에서
    /// 커널 전환 없이 소비할 수 있으므로 버스트 수신 시 syscall 횟수를 대폭 절감한다.
    /// </para>
    /// </summary>
    public void ReadPayloadInto(FrameHeader header, IPayloadSink target)
    {
        int remaining = header.PayloadLength;
        uint maskKey = header.MaskKey;
        int maskOffset = 0;

        if (remaining == 0)
        {
            return;
        }

        while (remaining > 0)
        {
            int n;
            int chunkOffset;

            int buffered = _bufferCount - _bufferOffset;
            if (buffered > 0)
            {
                n = Math.Min(remaining, buffered);
                chunkOffset = _bufferOffset;
                _bufferOffset += n;
            }
            else
            {
                // ReadExactlySync와 동일한 full-buffer read 전략:
                // remaining만큼만 읽으면 후속 프레임 데이터를 놓쳐 추가 syscall 발생.
                // _scratch 전체를 채워 커널 버퍼에 대기 중인 데이터를 한번에 가져온다.
                _bufferCount = _transport.Read(_scratch!);
                if (_bufferCount == 0) throw new WebSocketProtocolException("Connection closed while reading payload.");
                _bufferOffset = 0;
                n = Math.Min(remaining, _bufferCount);
                chunkOffset = 0;
                _bufferOffset = n;
            }

            var chunk = _scratch.AsSpan(chunkOffset, n);

            if (header.Masked)
            {
                Unmask(chunk, maskKey, ref maskOffset);
            }

            target.Append(chunk);
            remaining -= n;
        }
    }

    public ValueTask ReadPayloadIntoAsync(FrameHeader header, IPayloadSink target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadPayloadInto(header, target);
        return ValueTask.CompletedTask;
    }

    private void ReadExactlySync(Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int buffered = _bufferCount - _bufferOffset;
            if (buffered == 0)
            {
                _bufferCount = _transport.Read(_scratch);
                if (_bufferCount == 0) throw new WebSocketProtocolException("Connection closed.");
                _bufferOffset = 0;
                buffered = _bufferCount;
            }

            int toCopy = Math.Min(destination.Length - read, buffered);
            _scratch.AsSpan(_bufferOffset, toCopy).CopyTo(destination[read..]);
            _bufferOffset += toCopy;
            read += toCopy;
        }
    }

    // IsControl moved to WebSocketOpcodeExtensions

    private static void Unmask(Span<byte> data, uint key, ref int offset)
    {
        Span<byte> mask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mask, key);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= mask[(offset + i) & 3];
        }

        offset = (offset + data.Length) & 3;
    }
}
