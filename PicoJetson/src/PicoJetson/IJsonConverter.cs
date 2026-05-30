namespace PicoJetson;

public interface IJsonConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ref JsonReader reader);
}
