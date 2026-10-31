// Shared emission for the FUSED read (spec docs/superpowers/specs/2026-10-28-fused-token-reader-design.md
// §3.4 rules E-1/E-3, §3.4.2) — the generator-side half of the fused read contract that lives in
// PicoSerDe.Core. Compiled into every `.Gen` project by `<Compile Include>`, exactly like the other files in
// this directory, which is why everything here is `internal`: the five `.Gen` projects are the only callers
// and `api/*.public.txt` must not move (spec §4.1 A-4).
//
// WHY THIS FILE EXISTS. Before it, each generator spelled its own fused-read loop out in `Append` calls.
// The two shapes that exist today — MessagePack's counted loop (E-1) and JSON's fused `int` element loop
// (E-3) — were written twice with nothing tying them to the contract in Core, so "which member does this
// format call, and on what type" could only be answered by reading three files. Wiring a third format meant
// copying one of them. Here the shape is written once, and the caller supplies the reader.
//
// TWO RULES SHAPE EVERY SIGNATURE.
//   * THE READER IS A PARAMETER, NEVER THE LITERAL `reader`. The five formats do not agree on the local's
//     name (JSON/MsgPack/INI generate `reader`, TOML generates `r`, YAML has both), and spec §3.4.2 C-1 is
//     explicit that a shared emitter must take both the variable name and the concrete type name — the
//     mistake `GenInfrastructure.EmitCollectionFromList` made by hard-coding its operands.
//   * THE CALL IS EMITTED ON THE CONCRETE READER TYPE (spec §3.4.3). Nothing here names a token-reader
//     interface, not even in prose: the emit points' own guards grep the generators for those two names,
//     and a shared file is compiled into the same projects — spelling them here would put the ban out of
//     the guard's reach the moment someone widens the grep to the whole `.Gen` project.
//
// WHAT IS NOT HERE, ON PURPOSE. The end-token loop (E-2, `while (reader.Read() &&
// reader.TokenType != TokenType.ArrayEnd)`) is still written at each site: it is not a fused shape, its
// corpus is 24 sites across three generators (spec §3.4.2 C-2), and moving it is a separate piece of work
// with its own golden diff. The same goes for the dictionary `PropertyName` loop and TOML's `TablePath`
// checks.

using System;
using System.Text;

namespace PicoSerDe.Gen;

/// <summary>
/// The fused-read emit shapes of spec §3.4.1, written once for all five generators.
/// <para>
/// A method here writes <b>all</b> of the emitted text of the thing it is named after — the whole loop where
/// the rule is a loop, the opening of a branch where the rule is a branch — so the shape cannot
/// half-diverge between two formats. What stays with the caller is its own element body and its own closing
/// braces, because that is where the formats legitimately differ. Each method takes the reader's local name
/// and its concrete type's name (<see cref="RequireReader"/>) and emits at the column it is given, because
/// the generated code's indentation is part of the golden baseline.
/// </para>
/// <para>
/// <b>Adding a format</b> = pass that format's reader name and type to the shapes it needs. Nothing here
/// branches on a format tag: whether a reader can answer a count is a fact about the reader (and is
/// therefore the caller's decision, see <see cref="EmitCollectionCountProbe"/>), not about the text
/// format it belongs to.
/// </para>
/// </summary>
internal static class FusedEmit
{
    /// <summary>
    /// Emits rule <b>E-1</b>'s count probe (spec §3.4.1):
    /// <code>
    ///     if (reader.TryGetCollectionCount(out var __cn7)) {
    /// </code>
    /// — the opening of the branch whose body is the counted loop. It is separate from
    /// <see cref="EmitCountedLoopBodyOpen"/> because the two are <b>not adjacent</b>: a caller with a
    /// collection-level fast path nests it between them (MessagePack's bulk int-array read does), and the
    /// probe's number is that path's argument, so the emitted order is probe → fast path → loop.
    /// <para>
    /// <b>Who should call it:</b> the generator of a format whose reader declares a counted collection, i.e.
    /// the format for which <c>TryGetCollectionCount</c> answers the very number the reader itself uses to
    /// decide the collection ended (today MessagePack only, spec §6 S1). A format whose wire carries no count
    /// emits rule <b>E-2</b> instead and must not call this: a counted loop over a number the wire never
    /// carried is a silent truncation.
    /// </para>
    /// <para>
    /// <paramref name="indent"/> is the column of the <c>if (TokenType == ArrayStart)</c> the probe sits
    /// inside, and <paramref name="loopId"/> keeps <c>__cn</c>/<c>__ci</c> unique across every site in one
    /// generated file. The caller closes this branch after <see cref="EmitCountedCollectionEndRead"/>.
    /// </para>
    /// </summary>
    internal static void EmitCollectionCountProbe(
        StringBuilder sb,
        string readerVar,
        string readerType,
        string indent,
        int loopId
    )
    {
        RequireReader(readerVar, readerType);

        sb.Append(indent);
        sb.Append("    if (");
        sb.Append(readerVar);
        sb.Append(".TryGetCollectionCount(out var __cn");
        sb.Append(loopId);
        sb.AppendLine(")) {");
    }

