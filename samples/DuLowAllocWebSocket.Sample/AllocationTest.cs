using System.Runtime.InteropServices;
using DuLowAllocWebSocket;

public static class AllocationTest
{
    public static async Task RunAsync(string[] args)
    {
        var uri = new Uri(args.Length > 0 ? args[0] : "wss://fstream.binance.com/ws/!bookTicker");

        var options = new WebSocketClientOptions
        {
            ReceiveScratchBufferSize = 256 * 1024,
            SendScratchBufferSize = 64 * 1024,
            MessageBufferSize = 512 * 1024,
            InflateOutputBufferSize = 512 * 1024,
            MaxMessageBytes = 2 * 1024 * 1024,
            AutoPongOnPing = true,
            KeepAliveInterval = TimeSpan.Zero,
            EnablePerMessageDeflate = true,
        };

        using var client = new DuLowAllocWebSocketClient(options);
        using var cts = new CancellationTokenSource();

        const int warmupMessages = 200;
        const int measureMessages = 1000;

        long allocBefore = 0;
        long allocAfter = 0;
        long count = 0;
        var tcs = new TaskCompletionSource();

        client.MessageReceived += (result) =>
        {
            count++;

            if (count == warmupMessages)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                allocBefore = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (count == warmupMessages + measureMessages)
            {
                allocAfter = GC.GetAllocatedBytesForCurrentThread();
                tcs.TrySetResult();
            }
        };

        await client.ConnectAsync(uri, cts.Token);

        Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Runtime:  {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Connected: {uri}");
        Console.WriteLine($"Warming up ({warmupMessages} messages)...");

        await tcs.Task;

        long bytesAllocated = allocAfter - allocBefore;
        Console.WriteLine();
        Console.WriteLine($"=== Allocation Report ===");
        Console.WriteLine($"Messages measured: {measureMessages}");
        Console.WriteLine($"Total bytes allocated on receive thread: {bytesAllocated:N0}");
        Console.WriteLine($"Bytes per message: {(double)bytesAllocated / measureMessages:F2}");
        Console.WriteLine($"Zero-alloc: {(bytesAllocated == 0 ? "YES" : "NO")}");
    }
}
