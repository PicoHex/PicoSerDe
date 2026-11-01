// The null-literal guard a TEXT format puts in front of a nullable member's scalar parse (review C-30,
// converged here by C4). Compiled into every `.Gen` project by `PicoSerDe.Gen.props`' directory-derived
// `<Compile Include>`, exactly like the other files in this directory, which is why everything here is
// `internal`: the five `.Gen` projects are the only callers and `api/*.public.txt` must not move.
//
// WHY THIS FILE EXISTS. INI, TOML and YAML each had this guard as a private method — 39 lines, twice
// character-identical and once identical but for one word in the doc comment. A `diff` of the two closest
// copies produced exactly one line: `if (reader.IsNullLiteral)` vs `if (r.IsNullLiteral)`. So the ONE thing
// that distinguished them was the reader's local name, which is exactly the mistake `FusedEmit.cs` opens
// with: THE READER IS A PARAMETER, NEVER THE LITERAL `reader`.
//
// WHAT IS PARAMETRIC. Everything a format legitimately differs on is an argument: the reader's local name
// (`reader` in INI, `r` in TOML and YAML), the emit column (`pad` — indentation is part of the golden
// baseline), and the assignment target, passed as a delegate because each generator owns how it names the
// destination (`o.Member`, `obj.Member`, `__cp_0.Member`, ...). The shared code owns the shape, the caller
// owns the literals — the same split `AttributeLookup` follows for attribute names.
//
// WHAT IS NOT HERE, ON PURPOSE: THE DECISION TO EMIT THE GUARD AT ALL. Each generator still chooses its own
// sites, because that choice is a format fact: YAML's reader has a null literal for null ELEMENTS and null
// DICTIONARY VALUES (`- null`, `key: null`) and guards those with their own emitted shape
// (`__tmpList.Add(null!)`, `dict[__dk] = null`), while INI and TOML have no null literal at all and drop
// those members instead (PICOSERDE004). Those element guards are NOT copies of this one and are not folded
// into it: they emit different code, answer a different question (is this ELEMENT nullable, not is this
// MEMBER nullable) and have no `throw` / `null!` branches. Sharing the decision as well as the text is what
// would make a format that legitimately differs unrepresentable.

using System;
using System.Text;

namespace PicoSerDe.Gen;

/// <summary>
/// The one body behind the null-literal guard the three null-capable text formats emit in front of a
/// member's scalar read.
/// </summary>
internal static class NullGuardEmit
{
    /// <summary>
    /// Emits the guard that protects the scalar parse branch of one member:
    /// <code>
    /// if (&lt;reader&gt;.IsNullLiteral)
    /// {
    ///     &lt;target&gt; = null;              // or a loud throw, or `null!` — see below
    /// }
    /// else
    /// {
    /// </code>
    /// A nullable member takes <c>null</c>, a non-nullable value type fails loudly, and a non-nullable
    /// reference type keeps the existing NRT-lenient contract (<c>null!</c>). The guard is only an
    /// <b>opening</b>: the caller closes the <c>else</c> block after its own parse branch, because what
    /// happens inside the branch is the format's business (its own parse calls, its own variable names).
    /// </summary>
    /// <param name="s">The generator's source builder.</param>
    /// <param name="p">The member being read: its nullability and kind select the guard's body.</param>
    /// <param name="emitTarget">
    /// Emits the assignment target (<c>obj.Prop</c>, <c>o.Prop</c>, <c>__cp_0.Prop</c>, ...). A delegate
    /// rather than a string because each generator builds that name differently (INI threads a ctor flag,
    /// TOML and YAML capture a local function).
    /// </param>
    /// <param name="pad">The emit column.</param>
    /// <param name="reader">
    /// The reader's local name in this format's generated code — <c>reader</c> or <c>r</c> today. Never
    /// assumed: it is the one thing the three hand-written copies disagreed on.
    /// </param>
    internal static void EmitMemberNullGuard(
        StringBuilder s,
        PropertyInfo p,
        Action emitTarget,
        string pad,
        string reader
    )
    {
        s.Append(pad);
        s.Append("if (");
        s.Append(reader);
        s.AppendLine(".IsNullLiteral)");
        s.Append(pad);
        s.AppendLine("{");
        if (p.IsNullable || p.IsNullableReference)
        {
            s.Append(pad);
            s.Append("    ");
            emitTarget();
            s.AppendLine(" = null;");
        }
        else if (GenInfrastructure.IsValueTypeKind(p.TypeKind))
        {
            s.Append(pad);
            s.Append("    throw new System.FormatException($\"null value for non-nullable member ");
            s.Append(GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\");");
        }
        else
        {
            s.Append(pad);
            s.Append("    ");
            emitTarget();
            s.AppendLine(" = null!;");
        }
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else");
        s.Append(pad);
        s.AppendLine("{");
    }
}
