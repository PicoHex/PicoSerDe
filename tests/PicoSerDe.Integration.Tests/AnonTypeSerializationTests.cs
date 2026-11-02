namespace PicoSerDe.Integration.Tests;

public class AnonTypeSerializationTests
{
    // The nested shape, whole. The three `Contains` assertions this replaces were all
    // satisfied by `{"Outer":"root","Inner":"X":1,"Y":2}` — a document whose inner object
    // was never opened — which is exactly how a missing WriteStartObject/WriteEndObject
    // pair survived a green suite. Byte equality is the only assertion that sees a brace.
    [Test]
    public async Task Json_NestedAnonType_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Outer = "root", Inner = new { X = 1, Y = 2 } }
        );
        await Assert.That(json).IsEqualTo("""{"Outer":"root","Inner":{"X":1,"Y":2}}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        // Anonymous types are serialization-only (no setters), so the round trip is the
        // DOM: a value that merely appears somewhere in the document did not round-trip —
        // it has to come back under the path the shape declares.
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement["Outer"].GetString()).IsEqualTo("root");
        await Assert.That(doc.RootElement["Inner"]["X"].GetInt32()).IsEqualTo(1);
        await Assert.That(doc.RootElement["Inner"]["Y"].GetInt32()).IsEqualTo(2);
    }

    // Two levels: each nested helper needs its OWN pair, so a fix that only wraps the
    // first nested object still leaves `B` bare.
    [Test]
    public async Task Json_TwoLevelNestedAnonType_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(new { A = new { B = new { C = 1 } } });
        await Assert.That(json).IsEqualTo("""{"A":{"B":{"C":1}}}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement["A"]["B"]["C"].GetInt32()).IsEqualTo(1);
    }

    // A nested object carrying a collection: the pair has to wrap members that open and
    // close containers themselves, and `Tail` after the array is where a missing `}` or a
    // stray comma shows up.
    [Test]
    public async Task Json_NestedAnonType_WithCollectionMember_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Name = "n", Outer = new { Items = new[] { 1, 2, 3 }, Tail = "t" } }
        );
        await Assert.That(json).IsEqualTo("""{"Name":"n","Outer":{"Items":[1,2,3],"Tail":"t"}}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement["Outer"]["Items"].GetArrayLength()).IsEqualTo(3);
        await Assert.That(doc.RootElement["Outer"]["Tail"].GetString()).IsEqualTo("t");
    }

    // A null member INSIDE the nested object: the inner pair must still be closed after a
    // null value, and the member that follows has to stay inside it.
    [Test]
    public async Task Json_NestedAnonType_WithNullMember_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Outer = new { S = (string?)null, N = 1 } }
        );
        await Assert.That(json).IsEqualTo("""{"Outer":{"S":null,"N":1}}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement["Outer"]["S"].GetStringOrNull()).IsNull();
        await Assert.That(doc.RootElement["Outer"]["N"].GetInt32()).IsEqualTo(1);
    }

    // The nested object itself is null: the null branch owns the key and no braces may be
    // written at all — `"Inner":{}` would be a different document (a present empty object).
    [Test]
    public async Task Json_NestedAnonType_Null_SerializesAsNull()
    {
        var present = false;
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Outer = "root", Inner = present ? new { X = 1 } : null }
        );
        await Assert.That(json).IsEqualTo("""{"Outer":"root","Inner":null}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement["Inner"].ValueKind).IsEqualTo(PicoValueKind.Null);
    }

    [Test]
    public async Task Json_AnonTypeWithArray_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Name = "test", Items = new[] { 1, 2, 3 } }
        );
        await Assert.That(json).Contains("\"Items\":[1,2,3]");
    }

    [Test]
    public async Task Json_DefaultIgnoreCondition_WhenWritingNull()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Name = (string?)null, Value = 42 },
            new PicoJetson.JsonOptions
            {
                DefaultIgnoreCondition = PicoIgnoreCondition.WhenWritingNull,
            }
        );
        await Assert.That(json).DoesNotContain("Name");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task Json_MaxDepth_Exceeded_Throws()
    {
        var innerMost = new { X = 1 };
        var inner = new { A = innerMost };
        var outer = new { B = inner };
        Assert.Throws<FormatException>(() =>
            PicoJetson.JsonSerializer.Serialize(outer, new PicoJetson.JsonOptions { MaxDepth = 2 })
        );
    }

    // MessagePack opens with a COUNTED map header (WriteStartObject(int)), so the nested
    // helper needs the pair as much as JSON does: without it the payload is a bare list of
    // string/int values with no map around it, which no reader can decode.
    [Test]
    public async Task MsgPack_NestedAnonType_Serializes()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new { Outer = "root", Inner = new { X = 1 } }
        );
        await Assert.That(bytes.Length).IsGreaterThan(0);
        await Assert
            .That(MessagePack.MessagePackSerializer.ConvertToJson(bytes))
            .IsEqualTo("""{"Outer":"root","Inner":{"X":1}}""");
    }

    // YAML nests by indentation, so a missing WriteStartMapping/WriteEndMapping pair is not a
    // brace but a swallowed key: the inner name landed on the outer key's own line
    // (`  Inner:  X: 1`) and the value could not be read back at any path.
    [Test]
    public async Task Yaml_NestedAnonType_IndentsTheInnerMapping()
    {
        var yaml = YamlSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(yaml).IsEqualTo("  Outer: root\n  Inner:\n    X: 1\n");
    }

    [Test]
    public async Task Ini_NestedAnonType_Serializes()
    {
        var ini = IniSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(ini).Contains("Outer");
        await Assert.That(ini).Contains("X");
    }

    [Test]
    public async Task Toml_NestedAnonType_Serializes()
    {
        var toml = TomlSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(toml).Contains("Outer");
        await Assert.That(toml).Contains("X");
    }

    [Test]
    public async Task Yaml_NestedAnonType_Serializes()
    {
        var yaml = YamlSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(yaml).Contains("Outer");
        await Assert.That(yaml).Contains("X");
    }

    [Test]
    public async Task Json_AnonType_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(json).Contains("\"Name\":\"test\"");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task MsgPack_AnonType_Serializes()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new { Name = "test", Value = 42 });
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Ini_AnonType_Serializes()
    {
        var ini = IniSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(ini).Contains("Name");
        await Assert.That(ini).Contains("test");
    }

    [Test]
    public async Task Toml_AnonType_Serializes()
    {
        var toml = TomlSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(toml).Contains("Name");
        await Assert.That(toml).Contains("test");
    }

    [Test]
    public async Task Yaml_AnonType_Serializes()
    {
        var yaml = YamlSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(yaml).Contains("Name");
        await Assert.That(yaml).Contains("test");
    }

    // Review P1-2: every integer kind must serialize from an anonymous type —
    // a single missing kind used to crash the whole source generator
    // (CS8785 KeyNotFoundException), silently removing ALL serializers.
    [Test]
    public async Task Json_AnonType_IntegerFamily_Serializes()
    {
        await Assert.That(PicoJetson.JsonSerializer.Serialize(new { V = 1u })).Contains("\"V\":1");
        await Assert.That(PicoJetson.JsonSerializer.Serialize(new { V = 2ul })).Contains("\"V\":2");
        await Assert
            .That(PicoJetson.JsonSerializer.Serialize(new { V = (short)-3 }))
            .Contains("\"V\":-3");
        await Assert
            .That(PicoJetson.JsonSerializer.Serialize(new { V = (ushort)4 }))
            .Contains("\"V\":4");
        await Assert
            .That(PicoJetson.JsonSerializer.Serialize(new { V = (byte)5 }))
            .Contains("\"V\":5");
        await Assert
            .That(PicoJetson.JsonSerializer.Serialize(new { V = (sbyte)-6 }))
            .Contains("\"V\":-6");
        await Assert
            .That(PicoJetson.JsonSerializer.Serialize(new { V = 'x' }))
            .Contains("\"V\":\"x\"");
    }

    [Test]
    public async Task Ini_AnonType_WithUInt_Serializes()
    {
        var ini = IniSerializer.Serialize(new { V = 1u });
        await Assert.That(ini).Contains("V");
        await Assert.That(ini).Contains("1");
    }

    [Test]
    public async Task Toml_AnonType_WithUInt_Serializes()
    {
        var toml = TomlSerializer.Serialize(new { V = 1u });
        await Assert.That(toml).Contains("V");
        await Assert.That(toml).Contains("1");
    }

    [Test]
    public async Task Yaml_AnonType_WithUInt_Serializes()
    {
        var yaml = YamlSerializer.Serialize(new { V = 1u });
        await Assert.That(yaml).Contains("V");
        await Assert.That(yaml).Contains("1");
    }

    [Test]
    public async Task MsgPack_AnonType_WithUInt_Serializes()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new { V = 1u });
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }
}
