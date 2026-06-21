namespace PicoIni.Tests;

public class IniReaderBufferTests
{
    private static string ReadBackValue(ReadOnlySpan<byte> data)
    {
        using var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.ReadValue(); // String
        return Encoding.UTF8.GetString(reader.GetStringRaw());
    }

    [Test]
    public async Task LongQuotedValue_Over256Bytes_DoesNotOverflow()
    {
        // The reader rents a 256-byte buffer for quoted values.
        // Values longer than 256 bytes cause IndexOutOfRangeException.
        // The value must contain a character that triggers NeedsQuoting (e.g. space)
        var longValue = "prefix " + new string('A', 300) + " suffix";
        var buf = new ArrayBufferWriter<byte>(512);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Key"u8, Encoding.UTF8.GetBytes(longValue));

        var result = ReadBackValue(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo(longValue);
    }

    [Test]
    public async Task LongQuotedValue_WithEscapes_Over256Bytes_DoesNotOverflow()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("start ");
        for (int i = 0; i < 60; i++)
            sb.Append("AB\\nCD\\t");
        var longValue = sb.ToString();

        var buf = new ArrayBufferWriter<byte>(512);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Key"u8, Encoding.UTF8.GetBytes(longValue));

        var result = ReadBackValue(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo(longValue);
    }
}
