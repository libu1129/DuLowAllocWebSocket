# DuLowAllocWebSocket (.NET 10)

Low-allocation raw-socket WebSocket client focused on predictable receive latency.

## Implemented components

- `WebSocketHandshake`: Manual RFC6455 HTTP Upgrade + `Sec-WebSocket-Accept` validation + `ws://` and `wss://` transport support.
- `FrameReader` / `FrameWriter`: Manual frame parse/write (FIN/RSV1/opcode/length/mask) with RFC fail-fast validation.
- `CompressionNegotiator`: Negotiates and parses `permessage-deflate` extension parameters.
- `DeflateInflater`: Reusable zlib-based raw-DEFLATE inflater (RFC7692 trailer append).
- `MessageAssembler`: Pooled message accumulation for fragmentation without `MemoryStream`.
- `DuLowAllocWebSocketClient`: Public API (`State`, `ConnectAsync`, `SendAsync`, `SendPingAsync`, `CloseOutputAsync`, `CloseAsync`) with event-driven receive surface (`MessageReceived`).
- `WebSocketClientOptions`: Upfront pre-allocation and policy knobs (HFT-oriented burst handling), including `EnablePerMessageDeflate`.
- `OpenSslStream`: Linux-only OpenSSL P/Invoke TLS stream that bypasses `SslStream` internal allocations — achieves zero-heap receive on Linux `wss://`.

## Notes

- No `ClientWebSocket` is used; transport starts from raw `Socket` and upgrades to TLS stream for `wss://`. On Linux, `OpenSslStream` (direct OpenSSL P/Invoke via `SSL_read`/`SSL_write`) replaces `SslStream` to eliminate managed TLS layer allocations. On Windows, `SslStream` (SChannel) is used as-is since it is already allocation-free.
- Receive path is event-driven and avoids per-message `byte[]`/`string` allocations in steady state.
- Message receive callback path is designed for **0 heap allocations per received message** (excluding user callback logic and close-reason UTF-8 decode).
- Initial large allocations are allowed/configurable to avoid runtime growth during bursts.
- Compression extension negotiation can be explicitly enabled/disabled via `EnablePerMessageDeflate`.
- You can configure RFC7692 knobs via `ClientContextTakeover`, `ServerContextTakeover`, `ClientMaxWindowBits`, and `ServerMaxWindowBits`.
- Optional HTTP proxy tunnel is supported via `ProxyHost`, `ProxyPort`, `ProxyUsername`, and `ProxyPassword`.
- RFC6455 ping/pong policy is configurable via `AutoPongOnPing`, `KeepAliveInterval`, and `KeepAlivePingPayload` (when `KeepAliveInterval` is not `TimeSpan.Zero`, client sends periodic ping).
- Subscribe to `MessageReceived` and consume `DuLowAllocWebSocketReceiveResult`; when `IsClose` is false, `Payload` references pooled client-owned memory so consume/copy before the next callback message.
- `DuLowAllocWebSocketClient` is intended for single connection lifecycle; after close/disconnect, dispose and create a new instance for reconnect.
- Native zlib loading is cross-platform: tries `zlib1.dll` (Windows), `libz.so.1`/`libz.so` (Linux), and `libz.dylib` (macOS).
- Native OpenSSL loading for Linux TLS: tries `libssl.so.3`, `libssl.so.1.1`, `libssl.so`. Falls back to `SslStream` if unavailable.
- Windows and Linux zlib are expected to be provided by your environment/deployment (Windows: `zlib1.dll`, Linux: `libz.so.1`).
- For Windows manual setup, place `zlib1.dll` next to the app executable (for example `bin/Debug/net10.0/` or `bin/Release/net10.0/`).
- If `EnablePerMessageDeflate = true`, startup performs a native zlib validation check (`inflateInit2_`/`inflateEnd`) and fails fast with diagnostic details when invalid.
- To run fully without native zlib dependency, set `EnablePerMessageDeflate = false` (no compression).

## Build

```bash
dotnet build
```


## Sample (Binance Futures All Book Tickers)

```bash
dotnet run -- 'wss://fstream.binance.com/ws/!bookTicker'
```

```csharp
using var client = new DuLowAllocWebSocketClient(options);
using var cts = new CancellationTokenSource();

client.MessageReceived += result =>
{
    if (result.IsClose)
    {
        Console.WriteLine($"Close received: {result.CloseStatus} {result.CloseStatusDescription}");
        return;
    }

    // Keep this callback allocation-free if you want zero-heap receive behavior.
    // string json = Encoding.UTF8.GetString(result.Payload.Span); // optional debugging output
};

await client.ConnectAsync(uri, cts.Token);
```

- The sample receives all-symbol best bid/ask updates in real-time.
- It handles incoming data through `MessageReceived` (event-driven receive).
- Keep user callback logic allocation-free in steady state to preserve zero-heap receive behavior.

## Allocation Test

```bash
dotnet run -- --alloc-test
```

Measures per-message heap allocations on the receive thread. After 200 warmup messages, measures `GC.GetAllocatedBytesForCurrentThread()` over the next 1,000 messages and reports total/per-message bytes.
