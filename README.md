# DuLowAllocWebSocket (.NET 10)

예측 가능한 수신 레이턴시를 위한 저할당 raw-socket WebSocket 클라이언트입니다.

## 구현 컴포넌트

- `WebSocketHandshake`: RFC6455 수동 HTTP Upgrade + `Sec-WebSocket-Accept` 검증 + `ws://`, `wss://` 전송 지원.
- `FrameReader` / `FrameWriter`: FIN/RSV1/opcode/length/mask 수동 프레임 파싱/쓰기 및 RFC fail-fast 검증.
- `CompressionNegotiator`: `permessage-deflate` 확장 파라미터 협상 및 파싱.
- `DeflateInflater`: 재사용 가능한 zlib 기반 raw-DEFLATE 해제기 (RFC7692 트레일러 추가).
- `MessageAssembler`: `MemoryStream` 없이 풀 기반 프래그먼트 메시지 조립.
- `DuLowAllocWebSocketClient`: 공개 API (`State`, `ConnectAsync`, `SendAsync`, `SendSync`, `SendPingAsync`, `SendPingSync`, `CloseOutputAsync`, `CloseAsync`) 및 이벤트 기반 수신 (`MessageReceived`, `Disconnected`, `OnError`).
- `WebSocketClientOptions`: 사전 할당 및 정책 설정 (HFT 지향 버스트 처리), `EnablePerMessageDeflate`, `CustomHeaders` 포함.
- `LinuxNativeSocketStream`: Linux 전용 수신 스레드의 동기 read를 native `recv`/`poll`로 수행하고 handshake·write는 `NetworkStream`에 위임.
- `OpenSslStream`: 단일 I/O 소유자 재설계 연구용 비활성 코드. 현재 클라이언트의 TLS 전송에는 사용하지 않습니다.

## 참고 사항

- `ClientWebSocket`을 사용하지 않으며, raw `Socket`에서 시작하여 `wss://`의 경우 모든 플랫폼에서 `SslStream`으로 업그레이드합니다. 한 개의 네이티브 OpenSSL `SSL*`에 여러 스레드가 동시에 진입하지 않도록 OpenSSL 직접 P/Invoke 경로는 비활성화했습니다.
- WebSocket 프레임 수신 경로는 이벤트 기반이며, 정상 상태에서 메시지당 `byte[]`/`string` 할당을 하지 않습니다. Linux에서는 `SslStream` 아래의 동기 socket wait만 native `recv`/`poll`로 처리해 .NET `SocketAsyncContext` 대기 객체 할당을 피합니다. 호환성 우회가 필요하면 `UseNativeLinuxSyncReceive = false`로 기존 경로를 복원할 수 있습니다.
- 메시지 수신 콜백 경로는 **수신 메시지당 힙 할당 0**을 목표로 설계되었습니다 (TLS 구현, 사용자 콜백 로직 및 close-reason UTF-8 디코드 제외).
- 런타임 버스트 시 증가를 방지하기 위해 초기 대용량 할당을 허용/설정할 수 있습니다.
- 압축 확장 협상은 `EnablePerMessageDeflate`를 통해 명시적으로 활성화/비활성화할 수 있습니다.
- RFC7692 설정은 `ClientContextTakeover`, `ServerContextTakeover`, `ClientMaxWindowBits`, `ServerMaxWindowBits`로 구성할 수 있습니다.
- `ProxyHost`, `ProxyPort`, `ProxyUsername`, `ProxyPassword`를 통해 선택적 HTTP 프록시 터널을 지원합니다.
- `CustomHeaders`를 통해 핸드셰이크 HTTP 요청에 커스텀 헤더를 추가할 수 있습니다 (인증 토큰, API 키 등).
- `Disconnected` 이벤트로 연결 종료를 감지하고, `OnError` 이벤트로 수신 펌프 예외를 처리할 수 있습니다. `WebSocketProtocolException.IsSuspectedMisalignment`가 `true`이면 실제 프로토콜 위반이 아닌 네트워크 단절 후 프레임 경계 오정렬로 인한 오류입니다.
- 핸드셰이크 실패 시 서버 응답 헤더와 본문을 포함한 상세 에러 메시지를 제공합니다.
- `CustomHeaders`에 `User-Agent`를 설정하지 않으면 기본값 `DuLowAllocWebSocket/1.0`이 자동으로 포함됩니다 (Cloudflare 등 WAF 차단 방지).
- RFC6455 ping/pong 정책은 `AutoPongOnPing`, `KeepAliveInterval`, `KeepAlivePingPayload`로 설정 가능합니다 (기본값 30초 간격 ping, `TimeSpan.Zero`로 비활성화).
- `MessageReceived`를 구독하고 `DuLowAllocWebSocketReceiveResult`를 소비합니다. `IsClose`가 false이면 `Payload`는 클라이언트 소유 풀 메모리를 참조하므로, 다음 콜백 메시지 전에 소비하거나 복사해야 합니다.
- `DuLowAllocWebSocketClient`는 단일 연결 수명 주기용입니다. 연결 종료 후 재연결하려면 새 인스턴스를 생성하세요.
- 네이티브 zlib 로딩은 크로스 플랫폼입니다: NuGet에 포함된 native asset, `/opt/zlib-ng/lib/libz.so.1`, 시스템 `libz.so.1`/`libz.so`, `libz.dylib` 순서로 시도합니다.
- TLS는 운영체제의 .NET `SslStream` 구현을 사용합니다. 비활성 `OpenSslStream` 코드는 full-duplex client에 연결하지 않습니다.
- 윈도우/리눅스 zlib-ng compat 바이너리는 NuGet 패키지에 포함됩니다 (`runtimes/win-x64/native/zlib1.dll`, `runtimes/linux-x64/native/libz.so.1`).
- 윈도우 수동 설정 시, `zlib1.dll`을 실행 파일 옆에 배치하세요 (예: `bin/Debug/net10.0/` 또는 `bin/Release/net10.0/`).
- `EnablePerMessageDeflate = true`이면, 시작 시 네이티브 zlib 유효성 검사 (`inflateInit2_`/`inflateEnd`)를 수행하고 실패 시 진단 정보와 함께 즉시 실패합니다.
- 네이티브 zlib 의존성 없이 실행하려면 `EnablePerMessageDeflate = false`로 설정하세요 (압축 없음).

## 빌드

```bash
dotnet build
```

## 예제 (Binance Futures All Book Tickers)

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

    // 힙 할당 0 수신을 유지하려면 이 콜백을 할당 없이 유지하세요.
    // string json = Encoding.UTF8.GetString(result.Payload.Span); // 디버깅용 출력
};

await client.ConnectAsync(uri, cts.Token);
```

- 실시간으로 전 심볼 최우선 매수/매도 호가를 수신합니다.
- `MessageReceived` (이벤트 기반 수신)을 통해 수신 데이터를 처리합니다.
- 힙 할당 0 수신 동작을 유지하려면 사용자 콜백 로직을 정상 상태에서 할당 없이 유지하세요.

## 할당 테스트

```bash
dotnet run -- --alloc-test
```

수신 스레드에서 메시지당 힙 할당량을 측정합니다. 200개 워밍업 메시지 후, 다음 1,000개 메시지에 대해 `GC.GetAllocatedBytesForCurrentThread()`를 측정하여 총/메시지당 바이트를 보고합니다.
