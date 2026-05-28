namespace PicoToml;

/// <summary>Custom converter for serializing and deserializing a TOML value.</summary>
/// <typeparam name="T">The type to convert.</typeparam>
public interface ITomlConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ref TomlReader reader);
}
