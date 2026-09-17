using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoToml;
using PicoToml.Gen;

namespace PicoToml.Tests;

/// <summary>
/// Recursive/self-referencing DTOs cannot be represented by TOML (section-based,
/// one nesting level). The generator must skip the recursive member with the
/// shared PICOSERDE003 diagnostic instead of dropping it silently.
/// </summary>
public class RecursiveDiagnosticTests
{
    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "RecursiveDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new TomlSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.Diagnostics.Select(d => d.Id).ToArray();
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
            AssemblyPath(typeof(TomlSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }

    [Test]
    public async Task RecursiveMember_IsReportedOnceForAllUsages()
    {
        const string source = """
            using PicoToml;

            public class TreeNode
            {
                public int Value { get; set; }
                public TreeNode? Child { get; set; }
            }

            public static class Probe
            {
                public static byte[] A() => TomlSerializer.Serialize(new TreeNode { Value = 1 });
                public static TreeNode? B(byte[] d) => TomlSerializer.Deserialize<TreeNode>(d);
            }
            """;

        var ids = RunGenerator(source).Where(id => id == "PICOSERDE003").ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NestedListMember_IsReported()
    {
        const string source = """
            using PicoToml;

            public class Leaf { public string Name { get; set; } = ""; }

            public class Holder { public System.Collections.Generic.List<System.Collections.Generic.List<Leaf>> Rows { get; set; } = new(); }

            public static class Probe
            {
                public static byte[] Run() => TomlSerializer.Serialize(new Holder());
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOSERDE004");
    }

    [Test]
    public async Task DictValueListMember_IsReported()
    {
        const string source = """
            using PicoToml;

            public class Holder
            {
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> Map { get; set; } = new();
            }

            public static class Probe
            {
                public static byte[] Run() => TomlSerializer.Serialize(new Holder());
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOSERDE004");
    }

    [Test]
    public async Task RecursiveMember_IsReported()
    {
        const string source = """
            using PicoToml;

            public class TreeNode
            {
                public int Value { get; set; }
                public TreeNode? Child { get; set; }
            }

            public static class Probe
            {
                public static string Run() => TomlSerializer.Serialize(new TreeNode { Value = 1 });
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOSERDE003");
    }
}
