using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DuLowAllocWebSocket;

/// <summary>
/// permessage-deflate(RFC7692) 압축 해제기입니다.
/// 네이티브 zlib P/Invoke로 inflate를 수행하며, 출력 버퍼를
/// <see cref="ArrayPool{T}.Shared"/>에서 관리하여 steady-state 할당을 회피합니다.
/// </summary>
public sealed unsafe class DeflateInflater : IPayloadSink, IDisposable
{
    private const int ZOk = 0;
    private const int ZStreamEnd = 1;
    private const int ZBufError = -5;
    private const int ZSyncFlush = 2;

    private readonly bool _noContextTakeover;
    private readonly ZLibNativeMethods _native;
    private static readonly Lazy<ZLibNativeMethods?> Native = new(ZLibNativeMethods.TryLoad);
    private static readonly int ZStreamSize = Marshal.SizeOf<ZStream>();
    private ZStream _stream;
    private bool _initialized;
    private byte[] _outputBuffer;
    private int _outputWritten;

    public static bool IsSupported => Native.Value is not null;

    /// <summary>
    /// 로드된 zlib 라이브러리의 버전 문자열입니다 (예: "1.2.13", "1.3.1.zlib-ng").
    /// zlib-ng compat 빌드는 버전에 "zlib-ng" 접미사가 포함됩니다.
    /// 라이브러리가 로드되지 않았으면 <see langword="null"/>을 반환합니다.
    /// </summary>
    public static string? ZLibVersion => Native.Value?.Version;

    public static bool TryValidateNativeZlib(out string? error)
    {
        if (Native.Value is null)
        {
            error = "zlib native library was not loaded.";
            return false;
        }

        var native = Native.Value;
        ZStream stream = default;
        int initRet = native.InflateInit2(ref stream, -15, native.VersionPtr, ZStreamSize);
        if (initRet != ZOk)
        {
            error = $"inflateInit2_ returned {initRet} (zlibVersion={native.Version}).";
            return false;
        }

        int endRet = native.InflateEnd(ref stream);
        if (endRet != ZOk)
        {
            error = $"inflateEnd returned {endRet} (zlibVersion={native.Version}).";
            return false;
        }

        error = null;
        return true;
    }

    public DeflateInflater(bool noContextTakeover, int initialOutputSize = 16 * 1024)
    {
        _native = Native.Value ?? throw new DllNotFoundException(
            "zlib native library is not available. Disable permessage-deflate or install zlib (Windows: zlib1.dll, Linux: libz.so.1)."
        );

        _noContextTakeover = noContextTakeover;
        _outputBuffer = ArrayPool<byte>.Shared.Rent(initialOutputSize);
        Initialize();
    }

    /// <summary>
    /// 전체 압축 데이터를 한 번에 inflate합니다.
    /// 스트리밍이 불필요한 단순 호출용 편의 메서드입니다.
    /// </summary>
    public ReadOnlyMemory<byte> Inflate(ReadOnlySpan<byte> source)
    {
        BeginMessage();
        AppendCompressed(source);
        return FinishMessage();
    }

    /// <summary>
    /// 스트리밍 inflate 세션을 시작합니다. 출력 위치를 초기화하고,
    /// no_context_takeover 설정 시 zlib 스트림을 리셋합니다.
    /// 이후 <see cref="AppendCompressed"/>로 압축 청크를 공급하고,
    /// <see cref="FinishMessage"/>로 최종 결과를 수집합니다.
    /// </summary>
    public void BeginMessage()
    {
        _outputWritten = 0;
        if (_noContextTakeover)
        {
            ResetOrReinitializeStream();
        }
    }

    /// <summary>
    /// 압축된 데이터 청크를 zlib에 공급하여 즉시 inflate합니다.
    /// <see cref="FrameReader"/>가 읽은 청크를 중간 버퍼 복사 없이 직접 전달할 때 사용합니다.
    /// </summary>
    public void AppendCompressed(ReadOnlySpan<byte> chunk)
    {
        InflateChunk(chunk, ref _outputWritten, isTail: false);
    }

