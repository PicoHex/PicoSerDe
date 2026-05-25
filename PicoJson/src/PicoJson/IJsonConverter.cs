using System.Buffers;

namespace PicoJson;

public interface IJsonConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ReadOnlySpan<byte> data);
}
