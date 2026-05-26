namespace PicoIni;

public interface IIniConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ReadOnlySpan<byte> value);
}
