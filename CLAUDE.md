# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DuLowAllocWebSocket is a low-allocation, raw-socket WebSocket client library for .NET 10 and .NET 11, designed for zero-heap-allocation message reception in steady state. It targets latency-sensitive use cases (e.g., HFT market data feeds). No `ClientWebSocket` is used — transport starts from raw `Socket` with manual TLS upgrade.

- The `net10.0` asset uses the classic async state machine.
- The `net11.0` asset is compiled with runtime-async by default since package 1.5.20 (`Directory.Build.targets`).
  - Apps that load it need a .NET 11 RC1 or later runtime.
  - Do not treat "contains CoreCLR GC fix #131324" as a safety check for runtime-async.
    - That fix compiles only into non-regions GC builds, which the default 64-bit runtime does not use.

## Build & Run

```bash
dotnet build DuLowAllocWebSocket.sln -c Release
dotnet test tests/DuLowAllocWebSocket.Tests/DuLowAllocWebSocket.Tests.csproj -c Release
dotnet run --project samples/DuLowAllocWebSocket.Sample -- 'wss://fstream.binance.com/ws/!bookTicker'
dotnet run --project samples/DuLowAllocWebSocket.Sample -- --alloc-test
```

- The .NET 11 RC1 SDK `11.0.100-rc.1.26425.128` is pinned in `global.json` with `rollForward: disable`.
  - A runtime-async `net11.0` build fails on any other SDK. Preview 7 is unsupported.
- Tests use xUnit and run for both `net10.0` and `net11.0`.
- Pass `-p:EnableRuntimeAsync=false` for a classic-async comparison build of the `net11.0` asset.

### Release

- Pushing a `v*` tag runs `.github/workflows/publish.yml`: build → test → pack with the tag's version → push to GitHub Packages.
- Set `<Version>` in `DuLowAllocWebSocket.csproj` and the new `v{version}` tag to the same value.
  - Never move or reuse an existing tag.

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
  - This keeps the dedicated receive thread and the sender thread from entering one native OpenSSL `SSL*` at the same time.
- **Linux sync receive**: `LinuxNativeSocketStream` keeps TLS in `SslStream`, delegates handshake/async I/O/writes to `NetworkStream`, and uses native `recv` plus `poll` only for the dedicated synchronous reader. Preserve the teardown order `Socket.Shutdown` → receive-thread join → `Socket.Dispose`; it is the fd-lifetime contract.

### Connection Lifecycle

`DuLowAllocWebSocketClient` is single-use: connect → communicate → close → dispose. Reconnection requires a new instance. `WebSocketHandshake` handles DNS resolution, TCP connect, TLS negotiation, and HTTP Upgrade with `Sec-WebSocket-Accept` validation.

### Key Design Decisions

- **Buffers are pooled and activated on demand** via `WebSocketClientOptions`. `FrameReader` reuses the handshake buffer and grows read-ahead only under payload/backlog pressure; `FrameWriter` and message/control assemblers rent lazily. `ArrayPool<byte>.Shared` is used throughout.
- **Native zlib interop** for permessage-deflate: P/Invoke to platform-specific libraries (`zlib1.dll` / `libz.so.1` / `libz.dylib`). Validated at connect time with fail-fast diagnostics.
- **Linux zero-allocation blocking wait**: the dedicated reader bypasses .NET's synchronous `SocketAsyncContext` wait with native `recv`/`poll`; never extend this to a second concurrent reader.
- **Dedicated receive thread** (not async) to avoid Task/async state machine allocations.
- **Frame misalignment diagnostics**: `ValidateHeader` includes raw header bytes, previous frame info, and `FrameReader` buffer state in error messages. `WebSocketProtocolException.IsSuspectedMisalignment` distinguishes protocol violations from network-disconnect-induced misalignment.
- **Default User-Agent header**: `WebSocketHandshake` sends `User-Agent: DuLowAllocWebSocket/1.0` unless overridden via `CustomHeaders`, preventing Cloudflare WAF 403 blocks.
- **Korean-language XML docs** on `WebSocketClientOptions` properties — preserve this convention when adding new options.

### Source Layout

- `src/` — all library code (single project, no NuGet dependencies beyond BCL)
- `tests/DuLowAllocWebSocket.Tests/` — xUnit tests for `net10.0` and `net11.0`
- `samples/DuLowAllocWebSocket.Sample/` — Binance Futures stream example + allocation test (`--alloc-test`)
- `samples/DuLowAllocWebSocket.Compare/` — live receive comparison harness against `ClientWebSocket` (not in the solution)
- `benchmarks/DuLowAllocWebSocket.Benchmarks/` — BenchmarkDotNet suite, `net11.0` only
