using System.Net.WebSockets;

namespace DuLowAllocWebSocket;

public readonly struct DuLowAllocWebSocketReceiveResult
{
    public DuLowAllocWebSocketReceiveResult(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode)
    {
        Payload = payload;
        Opcode = opcode;
        CloseStatus = null;
        CloseStatusDescription = null;
    }

    public DuLowAllocWebSocketReceiveResult(WebSocketCloseStatus? closeStatus, string? closeStatusDescription)
    {
        Payload = ReadOnlyMemory<byte>.Empty;
        Opcode = WebSocketOpcode.Close;
        CloseStatus = closeStatus;
        CloseStatusDescription = closeStatusDescription;
    }

    public ReadOnlyMemory<byte> Payload { get; }
    public WebSocketOpcode Opcode { get; }
    public WebSocketCloseStatus? CloseStatus { get; }
    public string? CloseStatusDescription { get; }
    public bool IsClose => Opcode == WebSocketOpcode.Close;
}
