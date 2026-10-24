// Tests for source generator hintName uniqueness.
//
// Review I-23: the hintName assertions used to call a *copy* of the generator's
// naming helper that lived inside this test file, so they could never fail when
// the generator drifted. Every hintName assertion below now reads the real
// generator output (`CSharpGeneratorDriver.GetRunResult().GeneratedTrees`).
// The contract is: hintName always uses the fully-qualified name
// (`UniqueName(FullyQualifiedName)`), matching the Inner Helper naming strategy,
// with no short-then-FQN fallback.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

public sealed class GeneratorHintNameTests
{
    // ── SafeName correctness ──

    [Test]
    public async Task SafeName_TopLevelType_RemovesGlobalPrefix()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("global::System.String");
        await Assert.That(result).IsEqualTo("System_String");
    }

    [Test]
    public async Task SafeName_ReplacesDotsWithUnderscores()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("MyApp.Models.UserDto");
        await Assert.That(result).IsEqualTo("MyApp_Models_UserDto");
    }

    [Test]
    public async Task SafeName_CrossNamespaceSameShortName_Unique()
    {
        var safe1 = PicoSerDe.Gen.GenInfrastructure.SafeName("global::Ns1.SharedName");
        var safe2 = PicoSerDe.Gen.GenInfrastructure.SafeName("global::Ns2.SharedName");
        await Assert.That(safe1).IsNotEqualTo(safe2);
    }

    [Test]
    public async Task SafeName_EmptyInput_ReturnsEmpty()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("");
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task SafeName_AlreadyClean_NoChange()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("SimpleName");
        await Assert.That(result).IsEqualTo("SimpleName");
    }

    // ── HintName: asserted on the generator's real output ──

    [Test]
    public async Task HintName_SingleType_UsesFullyQualifiedName()
    {
        // A single unique type still gets the FQN-based name — no short-name fallback.
        var hintNames = RunGenerator(
            """
            using PicoJetson;

            namespace MyApp
            {
                public class MyType
                {
                    public int X { get; set; }
                }
            }

            public static class Probe
            {
                public static string Run() => JsonSerializer.Serialize(new MyApp.MyType());
            }
            """
        );

        await Assert.That(hintNames).Contains(MainHintName("MyApp.MyType"));
        await Assert.That(hintNames).DoesNotContain("MyType_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_CrossNamespaceSameShortName_BothUseFQN()
    {
        var hintNames = RunGenerator(
            """
            using PicoJetson;

            namespace Ns1
            {
                public class SharedName
                {
                    public int A { get; set; }
                }
            }

            namespace Ns2
            {
                public class SharedName
                {
                    public int B { get; set; }
                }
            }

            public static class Probe
            {
                public static string Run() =>
                    JsonSerializer.Serialize(new Ns1.SharedName())
                    + JsonSerializer.Serialize(new Ns2.SharedName());
            }
            """
        );

        var ns1 = MainHintName("Ns1.SharedName");
        var ns2 = MainHintName("Ns2.SharedName");

        await Assert.That(hintNames).Contains(ns1);
        await Assert.That(hintNames).Contains(ns2);
        await Assert.That(ns1).IsNotEqualTo(ns2);
        // No short-name special case: neither type fell back to the bare short name.
        await Assert.That(hintNames).DoesNotContain("SharedName_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_ThreeSameNamedTypes_AllUseFQNAndStayDistinct()
    {
        var hintNames = RunGenerator(
            """
            using PicoJetson;

            namespace Ns1
            {
                public class Data
                {
                    public int A { get; set; }
                }
            }

            namespace Ns2
            {
                public class Data
                {
                    public int B { get; set; }
                }
            }

            namespace Ns3
            {
                public class Data
                {
                    public int C { get; set; }
                }
            }

            public static class Probe
            {
                public static string Run() =>
                    JsonSerializer.Serialize(new Ns1.Data())
                    + JsonSerializer.Serialize(new Ns2.Data())
                    + JsonSerializer.Serialize(new Ns3.Data());
            }
            """
        );

        var expected = new[]
        {
            MainHintName("Ns1.Data"),
            MainHintName("Ns2.Data"),
            MainHintName("Ns3.Data"),
        };

        foreach (var name in expected)
            await Assert.That(hintNames).Contains(name);

        // All three files coexist: the generator emitted three distinct main trees.
        await Assert.That(expected.Distinct().Count()).IsEqualTo(3);
        await Assert.That(hintNames.Count(n => expected.Contains(n))).IsEqualTo(3);
    }

    [Test]
    public async Task HintName_GlobalNamespaceType_NoExtraUnderscore()
    {
        var hintNames = RunGenerator(
            """
            using PicoJetson;

            public class GlobalType
            {
                public int X { get; set; }
            }

            public static class Probe
            {
                public static string Run() => JsonSerializer.Serialize(new GlobalType());
            }
            """
        );

        await Assert.That(hintNames).Contains(MainHintName("GlobalType"));
        await Assert.That(hintNames).DoesNotContain("_GlobalType_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_NestedNamespace_DotsBecomeUnderscores()
    {
        var hintNames = RunGenerator(
            """
            using PicoJetson;

            namespace MyCompany.MyApp.Core.Models
            {
                public class UserProfile
                {
                    public int X { get; set; }
                }
            }

            public static class Probe
            {
                public static string Run() =>
                    JsonSerializer.Serialize(new MyCompany.MyApp.Core.Models.UserProfile());
            }
            """
        );

        await Assert
            .That(hintNames)
            .Contains(MainHintName("MyCompany.MyApp.Core.Models.UserProfile"));
        // The dots are gone from the file name — it is a single flat file name.
        await Assert.That(hintNames).DoesNotContain("UserProfile_JsonSerializer.g.cs");
    }

    // ── Harness: run the real generator and read back its hintNames ──

    /// <summary>
    /// The hintName the generator must produce for a type's main serializer file:
    /// <c>UniqueName(fullyQualifiedName) + "_JsonSerializer.g.cs"</c>.
    /// </summary>
    private static string MainHintName(string fullyQualifiedName) =>
        $"{PicoSerDe.Gen.GenInfrastructure.UniqueName(fullyQualifiedName)}_JsonSerializer.g.cs";

    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "HintNameAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.GeneratedTrees.Select(t => Path.GetFileName(t.FilePath)).Distinct().ToArray();
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
            AssemblyPath(typeof(JsonSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }
}
