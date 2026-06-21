using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime;
using System.Runtime.InteropServices;
using DuLowAllocWebSocket;

// DuLowAllocWebSocket vs System.Net.WebSockets.ClientWebSocket 라이브 수신 비교 하니스.
//
// 같은 Binance 스트림을 받아 측정 구간 동안:
//   - 메시지당 힙 할당(GC.GetTotalAllocatedBytes)
//   - GC 횟수(gen0/1/2) + 총 일시정지 시간(GC.GetTotalPauseDuration) → 지연 지터의 직접 측정
//   - CPU 시간(Process.TotalProcessorTime) → 효율
//   - 관리 힙/워킹셋 → 메모리 풋프린트
//   - 수신 지연: Binance event time "E"(ms) 대비 로컬 수신 시각 차 → end-to-end(네트워크 포함)
// 를 산출한다.
//
// 운영 방식: 두 클라이언트를 별도 프로세스로 동시에 띄운다(같은 시장 구간 공유, GC/CPU 계정은 격리).
// 측정 경로(스캔·기록)는 사전 할당 버퍼만 사용하여 0 할당이며, 양쪽 클라이언트에 동일하게 적용된다.

internal static class Program
{
    // 수신 스레드(또는 수신 루프 Task)가 쓰고 메인 컨트롤러가 읽는 측정 상태. 단일 writer.
    private static volatile bool _measuring;
    private static long _count;   // 총 수신 메시지(워밍업 포함). 측정 델타로 사용.
    private static long _bytes;   // 총 수신 페이로드 바이트(디코딩 후).

    // 지연 히스토그램: 1ms 버킷 0..2000ms + overflow. 사전 할당, 샘플당 0 할당.
    // Binance E와 로컬 시각이 모두 ms 단위라 분해능은 1ms. 절대값은 서버-로컬 시계 오프셋의 영향을 받으므로
    // 두 클라이언트(동시 실행, 동일 네트워크) 간 분포·꼬리 차이가 비교의 핵심이다.
    private const int BucketCount = 2000;
    private static readonly long[] _hist = new long[BucketCount + 1];
    private static long _latN;
    private static double _latSum;
    private static long _latMin = long.MaxValue;
    private static long _latMax = long.MinValue;

