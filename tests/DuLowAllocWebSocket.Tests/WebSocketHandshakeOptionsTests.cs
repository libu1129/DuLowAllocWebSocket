using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class WebSocketHandshakeOptionsTests
{
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
