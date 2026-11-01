using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoIni.Gen;
using PicoJetson.Gen;
using PicoMsgPack.Gen;
using PicoToml.Gen;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The guard for "the generated code stands on its own" — the one <c>1e1945f</c> shipped without.
/// <para>
/// <b>What went wrong without it.</b> That commit pinned the culture of a comma-joined INI list by
/// building the per-element text through <c>value.V.Select(__s =&gt; …)</c>. The text was right and
/// the wire was right, but <c>Select</c> is an <b>extension</b> method: the file only compiled
/// because <see cref="System.Linq"/> was already in scope. In this repository it always is —
/// <c>Directory.Build.props</c> turns <c>ImplicitUsings</c> on for every project — so nothing here
/// could see the problem. A consumer who turns implicit usings OFF gets, inside code it cannot
/// edit, <c>error CS1061: 'List&lt;int&gt;' does not contain a definition for 'Select'</c>. The
/// generated header emits <c>System</c>, <c>System.Buffers</c>, <c>System.Text</c>,
/// <c>System.Runtime.CompilerServices</c>, <c>PicoSerDe.Core</c>, the format's own namespace and
/// the model's namespace — and never <c>System.Linq</c>, on purpose: what a consumer has in scope
/// is the consumer's decision, so <b>generated code may only use what it spells or imports itself</b>.
/// </para>
/// <para>
/// <b>The rule this file enforces</b>, in the two directions the bug has:
/// </para>
/// <list type="number">
///   <item><b>Text.</b> No file in <c>tests/FormatCapabilities.Probe/generated-baseline/</c> may
///         call a LINQ-only extension method unqualified unless that same file declares
///         <c>using System.Linq;</c> (and none does — the header never emits one). This is the
///         cheap, file:line-reporting half.</item>
///   <item><b>Compile.</b> All five generators are driven over a battery of collection members and
///         the emitted text is COMPILED against the full framework reference set but with <b>no
///         implicit usings</b> — which is exactly a consumer project with
///         <c>&lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt;</c>, not an approximation of one.
///         This half cannot be fooled by how the emission is spelled; it asks the compiler.</item>
/// </list>
/// <para>
/// <b>Both halves can go red, and both say so out loud.</b> See
/// <see cref="The_baseline_scan_reports_an_unqualified_Linq_call"/> and
/// <see cref="The_consumer_compile_reports_CS1061_for_the_emission_it_exists_to_prevent"/>: they
/// feed each check the exact regression (<c>1e1945f</c>'s bare <c>.Select(</c>) and require it to be
/// reported. A scanner that matched nothing and a compilation that reported nothing would otherwise
/// look identical to a pass.
/// </para>
/// </summary>
public class GeneratedCodeSelfSufficiencyGuardTests
{
    // ── 1. the text half: the baseline may not borrow a using it does not declare ──────

    /// <summary>The five baseline folders, spelled as <c>generated-baseline/</c> spells them.</summary>
    private static readonly string[] BaselineFormats =
    [
        "PicoJetson.Gen",
        "PicoIni.Gen",
        "PicoToml.Gen",
        "PicoYaml.Gen",
        "PicoMsgPack.Gen",
    ];

    /// <summary>
    /// A floor on the file count, so a moved or emptied directory cannot make every "no LINQ
    /// anywhere" claim below true for the wrong reason. The baseline holds 96 files today.
    /// </summary>
    private const int MinimumBaselineFiles = 90;

