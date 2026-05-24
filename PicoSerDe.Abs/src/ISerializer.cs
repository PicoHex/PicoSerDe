using System.Buffers;

namespace PicoSerDe.Abs;

public interface ISerializer<T>
{
    void Serialize(IBufferWriter<byte> writer, T value);
}
