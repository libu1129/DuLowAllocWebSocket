using System.Buffers;
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
    private const int ControlFrameMaxPayloadBytes = 125;
    private const int AutoPongSlotSize = ControlFrameMaxPayloadBytes + 1;

    private readonly WebSocketHandshake _handshake = new();
    private readonly WebSocketClientOptions _options;

    // 대다수 단일 비압축 frame은 FrameReader scratch를 직접 빌려주므로,
    // fragmented/masked/scratch 초과 fallback이 실제로 발생할 때만 256KiB 버퍼를 대여한다.
    private MessageAssembler? _messageAssembler;
    // RFC-compliant unmasked control frame은 FrameReader scratch에서 직접 처리한다.
    // masked-frame 허용 또는 RFC 한도 초과 lenient fallback이 실제로 발생할 때만 대여한다.
    private MessageAssembler? _controlAssembler;
    private readonly ArrayPool<byte> _controlAssemblerPool;

    private Socket? _socket;
    private Stream? _transport;
    private FrameReader? _frameReader;
    private FrameWriter? _frameWriter;
    private DeflateInflater? _inflater;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // FrameWriter가 부분 프레임을 기록했을 수 있는 첫 실패를 lock 안에서 게시해 후속 송신을 차단한다.
    private int _sendFaulted;
    private CancellationTokenSource? _backgroundCts;
    private Task? _autoPingTask;
    private volatile bool _closeSent;
    private volatile bool _closeReceived;
    private TaskCompletionSource<DuLowAllocWebSocketReceiveResult>? _closeHandshakeCompletion;
    private volatile bool _disposed;
    // 0: open/not started, 1: one thread owns transport teardown, 2: teardown completed.
    private int _closing;
    private int _disposeStarted;
    private int _managedResourcesDisposed;
    private int _receiveResourcesDisposed;
    // MessageReceived/OnError/Disconnected 콜백이 모두 반환하기 전에는 수신 버퍼를 반환할 수 없다.
    private int _receivePumpExited = 1;
    private int _state = (int)WebSocketState.None;

    private Thread? _unsafeReceivePumpThread;
    private readonly AutoPongWorkItem _autoPongWorkItem;
    private readonly ArrayPool<byte> _autoPongPool;
    private byte[]? _autoPongSlots;
    private int _autoPongQueueCapacity;
    private int _autoPongHead;
    private int _autoPongTail;
    private int _autoPongCount;
    // 0: idle, 1: 동일한 reusable work item이 ThreadPool에 queued/running.
    private int _autoPongWorkerScheduled;
    private int _autoPongWorkerThreadId;
    private bool _autoPongReleaseWhenIdle;
    private readonly object _autoPongLock = new();

    private sealed class AutoPongWorkItem(DuLowAllocWebSocketClient owner) : IThreadPoolWorkItem
    {
        void IThreadPoolWorkItem.Execute() => owner.ProcessAutoPongQueue();
    }

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
    /// 수신 펌프 또는 transport 송신에서 <b>예기치 않은</b> 예외 발생 시 호출됩니다. Disconnected 이전에 호출됩니다.
    /// 클라이언트가 시작한 종료(Dispose/Close)로 블로킹 read가 깨져 발생하는 예외(예: "SSL_read failed",
    /// "Connection closed.")는 정상 종료 경로이므로 호출되지 않습니다.
    /// null이면 예외가 무시됩니다.
    /// </summary>
    public event Action<Exception>? OnError;

    /// <summary>
    /// 현재 WebSocket 연결 상태입니다.
    /// </summary>
    public WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

    /// <summary>
    /// Ping/Pong 같은 제어 프레임 전송 전 호출되는 제한기입니다.
    /// 여러 연결이 같은 인스턴스를 공유하면 서버 ping 응답이 한꺼번에 몰리는 상황을 줄일 수 있습니다.
    /// </summary>
    public IWebSocketControlFrameThrottle? ControlFrameThrottle { get; set; }

    /// <summary>
    /// <see cref="DuLowAllocWebSocketClient"/>의 새 인스턴스를 생성합니다.
    /// </summary>
    /// <param name="options">클라이언트 동작 옵션. <see langword="null"/>이면 기본값 사용.</param>
    public DuLowAllocWebSocketClient(WebSocketClientOptions? options = null)
        : this(options, ArrayPool<byte>.Shared, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>테스트에서 auto-pong 슬롯의 임대/반환 소유권을 검증하기 위한 내부 생성자입니다.</summary>
    internal DuLowAllocWebSocketClient(
        WebSocketClientOptions? options,
        ArrayPool<byte> autoPongPool)
        : this(options, autoPongPool, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>테스트에서 auto-pong/control 버퍼의 풀 소유권을 각각 검증하기 위한 내부 생성자입니다.</summary>
    internal DuLowAllocWebSocketClient(
        WebSocketClientOptions? options,
        ArrayPool<byte> autoPongPool,
        ArrayPool<byte> controlAssemblerPool)
    {
        ArgumentNullException.ThrowIfNull(autoPongPool);
        ArgumentNullException.ThrowIfNull(controlAssemblerPool);
        _options = options ?? new WebSocketClientOptions();
        _autoPongPool = autoPongPool;
        _controlAssemblerPool = controlAssemblerPool;
        if (_options.MessageBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WebSocketClientOptions.MessageBufferSize),
                _options.MessageBufferSize,
                "MessageBufferSize must be > 0.");
        }

        if (_options.ControlBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WebSocketClientOptions.ControlBufferSize),
                _options.ControlBufferSize,
                "ControlBufferSize must be > 0.");
        }

        if (_options.MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WebSocketClientOptions.MaxMessageBytes),
                _options.MaxMessageBytes,
                "MaxMessageBytes must be > 0.");
        }

        _autoPongWorkItem = new AutoPongWorkItem(this);
    }

    /// <summary>
    /// WebSocket 서버에 연결합니다 (DNS → TCP → TLS → HTTP Upgrade).
    /// 연결 성공 후 전용 수신 스레드가 시작되어 <see cref="MessageReceived"/>를 통해 메시지를 전달합니다.
    /// </summary>
    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ThrowIfDisposed();
        ValidateBackgroundOptions();
        if (Volatile.Read(ref _state) != (int)WebSocketState.None)
        {
            throw new InvalidOperationException("Already used. Dispose and create a new client for a new connection.");
        }

        Volatile.Write(ref _state, (int)WebSocketState.Connecting);
        WebSocketHandshake.WebSocketHandshakeResult handshakeResult;
        try
        {
            handshakeResult = await _handshake.ConnectWithInitialDataAsync(uri, _options, ct).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            throw;
        }

        try
        {
            try
            {
                _socket = handshakeResult.Socket;
                _transport = handshakeResult.Transport;
                if (handshakeResult.TryDetachInitialReadBuffer(
                    out byte[]? initialReadBuffer,
                    out int initialReadOffset,
                    out int initialReadCount))
                {
                    try
                    {
                        _frameReader = new FrameReader(
                            handshakeResult.Transport,
                            _options,
                            initialReadBuffer!,
                            initialReadOffset,
                            initialReadCount);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(initialReadBuffer!);
                        throw;
                    }
                }
                else
                {
                    _frameReader = new FrameReader(handshakeResult.Transport, _options);
                }
                _frameWriter = new FrameWriter(handshakeResult.Transport, _options);

                if (handshakeResult.Compression.Enabled)
                {
                    _inflater = new DeflateInflater(
                        handshakeResult.Compression.ServerNoContextTakeover,
                        _options.InflateOutputBufferSize,
                        _options.MaxMessageBytes);
                }
            }
            finally
            {
                handshakeResult.Dispose();
            }

            _closeSent = false;
            _closeReceived = false;
            _closeHandshakeCompletion = new TaskCompletionSource<DuLowAllocWebSocketReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _closing, 0);
            Volatile.Write(ref _state, (int)WebSocketState.Open);
            _backgroundCts = new CancellationTokenSource();
            InitializeAutoPongQueueIfEnabled();
            StartAutoPingLoopIfEnabled();

            var receiveThread = new Thread(UnsafeReceivePump)
            {
                IsBackground = true,
                Name = "DuLowAllocWebSocket.ReceivePump",
                Priority = _options.ReceiveThreadPriority
            };
            _unsafeReceivePumpThread = receiveThread;
            Volatile.Write(ref _receivePumpExited, 0);
            try
            {
                receiveThread.Start();
            }
            catch
            {
                Volatile.Write(ref _receivePumpExited, 1);
                throw;
            }
        }
        catch
        {
            if (CloseTransport())
            {
                Volatile.Write(ref _state, (int)WebSocketState.Closed);
            }
            throw;
        }
    }

    /// <summary>
    /// 지정한 opcode로 프레임을 전송합니다. 동시 호출은 내부 <see cref="SemaphoreSlim"/>으로 직렬화됩니다.
    /// Text/Binary data frame이 주 사용처입니다. Ping/Close는 제어 프레임 제한과 상태 전이를 보장하는 전용 API를 사용하세요.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureSendAllowed();
        return SendFrameAsync(payload, opcode, ct);
    }

    /// <summary>
    /// 지정한 opcode로 프레임을 동기적으로 전송합니다.
    /// Text/Binary data frame이 주 사용처입니다. Ping/Close는 제어 프레임 제한과 상태 전이를 보장하는 전용 API를 사용하세요.
    /// 동시 호출은 <see cref="SendAsync"/>와 같은 내부 lock으로 직렬화하며,
    /// 호출 스레드에서 write까지 수행하여 async 상태 머신 비용을 피합니다.
    /// </summary>
    public void SendSync(ReadOnlySpan<byte> payload, WebSocketOpcode opcode)
    {
        EnsureConnected();
        EnsureSendAllowed();
        SendFrameSyncStrict(payload, opcode);
    }

    /// <summary>
    /// Ping 제어 프레임을 전송합니다 (RFC 6455 5.5.2).
    /// </summary>
    /// <param name="payload">Ping 페이로드 (최대 125바이트).</param>
    /// <param name="ct">취소 토큰.</param>
    public ValueTask SendPingAsync(ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureSendAllowed();
        if (payload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new ArgumentException("Ping payload must be <= 125 bytes (RFC6455 5.5.2).", nameof(payload));
        }

        return SendControlFrameAsync(payload, WebSocketOpcode.Ping, ct, ControlFrameThrottle);
    }

    /// <summary>
    /// 빈 Ping 제어 프레임을 동기적으로 전송합니다 (RFC 6455 5.5.2).
    /// </summary>
    public void SendPingSync()
    {
        SendPingSync(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Ping 제어 프레임을 동기적으로 전송합니다 (RFC 6455 5.5.2).
    /// </summary>
    /// <param name="payload">Ping 페이로드 (최대 125바이트).</param>
    public void SendPingSync(ReadOnlySpan<byte> payload)
    {
        EnsureConnected();
        EnsureSendAllowed();
        if (payload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new ArgumentException("Ping payload must be <= 125 bytes (RFC6455 5.5.2).", nameof(payload));
        }

        WaitControlFrameThrottleSync(WebSocketOpcode.Ping);
        SendFrameSyncStrict(payload, WebSocketOpcode.Ping);
    }

    /// <summary>
    /// Pong 제어 프레임을 전송합니다 (RFC 6455 5.5.3).
    /// 서버 Ping 응답을 직접 제어하거나 unsolicited Pong이 필요한 서버 호환 경로에서 사용합니다.
    /// </summary>
    /// <param name="payload">Pong 페이로드 (최대 125바이트).</param>
    /// <param name="ct">취소 토큰.</param>
    public ValueTask SendPongAsync(ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureSendAllowed();
        if (payload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new ArgumentException("Pong payload must be <= 125 bytes (RFC6455 5.5.3).", nameof(payload));
        }

        return SendControlFrameAsync(payload, WebSocketOpcode.Pong, ct, ControlFrameThrottle);
    }

    /// <summary>
    /// 빈 Pong 제어 프레임을 동기적으로 전송합니다 (RFC 6455 5.5.3).
    /// </summary>
    public void SendPongSync()
    {
        SendPongSync(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Pong 제어 프레임을 동기적으로 전송합니다 (RFC 6455 5.5.3).
    /// 서버 Ping 응답을 직접 제어하거나 unsolicited Pong이 필요한 서버 호환 경로에서 사용합니다.
    /// </summary>
    /// <param name="payload">Pong 페이로드 (최대 125바이트).</param>
    public void SendPongSync(ReadOnlySpan<byte> payload)
    {
        EnsureConnected();
        EnsureSendAllowed();
        if (payload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new ArgumentException("Pong payload must be <= 125 bytes (RFC6455 5.5.3).", nameof(payload));
        }

        WaitControlFrameThrottleSync(WebSocketOpcode.Pong);
        SendFrameSyncStrict(payload, WebSocketOpcode.Pong);
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
        // 송신 lock 대기 중 receive pump가 EOF/오류로 Closed를 게시했을 수 있다.
        // terminal state를 CloseSent로 되돌리면 CloseAsync가 이미 끝난 pump의 TCS를 영구 대기한다.
        Interlocked.CompareExchange(
            ref _state,
            (int)WebSocketState.CloseSent,
            (int)WebSocketState.Open);
    }

    /// <summary>
    /// Close 프레임을 전송하고 상대방의 Close 응답을 수신한 뒤 트랜스포트를 닫습니다 (full close).
    /// </summary>
    public async ValueTask CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var initialState = (WebSocketState)Volatile.Read(ref _state);
        if (initialState is WebSocketState.Closed or WebSocketState.Aborted)
        {
            CloseTransport();
            return;
        }

        EnsureConnected();
        var completion = _closeHandshakeCompletion
            ?? throw new InvalidOperationException("Close handshake completion is not initialized.");
        await CloseOutputAsync(closeStatus, statusDescription, ct).ConfigureAwait(false);

        if (!_closeReceived)
        {
            var stateAfterSend = (WebSocketState)Volatile.Read(ref _state);
            if (stateAfterSend is WebSocketState.Closed or WebSocketState.Aborted)
            {
                CloseTransport();
                return;
            }

            // FrameReader의 유일한 소비자는 전용 수신 펌프다. 여기서 직접 읽으면 이미 블로킹 read 중인
            // 펌프와 프레임 경계를 나눠 가져 close 응답·직전 data frame이 손상된다(2026-08-07 재현).
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
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
        Exception? receiveFailure = null;
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
            // 완성 메시지의 opcode는 마지막 continuation이 아니라 시작 data frame 기준입니다.
            WebSocketOpcode messageOpcode = default;
            WebSocketOpcode lastOpcode = default;
            int lastPayloadLength = 0;

            while (!_disposed && Volatile.Read(ref _closing) == 0)
            {
                assembler?.Reset();
                insideFragmentedMessage = false;
                compressed = false;
                messageOpcode = default;

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

                    // MessageAssembler/DeflateInflater 상태가 필요 없는 단일 비압축 data frame만 zero-copy로 우회합니다.
                    // FrameReader는 partial payload도 scratch에 직접 채우며, fragment/RSV1 compressed/masked/scratch 초과
                    // frame만 아래 fallback이 정확성 기준입니다.
                    // Payload는 read-ahead scratch를 직접 가리켜 다음 read에서 덮이므로 콜백 안에서만 유효합니다.
                    if (!insideFragmentedMessage &&
                        header.Fin &&
                        !header.Rsv1 &&
                        header.Opcode is WebSocketOpcode.Text or WebSocketOpcode.Binary &&
                        reader.TryReadPayloadAsMemory(header, out var payload))
                    {
                        lastOpcode = header.Opcode;
                        lastPayloadLength = header.PayloadLength;
                        MessageReceived?.Invoke(new DuLowAllocWebSocketReceiveResult(payload, header.Opcode));
                        break;
                    }

                    if (!insideFragmentedMessage)
                    {
                        insideFragmentedMessage = true;
                        compressed = header.Rsv1;
                        messageOpcode = header.Opcode;
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
                        if (assembler is null)
                        {
                            assembler = new MessageAssembler(_options.MessageBufferSize, _options.MaxMessageBytes);
                            _messageAssembler = assembler;
                        }

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
                        result = new DuLowAllocWebSocketReceiveResult(assembler!.WrittenMemory, messageOpcode);
                    }
                    else
                    {
                        result = new DuLowAllocWebSocketReceiveResult(inflater!.FinishMessage(), messageOpcode);
                    }

                    MessageReceived?.Invoke(result);

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            receiveFailure = ex;
            // 클라이언트가 시작한 종료(Dispose/CloseTransport)는 블로킹 read를 소켓 shutdown으로 강제로 깨우므로
            // transport가 TLS/소켓 종료 예외 또는 "Connection closed."(FrameReader EOF)를 throw한다.
            // 이는 장애가 아니라 의도된 종료다 — _disposed/_closing이 선 상태면 OnError로 표출하지 않는다.
            // 의도치 않은 네트워크 단절(둘 다 미설정)만 진짜 에러로 보고하여 자동 재연결 판단의 정확도를 지킨다.
            if (!_disposed && Volatile.Read(ref _closing) == 0)
            {
                try { OnError?.Invoke(ex); } catch { }
            }
        }
        finally
        {
            try
            {
                if (Volatile.Read(ref _closing) == 0 &&
                    Volatile.Read(ref _state) != (int)WebSocketState.Aborted)
                {
                    // 예상하지 못한 EOF/오류 뒤 죽은 연결을 Open으로 노출하지 않는다.
                    Volatile.Write(ref _state, (int)WebSocketState.Closed);
                }

                var closeCompletion = _closeHandshakeCompletion;
                if (_closeSent && closeCompletion is not null && !closeCompletion.Task.IsCompleted)
                {
                    closeCompletion.TrySetException(receiveFailure
                        ?? new WebSocketException("Receive pump ended before the close handshake completed."));
                }
                try { Disconnected?.Invoke(); } catch { }
            }
            finally
            {
                // 콜백이 Dispose를 동기 호출해도 payload가 가리키는 pooled buffer는
                // 콜백 반환 뒤에만 반납한다.
                Volatile.Write(ref _receivePumpExited, 1);
                TryDisposeReceiveResources();
                TryDisposeManagedResources();
            }
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

        ReadOnlySpan<byte> payload;
        if (!header.Masked &&
            header.PayloadLength <= ControlFrameMaxPayloadBytes &&
            _frameReader!.TryReadPayloadAsMemory(header, out ReadOnlyMemory<byte> payloadMemory))
        {
            // scratch는 다음 read 전까지만 유효하다. 아래 처리는 ping payload를 queue slot에 복사하고,
            // close echo를 동기 송신한 뒤에만 반환하므로 이 수명 경계를 넘기지 않는다.
            payload = payloadMemory.Span;
        }
        else
        {
            MessageAssembler assembler = _controlAssembler ??= new MessageAssembler(
                _options.ControlBufferSize,
                _options.MaxMessageBytes,
                _controlAssemblerPool);
            assembler.Reset();
            _frameReader!.ReadPayloadInto(header, assembler);
            payload = assembler.WrittenSpan;
        }

        switch (header.Opcode)
        {
            case WebSocketOpcode.Ping:
                if (_options.AutoPongOnPing)
                {
                    EnqueueAutoPong(payload);
                }

                return null;
            case WebSocketOpcode.Pong:
                return null;
            case WebSocketOpcode.Close:
                var closeResult = ParseCloseResult(payload);
                _closeReceived = true;
                Volatile.Write(ref _state, (int)(_closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived));
                if (!_closeSent)
                {
                    // FrameWriter.SendSync가 반환되기 전에 payload를 scratch에서 소비하므로
                    // 다음 receive가 scratch를 덮기 전 close echo wire semantics가 보존된다.
                    SendFrameSync(payload, WebSocketOpcode.Close);
                    _closeSent = true;
                    Volatile.Write(ref _state, (int)WebSocketState.Closed);
                }

                if (CloseTransport())
                    _closeHandshakeCompletion?.TrySetResult(closeResult);
                else
                    _closeHandshakeCompletion?.TrySetException(
                        new WebSocketException("Transport shutdown failed during the close handshake."));
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

        if (_options.KeepAlivePingPayload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new InvalidOperationException("KeepAlivePingPayload must be <= 125 bytes.");
        }

        _autoPingTask = AutoPingLoopAsync(_options.KeepAliveInterval, _backgroundCts!.Token);
    }

    private void ValidateBackgroundOptions()
    {
        if (_options.KeepAliveInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException("KeepAliveInterval must be >= TimeSpan.Zero.");
        }

        if (_options.KeepAlivePingPayload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new InvalidOperationException("KeepAlivePingPayload must be <= 125 bytes.");
        }

        if (!_options.AutoPongOnPing)
        {
            return;
        }

        if (_options.AutoPongQueueCapacity <= 0)
        {
            throw new InvalidOperationException("AutoPongQueueCapacity must be > 0.");
        }

        if (_options.AutoPongQueueCapacity > int.MaxValue / AutoPongSlotSize)
        {
            throw new InvalidOperationException("AutoPongQueueCapacity is too large.");
        }
    }

    private void InitializeAutoPongQueueIfEnabled()
    {
        if (!_options.AutoPongOnPing)
        {
            return;
        }

        // 유효성 검사는 네트워크 연결 전에 ValidateBackgroundOptions에서 끝낸다. 여기서는
        // 첫 실제 Ping 전까지 슬롯 배열을 빌리지 않고 큐 상태만 게시한다.
        _autoPongQueueCapacity = _options.AutoPongQueueCapacity;
        _autoPongHead = 0;
        _autoPongTail = 0;
        _autoPongCount = 0;
        _autoPongWorkerScheduled = 0;
        _autoPongWorkerThreadId = 0;
        _autoPongReleaseWhenIdle = false;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // expected during dispose/shutdown
        }
        catch (Exception ex)
        {
            // heartbeat write 실패를 숨기면 receive가 계속 블로킹된 채 Open으로 남아
            // 상위 reconnect가 시작되지 않는다. 오류를 먼저 알린 뒤 transport를 끊어
            // receive pump의 Disconnected 경로를 확실히 유도한다.
            if (!_disposed && Volatile.Read(ref _closing) == 0)
            {
                try { OnError?.Invoke(ex); } catch { }
                try { CloseTransport(); } catch { }
            }
        }
    }

    private async ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken ct)
    {
        if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
        {
            throw new InvalidOperationException("Connection is closing or its send transport has failed.");
        }

        Exception? sendFailure = null;
        bool ownsSendFailure = false;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
            {
                throw new InvalidOperationException("Connection is closing or its send transport has failed.");
            }

            try
            {
                await _frameWriter!.SendAsync(payload, opcode, fin: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sendFailure = ex;
                ownsSendFailure = Interlocked.CompareExchange(ref _sendFaulted, 1, 0) == 0;
                throw;
            }
        }
        finally
        {
            _sendLock.Release();
            if (ownsSendFailure)
            {
                HandleFatalSendFailure(sendFailure!);
            }
        }
    }

    private async ValueTask SendControlFrameAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketOpcode opcode,
        CancellationToken ct,
        IWebSocketControlFrameThrottle? throttle)
    {
        await WaitControlFrameThrottleAsync(throttle, opcode, ct).ConfigureAwait(false);
        await SendFrameAsync(payload, opcode, ct).ConfigureAwait(false);
    }

    private static ValueTask WaitControlFrameThrottleAsync(IWebSocketControlFrameThrottle? throttle, WebSocketOpcode opcode, CancellationToken ct)
    {
        return throttle is null ? ValueTask.CompletedTask : throttle.WaitAsync(opcode, ct);
    }

    private void WaitControlFrameThrottleSync(WebSocketOpcode opcode)
    {
        WaitControlFrameThrottleSync(opcode, CancellationToken.None);
    }

    private void WaitControlFrameThrottleSync(WebSocketOpcode opcode, CancellationToken ct)
    {
        var throttle = ControlFrameThrottle;
        if (throttle is null)
            return;

        throttle.WaitAsync(opcode, ct).AsTask().GetAwaiter().GetResult();
    }

    private void SendFrameSyncStrict(ReadOnlySpan<byte> payload, WebSocketOpcode opcode)
    {
        if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
        {
            throw new InvalidOperationException("Connection is closing or its send transport has failed.");
        }

        Exception? sendFailure = null;
        bool ownsSendFailure = false;
        _sendLock.Wait();
        try
        {
            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
            {
                throw new InvalidOperationException("Connection is closing or its send transport has failed.");
            }

            try
            {
                _frameWriter!.SendSync(payload, opcode, fin: true);
            }
            catch (Exception ex)
            {
                sendFailure = ex;
                ownsSendFailure = Interlocked.CompareExchange(ref _sendFaulted, 1, 0) == 0;
                throw;
            }
        }
        finally
        {
            _sendLock.Release();
            if (ownsSendFailure)
            {
                HandleFatalSendFailure(sendFailure!);
            }
        }
    }

    /// <summary>
    /// 수신 스레드의 Close 응답과 ThreadPool auto-pong work item에서 사용하는 동기 전송 경로입니다.
    /// async 상태 머신 및 Task 힙 할당을 완전히 회피합니다.
    /// </summary>
    private void SendFrameSync(ReadOnlySpan<byte> payload, WebSocketOpcode opcode)
    {
        if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
        {
            return;
        }

        Exception? sendFailure = null;
        bool ownsSendFailure = false;
        _sendLock.Wait();
        try
        {
            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _sendFaulted) != 0)
            {
                return;
            }

            try
            {
                _frameWriter!.SendSync(payload, opcode, fin: true);
            }
            catch (Exception ex)
            {
                sendFailure = ex;
                ownsSendFailure = Interlocked.CompareExchange(ref _sendFaulted, 1, 0) == 0;
                throw;
            }
        }
        finally
        {
            _sendLock.Release();
            if (ownsSendFailure)
            {
                HandleFatalSendFailure(sendFailure!);
            }
        }
    }

    private void HandleFatalSendFailure(Exception error)
    {
        // 프레임 일부가 이미 기록됐을 수 있으므로 같은 transport에서 송신을 계속할 수 없다.
        // send lock을 놓은 뒤 호출되어 CloseTransport의 lock 획득과 self-deadlock하지 않는다.
        if (_disposed || Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        try { OnError?.Invoke(error); } catch { }
        try { CloseTransport(); } catch { }
    }

    private void EnqueueAutoPong(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ControlFrameMaxPayloadBytes)
        {
            throw new WebSocketProtocolException("Ping payload must be <= 125 bytes (RFC6455 5.5.2).");
        }

        if (Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        bool scheduleWorker = false;
        lock (_autoPongLock)
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }

            // Enqueue는 전용 receive pump의 단일 producer 경로다. lock은 worker/Dispose와의
            // 소유권 경계를 직렬화하며, 첫 Ping에서만 슬롯 배열을 한 번 빌린다.
            if (!TryEnsureAutoPongSlotsUnderLock(out var slots))
            {
                return;
            }

            if (_autoPongCount >= _autoPongQueueCapacity)
            {
                throw new InvalidOperationException("Auto-pong queue is full.");
            }

            var offset = _autoPongTail * AutoPongSlotSize;
            slots[offset] = (byte)payload.Length;
            payload.CopyTo(slots.AsSpan(offset + 1, payload.Length));
            _autoPongTail = (_autoPongTail + 1) % _autoPongQueueCapacity;
            _autoPongCount++;

            if (_autoPongWorkerScheduled == 0)
            {
                _autoPongWorkerScheduled = 1;
                scheduleWorker = true;
            }
        }

        if (!scheduleWorker)
        {
            return;
        }

        try
        {
            if (!ThreadPool.UnsafeQueueUserWorkItem(_autoPongWorkItem, preferLocal: false))
            {
                throw new InvalidOperationException("Failed to queue the auto-pong work item.");
            }
        }
        catch
        {
            lock (_autoPongLock)
            {
                // Queueing 자체가 실패했으므로 Execute가 이 상태를 소유할 수 없다.
                _autoPongWorkerScheduled = 0;
                Monitor.PulseAll(_autoPongLock);
            }

            throw;
        }
    }

    /// <summary>
    /// 첫 실제 Ping에서 auto-pong 슬롯을 지연 임대한다. Dispose가 Rent와 경합해 closing을
    /// 먼저 게시하면 배열을 필드에 노출하지 않고 즉시 반환한다.
    /// </summary>
    private bool TryEnsureAutoPongSlotsUnderLock(out byte[] slots)
    {
        byte[]? current = _autoPongSlots;
        if (current is not null)
        {
            slots = current;
            return true;
        }

        int capacity = _autoPongQueueCapacity;
        if (capacity <= 0)
        {
            throw new InvalidOperationException("Auto-pong queue is not initialized.");
        }

        byte[] rented = _autoPongPool.Rent(capacity * AutoPongSlotSize);
        if (Volatile.Read(ref _closing) != 0)
        {
            _autoPongPool.Return(rented);
            slots = null!;
            return false;
        }

        _autoPongSlots = rented;
        slots = rented;
        return true;
    }

    private void ProcessAutoPongQueue()
    {
        bool workerStateCompleted = false;
        bool ownsTransportTeardown = false;

        lock (_autoPongLock)
        {
            _autoPongWorkerThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            while (true)
            {
                if (!TryPeekAutoPongOrCompleteWorker(out var slots, out var offset, out var length))
                {
                    workerStateCompleted = true;
                    return;
                }

                if (Volatile.Read(ref _closing) != 0)
                {
                    return;
                }

                WaitControlFrameThrottleSync(WebSocketOpcode.Pong, _backgroundCts?.Token ?? CancellationToken.None);
                SendFrameSync(slots.AsSpan(offset + 1, length), WebSocketOpcode.Pong);
                CompleteAutoPong();
            }
        }
        catch (OperationCanceledException) when (
            _disposed ||
            Volatile.Read(ref _closing) != 0 ||
            _backgroundCts?.IsCancellationRequested == true)
        {
            // expected during shutdown
        }
        catch (ObjectDisposedException) when (_disposed || Volatile.Read(ref _closing) != 0)
        {
            // expected if shutdown wins the race against a delayed auto-pong
        }
        catch (InvalidOperationException) when (_disposed || Volatile.Read(ref _closing) != 0)
        {
            // expected if shutdown wins the race against a delayed auto-pong
        }
        catch (Exception ex)
        {
            if (!_disposed && Interlocked.CompareExchange(ref _closing, 1, 0) == 0)
            {
                // OnError가 이 work item에서 Dispose를 동기 호출해도 CloseTransport 재진입은
                // closing=1을 보고 빠져나온다. worker 상태를 finally에서 먼저 게시한 뒤 teardown한다.
                ownsTransportTeardown = true;
                try { OnError?.Invoke(ex); } catch { }
            }
        }
        finally
        {
            if (!workerStateCompleted)
            {
                CompleteAutoPongWorkerState();
            }
        }

        if (ownsTransportTeardown)
        {
            try { CloseTransportOwned(); } catch { }
        }
    }

    private bool TryPeekAutoPongOrCompleteWorker(out byte[] slots, out int offset, out int length)
    {
        lock (_autoPongLock)
        {
            byte[]? currentSlots = _autoPongSlots;
            if (Volatile.Read(ref _closing) != 0 || currentSlots is null || _autoPongCount == 0)
            {
                slots = null!;
                offset = 0;
                length = 0;
                CompleteAutoPongWorkerStateUnderLock();
                return false;
            }

            slots = currentSlots;
            offset = _autoPongHead * AutoPongSlotSize;
            length = currentSlots[offset];
            return true;
        }
    }

    private void CompleteAutoPong()
    {
        lock (_autoPongLock)
        {
            if (_autoPongCount == 0)
            {
                return;
            }

            _autoPongHead = (_autoPongHead + 1) % _autoPongQueueCapacity;
            _autoPongCount--;
        }
    }

    private bool StopAutoPongWorker()
    {
        lock (_autoPongLock)
        {
            if (_autoPongWorkerScheduled != 0 &&
                _autoPongWorkerThreadId == Environment.CurrentManagedThreadId)
            {
                // throttle/송신 오류 콜백이 같은 work item에서 Dispose를 호출한 경우다.
                // 현재 stack이 queue slot을 볼 수 있으므로 Execute의 finally가 반환한다.
                _autoPongReleaseWhenIdle = true;
                return true;
            }

            long deadline = Environment.TickCount64 + 30_000;
            while (_autoPongWorkerScheduled != 0)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    // 실행 중인 work item이 slot을 더 볼 수 있으므로 지금 반환하지 않는다.
                    // 실제 종료 시점의 finally가 안전하게 pool에 돌려준다.
                    _autoPongReleaseWhenIdle = true;
                    Volatile.Write(ref _state, (int)WebSocketState.Aborted);
                    return false;
                }

                Monitor.Wait(_autoPongLock, TimeSpan.FromMilliseconds(remaining));
            }

            ReleaseAutoPongQueueUnderLock();
            return true;
        }
    }

    private void CompleteAutoPongWorkerState()
    {
        lock (_autoPongLock)
        {
            CompleteAutoPongWorkerStateUnderLock();
        }
    }

    private void CompleteAutoPongWorkerStateUnderLock()
    {
        _autoPongWorkerThreadId = 0;
        _autoPongWorkerScheduled = 0;
        if (_autoPongReleaseWhenIdle || Volatile.Read(ref _closing) != 0)
        {
            ReleaseAutoPongQueueUnderLock();
        }

        Monitor.PulseAll(_autoPongLock);
    }

    private void ReleaseAutoPongQueueUnderLock()
    {
        var slots = _autoPongSlots;
        _autoPongSlots = null;
        if (slots is not null)
        {
            _autoPongPool.Return(slots);
        }

        _autoPongQueueCapacity = 0;
        _autoPongHead = 0;
        _autoPongTail = 0;
        _autoPongCount = 0;
        _autoPongReleaseWhenIdle = false;
    }

    // IsControl moved to WebSocketOpcodeExtensions

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
    /// transport는 수신 스레드가 확실히 종료된 후에만 해제하여,
    /// 진행 중인 read와 Dispose가 겹치지 않게 합니다.
    /// </summary>
    private bool CloseTransport()
    {
        if (Interlocked.CompareExchange(ref _closing, 1, 0) != 0)
        {
            // 다른 호출자가 아직 정리를 수행 중이거나 이미 완료했다. 이 호출이 완료를
            // 보장한 것은 아니므로 성공으로 보고하지 않는다.
            return false;
        }

        return CloseTransportOwned();
    }

    /// <summary>호출자가 <c>_closing</c>을 0→1로 바꿔 teardown 소유권을 얻은 뒤 호출합니다.</summary>
    private bool CloseTransportOwned()
    {

        if (Volatile.Read(ref _state) != (int)WebSocketState.Aborted)
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
        }

        try
        {
            var backgroundCts = _backgroundCts;
            // 사용자 throttle의 cancellation callback이 throw해도 socket/read teardown은 계속한다.
            try { backgroundCts?.Cancel(); } catch { }

            // 1단계: 소켓 Shutdown으로 transport의 블로킹 read를 멈춘다.
            try
            {
                _socket?.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // ignore socket shutdown failures during teardown
            }

            // 2단계: 수신 스레드가 완전히 종료될 때까지 대기한다.
            //        스레드가 아직 TLS read/inflate 내부에 있을 수 있으므로,
            //        transport 해제 전에 반드시 Join해야 한다.
            //        타임아웃 30초: 소켓 shutdown 후 SSL_read는 즉시 반환되어야 하나,
            //        극단적 스케줄링 지연에 대비하여 여유를 둔다.
            Thread? receiveThread = _unsafeReceivePumpThread;
            bool receiveThreadExited = true;
            if (receiveThread is not null && receiveThread != Thread.CurrentThread && receiveThread.IsAlive)
            {
                receiveThreadExited = receiveThread.Join(millisecondsTimeout: 30_000);
            }

            if (!receiveThreadExited)
            {
                Volatile.Write(ref _state, (int)WebSocketState.Aborted);
                return false;
            }

            bool autoPongWorkerExited = StopAutoPongWorker();
            if (!autoPongWorkerExited)
            {
                Volatile.Write(ref _state, (int)WebSocketState.Aborted);
                return false;
            }

            backgroundCts?.Dispose();
            _backgroundCts = null;

            // 3단계: transport 리소스를 안전하게 해제한다. FrameReader와 inflater는
            //        MessageReceived 콜백 payload를 소유하므로 수신 펌프 종료 확인 뒤
            //        TryDisposeReceiveResources에서 별도로 해제한다.
            _sendLock.Wait();
            try
            {
                _autoPingTask = null;

                _unsafeReceivePumpThread = null;
                _frameWriter?.Dispose();
                _frameWriter = null;

                _transport?.Dispose();
                _transport = null;
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

            return true;
        }
        catch
        {
            Volatile.Write(ref _state, (int)WebSocketState.Aborted);
            throw;
        }
        finally
        {
            // 다른 Dispose 호출은 정리 소유자가 이 지점에 도달하기 전까지
            // 수신 버퍼와 assembler를 해제하지 않는다.
            Volatile.Write(ref _closing, 2);
            TryDisposeReceiveResources();
            TryDisposeManagedResources();
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
    /// 정상 종료에서는 수신 스레드 종료 → 네이티브 핸들 해제 → ArrayPool 반환 순서를 보장합니다.
    /// 종료 확인 실패 시 살아 있는 스레드가 볼 수 있는 자원은 해제하지 않습니다.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposed = true;
        if (Volatile.Read(ref _state) != (int)WebSocketState.Aborted)
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
        }

        CloseTransport();
        TryDisposeReceiveResources();
        TryDisposeManagedResources();
    }

    private void TryDisposeReceiveResources()
    {
        if (Volatile.Read(ref _closing) != 2 ||
            Volatile.Read(ref _receivePumpExited) == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _receiveResourcesDisposed, 1) != 0)
        {
            return;
        }

        // FrameReader의 scratch buffer와 inflater의 출력 buffer는 MessageReceived payload가
        // 직접 가리킬 수 있다. 수신 콜백이 모두 반환한 뒤에만 풀에 반납한다.
        try { _frameReader?.Dispose(); } catch { }
        _frameReader = null;
        try { _inflater?.Dispose(); } catch { }
        _inflater = null;
    }

    private void TryDisposeManagedResources()
    {
        if (Volatile.Read(ref _disposeStarted) == 0 ||
            Volatile.Read(ref _closing) != 2 ||
            Volatile.Read(ref _receivePumpExited) == 0 ||
            Volatile.Read(ref _state) == (int)WebSocketState.Aborted)
        {
            return;
        }

        if (Interlocked.Exchange(ref _managedResourcesDisposed, 1) != 0)
        {
            return;
        }

        // closing=2는 teardown 소유자가 send lock을 놓고 transport 정리를 마친 뒤에만 게시된다.
        // _sendLock은 Dispose하지 않는다. closing 전 사전 검사를 이미 통과한 WaitAsync 호출이
        // teardown 소유자 뒤에 대기 중일 수 있고, SemaphoreSlim.Dispose는 그 waiter를 깨우지 않는다.
        // AvailableWaitHandle을 사용하지 않으므로 OS handle도 없으며 client와 함께 GC된다.
        try { _messageAssembler?.Dispose(); } catch { }
        _messageAssembler = null;
        try { _controlAssembler?.Dispose(); } catch { }
        _controlAssembler = null;
    }
}
