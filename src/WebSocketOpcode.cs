using System.Runtime.CompilerServices;

namespace DuLowAllocWebSocket;

public enum WebSocketOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA,
}

public static class WebSocketOpcodeExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsControl(this WebSocketOpcode opcode) => ((byte)opcode & 0x08) != 0;
}
