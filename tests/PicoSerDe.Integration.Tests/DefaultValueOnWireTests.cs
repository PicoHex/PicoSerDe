// The "a non-nullable value type at its default is WRITTEN" contract, at WIRE level.
//
// | DefaultIgnoreCondition | int / bool / Guid / double / enum at default |
// |------------------------|---------------------------------------------|
// | Never (the default)    | written, with the type's default literal     |
// | WhenWritingDefault     | omitted                                      |
//
// ── Why this file exists ────────────────────────────────────────────────────────────────────────
// `JsonSerializerGenerator.EmitIgnoreConditionOpen` opens a guard for every non-nullable value-type
// member so that WhenWritingDefault can omit it. The guard's `Never` side is the one that says
// "still write it", and NOTHING pinned it. Measured on this tree with a one-character mutation
// (`||` → `&&` in that branch — the member vanishes exactly when it holds its default): 7 red of
// 4011, three of them in this file and four incidental, so without this file ~3985 tests stay
// green while every default-valued member disappears from every JSON document. Making the branch
// omit unconditionally is even broader (139 red) and equally silent about WHAT went missing. Every
// `*_RoundTrips_*` test is immune to the silent drop, and for a structural reason, not by accident:
// if `Count = 0` never reaches the document, the reader leaves `Count` at `default`, which is
// exactly the value that was dropped — the round trip agrees with the defect.
//
// So the assertion has to be on the artifact: the bytes of the document, not the object that came
// back out of it. The rule itself is the option matrix above, and it is the same rule STJ applies
// (DefaultIgnoreCondition.Never writes the member whatever it holds), so it is not a local
// preference that a generator change may renegotiate.
//
// Covered emit paths: the top-level serializer body and the nested-object inner helper — the two
// object paths that carry the guard (`JsonSerializerGenerator.cs`, three `EmitIgnoreConditionOpen`
// call sites, the third being the polymorphic case arm, which shares the same helper).

using System.Text;
using PicoIni;
using PicoJetson;
using PicoMsgPack;
using PicoSerDe.Core;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

// ── Models: every kind whose default the guard must still write ──

public enum DefaultValueShade
{
    Light,
    Dark,
}

[PicoSerializable]
public class DefaultValueWireModel
{
    public int Count { get; set; }
    public bool Flag { get; set; }
    public Guid Id { get; set; }
    public double Ratio { get; set; }
    public DefaultValueShade Shade { get; set; }

    /// <summary>Control: a document is never empty, so "missing" means "dropped", not "no members".</summary>
    public string Name { get; set; } = "";
}

[PicoSerializable]
public class DefaultValueWireOuter
{
    public string Name { get; set; } = "";
    public DefaultValueWireModel Inner { get; set; } = new();
}

/// <summary>
/// A member holding <c>default</c> in a non-nullable value type must still be ON the wire under
/// the default options; only <see cref="PicoIgnoreCondition.WhenWritingDefault"/> may omit it.
/// </summary>
[NotInParallel("DefaultValueOnWire")]
public class DefaultValueOnWireTests
{
    private static readonly DefaultValueWireModel AllDefault = new();

    private static readonly JsonOptions WhenWritingDefault = new()
    {
        DefaultIgnoreCondition = PicoIgnoreCondition.WhenWritingDefault,
    };

    // ── JSON: the exact document, member by member ──

    /// <summary>
    /// The document a model of nothing-but-defaults produces under the DEFAULT options. Pinned as
    /// text on purpose: a dropped member is one absent key, which no round trip can see.
    /// </summary>
    [Test]
    public async Task Json_DefaultOption_WritesEveryDefaultValueMember()
    {
        var json = JsonSerializer.Serialize(AllDefault);

        await Assert
            .That(json)
            .IsEqualTo(
                "{\"Count\":0,\"Flag\":false,\"Id\":\"00000000-0000-0000-0000-000000000000\","
                    + "\"Ratio\":0,\"Shade\":0,\"Name\":\"\"}"
            )
            .Because(
                "DefaultIgnoreCondition.Never (the default) writes a non-nullable value type "
                    + "member whatever it holds — 'the value is default' is not a reason to omit "
                    + "it. Omission here is silent: the reader leaves the member at the very "
                    + "value that was dropped, so every round-trip test still passes."
            );
    }

    /// <summary>
    /// Each kind asserted on its own so a failure names the kind that disappeared instead of
    /// printing one long document. Same wire, finer blame.
    /// </summary>
    [Test]
    public async Task Json_DefaultOption_WritesEachDefaultKindWithItsDefaultLiteral()
    {
        var json = JsonSerializer.Serialize(AllDefault);

        (string Member, string Wire)[] expected =
        [
            ("int Count", "\"Count\":0"),
            ("bool Flag", "\"Flag\":false"),
            ("Guid Id", "\"Id\":\"00000000-0000-0000-0000-000000000000\""),
            ("double Ratio (0.0)", "\"Ratio\":0"),
            ("enum Shade (Light = 0)", "\"Shade\":0"),
        ];

        foreach (var (member, wire) in expected)
            await Assert
                .That(json)
                .Contains(wire)
                .Because($"'{member}' is at its default and must still be written");
    }