    /// <summary>
    /// <see cref="IPayloadSink"/> 구현. <see cref="FrameReader.ReadPayloadInto"/>에서
    /// 페이로드 청크를 직접 inflate 파이프라인에 공급합니다.
    /// </summary>
    void IPayloadSink.Append(ReadOnlySpan<byte> data) => AppendCompressed(data);

    /// <summary>
    /// 스트리밍 inflate 세션을 완료합니다. RFC7692 tail 바이트(0x00 0x00 0xFF 0xFF)를
    /// 추가하여 deflate 스트림을 종결하고, 전체 해제된 결과를 반환합니다.
    /// </summary>
    public ReadOnlyMemory<byte> FinishMessage()
    {
        // RFC7692 7.2.2: append 0x00 0x00 0xff 0xff to terminate raw-deflate message.
        Span<byte> tail = stackalloc byte[] { 0x00, 0x00, 0xFF, 0xFF };
        InflateChunk(tail, ref _outputWritten, isTail: true);
        return _outputBuffer.AsMemory(0, _outputWritten);
    }

    private void InflateChunk(ReadOnlySpan<byte> source, ref int outputWritten, bool isTail)
    {
        fixed (byte* src = source)
        {
            _stream.next_in = src;
            _stream.avail_in = (uint)source.Length;

            // JSON 압축률 ~3:1~5:1 고려하여 4배 프리사이징.
            // 충분한 출력 공간을 미리 확보해 inflate 루프 반복 횟수를 최소화한다.
            EnsureOutputSpace(outputWritten + Math.Max(source.Length * 4, 1024), outputWritten);

            while (true)
            {
                fixed (byte* dst = &_outputBuffer[outputWritten])
                {
                    _stream.next_out = dst;
                    uint beforeAvailOut = (uint)(_outputBuffer.Length - outputWritten);
                    _stream.avail_out = beforeAvailOut;

                    int ret = _native.Inflate(ref _stream, ZSyncFlush);
                    outputWritten += (int)(beforeAvailOut - _stream.avail_out);

                    if (ret == ZStreamEnd)
                    {
                        // 스트림 종료 — 남은 입력과 무관하게 즉시 반환.
                        // tail(00 00 FF FF) inflate 시 일부 zlib 구현체가
                        // avail_in > 0인 채로 Z_STREAM_END를 반환할 수 있다.
                        return;
                    }

                    if (ret == ZOk)
                    {
                        if (_stream.avail_in == 0)
                        {
                            return;
                        }

                        continue;
                    }

                    if (ret == ZBufError)
                    {
                        if (_stream.avail_in == 0)
                        {
                            return;
                        }

                        // 출력 버퍼 부족 — 2배 확장 후 재시도
                        EnsureOutputSpace(_outputBuffer.Length * 2, outputWritten);
                        continue;
                    }

                    ReinitializeStream();
                    throw new WebSocketProtocolException($"inflate failed: {ret} (tail={isTail})");
                }
            }
        }
    }

    private void ResetOrReinitializeStream()
    {
        // Ensure no stale managed buffer pointers remain in z_stream between messages.
        _stream.next_in = null;
        _stream.avail_in = 0;
        _stream.next_out = null;
        _stream.avail_out = 0;

        int reset = _native.InflateReset(ref _stream);
        if (reset == ZOk)
        {
            return;
        }

        // Some zlib builds can return Z_STREAM_ERROR (-2) if the stream state became invalid.
        // For no_context_takeover, full re-init is protocol-safe and gives us a deterministic fallback.
        ReinitializeStream();
    }

    private void ReinitializeStream()
    {
        if (_initialized)
        {
            _native.InflateEnd(ref _stream);
            _initialized = false;
        }

        _stream = default;

        int initRet = _native.InflateInit2(ref _stream, -15, _native.VersionPtr, ZStreamSize);
        if (initRet != ZOk)
        {
            throw new WebSocketProtocolException($"inflate reinitialize failed: {initRet}");
        }

        _initialized = true;
    }

