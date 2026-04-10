namespace DuLowAllocWebSocket.Benchmarks.Helpers;

/// <summary>
/// 데이터를 폐기하는 IPayloadSink. FrameReader 오버헤드 단독 측정용.
/// </summary>
public sealed class NullPayloadSink : IPayloadSink
{
    public void Append(ReadOnlySpan<byte> data) { }
}
