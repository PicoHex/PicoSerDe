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
/// I-2 at the SHAPE level: "an unsupported shape must not produce uncompilable generated
/// code" (spec §7). The build already proves the whole test assembly compiles, but that is
/// a mechanism-level proxy — a shape whose member is silently dropped trivially compiles,
/// and a shape that emits <c>new &lt;abstract type&gt;()</c> (R1 W3's four CS0144 errors)
/// only shows up when the assembly containing it is built. This file closes the gap the
/// W6 spec review and the T1 review both named: it runs each of the five generators over a
/// battery of recursion shapes and then <b>compiles the generated text</b> with Roslyn.
/// <para>
/// The judgement is deliberately two-sided, because "compiles" alone is a weak claim:
/// </para>
/// <list type="number">
///   <item><b>Supported ⇒ emitted.</b> A shape the table calls Supported must reach the
///         code — otherwise the check would be satisfied by dropping everything.</item>
///   <item><b>Not representable ⇒ dropped AND the assembly still compiles.</b> A shape the
///         emitter cannot close (a cycle through an abstract type in a format with no
///         polymorphic dispatch) must be dropped with a ⏸ debt diagnostic, never emitted
///         half-way.</item>
///   <item><b>The check can go RED.</b> <see cref="The_compile_check_sees_the_broken_emission_it_exists_to_prevent"/>
///         feeds the checker the exact emission a naive flip produces (<c>new AbsCycle()</c>)
///         and requires CS0144. Without that control, a compile helper that quietly returned
///         no diagnostics would look exactly like a passing I-2 test.</item>
/// </list>
/// </summary>
public class ShapeCompilationTests
{
    /// <summary>
    /// One recursion battery, in ONE compilation: the concrete self cycle (S11a, Supported
    /// in four formats), the concrete mutual cycle (S11b) and the abstract cycle (the shape
    /// that has no emit path outside json/msgpack). They share a compilation on purpose —
    /// I-2 is about the generated code of a whole unit, and the abstract member must not
    /// break its neighbours either.
    /// </summary>
    private const string RecursionBattery = """
        using PicoSerDe.Core;

        // S11a: a concrete self cycle. Value + Child, three constructible levels.
        [PicoSerializable]
        public partial class ConcCycle
        {
            public int Value { get; set; }
            public ConcCycle? Child { get; set; }
        }

        // S11b: a concrete mutual cycle (A -> B -> A).
        [PicoSerializable]
        public partial class MutualX
        {
            public string Name { get; set; } = "";
            public MutualY? Y { get; set; }
        }

        public partial class MutualY
        {
            public int N { get; set; }
            public MutualX? X { get; set; }
        }

        // The shape R1 W3 died on: a cycle whose target cannot be instantiated. json and
        // msgpack route it through the polymorphic helper; toml/yaml/ini have no
        // discriminator path, so emitting it would mean `new AbsCycle()`.
        [PicoSerializable]
        [PicoDerivedType(typeof(AbsLeaf), "leaf")]
        public abstract partial class AbsCycle
        {
            public int Value { get; set; }
            public AbsCycle? Next { get; set; }
        }

        public partial class AbsLeaf : AbsCycle
        {
            public string Extra { get; set; } = "";
        }
        """;

    /// <summary>Generated code that would exist if the abstract cycle were emitted.</summary>
    private const string BrokenEmission = """
        internal static class NaiveEmission
        {
            internal static AbsCycle Build() => new AbsCycle();
        }
        """;

    private static readonly (string Tag, IIncrementalGenerator Generator)[] Generators =
    [
        ("json", new JsonSerializerGenerator()),
        ("msgpack", new MsgPackSerializerGenerator()),
        ("toml", new TomlSerializerGenerator()),
        ("yaml", new YamlSerializerGenerator()),
        ("ini", new IniSerializerGenerator()),
    ];

