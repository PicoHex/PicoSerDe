namespace PicoIni;

/// <summary>Custom converter contract for INI serialization.</summary>
/// <typeparam name="T">The type to convert.</typeparam>
public interface IIniConverter<T>
{
    /// <summary>Writes the value to the buffer writer in INI-compatible format.</summary>
    void Write(IBufferWriter<byte> writer, T value);

    /// <summary>Reads and converts the INI value back to the target type.</summary>
    T Read(ReadOnlySpan<byte> value);
}
