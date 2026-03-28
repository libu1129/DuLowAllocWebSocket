using System.Runtime.InteropServices;
using System.Text;

namespace DuLowAllocWebSocket;

/// <summary>
/// 리눅스 전용 OpenSSL P/Invoke 기반 TLS 스트림입니다.
/// SslStream 내부 힙 할당을 완전히 우회하여, SSL_read/SSL_write를
/// 프리 할당된 버퍼에 직접 수행함으로써 수신 경로 힙 할당 0을 달성합니다.
/// </summary>
internal sealed unsafe class OpenSslStream : Stream
{
    private static readonly Lazy<OpenSslNativeMethods?> Native = new(OpenSslNativeMethods.TryLoad);

    private nint _ctx;
    private nint _ssl;
    private volatile bool _disposed;
    private int _dupFd;          // SSL이 사용하는 dup된 소켓 fd (InterruptRead용)
    private int _sslFreed;       // SslFree 완료 여부 (0: 미해제, 1: 해제됨) — 이중 해제 방지

    private const int SslVerifyPeer = 1;
    private const int SslCtrlSetTlsextHostname = 55;
    private const int TlsextNameTypeHostName = 0;
    private const int SslErrorZeroReturn = 6;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;
    private const int ShutRdWr = 2;

    private readonly OpenSslNativeMethods _native;

    public static bool IsSupported => Native.Value is not null;

    public OpenSslStream(int socketFd, string hostname)
    {
        var native = Native.Value ?? throw new DllNotFoundException(
            "OpenSSL native library not available. Install libssl (libssl.so.3 or libssl.so.1.1).");
        _native = native;

        int dupFd = LibcDup(socketFd);
        if (dupFd < 0)
        {
            throw new InvalidOperationException("Failed to duplicate socket file descriptor.");
        }

        _dupFd = dupFd;

        int flags = LibcFcntl(dupFd, FGetFl, 0);
        if (flags >= 0)
        {
            LibcFcntl(dupFd, FSetFl, flags & ~ONonBlock);
        }

        bool success = false;
        bool sslOwnsFd = false;
        try
        {
            _ctx = native.SslCtxNew(native.TlsClientMethod());
            if (_ctx == 0)
            {
                throw new WebSocketProtocolException("SSL_CTX_new failed.");
            }

            native.SslCtxSetDefaultVerifyPaths(_ctx);
            native.SslCtxSetVerify(_ctx, SslVerifyPeer, 0);

            _ssl = native.SslNew(_ctx);
            if (_ssl == 0)
            {
                throw new WebSocketProtocolException("SSL_new failed.");
            }

            if (native.SslSetFd(_ssl, dupFd) != 1)
            {
                throw new WebSocketProtocolException("SSL_set_fd failed.");
            }

            sslOwnsFd = true;

            int len = Encoding.ASCII.GetByteCount(hostname);
            byte* hostBuf = stackalloc byte[len + 1];
            Encoding.ASCII.GetBytes(hostname, new Span<byte>(hostBuf, len));
            hostBuf[len] = 0;
            native.SslCtrl(_ssl, SslCtrlSetTlsextHostname, TlsextNameTypeHostName, hostBuf);

            int ret = native.SslConnect(_ssl);
            if (ret != 1)
            {
                int error = native.SslGetError(_ssl, ret);
                throw new WebSocketProtocolException($"TLS handshake failed (SSL_connect error={error}).");
            }

            long verifyResult = native.SslGetVerifyResult(_ssl);
            if (verifyResult != 0)
            {
                throw new WebSocketProtocolException($"TLS certificate verification failed (code={verifyResult}).");
            }

            success = true;
        }
        finally
        {
            if (!success)
            {
                if (_ssl != 0)
                {
                    native.SslFree(_ssl);
                    _ssl = 0;
                }

                if (!sslOwnsFd)
                {
                    LibcClose(dupFd);
                }

                if (_ctx != 0)
                {
                    native.SslCtxFree(_ctx);
                    _ctx = 0;
                }
            }
        }
    }

