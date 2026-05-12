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
}
