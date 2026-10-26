namespace PicoYaml.Tests;

/// <summary>
/// R2 Task 4: the writer's null literal. The reader has always understood
/// <c>null</c>/<c>~</c> (<see cref="YamlReader.IsNullLiteral"/>); before this the writer had
/// no way to produce one, so "write null, read back missing" was the only possible round
/// trip. <see cref="YamlWriter.WriteNull"/> closes that asymmetry in the three positions a
/// value can occupy.
/// <para>
/// The literal is <c>null</c>, per YAML 1.1's tag resolution (<c>tag:yaml.org,2002:null</c>
/// covers <c>~</c>, <c>null</c>, <c>Null</c>, <c>NULL</c> and the empty value) and per YAML
/// 1.2's core schema, which lists the same spellings. <c>null</c> is the one both versions
/// and every other implementation agree on, and the spec's D1 ruling names it.
/// </para>
/// <para>
/// The last test is the one that keeps the two apart for good: a STRING that happens to
/// spell <c>null</c> must still be quoted, so the writer cannot "helpfully" turn user text
/// into a null and the reader cannot confuse the two.
/// </para>
/// <para>
/// Every document is built in a non-async helper: <see cref="YamlWriter"/> is a ref struct,
/// so it must not live in a method that awaits.
/// </para>
/// </summary>
public class YamlWriterNullTests
{
    private static string MappingValueDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("a"u8);
        w.WriteNull();
        w.WritePropertyName("b"u8);
        w.WriteNumber(7);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string NestedMappingDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteStartMapping();
        w.WritePropertyName("n"u8);
        w.WriteNull();
        w.WritePropertyName("e"u8);
        w.WriteNumber(0);
        w.WriteEndMapping();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string SequenceDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteStartSequence();
        w.WriteSequenceItem(1);
        w.WriteNull();
        w.WriteSequenceItem(3);
        w.WriteEndSequence();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string NestedSequenceDocument()
    {
        // List<List<int?>>: the inner sequence lives under a dash block, so its null item
        // still needs the dash prefix and the enclosing depth must not be lost.
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteStartSequence();
        w.WriteStartSequenceBlock();
        w.WriteStartSequence();
        w.WriteNull();
        w.WriteSequenceItem(7);
        w.WriteEndSequence();
        w.WriteEndSequenceBlock();
        w.WriteEndSequence();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string SoleValueDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WriteNull();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static (string Text, long BytesWritten, int WrittenCount) CountedNull()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WriteNull();
        return (Encoding.UTF8.GetString(buf.WrittenSpan), w.BytesWritten, buf.WrittenCount);
    }

    private static string EmptyStringDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteString(""u8);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string NullLiteralDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteNull();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string StringThatLooksNullDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("V"u8);
        w.WriteString("null"u8);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Test]
    public async Task WriteNull_AsMappingValue()
    {
        await Assert
            .That(MappingValueDocument())
            .IsEqualTo("a: null\nb: 7\n")
            .Because(
                "a null mapping value must end its key's line; leaving the writer 'after a key'"
                    + " would glue the next property onto it (a:nullb: 7)"
            );
    }

    [Test]
    public async Task WriteNull_AsNestedMappingValue()
    {
        await Assert.That(NestedMappingDocument()).IsEqualTo("V:\n  n: null\n  e: 0\n");
    }

    [Test]
    public async Task WriteNull_AsSequenceItem()
    {
        await Assert
            .That(SequenceDocument())
            .IsEqualTo("V:\n- 1\n- null\n- 3\n")
            .Because("spec D1: - null, never a bare dash (that is a nested sequence item)");
    }

    [Test]
    public async Task WriteNull_AsNestedSequenceItem()
    {
        await Assert.That(NestedSequenceDocument()).IsEqualTo("V:\n-\n  - null\n  - 7\n");
    }

    [Test]
    public async Task WriteNull_AsSoleValue()
    {
        await Assert.That(SoleValueDocument()).IsEqualTo("null\n");
    }

    [Test]
    public async Task WriteNull_CountsBytes()
    {
        var (text, bytesWritten, writtenCount) = CountedNull();
        await Assert.That(text).IsEqualTo("null\n");
        await Assert.That(bytesWritten).IsEqualTo(5);
        await Assert.That(bytesWritten).IsEqualTo(writtenCount);
    }

    [Test]
    public async Task WriteNull_DoesNotCollapseIntoEmptyOrString()
    {
        // Three different values, three different documents — the whole point of the
        // capability: "" is the empty string, null is the null literal, "null" is text.
        var emptyString = EmptyStringDocument();
        var nullLiteral = NullLiteralDocument();
        var stringNull = StringThatLooksNullDocument();

        await Assert.That(emptyString).IsEqualTo("V: \"\"\n");
        await Assert.That(nullLiteral).IsEqualTo("V: null\n");
        await Assert.That(stringNull).IsEqualTo("V: \"null\"\n");
        await Assert
            .That(new[] { emptyString, nullLiteral, stringNull }.Distinct().Count())
            .IsEqualTo(3);
    }
}
