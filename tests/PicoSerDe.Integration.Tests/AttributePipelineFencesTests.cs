// Fences for the ATTRIBUTE PIPELINE — the five holes ablation wave 3 / group D found
// protected-but-unasserted. Each item below names the mutation that must turn it red, and
// every assertion sits on the ARTIFACT (the document, the payload, the decoded value), never
// on "the generator ran".
//
// ── What a hole is, and why these five ─────────────────────────────────────────────────────────
// A green suite says the tests pass; it does not say they would fail. The ablation breaks one
// protection at a time and looks for red. Green under a mutation means nobody is watching — a
// defect in the fence, not in the code. These five came back green:
//
//   A1  GenInfrastructure.cs:3018-3019  member key vs class-level camelCase, named-type path
//   A2  GenInfrastructure.cs:2444       [PicoIgnore(Condition = Never)] must NOT cancel a
//                                       format-level [XxxIgnore]; the format marker is
//                                       unconditional and cannot be overridden
//   A3  GenInfrastructure.cs:1432       WhenWritingDefault must not degrade to "always drop":
//                                       a NON-default value still goes on the wire
//   A9  AttributeLookup.cs:186-193      the declaring-namespace filter: a same-named attribute
//                                       from a foreign namespace must not be believed
//   A10 AnonTypeHandler.cs:293-294      A1's logic, second copy — the anonymous-type path
//
// ── A1 and A10: why both, and what the second one can and cannot prove ────────────────────────
// The two sites are the same expression written twice (`GetCustomName ?? camelCase(member)`),
// so a mutation of the rule has to be applied TWICE to be complete — which is exactly the shape
// a fence forgets. A10's assertion is therefore written too, and the report records the measured
// answer: an ANONYMOUS type's members cannot carry attributes (C# gives a member declarator no
// attribute target, and the synthesized property's `GetAttributes()` is empty), so
// `GetCustomName` is always null there and `HasCamelCase(anonType)` is always false. The
// briefed mutation at A10 alone is consequently invisible from outside — the line is a copy of
// a rule onto a path that cannot reach either operand. What CAN be watched there, and is
// watched below, is the other half: the anon path must not apply camelCase of its own accord.

namespace PicoSerDe.Integration.Tests;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// A1 — an explicit member-level [XxxKey] OUTRANKS the class-level [XxxCamelCase]
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Mutation (A1): `GetCustomName(prop) ?? (useCamelCase ? ToCamelCase(prop.Name) : prop.Name)`
// becomes `useCamelCase ? ToCamelCase(prop.Name) : (GetCustomName(prop) ?? prop.Name)` — the
// class flag wins. `DisplayName` then goes out as `displayName` and `custom_name` vanishes from
// the document AND from the reader's key table, which is what the three assertions below see.

[PicoSerializable]
[JsonCamelCase]
public class AttrKeyBeatsCamelModel
{
    // The member name IS camelCase-able on purpose: under the mutation the difference is
    // `custom_name` vs `displayName`, not "attribute honoured vs ignored".
    [JsonPropertyName("custom_name")]
    public string DisplayName { get; set; } = string.Empty;

    // Control: the class flag still has to do its job on the member that carries no key.
    public string OtherValue { get; set; } = string.Empty;
}

