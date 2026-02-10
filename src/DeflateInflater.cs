using System.Buffers;
using System.Runtime.InteropServices;

namespace DuLowAllocWebSocket;

public sealed unsafe class DeflateInflater : IDisposable
{
    private const int ZOk = 0;
    private const int ZStreamEnd = 1;
    private const int ZBufError = -5;
    private const int ZSyncFlush = 2;

    private readonly bool _noContextTakeover;
    private static readonly ZLibNativeMethods Native = ZLibNativeMethods.Load();
    private ZStream _stream;
    private bool _initialized;
    private byte[] _outputBuffer;

    public DeflateInflater(bool noContextTakeover, int initialOutputSize = 16 * 1024)
    {
        _noContextTakeover = noContextTakeover;
        _outputBuffer = ArrayPool<byte>.Shared.Rent(initialOutputSize);
        Initialize();
    }

    public ReadOnlyMemory<byte> Inflate(ReadOnlySpan<byte> source)
    {
        if (_noContextTakeover)
        {
            int reset = Native.InflateReset(ref _stream);
            if (reset != ZOk)
            {
                throw new WebSocketProtocolException($"inflateReset failed: {reset}");
            }
        }

        // RFC7692 7.2.2: append 0x00 0x00 0xff 0xff to terminate raw-deflate message.
        Span<byte> tail = stackalloc byte[] { 0x00, 0x00, 0xFF, 0xFF };

        int outputWritten = 0;
        InflateChunk(source, ref outputWritten, isTail: false);
        InflateChunk(tail, ref outputWritten, isTail: true);

        return _outputBuffer.AsMemory(0, outputWritten);
    }

    private void InflateChunk(ReadOnlySpan<byte> source, ref int outputWritten, bool isTail)
    {
        fixed (byte* src = source)
        {
            _stream.next_in = src;
            _stream.avail_in = (uint)source.Length;

            while (true)
            {
                EnsureOutputSpace(outputWritten + 1024);

                fixed (byte* dst = &_outputBuffer[outputWritten])
                {
                    _stream.next_out = dst;
                    uint beforeAvailOut = (uint)(_outputBuffer.Length - outputWritten);
                    _stream.avail_out = beforeAvailOut;

                    int ret = Native.Inflate(ref _stream, ZSyncFlush);
                    int produced = (int)(beforeAvailOut - _stream.avail_out);
                    outputWritten += produced;

                    if (ret == ZOk || ret == ZStreamEnd)
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

                        EnsureOutputSpace(_outputBuffer.Length * 2);
                        continue;
                    }

                    throw new WebSocketProtocolException($"inflate failed: {ret} (tail={isTail})");
                }
            }
        }
    }

    private void EnsureOutputSpace(int min)
    {
        if (_outputBuffer.Length >= min)
        {
            return;
        }

        int size = _outputBuffer.Length;
        while (size < min)
        {
            size *= 2;
        }

        var next = ArrayPool<byte>.Shared.Rent(size);
        _outputBuffer.AsSpan().CopyTo(next);
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
        int ret = Native.InflateInit2(ref _stream, -15, Native.Version, Marshal.SizeOf<ZStream>());
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
            Native.InflateEnd(ref _stream);
            _initialized = false;
        }

        ArrayPool<byte>.Shared.Return(_outputBuffer);
    }

    private sealed class ZLibNativeMethods
    {
        public delegate nint ZlibVersionDelegate();
        public delegate int InflateInit2Delegate(ref ZStream strm, int windowBits, string version, int streamSize);
        public delegate int InflateDelegate(ref ZStream strm, int flush);
        public delegate int InflateResetDelegate(ref ZStream strm);
        public delegate int InflateEndDelegate(ref ZStream strm);

        public string Version { get; }
        public InflateInit2Delegate InflateInit2 { get; }
        public InflateDelegate Inflate { get; }
        public InflateResetDelegate InflateReset { get; }
        public InflateEndDelegate InflateEnd { get; }

        private ZLibNativeMethods(
            nint libHandle,
            string version,
            InflateInit2Delegate inflateInit2,
            InflateDelegate inflate,
            InflateResetDelegate inflateReset,
            InflateEndDelegate inflateEnd)
        {
            _ = libHandle;
            Version = version;
            InflateInit2 = inflateInit2;
            Inflate = inflate;
            InflateReset = inflateReset;
            InflateEnd = inflateEnd;
        }

        public static ZLibNativeMethods Load()
        {
            nint handle = TryLoadZlib();

            var zlibVersion = GetDelegate<ZlibVersionDelegate>(handle, "zlibVersion");
            var inflateInit2 = GetDelegate<InflateInit2Delegate>(handle, "inflateInit2_");
            var inflate = GetDelegate<InflateDelegate>(handle, "inflate");
            var inflateReset = GetDelegate<InflateResetDelegate>(handle, "inflateReset");
            var inflateEnd = GetDelegate<InflateEndDelegate>(handle, "inflateEnd");

            string version = Marshal.PtrToStringAnsi(zlibVersion()) ?? "1.2.11";

            return new ZLibNativeMethods(handle, version, inflateInit2, inflate, inflateReset, inflateEnd);
        }

        private static nint TryLoadZlib()
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
                if (NativeLibrary.TryLoad(candidate, out nint handle))
                {
                    return handle;
                }
            }

            throw new DllNotFoundException(
                "Unable to load zlib native library. Tried: zlib1.dll, libz.so.1, libz.so, libz.dylib.");
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
        public nuint total_in;
        public byte* next_out;
        public uint avail_out;
        public nuint total_out;
        public nint msg;
        public nint state;
        public nint zalloc;
        public nint zfree;
        public nint opaque;
        public int data_type;
        public uint adler;
        public uint reserved;
    }
}
