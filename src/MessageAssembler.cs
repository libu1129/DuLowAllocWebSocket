using System.Buffers;

namespace DuLowAllocWebSocket;

public sealed class MessageAssembler : IDisposable
{
    private byte[] _buffer;
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
        EnsureCapacity(_written + data.Length);
        data.CopyTo(_buffer.AsSpan(_written));
        _written += data.Length;
    }

    public void ReplaceWith(ReadOnlySpan<byte> data)
    {
        _written = 0;
        Append(data);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;

        int newSize = _buffer.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
