using System.Diagnostics;
using System.IO.Compression;
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
