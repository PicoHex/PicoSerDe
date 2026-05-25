using System.Text;

namespace PicoJson.Tests;

public class JsonWriterTests
{
    private static string GetWrittenString(JsonWriter writer, ArrayBufferWriter<byte> buf)
    {
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Test]
    public async Task WriteNull_ProducesNull()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNull();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("null");
    }

    [Test]
    public async Task WriteBoolean_True()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteBoolean(true);
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("true");
    }

    [Test]
    public async Task WriteBoolean_False()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteBoolean(false);
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("false");
    }

    [Test]
    public async Task WriteNumber_Int32()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(42);
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("42");
    }

    [Test]
    public async Task WriteNumber_Int64()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(1234567890123L);
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("1234567890123");
    }

    [Test]
    public async Task WriteNumber_Double()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(3.14);
        await Assert.That(GetWrittenString(w, b)).Contains("3.14");
    }

    [Test]
    public async Task WriteString_ProducesQuotedString()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteString("hello"u8);
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("\"hello\"");
    }

    [Test]
    public async Task WriteObject_Empty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("{}");
    }

    [Test]
    public async Task WriteObject_WithProperty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WritePropertyName("name"u8);
        w.WriteString("alice"u8);
        w.WriteEndObject();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("{\"name\":\"alice\"}");
    }

    [Test]
    public async Task WriteArray_Empty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartArray();
        w.WriteEndArray();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("[]");
    }

    [Test]
    public async Task WriteArray_WithInts()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartArray();
        w.WriteNumber(1);
        w.WriteNumber(2);
        w.WriteNumber(3);
        w.WriteEndArray();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("[1,2,3]");
    }

    [Test]
    public async Task WriteNested_ObjectInObject()
    {
        var b = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WritePropertyName("inner"u8);
        w.WriteStartObject();
        w.WritePropertyName("x"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("{\"inner\":{\"x\":1}}");
    }

    [Test]
    public async Task BytesWritten_Tracks()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(42);
        await Assert.That(w.BytesWritten).IsEqualTo(2);
    }

    [Test]
    public async Task Indented_ProducesMultilineOutput()
    {
        var b = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(b, indented: true);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        await Assert.That(GetWrittenString(w, b)).IsEqualTo("{\n  \"a\": 1\n}");
    }

    // === P0-1: String Escaping Tests ===

    [Test]
    public async Task WriteString_Escapes_Quote()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("he\"llo"u8); // contains embedded quote
        var json = GetWrittenString(w, buf);
        await Assert.That(json).IsEqualTo("\"he\\\"llo\"");
    }

    [Test]
    public async Task WriteString_Escapes_Backslash()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("a\\b"u8);
        var json = GetWrittenString(w, buf);
        await Assert.That(json).IsEqualTo("\"a\\\\b\"");
    }

    [Test]
    public async Task WriteString_Escapes_Newline()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("line1\nline2"u8);
        var json = GetWrittenString(w, buf);
        await Assert.That(json).IsEqualTo("\"line1\\nline2\"");
    }

    // === P0-3: NaN/Infinity Tests ===

    [Test]
    public async Task WriteNumber_NaN_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        try
        {
            w.WriteNumber(double.NaN);
            await Assert.That(true).IsFalse(); // should not reach
        }
        catch (ArgumentException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task WriteNumber_Infinity_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        try
        {
            w.WriteNumber(double.PositiveInfinity);
            await Assert.That(true).IsFalse(); // should not reach
        }
        catch (ArgumentException)
        {
            await Assert.That(true).IsTrue();
        }
    }
}
