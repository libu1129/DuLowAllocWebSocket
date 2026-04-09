using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace DuLowAllocWebSocket;

/// <summary>
/// 저할당 WebSocket 클라이언트입니다. 전용 수신 스레드에서 동기 읽기를 수행하여
/// steady-state 힙 할당 0을 달성합니다.
/// <para>
/// 인스턴스는 단일 사용(single-use)입니다: connect → communicate → close → dispose.
/// 재연결하려면 새 인스턴스를 생성하세요.
/// </para>
/// <para>
/// <see cref="MessageReceived"/> 콜백의 <see cref="DuLowAllocWebSocketReceiveResult.Payload"/>는
/// 내부 풀 메모리를 참조하며, 콜백이 반환되면 즉시 무효화됩니다.
/// 데이터를 유지하려면 콜백 내에서 복사하세요.
/// </para>
/// </summary>
public sealed class DuLowAllocWebSocketClient : IDisposable
{
    private readonly WebSocketHandshake _handshake = new();
    private readonly WebSocketClientOptions _options;

    private readonly MessageAssembler _messageAssembler;
    private readonly MessageAssembler _controlAssembler;

    private Socket? _socket;
    private Stream? _transport;
    private FrameReader? _frameReader;
    private FrameWriter? _frameWriter;
    private DeflateInflater? _inflater;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _backgroundCts;
    private Task? _autoPingTask;
    private volatile bool _closeSent;
    private volatile bool _closeReceived;
    private volatile bool _disposed;
    private int _closing;
    private int _state = (int)WebSocketState.None;

    private Thread? _unsafeReceivePumpThread;

    /// <summary>
    /// 완성된 메시지 수신 시 전용 수신 스레드에서 호출됩니다.
    /// <para>
    /// <b>주의:</b> <see cref="DuLowAllocWebSocketReceiveResult.Payload"/>는 내부 풀 버퍼를 참조합니다.
    /// 이 콜백이 반환되면 해당 메모리가 재사용되므로, 데이터를 유지하려면 콜백 내에서 복사해야 합니다.
    /// </para>
    /// </summary>
    public event Action<DuLowAllocWebSocketReceiveResult>? MessageReceived;

    /// <summary>
    /// 수신 펌프 스레드가 종료될 때 호출됩니다 (에러, 소켓 끊김, Close 프레임 등 모든 경우).
    /// WebsocketClient의 자동 재연결 로직에서 사용합니다.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// 수신 펌프에서 예외 발생 시 호출됩니다. Disconnected 이전에 호출됩니다.
    /// null이면 예외가 무시됩니다.
    /// </summary>
    public event Action<Exception>? OnError;

    public WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

    public DuLowAllocWebSocketClient(WebSocketClientOptions? options = null)
    {
        _options = options ?? new WebSocketClientOptions();
        _messageAssembler = new MessageAssembler(_options.MessageBufferSize);
        _controlAssembler = new MessageAssembler(_options.ControlBufferSize);
    }

