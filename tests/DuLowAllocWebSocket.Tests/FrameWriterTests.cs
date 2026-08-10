using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class FrameWriterTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    [InlineData(64 * 1024, 64 * 1024)]
    public void NormalizeScratchBufferSize_AlwaysLeavesPayloadProgress(
        int configuredSize,
        int expectedSize)
    {
        Assert.Equal(expectedSize, FrameWriter.NormalizeScratchBufferSize(configuredSize));
    }
}
