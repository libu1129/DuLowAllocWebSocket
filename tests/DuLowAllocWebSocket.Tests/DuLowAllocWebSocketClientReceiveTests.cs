using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class DuLowAllocWebSocketClientReceiveTests
{
    [Fact]
    public async Task ConnectAsync_WhenFirstFrameArrivesWithHandshake_DeliversFirstMessage()
    {
        byte[] expected = Encoding.UTF8.GetBytes("first");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: true,
            BuildFrame(WebSocketOpcode.Text, expected));

        using var client = CreateClient();
        var received = new TaskCompletionSource<(WebSocketOpcode Opcode, byte[] Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult((result.Opcode, result.Payload.ToArray()));
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Text, result.Opcode);
        Assert.Equal(expected, result.Payload);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocketHandshake_WhenFirstFrameArrivesWithHandshake_PreservesInitialFrameInTransport()
    {
        byte[] expected = Encoding.UTF8.GetBytes("first");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: true,
            BuildFrame(WebSocketOpcode.Text, expected));

        var options = CreateOptions();
        var handshake = new WebSocketHandshake();
        var result = await handshake.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), options, CancellationToken.None);

        using var socket = result.Socket;
        using var transport = result.Transport;
        using var reader = new FrameReader(transport, options);

        FrameHeader header = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Text, header.Opcode);
        Assert.Equal(expected, ReadPayload(reader, header));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenTextMessageIsFragmented_PreservesTextOpcode()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: false,
            BuildFrame(WebSocketOpcode.Text, Encoding.UTF8.GetBytes("hel"), fin: false),
            BuildFrame(WebSocketOpcode.Continuation, Encoding.UTF8.GetBytes("lo")));

        using var client = CreateClient();
        var received = new TaskCompletionSource<(WebSocketOpcode Opcode, byte[] Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult((result.Opcode, result.Payload.ToArray()));
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Text, result.Opcode);
        Assert.Equal("hello", Encoding.UTF8.GetString(result.Payload));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AutoPong_WhenNoThrottle_SendsPongWithSamePayload()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("plain-ping");

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingAndReadPongAsync(listener, pingPayload);

        using var client = CreateClient();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
    }

    [Fact]
    public async Task AutoPong_WhenControlFrameThrottleWaits_DeliversNextMessageBeforePong()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("p1");
        byte[] textPayload = Encoding.UTF8.GetBytes("after-ping");
        var throttle = new BlockingControlFrameThrottle();

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingThenTextAndReadPongAsync(listener, pingPayload, textPayload);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = throttle
        };

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult(result.Payload.ToArray());
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        Assert.Equal(WebSocketOpcode.Pong, await throttle.Entered.WaitAsync(TimeSpan.FromSeconds(5)));
        byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(textPayload, result);
        Assert.False(serverTask.IsCompleted);

        throttle.Release();
        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
    }

    [Fact]
    public async Task SendPongAsync_WhenControlFrameThrottleWaits_DelaysPongUntilReleased()
    {
        byte[] pongPayload = Encoding.UTF8.GetBytes("manual-pong");
        var throttle = new BlockingControlFrameThrottle();

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask = ServeAndReadClientFrameAsync(listener);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = throttle
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        Task sendTask = client.SendPongAsync(pongPayload).AsTask();

        Assert.Equal(WebSocketOpcode.Pong, await throttle.Entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(serverTask.IsCompleted);

        throttle.Release();
        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pongPayload, pong.Payload);
    }

    [Fact]
    public async Task ConnectAsync_WhenAutoPongQueueCapacityIsInvalid_FailsBeforeOpeningConnection()
    {
        using var listener = StartListener(out int port);
        using var client = new DuLowAllocWebSocketClient(new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
            AutoPongQueueCapacity = 0,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None));

        Assert.Contains("AutoPongQueueCapacity", ex.Message);
        Assert.Equal(WebSocketState.None, client.State);
        Assert.False(listener.Pending());
    }

    private static DuLowAllocWebSocketClient CreateClient() => new(CreateOptions());

    private static WebSocketClientOptions CreateOptions() => new()
    {
        EnablePerMessageDeflate = false,
        KeepAliveInterval = TimeSpan.Zero,
        ReceiveScratchBufferSize = 64,
    };

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task ServeWebSocketAsync(
        TcpListener listener,
        bool appendFramesToHandshake,
        params byte[][] frames)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        byte[] payload = Concat(frames);
        if (appendFramesToHandshake)
        {
            await stream.WriteAsync(Concat(response, payload));
        }
        else
        {
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            await Task.Delay(50);
            await stream.WriteAsync(payload);
        }

        await stream.FlushAsync();
        await Task.Delay(200);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServePingAndReadPongAsync(
        TcpListener listener,
        byte[] pingPayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, pingPayload));
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServePingThenTextAndReadPongAsync(
        TcpListener listener,
        byte[] pingPayload,
        byte[] textPayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(Concat(
            BuildFrame(WebSocketOpcode.Ping, pingPayload),
            BuildFrame(WebSocketOpcode.Text, textPayload)));
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServeAndReadClientFrameAsync(
        TcpListener listener)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
            {
                throw new IOException("Client closed before handshake request completed.");
            }

            read += n;
            if (ContainsHeaderTerminator(buffer.AsSpan(0, read)))
            {
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
        }

        throw new InvalidOperationException("Handshake request exceeded test buffer.");
    }

    private static bool ContainsHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (int i = 3; i < data.Length; i++)
        {
            if (data[i - 3] == (byte)'\r' &&
                data[i - 2] == (byte)'\n' &&
                data[i - 1] == (byte)'\r' &&
                data[i] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadHeader(string request, string headerName)
    {
        foreach (string line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator > 0 &&
                line.AsSpan(0, separator).Equals(headerName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        throw new InvalidOperationException($"Missing {headerName} header.");
    }

    private static string ComputeAccept(string secKey)
    {
        byte[] input = Encoding.ASCII.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        return Convert.ToBase64String(hash);
    }

    private static byte[] BuildFrame(WebSocketOpcode opcode, ReadOnlySpan<byte> payload, bool fin = true)
    {
        if (payload.Length > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] frame = new byte[2 + payload.Length];
        frame[0] = (byte)((fin ? 0b1000_0000 : 0) | ((byte)opcode & 0x0F));
        frame[1] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(2));
        return frame;
    }

    private static byte[] ReadPayload(FrameReader reader, FrameHeader header)
    {
        using var assembler = new MessageAssembler(initialCapacity: 16, maxMessageBytes: 1024);
        reader.ReadPayloadInto(header, assembler);
        return assembler.WrittenMemory.ToArray();
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ReadClientFrameAsync(NetworkStream stream)
    {
        byte[] header = new byte[2];
        await ReadExactAsync(stream, header);

        var opcode = (WebSocketOpcode)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        Assert.True(masked);

        ulong length = (ulong)(header[1] & 0x7F);
        if (length == 126)
        {
            byte[] ext = new byte[2];
            await ReadExactAsync(stream, ext);
            length = (ulong)((ext[0] << 8) | ext[1]);
        }
        else if (length == 127)
        {
            byte[] ext = new byte[8];
            await ReadExactAsync(stream, ext);
            length = 0;
            foreach (byte b in ext)
            {
                length = (length << 8) | b;
            }
        }

        if (length > int.MaxValue)
        {
            throw new InvalidOperationException("Client frame is too large for this test.");
        }

        byte[] mask = new byte[4];
        await ReadExactAsync(stream, mask);

        byte[] payload = new byte[(int)length];
        if (payload.Length > 0)
        {
            await ReadExactAsync(stream, payload);
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i & 3];
            }
        }

        return (opcode, payload);
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..]);
            if (n == 0)
            {
                throw new IOException("Stream ended before the expected bytes were read.");
            }

            read += n;
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private sealed class BlockingControlFrameThrottle : IWebSocketControlFrameThrottle
    {
        private readonly TaskCompletionSource<WebSocketOpcode> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WebSocketOpcode> Entered => _entered.Task;

        public async ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken)
        {
            _entered.TrySetResult(opcode);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }
}