    /// <summary>
    /// Extension methods that exist <b>only</b> as extension methods, so their presence in a file
    /// that does not import them can only be a build error in a consumer with implicit usings off.
    /// <para>
    /// <c>.ToArray(</c> is deliberately NOT in this list: <c>List&lt;T&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, <c>ImmutableArray&lt;T&gt;</c> and <c>Memory&lt;T&gt;</c> all have an
    /// <b>instance</b> <c>ToArray()</c>, and TOML's baseline legitimately calls
    /// <c>r.TablePath.ToArray()</c>. The one receiver where <c>ToArray()</c> would be LINQ — an
    /// array — is covered by the compile half below, which sees the receiver's real type instead of
    /// a spelling.
    /// </para>
    /// </summary>
    private static readonly string[] LinqOnlyMethods =
    [
        ".Select(",
        ".SelectMany(",
        ".Where(",
        ".Any(",
        ".All(",
        ".ToList(",
        ".OfType(",
        ".Cast(",
        ".First(",
        ".FirstOrDefault(",
        ".Last(",
        ".LastOrDefault(",
        ".Single(",
        ".SingleOrDefault(",
        ".OrderBy(",
        ".OrderByDescending(",
        ".ThenBy(",
        ".GroupBy(",
        ".Distinct(",
        ".Concat(",
        ".Skip(",
        ".Take(",
        ".Sum(",
        ".Count(",
        ".ToDictionary(",
        ".ToHashSet(",
        ".ToLookup(",
        ".Aggregate(",
        ".Zip(",
    ];

