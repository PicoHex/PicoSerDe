using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// S7 (<c>HashSet&lt;int&gt;</c> as a member) and S8 (<c>List&lt;HashSet&lt;int&gt;&gt;</c>, the
/// extended collection in ELEMENT position) across the four formats that can express them.
/// <para>
/// These two shapes are the extended-collection kind family: seven kinds
/// (<c>HashSet/ISet/Queue/Stack/LinkedList/ImmutableArray/Memory/ReadOnlyMemory</c>) that
/// every format writes as a plain sequence. Before R2 Task 2 only json resolved the kind
/// at all (<c>ExtendedKindResolves</c>), and in ELEMENT position an unconditional kind list
/// in the extractor dropped the member in all five formats — so the wire was empty, not
/// wrong.
/// </para>
/// <para>
/// The tests are round-trips, not wire snapshots: set equality is the shape's own
/// equivalence relation (spec R1′), so member order is irrelevant.
/// </para>
/// </summary>
/// <summary>S7 — the extended collection IS the member.</summary>
public class RvSetMemberModel
{
    public string Name { get; set; } = "";
    public HashSet<int> SetMember { get; set; } = [];
}

/// <summary>S8 — the extended collection is the ELEMENT of a list member.</summary>
public class RvSetElementModel
{
    public string Name { get; set; } = "";
    public List<HashSet<int>> SetElement { get; set; } = [];
}

public class ExtendedKindCollectionTests
{
    /// <summary>The four formats whose element-position cell is a gap (S8 x ini is a fact).</summary>
    private static readonly string[] FourFormats = ["json", "msgpack", "toml", "yaml"];

    // ── S7: HashSet<int> as a member ──────────────────────────────────────────────────

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Extended_kind_member_round_trips(string format)
    {
        await AssertSetMemberRoundTrip(format, new HashSet<int>());
        await AssertSetMemberRoundTrip(format, new HashSet<int> { 7 });
        await AssertSetMemberRoundTrip(format, new HashSet<int> { -1, 0, 2147483647 });
    }

    // ── S8: List<HashSet<int>> (extended collection as an element) ───────────────────

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Extended_kind_element_round_trips(string format)
    {
        await AssertSetElementRoundTrip(format, []);
        await AssertSetElementRoundTrip(format, [new HashSet<int> { 7 }]);
        await AssertSetElementRoundTrip(
            format,
            [new HashSet<int> { 2147483647 }, new HashSet<int>(), new HashSet<int> { -1, 0 }]
        );
    }

    // ── the guard: a Supported cell must reach the generated code ─────────────────────