public class A1_KeyBeatsClassCamelCaseTests
{
    [Test]
    public async Task A1_ExplicitMemberKey_IsTheWireName_EvenUnderClassLevelCamelCase()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new AttrKeyBeatsCamelModel { DisplayName = "attr", OtherValue = "flag" }
        );

        // The key the member declared, not the one the class flag would have produced.
        await Assert.That(json).Contains("\"custom_name\":\"attr\"");
        await Assert.That(json).DoesNotContain("displayName");
        await Assert.That(json).DoesNotContain("DisplayName");
        // Control: camelCase is still applied where no member key competes.
        await Assert.That(json).Contains("\"otherValue\":\"flag\"");
    }

    [Test]
    public async Task A1_TheReaderAcceptsTheExplicitMemberKey()
    {
        // The same fact on the read side: if the writer and the reader disagreed about which
        // name the member answers to, a green round trip could still be reading the OTHER key.
        var back = PicoJetson.JsonSerializer.Deserialize<AttrKeyBeatsCamelModel>(
            "{\"custom_name\":\"attr\",\"otherValue\":\"flag\"}"u8
        );

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.DisplayName).IsEqualTo("attr");
        await Assert.That(back.OtherValue).IsEqualTo("flag");
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// A2 — the format-level marker is UNCONDITIONAL: [PicoIgnore(Never)] cannot cancel it
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Mutation (A2): the guard at :2444 becomes conditional on the cross-format condition —
// `if (ignoreCondition is not "Never" && attrs.HasIgnore(prop))`. The member then survives into
// the document of the format that marked it, which is what these assertions forbid.
//
// The second role of the test is the CONTROL: `Never` alone must not strip anything. Without
// it a mutation that simply drops every member would also pass the negative half.

[PicoSerializable]
public class AttrFormatIgnoreBeatsPicoIgnoreModel
{
    [JsonIgnore]
    [PicoIgnore(Condition = PicoIgnoreCondition.Never)]
    public string JsonHidden { get; set; } = "json-leak";

    [YamlIgnore]
    [PicoIgnore(Condition = PicoIgnoreCondition.Never)]
    public string YamlHidden { get; set; } = "yaml-leak";

    public string Kept { get; set; } = "kept";
}

public class A2_FormatIgnoreIsNotCancelableTests
{
    // One test per format on purpose: the mutation is caught by ONE of them at a time, and a
    // shared method would report only the first failing assertion — leaving the other format's
    // half of the contract unmeasured in the red run.
    [Test]
    public async Task A2_Json_FormatMarker_StillStrips_WhenPicoIgnoreSaysNever()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new AttrFormatIgnoreBeatsPicoIgnoreModel()
        );

        await Assert.That(json).DoesNotContain("JsonHidden");
        await Assert.That(json).DoesNotContain("json-leak");
        await Assert.That(json).Contains("\"Kept\":\"kept\"");
    }

    [Test]
    public async Task A2_Yaml_FormatMarker_StillStrips_WhenPicoIgnoreSaysNever()
    {
        var yaml = PicoYaml.YamlSerializer.Serialize(
            new AttrFormatIgnoreBeatsPicoIgnoreModel()
        );

        await Assert.That(yaml).DoesNotContain("YamlHidden");
        await Assert.That(yaml).DoesNotContain("yaml-leak");
        await Assert.That(yaml).Contains("Kept:");
    }

    [Test]
    public async Task A2_PicoIgnoreNeverAlone_StripsNothing()
    {
        var model = new AttrFormatIgnoreBeatsPicoIgnoreModel();

        // Each format keeps the member the OTHER format marked: `Never` is an exemption from a
        // global condition, never an ignore in its own right (PicoSerDe.Core's contract).
        var json = PicoJetson.JsonSerializer.Serialize(model);
        await Assert.That(json).Contains("\"YamlHidden\":\"yaml-leak\"");

        var yaml = PicoYaml.YamlSerializer.Serialize(model);
        await Assert.That(yaml).Contains("JsonHidden: json-leak");
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// A3 — per-property WhenWritingDefault is a VALUE test, not "always drop"
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Mutation (A3): `3 => "WhenWritingDefault"` becomes `3 => "Always"` at :1432. The member is
// then stripped from the generated serializer entirely, so a member holding 7 disappears — the
// collapse is invisible to any round trip (the reader leaves it at default, and default is what
// a collapse produces), which is why the assertions are on the document/payload.
//
// One JSON test and one MessagePack test: the guard is emitted by each generator separately.
// The unannotated same-typed member is the control that keeps the test from passing on a
// serializer that drops nothing conditionally at all.

[PicoSerializable]
public class AttrWhenWritingDefaultModel
{
    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingDefault)]
    public int? OptCount { get; set; } = 7;

    // Control: same type, no condition — the doc has to distinguish "kept because non-default"
    // from "kept because unannotated".
    public int? ControlCount { get; set; } = 9;
}