    /// <summary>
    /// The formats that can close a cycle through a type that cannot be instantiated:
    /// json and msgpack emit the target's inner helper as a polymorphic dispatch. The
    /// question is asked through the model (<see cref="FormatCapabilities.CanConstructCycleTarget"/>)
    /// rather than restated here, so this fixture cannot silently disagree with the
    /// generator.
    /// </summary>
    private static bool ClosesPolymorphicCycles(string tag) =>
        FormatCapabilities.CanConstructCycleTarget(tag, targetIsInstantiable: false);

    [Test]
    public async Task Every_recursion_shape_generates_code_that_compiles()
    {
        foreach (var (tag, generator) in Generators)
        {
            var (errors, generated, _) = Generate(RecursionBattery, generator);

            await Assert
                .That(errors)
                .IsEmpty()
                .Because(
                    $"I-2: the {tag} generator emitted code that does not compile "
                        + $"({errors.Length} error(s)): {string.Join(" | ", errors.Take(5))}"
                );

            // Non-vacuous: each format must have emitted SOMETHING for the battery — a
            // generator that produced no source at all would make the compile check pass by
            // measuring nothing.
            await Assert.That(generated).Contains("ConcCycle").Because(tag);
        }
    }

    /// <summary>
    /// S11a (a concrete self cycle) is Supported everywhere except INI, so the member has to
    /// be there. This is the negative direction of the same invariant: a generator that
    /// keeps dropping the cycle cannot satisfy the test above by being quiet.
    /// </summary>
    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task A_concrete_self_cycle_is_emitted_where_the_table_says_supported(string tag)
    {
        var generator = Generators.Single(g => g.Tag == tag).Generator;
        var (errors, generated, _) = Generate(RecursionBattery, generator);

        await Assert
            .That(FormatCapabilities.DecideShapeTarget(tag, "S11a"))
            .IsEqualTo(Effect.Supported)
            .Because($"{tag} is expected to carry S11a in R2");

        await Assert.That(errors).IsEmpty().Because($"I-2: {string.Join(" | ", errors.Take(5))}");

        // The marker is the ACCESSOR, not the key literal: msgpack writes member keys as
        // integer indices (`mw.WriteInt32(0)`), so a key-shaped assertion would report the
        // member as missing in a format that emits it perfectly well.
        await Assert
            .That(generated)
            .Contains(".Child")
            .Because(
                $"S11a x {tag} is Supported, so ConcCycle.Child must reach the generated code "
                    + "(a compile check alone would pass on an empty serializer)"
            );
    }

    /// <summary>
    /// The abstract cycle: dropped where there is no polymorphic dispatch, emitted where
    /// there is — and in BOTH cases the emitted assembly must compile. The drop must be a
    /// ⏸ debt (the formats can express it; our emitters have no route), not a ⛔ format
    /// fact, or the reader is told a lie about their format (spec C4 / D-4 ②).
    /// </summary>
    [Test]
    public async Task An_abstract_cycle_target_is_dropped_where_it_cannot_be_constructed()
    {
        foreach (var (tag, generator) in Generators)
        {
            var (errors, generated, ids) = Generate(RecursionBattery, generator);

            await Assert
                .That(errors)
                .IsEmpty()
                .Because(
                    $"I-2: {tag} emitted uncompilable code: {string.Join(" | ", errors.Take(5))}"
                );

            // No path in any format may spell `new <abstract type>` — that IS R1 W3.
            await Assert
                .That(generated.Contains("new AbsCycle(", StringComparison.Ordinal))
                .IsFalse()
                .Because($"I-2: {tag} must never construct AbsCycle (CS0144)");

            var emitted = generated.Contains(".Next", StringComparison.Ordinal);

            if (ClosesPolymorphicCycles(tag))
            {
                await Assert
                    .That(emitted)
                    .IsTrue()
                    .Because($"{tag} closes the cycle through the polymorphic helper");
                continue;
            }

            await Assert
                .That(emitted)
                .IsFalse()
                .Because(
                    $"{tag} has no polymorphic cycle path, so AbsCycle.Next cannot be emitted"
                );

            await Assert
                .That(ids)
                .Contains("PICOSERDE003")
                .Because($"a dropped cycle is never silent (R2), and {tag} must still say so");

            var message = Messages(RecursionBattery, generator)
                .Single(m => m.Contains("Next", StringComparison.Ordinal));

            // Which verdict the drop deserves is the FACTS table's answer, not this file's:
            // ini cannot hold a cycle at all (⛔, spec §3), toml/yaml can and only lack the
            // polymorphic path (⏸, reopenable). Blaming the format for our own gap is D-4 ②.
            var marker = FormatCapabilities.CanRepresent(tag, "S11a")
                ? FormatCapabilities.DeferredMarker
                : FormatCapabilities.ImpossibleMarker;
            await Assert
                .That(message.StartsWith(marker, StringComparison.Ordinal))
                .IsTrue()
                .Because(
                    $"CanRepresent({tag}, S11a) decides the verdict, and the drop must say "
                        + $"exactly that: {message}"
                );
        }
    }