    /// <summary>
    /// 출력 버퍼가 <paramref name="min"/> 바이트 이상인지 확인합니다.
    /// fast path(크기 충분)는 인라이닝되어 비교 1회로 완료되고,
    /// slow path(리사이즈)는 별도 메서드로 분리하여 호출자 코드 크기를 최소화합니다.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureOutputSpace(int min, int preserveBytes)
    {
        if (_outputBuffer.Length < min)
        {
            GrowOutputBuffer(min, preserveBytes);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowOutputBuffer(int min, int preserveBytes)
    {
        long size = _outputBuffer.Length;
        while (size < min)
        {
            size *= 2;
        }

        if (size > Array.MaxLength)
        {
            size = Array.MaxLength;
        }

        var next = ArrayPool<byte>.Shared.Rent((int)size);
        _outputBuffer.AsSpan(0, preserveBytes).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_outputBuffer);
        _outputBuffer = next;
    }

    private void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _stream = default;
        int ret = _native.InflateInit2(ref _stream, -15, _native.VersionPtr, ZStreamSize);
        if (ret != ZOk)
        {
            throw new WebSocketProtocolException($"inflateInit2 failed: {ret}");
        }

        _initialized = true;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _native.InflateEnd(ref _stream);
            _initialized = false;
        }

        byte[]? buf = Interlocked.Exchange(ref _outputBuffer!, null!);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static ZLibNativeMethods GetNative()
    {
        return Native.Value ?? throw new DllNotFoundException(
            "zlib native library is not available. Disable permessage-deflate or install zlib (Windows: zlib1.dll, Linux: libz.so.1)."
        );
    }

    private sealed class ZLibNativeMethods
    {
        public delegate nint ZlibVersionDelegate();
        public delegate int InflateInit2Delegate(ref ZStream strm, int windowBits, nint version, int streamSize);
        public delegate int InflateDelegate(ref ZStream strm, int flush);
        public delegate int InflateResetDelegate(ref ZStream strm);
        public delegate int InflateEndDelegate(ref ZStream strm);

        public nint VersionPtr { get; }
        public string Version { get; }
        public InflateInit2Delegate InflateInit2 { get; }
        public InflateDelegate Inflate { get; }
        public InflateResetDelegate InflateReset { get; }
        public InflateEndDelegate InflateEnd { get; }

        private ZLibNativeMethods(
            nint libHandle,
            nint versionPtr,
            string version,
            InflateInit2Delegate inflateInit2,
            InflateDelegate inflate,
            InflateResetDelegate inflateReset,
            InflateEndDelegate inflateEnd)
        {
            _ = libHandle;
            VersionPtr = versionPtr;
            Version = version;
            InflateInit2 = inflateInit2;
            Inflate = inflate;
            InflateReset = inflateReset;
            InflateEnd = inflateEnd;
        }

        public static ZLibNativeMethods? TryLoad()
        {
            if (!TryLoadZlib(out nint handle))
            {
                return null;
            }

            try
            {
                var zlibVersion = GetDelegate<ZlibVersionDelegate>(handle, "zlibVersion");
                var inflateInit2 = GetDelegate<InflateInit2Delegate>(handle, "inflateInit2_");
                var inflate = GetDelegate<InflateDelegate>(handle, "inflate");
                var inflateReset = GetDelegate<InflateResetDelegate>(handle, "inflateReset");
                var inflateEnd = GetDelegate<InflateEndDelegate>(handle, "inflateEnd");

                nint versionPtr = zlibVersion();
                string version = Marshal.PtrToStringAnsi(versionPtr) ?? "1.2.11";

                return new ZLibNativeMethods(handle, versionPtr, version, inflateInit2, inflate, inflateReset, inflateEnd);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryLoadZlib(out nint handle)
        {
            ReadOnlySpan<string> candidates =
            [
                "zlib1.dll",
                "libz.so.1",
                "libz.so",
                "libz.dylib"
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
                throw new MissingMethodException($"zlib symbol '{symbolName}' was not found in loaded native library.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ZStream
    {
        public byte* next_in;
        public uint avail_in;
        public CULong total_in;
        public byte* next_out;
        public uint avail_out;
        public CULong total_out;
        public nint msg;
        public nint state;
        public nint zalloc;
        public nint zfree;
        public nint opaque;
        public int data_type;
        public CULong adler;
        public CULong reserved;
    }
}
