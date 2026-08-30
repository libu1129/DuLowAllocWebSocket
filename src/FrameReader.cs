using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

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
/// <remarks>
/// 단일 수신 소비자용입니다. read가 진행 중일 때 다른 스레드에서 <see cref="Dispose"/>를 호출하지 마세요.
/// </remarks>
public sealed class FrameReader : IDisposable
{
    private readonly Stream _transport;
    private byte[]? _scratch;
    private readonly ArrayPool<byte> _scratchPool;
    private readonly WebSocketClientOptions _options;
    private readonly int _maxScratchCapacity;
    private int _scratchCapacity;
    private int _bufferOffset;
    private int _bufferCount;
    // 이전 transport read가 scratch를 가득 채웠으면 다음 빈-buffer read 전에 한 단계 확장한다.
    // 작은/idle 연결은 handshake 크기에 머물고, backlog가 있는 연결은 기존 최대 read-ahead까지 빠르게 회복한다.
    private bool _growReadAheadOnNextRead;
    private readonly object _scratchLifecycleLock = new();

    /// <summary>
    /// <see cref="FrameReader"/>의 새 인스턴스를 생성하고 스크래치 버퍼를 할당합니다.
    /// </summary>
    /// <param name="transport">데이터를 읽을 전송 스트림.</param>
    /// <param name="options">수신 버퍼 크기 등 클라이언트 옵션.</param>
    public FrameReader(Stream transport, WebSocketClientOptions options)
        : this(transport, options, ReadOnlySpan<byte>.Empty, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>
    /// 핸드셰이크 응답과 같은 read에 같이 도착한 WebSocket 바이트를 먼저 소비하도록 초기화합니다.
    /// TCP/TLS는 업그레이드 응답 뒤 첫 프레임을 같은 read에 실어 보낼 수 있으므로,
    /// 이 바이트를 scratch에 보존해야 첫 메시지를 잃지 않습니다.
    /// </summary>
    internal FrameReader(Stream transport, WebSocketClientOptions options, ReadOnlySpan<byte> initialBufferedBytes)
        : this(transport, options, initialBufferedBytes, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>테스트에서 적응형 scratch 임대/반환을 추적하기 위한 생성자입니다.</summary>
    internal FrameReader(
        Stream transport,
        WebSocketClientOptions options,
        ReadOnlySpan<byte> initialBufferedBytes,
        ArrayPool<byte> scratchPool)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scratchPool);
        if (options.ReceiveScratchBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WebSocketClientOptions.ReceiveScratchBufferSize),
                options.ReceiveScratchBufferSize,
                "ReceiveScratchBufferSize must be > 0.");
        }

        _transport = transport;
        _options = options;
        _scratchPool = scratchPool;
        _maxScratchCapacity = Math.Max(options.ReceiveScratchBufferSize, initialBufferedBytes.Length);
        int initialCapacity = GetInitialScratchCapacity(options, initialBufferedBytes.Length);
        _scratch = scratchPool.Rent(initialCapacity);
        if (_scratch.Length < initialCapacity)
        {
            byte[] undersized = _scratch;
            _scratch = null;
            scratchPool.Return(undersized);
            throw new InvalidOperationException("The configured scratch pool returned a buffer smaller than requested.");
        }
        _scratchCapacity = Math.Min(_scratch.Length, _maxScratchCapacity);

        if (!initialBufferedBytes.IsEmpty)
        {
            initialBufferedBytes.CopyTo(_scratch);
            _bufferOffset = 0;
            _bufferCount = initialBufferedBytes.Length;
        }
    }

    /// <summary>
    /// 핸드셰이크 응답 버퍼의 소유권을 넘겨받아 첫 수신 scratch로 재사용합니다.
    /// </summary>
    internal FrameReader(
        Stream transport,
        WebSocketClientOptions options,
        byte[] ownedScratch,
        int initialOffset,
        int initialCount,
        ArrayPool<byte>? scratchPool = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownedScratch);
        if (ownedScratch.Length == 0)
        {
            throw new ArgumentException("Owned scratch buffer must not be empty.", nameof(ownedScratch));
        }
        if (options.ReceiveScratchBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WebSocketClientOptions.ReceiveScratchBufferSize),
                options.ReceiveScratchBufferSize,
                "ReceiveScratchBufferSize must be > 0.");
        }

        if ((uint)initialOffset > (uint)ownedScratch.Length
            || (uint)initialCount > (uint)(ownedScratch.Length - initialOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(initialOffset), "Initial buffered range is outside the owned scratch buffer.");
        }

        _transport = transport;
        _options = options;
        _scratchPool = scratchPool ?? ArrayPool<byte>.Shared;
        _maxScratchCapacity = Math.Max(options.ReceiveScratchBufferSize, initialCount);
        int desiredInitialCapacity = GetInitialScratchCapacity(options, initialCount);
        if (ownedScratch.Length > _maxScratchCapacity)
        {
            byte[] replacement = _scratchPool.Rent(desiredInitialCapacity);
            if (replacement.Length < desiredInitialCapacity)
            {
                _scratchPool.Return(replacement);
                throw new InvalidOperationException("The configured scratch pool returned a buffer smaller than requested.");
            }

            if (initialCount > 0)
            {
                ownedScratch.AsSpan(initialOffset, initialCount).CopyTo(replacement);
            }

            _scratch = replacement;
            _scratchCapacity = Math.Min(replacement.Length, _maxScratchCapacity);
            _bufferOffset = 0;
            _bufferCount = initialCount;
            _scratchPool.Return(ownedScratch);
        }
        else
        {
            _scratch = ownedScratch;
            _scratchCapacity = Math.Min(ownedScratch.Length, _maxScratchCapacity);
            _bufferOffset = initialOffset;
            _bufferCount = initialOffset + initialCount;
        }
    }

    /// <summary>
    /// 스크래치 버퍼를 <see cref="ArrayPool{T}.Shared"/>에 반환합니다.
    /// </summary>
    public void Dispose()
    {
        byte[]? buf;
        lock (_scratchLifecycleLock)
        {
            buf = _scratch;
            _scratch = null;
            _scratchCapacity = 0;
        }

        if (buf is not null)
        {
            _scratchPool.Return(buf);
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

    /// <summary>진단용: 현재 scratch에서 실제로 사용하는 논리 용량입니다.</summary>
    internal int DiagScratchCapacity => Volatile.Read(ref _scratchCapacity);

    /// <summary>
    /// 프레임 헤더를 비동기적으로 읽어 파싱합니다.
    /// 내부적으로 <see cref="ReadHeader"/>에 위임합니다.
    /// </summary>
    /// <param name="ct">취소 토큰.</param>
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
                _bufferCount = ReadIntoEmptyScratch();
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

    /// <summary>
    /// 비마스킹 payload가 scratch에 들어갈 수 있으면 필요한 나머지 바이트를 직접 채워
    /// 연속된 <see cref="ReadOnlyMemory{T}"/>로 빌려줍니다.
    /// 이미 일부만 read-ahead 된 경우에도 별도 조립 버퍼로 복사하지 않고 scratch의 빈 영역에
    /// transport를 읽습니다. masked frame 또는 scratch보다 큰 frame이면
    /// <see cref="ReadPayloadInto"/>가 정확성 기준입니다.
    /// 반환 메모리는 reader scratch를 직접 가리켜 다음 read에서 덮일 수 있으므로 콜백 안에서만 소비해야 합니다.
    /// </summary>
    internal bool TryReadPayloadAsMemory(FrameHeader header, out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        if (header.Masked)
        {
            return false;
        }

        int length = header.PayloadLength;
        if (length == 0)
        {
            payload = ReadOnlyMemory<byte>.Empty;
            return true;
        }

        int buffered = _bufferCount - _bufferOffset;
        if (buffered < length)
        {
            if (length > _maxScratchCapacity)
            {
                if (_growReadAheadOnNextRead && _scratchCapacity < _maxScratchCapacity)
                {
                    _growReadAheadOnNextRead = false;
                    EnsureScratchCapacity(_scratchCapacity + 1);
                }

                return false;
            }

            int requiredCapacity = length;
            if (_growReadAheadOnNextRead && _scratchCapacity < _maxScratchCapacity)
            {
                _growReadAheadOnNextRead = false;
                requiredCapacity = Math.Max(requiredCapacity, _scratchCapacity + 1);
            }

            EnsureScratchCapacity(requiredCapacity);

            byte[] scratch = _scratch!;
            int scratchCapacity = _scratchCapacity;

            // 소비된 header가 scratch 앞부분을 차지해 payload 전체를 연속 배치할 공간이 없으면
            // 이미 받은 payload 조각만 앞으로 당긴다. payload가 scratch보다 큰 경우는 위에서
            // 아무 상태도 바꾸지 않고 fallback하므로 ReadPayloadInto가 그대로 이어받을 수 있다.
            if (buffered == 0)
            {
                _bufferOffset = 0;
                _bufferCount = 0;
            }
            else if (length > scratchCapacity - _bufferOffset)
            {
                scratch.AsSpan(_bufferOffset, buffered).CopyTo(scratch);
                _bufferOffset = 0;
                _bufferCount = buffered;
            }

            while (_bufferCount - _bufferOffset < length)
            {
                int read = _transport.Read(scratch.AsSpan(_bufferCount, scratchCapacity - _bufferCount));
                if (read == 0)
                {
                    throw new WebSocketProtocolException("Connection closed while reading payload.");
                }

                _bufferCount += read;
                _growReadAheadOnNextRead = _bufferCount == scratchCapacity;
            }
        }

        payload = _scratch.AsMemory(_bufferOffset, length);
        _bufferOffset += length;
        return true;
    }

    private bool EnsureScratchCapacity(int requiredPayloadLength)
    {
        if (requiredPayloadLength <= Volatile.Read(ref _scratchCapacity))
        {
            return true;
        }

        if (requiredPayloadLength > _maxScratchCapacity)
        {
            return false;
        }

        byte[]? oldScratch = null;
        byte[]? replacement = null;
        lock (_scratchLifecycleLock)
        {
            byte[] current = _scratch ?? throw new ObjectDisposedException(nameof(FrameReader));
            if (requiredPayloadLength <= _scratchCapacity)
            {
                return true;
            }

            int doubled = _scratchCapacity <= _maxScratchCapacity / 2
                ? _scratchCapacity * 2
                : _maxScratchCapacity;
            int requestedCapacity = Math.Min(_maxScratchCapacity, Math.Max(requiredPayloadLength, doubled));
            replacement = _scratchPool.Rent(requestedCapacity);
            int replacementCapacity = Math.Min(replacement.Length, _maxScratchCapacity);
            if (replacementCapacity < requiredPayloadLength)
            {
                _scratchPool.Return(replacement);
                throw new InvalidOperationException("The configured scratch pool returned a buffer smaller than requested.");
            }

            int buffered = _bufferCount - _bufferOffset;
            if (buffered > 0)
            {
                current.AsSpan(_bufferOffset, buffered).CopyTo(replacement);
            }

            oldScratch = current;
            _scratch = replacement;
            _scratchCapacity = replacementCapacity;
            _bufferOffset = 0;
            _bufferCount = buffered;
        }

        _scratchPool.Return(oldScratch!);
        return true;
    }

    private static int GetInitialScratchCapacity(WebSocketClientOptions options, int initialBufferedLength)
    {
        int handshakeCapacity = options.HandshakeBufferSize > 0
            ? options.HandshakeBufferSize
            : 16 * 1024;
        int adaptiveCapacity = Math.Min(options.ReceiveScratchBufferSize, handshakeCapacity);
        return Math.Max(1, Math.Max(adaptiveCapacity, initialBufferedLength));
    }

    private int ReadIntoEmptyScratch()
    {
        if (_growReadAheadOnNextRead && _scratchCapacity < _maxScratchCapacity)
        {
            _growReadAheadOnNextRead = false;
            EnsureScratchCapacity(_scratchCapacity + 1);
        }

        byte[] scratch = _scratch ?? throw new ObjectDisposedException(nameof(FrameReader));
        int capacity = _scratchCapacity;
        int read = _transport.Read(scratch.AsSpan(0, capacity));
        _growReadAheadOnNextRead = read == capacity;
        return read;
    }

    /// <summary>
    /// 프레임 페이로드를 비동기적으로 읽어 <paramref name="target"/>에 추가합니다.
    /// 내부적으로 <see cref="ReadPayloadInto"/>에 위임합니다.
    /// </summary>
    /// <param name="header">읽을 프레임의 헤더.</param>
    /// <param name="target">페이로드를 수신할 싱크.</param>
    /// <param name="ct">취소 토큰.</param>
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
                _bufferCount = ReadIntoEmptyScratch();
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

    /// <summary>
    /// 페이로드에 XOR 마스크를 적용/해제합니다.
    /// SIMD 하드웨어 가속이 가능하면 <see cref="Vector{T}"/> 단위로 처리하고,
    /// 나머지 바이트는 스칼라 루프로 처리합니다.
    /// </summary>
    internal static void Unmask(Span<byte> data, uint key, ref int offset)
    {
        Span<byte> mask4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mask4, key);

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

        offset = (offset + data.Length) & 3;
    }
}