    /// <summary>
    /// The nested-object INNER HELPER opens the same guard from its own emit site
    /// (<c>JsonSerializerGenerator.GenerateInnerHelper</c>), so it gets its own assertion: the
    /// top-level path being right says nothing about the helper.
    /// </summary>
    [Test]
    public async Task Json_DefaultOption_WritesDefaultValueMembersOfANestedObject()
    {
        var json = JsonSerializer.Serialize(
            new DefaultValueWireOuter
            {
                Name = "outer",
                Inner = new DefaultValueWireModel { Name = "inner" },
            }
        );

        await Assert.That(json).Contains("\"outer\"");
        await Assert
            .That(json)
            .Contains(
                "\"Inner\":{\"Count\":0,\"Flag\":false,"
                    + "\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Ratio\":0,\"Shade\":0,"
                    + "\"Name\":\"inner\"}"
            )
            .Because(
                "the inner helper emits its own guard; a nested object's default-valued "
                    + "members must be written for the same reason as a top-level one's"
            );
    }

    /// <summary>
    /// The other half of the same branch, asserted here so this file pins the CONDITION and not
    /// just "always write": under <see cref="PicoIgnoreCondition.WhenWritingDefault"/> the very
    /// same model is written as an object holding only the member the condition cannot omit
    /// (<c>Name</c>, a non-null string — "is it default" is a value-type question).
    /// </summary>
    [Test]
    public async Task Json_WhenWritingDefault_OmitsTheVerySameMembers()
    {
        var json = JsonSerializer.Serialize(AllDefault, WhenWritingDefault);

        await Assert.That(json).IsEqualTo("{\"Name\":\"\"}");
        await Assert.That(json).DoesNotContain("Count");
        await Assert.That(json).DoesNotContain("Id");
    }

    // ── MessagePack: the same rule, proved on one header byte ──

    /// <summary>
    /// A fixmap header is the map's entry count, so it answers "did every member reach the
    /// document?" without re-reading anything: <c>0x86</c> = six entries, <c>0x82</c> = the two
    /// survivors. Measured: MessagePack writes all six under both options today, so only the
    /// default half is claimed here.
    /// </summary>
    [Test]
    public async Task MsgPack_DefaultOption_WritesEveryDefaultValueMember()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(AllDefault);

        await Assert
            .That(bytes[0])
            .IsEqualTo((byte)0x86)
            .Because(
                "0x86 is a fixmap of 6 entries — the model has 6 members, so not one of them was "
                    + "omitted for holding its default value"
            );
    }

    // ── TOML / YAML / INI: key = default literal, on the text ──

    [Test]
    public async Task Toml_DefaultOption_WritesEveryDefaultValueMember()
    {
        var toml = TomlSerializer.Serialize(AllDefault);

        await Assert.That(toml).Contains("Count = 0");
        await Assert.That(toml).Contains("Flag = false");
        await Assert.That(toml).Contains("Id = \"00000000-0000-0000-0000-000000000000\"");
        await Assert.That(toml).Contains("Ratio = 0");
        await Assert.That(toml).Contains("Shade = \"Light\"");
    }

    [Test]
    public async Task Yaml_DefaultOption_WritesEveryDefaultValueMember()
    {
        var yaml = YamlSerializer.Serialize(AllDefault);

        await Assert.That(yaml).Contains("Count: 0");
        await Assert.That(yaml).Contains("Flag: false");
        await Assert.That(yaml).Contains("Id: 00000000-0000-0000-0000-000000000000");
        await Assert.That(yaml).Contains("Ratio: 0");
        await Assert.That(yaml).Contains("Shade: Light");
    }

    [Test]
    public async Task Ini_DefaultOption_WritesEveryDefaultValueMember()
    {
        var ini = IniSerializer.Serialize(AllDefault);

        await Assert.That(ini).Contains("Count = 0");
        await Assert.That(ini).Contains("Flag = false");
        await Assert.That(ini).Contains("Id = 00000000-0000-0000-0000-000000000000");
        await Assert.That(ini).Contains("Ratio = 0");
        await Assert.That(ini).Contains("Shade = Light");
    }

    // ── The reader side of the same claim, for the record ──

    /// <summary>
    /// Why the wire assertion above is the only kind that can work: this is the round trip that
    /// stays green when a member is silently dropped, because the dropped value IS <c>default</c>.
    /// Kept deliberately as documentation of the immunity, and as the leg that would fail the day
    /// a reader starts distinguishing "absent" from "present and default".
    /// </summary>
    [Test]
    public async Task RoundTrip_CannotSeeTheDrop()
    {
        var back = JsonSerializer.Deserialize<DefaultValueWireModel>(
            Encoding.UTF8.GetBytes("{\"Name\":\"\"}")
        );

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Count).IsEqualTo(0);
        await Assert.That(back.Flag).IsFalse();
        await Assert.That(back.Id).IsEqualTo(Guid.Empty);
        await Assert.That(back.Ratio).IsEqualTo(0.0);
        await Assert.That(back.Shade).IsEqualTo(DefaultValueShade.Light);
    }
}