    private static async Task<int> Main(string[] args)
    {
        string client = GetArg(args, "--client", "dualloc");          // dualloc | clientws
        string uriStr = GetArg(args, "--uri", "wss://fstream.binance.com/ws/!bookTicker");
        bool deflate = GetArg(args, "--deflate", "on") == "on";
        int warmupMs = int.Parse(GetArg(args, "--warmup", "15")) * 1000;
        int measureMs = int.Parse(GetArg(args, "--measure", "90")) * 1000;
        string label = GetArg(args, "--label", client);

        var uri = new Uri(uriStr);
        var proc = Process.GetCurrentProcess();
        using var cts = new CancellationTokenSource();

        Console.Error.WriteLine($"[{label}] start client={client} deflate={deflate} uri={uri}");
        Console.Error.WriteLine($"[{label}] runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription}");
        Console.Error.WriteLine($"[{label}] gc_server={GCSettings.IsServerGC} gc_concurrent={(GCSettings.LatencyMode != GCLatencyMode.Batch)} latencyMode={GCSettings.LatencyMode} cpus={Environment.ProcessorCount}");

        // ── 연결 + 수신 시작 ───────────────────────────────────────────────
        DuLowAllocWebSocketClient? du = null;
        ClientWebSocket? cw = null;
        try
        {
            if (client == "dualloc")
            {
                var options = new WebSocketClientOptions
                {
                    ReceiveScratchBufferSize = 256 * 1024,
                    SendScratchBufferSize = 64 * 1024,
                    MessageBufferSize = 512 * 1024,
                    InflateOutputBufferSize = 512 * 1024,
                    MaxMessageBytes = 2 * 1024 * 1024,
                    AutoPongOnPing = true,
                    KeepAliveInterval = TimeSpan.Zero,
                    EnablePerMessageDeflate = deflate,
                    // Binance는 server_no_context_takeover를 강제하므로 옵션을 맞춰 협상 실패를 피한다.
                    ServerContextTakeover = false,
                };
                du = new DuLowAllocWebSocketClient(options);
                du.OnError += ex => Console.Error.WriteLine($"[{label}] OnError: {ex.GetType().Name}: {ex.Message}");
                du.Disconnected += () => Console.Error.WriteLine($"[{label}] Disconnected");
                du.MessageReceived += static result => Record(result.Payload.Span);
                await du.ConnectAsync(uri, cts.Token);
            }
            else
            {
                cw = new ClientWebSocket();
                cw.Options.KeepAliveInterval = TimeSpan.Zero;
                cw.Options.SetRequestHeader("User-Agent", "DuLowAllocWebSocket-Compare/1.0");
                if (deflate)
                {
                    cw.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
                    {
                        ClientMaxWindowBits = 15,
                        ServerMaxWindowBits = 15,
                        ClientContextTakeover = true,
                        ServerContextTakeover = false,
                    };
                }
                await cw.ConnectAsync(uri, cts.Token);
                _ = Task.Run(() => ClientWebSocketReceiveLoop(cw, cts.Token));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{label}] connect failed: {ex}");
            return 1;
        }

        Console.Error.WriteLine($"[{label}] connected. warmup {warmupMs / 1000}s ...");

        // ── 측정 컨트롤러 ─────────────────────────────────────────────────
        await Task.Delay(warmupMs, cts.Token);

        // 워밍업 산출물을 정리해 측정 구간의 gen 카운트/힙 베이스라인을 안정화한다.
        // (GetTotalAllocatedBytes는 누적값이라 Collect 영향을 받지 않는다.)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long c0 = Volatile.Read(ref _count);
        long b0 = Volatile.Read(ref _bytes);
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpu0 = proc.TotalProcessorTime;
        int gen0_0 = GC.CollectionCount(0), gen1_0 = GC.CollectionCount(1), gen2_0 = GC.CollectionCount(2);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        ResetLatency();

        var sw = Stopwatch.StartNew();
        _measuring = true;
        await Task.Delay(measureMs, cts.Token);
        _measuring = false;
        sw.Stop();

        long alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpu1 = proc.TotalProcessorTime;
        int gen0_1 = GC.CollectionCount(0), gen1_1 = GC.CollectionCount(1), gen2_1 = GC.CollectionCount(2);
        TimeSpan pause1 = GC.GetTotalPauseDuration();
        long c1 = Volatile.Read(ref _count);
        long b1 = Volatile.Read(ref _bytes);
        proc.Refresh();
        long workingSet = proc.WorkingSet64;
        long managedHeap = GC.GetTotalMemory(forceFullCollection: false);

        // ── 결과 산출 ─────────────────────────────────────────────────────
        double secs = sw.Elapsed.TotalSeconds;
        long msgs = c1 - c0;
        long bytes = b1 - b0;
        long allocBytes = alloc1 - alloc0;
        double cpuMs = (cpu1 - cpu0).TotalMilliseconds;
        double pauseMs = (pause1 - pause0).TotalMilliseconds;
        double msgPerS = msgs / secs;
        double allocPerMsg = msgs > 0 ? (double)allocBytes / msgs : 0;
        double cpuPerMsgUs = msgs > 0 ? cpuMs * 1000.0 / msgs : 0;
        double pausePct = secs > 0 ? pauseMs / (secs * 1000.0) * 100.0 : 0;

        long latMin = _latN > 0 ? _latMin : 0;
        long latMax = _latN > 0 ? _latMax : 0;
        double latMean = _latN > 0 ? _latSum / _latN : 0;

        // 기계 판독용 한 줄(러너가 awk로 파싱).
        Console.WriteLine(
            $"RESULT label={label} client={client} deflate={(deflate ? "on" : "off")} " +
            $"secs={secs:F1} msgs={msgs} msg_per_s={msgPerS:F1} bytes={bytes} bytes_per_msg={(msgs > 0 ? (double)bytes / msgs : 0):F1} " +
            $"alloc_bytes={allocBytes} alloc_per_msg={allocPerMsg:F2} " +
            $"gen0={gen0_1 - gen0_0} gen1={gen1_1 - gen1_0} gen2={gen2_1 - gen2_0} " +
            $"gc_pause_ms={pauseMs:F2} gc_pause_pct={pausePct:F4} " +
            $"cpu_ms={cpuMs:F1} cpu_per_msg_us={cpuPerMsgUs:F2} " +
            $"heap_bytes={managedHeap} ws_bytes={workingSet} " +
            $"lat_n={_latN} lat_min={latMin} lat_p50={Pct(50)} lat_p90={Pct(90)} lat_p99={Pct(99)} lat_p999={Pct(99.9)} lat_max={latMax} lat_mean={latMean:F2}");

        // ── 종료 ──────────────────────────────────────────────────────────
        try
        {
            cts.Cancel();
            if (cw is not null && cw.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(2000);
                try { await cw.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token); } catch { }
            }
            du?.Dispose();
            cw?.Dispose();
        }
        catch { }

