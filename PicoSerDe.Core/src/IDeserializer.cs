namespace PicoSerDe.Core;

public interface IDeserializer<T>
{
    T Deserialize(ReadOnlySpan<byte> data);
}
