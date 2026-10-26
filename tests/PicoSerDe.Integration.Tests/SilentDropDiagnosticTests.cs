using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoIni.Gen;
using PicoJetson.Gen;
using PicoMsgPack.Gen;
using PicoSerDe.Gen;
using PicoToml.Gen;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Two extraction paths used to drop a member with a bare <c>continue</c> and no
/// diagnostic at all — the only remaining silent data loss in the generator:
///
/// <list type="bullet">
///   <item>an INI member whose element is an object (<c>List&lt;Dto&gt;</c>);</item>
///   <item>any member whose element is an extended collection kind
///         (<c>HashSet</c>, <c>Queue</c>, <c>Stack</c>, <c>LinkedList</c>,
///         <c>ImmutableArray</c>, <c>Memory</c>, <c>ReadOnlyMemory</c>) — in
///         <b>every</b> format, JSON included.</item>
/// </list>
///
/// Both now report <c>PICOSERDE004</c> so the drop is visible in the build log.
/// These tests drive the generator in-proc because the diagnostic id (not the
/// runtime behaviour) is the contract being pinned.
///
/// <para>
/// The same file pins <c>PICOSERDE001</c> — the id for "this member's TYPE could not
/// be resolved at all". Both extraction sites (public field, property) collected it
/// into a <c>List&lt;Diagnostic&gt;</c> that no generator ever passed, so D-6 was a
/// silent loss: the collector now travels on the <c>TypeInfo</c> value and the
/// generators report it (one diagnostic per member).
/// </para>
/// </summary>
public class SilentDropDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    private const string UnresolvableTypeId = "PICOSERDE001";

    private const string PlainRow = """
        public class PlainRow
        {
            public string Name { get; set; } = "";
        }
        """;

    [Test]
    public async Task Ini_ObjectListMember_ReportsDiagnostic()
    {
        var source = $$"""
            using PicoIni;

            {{PlainRow}}

            [PicoIniSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<PlainRow> Rows { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunIni(source);
        await Assert
            .That(ids)
            .Contains(Id)
            .Because("INI cannot express an object list, so the drop must be reported");
        await Assert.That(generated).DoesNotContain("Rows").Because("the member is not emitted");
    }

    /// <summary>
    /// The debt this fixture used to pin is PAID (R2 Task 2): a <c>HashSet&lt;int&gt;</c> in
    /// ELEMENT position (spec §3 S8) is emitted as a nested array in json, so the drop
    /// diagnostic has to be gone — keeping the old assertion would demand the bug back.
    /// The still-dropped variants are the ini ones (S8 x ini is a format fact).
    /// </summary>
    [Test]
    public async Task Json_ExtendedCollectionElement_Is_Now_Emitted()
    {
        var source = """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<System.Collections.Generic.HashSet<int>> Tags { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);
        await Assert
            .That(ids)
            .DoesNotContain(Id)
            .Because("the element kind is carried by the generator now, so nothing is dropped");
        await Assert
            .That(generated)
            .Contains("Tags")
            .Because("S8 x json is Supported in the table, so the member must reach the code");
    }

    /// <summary>
    /// R2 Task 3 paid the debt this fixture used to pin: an extended collection as a dict
    /// VALUE (S10) is emitted in the four formats that can express it, so the drop
    /// diagnostic has to be gone. Keeping the old assertion would demand the bug back.
    /// </summary>
    [Test]
    public async Task MsgPack_ExtendedCollectionAsDictValue_Is_Now_Emitted()
    {
        var source = """
            using PicoMsgPack;

            [PicoMsgPackSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<int>> Tags { get; set; } = new();
            }
            """;

        var (ids, generated, _) = Run(new MsgPackSerializerGenerator(), source);
        await Assert
            .That(ids)
            .DoesNotContain(Id)
            .Because("the value position is implemented now, so nothing is dropped");
        await Assert
            .That(generated)
            .Contains("Tags")
            .Because("S10 x msgpack is Supported in the table, so the member must reach the code");
    }

    /// <summary>
    /// Control: the diagnostic must stay specific to unrepresentable shapes, otherwise
    /// this change would turn every ordinary object list into a build warning.
    /// </summary>
    [Test]
    public async Task Json_ObjectListMember_ReportsNoDiagnostic()
    {
        var source = $$"""
            using PicoJetson;

            {{PlainRow}}

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<PlainRow> Rows { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Rows").Because("JSON emits object lists");
    }

    /// <summary>
    /// Control: a flat scalar list in INI is representable (<c>K = [1, 2]</c>), so the
    /// new INI branch must not fire for it.
    /// </summary>
    [Test]
    public async Task Ini_FlatScalarListMember_ReportsNoDiagnostic()
    {
        var source = """
            using PicoIni;

            [PicoIniSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<int> Nums { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunIni(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Nums").Because("INI emits flat scalar lists");
    }

    // ── PICOSERDE001 (D-6): an unresolvable TYPE must not vanish silently ─────────────

    /// <summary>
    /// <c>Opaque</c> has no public member, so <c>TypeKindResolver</c> resolves nothing
    /// for it: the property could never be emitted. Before Task 5 the extractor pushed
    /// a diagnostic into a list nobody passed — the member simply disappeared.
    /// </summary>
    [Test]
    public async Task Unresolvable_property_type_reports_PICOSERDE001()
    {
        const string source = """
            using PicoJetson;

            public class Opaque { }

            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var (ids, generated, messages) = RunJson(source);

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because("a property whose type cannot be resolved is dropped and must say so");
        await Assert
            .That(ids.Count(i => i == UnresolvableTypeId))
            .IsEqualTo(1)
            .Because("one diagnostic per member, not one per extraction pass");
        await Assert
            .That(generated)
            .DoesNotContain("public global::Opaque V")
            .Because("the member is not emitted — that is exactly why it must be reported");
        await Assert
            .That(messages.Any(m => m.Contains("Doc.V")))
            .IsTrue()
            .Because($"the message must name the member, got: {string.Join(" | ", messages)}");
    }

    /// <summary>
    /// The public-field site of the same extractor. Reached with
    /// <c>[PicoSerializable(IncludeFields = true)]</c> (or a ref struct); its
    /// unresolvable-type `continue` was the other silent point of D-6.
    /// </summary>
    [Test]
    public async Task Unresolvable_public_field_reports_PICOSERDE001()
    {
        const string source = """
            using PicoSerDe.Core;

            public class Opaque { }

            [PicoSerializable(IncludeFields = true)]
            public sealed class Doc
            {
                public string Name = "";
                public Opaque V = new();
            }
            """;

        var (ids, generated, messages) = RunJson(source);

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because("the public-field path drops unresolvable fields too");
        await Assert
            .That(messages.Any(m => m.Contains("Doc.V")))
            .IsTrue()
            .Because($"the field must be named, got: {string.Join(" | ", messages)}");
        await Assert.That(generated).DoesNotContain("public global::Opaque V");
    }

    /// <summary>
    /// The same type is discovered once per pipeline (usage-driven, <c>[PicoSerializable]</c>,
    /// <c>[PicoJsonSerializable]</c>, ...). Each discovery re-extracts and re-records the
    /// drop, so the reporting side must dedupe — otherwise one bad member produces one
    /// warning per pipeline (review focus "diagnostics that multiply").
    /// </summary>
    [Test]
    public async Task Member_discovered_by_two_pipelines_reports_one_diagnostic()
    {
        const string source = """
            using PicoJetson;
            using PicoSerDe.Core;

            public class Opaque { }

            [PicoSerializable]
            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var (ids, _, _) = RunJson(source);

        await Assert
            .That(ids.Count(i => i == UnresolvableTypeId))
            .IsEqualTo(1)
            .Because("two discovery pipelines, one dropped member, one diagnostic");
    }

    /// <summary>
    /// Control: a resolvable member type must not produce PICOSERDE001 — the wiring
    /// must not turn every ordinary DTO into a warning.
    /// </summary>
    [Test]
    public async Task Resolvable_member_type_reports_no_PICOSERDE001()
    {
        var source = $$"""
            using PicoJetson;

            {{PlainRow}}

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public PlainRow Row { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);

        await Assert.That(ids).DoesNotContain(UnresolvableTypeId);
        await Assert.That(generated).Contains("Row");
    }

    /// <summary>
    /// I-1: PICOSERDE001 has to carry the member's DECLARATION SITE. Two things depend on
    /// it, and both were lost while the diagnostic was reported with a null location: the
    /// build log can point at the member (file(line,col)) only with one, and
    /// <c>#pragma warning disable PICOSERDE001</c> can silence one member in one file only
    /// with one. Without a location the sole way out is a project-wide <c>NoWarn</c>, which
    /// also silences the next member that is dropped by a real mistake.
    /// </summary>
    [Test]
    public async Task PICOSERDE001_points_at_the_dropped_member_declaration()
    {
        const string source = """
            using PicoJetson;

            public class Opaque { }

            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var warning = RunDiagnostics(new JsonSerializerGenerator(), source)
            .Single(d => d.Id == UnresolvableTypeId);

        await Assert
            .That(warning.Location.IsInSource)
            .IsTrue()
            .Because(
                $"PICOSERDE001 must be navigable and pragma-suppressible, got location "
                    + $"'{warning.Location}' (Kind={warning.Location.Kind})"
            );

        // It also has to point at the MEMBER, not at the containing type or at line 1:
        // the pragma a reader writes around `V` only works if the location is inside that
        // region, and an IDE jump has to land on the member that was dropped.
        var lines = source.Split('\n');
        var declaredOn = Array.FindIndex(
            lines,
            l => l.Contains("Opaque V", StringComparison.Ordinal)
        );
        var span = warning.Location.GetLineSpan();

        await Assert
            .That(span.StartLinePosition.Line)
            .IsEqualTo(declaredOn)
            .Because(
                "the location must be the member's own line, not its containing type: "
                    + $"got line {span.StartLinePosition.Line}, the member is on {declaredOn}"
            );
        await Assert
            .That(lines[declaredOn][span.StartLinePosition.Character..])
            .StartsWith("V")
            .Because("the location must start at the member name");
    }

    // ── I-5 (D-4): ONE drop id, THREE verdicts — ⛔ "the format cannot", ⏸ "we have
    // not", ⚠ "this member cannot be read at all" ──
    // W4 left I-5 unenforced: every drop used one sentence ("cannot be represented by this
    // format"), so the reader of a REPORTABLE cell was told to change a type that is
    // perfectly expressible (D-4 ②). These tests pin the two verdicts against the facts
    // table, one cell at a time, in BOTH directions: a format fact must not claim a debt,
    // and a debt must not claim the format cannot hold the shape.
    //
    // The glyphs are repeated here ON PURPOSE (fix round 1): the templates take them from
    // `GenInfrastructure.{Impossible,Deferred,MemberError}Marker` — that is the single
    // source — and this file spells them out as a GOLDEN value. The shared infrastructure
    // is an analyzer-only compile item (not linked into this project), so the test cannot
    // reference the constant; keeping the literal means changing the constant turns these
    // tests red instead of letting the generator and the tests drift apart silently.

    private const string Impossible = "\u26D4";
    private const string Deferred = "\u23F8";
    private const string MemberError = "\u26A0";

    /// <summary>§3 S5: <c>List&lt;int?&gt;</c> — TOML has no null literal, YAML has one.</summary>
    private static string NullableIntList(string ns, string attr) =>
        $$"""
            using {{ns}};

            [{{attr}}]
            public partial class Doc
            {
                public System.Collections.Generic.List<int?> V { get; set; } = new();
            }
            """;

    /// <summary>§3 S14: <c>List&lt;Row?&gt;</c> — a null ELEMENT of an object list.</summary>
    private static string ObjectNullElementList(string ns, string attr) =>
        $$"""
            using {{ns}};

            public class Row
            {
                public string Name { get; set; } = "";
            }

            [{{attr}}]
            public partial class Doc
            {
                public System.Collections.Generic.List<Row?> V { get; set; } = new();
            }
            """;

    /// <summary>
    /// Plan Task 7 Step 1 / spec §4 D-4 ①: a recursive skip must name a member the USER
    /// declared. The declaring type used to be rendered with the property's nullable
    /// annotation, so the message read <c>Doc2?.Child</c> — a name nobody wrote and nobody
    /// can search for.
    /// </summary>
    [Test]
    public async Task Recursive_diagnostic_names_the_member_the_user_declared()
    {
        const string mutual = """
            using PicoYaml;
            [PicoYamlSerializable] public partial class Doc { public string K { get; set; } = ""; public Doc2? Child { get; set; } }
            [PicoYamlSerializable] public partial class Doc2 { public string K { get; set; } = ""; public Doc? Child { get; set; } }
            """;

        var messages = RunYaml(mutual).Messages;

        await Assert
            .That(messages.Any(m => m.Contains("Doc.Child") || m.Contains("Doc2.Child")))
            .IsTrue()
            .Because(
                $"the drop must name a member of a type the user declared: {string.Join(" | ", messages)}"
            );
        await Assert
            .That(messages.Any(m => m.Contains("?.Child")))
            .IsFalse()
            .Because("`Doc2?.Child` is not a member the user declared (D-4 ①)");
    }

    /// <summary>§3 S10: a dict whose VALUE is a collection (D-3).</summary>
    private static string DictOfList(string ns, string attr) =>
        $$"""
            using {{ns}};

            [{{attr}}]
            public partial class Doc
            {
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> V { get; set; } = new();
            }
            """;

    /// <summary>S10 with an OBJECT leaf: a dict whose value is a list of objects.</summary>
    private static string DictOfObjectList(string ns, string attr) =>
        $$"""
            using {{ns}};

            [{{attr}}]
            public partial class Doc
            {
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Row>> V { get; set; } = new();
                public int Plain { get; set; }
            }

            [{{attr}}]
            public partial class Row
            {
                public string Name { get; set; } = "";
            }
            """;

    /// <summary>§3 S11a: self recursion.</summary>
    private static string SelfRecursive(string ns, string attr) =>
        $$"""
            using {{ns}};

            [{{attr}}]
            public partial class Node
            {
                public string Name { get; set; } = "";
                public Node? Child { get; set; }
            }
            """;

    private static string DropMessage(
        (string[] Ids, string Generated, string[] Messages) run,
        string member
    ) => run.Messages.Single(m => m.Contains(member, StringComparison.Ordinal));

    /// <summary>
    /// ⛔ half of I-5 on a cell the FACTS settle: TOML has no null literal (spec §3 S5), so
    /// no generator change can ever make this member serializable. The message must say so,
    /// and must not offer a "not implemented yet" hope.
    /// </summary>
    [Test]
    public async Task Toml_null_element_reports_the_format_fact()
    {
        var drop = DropMessage(
            RunToml(NullableIntList("PicoToml", "PicoTomlSerializable")),
            "Doc.V"
        );

        await Assert
            .That(drop.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because($"a cell the format cannot express must be marked {Impossible}: {drop}");
        await Assert
            .That(drop)
            .Contains("cannot be represented")
            .Because("the reader has to see that the FORMAT is the limit, not this generator");
        await Assert.That(drop.Contains(Deferred, StringComparison.Ordinal)).IsFalse();
        await Assert.That(drop).Contains("toml").Because("I-5: the message names the format");
        await Assert.That(drop).Contains("List<int?>").Because("I-5: the message names the shape");
        await Assert.That(drop).Contains("Doc.V").Because("I-5: the message names the member");
    }

    /// <summary>
    /// ⏸ half of I-5 on the SAME shape in the format that CAN hold it: YAML has a null
    /// literal (spec §3 S5, written as a bare <c>- null</c>), so the drop is our debt
    /// (D-7b). Claiming "cannot be represented by this format" here is the concrete mislead
    /// D-4 ② names.
    /// </summary>
    [Test]
    public async Task Yaml_null_element_reports_our_debt()
    {
        var drop = DropMessage(
            RunYaml(NullableIntList("PicoYaml", "PicoYamlSerializable")),
            "Doc.V"
        );

        await Assert
            .That(drop.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because($"yaml CAN express a null element, so this is a debt {Deferred}: {drop}");
        await Assert
            .That(drop)
            .Contains("has not implemented it yet")
            .Because("the reader must be able to tell our gap from the format's limit");
        await Assert
            .That(drop.Contains(Impossible, StringComparison.Ordinal))
            .IsFalse()
            .Because("blaming the format for our own gap is exactly D-4 ②");
        await Assert
            .That(drop.Contains("cannot be represented", StringComparison.Ordinal))
            .IsFalse()
            .Because("yaml can represent it");
    }

    /// <summary>
    /// I-5's discriminating half: the SAME member in the SAME shape, dropped by two formats,
    /// must not produce the same sentence — otherwise one template satisfies both tests
    /// above merely by containing both phrases. This is the test the plan's Step 2 expects
    /// to be RED on the pre-Task-7 code (both formats rendered one message).
    /// </summary>
    [Test]
    public async Task Fact_and_debt_messages_are_distinguishable()
    {
        var toml = DropMessage(
            RunToml(NullableIntList("PicoToml", "PicoTomlSerializable")),
            "Doc.V"
        );
        var yaml = DropMessage(
            RunYaml(NullableIntList("PicoYaml", "PicoYamlSerializable")),
            "Doc.V"
        );

        await Assert.That(Impossible).IsNotEqualTo(Deferred);
        await Assert.That(toml).IsNotEqualTo(yaml).Because("two verdicts, two sentences");
        await Assert
            .That(toml.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because($"toml x S5 is a format fact: {toml}");
        await Assert
            .That(yaml.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because($"yaml x S5 is our debt: {yaml}");

        // Spec C4 (spec review D2): a debt message must CARRY the reopen pointer, not
        // only say "recorded gap" — otherwise the reader cannot find the condition.
        await Assert
            .That(yaml)
            .Contains("reopen:")
            .Because($"C4: the debt message has to point at its reopen condition: {yaml}");
        await Assert
            .That(toml.Contains("reopen:", StringComparison.Ordinal))
            .IsFalse()
            .Because("a format fact is terminal, so it must not promise a reopen");
    }

    /// <summary>
    /// The cell D-4 ② names by hand: S14 x yaml. YAML can hold a null object element
    /// (PyYAML reads <c>- null</c>; spec §3 footnote ¹) but the generator drops the whole
    /// member — a DEBT in yaml, a FACT in the two formats that carry no null at all.
    /// </summary>
    [Test]
    public async Task Object_null_element_is_a_debt_in_yaml_and_a_fact_in_toml()
    {
        var yaml = DropMessage(
            RunYaml(ObjectNullElementList("PicoYaml", "PicoYamlSerializable")),
            "Doc.V"
        );
        var toml = DropMessage(
            RunToml(ObjectNullElementList("PicoToml", "PicoTomlSerializable")),
            "Doc.V"
        );

        await Assert.That(yaml.StartsWith(Deferred, StringComparison.Ordinal)).IsTrue();
        await Assert.That(toml.StartsWith(Impossible, StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml).IsNotEqualTo(toml);
    }

    /// <summary>
    /// The other cell D-4 ② names: a dict whose value is a collection (S10 / D-3) is
    /// structurally impossible in INI (its sections hold no nesting). R2 Task 3 paid the
    /// debt in the other four formats, so the ⏸ half of this pair is gone by value — the ini
    /// half stays, and it must still say ⛔ with the format named (the message comes from the
    /// facts, not from "our emitter lacks a case").
    /// </summary>
    [Test]
    public async Task Dict_of_list_is_a_fact_in_ini()
    {
        var ini = DropMessage(RunIni(DictOfList("PicoIni", "PicoIniSerializable")), "Doc.V");

        await Assert
            .That(ini.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because($"INI cannot nest inside a member (S10), so this is a fact: {ini}");
        await Assert.That(ini).Contains("ini").Because("I-5: the message names the format");
    }

    /// <summary>
    /// The value-position debt that is still open after Task 3: a dict value whose sequence
    /// ends in an OBJECT. The FACTS say TOML can express a nested object list (S4), so this
    /// drop is our gap (⏸, "not implemented yet"), not a format limit — and the message has
    /// to say that much, otherwise the reader is told a lie about their format.
    /// </summary>
    [Test]
    public async Task Dict_of_object_list_is_a_deferred_gap_in_toml()
    {
        var toml = DropMessage(
            RunToml(DictOfObjectList("PicoToml", "PicoTomlSerializable")),
            "Doc.V"
        );

        await Assert
            .That(toml.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because($"the facts say TOML can hold it, so this is our gap: {toml}");
        await Assert.That(toml).Contains("toml").Because("I-5: the message names the format");
    }

    /// <summary>
    /// PICOSERDE003 gets the same treatment, and now carries the format and a way out:
    /// TOML and YAML can hold a cycle (spec §3 S11a — nested tables / nested mappings),
    /// INI cannot (it is flat). The old single sentence — "cannot be represented by this
    /// format", with no format named — told the TOML user to remove a cycle the format
    /// supports.
    /// </summary>
    [Test]
    public async Task Recursion_is_a_debt_in_toml_and_a_fact_in_ini()
    {
        var toml = DropMessage(
            RunToml(SelfRecursive("PicoToml", "PicoTomlSerializable")),
            "Node.Child"
        );
        var ini = DropMessage(
            RunIni(SelfRecursive("PicoIni", "PicoIniSerializable")),
            "Node.Child"
        );

        await Assert
            .That(toml.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because($"TOML can hold a recursive member (S11a): {toml}");
        await Assert.That(ini.StartsWith(Impossible, StringComparison.Ordinal)).IsTrue();
        await Assert
            .That(toml)
            .Contains("toml")
            .Because("W4 review item 2: PICOSERDE003 never named the format it was talking about");
        await Assert
            .That(toml)
            .Contains("cycle")
            .Because("the message has to say WHAT is recursive, not just that it is");
    }

    /// <summary>
    /// The cell session W5 left behind (review I-1): <c>List&lt;HashSet&lt;int&gt;&gt;</c> (spec §3 S8)
    /// is <b>❌ in INI</b> — the format has no nesting at all — but the "type cannot be
    /// resolved" channel had no decision point and hard-coded the ⏸ wording, so the message
    /// claimed "the ini format can express this shape". That is D-4 ② in the other
    /// direction: a format fact shipped as reopenable debt.
    /// <para>
    /// The verdict now asks the ELEMENT-position cell when (and only when) the declared
    /// type is a collection whose element is itself a collection, which is precisely S8.
    /// </para>
    /// </summary>
    [Test]
    public async Task Unresolvable_collection_of_collections_is_a_fact_in_ini()
    {
        const string source = """
            using PicoIni;

            [PicoIniSerializable]
            public partial class Doc
            {
                public System.Collections.Generic.List<System.Collections.Generic.HashSet<int>> V { get; set; } = new();
            }
            """;

        var drop = DropMessage(RunIni(source), "Doc.V");

        await Assert
            .That(drop.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because($"S8 x ini is ❌ in spec §3, so the message must be a format fact: {drop}");
        await Assert
            .That(drop.Contains(Deferred, StringComparison.Ordinal))
            .IsFalse()
            .Because("⏸ would tell the reader the format could hold it — the mirror of D-4 ②");
        await Assert.That(drop).Contains("ini").Because("I-5: the message names the format");
    }

    /// <summary>
    /// The NEGATIVE control for the test above, and the reason the fix is safe: S7
    /// (<c>HashSet&lt;int&gt;</c>) is ✅ in INI (spec §3 — a comma-separated scalar list), so its
    /// element-position cell must NOT be consulted. A heuristic keyed on "the declared type
    /// is a collection" would have flipped this cell to ⛔ and swapped one wrong message for
    /// another; keying on "the ELEMENT is itself a collection" leaves it alone.
    /// </summary>
    [Test]
    public async Task Unresolvable_collection_with_a_scalar_element_stays_a_debt_in_ini()
    {
        const string source = """
            using PicoIni;

            [PicoIniSerializable]
            public partial class Doc
            {
                public System.Collections.Generic.HashSet<int> V { get; set; } = new();
            }
            """;

        var drop = DropMessage(RunIni(source), "Doc.V");

        await Assert
            .That(drop.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because($"S7 x ini is ✅ in spec §3, so this is our debt, not the format's: {drop}");
        await Assert
            .That(drop.Contains(Impossible, StringComparison.Ordinal))
            .IsFalse()
            .Because("marking a ✅ cell ⛔ is the mislead this fix must not introduce");
    }

    /// <summary>
    /// The dict-key limit, proven from both sides (review I-3). A key kind this generator
    /// cannot emit is only a FORMAT fact where the format itself narrows its key set: INI
    /// reads a key as raw line text, so only <c>string</c> can be a key there ⇒ ⛔. Where the
    /// format's key set is the model's widest one, the missing kind is a case our emitters
    /// never wrote ⇒ ⏸ — the old code called both "cannot be represented", which is C1's
    /// exact prohibition (implementation progress written as a format fact).
    /// </summary>
    [Test]
    public async Task Dict_key_kind_is_a_fact_in_ini_and_a_debt_where_the_format_narrows_nothing()
    {
        const string nsIni = "PicoIni";
        const string nsYaml = "PicoYaml";

        static string KeyedBy(string ns, string attr) =>
            $$"""
                using {{ns}};

                public class Row
                {
                    public string Name { get; set; } = "";
                }

                [{{attr}}]
                public partial class Doc
                {
                    public System.Collections.Generic.Dictionary<Row, int> KeyedItems { get; set; } = new();
                }
                """;

        var ini = DropMessage(RunIni(KeyedBy(nsIni, "PicoIniSerializable")), "Doc.KeyedItems");
        var yaml = DropMessage(RunYaml(KeyedBy(nsYaml, "PicoYamlSerializable")), "Doc.KeyedItems");

        // The table itself must answer the way the messages do (no second copy of the fact).
        await Assert
            .That(FormatCapabilities.AllowedDictKeyKinds("ini").Count)
            .IsLessThan(FormatCapabilities.AllowedDictKeyKinds("yaml").Count)
            .Because("ini is the only format that narrows the key set — that is the fact");

        await Assert
            .That(ini.StartsWith(Impossible, StringComparison.Ordinal))
            .IsTrue()
            .Because($"INI narrowed its key set, so the drop is a format fact: {ini}");
        await Assert
            .That(yaml.StartsWith(Deferred, StringComparison.Ordinal))
            .IsTrue()
            .Because(
                $"yaml narrowed nothing: the missing key case is OUR emitter, not the format: {yaml}"
            );
        await Assert
            .That(yaml.Contains("cannot be represented", StringComparison.Ordinal))
            .IsFalse()
            .Because("C1: implementation progress must not be written as a format fact");
    }

    /// <summary>
    /// PICOSERDE001's side of the same question: a <c>HashSet&lt;int&gt;</c> member is dropped by
    /// four formats as a DEBT (spec §3 S7 is ✅ everywhere — D-1 is an implementation gap),
    /// so the old "cannot be resolved by this format" wording sent those users off to change
    /// a type every format can hold.
    /// </summary>
    [Test]
    public async Task Unresolvable_extended_collection_is_reported_as_a_debt()
    {
        // yaml/toml/msgpack paid this debt in R2 Task 2 (S7 is Supported there now); ini
        // has not (its S7 column belongs to Task 6), so ini is the remaining example of
        // "our debt, not a format limit".
        const string source = """
            using PicoIni;

            [PicoIniSerializable]
            public partial class Doc
            {
                public System.Collections.Generic.HashSet<int> V { get; set; } = new();
            }
            """;

        var drop = DropMessage(RunIni(source), "Doc.V");

        await Assert.That(drop.StartsWith(Deferred, StringComparison.Ordinal)).IsTrue();
        await Assert.That(drop).Contains("ini");
    }

    /// <summary>
    /// R2's literal reading (W4 review, counter-example 1): a set-only property is a member
    /// that never reaches the generated code and used to leave with a bare <c>continue</c>.
    /// It is the THIRD class (fix round 1, review I-2): no format can serialize a value it
    /// cannot read, so it is neither "the format cannot" (the declared type here is
    /// <c>int</c>, which every format holds) nor our debt (no generator change helps). The
    /// old assertion nailed the misleading sentence — "The json format cannot represent it …
    /// use a format that can" — which sends the reader to four dead ends.
    /// </summary>
    [Test]
    public async Task Write_only_property_reports_a_diagnostic()
    {
        const string source = """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public int WriteOnly { set { _ = value; } }
            }
            """;

        var warning = RunDiagnostics(new JsonSerializerGenerator(), source)
            .Single(d => d.GetMessage().Contains("Doc.WriteOnly", StringComparison.Ordinal));
        var message = warning.GetMessage();

        await Assert.That(warning.Id).IsEqualTo(UnresolvableTypeId);
        await Assert
            .That(message.StartsWith(MemberError, StringComparison.Ordinal))
            .IsTrue()
            .Because(
                $"I-2: a member nobody can read is its own class, not a format fact: {message}"
            );
        await Assert
            .That(message.Contains(Impossible, StringComparison.Ordinal))
            .IsFalse()
            .Because("⛔ is reserved for 'the format cannot express the shape'");
        await Assert
            .That(message.Contains(Deferred, StringComparison.Ordinal))
            .IsFalse()
            .Because("this is not our debt either: no generator change can serialize it");
        await Assert
            .That(message)
            .Contains("no getter")
            .Because("the message has to name the actual cause");
        await Assert
            .That(message.Contains("use a format that can", StringComparison.Ordinal))
            .IsFalse()
            .Because("I-2: no other format can help, so that advice is a dead end");
        await Assert
            .That(message.Contains("cannot represent", StringComparison.Ordinal))
            .IsFalse()
            .Because("claiming json cannot represent an int is simply false");
        await Assert
            .That(warning.Location.IsInSource)
            .IsTrue()
            .Because("I-1: every drop is navigable and pragma-suppressible");
    }

    /// The invariant behind the tests above, applied to every diagnostic the five generators
    /// produce for three dropped shapes: each drop message is greppable (starts with ⛔ or
    /// ⏸) and names the format. A new drop site that forgets this fails here rather than
    /// in a user's build log.
    /// </summary>
    [Test]
    public async Task Every_drop_message_carries_a_verdict_marker_and_the_format()
    {
        var formats = new (
            string Tag,
            string Ns,
            string Attr,
            Func<string, (string[] Ids, string Generated, string[] Messages)> Run
        )[]
        {
            ("json", "PicoJetson", "PicoJsonSerializable", SilentDropDiagnosticTests.RunJson),
            (
                "msgpack",
                "PicoMsgPack",
                "PicoMsgPackSerializable",
                SilentDropDiagnosticTests.RunMsgPack
            ),
            ("toml", "PicoToml", "PicoTomlSerializable", SilentDropDiagnosticTests.RunToml),
            ("yaml", "PicoYaml", "PicoYamlSerializable", SilentDropDiagnosticTests.RunYaml),
            ("ini", "PicoIni", "PicoIniSerializable", SilentDropDiagnosticTests.RunIni),
        };

        foreach (var (tag, ns, attr, run) in formats)
        {
            // Four shapes, because the set of DROPPED shapes differs per format: json drops
            // only the object-leaf dict value (its remaining S10 gap) and ini drops all four.
            // The union keeps the test non-vacuous for every format without pretending that
            // each one drops the same thing.
            var messages = run(NullableIntList(ns, attr))
                .Messages.Concat(run(DictOfList(ns, attr)).Messages)
                .Concat(run(DictOfObjectList(ns, attr)).Messages)
                .Concat(run(SelfRecursive(ns, attr)).Messages)
                .ToArray();

            await Assert
                .That(messages)
                .IsNotEmpty()
                .Because($"{tag} drops at least one of the four shapes, so it must report it");

            foreach (var message in messages)
            {
                var marked =
                    message.StartsWith(Impossible, StringComparison.Ordinal)
                    || message.StartsWith(Deferred, StringComparison.Ordinal);
                await Assert
                    .That(marked)
                    .IsTrue()
                    .Because(
                        $"every drop message needs a {Impossible}/{Deferred} verdict: {message}"
                    );
                await Assert
                    .That(message)
                    .Contains(tag)
                    .Because($"I-5: the message must name the format: {message}");
            }
        }
    }

    // ── drivers ────────────────────────────────────────────────────────────────────────
    // One shape for all five formats: (diagnostic ids, generated text, diagnostic
    // messages). The messages are here for Task 7, which must tell the ⛔ wording from
    // the debt wording without driving the generator itself.

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunIni(
        string source
    ) => Run(new IniSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunJson(
        string source
    ) => Run(new JsonSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunMsgPack(
        string source
    ) => Run(new MsgPackSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunYaml(
        string source
    ) => Run(new YamlSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunToml(
        string source
    ) => Run(new TomlSerializerGenerator(), source);

    private static (string[] DiagnosticIds, string Generated, string[] Messages) Run(
        IIncrementalGenerator generator,
        string source
    )
    {
        var result = RunGenerator(generator, source);
        var ids = result.Diagnostics.Select(d => d.Id).ToArray();
        var messages = result.Diagnostics.Select(d => d.GetMessage()).ToArray();
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (ids, generated, messages);
    }

    /// <summary>The same run, with the diagnostics intact: I-1 is about the Location.</summary>
    private static Diagnostic[] RunDiagnostics(IIncrementalGenerator generator, string source) =>
        RunGenerator(generator, source).Diagnostics.ToArray();

    /// <summary>The one place that builds a compilation and drives a generator.</summary>
    private static GeneratorDriverRunResult RunGenerator(
        IIncrementalGenerator generator,
        string source
    )
    {
        var compilation = CSharpCompilation.Create(
            "SilentDropDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static string AssemblyPath(System.Reflection.Assembly assembly) =>
        Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            var name = Path.GetFileName(path);
            if (
                name
                is "System.Private.CoreLib.dll"
                    or "System.Runtime.dll"
                    or "System.Collections.dll"
                    or "netstandard.dll"
                    or "System.Memory.dll"
            )
                yield return MetadataReference.CreateFromFile(path);
        }
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoIni.IniSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoJetson.JsonSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoMsgPack.MsgPackSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoYaml.YamlSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoToml.TomlSerializer).Assembly)
        );
        // [PicoSerializable(IncludeFields = true)] lives in PicoSerDe.Core: without this
        // reference the attribute binds to an error type, ForAttributeWithMetadataName
        // finds nothing, and the public-field extraction path is never reached.
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.PicoSerializableAttribute).Assembly)
        );
    }
}
