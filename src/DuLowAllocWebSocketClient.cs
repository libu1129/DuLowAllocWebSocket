using System.Net.Sockets;

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

    public DuLowAllocWebSocketClient(WebSocketClientOptions? options = null)
    {
        _options = options ?? new WebSocketClientOptions();
        _messageAssembler = new MessageAssembler(_options.MessageBufferSize);
        _controlAssembler = new MessageAssembler(_options.ControlBufferSize);
    }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        var (socket, transport, compression) = await _handshake.ConnectAsync(uri, _options, ct).ConfigureAwait(false);
        _socket = socket;
        _transport = transport;
        _frameReader = new FrameReader(transport, _options);
        _frameWriter = new FrameWriter(transport, _options);

        if (compression.Enabled)
        {
            _inflater = new DeflateInflater(compression.ServerNoContextTakeover, _options.InflateOutputBufferSize);
        }

        StartAutoPingLoopIfEnabled();
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken ct = default)
    {
        EnsureConnected();
        return SendFrameAsync(payload, opcode, ct);
    }

    public ValueTask SendPingAsync(ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
    {
        EnsureConnected();
        if (payload.Length > 125)
        {
            throw new ArgumentException("Ping payload must be <= 125 bytes (RFC6455 5.5.2).", nameof(payload));
        }

        return SendFrameAsync(payload, WebSocketOpcode.Ping, ct);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct)
    {
        EnsureConnected();
        if (Interlocked.Exchange(ref _receiveInProgress, 1) != 0)
        {
            throw new InvalidOperationException("Concurrent ReceiveAsync is not supported. Await the previous receive before calling again.");
        }

        try
        {
            _messageAssembler.Reset();

            bool insideFragmentedMessage = false;
            bool compressed = false;

            while (true)
            {
                FrameHeader header = await _frameReader!.ReadHeaderAsync(ct).ConfigureAwait(false);
                ValidateHeader(header, insideFragmentedMessage);

                if (IsControlFrame(header.Opcode))
                {
                    await HandleControlFrameAsync(header, ct).ConfigureAwait(false);
                    continue;
                }

                if (!insideFragmentedMessage)
                {
                    insideFragmentedMessage = true;
                    compressed = header.Rsv1;
                }

                await _frameReader.ReadPayloadIntoAsync(header, _messageAssembler, ct).ConfigureAwait(false);

                if (!header.Fin)
                {
                    continue;
                }

                if (!compressed)
                {
                    return _messageAssembler.WrittenMemory;
                }

                if (_inflater is null)
                {
                    throw new WebSocketProtocolException("RSV1 set but permessage-deflate was not negotiated.");
                }

                return _inflater.Inflate(_messageAssembler.WrittenSpan);
            }
        }
        finally
        {
            Volatile.Write(ref _receiveInProgress, 0);
        }
    }

    private async ValueTask HandleControlFrameAsync(FrameHeader header, CancellationToken ct)
    {
        if (!header.Fin)
        {
            throw new WebSocketProtocolException("Control frames must not be fragmented (RFC6455 5.5).");
        }

        _controlAssembler.Reset();
        await _frameReader!.ReadPayloadIntoAsync(header, _controlAssembler, ct).ConfigureAwait(false);

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
                await SendFrameAsync(_controlAssembler.WrittenMemory, WebSocketOpcode.Close, ct).ConfigureAwait(false);
                _socket?.Dispose();
                throw new WebSocketProtocolException("Server requested close.");
            default:
                throw new WebSocketProtocolException($"Unexpected control opcode {header.Opcode}.");
        }
    }

    private void StartAutoPingLoopIfEnabled()
    {
        if (_options.PingMode != WebSocketPingMode.ClientDrivenAuto)
        {
            return;
        }

        if (_options.ClientPingInterval is null || _options.ClientPingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ClientDrivenAuto ping mode requires ClientPingInterval > 0.");
        }

        if (_options.ClientPingPayload.Length > 125)
        {
            throw new InvalidOperationException("ClientPingPayload must be <= 125 bytes.");
        }

        _backgroundCts = new CancellationTokenSource();
        _autoPingTask = AutoPingLoopAsync(_options.ClientPingInterval.Value, _backgroundCts.Token);
    }

    private async Task AutoPingLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SendPingAsync(_options.ClientPingPayload, ct).ConfigureAwait(false);
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
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _frameWriter!.SendAsync(payload, opcode, fin: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static bool IsControlFrame(WebSocketOpcode opcode) => ((byte)opcode & 0x08) != 0;

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
        if (_socket is null || _frameReader is null || _frameWriter is null || _transport is null)
        {
            throw new InvalidOperationException("Call ConnectAsync before send/receive.");
        }
    }

    public void Dispose()
    {
        if (_backgroundCts is not null)
        {
            _backgroundCts.Cancel();
            _backgroundCts.Dispose();
            _backgroundCts = null;
        }

        _autoPingTask = null;
        _frameReader?.Dispose();
        _frameWriter?.Dispose();
        _transport?.Dispose();
        _messageAssembler.Dispose();
        _controlAssembler.Dispose();
        _inflater?.Dispose();
        _socket?.Dispose();
        _sendLock.Dispose();
    }
}
