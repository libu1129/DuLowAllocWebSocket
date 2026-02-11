using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DuLowAllocWebSocket;

public sealed class WebSocketHandshake
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public async ValueTask<(Socket Socket, Stream Transport, CompressionOptions Compression)> ConnectAsync(
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

        try
        {
            if (options.EnablePerMessageDeflate && !DeflateInflater.IsSupported)
            {
                throw new InvalidOperationException(
                    "EnablePerMessageDeflate=true but native zlib is unavailable. Install zlib (Windows: zlib1.dll, Linux: libz.so.1) or disable permessage-deflate.");
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
                var ssl = new SslStream(transport, leaveInnerStreamOpen: true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.DnsSafeHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, ct).ConfigureAwait(false);
                transport = ssl;
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
            string request =
                $"GET {pathAndQuery} HTTP/1.1\r\n" +
                $"Host: {uri.Host}:{targetPort}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {secKey}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                BuildExtensionsHeader(options, compressionSupported) +
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
                    var (accepted, compression) = ValidateResponse(headerText, secKey, options, compressionSupported);
                    if (!accepted)
                    {
                        throw new WebSocketProtocolException("Server rejected WebSocket upgrade.");
                    }

                    return (socket, transport, compression);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(responseBuffer);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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

    private static (bool Accepted, CompressionOptions Compression) ValidateResponse(
        string responseHeaders,
        string secKey,
        WebSocketClientOptions options,
        bool compressionSupported)
    {
        string[] lines = responseHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase)) return (false, default);

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

        if (!string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)) return (false, default);
        if (connection is null || connection.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) < 0) return (false, default);
        if (accept is null) return (false, default);

        string expectedAccept = ComputeAccept(secKey);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedAccept), Encoding.ASCII.GetBytes(accept))) return (false, default);

        CompressionOptions compression = extensions is null
            ? new CompressionOptions(false, false, false, null, null)
            : CompressionNegotiator.ParseNegotiatedOptions(extensions.AsSpan());

        if ((!options.EnablePerMessageDeflate || !compressionSupported) && compression.Enabled)
        {
            return (false, default);
        }

        return (true, compression);
    }

    private static string ComputeAccept(string secKey)
    {
        Span<byte> hash = stackalloc byte[20];
        byte[] bytes = Encoding.ASCII.GetBytes(secKey + WsGuid);
        SHA1.HashData(bytes, hash);
        return Convert.ToBase64String(hash);
    }
}
