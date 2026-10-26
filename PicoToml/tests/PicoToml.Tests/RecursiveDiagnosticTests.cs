using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoToml;
using PicoToml.Gen;

namespace PicoToml.Tests;

/// <summary>
/// Recursive/self-referencing DTOs ARE representable in TOML (nested tables addressed by
/// their dotted path, spec §3 S11a) and R2 Task 5 implemented them. What is still dropped —
/// and must therefore still be reported with PICOSERDE003 rather than lost silently — is the
/// cycle TOML cannot close: one whose target cannot be instantiated (an abstract base), where
/// the only emission would be `new &lt;abstract&gt;()` (CS0144, invariant I-2).
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

            public abstract class TreeNode
            {
                public int Value { get; set; }
                public TreeNode? Child { get; set; }
            }

            public static class Probe
            {
                public static string A(TreeNode n) => TomlSerializer.Serialize(n);
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

    /// <summary>
    /// R2 Task 3 implemented the dict value position, so `Dictionary&lt;string, List&lt;int&gt;&gt;`
    /// is EMITTED in TOML now and reports nothing. The drop that is still open is a dict
    /// value whose sequence ends in an OBJECT: the facts say TOML can express it (S4), so
    /// the member is reported as debt rather than silently lost.
    /// </summary>
    [Test]
    public async Task DictValueObjectListMember_IsReported()
    {
        const string source = """
            using PicoToml;

            public class Leaf { public string Name { get; set; } = ""; }

            public class Holder
            {
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Leaf>> Map { get; set; } = new();
            }

            public static class Probe
            {
                public static byte[] Run() => TomlSerializer.Serialize(new Holder());
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOSERDE004");
    }

    /// <summary>
    /// The other half of the same row: a CONCRETE self cycle is emitted now, so the debt
    /// diagnostic has to be GONE. Keeping the old assertion would demand the bug back.
    /// </summary>
    [Test]
    public async Task ConcreteRecursiveMember_IsEmittedAndNotReported()
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
        await Assert.That(ids).DoesNotContain("PICOSERDE003");
    }

    /// <summary>
    /// The drop that REMAINS: an abstract cycle target. TOML has no polymorphic dispatch, so
    /// the only way to close it would be `new &lt;abstract&gt;()`, and the member is reported
    /// instead (⏸ debt — the format itself can hold the shape).
    /// </summary>
    [Test]
    public async Task AbstractRecursiveMember_IsReported()
    {
        const string source = """
            using PicoToml;

            public abstract class TreeNode
            {
                public int Value { get; set; }
                public TreeNode? Child { get; set; }
            }

            public static class Probe
            {
                public static string Run(TreeNode n) => TomlSerializer.Serialize(n);
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOSERDE003");
    }
}
