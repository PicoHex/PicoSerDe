namespace PicoSerDe.Core;

public interface ISerializer<T>
{
    void Serialize(IBufferWriter<byte> writer, T value);
}
