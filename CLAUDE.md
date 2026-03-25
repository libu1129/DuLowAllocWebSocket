# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DuLowAllocWebSocket is a low-allocation, raw-socket WebSocket client library for .NET 11, designed for zero-heap-allocation message reception in steady state. It targets latency-sensitive use cases (e.g., HFT market data feeds). No `ClientWebSocket` is used — transport starts from raw `Socket` with manual TLS upgrade.

## Build & Run

```bash
dotnet build
dotnet run -- 'wss://fstream.binance.com/ws/!bookTicker'   # sample app
```

Requires .NET 11 preview SDK. No test framework or linter is configured.

## Architecture

### Receive Path (zero-allocation critical path)

Messages are delivered via `MessageReceived` event on a dedicated background thread that synchronously reads frames. The chain is:

```
Socket/SslStream/OpenSslStream → FrameReader (parse frame header + payload)
                 → DeflateInflater (optional RFC7692 decompression via native zlib P/Invoke)
                 → MessageAssembler (pool-backed fragmentation reassembly)
                 → MessageReceived event → DuLowAllocWebSocketReceiveResult (readonly struct, references pooled memory)
```

`Payload` in the result references client-owned pooled memory — it must be consumed/copied before the callback returns.

### Send Path

`FrameWriter` serializes frames with client-to-server masking (RFC6455 requirement). Concurrent sends are serialized via `SemaphoreSlim`.


### TLS Transport (platform-specific)

- **Windows**: `SslStream` (SChannel) — already zero-allocation on receive.
- **Linux**: `OpenSslStream` — direct OpenSSL P/Invoke (`SSL_read`/`SSL_write` into pre-allocated buffers), bypassing `SslStream`'s managed layer allocations. Uses `dup(fd)` for independent fd lifecycle and `fcntl` to set blocking mode after async connect. Falls back to `SslStream` if libssl is unavailable.

### Connection Lifecycle

`DuLowAllocWebSocketClient` is single-use: connect → communicate → close → dispose. Reconnection requires a new instance. `WebSocketHandshake` handles DNS resolution, TCP connect, TLS negotiation, and HTTP Upgrade with `Sec-WebSocket-Accept` validation.

### Key Design Decisions

- **All buffers pre-allocated at connect time** via `WebSocketClientOptions` — no runtime growth during steady state. `ArrayPool<byte>.Shared` is used throughout (`FrameReader`, `FrameWriter`, `MessageAssembler`, `DeflateInflater`).
- **Native zlib interop** for permessage-deflate: P/Invoke to platform-specific libraries (`zlib1.dll` / `libz.so.1` / `libz.dylib`). Validated at connect time with fail-fast diagnostics.
- **Native OpenSSL interop** (Linux only): P/Invoke to `libssl.so.3` / `libssl.so.1.1` for TLS read/write, eliminating `SslStream` internal allocations on the receive hot path.
- **Dedicated receive thread** (not async) to avoid Task/async state machine allocations.
- **Frame misalignment diagnostics**: `ValidateHeader` includes raw header bytes, previous frame info, and `FrameReader` buffer state in error messages. `WebSocketProtocolException.IsSuspectedMisalignment` distinguishes protocol violations from network-disconnect-induced misalignment.
- **Default User-Agent header**: `WebSocketHandshake` sends `User-Agent: DuLowAllocWebSocket/1.0` unless overridden via `CustomHeaders`, preventing Cloudflare WAF 403 blocks.
- **Korean-language XML docs** on `WebSocketClientOptions` properties — preserve this convention when adding new options.

### Source Layout

- `src/` — all library code (single project, no NuGet dependencies beyond BCL)
- `samples/DuLowAllocWebSocket.Sample/` — Binance Futures stream example + allocation test (`--alloc-test`)