    /// <summary>
    /// The anti-false-green guard (spec review: "表翻转 ≠ 行为翻转").
    /// <para>
    /// The two sites that dropped these shapes used to decide by LITERAL — the extractor's
    /// <c>ek is "hashset" or …</c> list did not ask the table at all — so flipping the cell
    /// alone changed the model's answer and nothing else: the table and the probe would
    /// both say "supported" while the member stayed missing from the generated code. This
    /// test closes exactly that hole by reading the GENERATED SOURCE per format: if the
    /// table says the shape is supported, the member has to be in there.
    /// </para>
    /// <para>
    /// It is driven by the table, so it is a statement about the table: <b>flipping a cell
    /// without the emitter change turns it red</b> (measured in the Task 2 report, "table
    /// only" experiment). It is deliberately NOT driven by a hard-coded list of formats —
    /// that would pass no matter what the model said.
    /// </para>
    /// </summary>
    [Test]
    public async Task A_supported_cell_puts_the_member_in_the_generated_code()
    {
        (string Shape, Decision Cell, string Member, string Decl)[] probes =
        [
            (
                "S7",
                Decision.ExtendedKindResolves,
                "SetMember",
                "public System.Collections.Generic.HashSet<int> SetMember { get; set; } = new();"
            ),
            (
                "S8",
                Decision.ExtendedKindAsElement,
                "SetElement",
                "public System.Collections.Generic.List<System.Collections.Generic.HashSet<int>> SetElement { get; set; } = new();"
            ),
        ];

        var checkedPairs = new List<string>();
        var failures = new List<string>();

        foreach (var (shape, cell, member, decl) in probes)
        foreach (var (format, ns, attribute) in Formats.All)
        {
            // Either window counts as "the table promises this shape": the single cell it
            // asks (S7/S8 own one cell each) or the strictest effect on its path.
            var cellSays = FormatCapabilities.Decide(format, cell) == Effect.Supported;
            var pathSays = FormatCapabilities.DecideShape(format, shape) == Effect.Supported;
            if (!cellSays && !pathSays)
                continue;

            checkedPairs.Add($"{shape}x{format}");
            var generated = Formats.GeneratedSource(format, ns, attribute, decl);
            if (!generated.Contains(member, StringComparison.Ordinal))
                failures.Add(
                    $"{shape}x{format}: table says supported but {member} is not in the generated code"
                );
        }

        // Non-vacuous: at least the json/S7 pair is supported in every version of the table
        // this test has ever run against, so an empty loop is a bug in the probe, not a
        // capability statement.
        await Assert
            .That(checkedPairs)
            .IsNotEmpty()
            .Because("the guard checked no pair at all — a green guard that measures nothing");

        await Assert
            .That(failures)
            .IsEmpty()
            .Because(
                "the capability table and the generators disagree — the cell says the member "
                    + "is emitted and the generated source does not contain it: "
                    + string.Join(" | ", failures)
            );
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static async Task AssertSetMemberRoundTrip(string format, HashSet<int> value)
    {
        var model = new RvSetMemberModel { Name = "n", SetMember = value };
        var back = RoundTripSetMember(format, model);

        await Assert.That(back.Name).IsEqualTo("n").Because(format);
        await Assert
            .That(back.SetMember)
            .IsEquivalentTo(value)
            .Because($"{format}: S7 member HashSet<int> {Describe(value)}");
    }

    private static async Task AssertSetElementRoundTrip(string format, HashSet<int>[] value)
    {
        var model = new RvSetElementModel { Name = "n", SetElement = [.. value] };
        var back = RoundTripSetElement(format, model);

        await Assert.That(back.Name).IsEqualTo("n").Because(format);
        await Assert
            .That(back.SetElement.Count)
            .IsEqualTo(value.Length)
            .Because($"{format}: S8 element count for {Describe(value)}");
        for (var i = 0; i < value.Length; i++)
            await Assert
                .That(back.SetElement[i])
                .IsEquivalentTo(value[i])
                .Because($"{format}: S8 element #{i} of {Describe(value)}");
    }

    private static string Describe(IEnumerable<HashSet<int>> value) =>
        "[" + string.Join(", ", value.Select(s => "{" + string.Join(",", s) + "}")) + "]";

    private static string Describe(HashSet<int> value) => "{" + string.Join(",", value) + "}";

    // Both round-trip helpers are written out per model ON PURPOSE: the generators are
    // usage-driven, so a generic `RoundTrip<T>` call site only ever shows them `T` and no
    // serializer is registered at all. The concrete type has to appear at the call site.

    /// <summary>Round-trips the S7 model through one format.</summary>
    private static RvSetMemberModel RoundTripSetMember(string format, RvSetMemberModel model) =>
        format switch
        {
            "json" => JsonSerializer.Deserialize<RvSetMemberModel>(
                JsonSerializer.SerializeToUtf8Bytes(model)
            )!,
            "msgpack" => MsgPackSerializer.Deserialize<RvSetMemberModel>(
                MsgPackSerializer.SerializeToUtf8Bytes(model)
            )!,
            "toml" => TomlSerializer.Deserialize<RvSetMemberModel>(
                TomlSerializer.SerializeToUtf8Bytes(model)
            )!,
            _ => YamlSerializer.Deserialize<RvSetMemberModel>(
                YamlSerializer.SerializeToUtf8Bytes(model)
            )!,
        };

    /// <summary>Round-trips the S8 model through one format.</summary>
    private static RvSetElementModel RoundTripSetElement(string format, RvSetElementModel model) =>
        format switch
        {
            "json" => JsonSerializer.Deserialize<RvSetElementModel>(
                JsonSerializer.SerializeToUtf8Bytes(model)
            )!,
            "msgpack" => MsgPackSerializer.Deserialize<RvSetElementModel>(
                MsgPackSerializer.SerializeToUtf8Bytes(model)
            )!,
            "toml" => TomlSerializer.Deserialize<RvSetElementModel>(
                TomlSerializer.SerializeToUtf8Bytes(model)
            )!,
            _ => YamlSerializer.Deserialize<RvSetElementModel>(
                YamlSerializer.SerializeToUtf8Bytes(model)
            )!,
        };

    /// <summary>
    /// The format → (namespace, attribute, generator driver) table the guard needs: it has
    /// to compile a probe type per format and look at what that format's generator emitted.
    /// </summary>
    private static class Formats
    {
        internal static readonly (string Format, string Ns, string Attribute)[] All =
        [
            ("json", "PicoJetson", "PicoJsonSerializable"),
            ("msgpack", "PicoMsgPack", "PicoMsgPackSerializable"),
            ("toml", "PicoToml", "PicoTomlSerializable"),
            ("yaml", "PicoYaml", "PicoYamlSerializable"),
            ("ini", "PicoIni", "PicoIniSerializable"),
        ];

        internal static string GeneratedSource(
            string format,
            string ns,
            string attribute,
            string memberDecl
        )
        {
            var source = $$"""
                using {{ns}};

                [{{attribute}}]
                public partial class Doc
                {
                    {{memberDecl}}
                }
                """;

            var run = format switch
            {
                "json" => SilentDropDiagnosticTests.RunJson(source),
                "msgpack" => SilentDropDiagnosticTests.RunMsgPack(source),
                "toml" => SilentDropDiagnosticTests.RunToml(source),
                "yaml" => SilentDropDiagnosticTests.RunYaml(source),
                _ => SilentDropDiagnosticTests.RunIni(source),
            };
            return run.Generated;
        }
    }
}
