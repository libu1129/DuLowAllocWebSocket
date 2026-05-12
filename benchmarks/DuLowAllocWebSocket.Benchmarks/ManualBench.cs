using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using DuLowAllocWebSocket.Benchmarks.Helpers;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>
/// BenchmarkDotNet 자식 프로세스 호환성 문제 회피용 수동 마이크로벤치마크.
/// dotnet run -- manual 로 실행.
/// </summary>
public static class ManualBench
{
    public static void Run()
    {
        // 파이프 모드에서 Console 출력 버퍼링 방지
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"zlib: {DeflateInflater.ZLibVersion ?? "N/A"}");
        Console.WriteLine();

        BenchInflate();
        Console.WriteLine();
        BenchReceivePipeline();
        Console.WriteLine();
        BenchPayloadSinkDispatch();
        Console.WriteLine();
        BenchZeroCopyReceive();
    }

    private static void BenchInflate()
    {
        if (!DeflateInflater.IsSupported)
        {
            Console.WriteLine("[SKIP] DeflateInflater — zlib not available");
            return;
        }

        Console.WriteLine("=== DeflateInflater ===");
        Console.WriteLine($"{"Size",8} {"SingleShot",14} {"Streaming",14} {"Alloc",8}");

        foreach (int size in new[] { 256, 4096, 65536 })
        {
            var payload = MakeJson(size);
            var compressed = RawDeflate(payload);

            using var inflater = new DeflateInflater(noContextTakeover: false, initialOutputSize: size * 2);

            // warmup
            for (int i = 0; i < 100; i++)
                inflater.Inflate(compressed);

            // SingleShot
            long before = GC.GetAllocatedBytesForCurrentThread();
            int iters = size < 4096 ? 50_000 : 5_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++)
                inflater.Inflate(compressed);
            sw.Stop();
            long after = GC.GetAllocatedBytesForCurrentThread();
            double singleNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long allocPerOp = (after - before) / iters;

            // Streaming (4KB chunks)
            inflater.Inflate(compressed); // warmup streaming
            before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < iters; i++)
            {
                inflater.BeginMessage();
                int chunkSize = 4096;
                int offset = 0;
                while (offset < compressed.Length)
                {
                    int len = Math.Min(chunkSize, compressed.Length - offset);
                    inflater.AppendCompressed(compressed.AsSpan(offset, len));
                    offset += len;
                }
                inflater.FinishMessage();
            }
            sw.Stop();
            after = GC.GetAllocatedBytesForCurrentThread();
            double streamNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long streamAllocPerOp = (after - before) / iters;

            Console.WriteLine($"{size,8} {singleNs,11:F1} ns {streamNs,11:F1} ns {Math.Max(allocPerOp, streamAllocPerOp),5} B");
        }
    }

    private static void BenchReceivePipeline()
    {
        Console.WriteLine("=== ReceivePipeline E2E ===");
        Console.WriteLine($"{"Size",8} {"Compressed",11} {"Mean",14} {"Alloc",8}");

        foreach (int size in new[] { 1024, 16384 })
        {
            foreach (bool compressed in new[] { false, true })
            {
                if (compressed && !DeflateInflater.IsSupported)
                {
                    Console.WriteLine($"{size,8} {compressed,11} {"SKIP",14} {"N/A",8}");
                    continue;
                }

                var payload = MakeJson(size);
                byte[] frameBytes;
                DeflateInflater? inflater = null;

                if (compressed)
                {
                    frameBytes = FrameBuilder.BuildCompressedFrame(payload);
                    inflater = new DeflateInflater(noContextTakeover: true, initialOutputSize: size * 2);
                }
                else
                {
                    frameBytes = FrameBuilder.BuildUnmaskedTextFrame(payload);
                }

                var stream = new LoopingMemoryStream(frameBytes);
                var options = new WebSocketClientOptions
                {
                    ReceiveScratchBufferSize = 256 * 1024,
                    MaxMessageBytes = 4 * 1024 * 1024,
                };
                var reader = new FrameReader(stream, options);
                var assembler = new MessageAssembler(size * 2);

                // warmup
                for (int i = 0; i < 1000; i++)
                    DoReceive(reader, assembler, inflater, compressed);

                int iters = 50_000;
                GC.Collect(2, GCCollectionMode.Forced, true);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++)
                    DoReceive(reader, assembler, inflater, compressed);
                sw.Stop();
                long after = GC.GetAllocatedBytesForCurrentThread();
                double ns = (double)sw.Elapsed.TotalNanoseconds / iters;
                long allocPerOp = (after - before) / iters;

                Console.WriteLine($"{size,8} {compressed,11} {ns,11:F1} ns {allocPerOp,5} B");

                reader.Dispose();
                assembler.Dispose();
                inflater?.Dispose();
            }
        }
    }

    private static int DoReceive(FrameReader reader, MessageAssembler assembler, DeflateInflater? inflater, bool compressed)
    {
        assembler.Reset();
        var header = reader.ReadHeader();

        if (compressed && inflater is not null)
        {
            inflater.BeginMessage();
            reader.ReadPayloadInto(header, inflater);
            return inflater.FinishMessage().Length;
        }
        else
        {
            reader.ReadPayloadInto(header, assembler);
            return assembler.Length;
        }
    }

    private static void BenchPayloadSinkDispatch()
    {
        Console.WriteLine("=== IPayloadSink dispatch ===");
        Console.WriteLine($"{"Size",8} {"Direct",14} {"Interface",14} {"Generic",14} {"Alloc",8}");

        foreach (int size in new[] { 0, 16, 256, 4096 })
        {
            var payload = new byte[Math.Max(size, 1)];
            Random.Shared.NextBytes(payload);

            var sink = new CountingPayloadSink();
            IPayloadSink interfaceSink = sink;
            int iters = size <= 16 ? 20_000_000 : 5_000_000;

            for (int i = 0; i < 10_000; i++)
            {
                DirectAppend(sink, payload.AsSpan(0, size));
                InterfaceAppend(interfaceSink, payload.AsSpan(0, size));
                GenericAppend(sink, payload.AsSpan(0, size));
            }

            sink.Reset();
            GC.Collect(2, GCCollectionMode.Forced, true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++)
            {
                DirectAppend(sink, payload.AsSpan(0, size));
            }
            sw.Stop();
            long after = GC.GetAllocatedBytesForCurrentThread();
            double directNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long directAlloc = (after - before) / iters;

            sink.Reset();
            before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < iters; i++)
            {
                InterfaceAppend(interfaceSink, payload.AsSpan(0, size));
            }
            sw.Stop();
            after = GC.GetAllocatedBytesForCurrentThread();
            double interfaceNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long interfaceAlloc = (after - before) / iters;

            sink.Reset();
            before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < iters; i++)
            {
                GenericAppend(sink, payload.AsSpan(0, size));
            }
            sw.Stop();
            after = GC.GetAllocatedBytesForCurrentThread();
            double genericNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long genericAlloc = (after - before) / iters;

            long alloc = Math.Max(directAlloc, Math.Max(interfaceAlloc, genericAlloc));
            Console.WriteLine($"{size,8} {directNs,11:F2} ns {interfaceNs,11:F2} ns {genericNs,11:F2} ns {alloc,5} B");
        }
    }

    private static void BenchZeroCopyReceive()
    {
        Console.WriteLine("=== Zero-copy receive candidate ===");
        Console.WriteLine($"{"Size",8} {"Assemble",14} {"ZeroCopy",14} {"Alloc",8}");

        foreach (int size in new[] { 64, 1024, 16384, 65536 })
        {
            var payload = new byte[size];
            Random.Shared.NextBytes(payload);
            var frameBytes = FrameBuilder.BuildUnmaskedTextFrame(payload);
            var options = new WebSocketClientOptions
            {
                ReceiveScratchBufferSize = 256 * 1024,
                MaxMessageBytes = 4 * 1024 * 1024,
            };

            using var assembleReader = new FrameReader(new LoopingMemoryStream(frameBytes), options);
            using var zeroCopyReader = new FrameReader(new LoopingMemoryStream(frameBytes), options);
            using var assembler = new MessageAssembler(Math.Max(size * 2, 16 * 1024));
            using var fallbackAssembler = new MessageAssembler(Math.Max(size * 2, 16 * 1024));

            for (int i = 0; i < 10_000; i++)
            {
                ReceiveAssemble(assembleReader, assembler);
                ReceiveZeroCopy(zeroCopyReader, fallbackAssembler);
            }

            int iters = size <= 1024 ? 5_000_000 : 500_000;

            GC.Collect(2, GCCollectionMode.Forced, true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++)
            {
                ReceiveAssemble(assembleReader, assembler);
            }
            sw.Stop();
            long after = GC.GetAllocatedBytesForCurrentThread();
            double assembleNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long assembleAlloc = (after - before) / iters;

            before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < iters; i++)
            {
                ReceiveZeroCopy(zeroCopyReader, fallbackAssembler);
            }
            sw.Stop();
            after = GC.GetAllocatedBytesForCurrentThread();
            double zeroCopyNs = (double)sw.Elapsed.TotalNanoseconds / iters;
            long zeroCopyAlloc = (after - before) / iters;

            Console.WriteLine($"{size,8} {assembleNs,11:F1} ns {zeroCopyNs,11:F1} ns {Math.Max(assembleAlloc, zeroCopyAlloc),5} B");
        }
    }

    private static int ReceiveAssemble(FrameReader reader, MessageAssembler assembler)
    {
        assembler.Reset();
        var header = reader.ReadHeader();
        reader.ReadPayloadInto(header, assembler);
        return assembler.Length;
    }

    private static int ReceiveZeroCopy(FrameReader reader, MessageAssembler fallbackAssembler)
    {
        var header = reader.ReadHeader();
        if (reader.TryReadPayloadAsMemory(header, out var payload))
        {
            return payload.Length;
        }

        fallbackAssembler.Reset();
        reader.ReadPayloadInto(header, fallbackAssembler);
        return fallbackAssembler.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DirectAppend(CountingPayloadSink sink, ReadOnlySpan<byte> data)
    {
        sink.Append(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InterfaceAppend(IPayloadSink sink, ReadOnlySpan<byte> data)
    {
        sink.Append(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GenericAppend<TSink>(TSink sink, ReadOnlySpan<byte> data)
        where TSink : IPayloadSink
    {
        sink.Append(data);
    }

    private sealed class CountingPayloadSink : IPayloadSink
    {
        public long Length { get; private set; }

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

    private static byte[] MakeJson(int size)
    {
        var sb = new StringBuilder(size + 256);
        while (sb.Length < size)
            sb.Append("""{"s":"BTCUSDT","b":"67890.12","B":"1.234","a":"67890.56","A":"0.567"}""").Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString(0, Math.Min(sb.Length, size)));
    }

    private static byte[] RawDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(data);
        var result = ms.ToArray();
        if (result.Length >= 4 && result[^4] == 0x00 && result[^3] == 0x00 && result[^2] == 0xFF && result[^1] == 0xFF)
            return result[..^4];
        return result;
    }
}
