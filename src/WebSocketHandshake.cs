using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DuLowAllocWebSocket;

/// <summary>
/// WebSocket 핸드셰이크(DNS → TCP → TLS → HTTP Upgrade)를 수행합니다 (RFC 6455 4절).
/// </summary>
public sealed class WebSocketHandshake
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

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
                // 기존 public 계약은 Stream만 반환하므로, 첫 프레임 바이트를 스트림 앞에 다시 붙여 보존합니다.
                transport = new PrebufferedStream(transport, initialBuffer!, initialOffset, initialCount);
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
        if (uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("Only ws:// and wss:// are supported.", nameof(uri));
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        if (options.SocketReceiveBufferSize is int rcvBuf)
        {
            socket.ReceiveBufferSize = rcvBuf;
        }

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
            await socket.ConnectAsync(connectHost, connectPort, ct).ConfigureAwait(false);

            Stream transport = new NetworkStream(socket, ownsSocket: false);
            if (options.ProxyHost is not null)
            {
                await EstablishProxyTunnelAsync(transport, targetHost, targetPort, options, ct).ConfigureAwait(false);
            }

            if (uri.Scheme == "wss")
            {
                if (OperatingSystem.IsLinux() && OpenSslStream.IsSupported)
                {
                    transport.Dispose();
                    transport = new OpenSslStream(socket.Handle.ToInt32(), uri.DnsSafeHost);
                }
                else
                {
                    var ssl = new SslStream(transport, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.DnsSafeHost,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    }, ct).ConfigureAwait(false);
                    transport = ssl;
                }
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
                    if (initialReadCount > 0)
                    {
                        byte[] initialReadBuffer = responseBuffer;
                        responseBuffer = null;
                        return new WebSocketHandshakeResult(socket, transport, compression, initialReadBuffer, headerLength, initialReadCount);
                    }

                    return new WebSocketHandshakeResult(socket, transport, compression);
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
        catch
        {
            socket.Dispose();
            throw;
        }
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
