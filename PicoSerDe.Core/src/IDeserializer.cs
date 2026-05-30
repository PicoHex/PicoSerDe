namespace PicoSerDe.Core;

public interface IDeserializer<out T>
{
    T Deserialize(ReadOnlySpan<byte> data);
}
