using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace DuLowAllocWebSocket;

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
    private int _receiveInProgress;

    private CancellationTokenSource? _backgroundCts;
    private Task? _autoPingTask;
    private bool _closeSent;
    private bool _closeReceived;
    private bool _disposed;
    private int _closing;
    private WebSocketState _state = WebSocketState.None;

    private Channel<DuLowAllocWebSocketReceiveResult>? _unsafeReceiveQueue;
    private Task? _unsafeReceivePumpTask;

    public WebSocketState State => _state;

    public DuLowAllocWebSocketClient(WebSocketClientOptions? options = null)
    {
        _options = options ?? new WebSocketClientOptions();
        _messageAssembler = new MessageAssembler(_options.MessageBufferSize);
        _controlAssembler = new MessageAssembler(_options.ControlBufferSize);
    }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_state != WebSocketState.None)
        {
            throw new InvalidOperationException("Already used. Dispose and create a new client for a new connection.");
        }

        _state = WebSocketState.Connecting;
        Socket socket;
        Stream transport;
        var compression = default(CompressionOptions);
        try
        {
            (socket, transport, compression) = await _handshake.ConnectAsync(uri, _options, ct).ConfigureAwait(false);
        }
        catch
        {
            _state = WebSocketState.Closed;
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
        _state = WebSocketState.Open;
        StartAutoPingLoopIfEnabled();

        _unsafeReceiveQueue = Channel.CreateUnbounded<DuLowAllocWebSocketReceiveResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });

        _unsafeReceivePumpTask = Task.Factory.StartNew(
            UnsafeReceivePump,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

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

    public async ValueTask CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_state is WebSocketState.CloseSent or WebSocketState.Closed)
        {
            return;
        }

        ReadOnlyMemory<byte> payload = BuildClosePayload(closeStatus, statusDescription);
        await SendFrameAsync(payload, WebSocketOpcode.Close, ct).ConfigureAwait(false);
        _closeSent = true;
        _state = _closeReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
    }

    public async ValueTask CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct = default)
    {
        EnsureConnected();
        await CloseOutputAsync(closeStatus, statusDescription, ct).ConfigureAwait(false);

        if (!_closeReceived)
        {
            await ReceiveCloseHandshakeAsync(ct).ConfigureAwait(false);
        }

        _state = WebSocketState.Closed;
        CloseTransport();
    }

    public ValueTask<DuLowAllocWebSocketReceiveResult> ReceiveAsync(CancellationToken ct)
    {
        EnsureConnected();
        if (Interlocked.Exchange(ref _receiveInProgress, 1) != 0)
        {
            throw new InvalidOperationException("Concurrent ReceiveAsync is not supported. Await the previous receive before calling again.");
        }

        return ReceiveFromPumpAsync(ct);
    }

    private async ValueTask<DuLowAllocWebSocketReceiveResult> ReceiveFromPumpAsync(CancellationToken ct)
    {
        try
        {
            if (_unsafeReceiveQueue is null)
            {
                throw new InvalidOperationException("Unsafe receive queue is not initialized.");
            }

            return await _unsafeReceiveQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _receiveInProgress, 0);
        }
    }

    private void UnsafeReceivePump()
    {
        Exception? terminalError = null;

        try
        {
            if (_unsafeReceiveQueue is null || _frameReader is null)
            {
                throw new InvalidOperationException("Unsafe receive pump initialization failed.");
            }

            bool insideFragmentedMessage = false;
            bool compressed = false;

            while (!_disposed && Volatile.Read(ref _closing) == 0)
            {
                _messageAssembler.Reset();
                insideFragmentedMessage = false;
                compressed = false;

                while (true)
                {
                    FrameHeader header = _frameReader.ReadHeader();
                    ValidateHeader(header, insideFragmentedMessage);

                    if (IsControlFrame(header.Opcode))
                    {
                        var controlResult = HandleControlFrameSync(header);
                        if (controlResult is { } close)
                        {
                            _unsafeReceiveQueue.Writer.TryWrite(close);
                            return;
                        }

                        continue;
                    }

                    if (!insideFragmentedMessage)
                    {
                        insideFragmentedMessage = true;
                        compressed = header.Rsv1;
                    }

                    _frameReader.ReadPayloadInto(header, _messageAssembler);

                    if (!header.Fin)
                    {
                        continue;
                    }

                    DuLowAllocWebSocketReceiveResult result;
                    if (!compressed)
                    {
                        result = new DuLowAllocWebSocketReceiveResult(_messageAssembler.WrittenMemory, header.Opcode);
                    }
                    else
                    {
                        if (_inflater is null)
                        {
                            throw new WebSocketProtocolException("RSV1 set but permessage-deflate was not negotiated.");
                        }

                        result = new DuLowAllocWebSocketReceiveResult(_inflater.Inflate(_messageAssembler.WrittenSpan), header.Opcode);
                    }

                    if (!_unsafeReceiveQueue.Writer.TryWrite(result))
                    {
                        throw new WebSocketProtocolException("Receive queue write failed.");
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            _unsafeReceiveQueue?.Writer.TryComplete(terminalError);
        }
    }

    private DuLowAllocWebSocketReceiveResult? HandleControlFrameSync(FrameHeader header)
    {
        if (!header.Fin)
        {
            throw new WebSocketProtocolException("Control frames must not be fragmented (RFC6455 5.5).");
        }

        _controlAssembler.Reset();
        _frameReader!.ReadPayloadInto(header, _controlAssembler);

        switch (header.Opcode)
        {
            case WebSocketOpcode.Ping:
                if (_options.AutoPongOnPing)
                {
                    SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Pong, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }

                return null;
            case WebSocketOpcode.Pong:
                return null;
            case WebSocketOpcode.Close:
                var closeResult = ParseCloseResult(_controlAssembler.WrittenSpan);
                _closeReceived = true;
                _state = _closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                if (!_closeSent)
                {
                    SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Close, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    _closeSent = true;
                    _state = WebSocketState.Closed;
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

    private static bool IsControlFrame(WebSocketOpcode opcode) => ((byte)opcode & 0x08) != 0;

    private async Task ReceiveCloseHandshakeAsync(CancellationToken ct)
    {
        while (!_closeReceived)
        {
            FrameHeader header = await _frameReader!.ReadHeaderAsync(ct).ConfigureAwait(false);
            if (!IsControlFrame(header.Opcode))
            {
                _messageAssembler.Reset();
                await _frameReader.ReadPayloadIntoAsync(header, _messageAssembler, ct).ConfigureAwait(false);
                continue;
            }

            if (!header.Fin)
            {
                throw new WebSocketProtocolException("Control frames must not be fragmented (RFC6455 5.5).");
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
                    _state = _closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                    if (!_closeSent)
                    {
                        await SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Close, ct).ConfigureAwait(false);
                        _closeSent = true;
                        _state = WebSocketState.Closed;
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

    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        _sendLock.Wait();
        try
        {
            if (_backgroundCts is not null)
            {
                _backgroundCts.Cancel();
                _backgroundCts.Dispose();
                _backgroundCts = null;
            }

            _autoPingTask = null;
            _unsafeReceiveQueue?.Writer.TryComplete();
            _unsafeReceiveQueue = null;
            _unsafeReceivePumpTask = null;
            _frameReader?.Dispose();
            _frameReader = null;
            _frameWriter?.Dispose();
            _frameWriter = null;
            _transport?.Dispose();
            _transport = null;
            _inflater?.Dispose();
            _inflater = null;
            _socket?.Dispose();
            _socket = null;

            if (_state != WebSocketState.Aborted)
            {
                _state = WebSocketState.Closed;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static void ValidateHeader(FrameHeader header, bool insideFragmentedMessage)
    {
        if (header.Rsv1 && (header.Opcode is WebSocketOpcode.Continuation or WebSocketOpcode.Ping or WebSocketOpcode.Pong or WebSocketOpcode.Close))
        {
            throw new WebSocketProtocolException("Invalid RSV1 usage for opcode.");
        }

        if (insideFragmentedMessage && header.Opcode != WebSocketOpcode.Continuation && !IsControlFrame(header.Opcode))
        {
            throw new WebSocketProtocolException("Expected continuation frame.");
        }

        if (!insideFragmentedMessage && header.Opcode == WebSocketOpcode.Continuation)
        {
            throw new WebSocketProtocolException("Unexpected continuation frame.");
        }
    }

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
        if (_state != WebSocketState.Open)
        {
            throw new InvalidOperationException($"Cannot send when WebSocketState is {_state}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DuLowAllocWebSocketClient));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state = WebSocketState.Closed;
        CloseTransport();
        _messageAssembler.Dispose();
        _controlAssembler.Dispose();
        _sendLock.Dispose();
    }
}
