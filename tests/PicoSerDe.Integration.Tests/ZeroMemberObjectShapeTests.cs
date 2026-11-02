// BU-4: a [PicoSerializable] type with ZERO members is a SHAPE — an empty object — and a
// member of that type must reach the wire as that format's empty object, not disappear.
//
// The bug as reported: `[PicoSerializable] public sealed class CapabilityMark { }` held as a
// member made the WHOLE member vanish, so the parent serialized as
// `{"sessionCapabilities":{}}` — the member was not "an empty object", it was absent, and
// the reader got a silently wrong document (R2's "no silent loss" rule).
//
// The cause is one arm of `shared/Gen/TypeKindResolver.Resolve`: its "nested complex types"
// block only answered "object" when the type DECLARES a public instance property with a
// getter or a public instance field. A type that declares none fell through to `(null, …)` —
// "not resolvable for the current format" — and the extractor dropped the member with
// PICOSERDE001 ⏸ ("not implemented yet: this format CAN hold it"). ⏸ is the debt marker, so
// the report's own wording applies: the format can express it and we never implemented it.
//
// WHAT THIS FILE PINS, per format:
//
//   format   member `Mark`    element `Marks`         verdict
//   json     `"Mark":{}`      `"Marks":[{},{}]`       supported
//   msgpack  empty map        empty map in an array   supported
//   toml     `[Mark]` header  `[[Marks]]` header      supported
//   ini      `[Mark]` header  (none: S2 x ini is a format fact)  supported
//   yaml     — still DROPPED  — still DROPPED         ⏸ DEBT (see below)
//
// WHY YAML IS NOT FIXED HERE: the format CAN hold it — PyYAML reads `a: {}` as `{'a': {}}`
// (measured) and our own `YamlReader` reads the empty flow mapping (pinned by
// `Yaml_Reader_HoldsTheEmptyFlowMapping` below) — but `YamlWriter` has no empty-mapping FORM:
// its block mapper writes nothing for a mapping with no members and `Mark:` is YAML for null,
// not for `{}`. Adding one is a runtime change (`api/PicoYaml.public.txt` moves with it), which
// BU-4 is not allowed to make, so the shape stays a REGISTERED ⏸ debt — never ⛔, because
// calling our missing writer method a format limit is exactly the lie AGENTS.md rule 3 forbids.
// `DecisionTables.EmptyObjectFormats` names yaml as missing for that reason and nothing else.
//
// The fixtures are attribute-free where the INTERCEPTOR pipeline can carry them (that is how
// `NestedListOfObjectsTests` drives the same nested-object shapes): a `[PicoSerializable]`
// attribute would make the YAML generator emit a serializer for these types too, and its
// registered ⏸ drop would put two build warnings in this project that no test here is about.
// The attribute pipeline the report used is driven in-proc at the bottom of the file.

using System.Text;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// BU-4's type: zero members. Its serialized shape is the empty object.
/// </summary>
public sealed class CapabilityMark { }

/// <summary>
/// The parent that holds <see cref="CapabilityMark"/> in both reported positions: a plain
/// nullable member and an object-list element.
/// </summary>
public sealed class CapabilitySession
{
    public CapabilityMark? Mark { get; set; }
    public List<CapabilityMark> Marks { get; set; } = new();
}

/// <summary>
/// The INI-shaped parent: one object member and no object LIST, because an INI section cannot
/// carry a list of objects (spec §3 S2 is a format fact, reported as PICOSERDE004 project-wide).
/// </summary>
public sealed class FlatCapabilitySession
{
    public CapabilityMark? Mark { get; set; }
}

/// <summary>
/// A non-empty nested object with NO collection member — the middle level of the two-level case
/// below. It deliberately carries no object list: TOML's read path refuses an object collection
/// inside a nested table (EmitNestedObjectRead's HasNestedObjectCollection guard, pre-existing and
/// unrelated to BU-4), and that refusal would hide the path this fixture is about.
/// </summary>
public sealed class NestedMarkHolder
{
    public CapabilityMark? Mark { get; set; }
}

/// <summary>
/// Two levels of nesting: the empty object inside a non-empty object inside the root. TOML
/// carries each level as its own table header (<c>[Inner]</c>, <c>[Inner.Mark]</c>), so the new
/// empty-object arm has to close the path the same way a non-empty member does.
/// </summary>
public sealed class OuterCapabilityDoc
{
    public NestedMarkHolder? Inner { get; set; }
}

/// <summary>
/// The reported bug, one assertion per format and per position: a member whose type has zero
/// members is the empty object OF THAT FORMAT, and it survives the round trip.
/// </summary>
public class ZeroMemberObjectShapeTests
{
    // ── JSON ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Json_ZeroMemberObjectMember_IsEmittedAsAnEmptyObject()
    {
        var json = JsonSerializer.Serialize(new FlatCapabilitySession { Mark = new() });

        await Assert
            .That(json)
            .Contains("\"Mark\":{}")
            .Because(
                "BU-4: a member whose type declares zero members is an empty OBJECT; dropping "
                    + $"the member reported a shape every format can hold: {json}"
            );
    }

