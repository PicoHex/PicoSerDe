namespace PicoMsgPack;

public interface IMsgPackConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ref MsgPackReader reader);
}
