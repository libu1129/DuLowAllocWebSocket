namespace DuLowAllocWebSocket.Benchmarks.Helpers;

/// <summary>
/// 사전 구성된 바이트 배열을 순환 반복하여 반환하는 mock Stream.
/// FrameReader를 네트워크 I/O 없이 벤치마크하기 위한 용도.
/// </summary>
public sealed class LoopingMemoryStream : Stream
{
    private readonly byte[] _data;
    private int _position;

    public LoopingMemoryStream(byte[] data)
    {
        _data = data;
        _position = 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;

        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int available = _data.Length - _position;
            int toCopy = Math.Min(buffer.Length - totalRead, available);
            _data.AsSpan(_position, toCopy).CopyTo(buffer[totalRead..]);
            _position += toCopy;
            totalRead += toCopy;

            if (_position >= _data.Length)
                _position = 0;
        }

        return totalRead;
    }

    public void ResetPosition() => _position = 0;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
