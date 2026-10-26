using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoIni.Gen;
using PicoJetson.Gen;
using PicoMsgPack.Gen;
using PicoToml.Gen;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Review Task 2 (Critical 1 + Important 2): an anonymous-type collection member
/// can only be emitted by the JSON writer — <c>MsgPackWriter</c> only exposes
/// <c>WriteStartArray(int count)</c> (a parameterless call is CS7036), TOML/INI
/// embed the key in every scalar call and YAML writes per-item sequence entries.
/// Dropping such a member must be <em>loud</em> (PICOSERDE005) instead of
/// silently losing data, and the MsgPack path must never emit non-compiling
/// code (that half is asserted by <see cref="AnonNullableTests"/> compiling and
/// running a MsgPack anonymous collection fixture).
/// </summary>
public class AnonCollectionDiagnosticTests
{
    private const string Id = "PICOSERDE005";

    [Test]
    public async Task Toml_AnonCollectionMember_ReportsDiagnostic()
    {
        const string source = """
            using PicoToml;

            public static class Probe
            {
                public static string Run() =>
                    TomlSerializer.Serialize(
                        new { Name = "x", Xs = new System.Collections.Generic.List<int> { 1, 2 } }
                    );
            }
            """;

        var ids = RunGenerator(
            new TomlSerializerGenerator(),
            source,
            typeof(TomlSerializer).Assembly
        );
        await Assert.That(ids).Contains(Id);
    }

    [Test]
    public async Task Yaml_AnonCollectionMember_ReportsDiagnostic()
    {
        const string source = """
            using PicoYaml;

            public static class Probe
            {
                public static string Run() =>
                    YamlSerializer.Serialize(
                        new { Name = "x", Xs = new System.Collections.Generic.List<int> { 1, 2 } }
                    );
            }
            """;

        var ids = RunGenerator(
            new YamlSerializerGenerator(),
            source,
            typeof(YamlSerializer).Assembly
        );
        await Assert.That(ids).Contains(Id);
    }

    [Test]
    public async Task Ini_AnonCollectionMember_ReportsDiagnostic()
    {
        const string source = """
            using PicoIni;

            public static class Probe
            {
                public static string Run() =>
                    IniSerializer.Serialize(
                        new { Name = "x", Xs = new System.Collections.Generic.List<int> { 1, 2 } }
                    );
            }
            """;

        var ids = RunGenerator(
            new IniSerializerGenerator(),
            source,
            typeof(IniSerializer).Assembly
        );
        await Assert.That(ids).Contains(Id);
    }

    [Test]
    public async Task MsgPack_AnonCollectionMember_ReportsDiagnostic()
    {
        const string source = """
            using PicoMsgPack;

            public static class Probe
            {
                public static byte[] Run() =>
                    MsgPackSerializer.SerializeToUtf8Bytes(
                        new { Name = "x", Xs = new System.Collections.Generic.List<int> { 1, 2 } }
                    );
            }
            """;

        var ids = RunGenerator(
            new MsgPackSerializerGenerator(),
            source,
            typeof(MsgPackSerializer).Assembly
        );
        await Assert.That(ids).Contains(Id);
    }

    [Test]
    public async Task Json_AnonCollectionMember_NoDiagnostic()
    {
        // JSON does support anonymous collections (WriteStartArray() has no count
        // and the key lives outside the value) — the diagnostic must stay silent.
        const string source = """
            using PicoJetson;

            public static class Probe
            {
                public static string Run() =>
                    PicoJetson.JsonSerializer.Serialize(
                        new { Name = "x", Xs = new System.Collections.Generic.List<int> { 1, 2 } }
                    );
            }
            """;

        var ids = RunGenerator(
            new JsonSerializerGenerator(),
            source,
            typeof(PicoJetson.JsonSerializer).Assembly
        );
        await Assert.That(ids).DoesNotContain(Id);
    }

    [Test]
    public async Task Toml_AnonScalarOnly_NoDiagnostic()
    {
        // The diagnostic is specific to collection members, not to anonymous
        // types in general (those are supported by every format).
        const string source = """
            using PicoToml;

            public static class Probe
            {
                public static string Run() => TomlSerializer.Serialize(new { A = 1, B = "b" });
            }
            """;

        var ids = RunGenerator(
            new TomlSerializerGenerator(),
            source,
            typeof(TomlSerializer).Assembly
        );
        await Assert.That(ids).DoesNotContain(Id);
    }

