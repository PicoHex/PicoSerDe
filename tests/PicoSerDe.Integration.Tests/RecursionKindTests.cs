using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Spec §5.6's three obligations, pinned before Task 5 is allowed to consume them.
///
/// <para>
/// The extraction pipeline carries a <see cref="RecursionKind"/> out of the guarded descent
/// instead of a plain <c>bool</c>, so a site can tell <em>which</em> cycle it hit. That
/// distinction is the only thing standing between "TOML drops the member" and "TOML emits
/// one level of nested table" (<c>RecursiveSelf</c> vs <c>RecursiveMutual</c> in
/// <c>DecisionTables.Rules</c>), and R1 W3 proved that getting it wrong is not cosmetic:
/// judging a base chain's self-reference as mutual emitted <c>new</c> on types that cannot
/// be instantiated and turned the build red (CS0144/CS1503 on
/// <c>RvPolyBase</c>/<c>RvPolyLeaf</c>/<c>PolyRecAbstract</c>).
/// </para>
///
/// <para>
/// The first block drives the REAL classifier (<c>shared/Gen/RecursionClassifier.cs</c>,
/// linked into this project) over real Roslyn symbols built from the three cycle shapes: a
/// test that spells the rule out again would pass even when the generator disagreed with
/// it. The second block pins the wiring the classifier cannot see — which type each of the
/// six guarded-descent call sites hands it as <c>root</c> — by reading the one file that
/// holds them.
/// </para>
/// </summary>
public class RecursionKindTests
{
    /// <summary>The cycle shapes named in spec §5.6, plus the two degenerate ones.</summary>
    private const string Models = """
        // ① direct self recursion (S11a)
        public class Node
        {
            public int Value { get; set; }
            public Node? Child { get; set; }
        }

        // ② two-node mutual recursion (S11b)
        public class MutualA
        {
            public string Name { get; set; } = "";
            public MutualB? B { get; set; }
        }

        public class MutualB
        {
            public int N { get; set; }
            public MutualA? A { get; set; }
        }

        // ③ base chain, abstract base: `Next` is declared on the base and points at it.
        public abstract class PolyRecAbstract
        {
            public int Value { get; set; }
            public PolyRecAbstract? Next { get; set; }
        }

        public class PolyRecLeaf : PolyRecAbstract
        {
            public string Extra { get; set; } = "";
        }

        // ③b base chain, CONCRETE base (the shape that actually broke the build in R1:
        // a concrete base can be `new`-ed, so nothing but the Self verdict protects it).
        public class ConcBase
        {
            public int Id { get; set; }
            public ConcBase? Next { get; set; }
        }

        public class ConcLeaf : ConcBase
        {
            public string Extra { get; set; } = "";
        }

        // ④ a MUTUAL cycle whose target is abstract: `B.A : A` where A is abstract.
        public abstract class AbsA
        {
            public AbsB? B { get; set; }
        }

        public abstract class AbsB
        {
            public AbsA? A { get; set; }
        }
        """;

    private static readonly Lazy<Dictionary<string, INamedTypeSymbol>> Symbols = new(BuildSymbols);

    private static INamedTypeSymbol Type(string name) => Symbols.Value[name];

    // ── ① self recursion ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DirectSelfRecursion_IsSelf()
    {
        // Production: ExtractDeclaredProperties(Node) reaches `Child : Node` with root = Node
        // (the owner of the member set) while Node is already on the descent stack.
        await Assert
            .That(RecursionClassifier.Classify(Type("Node"), Type("Node")))
            .IsEqualTo(RecursionKind.Self);
    }

    // ── ② two-node mutual recursion ───────────────────────────────────────────────────

    [Test]
    public async Task TwoNodeMutualRecursion_IsMutual()
    {
        // Production: MutualB's members are walked with root = MutualB, and `B.A : A` hits
        // MutualA on the stack. The target is another type, not the owner.
        await Assert
            .That(RecursionClassifier.Classify(Type("MutualA"), Type("MutualB")))
            .IsEqualTo(RecursionKind.Mutual);
    }

