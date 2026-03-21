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
    private bool _disposed;

    private const int SslVerifyPeer = 1;
    private const int SslCtrlSetTlsextHostname = 55;
    private const int TlsextNameTypeHostName = 0;
    private const int SslErrorZeroReturn = 6;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;

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

    public override int Read(Span<byte> buffer)
    {
        fixed (byte* ptr = buffer)
        {
            int ret = _native.SslRead(_ssl, ptr, buffer.Length);
            if (ret > 0)
            {
                return ret;
            }

            int error = _native.SslGetError(_ssl, ret);
            if (error == SslErrorZeroReturn)
            {
                return 0;
            }

            throw new WebSocketProtocolException($"SSL_read failed (error={error}).");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
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

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_native is not null)
        {
            if (_ssl != 0)
            {
                _native.SslShutdown(_ssl);
                _native.SslFree(_ssl);
                _ssl = 0;
            }

            if (_ctx != 0)
            {
                _native.SslCtxFree(_ctx);
                _ctx = 0;
            }
        }

        base.Dispose(disposing);
    }

    [DllImport("libc", EntryPoint = "dup")]
    private static extern int LibcDup(int oldfd);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int LibcClose(int fd);

    [DllImport("libc", EntryPoint = "fcntl")]
    private static extern int LibcFcntl(int fd, int cmd, int arg);

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
