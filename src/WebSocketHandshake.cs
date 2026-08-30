using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DuLowAllocWebSocket;

/// <summary>핸드셰이크의 소켓 생성과 단일 DNS/TCP 연결 경계를 추상화합니다.</summary>
internal interface IWebSocketSocketConnector
{
    Socket CreateSocket();

    ValueTask ConnectAsync(Socket socket, string host, int port, CancellationToken cancellationToken);
}

/// <summary>.NET runtime의 dual-mode socket 및 DnsEndPoint 연결 경로를 사용합니다.</summary>
internal sealed class DefaultWebSocketSocketConnector : IWebSocketSocketConnector
{
    internal static readonly DefaultWebSocketSocketConnector Instance = new();

    private DefaultWebSocketSocketConnector()
    {
    }

    public Socket CreateSocket() => new(SocketType.Stream, ProtocolType.Tcp);

    public ValueTask ConnectAsync(Socket socket, string host, int port, CancellationToken cancellationToken) =>
        socket.ConnectAsync(host, port, cancellationToken);
}

/// <summary>
/// WebSocket 핸드셰이크(DNS → TCP → TLS → HTTP Upgrade)를 수행합니다 (RFC 6455 4절).
/// </summary>
public sealed class WebSocketHandshake
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly IWebSocketSocketConnector _socketConnector;

    /// <summary>기본 런타임 DNS/TCP 연결기를 사용하는 핸드셰이크 인스턴스를 생성합니다.</summary>
    public WebSocketHandshake()
        : this(DefaultWebSocketSocketConnector.Instance)
    {
    }

    /// <summary>테스트에서 실제 연결 대상과 취소/소켓 소유권을 검증하기 위한 내부 생성자입니다.</summary>
    internal WebSocketHandshake(IWebSocketSocketConnector socketConnector)
    {
        ArgumentNullException.ThrowIfNull(socketConnector);
        _socketConnector = socketConnector;
    }

    /// <summary>
    /// WebSocket 서버에 연결하고 HTTP Upgrade 핸드셰이크를 완료합니다.
    /// </summary>
    /// <param name="uri">연결 대상 URI (ws:// 또는 wss://).</param>
    /// <param name="options">클라이언트 옵션 (버퍼, 압축, 프록시 등).</param>
    /// <param name="ct">취소 토큰.</param>
    /// <returns>연결된 소켓, 전송 스트림, 협상된 압축 옵션의 튜플.</returns>
    public async ValueTask<(Socket Socket, Stream Transport, CompressionOptions Compression)> ConnectAsync(
        Uri uri,
        WebSocketClientOptions options,
        CancellationToken ct)
    {
        var result = await ConnectWithInitialDataAsync(uri, options, ct).ConfigureAwait(false);
        try
        {
            Stream transport = result.Transport;
            if (result.TryDetachInitialReadBuffer(out byte[]? initialBuffer, out int initialOffset, out int initialCount))
            {
                if (initialCount > 0)
                {
                    // 기존 public 계약은 Stream만 반환하므로, 첫 프레임 바이트를 스트림 앞에 다시 붙여 보존합니다.
                    transport = new PrebufferedStream(transport, initialBuffer!, initialOffset, initialCount);
                }
                else
                {
                    // public Stream-only API는 FrameReader로 소유권을 넘길 수 없으므로 빈 버퍼를 즉시 반환합니다.
                    ArrayPool<byte>.Shared.Return(initialBuffer!);
                }
            }

            return (result.Socket, transport, result.Compression);
        }
        finally
        {
            result.Dispose();
        }
    }

    /// <summary>
    /// WebSocket 클라이언트가 핸드셰이크 직후 첫 프레임 바이트를 FrameReader scratch로 직접 넘길 수 있게 합니다.
    /// </summary>
    internal async ValueTask<WebSocketHandshakeResult> ConnectWithInitialDataAsync(
        Uri uri,
        WebSocketClientOptions options,
        CancellationToken ct)
    {
        int connectTimeoutMilliseconds = NormalizeConnectTimeoutMilliseconds(options.ConnectTimeout);
        if (connectTimeoutMilliseconds == 0)
        {
            return await ConnectCoreWithInitialDataAsync(uri, options, ct, 0).ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(connectTimeoutMilliseconds);
        try
        {
            return await ConnectCoreWithInitialDataAsync(
                uri,
                options,
                timeoutCts.Token,
                connectTimeoutMilliseconds).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"WebSocket connection did not complete within {connectTimeoutMilliseconds} ms.", ex);
        }
    }

    private async ValueTask<WebSocketHandshakeResult> ConnectCoreWithInitialDataAsync(
        Uri uri,
        WebSocketClientOptions options,
        CancellationToken ct,
        int connectTimeoutMilliseconds)
    {
        if (uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("Only ws:// and wss:// are supported.", nameof(uri));
        }

        var socket_send_timeout = NormalizeSocketSendTimeoutMilliseconds(options.SocketSendTimeout);
        var handshake_send_timeout = connectTimeoutMilliseconds > 0
            && (socket_send_timeout == 0 || connectTimeoutMilliseconds < socket_send_timeout)
                ? connectTimeoutMilliseconds
                : socket_send_timeout;
        // AddressFamily를 DNS 첫 결과로 고정하지 않는다. 이 생성자는 IPv6 지원 플랫폼에서
        // dual-mode socket을 만들어 runtime의 단일 DnsEndPoint 연결이 IPv4/IPv6를 모두 시도하게 한다.
        // 프록시 사용 시에는 connectHost가 프록시이므로 targetHost를 로컬에서 해석하지 않는다.
        var socket = _socketConnector.CreateSocket();
        socket.NoDelay = true;
        socket.SendTimeout = handshake_send_timeout;
        socket.ReceiveTimeout = connectTimeoutMilliseconds;
        using var cancellationRegistration = ct.Register(static state =>
        {
            try { ((Socket)state!).Shutdown(SocketShutdown.Both); }
            catch { }
        }, socket);

        if (options.SocketReceiveBufferSize is int rcvBuf)
        {
            socket.ReceiveBufferSize = rcvBuf;
        }

        Stream? transport = null;
        try
        {
            if (options.EnablePerMessageDeflate && !DeflateInflater.IsSupported)
            {
                throw new InvalidOperationException(
                    "EnablePerMessageDeflate=true but native zlib is unavailable. Install zlib (Windows: zlib1.dll, Linux: packaged libz.so.1, /opt/zlib-ng/lib/libz.so.1, or system libz.so.1) or disable permessage-deflate.");
            }

            if (options.EnablePerMessageDeflate && !DeflateInflater.TryValidateNativeZlib(out string? zlibError))
            {
                throw new InvalidOperationException(
                    $"EnablePerMessageDeflate=true but native zlib validation failed: {zlibError} " +
                    "Check architecture match (x64/x86), DLL placement, and zlib binary compatibility.");
            }

            bool compressionSupported = options.EnablePerMessageDeflate;

            int targetPort = uri.IsDefaultPort ? (uri.Scheme == "wss" ? 443 : 80) : uri.Port;
            string targetHost = uri.DnsSafeHost;

            string connectHost = options.ProxyHost ?? targetHost;
            int connectPort = options.ProxyHost is null ? targetPort : (options.ProxyPort ?? 8080);
            await _socketConnector.ConnectAsync(socket, connectHost, connectPort, ct).ConfigureAwait(false);

            var networkStream = new NetworkStream(socket, ownsSocket: false);
            // Linux Socket.Receive(Span) 동기 대기는 내부 operation/대기 객체를 반복 할당한다.
            // 수신 전용 스레드의 동기 경로만 native poll/recv로 우회하고, TLS handshake와 송신은
            // NetworkStream에 그대로 위임하여 SslStream의 one-read/one-write 동시성 계약을 유지한다.
            transport = OperatingSystem.IsLinux() && options.UseNativeLinuxSyncReceive
                ? new LinuxNativeSocketStream(socket, networkStream)
                : networkStream;
            if (options.ProxyHost is not null)
            {
                await EstablishProxyTunnelAsync(transport, targetHost, targetPort, options, ct).ConfigureAwait(false);
            }

            if (uri.Scheme == "wss")
            {
                // OpenSSL SSL*은 한 시점에 한 스레드만 사용할 수 있다. 이 client는 수신 전용
                // 스레드와 임의 송신 스레드가 full-duplex로 동작하므로 플랫폼 SslStream을 쓴다.
                var ssl = new SslStream(transport, leaveInnerStreamOpen: true);
                // 인증 실패 시 catch가 SslStream 자체도 Dispose할 수 있도록 소유권을 먼저 게시한다.
                transport = ssl;
                await ssl.AuthenticateAsClientAsync(
                    CreateSslClientAuthenticationOptions(targetHost),
                    ct).ConfigureAwait(false);
            }

            var keyBytes = ArrayPool<byte>.Shared.Rent(16);
            string secKey;
            try
            {
                RandomNumberGenerator.Fill(keyBytes.AsSpan(0, 16));
                secKey = Convert.ToBase64String(keyBytes, 0, 16);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(keyBytes);
            }

            var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            string userAgentHeader = HasCustomHeader(options, "User-Agent")
                ? ""
                : "User-Agent: DuLowAllocWebSocket/1.0\r\n";
            string request =
                $"GET {pathAndQuery} HTTP/1.1\r\n" +
                $"Host: {uri.Host}:{targetPort}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {secKey}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                userAgentHeader +
                BuildExtensionsHeader(options, compressionSupported) +
                BuildCustomHeaders(options) +
                "\r\n";

            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await transport.WriteAsync(requestBytes, ct).ConfigureAwait(false);

            byte[]? responseBuffer = ArrayPool<byte>.Shared.Rent(options.HandshakeBufferSize);
            try
            {
                int read = 0;
                while (true)
                {
                    if (read == responseBuffer.Length)
                    {
                        throw new WebSocketProtocolException("Handshake response exceeded configured buffer size.");
                    }

                    int n = await transport.ReadAsync(responseBuffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new WebSocketProtocolException("Connection closed during handshake.");
                    }

                    read += n;
                    if (!TryFindHeaderTerminator(responseBuffer.AsSpan(0, read), out int headerLength))
                    {
                        continue;
                    }

                    string headerText = Encoding.ASCII.GetString(responseBuffer, 0, headerLength);
                    var (accepted, compression, rejectReason) = ValidateResponse(headerText, secKey, options, compressionSupported);
                    if (!accepted)
                    {
                        // 에러 응답의 body도 읽어서 포함
                        string body = "";
                        int bodyInBuffer = read - headerLength;
                        int contentLength = ExtractContentLength(headerText);
                        if (contentLength > 0 && contentLength <= 1024)
                        {
                            int remaining = contentLength - bodyInBuffer;
                            if (remaining > 0 && headerLength + contentLength <= responseBuffer.Length)
                            {
                                int bodyRead = bodyInBuffer;
                                while (bodyRead < contentLength)
                                {
                                    int bn = await transport.ReadAsync(responseBuffer.AsMemory(headerLength + bodyRead, contentLength - bodyRead), ct).ConfigureAwait(false);
                                    if (bn == 0) break;
                                    bodyRead += bn;
                                }
                                body = Encoding.UTF8.GetString(responseBuffer, headerLength, bodyRead);
                            }
                            else
                            {
                                body = Encoding.UTF8.GetString(responseBuffer, headerLength, Math.Min(bodyInBuffer, contentLength));
                            }
                        }
                        else if (bodyInBuffer > 0)
                        {
                            body = Encoding.UTF8.GetString(responseBuffer, headerLength, bodyInBuffer);
                        }

                        string bodyInfo = string.IsNullOrEmpty(body) ? "" : $"\nBody: {body}";
                        throw new WebSocketProtocolException($"Server rejected WebSocket upgrade: {rejectReason}{bodyInfo}\nResponse:\n{headerText}");
                    }

                    // HTTP 헤더 뒤 남은 바이트는 이미 수신된 WebSocket 프레임입니다.
                    // 버리면 업그레이드 직후 첫 시세 메시지가 사라질 수 있습니다.
                    int initialReadCount = read - headerLength;
                    RestorePostHandshakeSocketTimeouts(socket, socket_send_timeout);
                    // 내부 클라이언트는 핸드셰이크 버퍼를 FrameReader의 작은 초기 scratch로 그대로 재사용한다.
                    // 첫 frame 바이트가 아직 없어도 버퍼 소유권을 넘겨 256KiB 기본 scratch의 별도 rent를 피한다.
                    byte[] initialReadBuffer = responseBuffer;
                    responseBuffer = null;
                    return new WebSocketHandshakeResult(socket, transport, compression, initialReadBuffer, headerLength, initialReadCount);
                }
            }
            finally
            {
                if (responseBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(responseBuffer);
                }
            }
        }
        catch (Exception ex)
        {
            // ConnectWithInitialDataAsync 성공 전에는 핸드셰이크가 transport 소유자다.
            try { transport?.Dispose(); } catch { }
            socket.Dispose();
            if (ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException("WebSocket connection was canceled.", ex, ct);
            }
            throw;
        }
    }

    internal static int NormalizeSocketSendTimeoutMilliseconds(TimeSpan? timeout)
    {
        if (timeout is null) return 0;
        if (timeout <= TimeSpan.Zero || timeout.Value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Socket send timeout must be positive and no greater than Int32.MaxValue milliseconds.");

        return Math.Max(1, (int)Math.Ceiling(timeout.Value.TotalMilliseconds));
    }

    /// <summary>프록시 경유 여부와 무관하게 원래 WebSocket target을 TLS SNI/인증 호스트로 사용합니다.</summary>
    internal static SslClientAuthenticationOptions CreateSslClientAuthenticationOptions(string targetHost) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
    };

    internal static int NormalizeConnectTimeoutMilliseconds(TimeSpan? timeout)
    {
        if (timeout is null) return 0;
        if (timeout <= TimeSpan.Zero || timeout.Value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Connect timeout must be positive and no greater than Int32.MaxValue milliseconds.");

        return Math.Max(1, (int)Math.Ceiling(timeout.Value.TotalMilliseconds));
    }

    private static void RestorePostHandshakeSocketTimeouts(Socket socket, int socketSendTimeoutMilliseconds)
    {
        socket.ReceiveTimeout = 0;
        socket.SendTimeout = socketSendTimeoutMilliseconds;
    }

    /// <summary>
    /// 핸드셰이크 결과와 HTTP 응답 뒤에 같이 읽힌 WebSocket 바이트의 소유권을 함께 보관합니다.
    /// </summary>
    internal sealed class WebSocketHandshakeResult : IDisposable
    {
        private byte[]? _initialReadBuffer;

        public WebSocketHandshakeResult(Socket socket, Stream transport, CompressionOptions compression)
        {
            Socket = socket;
            Transport = transport;
            Compression = compression;
        }

        public WebSocketHandshakeResult(
            Socket socket,
            Stream transport,
            CompressionOptions compression,
            byte[] initialReadBuffer,
            int initialReadOffset,
            int initialReadCount)
            : this(socket, transport, compression)
        {
            _initialReadBuffer = initialReadBuffer;
            InitialReadOffset = initialReadOffset;
            InitialReadCount = initialReadCount;
        }

        public Socket Socket { get; }

        public Stream Transport { get; }

        public CompressionOptions Compression { get; }

        public int InitialReadOffset { get; }

        public int InitialReadCount { get; }

        public ReadOnlySpan<byte> InitialReadSpan =>
            _initialReadBuffer is null ? ReadOnlySpan<byte>.Empty : _initialReadBuffer.AsSpan(InitialReadOffset, InitialReadCount);

        public bool TryDetachInitialReadBuffer(out byte[]? buffer, out int offset, out int count)
        {
            buffer = Interlocked.Exchange(ref _initialReadBuffer, null);
            offset = InitialReadOffset;
            count = buffer is null ? 0 : InitialReadCount;
            return buffer is not null;
        }

        public void Dispose()
        {
            byte[]? buffer = Interlocked.Exchange(ref _initialReadBuffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// public ConnectAsync 경로에서 기존 Stream 반환 계약을 유지하면서 초기 수신 바이트를 먼저 읽게 합니다.
    /// </summary>
    private sealed class PrebufferedStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _buffer;
        private int _offset;
        private int _count;

        public PrebufferedStream(Stream inner, byte[] buffer, int offset, int count)
        {
            _inner = inner;
            _buffer = buffer;
            _offset = offset;
            _count = count;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_count > 0)
            {
                int n = Math.Min(buffer.Length, _count);
                _buffer.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                _count -= n;
                ReturnBufferIfConsumed();
                return n;
            }

            return _inner.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_count > 0)
            {
                return Read(buffer.Span);
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            ReturnBufferIfConsumed(force: true);
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ReturnBufferIfConsumed(bool force = false)
        {
            if (_buffer is not null && (force || _count == 0))
            {
                byte[] buffer = _buffer;
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }


    private static bool HasCustomHeader(WebSocketClientOptions options, string headerName)
    {
        if (options.CustomHeaders is not { Count: > 0 })
            return false;

        foreach (var key in options.CustomHeaders.Keys)
        {
            if (key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildCustomHeaders(WebSocketClientOptions options)
    {
        if (options.CustomHeaders is not { Count: > 0 })
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (key, value) in options.CustomHeaders)
        {
            sb.Append(key).Append(": ").Append(value).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string BuildExtensionsHeader(WebSocketClientOptions options, bool compressionSupported)
    {
        if (!compressionSupported)
        {
            return string.Empty;
        }

        ValidateCompressionOfferOptions(options);
        return $"Sec-WebSocket-Extensions: {CompressionNegotiator.BuildClientOfferHeader(options)}\r\n";
    }


    private static void ValidateCompressionOfferOptions(WebSocketClientOptions options)
    {
        ValidateWindowBits(options.ClientMaxWindowBits, nameof(options.ClientMaxWindowBits));
        ValidateWindowBits(options.ServerMaxWindowBits, nameof(options.ServerMaxWindowBits));
    }

    private static void ValidateWindowBits(int? bits, string name)
    {
        if (bits is null)
        {
            return;
        }

        if (bits < 8 || bits > 15)
        {
            throw new ArgumentOutOfRangeException(name, bits, "RFC7692 window bits must be in range 8..15.");
        }
    }

    private static async ValueTask EstablishProxyTunnelAsync(Stream transport, string targetHost, int targetPort, WebSocketClientOptions options, CancellationToken ct)
    {
        if (options.ProxyPort is not null && (options.ProxyPort < 1 || options.ProxyPort > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProxyPort), options.ProxyPort, "ProxyPort must be in range 1..65535.");
        }

        string request =
            $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n" +
            $"Host: {targetHost}:{targetPort}\r\n" +
            "Proxy-Connection: Keep-Alive\r\n" +
            BuildProxyAuthorizationHeader(options) +
            "\r\n";

        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await transport.WriteAsync(requestBytes, ct).ConfigureAwait(false);

        byte[] responseBuffer = ArrayPool<byte>.Shared.Rent(options.HandshakeBufferSize);
        try
        {
            int read = 0;
            while (true)
            {
                if (read == responseBuffer.Length)
                {
                    throw new WebSocketProtocolException("Proxy CONNECT response exceeded configured handshake buffer size.");
                }

                int n = await transport.ReadAsync(responseBuffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new WebSocketProtocolException("Connection closed during proxy CONNECT.");
                }

                read += n;
                if (!TryFindHeaderTerminator(responseBuffer.AsSpan(0, read), out int headerLength))
                {
                    continue;
                }

                string statusLine = Encoding.ASCII.GetString(responseBuffer, 0, headerLength).Split("\r\n", 2)[0];
                if (!statusLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !statusLine.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WebSocketProtocolException("Proxy CONNECT failed: " + statusLine);
                }

                return;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(responseBuffer);
        }
    }

    private static int ExtractContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.AsSpan(15).Trim();
                if (int.TryParse(value, out int cl)) return cl;
            }
        }
        return -1;
    }

    private static string BuildProxyAuthorizationHeader(WebSocketClientOptions options)
    {
        if (string.IsNullOrEmpty(options.ProxyUsername))
        {
            return string.Empty;
        }

        string userPass = $"{options.ProxyUsername}:{options.ProxyPassword ?? string.Empty}";
        string token = Convert.ToBase64String(Encoding.ASCII.GetBytes(userPass));
        return $"Proxy-Authorization: Basic {token}\r\n";
    }

    private static bool TryFindHeaderTerminator(ReadOnlySpan<byte> data, out int headerLength)
    {
        for (int i = 3; i < data.Length; i++)
        {
            if (data[i - 3] == (byte)'\r' && data[i - 2] == (byte)'\n' && data[i - 1] == (byte)'\r' && data[i] == (byte)'\n')
            {
                headerLength = i + 1;
                return true;
            }
        }

        headerLength = 0;
        return false;
    }

    private static (bool Accepted, CompressionOptions Compression, string? RejectReason) ValidateResponse(
        string responseHeaders,
        string secKey,
        WebSocketClientOptions options,
        bool compressionSupported)
    {
        string[] lines = responseHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
            return (false, default, $"Expected HTTP/1.1 101, got: {(lines.Length > 0 ? lines[0] : "(empty)")}");

        string? accept = null;
        string? connection = null;
        string? upgrade = null;
        string? extensions = null;

        for (int i = 1; i < lines.Length; i++)
        {
            int sep = lines[i].IndexOf(':');
            if (sep <= 0) continue;

            var name = lines[i].AsSpan(0, sep).Trim();
            var value = lines[i].AsSpan(sep + 1).Trim();

            if (name.Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase)) accept = value.ToString();
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) connection = value.ToString();
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) upgrade = value.ToString();
            else if (name.Equals("Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase)) extensions = value.ToString();
        }

        if (!string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase))
            return (false, default, $"Missing or invalid Upgrade header: '{upgrade}'");
        if (connection is null || connection.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) < 0)
            return (false, default, $"Missing or invalid Connection header: '{connection}'");
        if (accept is null)
            return (false, default, "Missing Sec-WebSocket-Accept header");

        string expectedAccept = ComputeAccept(secKey);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedAccept), Encoding.ASCII.GetBytes(accept)))
            return (false, default, "Sec-WebSocket-Accept mismatch");

        CompressionOptions compression = extensions is null
            ? new CompressionOptions(false, false, false, null, null)
            : CompressionNegotiator.ParseNegotiatedOptions(extensions.AsSpan());

        if ((!options.EnablePerMessageDeflate || !compressionSupported) && compression.Enabled)
        {
            return (false, default, "Server enabled compression but client did not request it");
        }

        return (true, compression, null);
    }

    private static string ComputeAccept(string secKey)
    {
        Span<byte> hash = stackalloc byte[20];
        byte[] bytes = Encoding.ASCII.GetBytes(secKey + WsGuid);
        SHA1.HashData(bytes, hash);
        return Convert.ToBase64String(hash);
    }
}
