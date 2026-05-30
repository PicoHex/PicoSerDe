namespace PicoSerDe.Core;

public interface ISerializer<in T>
{
    void Serialize(IBufferWriter<byte> writer, T value);
}