    [Test]
    public async Task MutualRecursion_HoldsInBothDirections()
    {
        // Symmetry: the pair must classify the same way whichever node the extraction enters.
        await Assert
            .That(RecursionClassifier.Classify(Type("MutualB"), Type("MutualA")))
            .IsEqualTo(RecursionKind.Mutual);
    }

    // ── ③ base chain (obligation ①) ───────────────────────────────────────────────────

    [Test]
    public async Task BaseChainSelfRecursionSeenFromDerivedType_IsSelf()
    {
        // Production: a derived type's INHERITED members are extracted at the base-chain
        // level (ExtractPropertiesCore seeds each level into `visiting`), so `Next : Base` is
        // walked with root = Base. That, and only that, makes it Self.
        await Assert
            .That(RecursionClassifier.Classify(Type("PolyRecAbstract"), Type("PolyRecAbstract")))
            .IsEqualTo(RecursionKind.Self);
        await Assert
            .That(RecursionClassifier.Classify(Type("ConcBase"), Type("ConcBase")))
            .IsEqualTo(RecursionKind.Self);
    }

    [Test]
    public async Task BaseChainJudgedAgainstTheOutermostType_WouldBeMutual()
    {
        // Negative control, kept because it is the exact input that turned R1 W3 red: judged
        // against the derived entry point (route A's "root = the outermost type"), a concrete
        // base's own self-reference answers Mutual -> toml = Supported -> the emitters write
        // `new ConcBase(...)` inside the derived type's serializer (CS1503/CS0144 territory).
        // If a future simplification makes root the outermost type "for consistency", this
        // assertion is what explains the breakage.
        await Assert
            .That(RecursionClassifier.Classify(Type("ConcBase"), Type("ConcLeaf")))
            .IsEqualTo(RecursionKind.Mutual);
    }

    // ── ④ abstract target (obligation ②, invariant I-2) ───────────────────────────────

    [Test]
    public async Task MutualCycleThroughAnAbstractType_DegradesToSelf()
    {
        // Emitting this as Mutual hands it to the Supported row, and every emitter closes a
        // mutual cycle by calling the target's helper -> `new AbsA(...)` -> CS0144. The
        // classifier keeps it on the Self row until an emitter can route an abstract target
        // through its polymorphic serializer (the way JSON/MsgPack already do).
        await Assert
            .That(RecursionClassifier.Classify(Type("AbsA"), Type("AbsB")))
            .IsEqualTo(RecursionKind.Self);
    }

    [Test]
    public async Task TheDegradation_OnlyCoversUninstantiableTargets()
    {
        // Scoped, not a blanket: a concrete Mutual must stay Mutual, or Task 5 would have
        // nothing left to flip.
        await Assert.That(Type("MutualB").IsAbstract).IsFalse();
        await Assert
            .That(RecursionClassifier.Classify(Type("MutualA"), Type("MutualB")))
            .IsEqualTo(RecursionKind.Mutual);
        await Assert.That(Type("AbsA").IsAbstract).IsTrue();
    }

    // ── the six call sites (obligation ①: root is the OWNER, threaded) ─────────────────

    [Test]
    public async Task TheSixGuardedDescents_PassTheOwnerTypeAsRoot()
    {
        var sites = Regex
            .Matches(
                Source(),
                @"(?<target>[A-Za-z0-9_]+) = ExtractNestedPropertiesGuarded\(\s*\r?\n"
                    + @"\s*(?<type>[A-Za-z0-9_]+),\s*\r?\n"
                    + @"\s*(?<root>[A-Za-z0-9_]+),",
                RegexOptions.CultureInvariant
            )
            .Select(m =>
                $"{m.Groups["target"].Value}/{m.Groups["type"].Value}/{m.Groups["root"].Value}"
            )
            .ToArray();

        // Four member-level sites pass the chain level in scope (`type`); the two inside the
        // nested-container helpers pass the `root` they were handed. Nothing passes the type
        // that was just descended into — that would answer Self for every cycle.
        await Assert
            .That(string.Join(" | ", sites))
            .IsEqualTo(
                "fieldNested/fieldObjNts/type | nestedProperties/eNtsObj/type"
                    + " | nestedProperties/vNtsObj/type | nestedProperties/objNts/type"
                    + " | innerNested/ntsObjElem/root | innerNested/ivNtsObj/root"
            );
    }