    /// <summary>
    /// WebSocket 서버에 연결합니다 (DNS → TCP → TLS → HTTP Upgrade).
    /// 연결 성공 후 전용 수신 스레드가 시작되어 <see cref="MessageReceived"/>를 통해 메시지를 전달합니다.
    /// </summary>
    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _state) != (int)WebSocketState.None)
        {
            throw new InvalidOperationException("Already used. Dispose and create a new client for a new connection.");
        }

        Volatile.Write(ref _state, (int)WebSocketState.Connecting);
        Socket socket;
        Stream transport;
        var compression = default(CompressionOptions);
        try
        {
            (socket, transport, compression) = await _handshake.ConnectAsync(uri, _options, ct).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            throw;
        }

        _socket = socket;
        _transport = transport;
        _frameReader = new FrameReader(transport, _options);
        _frameWriter = new FrameWriter(transport, _options);

        if (compression.Enabled)
        {
            _inflater = new DeflateInflater(compression.ServerNoContextTakeover, _options.InflateOutputBufferSize);
        }

        _closeSent = false;
        _closeReceived = false;
        Interlocked.Exchange(ref _closing, 0);
        Volatile.Write(ref _state, (int)WebSocketState.Open);
        StartAutoPingLoopIfEnabled();

        _unsafeReceivePumpThread = new Thread(UnsafeReceivePump)
        {
            IsBackground = true,
            Name = "DuLowAllocWebSocket.ReceivePump",
            Priority = _options.ReceiveThreadPriority
        };
        _unsafeReceivePumpThread.Start();
    }

    /// <summary>
    /// 데이터 프레임을 전송합니다. 동시 호출은 내부 <see cref="SemaphoreSlim"/>으로 직렬화됩니다.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureSendAllowed();
        return SendFrameAsync(payload, opcode, ct);
    }

    public ValueTask SendPingAsync(ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureSendAllowed();
        if (payload.Length > 125)
        {
            throw new ArgumentException("Ping payload must be <= 125 bytes (RFC6455 5.5.2).", nameof(payload));
        }

        return SendFrameAsync(payload, WebSocketOpcode.Ping, ct);
    }

    /// <summary>
    /// Close 프레임을 전송하되 상대방의 Close 응답을 기다리지 않습니다 (half-close).
    /// 수신 펌프는 계속 동작하며, 상대방이 Close로 응답하면 <see cref="MessageReceived"/>를 통해 전달됩니다.
    /// </summary>
    public async ValueTask CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct = default)
    {
        EnsureConnected();
        var state = (WebSocketState)Volatile.Read(ref _state);
        if (state is WebSocketState.CloseSent or WebSocketState.Closed)
        {
            return;
        }

        ReadOnlyMemory<byte> payload = BuildClosePayload(closeStatus, statusDescription);
        await SendFrameAsync(payload, WebSocketOpcode.Close, ct).ConfigureAwait(false);
        _closeSent = true;
        Volatile.Write(ref _state, (int)(_closeReceived ? WebSocketState.Closed : WebSocketState.CloseSent));
    }

    /// <summary>
    /// Close 프레임을 전송하고 상대방의 Close 응답을 수신한 뒤 트랜스포트를 닫습니다 (full close).
    /// </summary>
    public async ValueTask CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct = default)
    {
        EnsureConnected();
        await CloseOutputAsync(closeStatus, statusDescription, ct).ConfigureAwait(false);

        if (!_closeReceived)
        {
            await ReceiveCloseHandshakeAsync(ct).ConfigureAwait(false);
        }

        Volatile.Write(ref _state, (int)WebSocketState.Closed);
        CloseTransport();
    }

    /// <summary>
    /// 전용 수신 스레드의 진입점입니다. async/await 대신 동기 블로킹 읽기를 사용하여
    /// Task/상태 머신 할당을 완전히 제거합니다.
    /// 이 메서드 내부를 수정할 때 async 패턴이나 힙 할당을 도입하지 않도록 주의하세요.
    /// </summary>
    private void UnsafeReceivePump()
    {
        try
        {
            if (_frameReader is null)
            {
                throw new InvalidOperationException("Unsafe receive pump initialization failed.");
            }

            // 핫 루프에서 반복되는 인스턴스 필드 접근을 로컬로 캐싱하여
            // 레지스터 할당을 유도하고 필드 역참조 비용을 제거한다.
            var reader = _frameReader;
            var assembler = _messageAssembler;
            var inflater = _inflater;         // null이면 비압축 전용 연결
            bool insideFragmentedMessage = false;
            bool compressed = false;
            WebSocketOpcode lastOpcode = default;
            int lastPayloadLength = 0;

            while (!_disposed && Volatile.Read(ref _closing) == 0)
            {
                assembler.Reset();
                insideFragmentedMessage = false;
                compressed = false;

                while (true)
                {
                    FrameHeader header = reader.ReadHeader();
                    ValidateHeader(header, insideFragmentedMessage,
                        lastOpcode, lastPayloadLength,
                        reader.DiagBufferOffset, reader.DiagBufferCount);

                    if (header.Opcode.IsControl())
                    {
                        var controlResult = HandleControlFrameSync(header);
                        lastOpcode = header.Opcode;
                        lastPayloadLength = header.PayloadLength;
                        if (controlResult is { } close)
                        {
                            MessageReceived?.Invoke(close);
                            return;
                        }

                        continue;
                    }

                    if (!insideFragmentedMessage)
                    {
                        insideFragmentedMessage = true;
                        compressed = header.Rsv1;
                        if (compressed)
                        {
                            if (inflater is null)
                            {
                                throw new WebSocketProtocolException("RSV1 set but permessage-deflate was not negotiated.");
                            }

                            inflater.BeginMessage();
                        }
                    }

                    // 압축 메시지: FrameReader → DeflateInflater 직접 스트리밍 (MessageAssembler 우회)
                    // 비압축 메시지: 기존대로 MessageAssembler에 조립
                    if (compressed)
                    {
                        reader.ReadPayloadInto(header, inflater!);
                    }
                    else
                    {
                        reader.ReadPayloadInto(header, assembler);
                    }

                    lastOpcode = header.Opcode;
                    lastPayloadLength = header.PayloadLength;

                    if (!header.Fin)
                    {
                        continue;
                    }

                    DuLowAllocWebSocketReceiveResult result;
                    if (!compressed)
                    {
                        result = new DuLowAllocWebSocketReceiveResult(assembler.WrittenMemory, header.Opcode);
                    }
                    else
                    {
                        result = new DuLowAllocWebSocketReceiveResult(inflater!.FinishMessage(), header.Opcode);
                    }

                    MessageReceived?.Invoke(result);

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            try { OnError?.Invoke(ex); } catch { }
        }
        finally
        {
            try { Disconnected?.Invoke(); } catch { }
        }
    }

    private DuLowAllocWebSocketReceiveResult? HandleControlFrameSync(FrameHeader header)
    {
        if (!header.Fin)
        {
            // raw 바이트 포함: 네트워크 단절로 인한 프레임 경계 오정렬인지, 실제 프로토콜 위반인지 구분 가능
            throw new WebSocketProtocolException(
                $"Control frames must not be fragmented (RFC6455 5.5). " +
                $"Opcode: {header.Opcode}, PayloadLen: {header.PayloadLength}, " +
                $"RawHeader: 0x{header.RawByte0:X2} 0x{header.RawByte1:X2}, " +
                $"ReaderBuf: offset={_frameReader!.DiagBufferOffset} count={_frameReader.DiagBufferCount}",
                isSuspectedMisalignment: !IsKnownOpcode(header.Opcode));
        }

        _controlAssembler.Reset();
        _frameReader!.ReadPayloadInto(header, _controlAssembler);

        switch (header.Opcode)
        {
            case WebSocketOpcode.Ping:
                if (_options.AutoPongOnPing)
                {
                    SendFrameSync(_controlAssembler.WrittenSpan, WebSocketOpcode.Pong);
                }

                return null;
            case WebSocketOpcode.Pong:
                return null;
            case WebSocketOpcode.Close:
                var closeResult = ParseCloseResult(_controlAssembler.WrittenSpan);
                _closeReceived = true;
                Volatile.Write(ref _state, (int)(_closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived));
                if (!_closeSent)
                {
                    SendFrameSync(_controlAssembler.WrittenSpan, WebSocketOpcode.Close);
                    _closeSent = true;
                    Volatile.Write(ref _state, (int)WebSocketState.Closed);
                }

                CloseTransport();
                return closeResult;
            default:
                throw new WebSocketProtocolException($"Unexpected control opcode {header.Opcode}.");
        }
    }

    private void StartAutoPingLoopIfEnabled()
    {
        if (_options.KeepAliveInterval == TimeSpan.Zero)
        {
            return;
        }

        if (_options.KeepAliveInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException("KeepAliveInterval must be >= TimeSpan.Zero.");
        }

        if (_options.KeepAlivePingPayload.Length > 125)
        {
            throw new InvalidOperationException("KeepAlivePingPayload must be <= 125 bytes.");
        }

        _backgroundCts = new CancellationTokenSource();
        _autoPingTask = AutoPingLoopAsync(_options.KeepAliveInterval, _backgroundCts.Token);
    }

    private async Task AutoPingLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SendPingAsync(_options.KeepAlivePingPayload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected during dispose/shutdown
        }
        catch
        {
            // background ping loop should not crash process
        }
    }

    private async ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken ct)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            throw new InvalidOperationException("Connection is closing.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                throw new InvalidOperationException("Connection is closing.");
            }

            await _frameWriter!.SendAsync(payload, opcode, fin: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 전용 수신 스레드에서 Pong/Close 응답 시 사용하는 동기 전송 경로입니다.
    /// async 상태 머신 및 Task 힙 할당을 완전히 회피합니다.
    /// </summary>
    private void SendFrameSync(ReadOnlySpan<byte> payload, WebSocketOpcode opcode)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        _sendLock.Wait();
        try
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }

            _frameWriter!.SendSync(payload, opcode, fin: true);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // IsControl moved to WebSocketOpcodeExtensions

    private async Task ReceiveCloseHandshakeAsync(CancellationToken ct)
    {
        while (!_closeReceived)
        {
            FrameHeader header = await _frameReader!.ReadHeaderAsync(ct).ConfigureAwait(false);
            if (!header.Opcode.IsControl())
            {
                _messageAssembler.Reset();
                await _frameReader.ReadPayloadIntoAsync(header, _messageAssembler, ct).ConfigureAwait(false);
                continue;
            }

            if (!header.Fin)
            {
                throw new WebSocketProtocolException(
                    $"Control frames must not be fragmented (RFC6455 5.5). " +
                    $"Opcode: {header.Opcode}, PayloadLen: {header.PayloadLength}, " +
                    $"RawHeader: 0x{header.RawByte0:X2} 0x{header.RawByte1:X2}, " +
                    $"ReaderBuf: offset={_frameReader.DiagBufferOffset} count={_frameReader.DiagBufferCount}",
                    isSuspectedMisalignment: !IsKnownOpcode(header.Opcode));
            }

            _controlAssembler.Reset();
            await _frameReader.ReadPayloadIntoAsync(header, _controlAssembler, ct).ConfigureAwait(false);

            switch (header.Opcode)
            {
                case WebSocketOpcode.Ping:
                    if (_options.AutoPongOnPing)
                    {
                        await SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Pong, ct).ConfigureAwait(false);
                    }
                    break;
                case WebSocketOpcode.Pong:
                    break;
                case WebSocketOpcode.Close:
                    _closeReceived = true;
                    Volatile.Write(ref _state, (int)(_closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived));
                    if (!_closeSent)
                    {
                        await SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Close, ct).ConfigureAwait(false);
                        _closeSent = true;
                        Volatile.Write(ref _state, (int)WebSocketState.Closed);
                    }
                    break;
                default:
                    throw new WebSocketProtocolException($"Unexpected control opcode {header.Opcode}.");
            }
        }
    }

    private static DuLowAllocWebSocketReceiveResult ParseCloseResult(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return new DuLowAllocWebSocketReceiveResult(closeStatus: null, closeStatusDescription: null);
        }

        if (payload.Length == 1)
        {
            throw new WebSocketProtocolException("Close frame payload length of 1 is invalid (RFC6455 5.5.1).");
        }

        ushort code = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        string? description = payload.Length > 2 ? System.Text.Encoding.UTF8.GetString(payload[2..]) : null;
        return new DuLowAllocWebSocketReceiveResult((WebSocketCloseStatus)code, description);
    }

    private static ReadOnlyMemory<byte> BuildClosePayload(WebSocketCloseStatus closeStatus, string? statusDescription)
    {
        ValidateCloseStatus(closeStatus);

        if (statusDescription is null)
        {
            byte[] payloadWithoutReason = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(payloadWithoutReason, checked((ushort)closeStatus));
            return payloadWithoutReason;
        }

        int reasonByteCount = System.Text.Encoding.UTF8.GetByteCount(statusDescription);
        if (reasonByteCount > 123)
        {
            throw new ArgumentException("Close reason must be <= 123 UTF-8 bytes.", nameof(statusDescription));
        }

        byte[] payload = new byte[2 + reasonByteCount];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), checked((ushort)closeStatus));
        _ = System.Text.Encoding.UTF8.GetBytes(statusDescription, payload.AsSpan(2));
        return payload;
    }

    private static void ValidateCloseStatus(WebSocketCloseStatus closeStatus)
    {
        ushort code = checked((ushort)closeStatus);
        if (code is 1005 or 1006 or 1015)
        {
            throw new ArgumentException($"Close status code {code} cannot be sent on wire.", nameof(closeStatus));
        }

        if (code < 1000 || (code >= 1016 && code <= 1999) || (code >= 2000 && code <= 2999) || code >= 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(closeStatus), closeStatus, "Invalid WebSocket close status code.");
        }
    }

    /// <summary>
    /// 트랜스포트를 종료하고 모든 리소스를 해제합니다.
    /// SSL 네이티브 리소스(SslFree)는 수신 스레드가 확실히 종료된 후에만 해제하여,
    /// SSL_read 실행 중 SslFree가 호출되는 use-after-free를 구조적으로 차단합니다.
    /// </summary>
    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        if (_backgroundCts is not null)
        {
            _backgroundCts.Cancel();
            _backgroundCts.Dispose();
            _backgroundCts = null;
        }

        // 1단계: 소켓 Shutdown + OpenSslStream 인터럽트로 블로킹 read를 해제시킨다.
        //        소켓 Shutdown은 원본 fd에, InterruptRead는 dup된 fd에 shutdown을 호출하여
        //        어느 한쪽이 실패하더라도 SSL_read가 확실히 깨어나도록 한다.
        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignore socket shutdown failures during teardown
        }

        (_transport as OpenSslStream)?.InterruptRead();

        // 2단계: 수신 스레드가 완전히 종료될 때까지 대기한다.
        //        스레드가 아직 SSL_read/inflate 내부에 있을 수 있으므로,
        //        네이티브 핸들 해제 전에 반드시 Join해야 한다.
        //        타임아웃 30초: 소켓 shutdown 후 SSL_read는 즉시 반환되어야 하나,
        //        극단적 스케줄링 지연에 대비하여 여유를 둔다.
        Thread? receiveThread = _unsafeReceivePumpThread;
        bool receiveThreadExited = true;
        if (receiveThread is not null && receiveThread != Thread.CurrentThread && receiveThread.IsAlive)
        {
            receiveThreadExited = receiveThread.Join(millisecondsTimeout: 30_000);
        }

        // 3단계: 수신 스레드 종료 확인 후, 모든 리소스를 안전하게 해제한다.
        _sendLock.Wait();
        try
        {
            _autoPingTask = null;
            _unsafeReceivePumpThread = null;
            _frameReader?.Dispose();
            _frameReader = null;
            _frameWriter?.Dispose();
            _frameWriter = null;

            // SSL 네이티브 리소스 해제: 수신 스레드가 확실히 종료됐을 때만 수행.
            // - receiveThreadExited=true (Join 성공): 스레드 사망 확인 → SslFree 안전
            // - 수신 스레드 자신이 호출 (Close 프레임): SSL_read를 다시 호출하지 않음 → 안전
            // - receiveThreadExited=false (Join 타임아웃): SSL_read가 아직 실행 중일 수 있음 →
            //   SslFree 호출 시 use-after-free 위험 → 누수를 선택 (누수 < SEGV 크래시)
            if (receiveThreadExited)
            {
                (_transport as OpenSslStream)?.FreeSslResources();
            }

            _transport?.Dispose();
            _transport = null;
            _inflater?.Dispose();
            _inflater = null;
            _socket?.Dispose();
            _socket = null;

            if (Volatile.Read(ref _state) != (int)WebSocketState.Aborted)
            {
                Volatile.Write(ref _state, (int)WebSocketState.Closed);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 프레임 헤더의 프로토콜 유효성을 검증합니다.
    /// 정상 경로(3개 비교+분기)만 인라이닝되고, 예외 생성(string interpolation)은
    /// NoInlining throw helper로 분리하여 호출자의 코드 크기를 최소화합니다.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateHeader(
        FrameHeader header,
        bool insideFragmentedMessage,
        WebSocketOpcode lastOpcode,
        int lastPayloadLength,
        int readerBufOffset,
        int readerBufCount)
    {
        if (header.Rsv1 && (header.Opcode is WebSocketOpcode.Continuation or WebSocketOpcode.Ping or WebSocketOpcode.Pong or WebSocketOpcode.Close))
        {
            ThrowInvalidRsv1(header, lastOpcode, lastPayloadLength, readerBufOffset, readerBufCount);
        }

        if (insideFragmentedMessage && header.Opcode != WebSocketOpcode.Continuation && !header.Opcode.IsControl())
        {
            ThrowExpectedContinuation(header, lastOpcode, lastPayloadLength, readerBufOffset, readerBufCount);
        }

        if (!insideFragmentedMessage && header.Opcode == WebSocketOpcode.Continuation)
        {
            ThrowUnexpectedContinuation(header, lastOpcode, lastPayloadLength, readerBufOffset, readerBufCount);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRsv1(
        FrameHeader header, WebSocketOpcode lastOpcode, int lastPayloadLength, int readerBufOffset, int readerBufCount)
    {
        throw new WebSocketProtocolException(
            $"Invalid RSV1 usage for opcode {header.Opcode}. " +
            $"RawHeader: 0x{header.RawByte0:X2} 0x{header.RawByte1:X2}, " +
            $"Fin: {header.Fin}, PayloadLen: {header.PayloadLength}, " +
            $"PrevOpcode: {lastOpcode}, PrevPayloadLen: {lastPayloadLength}, " +
            $"ReaderBuf: offset={readerBufOffset} count={readerBufCount}",
            !IsKnownOpcode(header.Opcode));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExpectedContinuation(
        FrameHeader header, WebSocketOpcode lastOpcode, int lastPayloadLength, int readerBufOffset, int readerBufCount)
    {
        throw new WebSocketProtocolException(
            $"Expected continuation frame but got opcode {header.Opcode}. " +
            $"RawHeader: 0x{header.RawByte0:X2} 0x{header.RawByte1:X2}, " +
            $"Fin: {header.Fin}, PayloadLen: {header.PayloadLength}, " +
            $"PrevOpcode: {lastOpcode}, PrevPayloadLen: {lastPayloadLength}, " +
            $"ReaderBuf: offset={readerBufOffset} count={readerBufCount}",
            !IsKnownOpcode(header.Opcode));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnexpectedContinuation(
        FrameHeader header, WebSocketOpcode lastOpcode, int lastPayloadLength, int readerBufOffset, int readerBufCount)
    {
        throw new WebSocketProtocolException(
            $"Unexpected continuation frame. " +
            $"RawHeader: 0x{header.RawByte0:X2} 0x{header.RawByte1:X2}, " +
            $"Fin: {header.Fin}, PayloadLen: {header.PayloadLength}, " +
            $"PrevOpcode: {lastOpcode}, PrevPayloadLen: {lastPayloadLength}, " +
            $"ReaderBuf: offset={readerBufOffset} count={readerBufCount}",
            isSuspectedMisalignment: true);
    }

    private static bool IsKnownOpcode(WebSocketOpcode opcode) =>
        opcode is WebSocketOpcode.Continuation or WebSocketOpcode.Text or WebSocketOpcode.Binary
            or WebSocketOpcode.Close or WebSocketOpcode.Ping or WebSocketOpcode.Pong;

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (_socket is null || _frameReader is null || _frameWriter is null || _transport is null)
        {
            throw new InvalidOperationException("Call ConnectAsync before send/receive.");
        }
    }

    private void EnsureSendAllowed()
    {
        var state = (WebSocketState)Volatile.Read(ref _state);
        if (state != WebSocketState.Open)
        {
            throw new InvalidOperationException($"Cannot send when WebSocketState is {state}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DuLowAllocWebSocketClient));
        }
    }

    /// <summary>
    /// 클라이언트를 종료하고 모든 리소스를 해제합니다.
    /// 수신 스레드 종료 → 네이티브 핸들 해제 → ArrayPool 반환 순서를 보장합니다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _state, (int)WebSocketState.Closed);
        CloseTransport();
        _messageAssembler.Dispose();
        _controlAssembler.Dispose();

        // CloseTransport가 다른 스레드에서 _sendLock 내부 작업 중일 수 있으므로,
        // lock 획득 후 해제하여 완료를 보장한 뒤 Dispose한다.
        _sendLock.Wait();
        _sendLock.Release();
        _sendLock.Dispose();
    }
}
