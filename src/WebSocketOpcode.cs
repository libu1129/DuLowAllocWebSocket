using System.Runtime.CompilerServices;

namespace DuLowAllocWebSocket;

/// <summary>
/// WebSocket 프레임의 opcode 값입니다 (RFC 6455 5.2절).
/// </summary>
public enum WebSocketOpcode : byte
{
    /// <summary>연속 프레임 (fragmentation).</summary>
    Continuation = 0x0,

    /// <summary>텍스트 데이터 프레임.</summary>
    Text = 0x1,

    /// <summary>바이너리 데이터 프레임.</summary>
    Binary = 0x2,

    /// <summary>연결 종료 제어 프레임.</summary>
    Close = 0x8,

    /// <summary>Ping 제어 프레임.</summary>
    Ping = 0x9,

    /// <summary>Pong 제어 프레임.</summary>
    Pong = 0xA,
}

/// <summary>
/// <see cref="WebSocketOpcode"/>에 대한 확장 메서드입니다.
/// </summary>
public static class WebSocketOpcodeExtensions
{
    /// <summary>
    /// opcode가 제어 프레임(Close, Ping, Pong)인지 판별합니다 (RFC 6455 5.5절).
    /// </summary>
    /// <param name="opcode">판별할 opcode.</param>
    /// <returns>제어 프레임이면 <see langword="true"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsControl(this WebSocketOpcode opcode) => ((byte)opcode & 0x08) != 0;
}