    [Test]
    public async Task NestedContainerHelpers_ThreadRootInsteadOfRedefiningIt()
    {
        var source = Source();

        // Both helpers take `root` right after the container type and hand it on unchanged
        // (their two call sites pass `type` from the member level and `root` when recursing).
        await Assert
            .That(
                Regex
                    .Matches(
                        source,
                        @"(?<helper>BuildNested(?:List|Dict)Element)\(\s*\r?\n\s*[A-Za-z0-9_<>?\[\]]+ [A-Za-z0-9_]+,\s*\r?\n\s*INamedTypeSymbol root,",
                        RegexOptions.CultureInvariant
                    )
                    .Count
            )
            .IsEqualTo(2);

        var threaded = Regex
            .Matches(
                source,
                @"(?<first>[A-Za-z0-9_]+) = BuildNested(?:List|Dict)Element\(\s*\r?\n\s*[A-Za-z0-9_.]+,\s*\r?\n\s*(?<root>[A-Za-z0-9_.]+),",
                RegexOptions.CultureInvariant
            )
            .Select(m => m.Groups["root"].Value)
            .ToArray();

        await Assert.That(string.Join(", ", threaded)).IsEqualTo("type, type, root, root");
    }

    [Test]
    public async Task TheGuard_ClassifiesThroughTheSharedClassifier()
    {
        // One definition, one call: the guard must not re-derive self/mutual locally (that is
        // how the extraction path and this file would drift apart and still both be green).
        var calls = Regex
            .Matches(Source(), @"RecursionClassifier\.Classify\((?<args>[^)]*)\)")
            .Select(m => m.Groups["args"].Value.Replace(" ", ""))
            .ToArray();

        await Assert.That(string.Join(" | ", calls)).IsEqualTo("type,root");
    }

    // ── IsRecursiveRef stays a bool, fed by the kind ───────────────────────────────────

    [Test]
    public async Task IsRecursiveRef_IsTheKindReducedToOneBit()
    {
        var assignments = Regex
            .Matches(
                Source(),
                @"IsRecursiveRef:\s*(?<value>[^\r\n]+)",
                RegexOptions.CultureInvariant
            )
            .Select(m => m.Groups["value"].Value.Trim().TrimEnd(',').TrimEnd())
            .ToArray();

        // 4 sites: public field, property, nested list element, nested dict value.
        await Assert.That(assignments.Length).IsEqualTo(4);
        foreach (var value in assignments)
            await Assert.That(value).EndsWith("!= RecursionKind.None");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────

    private static string _source = "";

    private static string Source() =>
        _source != ""
            ? _source
            : _source = File.ReadAllText(
                Path.Combine(TestRepo.Root(), "shared", "Gen", "GenInfrastructure.cs")
            );

    private static Dictionary<string, INamedTypeSymbol> BuildSymbols()
    {
        var compilation = CSharpCompilation.Create(
            "RecursionKindFixture",
            [CSharpSyntaxTree.ParseText(Models, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                "fixture does not compile: " + string.Join("; ", errors.Select(e => e.GetMessage()))
            );

        return compilation
            .GetSymbolsWithName(_ => true)
            .OfType<INamedTypeSymbol>()
            .Where(s => s.ContainingType is null)
            .ToDictionary(s => s.Name, s => s);
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            var name = Path.GetFileName(path);
            if (name is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
                yield return MetadataReference.CreateFromFile(path);
        }
    }
}
