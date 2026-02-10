using System.Text;
using DuLowAllocWebSocket;

var uri = new Uri(args.Length > 0 ? args[0] : "wss://127.0.0.1:8443/ws");

var options = new WebSocketClientOptions
{
    // upfront large allocations to reduce runtime growth for bursty market-data streams
    ReceiveScratchBufferSize = 128 * 1024,
    SendScratchBufferSize = 128 * 1024,
    MessageBufferSize = 1024 * 1024,
    InflateOutputBufferSize = 1024 * 1024,
    MaxMessageBytes = 8 * 1024 * 1024,
    RejectMaskedServerFrames = true,
    EnablePerMessageDeflate = true,
    ClientContextTakeover = true,
    ServerContextTakeover = true,
    ClientMaxWindowBits = 15,
    ServerMaxWindowBits = 15,

    // Ping policy
    AutoPongOnPing = true,
    PingMode = WebSocketPingMode.ClientDrivenAuto,
    ClientPingInterval = TimeSpan.FromSeconds(15),
    ClientPingPayload = "hb"u8.ToArray(),

    // Optional proxy tunnel
    // ProxyHost = "127.0.0.1",
    // ProxyPort = 8080,
    // ProxyUsername = "user",
    // ProxyPassword = "pass",
};

using var client = new RawWebSocketClient(options);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await client.ConnectAsync(uri, cts.Token);
Console.WriteLine($"Connected to {uri}");

ReadOnlyMemory<byte> hello = "subscribe:book"u8.ToArray();
await client.SendAsync(hello, WebSocketOpcode.Text, cts.Token);
// Optional manual client-priority ping call
// await client.SendPingAsync("hb"u8.ToArray(), cts.Token);

while (!cts.IsCancellationRequested)
{
    ReadOnlyMemory<byte> payload = await client.ReceiveAsync(cts.Token);

    // Demo output only. In HFT path, parse payload as binary in-place.
    string msg = Encoding.UTF8.GetString(payload.Span);
    Console.WriteLine($"[{payload.Length} bytes] {msg}");
}
