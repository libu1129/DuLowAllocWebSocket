using System.Text;
using DuLowAllocWebSocket;

// Binance USDⓈ-M Futures: All Book Tickers Stream
// Docs: https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/All-Book-Tickers-Stream
var uri = new Uri(args.Length > 0 ? args[0] : "wss://fstream.binance.com/ws/!bookTicker");

var options = new WebSocketClientOptions
{
    // For market data bursts, favor upfront buffer allocation.
    ReceiveScratchBufferSize = 256 * 1024,
    SendScratchBufferSize = 64 * 1024,
    MessageBufferSize = 512 * 1024,
    InflateOutputBufferSize = 512 * 1024,
    MaxMessageBytes = 2 * 1024 * 1024,

    // Binance stream usage: server-driven keepalive is typically enough.
    AutoPongOnPing = true,
    PingMode = WebSocketPingMode.ServerDriven,

    // Compression can remain enabled; if server does not negotiate it, client continues uncompressed.
    EnablePerMessageDeflate = true,

    // Optional proxy tunnel
    // ProxyHost = "127.0.0.1",
    // ProxyPort = 8080,
    // ProxyUsername = "user",
    // ProxyPassword = "pass",
};

using var client = new DuLowAllocWebSocketClient(options);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await client.ConnectAsync(uri, cts.Token);
Console.WriteLine($"Connected: {uri}");
Console.WriteLine("Receiving all symbol best bid/ask updates (raw JSON, no deserialize)...");

long count = 0;
while (!cts.IsCancellationRequested)
{
    ReadOnlyMemory<byte> payload = await client.ReceiveAsync(cts.Token);
    count++;

    // Deserialize 없이 raw payload 출력
    string json = Encoding.UTF8.GetString(payload.Span);
    Console.WriteLine($"#{count} [{payload.Length} bytes] {json}");
}
