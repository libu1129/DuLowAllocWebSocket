using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>네이티브 호출 전 버퍼 포인터 획득 방식 비교.</summary>
[Config(typeof(DefaultConfig))]
public unsafe class PinnedBufferBenchmarks
{
    private byte[] _source = null!;
    private byte[] _destination = null!;
    private GCHandle _sourceHandle;
    private GCHandle _destinationHandle;
    private byte* _sourcePtr;
    private byte* _destinationPtr;

    [Params(64, 1024, 16384)]
    public int BufferSize;

    [GlobalSetup]
    public void Setup()
    {
        _source = new byte[BufferSize];
        _destination = new byte[BufferSize];
        Random.Shared.NextBytes(_source);

        _sourceHandle = GCHandle.Alloc(_source, GCHandleType.Pinned);
        _destinationHandle = GCHandle.Alloc(_destination, GCHandleType.Pinned);
        _sourcePtr = (byte*)_sourceHandle.AddrOfPinnedObject();
        _destinationPtr = (byte*)_destinationHandle.AddrOfPinnedObject();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_sourceHandle.IsAllocated)
        {
            _sourceHandle.Free();
        }

        if (_destinationHandle.IsAllocated)
        {
            _destinationHandle.Free();
        }
    }

    [Benchmark(Baseline = true)]
    public int FixedEachCall()
    {
        fixed (byte* src = _source)
        fixed (byte* dst = _destination)
        {
            return ProbePointers(src, dst, BufferSize);
        }
    }

    [Benchmark]
    public int PrePinned()
    {
        return ProbePointers(_sourcePtr, _destinationPtr, BufferSize);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbePointers(byte* source, byte* destination, int length)
    {
        int last = length - 1;
        byte value = source[0];
        destination[last] = value;
        return value + destination[last];
    }
}
