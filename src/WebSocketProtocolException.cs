namespace DuLowAllocWebSocket;

public sealed class WebSocketProtocolException : Exception
{
    public WebSocketProtocolException(string message) : base(message) { }
}
