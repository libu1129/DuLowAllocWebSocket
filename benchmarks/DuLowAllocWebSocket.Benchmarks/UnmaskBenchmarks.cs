using System.Buffers;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>FrameReader.Unmask 격리 처리량 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class UnmaskBenchmarks
{
    private byte[] _data = null!;
    private byte[] _backup = null!;
    private uint _maskKey;

    [Params(64, 256, 1024, 4096, 16384, 65536)]
    public int DataSize;

    [Params(0, 1)]
    public int InitialOffset;

    [GlobalSetup]
    public void Setup()
    {
        _data = ArrayPool<byte>.Shared.Rent(DataSize);
        _backup = new byte[DataSize];
        RandomNumberGenerator.Fill(_backup.AsSpan(0, DataSize));
        _maskKey = 0xDEADBEEF;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        ArrayPool<byte>.Shared.Return(_data);
    }

    [IterationSetup]
    public void IterSetup()
    {
        _backup.AsSpan(0, DataSize).CopyTo(_data);
    }

    [Benchmark]
    public void Unmask()
    {
        int offset = InitialOffset;
        FrameReader.Unmask(_data.AsSpan(0, DataSize), _maskKey, ref offset);
    }
}
