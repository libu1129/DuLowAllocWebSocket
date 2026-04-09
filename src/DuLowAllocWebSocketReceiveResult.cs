using System.Net.WebSockets;

namespace DuLowAllocWebSocket;

/// <summary>
/// <see cref="DuLowAllocWebSocketClient.MessageReceived"/> 콜백으로 전달되는 수신 결과입니다.
/// <para>
/// <b>수명 제약:</b> <see cref="Payload"/>는 클라이언트 내부 풀 버퍼를 직접 참조합니다.
/// 콜백이 반환되면 해당 메모리가 다음 메시지에 재사용되므로, 데이터를 유지하려면
/// 콜백 내에서 <c>Payload.ToArray()</c> 또는 <c>Payload.Span.CopyTo()</c>로 복사해야 합니다.
/// </para>
/// </summary>
public readonly struct DuLowAllocWebSocketReceiveResult
{
    /// <summary>데이터 프레임 수신 결과를 생성합니다.</summary>
    public DuLowAllocWebSocketReceiveResult(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode)
    {
        Payload = payload;
        Opcode = opcode;
        CloseStatus = null;
        CloseStatusDescription = null;
    }

    /// <summary>Close 프레임 수신 결과를 생성합니다.</summary>
    public DuLowAllocWebSocketReceiveResult(WebSocketCloseStatus? closeStatus, string? closeStatusDescription)
    {
        Payload = ReadOnlyMemory<byte>.Empty;
        Opcode = WebSocketOpcode.Close;
        CloseStatus = closeStatus;
        CloseStatusDescription = closeStatusDescription;
    }

    /// <summary>
    /// 수신된 메시지 페이로드입니다. 내부 풀 버퍼를 참조하므로 콜백 반환 후 무효화됩니다.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>프레임의 opcode입니다 (Text, Binary, Close 등).</summary>
    public WebSocketOpcode Opcode { get; }

    /// <summary>Close 프레임의 상태 코드입니다. Close가 아니면 <see langword="null"/>.</summary>
    public WebSocketCloseStatus? CloseStatus { get; }

    /// <summary>Close 프레임의 사유 문자열입니다.</summary>
    public string? CloseStatusDescription { get; }

    /// <summary>Close 프레임인지 여부입니다.</summary>
    public bool IsClose => Opcode == WebSocketOpcode.Close;
}
