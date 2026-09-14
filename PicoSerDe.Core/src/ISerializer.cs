namespace PicoSerDe.Core;

public interface ISerializer<in T>
{
    void Serialize(IBufferWriter<byte> writer, T value);
}

/// <summary>
/// Optional extension implemented by generated serializers that consume the
/// per-call options object (e.g. <c>Indented</c>). The compat
/// <see cref="SerializerFacade{TFormat}.Register{T}(ISerializer{T}, IDeserializer{T})"/>
/// path detects it and forwards options instead of dropping them.
/// </summary>
public interface IOptionsSerializer<in T>
{
    void Serialize(IBufferWriter<byte> writer, T value, SerOptions? options);
}
