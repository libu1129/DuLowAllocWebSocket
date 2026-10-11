# DuLowAllocWebSocket (.NET 10 / .NET 11)

예측 가능한 수신 레이턴시를 위한 저할당 raw-socket WebSocket 클라이언트입니다.

## 구현 컴포넌트

- `WebSocketHandshake`: RFC6455 수동 HTTP Upgrade + `Sec-WebSocket-Accept` 검증 + `ws://`, `wss://` 전송 지원.
- `FrameReader` / `FrameWriter`: FIN/RSV1/opcode/length/mask 수동 프레임 파싱/쓰기 및 RFC fail-fast 검증.
- `CompressionNegotiator`: `permessage-deflate` 확장 파라미터 협상 및 파싱.
- `DeflateInflater`: 재사용 가능한 zlib 기반 raw-DEFLATE 해제기 (RFC7692 트레일러 추가).
- `MessageAssembler`: `MemoryStream` 없이 풀 기반 프래그먼트 메시지 조립.
- `DuLowAllocWebSocketClient`: 공개 API와 이벤트 기반 수신.
  - 메서드·속성: `State`, `ConnectAsync`, `SendAsync`, `SendSync`, `SendPingAsync`, `SendPingSync`, `SendPongAsync`, `SendPongSync`, `CloseOutputAsync`, `CloseAsync`, `ControlFrameThrottle`.
  - 이벤트: `MessageReceived`, `Disconnected`, `OnError`.
- `WebSocketClientOptions`: 버퍼 상한·초기 용량, `EnablePerMessageDeflate`, `CustomHeaders` 등 동작 정책 설정.
- `LinuxNativeSocketStream`: Linux 전용 수신 스레드의 동기 read를 native `recv`/`poll`로 수행하고 handshake·write는 `NetworkStream`에 위임.

## 참고 사항

### 런타임

- NuGet 패키지 1.5.20부터 `net11.0` 자산은 .NET 11 RC1 SDK의 RuntimeAsync로 컴파일됩니다.
  - 이 자산을 실행하는 앱은 .NET 11 RC1 이상을 사용해야 합니다.
  - GC 수정 #131324 포함 여부는 RuntimeAsync 안전 기준이 아닙니다. 이 수정은 64비트 기본 런타임이 쓰지 않는 non-regions GC 빌드에만 들어갑니다.
  - `net10.0` 자산은 클래식 async를 유지합니다.
  - 소스의 클래식 비교 빌드는 `-p:EnableRuntimeAsync=false`로 실행합니다.

### 전송·수신

- `ClientWebSocket`을 사용하지 않으며, raw `Socket`에서 시작하여 `wss://`의 경우 모든 플랫폼에서 `SslStream`으로 업그레이드합니다.
- 수신 경로는 이벤트 기반이며, 정상 상태에서 **수신 메시지당 힙 할당 0**을 목표로 합니다.
  - TLS 구현, 사용자 콜백 로직, close-reason UTF-8 디코드는 이 목표에서 제외됩니다.
  - 이 목표를 유지하려면 `MessageReceived` 콜백도 할당 없이 작성하세요.
- Linux에서는 `SslStream` 아래의 동기 socket wait만 native `recv`/`poll`로 처리해 .NET `SocketAsyncContext` 대기 객체 할당을 피합니다.
  - 호환성 우회가 필요하면 `UseNativeLinuxSyncReceive = false`로 기존 경로를 복원할 수 있습니다.
- 버퍼는 필요할 때 풀에서 빌리며, 송수신 스크래치 버퍼는 `ReceiveScratchBufferSize`·`SendScratchBufferSize` 상한까지 늘어납니다.
  - 메시지 조립 버퍼 `MessageBufferSize`와 압축 해제 버퍼 `InflateOutputBufferSize`의 초기 용량을 키우면 버스트 중 확장을 줄일 수 있습니다.

### 압축·zlib

- 압축 확장 협상은 기본적으로 비활성화되며, 검증된 연결에서만 `EnablePerMessageDeflate = true`로 명시적으로 활성화합니다.
  - 비활성 상태에서는 네이티브 zlib 없이 실행됩니다.