    /// <summary>
    /// Emits rule <b>E-1</b>'s counted loop body opening: the <c>for</c> header over the count and the
    /// element-existence guard, i.e.
    /// <code>
    ///         for (var __ci7 = 0; __ci7 &lt; __cn7; __ci7++) {
    ///             if (!reader.Read()) break;
    /// </code>
    /// — the caller's element body follows immediately.
    /// <para>
    /// <b>Why the guard is part of the shape and not an option.</b> The count promises the elements, not a
    /// well-formed payload: <c>Read()</c> answers <c>false</c> when the data ends inside the collection and
    /// leaves the previous token in place, so breaking is exactly what the end-token form (<b>E-2</b>) did at
    /// the same point. Without it a truncated collection re-reads a stale value span and reports the last
    /// element twice. On a well-formed payload the branch is never taken and the token count stays E-2's
    /// <c>count + 1</c>.
    /// </para>
    /// <para>
    /// <b>Who should call it:</b> whoever called <see cref="EmitCollectionCountProbe"/>, once its fast path
    /// (if any) is emitted. Not for a fused element read: rule <b>E-3</b> answers for its own failures
    /// (<see cref="EmitFusedInt32ElementLoop"/>).
    /// </para>
    /// <para>
    /// <paramref name="loopIndent"/> is the <c>for</c> line's column, which is not the probe's column plus a
    /// constant (MessagePack's bulk path indents the loop one level deeper).
    /// </para>
    /// </summary>
    internal static void EmitCountedLoopBodyOpen(
        StringBuilder sb,
        string readerVar,
        string readerType,
        string loopIndent,
        int loopId
    )
    {
        RequireReader(readerVar, readerType);

        sb.Append(loopIndent);
        sb.Append("for (var __ci");
        sb.Append(loopId);
        sb.Append(" = 0; __ci");
        sb.Append(loopId);
        sb.Append(" < __cn");
        sb.Append(loopId);
        sb.Append("; __ci");
        sb.Append(loopId);
        sb.AppendLine("++) {");

        sb.Append(loopIndent);
        sb.Append("    if (!");
        sb.Append(readerVar);
        sb.AppendLine(".Read()) break;");
    }

    /// <summary>
    /// Emits rule <b>E-1</b>'s explicit trailing read: <c>reader.Read();</c> at
    /// <paramref name="readIndent"/>.
    /// <para>
    /// <b>This single line is the whole correctness argument of E-1.</b> Counting the elements does not
    /// close the collection: after <c>count</c> elements the reader's level counter is 0 and the <i>next</i>
    /// <c>Read()</c> produces the end token. The <c>while</c> form performed that read as its own exit
    /// condition, so the counted loop has to do it explicitly — otherwise the token stream shifts by one and
    /// the parent's next member is decoded from the end token. Centralising it is how a fourth and fifth
    /// format cannot forget it.
    /// </para>
    /// <para>
    /// <b>Who should call it:</b> whoever opened a counted loop with
    /// <see cref="EmitCollectionCountProbe"/>, after closing that loop's braces.
    /// </para>
    /// </summary>
    internal static void EmitCountedCollectionEndRead(
        StringBuilder sb,
        string readerVar,
        string readerType,
        string readIndent
    )
    {
        RequireReader(readerVar, readerType);

        sb.Append(readIndent);
        sb.Append(readerVar);
        sb.AppendLine(".Read();");
    }