    /// <summary>
    /// Spec D-10: the fourth drop surface. Until R2 this diagnostic was the only one that
    /// broke BOTH halves of the drop contract at once — no ⛔/⏸/⚠ verdict glyph (so it
    /// could not be told apart from a format fact, and `grep ⏸` missed it) and
    /// `location: null` (so it was neither navigable in an IDE nor suppressible per site
    /// with a `#pragma`). Its wording was wrong in a third way: "cannot be serialized by
    /// {format}" states OUR missing emitter as a fact about the format (spec C1).
    /// <para>
    /// All three are pinned here, per format, because the verdict must be derived from the
    /// capability model rather than written into the message: a collection is something
    /// every one of the five formats can hold (spec §3 S1), so the honest verdict is ⏸
    /// "not implemented in this generator yet", with the `reopen:` pointer C4 asks for.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("toml", typeof(TomlSerializer))]
    [Arguments("yaml", typeof(YamlSerializer))]
    [Arguments("ini", typeof(IniSerializer))]
    [Arguments("msgpack", typeof(MsgPackSerializer))]
    public async Task AnonCollectionDrop_CarriesTheDeferredVerdict_ReopenPointer_AndTheMemberLocation(
        string tag,
        Type formatAssembly
    )
    {
        // The anonymous type is created AT THE CALL SITE: the anonymous pipeline hooks
        // serializer invocations, so an anonymous type built in a property initializer is
        // never seen (a fixture shaped that way would prove nothing).
        var source = tag switch
        {
            "toml" => """
                using PicoToml;

                public static class Probe
                {
                    public static string Run() =>
                        TomlSerializer.Serialize(
                            new
                            {
                                Name = "x",
                                Xs = new System.Collections.Generic.List<int> { 1, 2 },
                            }
                        );
                }
                """,
            "yaml" => """
                using PicoYaml;

                public static class Probe
                {
                    public static string Run() =>
                        YamlSerializer.Serialize(
                            new
                            {
                                Name = "x",
                                Xs = new System.Collections.Generic.List<int> { 1, 2 },
                            }
                        );
                }
                """,
            "ini" => """
                using PicoIni;

                public static class Probe
                {
                    public static string Run() =>
                        IniSerializer.Serialize(
                            new
                            {
                                Name = "x",
                                Xs = new System.Collections.Generic.List<int> { 1, 2 },
                            }
                        );
                }
                """,
            _ => """
                using PicoMsgPack;

                public static class Probe
                {
                    public static byte[] Run() =>
                        MsgPackSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                Name = "x",
                                Xs = new System.Collections.Generic.List<int> { 1, 2 },
                            }
                        );
                }
                """,
        };

        var generator =
            tag == "toml" ? (IIncrementalGenerator)new TomlSerializerGenerator()
            : tag == "yaml" ? new YamlSerializerGenerator()
            : tag == "ini" ? new IniSerializerGenerator()
            : new MsgPackSerializerGenerator();

        var diagnostics = RunDiagnostics(generator, source, formatAssembly.Assembly);
        var drop = diagnostics.Single(d => d.Id == Id);
        var message = drop.GetMessage();

        // ⏸, not ⛔: the format can hold a collection; this generator's anonymous-type
        // emitter is the missing half.
        await Assert
            .That(message.StartsWith("⏸", StringComparison.Ordinal))
            .IsTrue()
            .Because($"D-10: the drop needs the DEBT marker, not a format fact: {message}");
        await Assert
            .That(message.Contains("⛔", StringComparison.Ordinal))
            .IsFalse()
            .Because("⛔ is reserved for 'the format cannot express it' (spec C1)");
        await Assert
            .That(message)
            .Contains("reopen:")
            .Because($"C4: a ⏸ message must say where the condition is tracked: {message}");
        await Assert
            .That(message.Contains("cannot be serialized by", StringComparison.Ordinal))
            .IsFalse()
            .Because(
                "D-10: OUR missing emitter must not be stated as a fact about the format: "
                    + message
            );
        await Assert
            .That(message)
            .Contains(tag)
            .Because($"I-5: the message must name the format: {message}");
        await Assert
            .That(message)
            .Contains("Xs")
            .Because($"I-5: the message must name the member: {message}");

        // The location is the MEMBER's own declaration site (navigable, pragma-suppressible),
        // not Location.None and not the enclosing call.
        await Assert.That(drop.Location).IsNotNull();
        await Assert
            .That(drop.Location.IsInSource)
            .IsTrue()
            .Because("I-1: every drop is navigable and pragma-suppressible");
        var span = drop.Location.GetLineSpan();
        await Assert
            .That(
                SourceLineAt(source, span.StartLinePosition.Line)
                    .Substring(span.StartLinePosition.Character)
            )
            .StartsWith("Xs")
            .Because(
                "D-10: the location must start at the member name, not at the anonymous "
                    + $"initializer or the call; got line {span.StartLinePosition.Line}, "
                    + $"column {span.StartLinePosition.Character}"
            );
    }

    // ── D-8: the sixth emit surface answers like the other five ─────────────────────

