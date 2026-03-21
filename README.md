# DuLowAllocWebSocket (.NET 10)

예측 가능한 수신 레이턴시를 위한 저할당 raw-socket WebSocket 클라이언트입니다.

## 구현 컴포넌트

- `WebSocketHandshake`: RFC6455 수동 HTTP Upgrade + `Sec-WebSocket-Accept` 검증 + `ws://`, `wss://` 전송 지원.
- `FrameReader` / `FrameWriter`: FIN/RSV1/opcode/length/mask 수동 프레임 파싱/쓰기 및 RFC fail-fast 검증.
- `CompressionNegotiator`: `permessage-deflate` 확장 파라미터 협상 및 파싱.
- `DeflateInflater`: 재사용 가능한 zlib 기반 raw-DEFLATE 해제기 (RFC7692 트레일러 추가).
- `MessageAssembler`: `MemoryStream` 없이 풀 기반 프래그먼트 메시지 조립.
- `DuLowAllocWebSocketClient`: 공개 API (`State`, `ConnectAsync`, `SendAsync`, `SendPingAsync`, `CloseOutputAsync`, `CloseAsync`) 및 이벤트 기반 수신 (`MessageReceived`).
- `WebSocketClientOptions`: 사전 할당 및 정책 설정 (HFT 지향 버스트 처리), `EnablePerMessageDeflate` 포함.
- `OpenSslStream`: 리눅스 전용 OpenSSL P/Invoke TLS 스트림. `SslStream` 내부 할당을 우회하여 리눅스 `wss://` 수신 힙 할당 0 달성.

## 참고 사항

- `ClientWebSocket`을 사용하지 않으며, raw `Socket`에서 시작하여 `wss://`의 경우 TLS 스트림으로 업그레이드합니다. 리눅스에서는 `OpenSslStream` (`SSL_read`/`SSL_write` 직접 P/Invoke)이 `SslStream`을 대체하여 매니지드 TLS 레이어 할당을 제거합니다. 윈도우에서는 `SslStream` (SChannel)을 그대로 사용합니다 (이미 할당 없음).
- 수신 경로는 이벤트 기반이며, 정상 상태에서 메시지당 `byte[]`/`string` 할당을 하지 않습니다.
- 메시지 수신 콜백 경로는 **수신 메시지당 힙 할당 0**을 목표로 설계되었습니다 (사용자 콜백 로직 및 close-reason UTF-8 디코드 제외).
- 런타임 버스트 시 증가를 방지하기 위해 초기 대용량 할당을 허용/설정할 수 있습니다.
- 압축 확장 협상은 `EnablePerMessageDeflate`를 통해 명시적으로 활성화/비활성화할 수 있습니다.
- RFC7692 설정은 `ClientContextTakeover`, `ServerContextTakeover`, `ClientMaxWindowBits`, `ServerMaxWindowBits`로 구성할 수 있습니다.
- `ProxyHost`, `ProxyPort`, `ProxyUsername`, `ProxyPassword`를 통해 선택적 HTTP 프록시 터널을 지원합니다.
- RFC6455 ping/pong 정책은 `AutoPongOnPing`, `KeepAliveInterval`, `KeepAlivePingPayload`로 설정 가능합니다 (`KeepAliveInterval`이 `TimeSpan.Zero`가 아니면 주기적 ping 전송).
- `MessageReceived`를 구독하고 `DuLowAllocWebSocketReceiveResult`를 소비합니다. `IsClose`가 false이면 `Payload`는 클라이언트 소유 풀 메모리를 참조하므로, 다음 콜백 메시지 전에 소비하거나 복사해야 합니다.
- `DuLowAllocWebSocketClient`는 단일 연결 수명 주기용입니다. 연결 종료 후 재연결하려면 새 인스턴스를 생성하세요.
- 네이티브 zlib 로딩은 크로스 플랫폼입니다: `zlib1.dll` (윈도우), `libz.so.1`/`libz.so` (리눅스), `libz.dylib` (macOS)을 시도합니다.
- 리눅스 TLS용 네이티브 OpenSSL 로딩: `libssl.so.3`, `libssl.so.1.1`, `libssl.so`를 시도합니다. 사용 불가 시 `SslStream`으로 폴백합니다.
- 윈도우/리눅스 zlib는 실행 환경에서 제공되어야 합니다 (윈도우: `zlib1.dll`, 리눅스: `libz.so.1`).
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
