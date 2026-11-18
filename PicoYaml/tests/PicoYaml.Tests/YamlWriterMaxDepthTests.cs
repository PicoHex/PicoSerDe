namespace PicoYaml.Tests;

// ── The write-side maximum depth ────────────────────────────────────────
// YamlWriter.WriteStartMapping() refuses to open a mapping at
// `_depth >= _maxDepth` (YamlWriter.cs:386-387) and throws
// `FormatException("Maximum depth of {_maxDepth} exceeded")`. The MsgPack writer
// has the same kind of guard AND a test for it; YAML's had none, so deleting the
// three lines left all 285 YAML tests green. These are its fences: the limit
// itself is writable, one level past it is not, and the number in the message is
// the one the caller configured (not a constant the constructor ignores).
//
// Context worth knowing: the serializer does not thread a depth through -- the
// generated code constructs `new YamlWriter(w, indented: ...)`, so the default
// 256 is what a `YamlSerializer.Serialize` call runs with, and the guard is only
// reachable through its own value from a directly constructed writer.

public class YamlWriterMaxDepthTests
{
    /// <summary>
    /// Exactly at the limit: three nested mappings under <c>maxDepth: 3</c> are
    /// written, and the document is what a reader expects. This is also the
    /// "the limit is not 1" fence — a guard that refused the second level, or one
    /// that counted siblings as nesting, fails here.
    /// </summary>
    [Test]
    public async Task NestingExactlyAtConfiguredLimit_IsWritten()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new YamlWriter(buffer, maxDepth: 3);

        writer.WritePropertyName("a"u8);
        writer.WriteStartMapping(); // depth 1
        writer.WritePropertyName("b"u8);
        writer.WriteStartMapping(); // depth 2
        writer.WritePropertyName("c"u8);
        writer.WriteStartMapping(); // depth 3 — the limit itself is allowed
        writer.WritePropertyName("leaf"u8);
        writer.WriteNumber(7);
        writer.WriteEndMapping();
        writer.WriteEndMapping();
        writer.WriteEndMapping();

        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await Assert.That(text).IsEqualTo("a:\n  b:\n    c:\n      leaf: 7\n");
    }

    /// <summary>
    /// One level past the limit: the fourth <c>WriteStartMapping()</c> throws, and
    /// the message carries the configured limit (3), not a default or a constant.
    /// </summary>
    [Test]
    public async Task NestingOnePastConfiguredLimit_ThrowsFormatException()
    {
        var (type, message) = OpenOneTooMany(3);
        await Assert.That(type).IsEqualTo("FormatException");
        await Assert.That(message).IsEqualTo("Maximum depth of 3 exceeded");
    }

    /// <summary>
    /// The limit is the constructor's value, not a constant: the same document
    /// (N nested mappings plus one too many) reports the number it was built with.
    /// A guard that hardcoded 256 — or 1 — cannot pass both halves.
    /// </summary>
    [Test]
    public async Task ConfiguredLimit_IsTheNumberInTheMessage()
    {
        var (tightType, tightMessage) = OpenOneTooMany(1);
        await Assert.That(tightType).IsEqualTo("FormatException");
        await Assert.That(tightMessage).IsEqualTo("Maximum depth of 1 exceeded");

        var (looseType, looseMessage) = OpenOneTooMany(2);
        await Assert.That(looseType).IsEqualTo("FormatException");
        await Assert.That(looseMessage).IsEqualTo("Maximum depth of 2 exceeded");
    }

    /// <summary>
    /// Closing a mapping gives its level back: three separate three-deep towers all
    /// fit in <c>maxDepth: 3</c>. A guard that incremented without a matching
    /// decrement would fail on the second tower.
    /// </summary>
    [Test]
    public async Task ClosedTowers_DoNotConsumeTheBudget()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new YamlWriter(buffer, maxDepth: 3);
        var failure = "<no throw>";
        try
        {
            for (var tower = 0; tower < 3; tower++)
            {
                writer.WritePropertyName("a"u8);
                writer.WriteStartMapping();
                writer.WritePropertyName("b"u8);
                writer.WriteStartMapping();
                writer.WritePropertyName("c"u8);
                writer.WriteStartMapping();
                writer.WritePropertyName("leaf"u8);
                writer.WriteNumber(tower);
                writer.WriteEndMapping();
                writer.WriteEndMapping();
                writer.WriteEndMapping();
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        await Assert.That(failure).IsEqualTo("<no throw>");
        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await Assert.That(text).Contains("leaf: 0");
        await Assert.That(text).Contains("leaf: 1");
        await Assert.That(text).Contains("leaf: 2");
    }

    /// <summary>
    /// The default is 256, so ordinary nesting never meets the guard. Pinned because
    /// the serializer relies on that default — a writer constructed with the default
    /// must still write a four-deep document.
    /// </summary>
    [Test]
    public async Task DefaultWriter_WritesFourLevels()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new YamlWriter(buffer);

        writer.WritePropertyName("a"u8);
        writer.WriteStartMapping();
        writer.WritePropertyName("b"u8);
        writer.WriteStartMapping();
        writer.WritePropertyName("c"u8);
        writer.WriteStartMapping();
        writer.WritePropertyName("d"u8);
        writer.WriteStartMapping();
        writer.WritePropertyName("leaf"u8);
        writer.WriteNumber(7);
        writer.WriteEndMapping();
        writer.WriteEndMapping();
        writer.WriteEndMapping();
        writer.WriteEndMapping();

        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await Assert.That(text).IsEqualTo("a:\n  b:\n    c:\n      d:\n        leaf: 7\n");
    }

    /// <summary>
    /// Opens <paramref name="maxDepth"/> mappings (all allowed), then tries one more
    /// and reports what happened. Returns <c>"&lt;no throw&gt;"</c> when the extra
    /// level was accepted.
    /// </summary>
    private static (string Type, string Message) OpenOneTooMany(int maxDepth)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new YamlWriter(buffer, maxDepth: maxDepth);
        for (var level = 0; level < maxDepth; level++)
        {
            writer.WritePropertyName("a"u8);
            writer.WriteStartMapping();
        }
        try
        {
            writer.WritePropertyName("toofar"u8);
            writer.WriteStartMapping();
        }
        catch (Exception ex)
        {
            return (
                ex.GetType().Name,
                string.IsNullOrEmpty(ex.Message) ? "<no message>" : ex.Message
            );
        }
        return ("<no throw>", "<no throw>");
    }
}
