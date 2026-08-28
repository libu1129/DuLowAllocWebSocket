using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DuLowAllocWebSocket;

/// <summary>
/// Linux에서 동기 수신을 native poll/recv로 수행하는 Socket Stream입니다.
/// <para>
/// .NET의 Linux 동기 Socket 수신은 내부 비동기 큐와 대기 객체를 매번 만들 수 있으므로,
/// 전용 수신 스레드가 보장된 이 클라이언트에서는 non-blocking fd를 직접 poll한 뒤 recv합니다.
/// 송신과 핸드셰이크 비동기 I/O는 기존 NetworkStream에 위임하여 한 reader/한 writer의
/// full-duplex 계약을 유지합니다.
/// </para>
/// </summary>
internal sealed class LinuxNativeSocketStream : Stream
{
    private const short PollIn = 0x0001;
    private const int Interrupted = 4;
    private const int TryAgain = 11;

    // Socket은 fd의 수명을 보유한다. 클라이언트 teardown은 Shutdown으로 poll/recv를 깨운 뒤
    // 수신 스레드를 Join하고 마지막에 Socket을 Dispose하므로 fd가 읽는 중 재사용되지 않는다.
    private readonly Socket _socket;
    private readonly NetworkStream _inner;
    private readonly int _fileDescriptor;

    public LinuxNativeSocketStream(Socket socket, NetworkStream inner)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux native socket stream is Linux-only.");
        }

        _socket = socket;
        _inner = inner;
        _fileDescriptor = checked((int)socket.SafeHandle.DangerousGetHandle());
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanTimeout => _inner.CanTimeout;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout
    {
        get => _inner.ReadTimeout;
        set => _inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _inner.WriteTimeout;
        set => _inner.WriteTimeout = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override unsafe int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            ObjectDisposedException.ThrowIf(_socket.SafeHandle.IsClosed, _socket);

            // poll에서 대기하는 동안 managed scratch를 pin하지 않는다. recv 자체는 non-blocking이므로
            // 실제 native write가 일어나는 짧은 구간에만 destination 주소를 고정하면 충분하다.
            nint received;
            fixed (byte* destination = buffer)
            {
                // 먼저 recv를 시도해 이미 도착한 데이터의 poll syscall도 피한다.
                // fd는 .NET SocketAsyncContext 때문에 non-blocking 상태이므로 EAGAIN만 poll로 넘긴다.
                received = recv(_fileDescriptor, destination, (nuint)buffer.Length, 0);
            }

            if (received >= 0)
            {
                return checked((int)received);
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == Interrupted)
            {
                continue;
            }

            if (error != TryAgain)
            {
                throw new IOException("Native socket receive failed.", new SocketException(error));
            }

            PollFileDescriptor descriptor = new()
            {
                FileDescriptor = _fileDescriptor,
                Events = PollIn,
            };

            int pollResult;
            do
            {
                pollResult = poll(&descriptor, 1, -1);
            }
            while (pollResult < 0 && Marshal.GetLastPInvokeError() == Interrupted);

            if (pollResult < 0)
            {
                error = Marshal.GetLastPInvokeError();
                throw new IOException("Native socket poll failed.", new SocketException(error));
            }
        }
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint recv(int socket, byte* buffer, nuint length, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFileDescriptor* descriptors, nuint count, int timeout);
}
