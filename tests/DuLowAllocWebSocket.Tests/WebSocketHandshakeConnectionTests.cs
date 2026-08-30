using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class WebSocketHandshakeConnectionTests
{
    [Fact]
    public async Task ConnectAsync_DirectHost_UsesOnlyTheTargetConnectorOnce()
    {
        using var listener = StartListener(IPAddress.Loopback, out int port);
        Task<string> serverTask = ServeUpgradeAsync(listener);
        var connector = new RecordingSocketConnector(IPAddress.Loopback);
        var handshake = new WebSocketHandshake(connector);
        var uri = new Uri($"ws://target-does-not-resolve.invalid:{port}/feed?depth=1");

        var result = await handshake.ConnectAsync(uri, CreateOptions(), CancellationToken.None);
        using (result.Transport)
        using (result.Socket)
        {
            string request = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("GET /feed?depth=1 HTTP/1.1\r\n", request, StringComparison.Ordinal);
            Assert.Contains($"Host: target-does-not-resolve.invalid:{port}\r\n", request, StringComparison.Ordinal);
        }

        Assert.Equal(1, connector.ConnectCount);
        Assert.Equal(("target-does-not-resolve.invalid", port), connector.LastTarget);
    }

    [Fact]
    public async Task ConnectAsync_Proxy_DoesNotResolveTargetLocallyAndPreservesConnectHostname()
    {
        using var proxy = StartListener(IPAddress.Loopback, out int proxyPort);
        Task<(string Connect, string Upgrade)> proxyTask = ServeProxyUpgradeAsync(proxy);
        var connector = new RecordingSocketConnector(IPAddress.Loopback);
        var handshake = new WebSocketHandshake(connector);
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseNativeLinuxSyncReceive = false,
            ProxyHost = "proxy-does-not-resolve.invalid",
            ProxyPort = proxyPort,
        };
        var uri = new Uri("ws://target-only-resolves-inside-proxy.invalid:4567/private?channel=orders");

        var result = await handshake.ConnectAsync(uri, options, CancellationToken.None);
        using (result.Transport)
        using (result.Socket)
        {
            var requests = await proxyTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith(
                "CONNECT target-only-resolves-inside-proxy.invalid:4567 HTTP/1.1\r\n",
                requests.Connect,
                StringComparison.Ordinal);
            Assert.Contains(
                "Host: target-only-resolves-inside-proxy.invalid:4567\r\n",
                requests.Connect,
                StringComparison.Ordinal);
            Assert.StartsWith(
                "GET /private?channel=orders HTTP/1.1\r\n",
                requests.Upgrade,
                StringComparison.Ordinal);
            Assert.Contains(
                "Host: target-only-resolves-inside-proxy.invalid:4567\r\n",
                requests.Upgrade,
                StringComparison.Ordinal);
        }

        Assert.Equal(1, connector.ConnectCount);
        Assert.Equal(("proxy-does-not-resolve.invalid", proxyPort), connector.LastTarget);
    }

    [Fact]
    public void DefaultConnector_CreatesDualModeSocketWhenIpv6IsAvailable()
    {
        using Socket socket = DefaultWebSocketSocketConnector.Instance.CreateSocket();

        if (Socket.OSSupportsIPv6)
        {
            Assert.Equal(AddressFamily.InterNetworkV6, socket.AddressFamily);
            Assert.True(socket.DualMode);
        }
        else
        {
            Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        }
    }

    [Fact]
    public async Task ConnectAsync_DefaultConnector_AcceptsIpv4AndIpv6LiteralTargets()
    {
        await ConnectLoopbackAsync(IPAddress.Loopback);

        if (Socket.OSSupportsIPv6)
        {
            await ConnectLoopbackAsync(IPAddress.IPv6Loopback);
        }
    }

    [Fact]
    public async Task ConnectAsync_DefaultConnector_ResolvesLocalhostAndFallsBackToIpv4Listener()
    {
        // The listener is intentionally IPv4-only. On hosts where localhost resolves to ::1 first,
        // this verifies the runtime DnsEndPoint path continues to the IPv4 address.
        using var listener = StartListener(IPAddress.Loopback, out int port);
        Task<string> serverTask = ServeUpgradeAsync(listener);
        var handshake = new WebSocketHandshake();

        var result = await handshake.ConnectAsync(
            new Uri($"ws://localhost:{port}/feed"),
            CreateOptions(),
            CancellationToken.None);
        using (result.Transport)
        using (result.Socket)
        {
            string request = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains($"Host: localhost:{port}\r\n", request, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectorIsCanceled_DisposesCreatedSocket()
    {
        var connector = new BlockingSocketConnector();
        var handshake = new WebSocketHandshake(connector);
        using var cancellation = new CancellationTokenSource();
        Task connectTask = handshake.ConnectAsync(
            new Uri("ws://cancel-target.invalid/feed"),
            new WebSocketClientOptions
            {
                EnablePerMessageDeflate = false,
                ConnectTimeout = null,
                UseNativeLinuxSyncReceive = false,
            },
            cancellation.Token).AsTask();

        await connector.ConnectEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
        Assert.Equal(1, connector.ConnectCount);
        Assert.Equal(("cancel-target.invalid", 80), connector.LastTarget);
        Assert.NotNull(connector.CreatedSocket);
        Assert.True(connector.CreatedSocket!.SafeHandle.IsClosed);
    }

    [Fact]
    public void TlsOptions_UseOriginalTargetForSniRatherThanProxyHost()
    {
        SslClientAuthenticationOptions options =
            WebSocketHandshake.CreateSslClientAuthenticationOptions("target-only-resolves-inside-proxy.invalid");

        Assert.Equal("target-only-resolves-inside-proxy.invalid", options.TargetHost);
    }

    private static WebSocketClientOptions CreateOptions() => new()
    {
        EnablePerMessageDeflate = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        UseNativeLinuxSyncReceive = false,
    };

    private static async Task ConnectLoopbackAsync(IPAddress address)
    {
        using var listener = StartListener(address, out int port);
        Task<string> serverTask = ServeUpgradeAsync(listener);
        string host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        var handshake = new WebSocketHandshake();

        var result = await handshake.ConnectAsync(
            new Uri($"ws://{host}:{port}/feed"),
            CreateOptions(),
            CancellationToken.None);
        using (result.Transport)
        using (result.Socket)
        {
            _ = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static TcpListener StartListener(IPAddress address, out int port)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task<string> ServeUpgradeAsync(TcpListener listener)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = accepted.GetStream();
        string request = await ReadHttpRequestAsync(stream);
        await WriteUpgradeResponseAsync(stream, request);
        return request;
    }

    private static async Task<(string Connect, string Upgrade)> ServeProxyUpgradeAsync(TcpListener listener)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = accepted.GetStream();

        string connectRequest = await ReadHttpRequestAsync(stream);
        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
        await stream.FlushAsync();

        string upgradeRequest = await ReadHttpRequestAsync(stream);
        await WriteUpgradeResponseAsync(stream, upgradeRequest);
        return (connectRequest, upgradeRequest);
    }

    private static async Task WriteUpgradeResponseAsync(NetworkStream stream, string request)
    {
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read));
            if (count == 0)
            {
                throw new IOException("Connection closed before the HTTP request completed.");
            }

            read += count;
            if (buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8) >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
        }

        throw new InvalidOperationException("HTTP request exceeded the test buffer.");
    }

    private static string ReadHeader(string request, string name)
    {
        foreach (string line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator > 0 &&
                line.AsSpan(0, separator).Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        throw new InvalidOperationException($"Missing {name} header.");
    }

    private static string ComputeAccept(string key)
    {
        byte[] source = Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(source, hash);
        return Convert.ToBase64String(hash);
    }

    private sealed class RecordingSocketConnector(IPAddress connectAddress) : IWebSocketSocketConnector
    {
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal (string Host, int Port) LastTarget { get; private set; }

        public Socket CreateSocket() => DefaultWebSocketSocketConnector.Instance.CreateSocket();

        public async ValueTask ConnectAsync(
            Socket socket,
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            LastTarget = (host, port);
            Interlocked.Increment(ref _connectCount);
            await socket.ConnectAsync(new IPEndPoint(connectAddress, port), cancellationToken);
        }
    }

    private sealed class BlockingSocketConnector : IWebSocketSocketConnector
    {
        private readonly TaskCompletionSource _connectEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal Task ConnectEntered => _connectEntered.Task;
        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal (string Host, int Port) LastTarget { get; private set; }
        internal Socket? CreatedSocket { get; private set; }

        public Socket CreateSocket()
        {
            CreatedSocket = DefaultWebSocketSocketConnector.Instance.CreateSocket();
            return CreatedSocket;
        }

        public async ValueTask ConnectAsync(
            Socket socket,
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            LastTarget = (host, port);
            Interlocked.Increment(ref _connectCount);
            _connectEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
