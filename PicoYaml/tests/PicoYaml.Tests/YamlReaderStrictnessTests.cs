namespace PicoYaml.Tests;

// ── YAML 1.2, three inputs the spec forbids and the reader used to take silently ─────────
//
//   §6.1 Indentation Spaces — indentation is spaces only (`s-indent(n) ::= s-space × n`); a
//        tab is a separation character, never an indentation step.
//   §7.3.1 Double-Quoted Scalars — a quoted scalar ends at its closing quote; a document
//        whose quote never closes is malformed, not a scalar that runs to end of file.
//   §5.7 Escaped Characters — the escape set is closed (`ns-esc-char`): `\q` is not in it,
//        and the same rule already makes the JSON path throw (README.md:370).
//
// Each half is paired with the legal neighbour it must not swallow: a tab INSIDE a quoted
// scalar or a block scalar is content, and the escapes the spec does define keep decoding.

/// <summary>Scalar member used by the serializer-level halves.</summary>
public class YamlStrictnessDoc
{
    public string? A { get; set; }
}

public class YamlReaderStrictnessTests
{
    /// <summary>Walks the whole document and lets the reader's own verdict out.</summary>
    private static void WalkAll(string yaml)
    {
        var reader = new YamlReader(Encoding.UTF8.GetBytes(yaml));
        while (reader.Read()) { }
    }

    /// <summary>
    /// One comparable line describing what the reader did: the <c>PropertyName(key)=value</c>
    /// pairs it produced, or the FormatException that ended the walk. "Silently accepted" and
    /// "refused" then read as two different answers to the same question.
    /// </summary>
    private static string Describe(string yaml)
    {
        var seen = new List<string>();
        try
        {
            var reader = new YamlReader(Encoding.UTF8.GetBytes(yaml));
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    seen.Add(
                        "PropertyName("
                            + Encoding.UTF8.GetString(reader.KeySpan)
                            + ")="
                            + Encoding.UTF8.GetString(reader.ValueSpan)
                    );
            }
        }
        catch (FormatException)
        {
            return seen.Count == 0
                ? "FormatException"
                : "FormatException after " + string.Join(", ", seen);
        }
        return string.Join(", ", seen);
    }

    // ── 1. a tab in the indentation ──

    [Test]
    public async Task TabIndentedNestedMapping_Throws()
    {
        await Assert
            .That(Describe("a:\n\tb: 1\n"))
            .IsEqualTo("FormatException after PropertyName(a)=")
            .Because(
                "a tab may not indent a line: b is a member of a, not a root key named '\\tb'"
            );
    }

    [Test]
    public async Task TabIndentedNestedMapping_ThrowsOnDeserialize()
    {
        await Assert
            .That(() =>
                YamlSerializer.Deserialize<YamlStrictnessDoc>(
                    Encoding.UTF8.GetBytes("A:\n\tb: 1\n")
                )
            )
            .Throws<FormatException>();
    }

    [Test]
    public async Task TabIndentedRootMember_Throws()
    {
        await Assert.That(Describe("\tA: 1\n")).IsEqualTo("FormatException");
    }

    [Test]
    public async Task TabIndentedUnderANestedMapping_Throws()
    {
        await Assert.That(() => WalkAll("a:\n  b:\n\tc: 1\n")).Throws<FormatException>();
    }

    // ── the legal neighbours: a tab is still content where the spec allows it ──

    [Test]
    public async Task TabInsideQuotedValue_IsContent()
    {
        await Assert.That(Describe("A: \"x\\ty\"\n")).IsEqualTo("PropertyName(A)=x\ty");
    }

    [Test]
    public async Task TabInsideBlockScalar_IsContent()
    {
        await Assert.That(Describe("A: |\n  x\ty\n")).IsEqualTo("PropertyName(A)=x\ty\n");
    }

    // ── 2. an unclosed quote ──

    [Test]
    public async Task UnclosedDoubleQuote_Throws()
    {
        await Assert
            .That(Describe("A: \"oops"))
            .IsEqualTo("FormatException")
            .Because("the closing quote is owed; the value is not 'oops to end of file'");
    }

    [Test]
    public async Task UnclosedDoubleQuote_ThrowsOnDeserialize()
    {
        await Assert
            .That(() =>
                YamlSerializer.Deserialize<YamlStrictnessDoc>(Encoding.UTF8.GetBytes("A: \"oops"))
            )
            .Throws<FormatException>();
    }

    [Test]
    public async Task UnclosedSingleQuote_Throws()
    {
        await Assert.That(Describe("A: 'oops")).IsEqualTo("FormatException");
    }

    [Test]
    public async Task ClosedQuotes_StillRead()
    {
        await Assert.That(Describe("A: \"ok\"\n")).IsEqualTo("PropertyName(A)=ok");
        await Assert.That(Describe("A: 'ok'\n")).IsEqualTo("PropertyName(A)=ok");
    }

    // ── 3. an escape the spec does not define ──

    [Test]
    public async Task InvalidEscape_Throws()
    {
        await Assert
            .That(Describe("A: \"x\\qy\"\n"))
            .IsEqualTo("FormatException")
            .Because("\\q is not in ns-esc-char (YAML 1.2 §5.7), and the JSON path refuses it too");
    }

    [Test]
    public async Task InvalidEscape_ThrowsOnDeserialize()
    {
        await Assert
            .That(() =>
                YamlSerializer.Deserialize<YamlStrictnessDoc>(
                    Encoding.UTF8.GetBytes("A: \"x\\qy\"\n")
                )
            )
            .Throws<FormatException>();
    }

    [Test]
    public async Task ValidEscapes_AreStillDecoded()
    {
        await Assert
            .That(Describe("A: \"t\\tn\\nr\\rq\\\"b\\\\\"\n"))
            .IsEqualTo("PropertyName(A)=t\tn\nr\rq\"b\\");
    }
}
