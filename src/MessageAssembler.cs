using System.Buffers;
using System.Runtime.CompilerServices;

namespace DuLowAllocWebSocket;

/// <summary>
/// 분할된 WebSocket 프레임 페이로드를 하나의 메시지로 조립합니다.
/// <see cref="ArrayPool{T}.Shared"/> 기반 버퍼를 사용하며, <see cref="Reset"/>으로
/// 쓰기 오프셋만 초기화하여 버퍼 반환/재대여 없이 다음 메시지를 수신합니다.
/// </summary>
public sealed class MessageAssembler : IPayloadSink, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    public MessageAssembler(int initialCapacity = 16 * 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _written = 0;
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    public int Length => _written;

    public void Reset() => _written = 0;

    public void Append(ReadOnlySpan<byte> data)
    {
        int required = checked(_written + data.Length);
        EnsureCapacity(required);
        data.CopyTo(_buffer.AsSpan(_written));
        _written += data.Length;
    }

    public void ReplaceWith(ReadOnlySpan<byte> data)
    {
        _written = 0;
        Append(data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int required)
    {
        if (required > _buffer!.Length)
        {
            GrowBuffer(required);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowBuffer(int required)
    {
        long newSize = _buffer!.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }

        if (newSize > Array.MaxLength)
        {
            newSize = Array.MaxLength;
        }

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent((int)newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