    /// <summary>
    /// Emits rule <b>E-3</b> for an <c>int</c> element: the whole
    /// <c>while (true) { ... }</c> loop whose first statement is
    /// <c>if (reader.TryReadInt32(out var __elementValue))</c> — one call that advances the reader
    /// <b>and</b> decodes the token, replacing the reference pair whose <c>Read()</c> classified the same
    /// bytes (int, then long, then ulong) before <c>TryGetInt32()</c> parsed them once more.
    /// <para>
    /// <b>Why <c>int32</c> and nothing else.</b> Only <c>int</c> is fused in JSON: spec §3.3 marks
    /// <c>TryReadInt32</c> as its single override, so emitting the other three fused reads would rewrite
    /// every golden file for a one-line forwarder's worth of gain. The same reasoning keeps this shape out
    /// of the formats that never pursued emission (the registry in <c>docs/fused-emission-registry.md</c>
    /// records that decision per member).
    /// </para>
    /// <para>
    /// <b>Why the failure path is longer than "swap the two calls".</b> JSON carries no element count
    /// (spec §3.3), so this loop cannot assume an element is there, and <c>false</c> means one of three
    /// things, each with a different answer in the loop being replaced:
    /// </para>
    /// <list type="number">
    /// <item><description><b>The end token</b> (<c>TokenType.ArrayEnd</c>) — exactly what the reference
    /// <c>while</c> condition tested, so break.</description></item>
    /// <item><description><b>A chunk boundary</b> (<c>NeedsMoreData</c>, which only <c>Read()</c> sets and
    /// a token it produced always clears) — the reference loop's <c>Read()</c> was false and broke out, so
    /// this loop breaks identically and the caller keeps its rewind-and-retry.</description></item>
    /// <item><description><b>The end of the data</b> — no token at all. <c>Read()</c> returns the pooled
    /// buffer and clears the value span when it fails at the end of a final block, so the token still
    /// current is the <i>previous element</i>: breaking is what the reference loop did, while consuming it
    /// again would append the last element twice. Recognised by an empty <c>ValueSpan</c> <b>plus</b> a type
    /// this loop can leave behind (<c>Int32</c>/<c>Int64</c>/<c>ArrayStart</c>): a fresh <c>Int32</c> can
    /// never reach this branch (the fused read answers <c>true</c> for every literal <c>Read()</c> would
    /// call <c>Int32</c>), a fresh <c>Int64</c> carries its literal, and a fresh <c>ArrayStart</c> carries
    /// the superseded span.</description></item>
    /// </list>
    /// <para>
    /// <b>Three is a conservative test, not a proof.</b> It can only misfire on a malformed payload whose
    /// collection is preceded by an <i>empty</i> property name (the superseded span of a fresh
    /// <c>ArrayStart</c> is then empty too), where this loop exits instead of throwing; every well-formed
    /// document and every malformed one without that shape takes the reference answer. The alternative —
    /// asking the reader whether its last read produced a token — would be a new member, and spec §2.2 N2
    /// keeps <c>Read()</c>'s observable surface closed.
    /// </para>
    /// <para>
    /// <b>Everything else keeps the reference body.</b> A JSON <c>null</c> element is stored when the
    /// declared element is nullable (<paramref name="nullableElement"/>) and is otherwise the same format
    /// error; a number that needs <c>long</c> is widened and narrowed with <c>checked</c> (the reference
    /// never truncates); any other token type throws the identical message at the identical offset.
    /// </para>
    /// <para>
    /// <b>Who should call it:</b> the JSON generator, at every site whose element kind is <c>int32</c> —
    /// the property collection, the constructor-parameter collection, the extended collection
    /// (<c>HashSet&lt;int&gt;</c>), a dictionary value that is a collection, and the top-level
    /// <c>int[]</c>/<c>List&lt;int&gt;</c> entry point. A format that carries an element count must
    /// <b>not</b> call this: it has rule E-1, whose failure path is the single <c>throw</c> the count
    /// justifies (<see cref="EmitCountedLoopBodyOpen"/>).
    /// </para>
    /// </summary>
    internal static void EmitFusedInt32ElementLoop(
        StringBuilder sb,
        string readerVar,
        string readerType,
        string addTarget,
        string valueVar,
        bool nullableElement,
        string indent
    )
    {
        RequireReader(readerVar, readerType);

        var body = indent + "    ";
        var cold = indent + "        ";

        sb.Append(indent);
        sb.AppendLine("while (true)");
        sb.Append(indent);
        sb.AppendLine("{");
        sb.Append(body);
        sb.Append("if (");
        sb.Append(readerVar);
        sb.Append(".TryReadInt32(out var ");
        sb.Append(valueVar);
        sb.AppendLine("))");
        sb.Append(body);
        sb.AppendLine("{");
        sb.Append(cold);
        sb.Append(addTarget);
        sb.Append('.');
        sb.AppendLine("Add(" + valueVar + ");");
        sb.Append(cold);
        sb.AppendLine("continue;");
        sb.Append(body);
        sb.AppendLine("}");
        sb.Append(body);
        sb.AppendLine("// rule E-3 (spec §3.4.1): the three ways the fused read can answer false");
        sb.Append(body);
        sb.Append("if (");
        sb.Append(readerVar);
        sb.AppendLine(".TokenType == TokenType.ArrayEnd) break;");
        sb.Append(body);
        sb.Append("if (");
        sb.Append(readerVar);
        sb.AppendLine(".NeedsMoreData) break;");
        sb.Append(body);
        sb.Append("if (");
        sb.Append(readerVar);
        sb.AppendLine(".ValueSpan.IsEmpty");
        sb.Append(body);
        sb.Append("    && ");
        sb.Append(readerVar);
        sb.AppendLine(
            ".TokenType is TokenType.Int32 or TokenType.Int64 or TokenType.ArrayStart) break;"
        );
        if (nullableElement)
        {
            sb.Append(body);
            sb.Append("if (");
            sb.Append(readerVar);
            sb.AppendLine(".TokenType == TokenType.Null)");
            sb.Append(body);
            sb.AppendLine("{");
            sb.Append(cold);
            sb.Append(addTarget);
            sb.AppendLine(".Add(null!);");
            sb.Append(cold);
            sb.AppendLine("continue;");
            sb.Append(body);
            sb.AppendLine("}");
        }
        sb.Append(body);
        sb.Append("if (!");
        sb.Append(readerVar);
        sb.AppendLine(".TryGetInt64(out var __lev))");
        sb.Append(cold);
        sb.Append("throw new System.FormatException($\"Expected an integer element at offset {");
        sb.Append(readerVar);
        sb.AppendLine(".BytesConsumed}\");");
        sb.Append(body);
        sb.Append(addTarget);
        sb.AppendLine(".Add(checked((int)__lev));");
        sb.Append(indent);
        sb.AppendLine("}");
    }

