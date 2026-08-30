using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class DuLowAllocWebSocketClientReceiveTests
{
    [Fact]
    public void Constructor_DoesNotCreateMessageAssemblerBeforeFallback()
    {
        using var client = CreateClient();

        Assert.Null(GetNullableField<MessageAssembler>(client, "_messageAssembler"));
    }

    [Fact]
    public void Constructor_DoesNotRentControlAssemblerBeforeFallback()
    {
        var controlPool = new TrackingByteArrayPool();
        using var client = new DuLowAllocWebSocketClient(
            CreateOptions(),
            ArrayPool<byte>.Shared,
            controlPool);

        Assert.Null(GetNullableField<MessageAssembler>(client, "_controlAssembler"));
        Assert.Equal(0, controlPool.RentCount);

        client.Dispose();
        Assert.Equal(0, controlPool.ReturnCount);
        Assert.Equal(0, controlPool.OutstandingCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenFirstFrameArrivesWithHandshake_DeliversFirstMessage()
    {
        byte[] expected = Encoding.UTF8.GetBytes("first");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: true,
            BuildFrame(WebSocketOpcode.Text, expected));

        using var client = CreateClient();
        var received = new TaskCompletionSource<(WebSocketOpcode Opcode, byte[] Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult((result.Opcode, result.Payload.ToArray()));
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Text, result.Opcode);
        Assert.Equal(expected, result.Payload);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenSingleFrameArrivesInPartialWrites_UsesScratchWithoutAssembler()
    {
        byte[] expected = Enumerable.Range(1, 40).Select(static value => (byte)value).ToArray();
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketInPartialWritesAsync(
            listener,
            BuildFrame(WebSocketOpcode.Binary, expected),
            firstWriteLength: 7);

        using var client = CreateClient();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (result.IsClose)
            {
                return;
            }

            try
            {
                FrameReader reader = GetField<FrameReader>(client, "_frameReader");
                byte[] scratch = GetNullableObjectField<byte[]>(reader, "_scratch")
                    ?? throw new InvalidOperationException("FrameReader scratch was already released.");
                Assert.True(MemoryMarshal.TryGetArray(result.Payload, out ArraySegment<byte> segment));
                Assert.Same(scratch, segment.Array);
                Assert.Null(GetNullableField<MessageAssembler>(client, "_messageAssembler"));
                received.TrySetResult(result.Payload.ToArray());
            }
            catch (Exception ex)
            {
                received.TrySetException(ex);
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        Assert.Equal(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocketHandshake_WhenFirstFrameArrivesWithHandshake_PreservesInitialFrameInTransport()
    {
        byte[] expected = Encoding.UTF8.GetBytes("first");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: true,
            BuildFrame(WebSocketOpcode.Text, expected));

        var options = CreateOptions();
        var handshake = new WebSocketHandshake();
        var result = await handshake.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), options, CancellationToken.None);

        using var socket = result.Socket;
        using var transport = result.Transport;
        using var reader = new FrameReader(transport, options);

        FrameHeader header = reader.ReadHeader();
        Assert.Equal(WebSocketOpcode.Text, header.Opcode);
        Assert.Equal(expected, ReadPayload(reader, header));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenTextMessageIsFragmented_PreservesTextOpcode()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: false,
            BuildFrame(WebSocketOpcode.Text, Encoding.UTF8.GetBytes("hel"), fin: false),
            BuildFrame(WebSocketOpcode.Continuation, Encoding.UTF8.GetBytes("lo")));

        using var client = CreateClient();
        var received = new TaskCompletionSource<(WebSocketOpcode Opcode, byte[] Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult((result.Opcode, result.Payload.ToArray()));
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Text, result.Opcode);
        Assert.Equal("hello", Encoding.UTF8.GetString(result.Payload));
        Assert.NotNull(GetNullableField<MessageAssembler>(client, "_messageAssembler"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenServerFrameIsMasked_UsesLazyAssemblerAndUnmasksPayload()
    {
        byte[] expected = Encoding.UTF8.GetBytes("masked-server-payload");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: false,
            BuildFrame(WebSocketOpcode.Binary, expected, masked: true));
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
            ReceiveScratchBufferSize = 64,
            RejectMaskedServerFrames = false,
        };

        using var client = new DuLowAllocWebSocketClient(options);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult(result.Payload.ToArray());
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        Assert.Equal(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(GetNullableField<MessageAssembler>(client, "_messageAssembler"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenSingleFrameExceedsScratch_UsesLazyAssembler()
    {
        byte[] expected = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: false,
            BuildFrame(WebSocketOpcode.Binary, expected));

        using var client = CreateClient();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult(result.Payload.ToArray());
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        Assert.Equal(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(GetNullableField<MessageAssembler>(client, "_messageAssembler"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AutoPong_ConstructorAndConnectedClientWithoutPing_DoNotRentSlots()
    {
        var pool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task serverTask = ServeUntilClientDisconnectAsync(listener);
        using var client = new DuLowAllocWebSocketClient(CreateOptions(), pool);

        Assert.Equal(0, pool.RentCount);
        Assert.Null(GetNullableObjectField<byte[]>(client, "_autoPongSlots"));

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Null(GetNullableObjectField<byte[]>(client, "_autoPongSlots"));

        client.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task AutoPong_WhenNoThrottle_SendsPongWithSamePayload()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("plain-ping");

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingAndReadPongAsync(listener, pingPayload);

        using var client = CreateClient();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
    }

    [Fact]
    public async Task ControlFrames_WhenCompliantAndUnmasked_UseScratchWithoutAssemblerLease()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("scratch-ping");
        byte[] closePayload = [0x03, 0xE8, .. Encoding.UTF8.GetBytes("done")];
        var controlPool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task<((WebSocketOpcode Opcode, byte[] Payload) Pong, (WebSocketOpcode Opcode, byte[] Payload) Close)> serverTask =
            ServeCompliantControlsAsync(listener, pingPayload, closePayload);
        using var client = new DuLowAllocWebSocketClient(
            CreateOptions(),
            ArrayPool<byte>.Shared,
            controlPool);
        var closeReceived = new TaskCompletionSource<DuLowAllocWebSocketReceiveResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (result.IsClose)
            {
                closeReceived.TrySetResult(result);
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var frames = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        var close = await closeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Pong, frames.Pong.Opcode);
        Assert.Equal(pingPayload, frames.Pong.Payload);
        Assert.Equal(WebSocketOpcode.Close, frames.Close.Opcode);
        Assert.Equal(closePayload, frames.Close.Payload);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
        Assert.Equal("done", close.CloseStatusDescription);
        Assert.Null(GetNullableField<MessageAssembler>(client, "_controlAssembler"));
        Assert.Equal(0, controlPool.RentCount);
        Assert.Equal(0, controlPool.ReturnCount);

        client.Dispose();
        Assert.Equal(0, controlPool.OutstandingCount);
    }

    [Fact]
    public async Task ControlFrame_WhenMaskedAndAllowed_RentsFallbackAssemblerExactlyOnce()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("masked-control");
        var controlPool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingAndReadPongAsync(listener, pingPayload, masked: true);
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
            ReceiveScratchBufferSize = 64,
            RejectMaskedServerFrames = false,
        };
        using var client = new DuLowAllocWebSocketClient(
            options,
            ArrayPool<byte>.Shared,
            controlPool);

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
        Assert.NotNull(GetNullableField<MessageAssembler>(client, "_controlAssembler"));
        Assert.Equal(1, controlPool.RentCount);
        Assert.Equal(0, controlPool.ReturnCount);

        client.Dispose();
        Assert.Equal(1, controlPool.ReturnCount);
        Assert.Equal(0, controlPool.OutstandingCount);
    }

    [Fact]
    public async Task ControlFrame_WhenPayloadExceedsRfcLimit_UsesLenientFallbackAssembler()
    {
        byte[] oversizedPong = Enumerable.Range(0, 126).Select(static value => (byte)value).ToArray();
        byte[] pingPayload = Encoding.UTF8.GetBytes("after-oversized-pong");
        var controlPool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePongThenPingAndReadPongAsync(listener, oversizedPong, pingPayload);
        using var client = new DuLowAllocWebSocketClient(
            CreateOptions(),
            ArrayPool<byte>.Shared,
            controlPool);

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
        Assert.NotNull(GetNullableField<MessageAssembler>(client, "_controlAssembler"));
        Assert.Equal(1, controlPool.RentCount);
        Assert.Equal(0, controlPool.ReturnCount);

        client.Dispose();
        Assert.Equal(1, controlPool.ReturnCount);
        Assert.Equal(0, controlPool.OutstandingCount);
    }

    [Fact]
    public async Task ControlFrame_WhenDisposeRacesFirstFallbackRent_ReturnsLeaseExactlyOnce()
    {
        var controlPool = new TrackingByteArrayPool(blockFirstRent: true);
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: false,
            BuildFrame(WebSocketOpcode.Pong, "blocked-fallback"u8, masked: true));
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
            ReceiveScratchBufferSize = 64,
            RejectMaskedServerFrames = false,
        };
        using var client = new DuLowAllocWebSocketClient(
            options,
            ArrayPool<byte>.Shared,
            controlPool);

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await controlPool.FirstRentEntered.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposeTask = Task.Run(client.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => GetField<int>(client, "_closing") != 0,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            controlPool.ReleaseFirstRent();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, controlPool.RentCount);
        Assert.Equal(1, controlPool.ReturnCount);
        Assert.Equal(0, controlPool.OutstandingCount);
        Assert.Null(GetNullableField<MessageAssembler>(client, "_controlAssembler"));
    }

    [Fact]
    public async Task AutoPong_UsesSharedThreadPoolWorkItemAndReturnsToIdle()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("thread-pool-ping");
        var throttle = new CapturingControlFrameThrottle();
        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingAndReadPongAsync(listener, pingPayload);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = throttle
        };
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
        Assert.True(throttle.RanOnThreadPool);
        Assert.Null(typeof(DuLowAllocWebSocketClient).GetField(
            "_autoPongThread",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(SpinWait.SpinUntil(
            () => GetField<int>(client, "_autoPongWorkerScheduled") == 0,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AutoPong_WhenNextPingArrivesAfterIdle_RequeuesReusableWorkItem()
    {
        byte[] firstPing = Encoding.UTF8.GetBytes("first-ping");
        byte[] secondPing = Encoding.UTF8.GetBytes("second-ping");
        var pool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task<(byte[] First, byte[] Second)> serverTask =
            ServeTwoPingsAndReadPongsAsync(listener, firstPing, secondPing);

        using var client = new DuLowAllocWebSocketClient(CreateOptions(), pool);
        Assert.Equal(0, pool.RentCount);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        var pongs = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstPing, pongs.First);
        Assert.Equal(secondPing, pongs.Second);
        Assert.True(SpinWait.SpinUntil(
            () => GetField<int>(client, "_autoPongWorkerScheduled") == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(1, pool.OutstandingCount);

        client.Dispose();
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task AutoPong_WhenThrottleDisposesClientOnWorker_DoesNotDeadlockAndReleasesQueue()
    {
        var pool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        Task serverTask = ServePingAndWaitForDisconnectAsync(listener);
        using var client = new DuLowAllocWebSocketClient(CreateOptions(), pool);
        var throttle = new DisposingControlFrameThrottle(client);
        client.ControlFrameThrottle = throttle;

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        await throttle.DisposeReturned.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Closed, client.State);
        Assert.True(SpinWait.SpinUntil(
            () => GetNullableObjectField<byte[]>(client, "_autoPongSlots") is null &&
                  GetField<int>(client, "_autoPongWorkerScheduled") == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task AutoPong_WhenDisposeWinsFirstLazyRent_ReturnsLeaseExactlyOnce()
    {
        var pool = new TrackingByteArrayPool(blockFirstRent: true);
        using var listener = StartListener(out int port);
        Task serverTask = ServePingAndWaitForDisconnectAsync(listener);
        using var client = new DuLowAllocWebSocketClient(CreateOptions(), pool);

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await pool.FirstRentEntered.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposeTask = Task.Run(client.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => GetField<int>(client, "_closing") != 0,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            pool.ReleaseFirstRent();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Null(GetNullableObjectField<byte[]>(client, "_autoPongSlots"));
        Assert.Equal(0, GetField<int>(client, "_autoPongWorkerScheduled"));
    }

    [Fact]
    public async Task AutoPong_ReconnectGenerations_OwnIndependentLazyLeases()
    {
        var pool = new TrackingByteArrayPool();

        for (int generation = 1; generation <= 2; generation++)
        {
            byte[] pingPayload = Encoding.UTF8.GetBytes($"generation-{generation}");
            using var listener = StartListener(out int port);
            Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
                ServePingAndReadPongAsync(listener, pingPayload);
            using var client = new DuLowAllocWebSocketClient(CreateOptions(), pool);

            Assert.Null(GetNullableObjectField<byte[]>(client, "_autoPongSlots"));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
            var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
            Assert.Equal(pingPayload, pong.Payload);
            Assert.Equal(generation, pool.RentCount);

            client.Dispose();
            Assert.Equal(generation, pool.ReturnCount);
            Assert.Equal(0, pool.OutstandingCount);
        }
    }

    [Fact]
    public async Task AutoPong_WhenControlFrameThrottleWaits_DeliversNextMessageBeforePong()
    {
        byte[] pingPayload = Encoding.UTF8.GetBytes("p1");
        byte[] textPayload = Encoding.UTF8.GetBytes("after-ping");
        var throttle = new BlockingControlFrameThrottle();

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServePingThenTextAndReadPongAsync(listener, pingPayload, textPayload);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = throttle
        };

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (!result.IsClose)
            {
                received.TrySetResult(result.Payload.ToArray());
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        Assert.Equal(WebSocketOpcode.Pong, await throttle.Entered.WaitAsync(TimeSpan.FromSeconds(5)));
        byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(textPayload, result);
        Assert.False(serverTask.IsCompleted);

        throttle.Release();
        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pingPayload, pong.Payload);
    }

    [Fact]
    public async Task AutoPong_WhenThrottleFails_ReportsErrorAndDisconnects()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServePingAndWaitForDisconnectAsync(listener);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = new ThrowingControlFrameThrottle()
        };
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnError += ex => errorReported.TrySetResult(ex);
        client.Disconnected += () => disconnected.TrySetResult();

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        Exception error = await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<IOException>(error);
        Assert.Equal(WebSocketState.Closed, client.State);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendPongAsync_WhenControlFrameThrottleWaits_DelaysPongUntilReleased()
    {
        byte[] pongPayload = Encoding.UTF8.GetBytes("manual-pong");
        var throttle = new BlockingControlFrameThrottle();

        using var listener = StartListener(out int port);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask = ServeAndReadClientFrameAsync(listener);

        using var client = new DuLowAllocWebSocketClient(CreateOptions())
        {
            ControlFrameThrottle = throttle
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        Task sendTask = client.SendPongAsync(pongPayload).AsTask();

        Assert.Equal(WebSocketOpcode.Pong, await throttle.Entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(serverTask.IsCompleted);

        throttle.Release();
        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        var pong = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Pong, pong.Opcode);
        Assert.Equal(pongPayload, pong.Payload);
    }

    [Fact]
    public async Task CloseAsync_WhenReceivePumpIsWaiting_UsesPumpForCloseHandshake()
    {
        byte[] precedingPayload = Encoding.UTF8.GetBytes("before-close");

        using var listener = StartListener(out int port);
        Task serverTask = ServeClientCloseAsync(listener, precedingPayload);

        using var client = CreateClient();
        var textReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (result.IsClose)
                closeReceived.TrySetResult();
            else
                textReceived.TrySetResult(result.Payload.ToArray());
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(precedingPayload, await textReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await closeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Closed, client.State);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnexpectedEof_ClosesStateAndSubsequentCloseDoesNotWaitForever()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(listener, appendFramesToHandshake: false);

        using var client = CreateClient();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketState.Closed, client.State);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "already closed")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CloseAsync_WhenReceivePumpClosesWhileSendLockIsWaiting_DoesNotLoseWakeup()
    {
        using var listener = StartListener(out int port);
        var halfCloseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(WebSocketOpcode Opcode, byte[] Payload)> serverTask =
            ServeHalfCloseThenReadClientCloseAsync(listener, halfCloseRequested.Task);

        using var client = CreateClient();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        SemaphoreSlim sendLock = GetField<SemaphoreSlim>(client, "_sendLock");
        await sendLock.WaitAsync();
        Task closeTask = client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done").AsTask();
        try
        {
            halfCloseRequested.TrySetResult();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketState.Closed, client.State);
        }
        finally
        {
            sendLock.Release();
        }

        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var close = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketOpcode.Close, close.Opcode);
    }

    [Fact]
    public async Task AutoPing_WhenWriteFails_ReportsErrorAndDisconnects()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeUntilClientDisconnectAsync(listener);
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.FromMilliseconds(250),
            ReceiveScratchBufferSize = 64,
        };

        using var client = new DuLowAllocWebSocketClient(options);
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnError += ex => errorReported.TrySetResult(ex);
        client.Disconnected += () => disconnected.TrySetResult();

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        FrameWriter originalWriter = GetField<FrameWriter>(client, "_frameWriter");
        SetField(client, "_frameWriter", new FrameWriter(new ThrowingWriteStream(), options));
        originalWriter.Dispose();

        Exception error = await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<IOException>(error);
        Assert.Equal(WebSocketState.Closed, client.State);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendAsync_WhenFrameWriteFails_ReportsErrorAndDisconnects()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeUntilClientDisconnectAsync(listener);
        var options = CreateOptions();

        using var client = new DuLowAllocWebSocketClient(options);
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnError += ex => errorReported.TrySetResult(ex);
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        FrameWriter originalWriter = GetField<FrameWriter>(client, "_frameWriter");
        SetField(client, "_frameWriter", new FrameWriter(new ThrowingWriteStream(), options));
        originalWriter.Dispose();

        await Assert.ThrowsAsync<IOException>(
            () => client.SendAsync("fail"u8.ToArray(), WebSocketOpcode.Text).AsTask());
        Exception error = await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<IOException>(error);
        Assert.Equal(WebSocketState.Closed, client.State);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendAsync_WhenFirstConcurrentWriteFails_BlocksQueuedWriterBeforeTransportWrite()
    {
        using var listener = StartListener(out int port);
        Task serverTask = ServeUntilClientDisconnectAsync(listener);
        var options = CreateOptions();

        using var client = new DuLowAllocWebSocketClient(options);
        var writeStream = new BlockingThrowingWriteStream();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);

        FrameWriter originalWriter = GetField<FrameWriter>(client, "_frameWriter");
        SetField(client, "_frameWriter", new FrameWriter(writeStream, options));
        originalWriter.Dispose();

        Task firstSend = client.SendAsync("first"u8.ToArray(), WebSocketOpcode.Text).AsTask();
        await writeStream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Task queuedSend = client.SendAsync("queued"u8.ToArray(), WebSocketOpcode.Text).AsTask();
        writeStream.Release();

        await Assert.ThrowsAsync<IOException>(() => firstSend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queuedSend);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, writeStream.WriteAttempts);
        Assert.Equal(1, GetField<int>(client, "_sendFaulted"));
        Assert.Equal(WebSocketState.Closed, client.State);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenCallbackDisposesClient_KeepsZeroCopyPayloadAliveUntilCallbackReturns()
    {
        byte[] expected = Encoding.UTF8.GetBytes("payload-owned-by-frame-reader");
        using var listener = StartListener(out int port);
        Task serverTask = ServeWebSocketAsync(
            listener,
            appendFramesToHandshake: true,
            BuildFrame(WebSocketOpcode.Text, expected));

        using var client = CreateClient();
        var callbackCompleted = new TaskCompletionSource<(byte[] Payload, FrameReader Reader)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (result.IsClose)
            {
                return;
            }

            try
            {
                ReadOnlyMemory<byte> payload = result.Payload;
                FrameReader reader = GetField<FrameReader>(client, "_frameReader");
                byte[] scratch = GetNullableObjectField<byte[]>(reader, "_scratch")
                    ?? throw new InvalidOperationException("FrameReader scratch was already released.");
                Assert.True(MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> payloadSegment));
                Assert.Same(scratch, payloadSegment.Array);

                client.Dispose();
                Assert.Equal(0, GetField<int>(client, "_receiveResourcesDisposed"));
                Assert.Same(reader, GetNullableField<FrameReader>(client, "_frameReader"));
                Assert.Same(scratch, GetNullableObjectField<byte[]>(reader, "_scratch"));
                callbackCompleted.TrySetResult((payload.ToArray(), reader));
            }
            catch (Exception ex)
            {
                callbackCompleted.TrySetException(ex);
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var received = await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, received.Payload);
        Assert.True(SpinWait.SpinUntil(
            () => GetNullableObjectField<byte[]>(received.Reader, "_scratch") is null,
            TimeSpan.FromSeconds(5)));
        Assert.Null(GetNullableField<FrameReader>(client, "_frameReader"));
        Assert.Equal(1, GetField<int>(client, "_receiveResourcesDisposed"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MessageReceived_WhenCompressedCallbackDisposesClient_KeepsInflatedPayloadAliveUntilCallbackReturns()
    {
        if (!DeflateInflater.IsSupported)
        {
            return;
        }

        byte[] expected = Encoding.UTF8.GetBytes("payload-owned-by-deflate-inflater");
        byte[] compressed = RawDeflate(expected);
        using var listener = StartListener(out int port);
        Task serverTask = ServeCompressedWebSocketAsync(
            listener,
            BuildFrame(WebSocketOpcode.Text, compressed, rsv1: true));
        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = true,
            KeepAliveInterval = TimeSpan.Zero,
            ReceiveScratchBufferSize = 64,
            InflateOutputBufferSize = 64,
        };

        using var client = new DuLowAllocWebSocketClient(options);
        var callbackCompleted = new TaskCompletionSource<(byte[] Payload, DeflateInflater Inflater)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += result =>
        {
            if (result.IsClose)
            {
                return;
            }

            try
            {
                ReadOnlyMemory<byte> payload = result.Payload;
                DeflateInflater inflater = GetField<DeflateInflater>(client, "_inflater");
                byte[] outputBuffer = GetNullableObjectField<byte[]>(inflater, "_outputBuffer")
                    ?? throw new InvalidOperationException("Inflater output buffer was already released.");
                Assert.True(MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> payloadSegment));
                Assert.Same(outputBuffer, payloadSegment.Array);
                Assert.Null(GetNullableField<MessageAssembler>(client, "_messageAssembler"));

                client.Dispose();
                Assert.Equal(0, GetField<int>(client, "_receiveResourcesDisposed"));
                Assert.Same(inflater, GetNullableField<DeflateInflater>(client, "_inflater"));
                Assert.Same(outputBuffer, GetNullableObjectField<byte[]>(inflater, "_outputBuffer"));
                callbackCompleted.TrySetResult((payload.ToArray(), inflater));
            }
            catch (Exception ex)
            {
                callbackCompleted.TrySetException(ex);
            }
        };

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        var received = await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, received.Payload);
        Assert.True(SpinWait.SpinUntil(
            () => GetNullableObjectField<byte[]>(received.Inflater, "_outputBuffer") is null,
            TimeSpan.FromSeconds(5)));
        Assert.Null(GetNullableField<DeflateInflater>(client, "_inflater"));
        Assert.Equal(1, GetField<int>(client, "_receiveResourcesDisposed"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectAsync_WhenAutoPongQueueCapacityIsInvalid_FailsBeforeOpeningConnection()
    {
        var pool = new TrackingByteArrayPool();
        using var listener = StartListener(out int port);
        using var client = new DuLowAllocWebSocketClient(new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
            AutoPongQueueCapacity = 0,
        }, pool);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None));

        Assert.Contains("AutoPongQueueCapacity", ex.Message);
        Assert.Equal(WebSocketState.None, client.State);
        Assert.False(listener.Pending());
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    private static DuLowAllocWebSocketClient CreateClient() => new(CreateOptions());

    private static WebSocketClientOptions CreateOptions() => new()
    {
        EnablePerMessageDeflate = false,
        KeepAliveInterval = TimeSpan.Zero,
        ReceiveScratchBufferSize = 64,
    };

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task ServeWebSocketAsync(
        TcpListener listener,
        bool appendFramesToHandshake,
        params byte[][] frames)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        byte[] payload = Concat(frames);
        if (appendFramesToHandshake)
        {
            await stream.WriteAsync(Concat(response, payload));
        }
        else
        {
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            await Task.Delay(50);
            await stream.WriteAsync(payload);
        }

        await stream.FlushAsync();
        await Task.Delay(200);
    }

    private static async Task ServeWebSocketInPartialWritesAsync(
        TcpListener listener,
        byte[] frame,
        int firstWriteLength)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        if (firstWriteLength < 2 || firstWriteLength >= frame.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstWriteLength));
        }

        await stream.WriteAsync(response);
        await stream.WriteAsync(frame.AsMemory(0, firstWriteLength));
        await stream.FlushAsync();
        await Task.Delay(100);
        await stream.WriteAsync(frame.AsMemory(firstWriteLength));
        await stream.FlushAsync();
        await Task.Delay(200);
    }

    private static async Task ServeCompressedWebSocketAsync(TcpListener listener, byte[] frame)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "Sec-WebSocket-Extensions: permessage-deflate\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
        await Task.Delay(200);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServePingAndReadPongAsync(
        TcpListener listener,
        byte[] pingPayload,
        bool masked = false)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, pingPayload, masked: masked));
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task<((WebSocketOpcode Opcode, byte[] Payload) Pong, (WebSocketOpcode Opcode, byte[] Payload) Close)>
        ServeCompliantControlsAsync(TcpListener listener, byte[] pingPayload, byte[] closePayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, pingPayload));
        await stream.FlushAsync();
        var pong = await ReadClientFrameAsync(stream);

        await stream.WriteAsync(Concat(
            BuildFrame(WebSocketOpcode.Pong, "server-pong"u8),
            BuildFrame(WebSocketOpcode.Close, closePayload)));
        await stream.FlushAsync();
        var close = await ReadClientFrameAsync(stream);
        return (pong, close);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServePongThenPingAndReadPongAsync(
        TcpListener listener,
        byte[] pongPayload,
        byte[] pingPayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(Concat(
            BuildFrame(WebSocketOpcode.Pong, pongPayload),
            BuildFrame(WebSocketOpcode.Ping, pingPayload)));
        await stream.FlushAsync();
        return await ReadClientFrameAsync(stream);
    }

    private static async Task<(byte[] First, byte[] Second)> ServeTwoPingsAndReadPongsAsync(
        TcpListener listener,
        byte[] firstPing,
        byte[] secondPing)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, firstPing));
        await stream.FlushAsync();
        var firstPong = await ReadClientFrameAsync(stream);
        Assert.Equal(WebSocketOpcode.Pong, firstPong.Opcode);

        // 첫 work item이 빈 큐를 관찰하고 idle로 돌아갈 시간을 준 뒤 동일 객체를 다시 예약한다.
        await Task.Delay(100);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, secondPing));
        await stream.FlushAsync();
        var secondPong = await ReadClientFrameAsync(stream);
        Assert.Equal(WebSocketOpcode.Pong, secondPong.Opcode);

        return (firstPong.Payload, secondPong.Payload);
    }

    private static async Task ServePingAndWaitForDisconnectAsync(TcpListener listener)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(BuildFrame(WebSocketOpcode.Ping, "fail"u8));
        await stream.FlushAsync();

        byte[] buffer = new byte[64];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (IOException)
        {
            // client shutdown may surface as EOF or a reset depending on the platform
        }
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServePingThenTextAndReadPongAsync(
        TcpListener listener,
        byte[] pingPayload,
        byte[] textPayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.WriteAsync(Concat(
            BuildFrame(WebSocketOpcode.Ping, pingPayload),
            BuildFrame(WebSocketOpcode.Text, textPayload)));
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServeAndReadClientFrameAsync(
        TcpListener listener)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.FlushAsync();

        return await ReadClientFrameAsync(stream);
    }

    private static async Task ServeClientCloseAsync(TcpListener listener, byte[] precedingPayload)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.FlushAsync();

        var close = await ReadClientFrameAsync(stream);
        Assert.Equal(WebSocketOpcode.Close, close.Opcode);

        // 수신 펌프가 이미 read 대기 중인 상태에서 data+close를 한 번에 보낸다.
        // CloseAsync가 직접 읽으면 같은 FrameReader/NetworkStream의 두 소비자가 경합한다.
        await stream.WriteAsync(Concat(
            BuildFrame(WebSocketOpcode.Text, precedingPayload),
            BuildFrame(WebSocketOpcode.Close, close.Payload)));
        await stream.FlushAsync();
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ServeHalfCloseThenReadClientCloseAsync(
        TcpListener listener,
        Task halfCloseRequested)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.FlushAsync();
        await halfCloseRequested;

        // 서버→클라이언트 방향만 EOF로 만들고, 반대 방향은 열어 둬 client close write는 성공시킨다.
        server.Client.Shutdown(SocketShutdown.Send);
        return await ReadClientFrameAsync(stream);
    }

    private static async Task ServeUntilClientDisconnectAsync(TcpListener listener)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = server.GetStream();

        string request = await ReadHttpRequestAsync(stream);
        string key = ReadHeader(request, "Sec-WebSocket-Key");
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n" +
            "\r\n");

        await stream.WriteAsync(response);
        await stream.FlushAsync();

        byte[] buffer = new byte[64];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (IOException)
        {
            // client shutdown may surface as EOF or a reset depending on the platform
        }
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
            {
                throw new IOException("Client closed before handshake request completed.");
            }

            read += n;
            if (ContainsHeaderTerminator(buffer.AsSpan(0, read)))
            {
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
        }

        throw new InvalidOperationException("Handshake request exceeded test buffer.");
    }

    private static bool ContainsHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (int i = 3; i < data.Length; i++)
        {
            if (data[i - 3] == (byte)'\r' &&
                data[i - 2] == (byte)'\n' &&
                data[i - 1] == (byte)'\r' &&
                data[i] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadHeader(string request, string headerName)
    {
        foreach (string line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator > 0 &&
                line.AsSpan(0, separator).Equals(headerName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        throw new InvalidOperationException($"Missing {headerName} header.");
    }

    private static string ComputeAccept(string secKey)
    {
        byte[] input = Encoding.ASCII.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        return Convert.ToBase64String(hash);
    }

    private static byte[] BuildFrame(
        WebSocketOpcode opcode,
        ReadOnlySpan<byte> payload,
        bool fin = true,
        bool rsv1 = false,
        bool masked = false,
        byte[]? maskKey = null)
    {
        if (masked)
        {
            maskKey ??= [1, 2, 3, 4];
            if (maskKey.Length != 4)
            {
                throw new ArgumentException("Mask key must be 4 bytes.", nameof(maskKey));
            }
        }

        int extendedLengthBytes = payload.Length <= 125 ? 0 : payload.Length <= ushort.MaxValue ? 2 : 8;
        int maskBytes = masked ? 4 : 0;
        byte[] frame = new byte[2 + extendedLengthBytes + maskBytes + payload.Length];
        frame[0] = (byte)((fin ? 0b1000_0000 : 0) | (rsv1 ? 0b0100_0000 : 0) | ((byte)opcode & 0x0F));
        int offset = 2;
        byte maskBit = masked ? (byte)0b1000_0000 : (byte)0;
        if (extendedLengthBytes == 0)
        {
            frame[1] = (byte)(maskBit | payload.Length);
        }
        else if (extendedLengthBytes == 2)
        {
            frame[1] = (byte)(maskBit | 126);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), checked((ushort)payload.Length));
            offset += 2;
        }
        else
        {
            frame[1] = (byte)(maskBit | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(offset, 8), (ulong)payload.Length);
            offset += 8;
        }

        if (masked)
        {
            maskKey!.CopyTo(frame, offset);
            offset += 4;
            for (int i = 0; i < payload.Length; i++)
            {
                frame[offset + i] = (byte)(payload[i] ^ maskKey[i & 3]);
            }
        }
        else
        {
            payload.CopyTo(frame.AsSpan(offset));
        }

        return frame;
    }

    private static byte[] RawDeflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] ReadPayload(FrameReader reader, FrameHeader header)
    {
        using var assembler = new MessageAssembler(initialCapacity: 16, maxMessageBytes: 1024);
        reader.ReadPayloadInto(header, assembler);
        return assembler.WrittenMemory.ToArray();
    }

    private static async Task<(WebSocketOpcode Opcode, byte[] Payload)> ReadClientFrameAsync(NetworkStream stream)
    {
        byte[] header = new byte[2];
        await ReadExactAsync(stream, header);

        var opcode = (WebSocketOpcode)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        Assert.True(masked);

        ulong length = (ulong)(header[1] & 0x7F);
        if (length == 126)
        {
            byte[] ext = new byte[2];
            await ReadExactAsync(stream, ext);
            length = (ulong)((ext[0] << 8) | ext[1]);
        }
        else if (length == 127)
        {
            byte[] ext = new byte[8];
            await ReadExactAsync(stream, ext);
            length = 0;
            foreach (byte b in ext)
            {
                length = (length << 8) | b;
            }
        }

        if (length > int.MaxValue)
        {
            throw new InvalidOperationException("Client frame is too large for this test.");
        }

        byte[] mask = new byte[4];
        await ReadExactAsync(stream, mask);

        byte[] payload = new byte[(int)length];
        if (payload.Length > 0)
        {
            await ReadExactAsync(stream, payload);
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i & 3];
            }
        }

        return (opcode, payload);
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..]);
            if (n == 0)
            {
                throw new IOException("Stream ended before the expected bytes were read.");
            }

            read += n;
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private static void SetField<T>(DuLowAllocWebSocketClient client, string name, T value)
    {
        FieldInfo field = typeof(DuLowAllocWebSocketClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        field.SetValue(client, value);
    }

    private static T GetField<T>(DuLowAllocWebSocketClient client, string name)
    {
        FieldInfo field = typeof(DuLowAllocWebSocketClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)field.GetValue(client)!;
    }

    private static T? GetNullableField<T>(DuLowAllocWebSocketClient client, string name)
        where T : class
    {
        FieldInfo field = typeof(DuLowAllocWebSocketClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T?)field.GetValue(client);
    }

    private static T? GetNullableObjectField<T>(object instance, string name)
        where T : class
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T?)field.GetValue(instance);
    }

    private sealed class TrackingByteArrayPool(bool blockFirstRent = false) : ArrayPool<byte>
    {
        private readonly object _gate = new();
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);
        private readonly ManualResetEventSlim _firstRentRelease = new(initialState: !blockFirstRent);
        private readonly TaskCompletionSource _firstRentEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rentCount;
        private int _returnCount;

        public int RentCount => Volatile.Read(ref _rentCount);
        public int ReturnCount => Volatile.Read(ref _returnCount);

        public int OutstandingCount
        {
            get
            {
                lock (_gate)
                {
                    return _outstanding.Count;
                }
            }
        }

        public Task FirstRentEntered => _firstRentEntered.Task;

        public void ReleaseFirstRent() => _firstRentRelease.Set();

        public override byte[] Rent(int minimumLength)
        {
            int rentCount = Interlocked.Increment(ref _rentCount);
            if (blockFirstRent && rentCount == 1)
            {
                _firstRentEntered.TrySetResult();
                if (!_firstRentRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The test did not release the first pool rent.");
                }
            }

            byte[] array = new byte[Math.Max(1, minimumLength)];
            lock (_gate)
            {
                if (!_outstanding.Add(array))
                {
                    throw new InvalidOperationException("The test pool returned the same lease twice.");
                }
            }

            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            lock (_gate)
            {
                if (!_outstanding.Remove(array))
                {
                    throw new InvalidOperationException("An unknown or already returned lease was returned.");
                }
            }

            if (clearArray)
            {
                Array.Clear(array);
            }

            Interlocked.Increment(ref _returnCount);
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected ping write failure.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Injected ping write failure.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected ping write failure."));
    }

    private sealed class BlockingThrowingWriteStream : Stream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeAttempts;

        public Task Entered => _entered.Task;
        public int WriteAttempts => Volatile.Read(ref _writeAttempts);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _release.TrySetResult();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeAttempts);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected partial frame write failure.");
        }
    }

    private sealed class ThrowingControlFrameThrottle : IWebSocketControlFrameThrottle
    {
        public ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Injected control-frame throttle failure."));
    }

    private sealed class CapturingControlFrameThrottle : IWebSocketControlFrameThrottle
    {
        private int _ranOnThreadPool;

        public bool RanOnThreadPool => Volatile.Read(ref _ranOnThreadPool) != 0;

        public ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken)
        {
            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                Volatile.Write(ref _ranOnThreadPool, 1);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposingControlFrameThrottle(DuLowAllocWebSocketClient client)
        : IWebSocketControlFrameThrottle
    {
        private readonly TaskCompletionSource _disposeReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposeReturned => _disposeReturned.Task;

        public ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken)
        {
            client.Dispose();
            _disposeReturned.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingControlFrameThrottle : IWebSocketControlFrameThrottle
    {
        private readonly TaskCompletionSource<WebSocketOpcode> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WebSocketOpcode> Entered => _entered.Task;

        public async ValueTask WaitAsync(WebSocketOpcode opcode, CancellationToken cancellationToken)
        {
            _entered.TrySetResult(opcode);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }
}
