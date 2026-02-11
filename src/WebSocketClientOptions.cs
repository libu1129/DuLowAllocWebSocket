namespace DuLowAllocWebSocket;

public sealed class WebSocketClientOptions
{
    /// <summary>
    /// 프레임 수신 파서가 내부적으로 사용하는 임시 스크래치 버퍼 크기(바이트)입니다.
    /// 값을 크게 잡으면 버스트 트래픽에서 재할당/복사 빈도를 줄일 수 있지만, 연결당 초기 메모리 사용량은 증가합니다.
    /// </summary>
    public int ReceiveScratchBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// 송신 프레임을 구성할 때 사용하는 임시 스크래치 버퍼 크기(바이트)입니다.
    /// 대용량 메시지를 자주 전송한다면 여유 있게 설정해 조각화/복사 비용을 줄일 수 있습니다.
    /// </summary>
    public int SendScratchBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// 단일 메시지(여러 프레임으로 분할 가능)를 조립할 때 사용하는 버퍼 크기(바이트)입니다.
    /// 메시지가 이 크기를 자주 초과하면 추가 처리 비용이 늘어나므로, 예상 최대 메시지 크기를 고려해 설정하세요.
    /// </summary>
    public int MessageBufferSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Ping/Pong/Close 같은 제어 프레임 처리용 버퍼 크기(바이트)입니다.
    /// RFC 상 제어 프레임은 작기 때문에 일반적으로 기본값이면 충분합니다.
    /// </summary>
    public int ControlBufferSize { get; init; } = 8 * 1024;

    /// <summary>
    /// permessage-deflate 해제(inflate) 시 출력 데이터를 임시로 담는 버퍼 크기(바이트)입니다.
    /// 압축률이 높은 메시지를 다룰수록 충분한 크기로 잡아야 재할당/분할 처리를 줄일 수 있습니다.
    /// </summary>
    public int InflateOutputBufferSize { get; init; } = 256 * 1024;

    /// <summary>
    /// HTTP 핸드셰이크 요청/응답 파싱에 사용하는 버퍼 크기(바이트)입니다.
    /// 커스텀 헤더가 많은 환경이라면 값을 늘려 헤더 초과로 인한 실패를 방지할 수 있습니다.
    /// </summary>
    public int HandshakeBufferSize { get; init; } = 16 * 1024;

    /// <summary>
    /// permessage-deflate 확장 협상을 시도할지 여부입니다.
    /// <see langword="true"/>이면 서버와 압축 확장을 협상해 대역폭을 절약할 수 있고,
    /// <see langword="false"/>이면 CPU 비용을 줄이는 대신 원본 크기 그대로 송수신합니다.
    /// </summary>
    public bool EnablePerMessageDeflate { get; init; } = true;

    /// <summary>
    /// 클라이언트 측 압축 컨텍스트 재사용 허용 여부입니다(RFC7692 client_no_context_takeover 반대 개념).
    /// <see langword="true"/>면 연속 메시지에서 압축 효율이 좋아지지만, 메시지 간 상태가 유지됩니다.
    /// </summary>
    public bool ClientContextTakeover { get; init; } = true;

    /// <summary>
    /// 서버 측 압축 컨텍스트 재사용 허용 여부입니다(RFC7692 server_no_context_takeover 반대 개념).
    /// <see langword="true"/>면 서버가 이전 메시지 상태를 활용해 더 높은 압축률을 얻을 수 있습니다.
    /// </summary>
    public bool ServerContextTakeover { get; init; } = true;

    /// <summary>
    /// 클라이언트의 deflate 윈도우 비트 크기(허용 범위: 8~15)입니다.
    /// 값이 작을수록 메모리 사용량은 줄지만 압축률이 낮아질 수 있으며,
    /// <see langword="null"/>이면 확장 제안에서 해당 파라미터를 생략합니다.
    /// </summary>
    public int? ClientMaxWindowBits { get; init; } = 15;

    /// <summary>
    /// 서버에 요청할 deflate 윈도우 비트 크기(허용 범위: 8~15)입니다.
    /// 네트워크 대역폭, 서버 리소스 정책에 맞춰 조정하며,
    /// <see langword="null"/>이면 확장 제안에서 해당 파라미터를 생략합니다.
    /// </summary>
    public int? ServerMaxWindowBits { get; init; } = 15;


    /// <summary>
    /// HTTP 프록시 호스트 이름 또는 IP입니다.
    /// 설정하면 WebSocket 연결 전에 CONNECT 터널을 통해 프록시를 경유합니다.
    /// </summary>
    public string? ProxyHost { get; init; }

    /// <summary>
    /// HTTP 프록시 포트입니다.
    /// <see cref="ProxyHost"/>를 지정한 경우 함께 설정해야 하며, 일반적으로 3128/8080/8888 등을 사용합니다.
    /// </summary>
    public int? ProxyPort { get; init; }

    /// <summary>
    /// 프록시 인증에 사용할 사용자 이름입니다.
    /// 인증이 필요한 프록시에서만 설정하면 됩니다.
    /// </summary>
    public string? ProxyUsername { get; init; }

    /// <summary>
    /// 프록시 인증 비밀번호입니다.
    /// 민감 정보이므로 안전한 비밀 저장소를 통해 주입하는 방식을 권장합니다.
    /// </summary>
    public string? ProxyPassword { get; init; }

    /// <summary>
    /// 서버에서 Ping 수신 시 자동으로 Pong을 응답할지 여부입니다.
    /// 대부분의 환경에서는 연결 유지를 위해 <see langword="true"/>를 권장합니다.
    /// </summary>
    public bool AutoPongOnPing { get; init; } = true;

    /// <summary>
    /// Ping 송신 전략을 정의합니다(예: 서버 주도, 클라이언트 주기 송신).
    /// 운영 환경의 유휴 타임아웃 정책에 맞춰 선택하세요.
    /// </summary>
    public WebSocketPingMode PingMode { get; init; } = WebSocketPingMode.ServerDriven;

    /// <summary>
    /// 클라이언트 주도 Ping 모드에서 Ping 전송 간격입니다.
    /// <see cref="PingMode"/>가 주기 송신 모드일 때만 의미가 있으며,
    /// 너무 짧으면 불필요한 트래픽이 증가하고 너무 길면 연결 단절 감지가 늦어질 수 있습니다.
    /// </summary>
    public TimeSpan? ClientPingInterval { get; init; }

    /// <summary>
    /// 클라이언트가 전송하는 Ping 프레임의 페이로드입니다.
    /// 진단용 식별자/타임스탬프 등을 담을 수 있으며, RFC6455 제어 프레임 제한을 고려해 짧게 유지하세요.
    /// </summary>
    public ReadOnlyMemory<byte> ClientPingPayload { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// 서버가 잘못 마스킹된 프레임을 보낼 때 즉시 연결을 실패 처리할지 여부입니다.
    /// 프로토콜 위반을 엄격히 차단하려면 <see langword="true"/>를 유지하세요.
    /// </summary>
    public bool RejectMaskedServerFrames { get; init; } = true;

    /// <summary>
    /// 수신 가능한 최대 메시지 크기(바이트)입니다.
    /// 악의적/비정상 대용량 메시지로부터 메모리를 보호하는 안전 장치이며,
    /// 애플리케이션 도메인에 맞는 상한값으로 조정하는 것이 좋습니다.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}
