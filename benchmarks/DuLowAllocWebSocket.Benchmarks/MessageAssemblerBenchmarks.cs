using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>MessageAssembler.Append 처리량 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class MessageAssemblerBenchmarks
{
    private MessageAssembler _assembler = null!;
    private byte[] _payload = null!;

    [Params(64, 1024, 16384, 65536)]
    public int PayloadSize;

    [Params(1, 4, 16)]
    public int ChunkCount;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);
        _assembler = new MessageAssembler(PayloadSize * 2);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _assembler.Dispose();
    }

    [Benchmark]
    public int Append()
    {
        _assembler.Reset();
        int chunkSize = PayloadSize / ChunkCount;

        for (int i = 0; i < ChunkCount; i++)
        {
            int offset = i * chunkSize;
            int len = (i == ChunkCount - 1) ? PayloadSize - offset : chunkSize;
            _assembler.Append(_payload.AsSpan(offset, len));
        }

        return _assembler.Length;
    }
}
