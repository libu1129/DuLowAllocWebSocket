namespace DuLowAllocWebSocket.Benchmarks;

internal static class BenchmarkFixtureVerify
{
    internal static void Run()
    {
        foreach (var size in new[] { 256, 4096, 65536 })
        foreach (var chunks in new[] { 1, 4 })
        {
            var fixture = new DeflateInflaterBenchmarks { OriginalSize = size, ChunkCount = chunks };
            try
            {
                fixture.Setup();
                for (var i = 0; i < 8; i++)
                    if (fixture.Inflate_SingleShot().Length != size || fixture.Inflate_Streaming().Length != size)
                        throw new InvalidOperationException("Repeated inflate lost the payload.");
            }
            finally { fixture.Cleanup(); }
        }
        foreach (var size in new[] { 0, 16, 256, 4096 })
        {
            var fixture = new PayloadSinkDispatchBenchmarks { PayloadSize = size };
            fixture.Setup();
            if (fixture.DirectConcrete() != size || fixture.InterfaceField() != size || fixture.GenericConstrained() != size)
                throw new InvalidOperationException("Payload sink dispatch outputs differ.");
        }
        Console.WriteLine("PASS: split inflate payloads and symmetric sink dispatch fixtures");
    }
}
