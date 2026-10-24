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

    private static string[] RunGenerator(
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
        return result.Diagnostics.Select(d => d.Id).ToArray();
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
