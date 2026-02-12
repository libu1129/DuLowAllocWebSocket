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

    // Binance stream usage: server ping에 대한 자동 pong만으로도 충분한 경우가 많습니다.
    AutoPongOnPing = true,

    // 필요할 때만 클라이언트 주도 ping 활성화(0이면 비활성화).
    KeepAliveInterval = TimeSpan.Zero,

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
client.MessageReceived += (result) =>
{
    count++;

    //// Deserialize 없이 raw payload 출력
    //string json = Encoding.UTF8.GetString(result.Payload.Span);
    //Console.WriteLine($"#{count} [{result.Payload.Length} bytes] {json}");
};

Console.ReadKey();