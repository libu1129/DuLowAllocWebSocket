namespace DuLowAllocWebSocket;

/// <summary>
/// Ping/Pong 같은 WebSocket 제어 프레임 전송 전 호출되는 제한기입니다.
/// 여러 연결이 같은 인스턴스를 공유하면 거래소/IP 기준 송신 제한을 한곳에서 맞출 수 있습니다.
/// </summary>
public interface IWebSocketControlFrameThrottle
{
    /// <summary>
    /// 지정한 제어 프레임을 보내기 전에 필요한 만큼 대기합니다.
    /// </summary>
    /// <param name="opcode">전송할 제어 프레임 opcode.</param>
    /// <param name="cancellationToken">연결 종료나 호출자 취소를 전달하는 토큰.</param>
    ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken);
}
