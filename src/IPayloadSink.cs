namespace DuLowAllocWebSocket;

/// <summary>
/// 프레임 페이로드 청크를 소비하는 싱크 인터페이스입니다.
/// <see cref="MessageAssembler"/>와 <see cref="DeflateInflater"/>가 구현하여,
/// <see cref="FrameReader"/>로부터 중간 복사 없이 직접 데이터를 수신합니다.
/// </summary>
public interface IPayloadSink
{
    /// <summary>데이터 청크를 싱크에 추가합니다.</summary>
    void Append(ReadOnlySpan<byte> data);
}