    /// <summary>
    /// The precondition of every shape in this file, and the reason <paramref name="readerType"/> is a
    /// parameter at all (spec §3.4.2 C-1): the local's name <b>and</b> its concrete type's name identify the
    /// reader, the five formats do not even agree on the variable (JSON/MsgPack/INI generate <c>reader</c>,
    /// TOML generates <c>r</c>, YAML has both) and the type differs per format.
    /// <para>
    /// <b>Failing here is deliberate.</b> A wrong pair is a defect in the <i>generator</i>, not in a payload,
    /// and it is unreachable from user code: it would otherwise surface as a compile error in every consumer
    /// build, hundreds of generated lines away from the emit that caused it. Naming the pair at the emit
    /// point costs one comparison and turns that hunt into a stack trace — and the type name is what makes
    /// the message identify <i>which</i> generator's wiring is wrong.
    /// </para>
    /// </summary>
    static void RequireReader(string readerVar, string readerType)
    {
        if (string.IsNullOrEmpty(readerVar) || string.IsNullOrEmpty(readerType))
        {
            throw new ArgumentException(
                "A fused emit needs the reader local's name and its concrete type's name "
                    + $"(spec §3.4.2 C-1); got variable='{readerVar}', type='{readerType}'."
            );
        }
    }
}
