// The default literal a generator writes for a C# TYPE KIND — one table, five rows (audit C12).
//
// One question — "this member has not been read yet, what do I write for a value of this kind?" —
// was spelled by hand at TWENTY-THREE sites in the five generators, in five shapes (`c5ff0b2`):
//
//   new T { Member = <literal>, … }       11  the eight 9-line `switch (rp.TypeKind)` copies
//                                             (JSON 3, MessagePack 2, INI 1, TOML 1, YAML 1)
//                                             plus three inline ternaries (INI, TOML, YAML)
//   ctor temp, 9 numeric kinds -> "0"      5  JSON 4, MessagePack 1
//   ctor temp, 11 numeric kinds -> "0"     1  INI's polymorphic path (adds float32, decimal)
//   ctor temp, no numeric row              1  INI's plain path (`""` / `default!`)
//   ctor temp, string -> "null!"           5  MessagePack 1, TOML 2, YAML 2
//
// The eight switch copies were byte-identical apart from the builder's name — the "five generators
// each have their own" shape the audit was looking for. The four ctor-temp tables are the more
// interesting find: ONE question with THREE answers, so "4–5 copies of one table" is wrong in both
// directions (more sites, and they are not one table). Every row compiles for every kind it is
// asked about, so the differences are style — and a style is exactly what an emitted `.g.cs` is
// made of, so collapsing two rows is a byte-diff decision with its own evidence, not a tidy-up.
// This change is byte-identical and therefore keeps all five rows, named.
//
// WHAT IS NOT HERE, ON PURPOSE. The WRAPPER around the member line: `foreach` plus `new T {`/`};`
// is emitted three ways (`var obj = new`, `var o = new`, `result = partial ?? new`) and JSON's
// sibling `else` branch calls `ObjectConstructor` instead of an initializer at all; the member LINE
// is shared (indentation is a parameter, as in `NullGuardEmit`), the loop is not. The inline
// `{ A = x, B = y }` form (3 sites) keeps its four hand-written appends: a different separator and no
// line to share. And `else s.Append("default")` in TOML's/YAML's scalar-parse fallbacks is
// `X.TryParse(...) ? __v : <fallback>`, whose other arms are per-format parse calls.
//
// THE ROW THE CHECKED-IN CORPUS CANNOT SEE. `RequiredMember` is reached only by a C# `required`
// member — `PropertyInfo.IsRequired` is Roslyn's `IPropertySymbol.IsRequired`, passed through by
// GenInfrastructure's extraction — and `tests/FormatCapabilities.Probe/Models.cs` declares none, so
// the checked-in 96-file baseline never enters those eleven sites. Their evidence is the targeted
// corpus in `.superpowers/sdd/c12-c22/report.md` (sixteen `required` members × five formats, before
// vs after, byte-identical), not the byte-diff.

using System;
using System.Text;

namespace PicoSerDe.Gen;

/// <summary>
/// Which of the tree's five answers a site asks <see cref="DefaultLiterals.For"/> for. The members
/// are ROWS of one table, not a taxonomy; each names the sites that asked for it.
/// </summary>
internal enum DefaultLiteralRow
{
    /// <summary>Required member of a <c>new T { … }</c>: <c>""</c> / <c>default</c>. Eleven sites.</summary>
    RequiredMember,

    /// <summary>Ctor temp: <c>""</c>, <c>0</c> for nine numeric kinds, <c>false</c>, else <c>default!</c>. Five sites.</summary>
    CtorTemp,

    /// <summary><see cref="CtorTemp"/> plus <c>float32</c>/<c>decimal</c> in the numeric row. ONE site —
    /// INI's polymorphic declaration, where the other four generators answer <c>default!</c>.</summary>
    CtorTempWider,

    /// <summary><see cref="CtorTemp"/> with no numeric row: <c>""</c>, then <c>default!</c>. ONE site —
    /// INI's plain ctor-temp declaration, the oldest of the five shapes.</summary>
    CtorTempNoZeroes,

    /// <summary>Ctor temp whose string cell is <c>null!</c> rather than <c>""</c>. Five sites (MessagePack 1,
    /// TOML 2, YAML 2) where <see cref="CtorTemp"/>'s five answer <c>""</c>.</summary>
    CtorTempNull,
}

/// <summary>
/// The one body behind every emitted default literal. A generator asks <see cref="For"/> or
/// <see cref="EmitRequiredMemberLine"/>; it does not spell <c>case "string": case "int32": …</c> again.
/// </summary>
internal static class DefaultLiterals
{
    private static readonly string[] NoNumericKinds = [];

    /// <summary>The numeric kinds that already answered <c>0</c> in every wide table.</summary>
    private static readonly string[] NineNumericKinds =
    [
        "int32",
        "int64",
        "int16",
        "uint16",
        "sbyte",
        "byte",
        "uint32",
        "uint64",
        "float64",
    ];

    /// <summary><see cref="NineNumericKinds"/> plus the two only INI's poly path answered.</summary>
    private static readonly string[] ElevenNumericKinds =
    [
        .. NineNumericKinds,
        "float32",
        "decimal",
    ];

    /// <summary>
    /// THE TABLE: (string cell, boolean cell, other cell, numeric cells). The <c>0</c> of the numeric
    /// cells is not stored — it is the same literal in every row that has one.
    /// </summary>
    private static (string Text, string Boolean, string Other, string[] Numeric) Cells(
        DefaultLiteralRow row
    ) =>
        row switch
        {
            DefaultLiteralRow.RequiredMember => ("\"\"", "default", "default", NoNumericKinds),
            DefaultLiteralRow.CtorTemp => ("\"\"", "false", "default!", NineNumericKinds),
            DefaultLiteralRow.CtorTempWider => ("\"\"", "false", "default!", ElevenNumericKinds),
            DefaultLiteralRow.CtorTempNoZeroes => ("\"\"", "default!", "default!", NoNumericKinds),
            DefaultLiteralRow.CtorTempNull => ("null!", "default!", "default!", NoNumericKinds),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no row for this site"),
        };

    /// <summary>
    /// The C# literal for <paramref name="typeKind"/> at a <paramref name="row"/> site:
    /// <c>"\"\""</c>, <c>"0"</c>, <c>"false"</c>, <c>"null!"</c>, or <c>"default"</c> / <c>"default!"</c>.
    /// A null kind (a member whose type did not resolve) takes the row's "other" cell, which is what the
    /// hand-written <c>default:</c> arms did.
    /// </summary>
    internal static string For(string? typeKind, DefaultLiteralRow row)
    {
        var cells = Cells(row);
        if (string.Equals(typeKind, "string", StringComparison.Ordinal))
            return cells.Text;
        if (string.Equals(typeKind, "boolean", StringComparison.Ordinal))
            return cells.Boolean;
        foreach (var numeric in cells.Numeric)
            if (string.Equals(numeric, typeKind, StringComparison.Ordinal))
                return "0";
        return cells.Other;
    }

    /// <summary>
    /// Emits ONE line of a <c>new T { … }</c> initializer for a required member —
    /// <c>&lt;pad&gt;Name = &lt;literal&gt;,&lt;newline&gt;</c> — the five append calls the eight sites
    /// each spelled by hand. The wrapper stays at the call site; see this file's header.
    /// </summary>
    internal static void EmitRequiredMemberLine(StringBuilder sb, PropertyInfo prop, string pad)
    {
        sb.Append(pad);
        sb.Append(prop.Name);
        sb.Append(" = ");
        sb.Append(For(prop.TypeKind, DefaultLiteralRow.RequiredMember));
        sb.AppendLine(",");
    }
}