    /// <summary>
    /// SSL_read를 호출하여 복호화된 데이터를 읽습니다.
    /// _disposed 체크로 이미 종료된 스트림에서의 읽기를 방지합니다.
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenSslStream));

        fixed (byte* ptr = buffer)
        {
            int ret = _native.SslRead(_ssl, ptr, buffer.Length);
            if (ret > 0)
            {
                return ret;
            }

            // Dispose/InterruptRead에 의한 SSL_read 실패는 정상 종료 경로
            if (_disposed) return 0;

            int error = _native.SslGetError(_ssl, ret);
            if (error == SslErrorZeroReturn)
            {
                return 0;
            }

            // SslGetError 직후 Dispose가 진행됐을 수 있으므로 재확인
            if (_disposed) return 0;

            throw new WebSocketProtocolException($"SSL_read failed (error={error}).");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenSslStream));

        int written = 0;
        while (written < buffer.Length)
        {
            fixed (byte* ptr = buffer[written..])
            {
                int ret = _native.SslWrite(_ssl, ptr, buffer.Length - written);
                if (ret <= 0)
                {
                    int error = _native.SslGetError(_ssl, ret);
                    throw new WebSocketProtocolException($"SSL_write failed (error={error}).");
                }

                written += ret;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// 블로킹 중인 SSL_read를 강제 해제합니다.
    /// dup된 소켓 fd에 shutdown을 호출하여 SSL_read가 에러와 함께 반환되도록 합니다.
    /// CloseTransport에서 Thread.Join 이전에 호출하여, 소켓 Shutdown이 실패한 경우에도
    /// 수신 스레드를 확실히 깨울 수 있습니다.
    /// </summary>
    internal void InterruptRead()
    {
        int fd = _dupFd;
        if (fd > 0)
        {
            LibcShutdown(fd, ShutRdWr);
        }
    }

    /// <summary>
    /// SSL/SSL_CTX 네이티브 리소스를 해제합니다.
    /// 수신 스레드가 확실히 종료된 후에만 호출해야 합니다 (Thread.Join 성공 후 또는 수신 스레드 자신이 호출).
    /// Dispose()와 분리하여, 타이밍에 의존하지 않는 확정적 안전성을 보장합니다.
    /// </summary>
    internal void FreeSslResources()
    {
        // CAS로 이중 해제 방지 (CloseTransport과 Dispose가 동시 호출될 경우)
        if (Interlocked.Exchange(ref _sslFreed, 1) == 1)
        {
            return;
        }

        if (_native is not null)
        {
            if (_ssl != 0)
            {
                _native.SslShutdown(_ssl);
                _native.SslFree(_ssl);
                _ssl = 0;
                _dupFd = 0; // SslFree가 내부적으로 fd를 닫으므로 댕글링 방지
            }

            if (_ctx != 0)
            {
                _native.SslCtxFree(_ctx);
                _ctx = 0;
            }
        }
    }

    /// <summary>
    /// 스트림을 종료 상태로 전환하고 블로킹 read를 깨웁니다.
    /// SSL 네이티브 리소스는 해제하지 않습니다 — FreeSslResources()가 수신 스레드
    /// 종료 후 별도 호출됩니다. 이렇게 분리하여 SSL_read 실행 중 SslFree가
    /// 호출되는 use-after-free race condition을 구조적으로 차단합니다.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 블로킹 SSL_read를 깨워 수신 스레드가 종료할 수 있게 한다.
        // SslFree는 여기서 호출하지 않는다 — FreeSslResources()에서만 호출.
        InterruptRead();

        base.Dispose(disposing);
    }

    [DllImport("libc", EntryPoint = "dup")]
    private static extern int LibcDup(int oldfd);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int LibcClose(int fd);

    [DllImport("libc", EntryPoint = "fcntl")]
    private static extern int LibcFcntl(int fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "shutdown")]
    private static extern int LibcShutdown(int sockfd, int how);

    private sealed class OpenSslNativeMethods
    {
        public delegate nint TlsClientMethodDelegate();
        public delegate nint SslCtxNewDelegate(nint method);
        public delegate void SslCtxFreeDelegate(nint ctx);
        public delegate int SslCtxSetDefaultVerifyPathsDelegate(nint ctx);
        public delegate void SslCtxSetVerifyDelegate(nint ctx, int mode, nint callback);
        public delegate nint SslNewDelegate(nint ctx);
        public delegate void SslFreeDelegate(nint ssl);
        public delegate int SslSetFdDelegate(nint ssl, int fd);
        public delegate long SslCtrlDelegate(nint ssl, int cmd, long larg, byte* parg);
        public delegate int SslConnectDelegate(nint ssl);
        public delegate int SslReadDelegate(nint ssl, byte* buf, int num);
        public delegate int SslWriteDelegate(nint ssl, byte* buf, int num);
        public delegate int SslShutdownDelegate(nint ssl);
        public delegate int SslGetErrorDelegate(nint ssl, int ret);
        public delegate long SslGetVerifyResultDelegate(nint ssl);

        public TlsClientMethodDelegate TlsClientMethod { get; }
        public SslCtxNewDelegate SslCtxNew { get; }
        public SslCtxFreeDelegate SslCtxFree { get; }
        public SslCtxSetDefaultVerifyPathsDelegate SslCtxSetDefaultVerifyPaths { get; }
        public SslCtxSetVerifyDelegate SslCtxSetVerify { get; }
        public SslNewDelegate SslNew { get; }
        public SslFreeDelegate SslFree { get; }
        public SslSetFdDelegate SslSetFd { get; }
        public SslCtrlDelegate SslCtrl { get; }
        public SslConnectDelegate SslConnect { get; }
        public SslReadDelegate SslRead { get; }
        public SslWriteDelegate SslWrite { get; }
        public SslShutdownDelegate SslShutdown { get; }
        public SslGetErrorDelegate SslGetError { get; }
        public SslGetVerifyResultDelegate SslGetVerifyResult { get; }

        private OpenSslNativeMethods(
            TlsClientMethodDelegate tlsClientMethod,
            SslCtxNewDelegate sslCtxNew,
            SslCtxFreeDelegate sslCtxFree,
            SslCtxSetDefaultVerifyPathsDelegate sslCtxSetDefaultVerifyPaths,
            SslCtxSetVerifyDelegate sslCtxSetVerify,
            SslNewDelegate sslNew,
            SslFreeDelegate sslFree,
            SslSetFdDelegate sslSetFd,
            SslCtrlDelegate sslCtrl,
            SslConnectDelegate sslConnect,
            SslReadDelegate sslRead,
            SslWriteDelegate sslWrite,
            SslShutdownDelegate sslShutdown,
            SslGetErrorDelegate sslGetError,
            SslGetVerifyResultDelegate sslGetVerifyResult)
        {
            TlsClientMethod = tlsClientMethod;
            SslCtxNew = sslCtxNew;
            SslCtxFree = sslCtxFree;
            SslCtxSetDefaultVerifyPaths = sslCtxSetDefaultVerifyPaths;
            SslCtxSetVerify = sslCtxSetVerify;
            SslNew = sslNew;
            SslFree = sslFree;
            SslSetFd = sslSetFd;
            SslCtrl = sslCtrl;
            SslConnect = sslConnect;
            SslRead = sslRead;
            SslWrite = sslWrite;
            SslShutdown = sslShutdown;
            SslGetError = sslGetError;
            SslGetVerifyResult = sslGetVerifyResult;
        }

        public static OpenSslNativeMethods? TryLoad()
        {
            if (!TryLoadSsl(out nint handle))
            {
                return null;
            }

            try
            {
                return new OpenSslNativeMethods(
                    GetDelegate<TlsClientMethodDelegate>(handle, "TLS_client_method"),
                    GetDelegate<SslCtxNewDelegate>(handle, "SSL_CTX_new"),
                    GetDelegate<SslCtxFreeDelegate>(handle, "SSL_CTX_free"),
                    GetDelegate<SslCtxSetDefaultVerifyPathsDelegate>(handle, "SSL_CTX_set_default_verify_paths"),
                    GetDelegate<SslCtxSetVerifyDelegate>(handle, "SSL_CTX_set_verify"),
                    GetDelegate<SslNewDelegate>(handle, "SSL_new"),
                    GetDelegate<SslFreeDelegate>(handle, "SSL_free"),
                    GetDelegate<SslSetFdDelegate>(handle, "SSL_set_fd"),
                    GetDelegate<SslCtrlDelegate>(handle, "SSL_ctrl"),
                    GetDelegate<SslConnectDelegate>(handle, "SSL_connect"),
                    GetDelegate<SslReadDelegate>(handle, "SSL_read"),
                    GetDelegate<SslWriteDelegate>(handle, "SSL_write"),
                    GetDelegate<SslShutdownDelegate>(handle, "SSL_shutdown"),
                    GetDelegate<SslGetErrorDelegate>(handle, "SSL_get_error"),
                    GetDelegate<SslGetVerifyResultDelegate>(handle, "SSL_get_verify_result"));
            }
            catch
            {
                return null;
            }
        }

        private static bool TryLoadSsl(out nint handle)
        {
            ReadOnlySpan<string> candidates =
            [
                "libssl.so.3",
                "libssl.so.1.1",
                "libssl.so"
            ];

            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    return true;
                }
            }

            handle = 0;
            return false;
        }

        private static T GetDelegate<T>(nint handle, string symbolName)
            where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(handle, symbolName, out nint fnPtr))
            {
                throw new MissingMethodException($"OpenSSL symbol '{symbolName}' was not found in loaded native library.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }
    }
}
