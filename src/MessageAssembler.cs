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

    /// <summary>
    /// <see cref="MessageAssembler"/>의 새 인스턴스를 생성하고 풀 버퍼를 할당합니다.
    /// </summary>
    /// <param name="initialCapacity">초기 버퍼 크기(바이트). <see cref="ArrayPool{T}.Shared"/>에서 대여.</param>
    public MessageAssembler(int initialCapacity = 16 * 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _written = 0;
    }

    /// <summary>
    /// 현재까지 조립된 데이터의 <see cref="ReadOnlySpan{T}"/> 뷰입니다.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>
    /// 현재까지 조립된 데이터의 <see cref="ReadOnlyMemory{T}"/> 뷰입니다.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// 현재까지 조립된 데이터의 바이트 수입니다.
    /// </summary>
    public int Length => _written;

    /// <summary>
    /// 쓰기 오프셋을 초기화합니다. 버퍼는 유지되며 다음 메시지 조립에 재사용됩니다.
    /// </summary>
    public void Reset() => _written = 0;

    /// <summary>
    /// 데이터를 버퍼 끝에 추가합니다. 용량 부족 시 자동 확장됩니다.
    /// </summary>
    /// <param name="data">추가할 데이터.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        int required = checked(_written + data.Length);
        EnsureCapacity(required);
        data.CopyTo(_buffer.AsSpan(_written));
        _written += data.Length;
    }

    /// <summary>
    /// 기존 내용을 모두 지우고 <paramref name="data"/>로 대체합니다.
    /// </summary>
    /// <param name="data">새 데이터.</param>
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

    /// <summary>
    /// 내부 버퍼를 <see cref="ArrayPool{T}.Shared"/>에 반환합니다.
    /// </summary>
    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
