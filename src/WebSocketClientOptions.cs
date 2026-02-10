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

    // RFC6455 fail-fast policy.
    public bool RejectMaskedServerFrames { get; init; } = true;
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}
