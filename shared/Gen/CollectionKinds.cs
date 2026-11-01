using System;
using System.Collections.Generic;

namespace PicoSerDe.Gen;

/// <summary>
/// What a collection TypeKind CAN DO, as six independent capability bits.
/// <para>
/// The bug this model exists for: the collection-kind lists in the five generators looked like ONE
/// list copied nineteen times, and a single <c>IsListLike()</c> built from that reading would have
/// been wrong at three quarters of the sites. Measured on <c>930f193</c>, those sites carry six
/// distinct member sets, and each one has a reason that lives in the wire format or in C#:
/// </para>
/// <list type="table">
/// <item><term>9</term><description><see cref="CollectionKinds.IsSequence"/> — the wire form is a sequence</description></item>
/// <item><term>7</term><description><see cref="CollectionKinds.IsForeachable"/> — a <c>foreach</c> can enumerate it;
/// <c>Memory</c>/<c>ReadOnlyMemory</c> cannot and need <c>.Span</c> (<see cref="CollectionKinds.RequiresSpan"/>, the other 2)</description></item>
/// <item><term>7</term><description><see cref="CollectionKinds.IsExtendedCollection"/> — the READ side rebuilds the declared
/// type from a temp <c>List&lt;T&gt;</c>; <c>list</c>/<c>array</c> have their own read paths</description></item>
/// <item><term>8</term><description><see cref="CollectionKinds.RequiresListConversion"/> — the temp list has to become the
/// declared type (<c>array</c> via <c>.ToArray()</c> plus the seven rebuilt kinds); <c>list</c> IS the temp list</description></item>
/// <item><term>6</term><description><see cref="CollectionKinds.IsNullableSequence"/> — the value can be null; the other
/// three are structs, so <c>x is null</c> does not compile on them</description></item>
/// <item><term>4</term><description><see cref="CollectionKinds.UsesLength"/> — the count is <c>.Length</c>, not <c>.Count</c></description></item>
/// </list>
/// <para>
/// Six sets, six bits, nine rows: a new collection kind is ONE row here, and a site that needs "a
/// collection that can do X" asks for X by name instead of re-spelling the neighbourhood of kinds
/// that happen to have X today.
/// </para>
/// <para>
/// This file is NOT the per-kind <em>emitter</em>: <see cref="GenInfrastructure.EmitCollectionFromList"/>
/// still owns the construction code for each kind (a new kind really does have to add that arm), and
/// <c>TypeKindResolver</c> still owns the <c>(namespace, name) → kind</c> registry (which types
/// become kinds). Membership is a different question from construction and from identity.
/// </para>
/// </summary>
[Flags]
internal enum CollectionCapability
{
    /// <summary>Not a collection kind (or not a kind at all).</summary>
    None = 0,

    /// <summary>The wire form is a SEQUENCE of values: <c>[1,2,3]</c>, a msgpack array, an INI comma line.</summary>
    Sequence = 1,

    /// <summary>
    /// A <c>foreach</c> can enumerate the value directly. False for <c>Memory&lt;T&gt;</c> and
    /// <c>ReadOnlyMemory&lt;T&gt;</c>: they are not enumerable, so a writer goes through <c>.Span</c>
    /// first (and a <c>foreach</c> over them does not compile).
    /// </summary>
    Foreach = 1 << 1,

    /// <summary>
    /// The READ side fills a temp <c>List&lt;T&gt;</c> and then builds the DECLARED type from it
    /// (<c>new HashSet&lt;int&gt;(tmp)</c>, <c>ImmutableArray.CreateRange(tmp)</c>, …).
    /// </summary>
    Reconstituted = 1 << 2,

    /// <summary>The temp <c>List&lt;T&gt;</c> IS the declared type — only <c>list</c>.</summary>
    TempListIsDeclared = 1 << 3,

    /// <summary>A null value is expressible: false where the kind is a struct, so the null guard must not be emitted.</summary>
    NullableValue = 1 << 4,

    /// <summary>The element count is <c>.Length</c>; every other kind counts with <c>.Count</c>.</summary>
    Length = 1 << 5,
}

/// <summary>
/// The collection-kind table and the named queries over it — ONE place that says which TypeKind
/// belongs to which collection family. A generator asks a query here; it does not spell
/// <c>case "hashset": case "queue": …</c> again.
/// </summary>
internal static class CollectionKinds
{
    private const CollectionCapability Seq = CollectionCapability.Sequence;
    private const CollectionCapability Fo = CollectionCapability.Foreach;
    private const CollectionCapability Reb = CollectionCapability.Reconstituted;
    private const CollectionCapability Temp = CollectionCapability.TempListIsDeclared;
    private const CollectionCapability Null = CollectionCapability.NullableValue;
    private const CollectionCapability Len = CollectionCapability.Length;

