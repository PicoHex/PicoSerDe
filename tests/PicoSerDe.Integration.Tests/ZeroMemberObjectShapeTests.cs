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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

    // ══ N-1: the three boundaries no generator change can cross ══════════════════════
    //
    // BU-4 rescued the types that DECLARE nothing. Three neighbours stay dropped because C#
    // itself refuses to build them — abstract (CS0144), a static class (CS0712), a type whose
    // only constructors take parameters (CS7036) — and until N-1 they were reported with the ⏸
    // wording ("The json format can express this shape, but this generator has not implemented it
    // yet … reopen: …"), i.e. a member we will NEVER implement was written up as a debt to wait
    // for. AGENTS.md rule 3 forbids that in BOTH directions: ⏸ promises a reopen that cannot
    // happen, and the ⛔ template for a FORMAT fact would claim "the json format cannot represent
    // it" — false, json holds `{}` (the tests above prove it); it is the TYPE that has no
    // instance.
    //
    // The three fixtures below therefore assert the same contract: PICOSERDE001, the TERMINAL ⛔
    // marker, no ⏸, the actual cause named, and no advice the reader cannot follow ("use a format
    // that can" is a five-way dead end: no format can call `new T()` here).

    private const string Impossible = "\u26D4";
    private const string Deferred = "\u23F8";

    /// <summary>
    /// Abstract: the reader's <c>new ShapeMark()</c> is CS0144, so the member cannot round-trip in
    /// any format. Terminal — and it must SAY so instead of promising a reopen.
    /// </summary>
    [Test]
    public async Task AbstractZeroMemberType_IsATerminalDrop_NotADebt()
    {
        const string source = """
            using PicoJetson;

            public abstract class ShapeMark { }

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        await AssertTerminalDrop(
            SilentDropDiagnosticTests.RunJson(source),
            "ShapeDoc.Mark",
            "ShapeMark",
            "abstract"
        );
    }

    /// <summary>
    /// Only <c>Mark(int)</c>: no parameterless constructor exists, so <c>new T()</c> is CS7036.
    /// This is the N-1 fixture that is NOT about the class being abstract — the type is perfectly
    /// concrete and still impossible to build from a document.
    /// </summary>
    [Test]
    public async Task ZeroMemberTypeWithOnlyAParameterizedConstructor_IsATerminalDrop_NotADebt()
    {
        const string source = """
            using PicoJetson;

            public sealed class ShapeMark
            {
                public ShapeMark(int seed) { _ = seed; }
            }

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        await AssertTerminalDrop(
            SilentDropDiagnosticTests.RunJson(source),
            "ShapeDoc.Mark",
            "ShapeMark",
            "no accessible parameterless constructor"
        );
    }

    /// <summary>
    /// A static class is not a value at all: <c>new T()</c> is CS0712 (and the member declaration
    /// itself is CS0723 — the generator still extracts it, which is exactly the build-time moment
    /// this wording is read in).
    /// </summary>
    [Test]
    public async Task StaticClassMemberType_IsATerminalDrop_NotADebt()
    {
        const string source = """
            using PicoJetson;

            public static class ShapeMark
            {
                public static int Value => 1;
            }

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        await AssertTerminalDrop(
            SilentDropDiagnosticTests.RunJson(source),
            "ShapeDoc.Mark",
            "ShapeMark",
            "static class"
        );
    }

    /// <summary>
    /// The positive control that keeps N-1 from becoming a blanket ⛔: the SAME model with the one
    /// keyword that makes the type constructible is not reported at all and still reaches the wire
    /// as <c>{}</c>. Without this pair, "the drop is terminal" could be satisfied by terminalising
    /// every zero-member drop.
    /// </summary>
    [Test]
    public async Task TheConcreteTwinOfTheTerminalFixtures_IsStillTheEmptyObject()
    {
        const string source = """
            using PicoJetson;

            public sealed class ShapeMark { }

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        var (ids, generated, messages) = SilentDropDiagnosticTests.RunJson(source);

        await Assert
            .That(ids)
            .DoesNotContain(UnresolvableTypeId)
            .Because(
                "a zero-member type that CAN be built is the empty object, not a drop: "
                    + string.Join(" | ", messages)
            );
        await Assert
            .That(generated)
            .Contains("ShapeMark")
            .Because("the member must reach the generated serializer");
    }

    // ══ N-4: `internal` counts when the type is compiled ALONGSIDE us ════════════════
    //
    // The generated code lands in the CONSUMER assembly, so `new ShapeMark()` on a type declared
    // there with an `internal` parameterless constructor compiles — demanding `public` dropped a
    // shape that was one keyword away from working. The allowance is "declared in this
    // compilation", not "internal": a type from a REFERENCED package keeps its drop, because that
    // constructor is not callable from the assembly we generate into.

    [Test]
    public async Task ZeroMemberTypeWithAnInternalParameterlessConstructor_IsTheEmptyObject()
    {
        const string source = """
            using PicoJetson;

            public sealed class ShapeMark
            {
                internal ShapeMark() { }
            }

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        var (ids, generated, messages) = SilentDropDiagnosticTests.RunJson(source);

        await Assert
            .That(ids)
            .DoesNotContain(UnresolvableTypeId)
            .Because(
                "`new ShapeMark()` is callable from the generated code — both live in the "
                    + $"consumer assembly: {string.Join(" | ", messages)}"
            );
        await Assert
            .That(generated)
            .Contains("ShapeMark")
            .Because("the member must reach the generated serializer");
    }

    /// <summary>
    /// The other half of N-4, and the reason the widening is conditional: a zero-member type whose
    /// only constructor is <c>internal</c> and which arrives from METADATA (a referenced package)
    /// cannot be built by the generated code at all, so it must keep the loud terminal drop
    /// instead of emitting <c>new LibMark()</c> into a CS1729.
    /// </summary>
    [Test]
    public async Task ZeroMemberTypeWithAnInternalConstructorFromMetadata_StaysATerminalDrop()
    {
        var reference = InternalCtorLibrary();
        const string source = """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public MetadataLib.LibMark? Mark { get; set; }
            }
            """;

        await AssertTerminalDrop(
            SilentDropDiagnosticTests.RunJsonWithRefs(source, [reference]),
            "ShapeDoc.Mark",
            "LibMark",
            "no accessible parameterless constructor"
        );
    }

    /// <summary>
    /// One assertion set for every N-1 fixture: the id, the ⛔ marker, the ABSENCE of ⏸ and of the
    /// reopen pointer, the cause named in the message, the advice that actually exists, and the
    /// member missing from the generated code (which is why it has to be reported).
    /// </summary>
    private static async Task AssertTerminalDrop(
        (string[] Ids, string Generated, string[] Messages) run,
        string member,
        string typeName,
        string cause
    )
    {
        await Assert
            .That(run.Ids)
            .Contains(UnresolvableTypeId)
            .Because($"{member} is still dropped, so it must be reported");

        var message = run.Messages.FirstOrDefault(m =>
            m.Contains(member, StringComparison.Ordinal)
        );

        await Assert
            .That(message)
            .IsNotNull()
            .Because($"the message must name the member: {string.Join(" | ", run.Messages)}");

        await Assert
            .That(message!.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because(
                "N-1: no instance of the type can be created in ANY format, so the verdict is "
                    + $"terminal (⛔), never a reopenable debt (⏸): {message}"
            );
        await Assert
            .That(message.Contains(Deferred, StringComparison.Ordinal))
            .IsFalse()
            .Because("⏸ would tell the reader to wait for us about a member we cannot implement");
        await Assert
            .That(message.Contains("not implemented yet", StringComparison.Ordinal))
            .IsFalse()
            .Because($"the old wording claimed a gap to reopen: {message}");
        await Assert
            .That(message.Contains("reopen:", StringComparison.Ordinal))
            .IsFalse()
            .Because(
                $"there is nothing to reopen — the type, not the emitter, is the limit: {message}"
            );
        await Assert
            .That(message)
            .Contains(cause)
            .Because("the message has to name the actual cause, not the symptom");
        await Assert
            .That(message.Contains("use a format that can", StringComparison.Ordinal))
            .IsFalse()
            .Because(
                "every format holds the empty object; none can call `new` on this type, so that "
                    + "advice is a five-way dead end"
            );
        await Assert
            .That(run.Generated)
            .DoesNotContain(typeName)
            .Because("the member is not emitted — that is exactly why it must be reported");
    }

    /// <summary>
    /// An in-memory assembly declaring <c>MetadataLib.LibMark</c> with ONLY an internal
    /// parameterless constructor — the shape that is legal in a library and unbuildable from the
    /// assembly we generate into. Compiled rather than hand-written because only a metadata symbol
    /// has no source declarations, which is the oracle <c>HasParameterlessConstructor</c> reads.
    /// </summary>
    private static MetadataReference InternalCtorLibrary()
    {
        var compilation = CSharpCompilation.Create(
            "InternalCtorLib",
            [
                CSharpSyntaxTree.ParseText(
                    "namespace MetadataLib { public sealed class LibMark { internal LibMark() { } } }",
                    new CSharpParseOptions(LanguageVersion.Latest)
                ),
            ],
            AllFormatReferences.References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);

        if (!emitted.Success)
            throw new InvalidOperationException(
                "the N-4 metadata fixture must compile: "
                    + string.Join(" | ", emitted.Diagnostics.Select(d => d.ToString()))
            );

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    // ══ N-5: TOML's empty-object predicate asks what extraction SAW ══════════════════
    //
    // `IsEmptyObjectShape(p) => p.NestedProperties.Length == 0 && !p.IsRecursiveRef` is "we saw no
    // member", not "the type declares no member", and the review was right that the two differ: a
    // type whose only member is DROPPED (a KeyValuePair-shaped one) also comes back with no nested
    // properties. Measured, that is the correct arm: TOML writes `[Mark]`, exactly what json's
    // `{}` says about the same model, AND the dropped member keeps its own PICOSERDE001 — so the
    // empty table is never a silent loss. The alternative (the scalar arm) writes the OBJECT as
    // the value, a compile error in the consumer's build; that is the bug the predicate closed.
    // The question "does the type declare zero members?" is the RESOLVER's
    // (`DeclaresPublicInstanceData`), which is where it decides the shape and, since N-1, the
    // verdict.

    [Test]
    public async Task Toml_TypeWhoseMembersWereAllDropped_IsStillAnEmptyTable_AndTheDropIsLoud()
    {
        const string source = """
            using PicoToml;

            public sealed class ShapeMark
            {
                public System.Collections.Generic.KeyValuePair<int, int> Pair { get; set; }
            }

            [PicoTomlSerializable]
            public partial class ShapeDoc
            {
                public string Name { get; set; } = "";
                public ShapeMark? Mark { get; set; }
            }
            """;

        var (ids, generated, messages) = SilentDropDiagnosticTests.RunToml(source);

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because(
                "the drop of the only member must stay loud — that is what makes the empty table "
                    + $"acceptable: {string.Join(" | ", messages)}"
            );
        await Assert
            .That(
                messages.Any(m =>
                    m.Contains("ShapeMark.Pair", StringComparison.Ordinal)
                    && m.StartsWith(Deferred, StringComparison.Ordinal)
                )
            )
            .IsTrue()
            .Because(
                $"the message must name the member that was dropped: {string.Join(" | ", messages)}"
            );
        await Assert
            .That(generated)
            .Contains("WriteNestedTable(\"Mark\"u8)")
            .Because(
                $"an empty table is TOML's empty object, so the nested-table arm is right: no member of ShapeMark reached the emitter"
            );
        await Assert
            .That(generated)
            .DoesNotContain("WriteKeyValue(\"Mark\"u8, ")
            .Because(
                "the scalar arm would write the OBJECT as a value — a compile error in the "
                    + "consumer's build, which is the bug IsEmptyObjectShape closed"
            );
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
