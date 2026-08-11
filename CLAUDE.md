# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DuLowAllocWebSocket is a low-allocation, raw-socket WebSocket client library for .NET 10, designed for zero-heap-allocation message reception in steady state. It targets latency-sensitive use cases (e.g., HFT market data feeds). No `ClientWebSocket` is used — transport starts from raw `Socket` with manual TLS upgrade.

## Build & Run

```bash
dotnet build
dotnet run -- 'wss://fstream.binance.com/ws/!bookTicker'   # sample app
```

Requires .NET 10 SDK. No test framework or linter is configured.

## Architecture

### Receive Path (zero-allocation critical path)

Messages are delivered via `MessageReceived` event on a dedicated background thread that synchronously reads frames. The chain is:

```
Socket/SslStream → FrameReader (parse frame header + payload)
                 → DeflateInflater (optional RFC7692 decompression via native zlib P/Invoke)
                 → MessageAssembler (pool-backed fragmentation reassembly)
                 → MessageReceived event → DuLowAllocWebSocketReceiveResult (readonly struct, references pooled memory)
```

`Payload` in the result references client-owned pooled memory — it must be consumed/copied before the callback returns.

### Send Path

`FrameWriter` serializes frames with client-to-server masking (RFC6455 requirement). Concurrent sends are serialized via `SemaphoreSlim`.


### TLS Transport

- **All platforms**: `SslStream`. The client deliberately performs one receive and one send concurrently.
- **Linux sync receive**: `LinuxNativeSocketStream` keeps TLS in `SslStream`, delegates handshake/async I/O/writes to `NetworkStream`, and uses native `recv` plus `poll` only for the dedicated synchronous reader. Preserve the teardown order `Socket.Shutdown` → receive-thread join → `Socket.Dispose`; it is the fd-lifetime contract.
- `OpenSslStream` remains dormant reference code and must not be connected to this full-duplex client: an OpenSSL `SSL*` may only be used by one thread at a time. Re-enabling it requires a non-blocking, single-owner I/O design.

### Connection Lifecycle

`DuLowAllocWebSocketClient` is single-use: connect → communicate → close → dispose. Reconnection requires a new instance. `WebSocketHandshake` handles DNS resolution, TCP connect, TLS negotiation, and HTTP Upgrade with `Sec-WebSocket-Accept` validation.

### Key Design Decisions

- **All buffers pre-allocated at connect time** via `WebSocketClientOptions` — no runtime growth during steady state. `ArrayPool<byte>.Shared` is used throughout (`FrameReader`, `FrameWriter`, `MessageAssembler`, `DeflateInflater`).
- **Native zlib interop** for permessage-deflate: P/Invoke to platform-specific libraries (`zlib1.dll` / `libz.so.1` / `libz.dylib`). Validated at connect time with fail-fast diagnostics.
- **TLS concurrency correctness**: `SslStream` is used on Linux and Windows so the dedicated receive thread and sender thread do not concurrently enter one native OpenSSL `SSL*`.
- **Linux zero-allocation blocking wait**: the dedicated reader bypasses .NET's synchronous `SocketAsyncContext` wait with native `recv`/`poll`; never extend this to a second concurrent reader.
- **Dedicated receive thread** (not async) to avoid Task/async state machine allocations.
- **Frame misalignment diagnostics**: `ValidateHeader` includes raw header bytes, previous frame info, and `FrameReader` buffer state in error messages. `WebSocketProtocolException.IsSuspectedMisalignment` distinguishes protocol violations from network-disconnect-induced misalignment.
- **Default User-Agent header**: `WebSocketHandshake` sends `User-Agent: DuLowAllocWebSocket/1.0` unless overridden via `CustomHeaders`, preventing Cloudflare WAF 403 blocks.
- **Korean-language XML docs** on `WebSocketClientOptions` properties — preserve this convention when adding new options.

### Source Layout

- `src/` — all library code (single project, no NuGet dependencies beyond BCL)
- `samples/DuLowAllocWebSocket.Sample/` — Binance Futures stream example + allocation test (`--alloc-test`)
