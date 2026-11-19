namespace PicoYaml.Tests;

// ── Step 1 fixtures: the standard `!!` tags, applied on the read side ──────────
//
// Every member here carries a non-default initializer on purpose: the generated
// deserializer only assigns what it reads, so "the member kept its initializer"
// is how a swallowed value shows up.

public class TagScalarDoc
{
    public string? Text { get; set; } = "unread";
    public int Number { get; set; }
    public double Ratio { get; set; }
    public bool Flag { get; set; }
}

public class TagInner
{
    public int B { get; set; }
}

public class TagCollectionDoc
{
    public List<int>? Items { get; set; }
    public List<string>? Words { get; set; }
    public TagInner? Inner { get; set; }
    public Dictionary<string, string>? Map { get; set; }
}

/// <summary>
/// YAML 1.2 makes a tag the format's type-extension point, and PyYAML is this repository's
/// arbiter for what a YAML document means (<c>AGENTS.md</c>, "What 'the format' MEANS").
/// These tests pin the reader to PyYAML 6.0.3, one document per case; each expected value
/// was produced by <c>yaml.safe_load</c> on the same input.
/// </summary>
public class YamlTagResolutionTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static byte[] Doc(string yaml) => Utf8.GetBytes(yaml);

    /// <summary>The single PropertyName token a tag test is about, read off the raw token stream.</summary>
    private static (string Value, bool IsNull, bool WasQuoted) ReadFirstValue(string yaml)
    {
        using var reader = new YamlReader(Doc(yaml));
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.PropertyName)
                return (
                    Encoding.UTF8.GetString(reader.ValueSpan),
                    reader.IsNullLiteral,
                    reader.ValueWasQuoted
                );
        }
        throw new InvalidOperationException("no PropertyName token: " + yaml);
    }

    private static string MessageOf(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
        return "<no exception>";
    }

    // ── !!str: the payload is text, whatever it looks like ────────────────────

    // PyYAML: 'A: !!str 1' -> {'A': '1'}
    [Test]
    public async Task StrTag_OnANumber_IsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!str 1\n"));
        await Assert.That(doc!.Text).IsEqualTo("1");
    }

    // PyYAML: 'A: !!str null' -> {'A': 'null'} — the word is text, not the null literal.
    // Unquoted `Text: null` reads back as null, so this is the case a shape-only reader
    // cannot get right.
    [Test]
    public async Task StrTag_OnTheNullWord_IsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!str null\n"));
        await Assert.That(doc!.Text).IsEqualTo("null");
    }

    // PyYAML: 'A: !!str ~' -> {'A': '~'}
    [Test]
    public async Task StrTag_OnATilde_IsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!str ~\n"));
        await Assert.That(doc!.Text).IsEqualTo("~");
    }

    // PyYAML: 'A: !!str' -> {'A': ''} — an empty payload tagged str is the empty
    // string, never null (PyYAML: 'A: !!null' -> None, the two differ only by tag).
    [Test]
    public async Task StrTag_WithNoPayload_IsTheEmptyString()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!str\n"));
        await Assert.That(doc!.Text).IsEqualTo(string.Empty);
    }

    // PyYAML: 'A: !!str 1' -> {'A': '1'} at the token level too: the value carries no
    // null-ness and reports itself as text.
    [Test]
    public async Task StrTag_MakesTheTokenTextAndNotANullLiteral()
    {
        var (value, isNull, _) = ReadFirstValue("Text: !!str null\n");
        await Assert.That(value).IsEqualTo("null");
        await Assert.That(isNull).IsFalse();
    }

    // ── !!int / !!float / !!bool: the payload must BE that type ───────────────

    // PyYAML: 'A: !!int "42"' -> {'A': 42}
    [Test]
    public async Task IntTag_OnAQuotedNumber_IsTheNumber()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!int \"42\"\n"));
        await Assert.That(doc!.Number).IsEqualTo(42);
    }

    // PyYAML: 'A: !!int "abc"' -> ValueError: invalid literal for int() with base 10
    [Test]
    public async Task IntTag_OnAWord_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!int \"abc\"\n"))
        );
        await Assert.That(message).Contains("!!int");
    }

    // PyYAML: 'A: !!float "1e3"' -> {'A': 1000.0}
    [Test]
    public async Task FloatTag_OnAQuotedExponent_IsTheNumber()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Ratio: !!float \"1e3\"\n"));
        await Assert.That(doc!.Ratio).IsEqualTo(1000.0);
    }

    // PyYAML: 'A: !!float "abc"' -> ValueError: could not convert string to float
    [Test]
    public async Task FloatTag_OnAWord_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Ratio: !!float \"abc\"\n"))
        );
        await Assert.That(message).Contains("!!float");
    }

    // PyYAML: 'A: !!bool "true"' -> {'A': True}
    [Test]
    public async Task BoolTag_OnAQuotedTrue_ReadsTrue()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Flag: !!bool \"true\"\n"));
        await Assert.That(doc!.Flag).IsTrue();
    }

    // PyYAML: 'A: !!bool "1"' -> KeyError (its bool table has no '1')
    [Test]
    public async Task BoolTag_OnANonBooleanWord_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Flag: !!bool \"maybe\"\n"))
        );
        await Assert.That(message).Contains("!!bool");
    }

    // ── !!null: the payload is null, whatever it says ─────────────────────────

    // PyYAML: 'A: !!null ""' -> {'A': None} — a QUOTED scalar is null only because of
    // the tag; without it `A: ""` is the empty string.
    [Test]
    public async Task NullTag_OnAnEmptyQuotedScalar_IsNull()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!null \"\"\n"));
        await Assert.That(doc!.Text).IsNull();
    }

    // PyYAML: 'A: !!null abc' -> {'A': None}
    [Test]
    public async Task NullTag_OnAWord_IsNull()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!null gone\n"));
        await Assert.That(doc!.Text).IsNull();
    }

    // PyYAML: 'A: !!null' -> {'A': None}
    [Test]
    public async Task NullTag_WithNoPayload_IsNull()
    {
        var (_, isNull, _) = ReadFirstValue("Text: !!null\n");
        await Assert.That(isNull).IsTrue();
    }

    // ── !!map / !!seq: the node must be that kind of collection ───────────────

    // PyYAML: 'A: !!map\n  b: 1' -> {'A': {'b': 1}}
    [Test]
    public async Task MapTag_OnABlockMapping_Reads()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !!map\n  B: 1\n"));
        await Assert.That(doc!.Inner!.B).IsEqualTo(1);
    }

    // PyYAML: 'A: !!seq\n  - 1' -> {'A': [1]}
    [Test]
    public async Task SeqTag_OnABlockSequence_Reads()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items: !!seq\n  - 1\n  - 2\n"));
        await Assert.That(doc!.Items).IsEquivalentTo(new List<int> { 1, 2 });
    }

    // PyYAML: 'A: !!map {b: 1}' -> {'A': {'b': 1}}
    [Test]
    public async Task MapTag_OnAFlowMapping_Reads()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !!map {B: 1}\n"));
        await Assert.That(doc!.Inner!.B).IsEqualTo(1);
    }

    // PyYAML: 'A: !!seq [1, 2]' -> {'A': [1, 2]}
    [Test]
    public async Task SeqTag_OnAFlowSequence_Reads()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items: !!seq [1, 2]\n"));
        await Assert.That(doc!.Items).IsEquivalentTo(new List<int> { 1, 2 });
    }

    // PyYAML: 'A: !!map [1]' -> ConstructorError: expected a mapping node, but found sequence
    [Test]
    public async Task MapTag_OnASequence_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items: !!map [1]\n"))
        );
        await Assert.That(message).Contains("!!map");
    }

    // PyYAML: 'A: !!seq {b: 1}' -> ConstructorError: expected a sequence node, but found mapping
    [Test]
    public async Task SeqTag_OnAFlowMapping_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !!seq {B: 1}\n"))
        );
        await Assert.That(message).Contains("!!seq");
    }

    // PyYAML: 'A: !!seq\n  b: 1' -> ConstructorError: expected a sequence node, but found mapping
    // (the block form of the same mismatch — the kind is only knowable from the next line).
    [Test]
    public async Task SeqTag_OnABlockMapping_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !!seq\n  B: 1\n"))
        );
        await Assert.That(message).Contains("!!seq");
    }

    // PyYAML: 'A: !!str []' -> ConstructorError: expected a scalar node, but found sequence
    [Test]
    public async Task StrTag_OnAFlowSequence_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items: !!str []\n"))
        );
        await Assert.That(message).Contains("!!str");
    }

    // ── sequence items carry tags too ────────────────────────────────────────

    // PyYAML: 'Items:\n  - !!str 1\n  - !!int 2' -> {'Items': ['1', 2]}
    [Test]
    public async Task StrTagOnASequenceItem_KeepsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Words:\n  - !!str null\n"));
        await Assert.That(doc!.Words).IsEquivalentTo(new List<string> { "null" });
    }

    // PyYAML: 'Items:\n  - !!int "7"' -> {'Items': [7]}
    [Test]
    public async Task IntTagOnASequenceItem_IsTheNumber()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items:\n  - !!int \"7\"\n"));
        await Assert.That(doc!.Items).IsEquivalentTo(new List<int> { 7 });
    }

    // PyYAML: '[!!str 1]' -> ['1'] — a flow-sequence element is a node like any other.
    [Test]
    public async Task StrTagOnAFlowSequenceItem_KeepsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Words: [!!str null]\n"));
        await Assert.That(doc!.Words).IsEquivalentTo(new List<string> { "null" });
    }

    // PyYAML: '{A: !!str null}' -> {'A': 'null'}
    [Test]
    public async Task StrTagOnAFlowMappingValue_KeepsTheText()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Map: {A: !!str null}\n"));
        await Assert.That(doc!.Map!["A"]).IsEqualTo("null");
    }

    // PyYAML: "A: !!int |-\n  42" -> {'A': 42} — a block scalar is a scalar node, so a tag
    // in front of it labels the block (and used to be left behind as a plain "|").
    [Test]
    public async Task IntTag_OnABlockScalar_IsTheNumber()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!int |-\n  42\n"));
        await Assert.That(doc!.Number).IsEqualTo(42);
    }

    // PyYAML: 'A: !!str # comment' -> {'A': ''}
    [Test]
    public async Task StrTag_WithATrailingComment_IsTheEmptyString()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !!str # note\n"));
        await Assert.That(doc!.Text).IsEqualTo(string.Empty);
    }

    // PyYAML: 'A: !!map' -> ConstructorError: expected a mapping node, but found scalar
    [Test]
    public async Task MapTag_WithNoPayload_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !!map\n"))
        );
        await Assert.That(message).Contains("!!map");
    }

    // The same tag on a line of its own inside a block is a node property of the collection
    // below it (PyYAML: "A: !!seq\n  - 1" -> {'A': [1]}).
    [Test]
    public async Task SeqTag_OnAStandaloneLine_LabelsTheNodeBelow()
    {
        var doc = YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Items:\n  !!seq\n  - 1\n"));
        await Assert.That(doc!.Items).IsEquivalentTo(new List<int> { 1 });
    }

    // ── the neighbouring features must not regress ───────────────────────────

    // PyYAML: 'A: &a 1\nB: *a' -> {'A': 1, 'B': 1} — anchors/aliases are untouched by
    // the tag work; the tag path and the anchor path share one property loop now.
    [Test]
    public async Task Anchor_And_Alias_StillWork()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: &a 1\nOther: *a\n"));
        await Assert.That(doc!.Number).IsEqualTo(1);
    }

    // PyYAML: 'A: &a !!str null' -> {'A': 'null'} — properties in the grammar's order:
    // the anchor first, the tag second.
    [Test]
    public async Task AnchorThenTag_KeepsTheTagMeaning()
    {
        var (value, isNull, _) = ReadFirstValue("Text: &a !!str null\n");
        await Assert.That(value).IsEqualTo("null");
        await Assert.That(isNull).IsFalse();
    }

    // PyYAML: same document with the properties the other way round.
    [Test]
    public async Task TagThenAnchor_KeepsTheTagMeaning()
    {
        var (value, isNull, _) = ReadFirstValue("Text: !!str &a null\n");
        await Assert.That(value).IsEqualTo("null");
        await Assert.That(isNull).IsFalse();
    }

    // PyYAML 6, multi-document: each document resolves its own tags.
    [Test]
    public async Task MultiDocument_TagsApplyPerDocument()
    {
        var yaml = Doc("---\nText: !!str null\n---\nText: !!null gone\n");
        var seen = new List<(string Value, bool IsNull)>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    seen.Add((Encoding.UTF8.GetString(reader.ValueSpan), reader.IsNullLiteral));
            }
        }
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Value).IsEqualTo("null");
        await Assert.That(seen[0].IsNull).IsFalse();
        await Assert.That(seen[1].IsNull).IsTrue();
    }

    // The streaming (ReadOnlySequence) half of the reader has its own copy of the value
    // scan, so the tags have to mean the same thing there.
    [Test]
    public async Task SequenceMode_ResolvesTheSameTags()
    {
        var data = Doc("Text: !!str null\n");
        var seq = new ReadOnlySequence<byte>(data);
        var values = new List<(string Value, bool IsNull)>();
        using (var reader = new YamlReader(seq))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    values.Add((Encoding.UTF8.GetString(reader.ValueSpan), reader.IsNullLiteral));
            }
        }
        await Assert.That(values.Count).IsEqualTo(1);
        await Assert.That(values[0].Value).IsEqualTo("null");
        await Assert.That(values[0].IsNull).IsFalse();
    }
}
