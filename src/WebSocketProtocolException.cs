namespace DuLowAllocWebSocket;

/// <summary>
/// WebSocket 프로토콜 위반 또는 프레임 경계 오정렬 시 발생하는 예외입니다.
/// </summary>
public sealed class WebSocketProtocolException : Exception
{
    public WebSocketProtocolException(string message) : base(message) { }

    public WebSocketProtocolException(string message, bool isSuspectedMisalignment)
        : base(message)
    {
        IsSuspectedMisalignment = isSuspectedMisalignment;
    }

    /// <summary>
    /// 프레임 경계 오정렬로 인한 오류로 의심되면 <see langword="true"/>입니다.
    /// <see langword="true"/>이면 실제 프로토콜 위반이 아닌,
    /// 네트워크 단절 후 잔여 버퍼 데이터의 잘못된 해석일 가능성이 높습니다.
    /// </summary>
    public bool IsSuspectedMisalignment { get; }
}