    /// <summary>
    /// THE TABLE. One row per collection kind: the name, then what it can do. Adding a kind is this
    /// line (plus, inevitably, its construction code in
    /// <see cref="GenInfrastructure.EmitCollectionFromList"/>) — every query below, and every site
    /// that asks one, follows from the bits.
    /// </summary>
    private static readonly (string Name, CollectionCapability Bits)[] Rows =
    [
        //  kind                capabilities
        ("list", Seq | Fo | Temp | Null),
        ("array", Seq | Fo | Null | Len),
        ("hashset", Seq | Fo | Reb | Null),
        ("queue", Seq | Fo | Reb | Null),
        ("stack", Seq | Fo | Reb | Null),
        ("linkedlist", Seq | Fo | Reb | Null),
        ("immutablearray", Seq | Fo | Reb | Len),
        ("memory", Seq | Reb | Len),
        ("readonlymemory", Seq | Reb | Len),
    ];

    private static readonly Dictionary<string, CollectionCapability> Table = Index(Rows);

    /// <summary>
    /// Every collection kind, in table order. The differential harness enumerates this so that "all
    /// kinds" cannot drift from the table it is testing.
    /// </summary>
    internal static IReadOnlyList<string> Names { get; } = NamesOf(Rows);

    /// <summary>The capability bits of <paramref name="kind"/>; <see cref="CollectionCapability.None"/> when it is not a collection kind.</summary>
    internal static CollectionCapability Capabilities(string? kind) =>
        kind is not null && Table.TryGetValue(kind, out var bits)
            ? bits
            : CollectionCapability.None;

    /// <summary>
    /// Every kind whose wire form is a sequence — <c>list</c>, <c>array</c> and the seven extended
    /// collection kinds. The arm that answers "is this member/element a sequence at all".
    /// </summary>
    internal static bool IsSequence(string? kind) => Has(kind, Seq);

    /// <summary>
    /// The kinds a <c>foreach</c> can enumerate with no <c>.Span</c> — the nine sequence kinds minus
    /// <c>memory</c>/<c>readonlymemory</c>. The JSON member writer splits its sequence arm on this,
    /// because <c>foreach (var x in mem)</c> does not compile.
    /// </summary>
    internal static bool IsForeachable(string? kind) => Has(kind, Fo);

    /// <summary>
    /// The kinds whose value must be written through <c>.Span</c> — the complement of
    /// <see cref="IsForeachable"/> inside the sequence family. Both halves are asked by name, so
    /// neither is "whatever the other one is not".
    /// </summary>
    internal static bool RequiresSpan(string? kind) => IsSequence(kind) && !IsForeachable(kind);

    /// <summary>
    /// The seven EXTENDED collection kinds: sequences every format writes like a <c>List&lt;T&gt;</c>
    /// but that need their own construction on the read side. <c>list</c>/<c>array</c> are sequences
    /// too and are deliberately NOT here — they have their own read arms.
    /// </summary>
    internal static bool IsExtendedCollection(string? kind) => Has(kind, Reb);

    /// <summary>
    /// The temp <c>List&lt;T&gt;</c> a reader filled has to be CONVERTED into the declared type: every
    /// sequence kind except <c>list</c>. The read arms that "fill a temp list, then convert" ask this
    /// once instead of listing <c>array</c> plus the seven extended kinds.
    /// </summary>
    internal static bool RequiresListConversion(string? kind) =>
        IsSequence(kind) && !Has(kind, Temp);

    /// <summary>
    /// The sequence kinds whose value can be null. <c>ImmutableArray&lt;T&gt;</c>,
    /// <c>Memory&lt;T&gt;</c> and <c>ReadOnlyMemory&lt;T&gt;</c> are structs, so a generated
    /// <c>x ?? []</c> or <c>x is null</c> on them does not compile.
    /// </summary>
    internal static bool IsNullableSequence(string? kind) => IsSequence(kind) && Has(kind, Null);

    /// <summary>True when the element count is <c>.Length</c> rather than <c>.Count</c>.</summary>
    internal static bool UsesLength(string? kind) => Has(kind, Len);

    /// <summary>
    /// The expression that enumerates a sequence value on the WRITE side: <c>.Span</c> for the
    /// <see cref="RequiresSpan"/> kinds, the accessor itself for every other sequence kind.
    /// </summary>
    internal static string SequenceItemsExpression(string? kind, string accessor) =>
        RequiresSpan(kind) ? accessor + ".Span" : accessor;

    /// <summary>
    /// The <c>Count</c>/<c>Length</c> expression for a sequence value, for the writers that announce
    /// the element count up front (msgpack) or test for an empty sequence (yaml's <c>- []</c>).
    /// </summary>
    internal static string SequenceCountExpression(string? kind, string accessor) =>
        UsesLength(kind) ? accessor + ".Length" : accessor + ".Count";

    private static bool Has(string? kind, CollectionCapability bits) =>
        (Capabilities(kind) & bits) != 0;

    private static Dictionary<string, CollectionCapability> Index(
        (string Name, CollectionCapability Bits)[] rows
    )
    {
        var index = new Dictionary<string, CollectionCapability>(
            rows.Length,
            StringComparer.Ordinal
        );
        foreach (var row in rows)
            index.Add(row.Name, row.Bits);
        return index;
    }

    private static IReadOnlyList<string> NamesOf((string Name, CollectionCapability Bits)[] rows)
    {
        var names = new string[rows.Length];
        for (var i = 0; i < rows.Length; i++)
            names[i] = rows[i].Name;
        return Array.AsReadOnly(names);
    }
}
