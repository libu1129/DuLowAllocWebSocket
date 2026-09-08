using System.Buffers;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>FrameReader.Unmask 격리 처리량 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class UnmaskBenchmarks
{
    private const int BatchSize = 64;
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
        _backup.CopyTo(_data, 0);
        var expected = (byte[])_backup.Clone();
        ReadOnlySpan<byte> maskBytes = [0xDE, 0xAD, 0xBE, 0xEF];
        for (var i = 0; i < expected.Length; i++)
            expected[i] ^= maskBytes[(InitialOffset + i) & 3];
        var offset = InitialOffset;
        FrameReader.Unmask(_data.AsSpan(0, DataSize), _maskKey, ref offset);
        if (!_data.AsSpan(0, DataSize).SequenceEqual(expected))
            throw new InvalidOperationException("Unmask fixture differs from scalar XOR.");
        _backup.CopyTo(_data, 0);
        Unmask();
        if (!_data.AsSpan(0, DataSize).SequenceEqual(_backup))
            throw new InvalidOperationException("Even XOR batch must restore the input.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        ArrayPool<byte>.Shared.Return(_data);
    }

    // XOR는 데이터 값에 따른 분기가 없고 짝수 반복 뒤 원본으로 돌아온다.
    // IterationSetup의 단발 invocation 제약 없이 BDN이 충분한 측정 길이를 선택한다.
    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void Unmask()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            int offset = InitialOffset;
            FrameReader.Unmask(_data.AsSpan(0, DataSize), _maskKey, ref offset);
        }
    }
}
