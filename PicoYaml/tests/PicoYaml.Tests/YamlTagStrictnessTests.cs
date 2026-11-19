namespace PicoYaml.Tests;

/// <summary>
/// Step 2 of the tag work: a tag this reader cannot honour must FAIL the read, not be
/// dropped. Dropping it is the one thing a tag exists to prevent — the document says the
/// value is something, and handing it back as if it were untagged is a silent lie about
/// the wire. PyYAML (this format's arbiter) fails on all of these too: ConstructorError
/// <c>could not determine a constructor for the tag '!foo'</c> for an application tag,
/// <c>'tag:yaml.org,2002:unknown'</c> for an unrecognised <c>!!</c> tag, and ParserError
/// <c>found undefined tag handle '!myapp!'</c> for a handle with no <c>%TAG</c> directive.
/// </summary>
public class YamlTagStrictnessTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static byte[] Doc(string yaml) => Utf8.GetBytes(yaml);

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

    private static bool ThrowsFormat(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── an application tag has no constructor here ───────────────────────────

    // PyYAML: 'A: !foo 1' -> ConstructorError: could not determine a constructor for the tag '!foo'
    [Test]
    public async Task ApplicationTag_OnAValue_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !foo 1\n"))
        );
        await Assert.That(message).Contains("FormatException");
        await Assert.That(message).Contains("!foo");
    }

    // ── an unknown `!!` name is not a core-schema type ───────────────────────

    // PyYAML: 'A: !!unknown 1' -> ConstructorError: ... for the tag 'tag:yaml.org,2002:unknown'
    [Test]
    public async Task UnresolvableStandardTag_OnAValue_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!unknown 1\n"))
        );
        await Assert.That(message).Contains("FormatException");
        await Assert.That(message).Contains("!!unknown");
    }

    // PyYAML: 'A: !myapp!Thing {x: 1}' -> ParserError: found undefined tag handle '!myapp!'
    // A handle needs a %TAG directive, which this reader has no mechanism to declare.
    [Test]
    public async Task ApplicationTagHandle_FailsNamingTheHandle()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Inner: !myapp!Thing {B: 1}\n"))
        );
        await Assert.That(message).Contains("FormatException");
        await Assert.That(message).Contains("!myapp!");
    }

    // ── every node position is covered, not just a block mapping's value ─────

    // PyYAML: '{A: !foo 1}' -> ConstructorError (the flow value is a node too).
    [Test]
    public async Task ApplicationTag_OnAFlowMappingValue_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Map: {A: !foo 1}\n"))
        );
        await Assert.That(message).Contains("!foo");
    }

    // PyYAML: '[!foo 1]' -> ConstructorError.
    [Test]
    public async Task ApplicationTag_OnAFlowSequenceItem_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Words: [!foo 1]\n"))
        );
        await Assert.That(message).Contains("!foo");
    }

    // PyYAML: 'Items:\n  - !foo 1' -> ConstructorError.
    [Test]
    public async Task ApplicationTag_OnASequenceItem_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagCollectionDoc>(Doc("Words:\n  - !foo 1\n"))
        );
        await Assert.That(message).Contains("!foo");
    }

    // A tag on a line of its own is still a tag: `!!unknown` there used to be a skipped
    // line, i.e. a document read as if the tag had never been written.
    [Test]
    public async Task UnresolvableStandardTag_OnATagOnlyLine_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("!!unknown\nText: 1\n"))
        );
        await Assert.That(message).Contains("!!unknown");
    }

    // PyYAML: 'A: !foo |\n  x' -> ConstructorError (the tag labels the block scalar).
    [Test]
    public async Task ApplicationTag_BeforeABlockScalar_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !foo |\n  x\n"))
        );
        await Assert.That(message).Contains("!foo");
    }

    // The strictness is a FormatException, the exception type every other malformed-YAML
    // path in this reader raises (not a bare Exception, not a parse-success).
    [Test]
    public async Task UnresolvableTag_IsAFormatException()
    {
        await Assert
            .That(
                ThrowsFormat(() => YamlSerializer.Deserialize<TagScalarDoc>(Doc("Text: !foo 1\n")))
            )
            .IsTrue();
    }

    // ── the two tags that are NOT an error, and why ─────────────────────────

    // `!` alone is the non-specific tag: "resolve this node by its own shape", which is
    // exactly what an untagged node does here — PyYAML: 'A: ! 1' -> {'A': 1}.
    [Test]
    public async Task NonSpecificTag_ResolvesByShape()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: ! 1\n"));
        await Assert.That(doc!.Number).IsEqualTo(1);
    }

    // A node-level `!name` tag is what OUR WRITER emits for a [YamlTag] type
    // (YamlWriter.WriteTag), and it is read through on purpose: the mapped type comes from
    // the target type, not from the tag, so the tag carries an identity this typed reader
    // does not need (a registration mechanism, if it ever exists, is what would use it).
    // This is the one documented read-through, and YamlTagTests.Serialize_TaggedType_Roundtrips
    // is the fixture that would break if it silently stopped working.
    [Test]
    public async Task NodeLevelLocalTag_IsReadThrough()
    {
        var doc = YamlSerializer.Deserialize<TagScalarDoc>(Doc("!person\nText: hi\n"));
        await Assert.That(doc!.Text).IsEqualTo("hi");
    }

    // ── the two documented divergences, pinned so the README cannot drift ───

    // PyYAML (a YAML 1.1 resolver) reads `!!int "0x1f"` as 31. This reader's integer grammar is
    // the 1.2 core schema's decimal form, so the tag fails — and it fails the SAME way the
    // untagged `Number: 0x1f` does, which is the point: a tag never widens the grammar.
    [Test]
    public async Task IntTag_OnAHexForm_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!int \"0x1f\"\n"))
        );
        await Assert.That(message).Contains("!!int");
    }

    // The untagged control for the test above would not fail: `Number: 0x1f` reads as 0 today,
    // because the untagged scalar read stops at the leading `0` (a pre-existing prefix soft spot,
    // not a contract this file wants to freeze). What the TAG guarantees instead is that the WHOLE
    // scalar is the type it names — "42x" is not an integer even though its prefix is one.
    [Test]
    public async Task IntTag_RequiresTheWholeScalarToBeAnInteger()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Number: !!int \"42x\"\n"))
        );
        await Assert.That(message).Contains("!!int");
    }

    // PyYAML's bool table is YAML 1.1's (yes/no/on/off); this reader's is the 1.2 core schema's
    // true/false, so `!!bool yes` fails here and `!!bool true` does not.
    [Test]
    public async Task BoolTag_OnAYaml11Word_FailsNamingTheTag()
    {
        var message = MessageOf(() =>
            YamlSerializer.Deserialize<TagScalarDoc>(Doc("Flag: !!bool yes\n"))
        );
        await Assert.That(message).Contains("!!bool");
    }

    // The same word with no tag at all fails too — the tag did not narrow anything.
    [Test]
    public async Task UntaggedYaml11Word_IsNotABooleanEither()
    {
        var message = MessageOf(() => YamlSerializer.Deserialize<TagScalarDoc>(Doc("Flag: yes\n")));
        await Assert.That(message).Contains("FormatException");
    }
}
