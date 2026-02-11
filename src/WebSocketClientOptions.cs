namespace DuLowAllocWebSocket;

public sealed class WebSocketClientOptions
{
    // Initial large allocations are intentionally allowed for HFT workloads
    // to reduce runtime growth/copy events under burst traffic.
    public int ReceiveScratchBufferSize { get; init; } = 64 * 1024;
    public int SendScratchBufferSize { get; init; } = 64 * 1024;
    public int MessageBufferSize { get; init; } = 256 * 1024;
    public int ControlBufferSize { get; init; } = 8 * 1024;
    public int InflateOutputBufferSize { get; init; } = 256 * 1024;
    public int HandshakeBufferSize { get; init; } = 16 * 1024;

    // Compression / extension policy.
    public bool EnablePerMessageDeflate { get; init; } = true;
    public bool ClientContextTakeover { get; init; } = true;
    public bool ServerContextTakeover { get; init; } = true;

    // RFC7692 window bits range: 8..15. null => omit parameter in extension offer.
    public int? ClientMaxWindowBits { get; init; } = 15;
    public int? ServerMaxWindowBits { get; init; } = 15;


    // Optional HTTP proxy (CONNECT tunnel).
    public string? ProxyHost { get; init; }
    public int? ProxyPort { get; init; }
    public string? ProxyUsername { get; init; }
    public string? ProxyPassword { get; init; }

    // RFC6455 ping/pong policy.
    public bool AutoPongOnPing { get; init; } = true;
    public WebSocketPingMode PingMode { get; init; } = WebSocketPingMode.ServerDriven;
    public TimeSpan? ClientPingInterval { get; init; }
    public ReadOnlyMemory<byte> ClientPingPayload { get; init; } = ReadOnlyMemory<byte>.Empty;

    // RFC6455 fail-fast policy.
    public bool RejectMaskedServerFrames { get; init; } = true;
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}