public class A3_WhenWritingDefaultKeepsNonDefaultTests
{
    [Test]
    public async Task A3_Json_WhenWritingDefault_WritesTheNonDefaultValue()
    {
        var json = PicoJetson.JsonSerializer.Serialize(new AttrWhenWritingDefaultModel());

        await Assert.That(json).Contains("\"OptCount\":7");
        await Assert.That(json).Contains("\"ControlCount\":9");
    }

    [Test]
    public async Task A3_MsgPack_WhenWritingDefault_WritesTheNonDefaultValue()
    {
        var bytes = PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(
            new AttrWhenWritingDefaultModel()
        );
        var map = MessagePack.MessagePackSerializer.Deserialize<Dictionary<int, object?>>(bytes);

        // MessagePack keys are declaration indices: OptCount = 0, ControlCount = 1. A collapse
        // to "Always" drops key 0 and SHIFTS the rest, so both halves matter.
        await Assert.That(map.ContainsKey(0)).IsTrue();
        await Assert.That(map.ContainsKey(1)).IsTrue();

        var back = PicoMsgPack.MsgPackSerializer.Deserialize<AttrWhenWritingDefaultModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.OptCount).IsEqualTo(7);
        await Assert.That(back.ControlCount).IsEqualTo(9);
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// A9 — the attribute lookup honours only an attribute DECLARED IN THE FORMAT'S NAMESPACE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Mutation (A9): the `inNamespace is null || ... ContainingNamespace == inNamespace` half of
// `AttributeLookup.Matches` is deleted, leaving a name-only match. `[ForeignAttributes.TomlKey]`
// is then believed and the TOML key becomes `nope`.
//
// This is the one hole whose repair needs a symbol the compiler would reject if it were the real
// attribute — hence the stand-in in `ForeignAttributes.cs`: same CLASS NAME, different declaring
// namespace, so only a lookup that forgot its namespace filter can see it.

[PicoSerializable]
public class AttrForeignKeyModel
{
    [ForeignAttributes.TomlKey("nope")]
    public string Name { get; set; } = string.Empty;
}

public class A9_ForeignNamespaceAttributeIsIgnoredTests
{
    [Test]
    public async Task A9_SameNamedAttributeFromAnotherNamespace_IsNotBelieved()
    {
        var toml = PicoToml.TomlSerializer.Serialize(new AttrForeignKeyModel { Name = "v" });

        // The wire name is the MEMBER name: the foreign attribute carries no authority.
        await Assert.That(toml).Contains("Name = \"v\"");
        await Assert.That(toml).DoesNotContain("nope");
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// A10 — A1's rule on the anonymous-type path (the second copy)
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The briefed mutation here is A1's (make the class-level camelCase win). It is applied to
// AnonTypeHandler.cs:293-294 alone and — measured, see the report — leaves everything green:
// an anonymous type member cannot carry `[XxxKey]`, and an anonymous type cannot carry
// `[XxxCamelCase]`, so both operands of that expression are constants on this path.
//
// What is watchable, and what this test watches, is that the anon path writes the MEMBER NAME.
// Mutation (A10-supplementary): drop the `attrs.HasCamelCase(anonType)` guard and camelCase
// unconditionally — this test goes red, which is what makes it a fence rather than a comment.

public class A10_AnonymousTypeWireNameTests
{
    [Test]
    public async Task A10_AnonymousTypeMemberName_IsTheWireName_AtEveryLevel()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { FirstName = "v", Inner = new { LastName = "w" } }
        );

        // Exact document: PascalCase member names, nested key included. A camelCase-ifying anon
        // path would print `firstName`/`lastName` here.
        await Assert.That(json).IsEqualTo("""{"FirstName":"v","Inner":{"LastName":"w"}}""");
    }
}
