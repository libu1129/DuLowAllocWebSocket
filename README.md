# DuLowAllocWebSocket (.NET 10)

Low-allocation raw-socket WebSocket client focused on predictable receive latency.

## Implemented components

- `WebSocketHandshake`: Manual RFC6455 HTTP Upgrade + `Sec-WebSocket-Accept` validation + `ws://` and `wss://` transport support.
- `FrameReader` / `FrameWriter`: Manual frame parse/write (FIN/RSV1/opcode/length/mask) with RFC fail-fast validation.
- `CompressionNegotiator`: Negotiates and parses `permessage-deflate` extension parameters.
- `DeflateInflater`: Reusable zlib-based raw-DEFLATE inflater (RFC7692 trailer append).
- `MessageAssembler`: Pooled message accumulation for fragmentation without `MemoryStream`.
- `RawWebSocketClient`: Public API (`ConnectAsync`, `SendAsync`, `ReceiveAsync`) with pooled-memory receive surface.
- `WebSocketClientOptions`: Upfront pre-allocation and policy knobs (HFT-oriented burst handling), including `EnablePerMessageDeflate`.

## Notes

- No `ClientWebSocket` is used; transport starts from raw `Socket` and upgrades to TLS stream for `wss://`.
- Receive path avoids per-message `byte[]`/`string` allocations in steady state.
- Initial large allocations are allowed/configurable to avoid runtime growth during bursts.
- Compression extension negotiation can be explicitly enabled/disabled via `EnablePerMessageDeflate`.
- You can configure RFC7692 knobs via `ClientContextTakeover`, `ServerContextTakeover`, `ClientMaxWindowBits`, and `ServerMaxWindowBits`.
- Optional HTTP proxy tunnel is supported via `ProxyHost`, `ProxyPort`, `ProxyUsername`, and `ProxyPassword`.
- RFC6455 ping/pong policy is configurable via `AutoPongOnPing`, `PingMode`, `ClientPingInterval`, and `ClientPingPayload` (manual or periodic client-priority ping).
- Payload returned from `ReceiveAsync` references pooled client-owned memory; consume/copy before next call.
- Native zlib loading is cross-platform: tries `zlib1.dll` (Windows), `libz.so.1`/`libz.so` (Linux), and `libz.dylib` (macOS).

## Build

```bash
dotnet build
```


## Sample (Binance Futures All Book Tickers)

```bash
dotnet run -- 'wss://fstream.binance.com/ws/!bookTicker'
```

- The sample receives all-symbol best bid/ask updates in real-time.
- It prints raw JSON payloads without deserialization.
