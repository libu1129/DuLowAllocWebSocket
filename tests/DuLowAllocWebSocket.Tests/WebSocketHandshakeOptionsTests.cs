using Xunit;
using System.Net;
using System.Net.Sockets;

namespace DuLowAllocWebSocket.Tests;

public sealed class WebSocketHandshakeOptionsTests
{
    [Fact]
    public void ConnectTimeout_DefaultsToThirtySeconds()
    {
        var options = new WebSocketClientOptions();

        Assert.Equal(30_000, WebSocketHandshake.NormalizeConnectTimeoutMilliseconds(options.ConnectTimeout));
    }

    [Fact]
    public void ConnectTimeout_NullUsesCallerCancellationOnly()
    {
        Assert.Equal(0, WebSocketHandshake.NormalizeConnectTimeoutMilliseconds(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConnectTimeout_NonPositiveValueIsRejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebSocketHandshake.NormalizeConnectTimeoutMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task ConnectTimeout_WhenUpgradeResponseStalls_ThrowsTimeoutException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var releaseServer = new CancellationTokenSource();

        Task server = RunStallingServerAsync(listener, releaseServer.Token);

        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
        };

        try
        {
            var handshake = new WebSocketHandshake();
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await handshake.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/feed"),
                    options,
                    CancellationToken.None));
        }
        finally
        {
            releaseServer.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenCallerCancelsStalledUpgrade_ThrowsOperationCanceledException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var releaseServer = new CancellationTokenSource();
        Task server = RunStallingServerAsync(listener, releaseServer.Token);

        var options = new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            ConnectTimeout = null,
        };
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            var handshake = new WebSocketHandshake();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await handshake.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/feed"),
                    options,
                    callerCancellation.Token));
        }
        finally
        {
            releaseServer.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task RunStallingServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellationToken);
            byte[] request = new byte[1024];
            _ = await accepted.GetStream().ReadAsync(request, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [Fact]
    public void SocketSendTimeout_DefaultsToThirtySeconds()
    {
        var options = new WebSocketClientOptions();

        Assert.Equal(30_000, WebSocketHandshake.NormalizeSocketSendTimeoutMilliseconds(options.SocketSendTimeout));
    }

    [Fact]
    public void SocketSendTimeout_NullKeepsOperatingSystemInfiniteDefault()
    {
        Assert.Equal(0, WebSocketHandshake.NormalizeSocketSendTimeoutMilliseconds(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SocketSendTimeout_NonPositiveValueIsRejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebSocketHandshake.NormalizeSocketSendTimeoutMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void SocketSendTimeout_SubMillisecondValueRoundsUpToOneMillisecond()
    {
        Assert.Equal(1, WebSocketHandshake.NormalizeSocketSendTimeoutMilliseconds(TimeSpan.FromTicks(1)));
    }
}