    /// <summary>
    /// Spec D-8: the anonymous-type drop is the SIXTH emit surface, and it used to decide for
    /// itself — <c>fmtTag is "json"</c>, a format literal inside a drop decision, i.e. the one
    /// shape invariant I-3 bans. The decision now comes from
    /// <see cref="PicoSerDe.Gen.FormatCapabilities.AnonCollectionSupported"/> (whose table is
    /// <c>DecisionTables.AnonCollectionFormats</c>), so this test pins the two directions of
    /// that convergence for every format: the model says "supported" exactly when the emitter
    /// emits no drop. A re-added literal, or a table row that nobody reads, breaks one of the
    /// five cases.
    /// </summary>
    [Test]
    public async Task TheDropDecisionIsTheCapabilityModelAndNotAFormatLiteral()
    {
        var cases = new (
            string Tag,
            Func<IIncrementalGenerator> Create,
            System.Reflection.Assembly FormatAssembly,
            string Sender,
            string ReturnType
        )[]
        {
            (
                "json",
                () => new JsonSerializerGenerator(),
                typeof(JsonSerializer).Assembly,
                "PicoJetson.JsonSerializer.Serialize",
                "string"
            ),
            (
                "msgpack",
                () => new MsgPackSerializerGenerator(),
                typeof(MsgPackSerializer).Assembly,
                "PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes",
                "byte[]"
            ),
            (
                "toml",
                () => new TomlSerializerGenerator(),
                typeof(TomlSerializer).Assembly,
                "PicoToml.TomlSerializer.Serialize",
                "string"
            ),
            (
                "yaml",
                () => new YamlSerializerGenerator(),
                typeof(YamlSerializer).Assembly,
                "PicoYaml.YamlSerializer.Serialize",
                "string"
            ),
            (
                "ini",
                () => new IniSerializerGenerator(),
                typeof(IniSerializer).Assembly,
                "PicoIni.IniSerializer.Serialize",
                "string"
            ),
        };

        foreach (var (tag, create, formatAssembly, sender, returnType) in cases)
        {
            var ids = RunGenerator(create(), CallSiteSource(sender, returnType), formatAssembly);

            var dropped = ids.Contains(Id);
            var expected = !PicoSerDe.Gen.FormatCapabilities.AnonCollectionSupported(tag);

            await Assert
                .That(dropped)
                .IsEqualTo(expected)
                .Because(
                    $"D-8/I-3: whether the {tag} anonymous-type emitter drops a collection "
                        + "member must BE the capability model's answer "
                        + $"(AnonCollectionSupported('{tag}') = "
                        + $"{PicoSerDe.Gen.FormatCapabilities.AnonCollectionSupported(tag)}). "
                        + "A mismatch means a format literal or a second table decided it."
                );
        }
    }

    /// <summary>
    /// The VERDICT half of D-8: the anonymous drop is our gap in every format, because every
    /// format can hold the shape being refused (a flat scalar collection — spec §3 S1 is ✅ in
    /// all five). Pinned as an invariant rather than as prose in a template: if a fact ever
    /// says otherwise the ⛔ wording must take over, and this test says so first.
    /// </summary>
    [Test]
    public async Task TheAnonymousDropIsOurGapInEveryFormatBecauseTheShapeIsRepresentable()
    {
        foreach (var tag in PicoSerDe.Gen.FormatCapabilities.AllFormatTags)
        {
            await Assert
                .That(PicoSerDe.Gen.FormatCapabilities.AnonCollectionIsFormatFact(tag))
                .IsFalse()
                .Because(
                    $"{tag} can hold a flat scalar collection (spec §3 S1), so refusing one "
                        + "inside an anonymous type is OUR emitter gap (⏸), never a format "
                        + "fact (⛔) — spec C1"
                );

            await Assert
                .That(PicoSerDe.Gen.FormatCapabilities.CanRepresent(tag, "S1"))
                .IsTrue()
                .Because(
                    $"{tag} must still be able to represent S1: AnonCollectionIsFormatFact "
                        + "is derived from that cell"
                );
        }
    }

    /// <summary>
    /// An anonymous-type collection member at a CALL SITE (the pipeline hooks serializer
    /// invocations, so the shape has to be there), for the format whose serializer the
    /// parameters name.
    /// </summary>
    private static string CallSiteSource(string sender, string returnType) =>
        $$"""
            public static class AnonProbe
            {
                public static {{returnType}} Run() =>
                    {{sender}}(
                        new
                        {
                            Name = "x",
                            Xs = new System.Collections.Generic.List<int> { 1, 2 },
                        }
                    );
            }
            """;

    /// <summary>The fixture source line a span reports, read through Roslyn's own text.
    /// </summary>
    private static string SourceLineAt(string source, int line)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source);
        return text.ToString(text.Lines[line].Span);
    }

    private static string[] RunGenerator(
        IIncrementalGenerator generator,
        string source,
        System.Reflection.Assembly formatAssembly
    ) => RunDiagnostics(generator, source, formatAssembly).Select(d => d.Id).ToArray();

    private static Microsoft.CodeAnalysis.Diagnostic[] RunDiagnostics(
        IIncrementalGenerator generator,
        string source,
        System.Reflection.Assembly formatAssembly
    )
    {
        var compilation = CSharpCompilation.Create(
            "AnonCollectionDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(formatAssembly),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(generator);
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.Diagnostics.ToArray();
    }

    private static string AssemblyPath(System.Reflection.Assembly assembly) =>
        Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");

    private static IEnumerable<MetadataReference> PlatformReferences(
        System.Reflection.Assembly formatAssembly
    )
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
        yield return MetadataReference.CreateFromFile(AssemblyPath(formatAssembly));
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }
}