    /// <summary>
    /// The control that keeps the three tests above honest: the compile check must SEE the
    /// broken emission it exists to prevent. R1 W3's build died with four CS0144 errors on
    /// exactly this line shape, so a checker that reports nothing for it would report
    /// nothing for the real thing either.
    /// <para>
    /// This is also the answer to "how do you know a shape that cannot be emitted would be
    /// red?": the emission is synthesised here and required to fail, so the drop in
    /// <see cref="An_abstract_cycle_target_is_dropped_where_it_cannot_be_constructed"/> is
    /// demonstrably the thing standing between us and an uncompilable assembly.
    /// </para>
    /// </summary>
    [Test]
    public async Task The_compile_check_sees_the_broken_emission_it_exists_to_prevent()
    {
        var errors = Compile(RecursionBattery + "\n" + BrokenEmission);

        await Assert
            .That(errors.Any(e => e.Contains("CS0144", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "the negative control failed: a checker that does not report CS0144 for "
                    + "`new AbsCycle()` cannot certify I-2 for any shape. Errors seen: "
                    + string.Join(" | ", errors)
            );
    }

    // ── driver ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive one generator over <paramref name="source"/> and then COMPILE the model plus
    /// everything the generator emitted. Errors are returned as text (the assertion message
    /// is where a compile failure has to be readable).
    /// </summary>
    private static (string[] Errors, string Generated, string[] Ids) Generate(
        string source,
        IIncrementalGenerator generator
    )
    {
        var result = SilentDropDiagnosticTests.RunGenerator(generator, source);
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        var errors = Compile(source, result);
        return (errors, generated, result.Diagnostics.Select(d => d.Id).ToArray());
    }

    private static string[] Messages(string source, IIncrementalGenerator generator) =>
        SilentDropDiagnosticTests
            .RunGenerator(generator, source)
            .Diagnostics.Select(d => d.GetMessage())
            .ToArray();

    private static string[] Compile(string source) => Compile(source, generated: null);

    /// <summary>
    /// One compilation: the model source, optionally plus the trees a generator produced.
    /// The reference set is the one <see cref="SilentDropDiagnosticTests"/> already uses, so
    /// a new runtime assembly cannot be missing here only.
    /// </summary>
    private static string[] Compile(string source, GeneratorDriverRunResult? generated)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "ShapeCompilationAsm",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            SilentDropDiagnosticTests.PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        if (generated is not null)
        {
            var trees = generated
                .Results.SelectMany(r => r.GeneratedSources)
                .Select(g =>
                    CSharpSyntaxTree.ParseText(g.SourceText.ToString(), parseOptions, g.HintName)
                );
            compilation = compilation.AddSyntaxTrees(trees);
        }

        return compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
    }
}