        return 0;
    }

    /// <summary>
    /// 메시지 1건 소비. 두 클라이언트 경로가 호출하는 동일 작업: 카운트 + (측정 중) Binance "E" 추출 후 지연 기록.
    /// 사전 할당 버퍼만 쓰며 할당이 없다.
    /// </summary>
    private static void Record(ReadOnlySpan<byte> payload)
    {
        _count++;
        _bytes += payload.Length;
        if (!_measuring)
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int idx = payload.IndexOf("\"E\":"u8);
        if (idx < 0)
        {
            return;
        }

        long e = ParseUnsignedDigits(payload, idx + 4);
        if (e <= 0)
        {
            return;
        }

        long d = nowMs - e;
        _latSum += d;
        _latN++;
        if (d < _latMin) _latMin = d;
        if (d > _latMax) _latMax = d;
        int bucket = d <= 0 ? 0 : (d >= BucketCount ? BucketCount : (int)d);
        _hist[bucket]++;
    }

    private static long ParseUnsignedDigits(ReadOnlySpan<byte> s, int start)
    {
        long v = 0;
        for (int i = start; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9')
            {
                break;
            }
            v = v * 10 + (c - (byte)'0');
        }
        return v;
    }

    private static void ResetLatency()
    {
        Array.Clear(_hist);
        _latN = 0;
        _latSum = 0;
        _latMin = long.MaxValue;
        _latMax = long.MinValue;
    }

    /// <summary>지정 백분위에 해당하는 지연(ms)을 히스토그램에서 산출. 1ms 분해능.</summary>
    private static long Pct(double p)
    {
        if (_latN <= 0)
        {
            return 0;
        }
        long target = (long)Math.Ceiling(p / 100.0 * _latN);
        if (target < 1) target = 1;
        long cum = 0;
        for (int b = 0; b <= BucketCount; b++)
        {
            cum += _hist[b];
            if (cum >= target)
            {
                return b;
            }
        }
        return BucketCount;
    }

    /// <summary>
    /// ClientWebSocket 저할당 수신 루프: 재사용 버퍼에 Memory 오버로드로 수신, EndOfMessage까지 조립 후 소비.
    /// ClientWebSocket의 가장 효율적인 사용법으로 베이스라인을 공정하게 측정한다.
    /// </summary>
    private static async Task ClientWebSocketReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[512 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int total = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (total >= buffer.Length)
                    {
                        // 예상 밖 대용량: 버퍼 확장(정상 시장 데이터에서는 도달하지 않음).
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    r = await ws.ReceiveAsync(buffer.AsMemory(total), ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    total += r.Count;
                }
                while (!r.EndOfMessage);

                Record(buffer.AsSpan(0, total));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clientws] receive loop error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetArg(string[] args, string key, string def)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
            {
                return args[i + 1];
            }
        }
        return def;
    }
}
