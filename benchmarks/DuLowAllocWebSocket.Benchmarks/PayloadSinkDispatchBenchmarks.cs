using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>IPayloadSink.Append 호출 방식별 오버헤드 벤치마크.</summary>
[Config(typeof(DefaultConfig))]
public class PayloadSinkDispatchBenchmarks
{
    private byte[] _payload = null!;
    private CountingPayloadSink _sink = null!;
    private IPayloadSink _interfaceSink = null!;

    [Params(0, 16, 256, 4096)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[Math.Max(PayloadSize, 1)];
        Random.Shared.NextBytes(_payload);
        _sink = new CountingPayloadSink();
        _interfaceSink = _sink;
    }

    [Benchmark(Baseline = true)]
    public int DirectConcrete()
    {
        _sink.Reset();
        _sink.Append(_payload.AsSpan(0, PayloadSize));
        return _sink.Length;
    }

    [Benchmark]
    public int InterfaceField()
    {
        _sink.Reset();
        _interfaceSink.Append(_payload.AsSpan(0, PayloadSize));
        return _sink.Length;
    }

    [Benchmark]
    public int GenericConstrained()
    {
        _sink.Reset();
        AppendGeneric(_sink, _payload.AsSpan(0, PayloadSize));
        return _sink.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AppendGeneric<TSink>(TSink sink, ReadOnlySpan<byte> data)
        where TSink : IPayloadSink
    {
        sink.Append(data);
    }

    private sealed class CountingPayloadSink : IPayloadSink
    {
        public int Length { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(ReadOnlySpan<byte> data)
        {
            Length += data.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            Length = 0;
        }
    }
}