    [Test]
    public async Task Json_ZeroMemberObjectElementInAList_IsEmittedAsAnEmptyObject()
    {
        var json = JsonSerializer.Serialize(
            new CapabilitySession
            {
                Marks = new List<CapabilityMark> { new(), new() },
            }
        );

        await Assert
            .That(json)
            .Contains("\"Marks\":[{},{}]")
            .Because($"the element position must reach the wire as `{{}}` too: {json}");
    }

    [Test]
    public async Task Json_ZeroMemberObject_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new CapabilitySession
                {
                    Mark = new(),
                    Marks = new List<CapabilityMark> { new() },
                }
            )
        );

        var back = JsonSerializer.Deserialize<CapabilitySession>(bytes);

        await Assert.That(back!.Mark).IsNotNull().Because("`{}` must read back as an instance");
        await Assert.That(back.Marks.Count).IsEqualTo(1);
        await Assert.That(back.Marks[0]).IsNotNull();
    }

    // ── TOML ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Toml_ZeroMemberObjectMember_IsEmittedAsATableHeaderWithNoKeys()
    {
        var toml = TomlSerializer.Serialize(new FlatCapabilitySession { Mark = new() });

        await Assert
            .That(toml)
            .Contains("[Mark]")
            .Because(
                "TOML's object form is a table, and an empty table is a header with no keys "
                    + $"(measured: `tomllib.loads('[Mark]')` → {{'Mark': {{}}}}): {toml}"
            );
    }

    [Test]
    public async Task Toml_ZeroMemberObjectElementInAList_IsEmittedAsAnArrayOfEmptyTables()
    {
        var toml = TomlSerializer.Serialize(
            new CapabilitySession
            {
                Marks = new List<CapabilityMark> { new(), new() },
            }
        );

        var headers = toml.Split("[[Marks]]", StringSplitOptions.None).Length - 1;

        await Assert
            .That(headers)
            .IsEqualTo(2)
            .Because(
                "one `[[Marks]]` header per element is one empty table per element (measured: "
                    + $"`tomllib.loads('[[Marks]]')` → {{'Marks': [{{}}]}}): {toml}"
            );
    }

    [Test]
    public async Task Toml_ZeroMemberObject_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes(
            TomlSerializer.Serialize(
                new CapabilitySession
                {
                    Mark = new(),
                    Marks = new List<CapabilityMark> { new() },
                }
            )
        );

        var back = TomlSerializer.Deserialize<CapabilitySession>(bytes);

        await Assert
            .That(back!.Mark)
            .IsNotNull()
            .Because($"`[Mark]` must read back as an instance: {Encoding.UTF8.GetString(bytes)}");
        await Assert.That(back.Marks.Count).IsEqualTo(1);
        await Assert.That(back.Marks[0]).IsNotNull();
    }

    /// <summary>
    /// The same shape one level deeper: an empty object under a non-empty nested object. TOML
    /// writes table headers with their ANCESTOR PATH, so this is the case where a wrong arm would
    /// produce <c>[Mark]</c> at the root instead of <c>[Session.Mark]</c> and lose the value.
    /// </summary>
    [Test]
    public async Task Toml_NestedZeroMemberObject_TwoLevelsDeep_RoundTrips()
    {
        var toml = TomlSerializer.Serialize(
            new OuterCapabilityDoc { Inner = new NestedMarkHolder { Mark = new() } }
        );

        await Assert
            .That(toml)
            .Contains("[Inner.Mark]")
            .Because($"a nested empty object must carry its ancestor path: {toml}");

        var back = TomlSerializer.Deserialize<OuterCapabilityDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Inner).IsNotNull();
        await Assert.That(back.Inner!.Mark).IsNotNull();
    }

    [Test]
    public async Task Json_NestedZeroMemberObject_TwoLevelsDeep_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new OuterCapabilityDoc { Inner = new NestedMarkHolder { Mark = new() } }
            )
        );

        var back = JsonSerializer.Deserialize<OuterCapabilityDoc>(bytes);

        await Assert.That(back!.Inner).IsNotNull();
        await Assert.That(back.Inner!.Mark).IsNotNull();
    }

    // ── MessagePack ───────────────────────────────────────────────────────────────

    [Test]
    public async Task MsgPack_ZeroMemberObject_RoundTrips()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new CapabilitySession
            {
                Mark = new(),
                Marks = new List<CapabilityMark> { new() },
            }
        );

        var back = MsgPackSerializer.Deserialize<CapabilitySession>(bytes);

        await Assert.That(back!.Mark).IsNotNull().Because("an empty map is a member value");
        await Assert.That(back.Marks.Count).IsEqualTo(1);
        await Assert.That(back.Marks[0]).IsNotNull();
    }

    // ── INI ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Ini_ZeroMemberObject_RoundTrips()
    {
        var ini = IniSerializer.Serialize(new FlatCapabilitySession { Mark = new() });

        await Assert
            .That(ini)
            .Contains("[Mark]")
            .Because(
                "INI's empty object is its own section header — a section with no keys. "
                    + $"Dropping the member instead reported a shape INI's grammar holds: {ini}"
            );

        var back = IniSerializer.Deserialize<FlatCapabilitySession>(Encoding.UTF8.GetBytes(ini));

        await Assert.That(back!.Mark).IsNotNull().Because("`[Mark]` must read back as an instance");
    }

    // ── YAML: the DEBT, measured on both sides ────────────────────────────────────

    /// <summary>
    /// The debt's READ half: our own reader already understands the empty flow mapping, so
    /// "yaml cannot hold an empty object" is false — the missing half is `YamlWriter`'s form
    /// (see the file header). This is the measurement that keeps the drop a ⏸ and not a ⛔.
    /// </summary>
    [Test]
    public async Task Yaml_Reader_HoldsTheEmptyFlowMapping()
    {
        var reader = new YamlReader(Encoding.UTF8.GetBytes("Mark: {}"));
        var tokens = new List<string>();
        while (reader.Read())
            tokens.Add(reader.TokenType.ToString());

        await Assert
            .That(string.Join(",", tokens))
            .IsEqualTo("PropertyName,ObjectStart,ObjectEnd")
            .Because(
                "the format holds `a: {}` and our reader tokenizes it as an empty mapping, so "
                    + "the writer's missing form is OUR gap, not a format limit"
            );
    }

    // ── the ATTRIBUTE pipeline the report used: the diagnostic must be gone ───────
    // The runtime tests above drive the interceptor pipeline. These drive each generator
    // directly (one model per format, with the attribute THAT generator knows) and ask the
    // question the report asked: is the member still dropped and reported? No `#pragma` in
    // the test project can hide an answer here — the diagnostics are read from the generator's
    // own output, which is exactly what `EmptyObjectFormats` promises to have cleared.

    private const string UnresolvableTypeId = "PICOSERDE001";

    [Test]
    public async Task ZeroMemberObjectMember_IsNoLongerDroppedByTheFormatsThatHoldIt()
    {
        foreach (var (format, ns, attr, run) in Drivers())
        {
            if (format is "yaml")
                continue; // the registered debt, pinned by the test below

            var (ids, generated, messages) = run(ZeroMemberModel(ns, attr));

            await Assert
                .That(ids)
                .DoesNotContain(UnresolvableTypeId)
                .Because(
                    $"{format}: a member whose type declares zero members is the empty object, "
                        + $"and {format} has the form for it: {string.Join(" | ", messages)}"
                );
            await Assert
                .That(generated)
                .Contains("ShapeMark")
                .Because($"{format}: the member must reach the generated code");
        }
    }

    [Test]
    public async Task Yaml_ZeroMemberObjectMember_IsStillADeclaredDebt()
    {
        var (ids, _, messages) = SilentDropDiagnosticTests.RunYaml(
            ZeroMemberModel("PicoYaml", "PicoYamlSerializable")
        );

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because(
                "yaml keeps the drop until YamlWriter has an empty-mapping form — a registered "
                    + "debt that must stay VISIBLE, not a silent loss"
            );

        var message = messages.Single(m => m.Contains("ShapeDoc.Mark", StringComparison.Ordinal));

        await Assert
            .That(message.StartsWith("\u23F8", StringComparison.Ordinal))
            .IsTrue()
            .Because(
                "YAML CAN hold an empty object (PyYAML and our own reader say so), so the drop "
                    + $"is our debt (⏸) and never a format limit (⛔): {message}"
            );
        await Assert
            .That(message.Contains("\u26D4", StringComparison.Ordinal))
            .IsFalse()
            .Because("a ⛔ here would tell the reader to change a perfectly expressible type");
    }

    /// <summary>
    /// The five formats' in-proc drivers — same tuple shape as
    /// <see cref="SilentDropDiagnosticTests"/>' own table, so the format tag, the namespace
    /// and the attribute cannot drift apart between the two lists.
    /// </summary>
    private static (
        string Format,
        string Ns,
        string Attr,
        Func<string, (string[] Ids, string Generated, string[] Messages)> Run
    )[] Drivers() =>
        [
            ("json", "PicoJetson", "PicoJsonSerializable", SilentDropDiagnosticTests.RunJson),
            (
                "msgpack",
                "PicoMsgPack",
                "PicoMsgPackSerializable",
                SilentDropDiagnosticTests.RunMsgPack
            ),
            ("toml", "PicoToml", "PicoTomlSerializable", SilentDropDiagnosticTests.RunToml),
            ("ini", "PicoIni", "PicoIniSerializable", SilentDropDiagnosticTests.RunIni),
            ("yaml", "PicoYaml", "PicoYamlSerializable", SilentDropDiagnosticTests.RunYaml),
        ];

    /// <summary>
    /// The reported model, in the attribute form the bug report used: a zero-member type held
    /// by a partial, attributed parent.
    /// </summary>
    private static string ZeroMemberModel(string ns, string attr) =>
        $$"""
            using {{ns}};

            public sealed class ShapeMark { }

            [{{attr}}]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;
}