    [Test]
    public async Task Generated_baseline_never_calls_a_Linq_method_it_does_not_import()
    {
        var files = BaselineFiles();

        // Non-vacuous: nothing below means anything if the scan was handed no files.
        await Assert
            .That(files.Length)
            .IsGreaterThanOrEqualTo(MinimumBaselineFiles)
            .Because(
                $"the generated baseline holds only {files.Length} *.g.cs file(s) — an empty or "
                    + "mislocated baseline would make every claim in this test true for the wrong reason"
            );

        var problems = new List<string>();
        foreach (var file in files)
        {
            foreach (var (line, needle) in UnimportedLinqCalls(File.ReadAllText(file)))
                problems.Add($"{Relative(file)}:{line} calls `{needle.TrimStart('.')}…`");
        }

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? $"none of the {files.Length} generated files calls a LINQ extension method it "
                        + $"does not import ({LinqOnlyMethods.Length} spellings checked)"
                    : "generated code may only use what it spells in full or imports itself, and the"
                        + " generated header never emits `using System.Linq;`. These call sites would"
                        + " be `error CS1061` inside generated code for any consumer with"
                        + " `<ImplicitUsings>disable</ImplicitUsings>`:\n  - "
                        + string.Join("\n  - ", problems)
                        + "\n  Fix the GENERATOR (spell it fully, e.g. `System.Linq.Enumerable.Select(…)`,"
                        + " — fully qualified it needs no using) and refresh the baseline in the"
                        + " same commit."
            );
    }

    /// <summary>
    /// The control that keeps the scan honest: fed the exact line <c>1e1945f</c> emitted, it must
    /// report a violation, and fed the fully-qualified replacement it must report none. A matcher
    /// that silently matched nothing would certify every generator, forever.
    /// </summary>
    [Test]
    public async Task The_baseline_scan_reports_an_unqualified_Linq_call()
    {
        const string Regression = """
            iw.WriteKeyValue("V"u8, string.Join(",", value.V.Select(__s => __s.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            """;
        const string Fixed = """
            iw.WriteKeyValue("V"u8, string.Join(",", System.Linq.Enumerable.Select(value.V, __s => __s.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            """;
        const string Qualified = """
            var __t = System.Linq.Enumerable.Select(value.V, __s => __s.ToString());
            """;

        await Assert
            .That(UnimportedLinqCalls(Regression))
            .IsNotEmpty()
            .Because("the scan must see the emission it exists to prevent, or it proves nothing");

        await Assert
            .That(UnimportedLinqCalls(Fixed))
            .IsEmpty()
            .Because(
                "the fully-qualified call needs no `using System.Linq;`, so it must not be "
                    + "reported — this is the shape the two INI baselines carry today"
            );

        await Assert
            .That(UnimportedLinqCalls(Qualified))
            .IsEmpty()
            .Because(
                "a FULLY qualified LINQ call is self-sufficient and must not be reported — the rule "
                    + "is about usings, not about System.Linq"
            );

        // ... and the escape hatch the rule itself names: a file that DOES import System.Linq may
        // call the extension. Without this, the "unless" in the rule would be untested prose.
        await Assert
            .That(UnimportedLinqCalls("using System.Linq;\n" + Regression))
            .IsEmpty()
            .Because("a file that imports System.Linq itself is self-sufficient");
    }

    /// <summary>
    /// The header the generators emit never carries <c>using System.Linq;</c> — that is WHY a bare
    /// extension call is always a violation above. If a generator ever starts emitting it, this
    /// test says so, so the "unless" branch cannot quietly start covering real regressions.
    /// </summary>
    [Test]
    public async Task No_generated_file_imports_System_Linq()
    {
        var offenders = BaselineFiles()
            .Where(f => File.ReadAllLines(f).Any(IsLinqUsing))
            .Select(Relative)
            .ToArray();

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                offenders.Length == 0
                    ? "no generated file imports System.Linq, so every LINQ call in generated code "
                        + "must be spelled in full"
                    : $"these generated files import System.Linq ({string.Join(", ", offenders)}): the"
                        + " generated header is consumer-independent by design and carries only"
                        + " System/System.Buffers/System.Text/System.Runtime.CompilerServices — if a"
                        + " generator truly needs LINQ in its output, add it to that header here and to"
                        + " the emitter, not only to the baseline."
            );
    }

    // ── 2. the compile half: a consumer that disables implicit usings ────────────────

    /// <summary>
    /// The members the regression lived behind, plus their neighbours — every element kind the INI
    /// comma-join formats specially, and every collection kind it reaches. Usings are explicit
    /// because the point is to measure the GENERATED files, not this model: a model that leans on
    /// implicit usings would fail for a reason that has nothing to do with the bug.
    /// <para>
    /// <c>HashSet&lt;int&gt;</c> is here on purpose, and it is what decided the shape of the fix:
    /// <c>HashSet&lt;T&gt;</c> has <b>no</b> instance <c>ToArray()</c> (only <c>List&lt;T&gt;</c>,
    /// <c>Queue&lt;T&gt;</c>, <c>Stack&lt;T&gt;</c>, <c>LinkedList&lt;T&gt;</c>, <c>ImmutableArray&lt;T&gt;</c> and
    /// <c>Memory&lt;T&gt;</c> do), so a <c>System.Array.ConvertAll(x.ToArray(), …)</c> emission would still
    /// need LINQ for this one element kind. <c>System.Linq.Enumerable.Select</c>, spelled in full,
    /// serves every kind with no using at all — and an array has no instance <c>ToArray()</c> either.
    /// </para>
    /// </summary>
    private const string ConsumerBattery = """
        using System;
        using System.Collections.Generic;
        using PicoSerDe.Core;

        public enum Shade
        {
            Zero = 0,
            Neg = -1,
        }

        [PicoSerializable]
        public class NoImplicitUsingsModel
        {
            public List<int> Ints { get; set; } = new List<int>();
            public List<long> Longs { get; set; } = new List<long>();
            public List<double> Reals { get; set; } = new List<double>();
            public List<decimal> Moneys { get; set; } = new List<decimal>();
            public List<bool> Flags { get; set; } = new List<bool>();
            public List<string> Names { get; set; } = new List<string>();
            public List<Shade> Shades { get; set; } = new List<Shade>();
            public HashSet<int> Set { get; set; } = new HashSet<int>();
            public int[] Array { get; set; } = new int[0];
            public Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>();

            // The STRING-KEY overload set on IniWriter is smaller than the span-key one, and a
            // dictionary value is the only INI write site that resolves against it. It has no
            // `ulong`/`uint`/`short`/`byte`/`float` arm, so those value kinds reach it through
            // IMPLICIT CONVERSION — i.e. through overload ranking, where `ulong` is ambiguous
            // between `double` and `decimal`:
            //   error CS0121: The call is ambiguous between
            //   'IniWriter.WriteKeyValue(string, double)' and '(string, decimal)'
            // `c5cf9b2` shipped exactly that, as a hard compile error in generated code; nothing
            // in the repository saw it because no model here carried a `Dictionary<string,ulong>`.
            // These three rows are that gap closed. (`Dictionary<string,double|decimal|bool|
            // DateTime|Guid|…>` are NOT here: their INI READ path is a separate, PRE-EXISTING
            // CS0029 defect on `b4376bf` too, and this battery would only ledger it.)
            public Dictionary<string, ulong> Big { get; set; } = new Dictionary<string, ulong>();
            public Dictionary<string, uint> Medium { get; set; } = new Dictionary<string, uint>();
            public Dictionary<string, long> Lons { get; set; } = new Dictionary<string, long>();
            public uint Count { get; set; }
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
    /// The real proof, one compilation per format: the model plus everything that format's
    /// generator emitted, compiled with <b>no implicit usings</b>. A bare <c>.Select(</c> in the
    /// emitted text is <c>CS1061</c> here for the same reason it is in a consumer project —
    /// because <c>using System.Linq;</c> is simply not in scope.
    /// <para>
    /// Collects every format's violation before failing, so one run tells the whole story instead
    /// of the first red format only.
    /// </para>
    /// </summary>
    [Test]
    public async Task Generated_code_compiles_where_implicit_usings_are_disabled()
    {
        var problems = new List<string>();
        var stale = new List<string>();

        foreach (var (tag, generator) in Generators)
        {
            var (errors, generated) = GenerateAndCompile(ConsumerBattery, generator);

            // Non-vacuous: a generator that emitted nothing would make the compile pass by
            // measuring nothing.
            await Assert
                .That(generated)
                .Contains("NoImplicitUsingsModel")
                .Because(
                    $"{tag} emitted no serializer for the battery, so the compile below proves nothing"
                );

            var ledgerRows = KnownUncompilable.Where(row => row.Format == tag).ToArray();
            if (ledgerRows.Length == 0)
            {
                if (errors.Length > 0)
                    problems.Add($"{tag} ({errors.Length}): {string.Join(" | ", errors.Take(4))}");
                continue;
            }

            // A ledged format may only fail with the failures the ledger names — a CS1061 here is
            // still a violation, which is the whole point of spelling the codes out.
            var unexpected = errors
                .Where(e =>
                    !ledgerRows.Any(row =>
                        row.Codes.Any(code => e.Contains("error " + code, StringComparison.Ordinal))
                    )
                )
                .ToArray();
            if (unexpected.Length > 0)
                problems.Add(
                    $"{tag} ({unexpected.Length}, NOT ledged): {string.Join(" | ", unexpected.Take(4))}"
                );

            if (errors.Length == 0)
                stale.Add(tag);
        }

        await Assert
            .That(stale)
            .IsEmpty()
            .Because(
                stale.Count == 0
                    ? "every `known uncompilable` row still reports its errors"
                    : $"the ledger row(s) for {string.Join(", ", stale)} are STALE: the format now"
                        + " compiles cleanly for this battery, so remove the row from"
                        + " KnownUncompilable in this file (a ledger that outlives its defect hides the"
                        + " next one)"
            );

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? $"all {Generators.Length} generators emit code that compiles for a consumer with"
                        + " `<ImplicitUsings>disable</ImplicitUsings>` (or fail only where"
                        + " KnownUncompilable says they already did)"
                    : "generated code must compile for a consumer with"
                        + " `<ImplicitUsings>disable</ImplicitUsings>`: it may only use what it spells in"
                        + " full or imports itself. A missing-using error (CS1061/CS0103/CS0246/CS1501/"
                        + "CS0411/CS1929) belongs to the GENERATOR, not to the consumer:\n  - "
                        + string.Join("\n  - ", problems)
            );
    }

    /// <summary>
    /// Known, <b>pre-existing</b> defects that make ONE format's emission uncompilable for reasons
    /// that are not this guard's subject — recorded with the exact codes, not muted.
    /// <para>
    /// <b>TOML, the list READ path.</b> For a <c>List&lt;decimal&gt;</c> or <c>List&lt;enum&gt;</c> member the
    /// emitted deserializer adds a <c>string</c> to the typed list:
    /// <c>__tmpList.Add(Encoding.UTF8.GetString(r.ValueSpan));</c> on a
    /// <c>List&lt;decimal&gt;</c> ⇒ <c>CS1503: cannot convert from 'string' to 'decimal'</c>. Reproduced on
    /// <c>b4376bf</c> with a throwaway consumer outside the repository, so it is not this branch's
    /// doing and not a using problem: the argument's TYPE is wrong, and no <c>using</c> can fix a type.
    /// It belongs to a reader fix of its own (this branch may not touch readers), so it is ledged
    /// here — and <see cref="Generated_code_compiles_where_implicit_usings_are_disabled"/> fails if
    /// it ever becomes a missing-using error, or if the row outlives the defect.
    /// </para>
    /// </summary>
    private static readonly (string Format, string[] Codes)[] KnownUncompilable =
    [
        ("toml", ["CS1503"]),
    ];

    /// <summary>
    /// The control for the compile half: the same compilation, plus the one line <c>1e1945f</c>
    /// emitted, must report <c>CS1061</c> — the error a consumer actually got. Without this, a
    /// reference set that happened to make the real regression compile (for instance one that
    /// injected <c>global using System.Linq;</c>) would turn the proof above into a no-op.
    /// </summary>
    [Test]
    public async Task The_consumer_compile_reports_CS1061_for_the_emission_it_exists_to_prevent()
    {
        const string Regression = """
            internal static class NaiveJoin
            {
                internal static string Build(List<int> v) =>
                    string.Join(",", v.Select(__s => __s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            """;

        var errors = Compile(ConsumerBattery + "\n" + Regression, generated: null);

        await Assert
            .That(errors.Any(e => e.Contains("CS1061", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "the negative control failed: a compilation that does not report CS1061 for a bare"
                    + " `.Select(` cannot certify anything about implicit usings. Errors seen: "
                    + string.Join(" | ", errors)
            );

        // The other half of the control: the SHELL that replaced it compiles in the same setup.
        const string Replacement = """
            internal static class NaiveJoin
            {
                internal static string Build(List<int> v) =>
                    string.Join(",", System.Linq.Enumerable.Select(v, __s => __s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            """;

        var replacementErrors = Compile(ConsumerBattery + "\n" + Replacement, generated: null);
        await Assert
            .That(replacementErrors)
            .IsEmpty()
            .Because(
                "The fully-qualified Select needs no using, so it must compile here — otherwise the fix "
                    + "for the control would be untested: "
                    + string.Join(" | ", replacementErrors)
            );
    }

    // ── the scan: which calls in this text are unqualified LINQ? ─────────────────────

    /// <summary>
    /// Every LINQ-only call in <paramref name="text"/> that is neither fully qualified
    /// (<c>System.Linq.Enumerable.</c>) nor covered by a <c>using System.Linq;</c> in the same text,
    /// as <c>(line, needle)</c>.
    /// <para>
    /// Line comments are stripped before matching, so a comment that QUOTES an emission (this
    /// repository's comments do) cannot turn the scan red. String literals are not parsed — a
    /// generated literal holding the text <c>.Select(</c> is not a construct any emitter produces,
    /// and the compile half would not be fooled by it anyway.
    /// </para>
    /// </summary>
    private static List<(int Line, string Needle)> UnimportedLinqCalls(string text)
    {
        var found = new List<(int, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Any(IsLinqUsing))
            return found;

        for (var i = 0; i < lines.Length; i++)
        {
            var code = StripLineComment(lines[i]);
            foreach (var needle in LinqOnlyMethods)
            {
                var at = code.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0)
                    continue;

                // `System.Linq.Enumerable.Select(` is spelled in full: it needs no using.
                if (at >= "System.Linq.Enumerable".Length)
                {
                    var prefix = code.Substring(
                        at - "System.Linq.Enumerable".Length,
                        "System.Linq.Enumerable".Length
                    );
                    if (prefix.EndsWith("System.Linq.Enumerable", StringComparison.Ordinal))
                        continue;
                }

                found.Add((i + 1, needle));
                break;
            }
        }

        return found;
    }

    /// <summary>Is this line the <c>using System.Linq;</c> the rule talks about?</summary>
    private static bool IsLinqUsing(string line) =>
        line.Trim() is "using System.Linq;" or "global using System.Linq;";

    /// <summary>
    /// The line with its <c>//</c> comment removed. String literals are not understood, so a
    /// <c>//</c> inside a generated literal would truncate the line — harmless here, because
    /// truncating a line can only ever HIDE a match, and every match this guard needs to find is
    /// in code, not in a comment.
    /// </summary>
    private static string StripLineComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static string[] BaselineFiles()
    {
        var root = Path.Combine(
            TestRepo.Root(),
            "tests",
            "FormatCapabilities.Probe",
            "generated-baseline"
        );
        return BaselineFormats
            .Select(format => Path.Combine(root, format))
            .Where(Directory.Exists)
            .SelectMany(directory =>
                Directory.GetFiles(directory, "*.g.cs", SearchOption.AllDirectories)
            )
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepo.Root(), file).Replace('\\', '/');

    // ── the compile: a consumer with implicit usings off ────────────────────────────

    private static (string[] Errors, string Generated) GenerateAndCompile(
        string source,
        IIncrementalGenerator generator
    )
    {
        var result = SilentDropDiagnosticTests.RunGenerator(generator, source);
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (Compile(source, result), generated);
    }

    /// <summary>
    /// One compilation of <paramref name="source"/> plus, when given, every tree a generator
    /// emitted. Returns errors as text: the assertion message is where a compile failure has to be
    /// readable.
    /// </summary>
    private static string[] Compile(string source, GeneratorDriverRunResult? generated)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "SelfSufficiencyAsm",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            ConsumerReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable
            )
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

    /// <summary>
    /// The <b>whole</b> shared framework — deliberately wider than
    /// <see cref="AllFormatReferences.References"/> (and than
    /// <see cref="PicoSerDe.TestSupport.RoslynProbe.PlatformReferences"/>): <c>System.Linq.dll</c> has to be
    /// referenced for this check to be about <b>usings</b>. With the extension methods absent from
    /// the reference set, a bare <c>.Select(</c> would fail here for the wrong reason and the guard
    /// would be measuring the reference list instead of self-sufficiency. Nothing adds
    /// <c>global using System.Linq;</c>, so what is left is exactly a consumer project with
    /// <c>&lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt;</c>.
    /// </summary>
    private static MetadataReference[] ConsumerReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var paths = new List<string>(tpa.Split(Path.PathSeparator));

        // The five formats, their core, and the generators' own assemblies: the emitted code names
        // `PicoSerDe.Core`, `PicoIni`, … and `[PicoSerializable]` has to bind for the generator
        // (driven in-proc) to find the model at all.
        foreach (
            var assembly in new[]
            {
                typeof(PicoIni.IniSerializer).Assembly,
                typeof(PicoJetson.JsonSerializer).Assembly,
                typeof(PicoMsgPack.MsgPackSerializer).Assembly,
                typeof(PicoYaml.YamlSerializer).Assembly,
                typeof(PicoToml.TomlSerializer).Assembly,
                typeof(PicoSerDe.Core.PicoSerializableAttribute).Assembly,
            }
        )
        {
            var path = Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");
            if (File.Exists(path))
                paths.Add(path);
        }

        // A duplicate metadata reference is a CS1704-class hazard, and the load path can legitimately
        // list the same file twice — so dedupe by PATH, not by reference identity.
        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
