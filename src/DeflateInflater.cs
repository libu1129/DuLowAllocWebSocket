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
            int reset = inflateReset(ref _stream);
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

                    int ret = inflate(ref _stream, ZSyncFlush);
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
        int ret = inflateInit2_(ref _stream, -15, ZLibVersion, Marshal.SizeOf<ZStream>());
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
            inflateEnd(ref _stream);
            _initialized = false;
        }

        ArrayPool<byte>.Shared.Return(_outputBuffer);
    }

    [DllImport("libz.so.1", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint zlibVersion();

    private static string ZLibVersion => Marshal.PtrToStringAnsi(zlibVersion()) ?? "1.2.11";

    [DllImport("libz.so.1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "inflateInit2_")]
    private static extern int inflateInit2_(ref ZStream strm, int windowBits, string version, int streamSize);

    [DllImport("libz.so.1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int inflate(ref ZStream strm, int flush);

    [DllImport("libz.so.1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int inflateReset(ref ZStream strm);

    [DllImport("libz.so.1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int inflateEnd(ref ZStream strm);

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
