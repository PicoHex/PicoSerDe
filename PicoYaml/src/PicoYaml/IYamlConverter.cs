namespace PicoYaml;

/// <summary>Custom converter for serializing and deserializing a YAML value.</summary>
/// <typeparam name="T">The type to convert.</typeparam>
public interface IYamlConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ref YamlReader reader);
}
