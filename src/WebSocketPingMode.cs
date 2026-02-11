namespace DuLowAllocWebSocket;

public enum WebSocketPingMode
{
    // Respond to server Ping only (passive keepalive).
    ServerDriven = 0,

    // Client can call SendPingAsync explicitly.
    ClientDrivenManual = 1,

    // Client sends Ping on configured interval.
    ClientDrivenAuto = 2,
}
