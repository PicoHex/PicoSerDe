namespace PicoSerDe.Abs;

public interface IDeserializer<T>
{
    T Deserialize(ReadOnlySpan<byte> data);
}