- RFC7692 설정은 `ClientContextTakeover`, `ServerContextTakeover`, `ClientMaxWindowBits`, `ServerMaxWindowBits`로 구성할 수 있습니다.
- `EnablePerMessageDeflate = true`이면 시작 시 `inflateInit2_`/`inflateEnd`로 네이티브 zlib 유효성을 검사하고, 실패하면 진단 정보와 함께 즉시 실패합니다.
- 네이티브 zlib 로딩은 크로스 플랫폼입니다. NuGet에 포함된 native asset, `/opt/zlib-ng/lib/libz.so.1`, 시스템 `libz.so.1`/`libz.so`, `libz.dylib` 순서로 시도합니다.
  - 윈도우/리눅스 zlib-ng compat 바이너리는 NuGet 패키지에 포함됩니다: `runtimes/win-x64/native/zlib1.dll`, `runtimes/linux-x64/native/libz.so.1`.
  - 윈도우 수동 설정 시 `zlib1.dll`을 실행 파일 옆에 배치하세요. 예: `bin/Release/net10.0/` 또는 대응하는 `net11.0` 경로.

### 헤더·프록시

- `CustomHeaders`로 핸드셰이크 HTTP 요청에 인증 토큰, API 키 등 커스텀 헤더를 추가할 수 있습니다.
  - `User-Agent`를 설정하지 않으면 기본값 `DuLowAllocWebSocket/1.0`이 자동으로 포함됩니다. Cloudflare 등 WAF 차단을 막기 위한 것입니다.
- `ProxyHost`, `ProxyPort`, `ProxyUsername`, `ProxyPassword`로 선택적 HTTP 프록시 터널을 지원합니다.
- 핸드셰이크 실패 시 서버 응답 헤더와 본문을 포함한 상세 에러 메시지를 제공합니다.

### 이벤트·수명

- `MessageReceived`를 구독하고 `DuLowAllocWebSocketReceiveResult`를 소비합니다.
  - `IsClose`가 false이면 `Payload`는 클라이언트 소유 풀 메모리를 참조하므로, 콜백이 반환되기 전에 소비하거나 복사해야 합니다.
- `Disconnected` 이벤트로 연결 종료를 감지하고, `OnError` 이벤트로 수신 펌프 예외를 처리할 수 있습니다.
  - `WebSocketProtocolException.IsSuspectedMisalignment`가 `true`이면 실제 프로토콜 위반이 아닌 네트워크 단절 후 프레임 경계 오정렬로 인한 오류입니다.
- RFC6455 ping/pong 정책은 `AutoPongOnPing`, `KeepAliveInterval`, `KeepAlivePingPayload`로 설정합니다.
  - 기본값은 30초 간격 ping이며, `TimeSpan.Zero`로 비활성화합니다.
- `DuLowAllocWebSocketClient`는 단일 연결 수명 주기용입니다. 연결 종료 후 재연결하려면 새 인스턴스를 생성하세요.

## 빌드

두 대상 프레임워크를 모두 빌드하려면 `global.json`에 고정된 .NET 11 RC1 SDK `11.0.100-rc.1.26425.128`이 필요합니다.

```bash
dotnet build DuLowAllocWebSocket.sln -c Release
dotnet test tests/DuLowAllocWebSocket.Tests/DuLowAllocWebSocket.Tests.csproj -c Release
```

## 예제 (Binance Futures All Book Tickers)

실시간으로 전 심볼 최우선 매수/매도 호가를 수신합니다.

```bash
dotnet run --project samples/DuLowAllocWebSocket.Sample -- 'wss://fstream.binance.com/ws/!bookTicker'
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

## 할당 테스트

```bash
dotnet run --project samples/DuLowAllocWebSocket.Sample -- --alloc-test
```

수신 스레드에서 메시지당 힙 할당량을 측정합니다. 200개 워밍업 메시지 후, 다음 1,000개 메시지에 대해 `GC.GetAllocatedBytesForCurrentThread()`를 측정하여 총/메시지당 바이트를 보고합니다.
