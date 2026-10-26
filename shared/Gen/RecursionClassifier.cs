// The recursion taxonomy every extractor and every emitter agrees on (spec §3 S11a/S11b).
// Linked into all 5 *.Gen projects AND into the integration test project, so the tests
// drive this exact code over real Roslyn symbols instead of restating the rule.

// Spelled here rather than inherited from the projects' global usings: this file is
// linked into six projects whose globals differ, and Roslyn is all it needs.
using Microsoft.CodeAnalysis;

namespace PicoSerDe.Gen;

/// <summary>
/// What kind of cycle a nested-property descent ran into — the answer to "which cycle does
/// this member close?" (spec §3, S11a vs S11b).
/// <para>
/// The distinction is not cosmetic: <c>RecursiveMutual × toml = Supported</c> while
/// <c>RecursiveSelf × toml = DropMember</c> (DecisionTables.Rules), because TOML holds one
/// level of nested table. A build that cannot tell the two apart either drops a shape the
/// format holds or emits a shape it does not — that was R1 W3's red build (4 errors:
/// CS0144 / CS1503), which is why R1 shipped a plain <c>bool</c> and this taxonomy waits
/// for R2 (spec §5.6, the three obligations).
/// </para>
/// </summary>
internal enum RecursionKind
{
    /// <summary>No cycle: the descent completed and the member was expanded inline.</summary>
    None,

    /// <summary>
    /// The member points back at the type that OWNS it (<c>root</c>): <c>Node.Child : Node</c>,
    /// <c>Node { List&lt;Node&gt; Children }</c>, and <c>Base.Next : Base</c> seen while a
    /// DERIVED type's inherited members are extracted (obligation ① in spec §5.6: a base
    /// type's own self-reference belongs to the base, not to the derived entry point).
    /// </summary>
    Self,

    /// <summary>
    /// The member points back at a type already on the descent stack that is NOT the type
    /// it is declared on: <c>A.B : B</c> with <c>B.A : A</c>, or <c>A.B : B</c> with
    /// <c>B { List&lt;A&gt; As }</c>. Closing that cycle goes through another type's
    /// helper, which is a different emitter contract from <see cref="Self"/>.
    /// </summary>
    Mutual,
}

/// <summary>
/// Classifies a cycle that the guarded descent just hit. One function, so the extraction
/// guard and the tests cannot drift into two definitions of "self" and "mutual".
/// </summary>
internal static class RecursionClassifier
{
    /// <summary>
    /// <see cref="RecursionKind.Self"/> when <paramref name="target"/> IS the type that
    /// owns the member set being extracted, <see cref="RecursionKind.Mutual"/> otherwise.
    /// </summary>
    /// <param name="target">The type the member points at (already on the descent stack).</param>
    /// <param name="root">
    /// The type that OWNS the member set this descent belongs to — the extraction root of
    /// this unit, which is not necessarily the outermost type of the whole request. For a
    /// derived type's INHERITED members that is the base-chain level, and that is the whole
    /// point of obligation ①: judged against the outermost derived type instead,
    /// <c>Base.Next : Base</c> answers Mutual, which hands it to the
    /// <c>RecursiveMutual × toml = Supported</c> row and emits a member that is dropped
    /// today (R1 W3's red build).
    /// </param>
    /// <remarks>
    /// Conservative degradation (spec §5.6 obligation ②, invariant I-2): a mutual cycle
    /// whose target cannot be instantiated is reported as <see cref="RecursionKind.Self"/>.
    /// Every emitter closes a cycle by CALLING the target type's generated helper, and a
    /// helper for an abstract type cannot construct it (<c>new T(...)</c> is CS0144). Until
    /// an emitter routes an abstract cycle target through its polymorphic serializer — which
    /// JSON and MsgPack already do for the row that is Supported today — the extraction
    /// layer keeps such a member on the Self row, where every format that cannot express it
    /// already drops it. "Never emit uncompilable code" (I-2) outranks "never under-report".
    /// </remarks>
    internal static RecursionKind Classify(INamedTypeSymbol target, INamedTypeSymbol root)
    {
        if (SymbolEqualityComparer.Default.Equals(target, root))
            return RecursionKind.Self;

        return IsInstantiable(target) ? RecursionKind.Mutual : RecursionKind.Self;
    }

    /// <summary>
    /// True when the generated helper for <paramref name="type"/> can end in
    /// <c>new T(...)</c>: a class or a struct that is not abstract. Interfaces and abstract
    /// classes are polymorphic entry points, not values.
    /// </summary>
    private static bool IsInstantiable(INamedTypeSymbol type) =>
        type.TypeKind != TypeKind.Interface && !type.IsAbstract;
}
