using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The verdict glyphs have ONE definition each, and a test can see a second one.
/// <para>
/// Review W5-2 found exactly this shape: ⛔ and ⏸ were defined BOTH in
/// <c>FormatCapabilities</c> (the capability model) and AGAIN in <c>GenInfrastructure</c>
/// (the implementation file), with <c>MarkerFor</c> — the one member that was supposed to
/// be the seam between them — called by nobody. Two copies of a literal compile, emit the
/// same bytes, and break no behavioural test; the day someone edits one copy (a different
/// glyph, a different width, a stray space) the two sites disagree in silence. That is the
/// duplicate-source class R1 exists to kill, so it gets a guard instead of a comment.
/// </para>
/// <para>
/// Three glyphs, two homes, and the split is a rule, not a preference:
/// ⛔ <c>FormatCapabilities.ImpossibleMarker</c> and ⏸
/// <c>FormatCapabilities.DeferredMarker</c> are CAPABILITY verdicts ("the format cannot
/// express this" vs "the format can and we have not implemented it" — spec C1/C4, D-4 ②),
/// so they live with the model every generator already asks. ⚠
/// <c>GenInfrastructure.MemberErrorMarker</c> is NOT a capability verdict (no format can
/// serialize a member it cannot read), so it stays in the implementation file.
/// </para>
/// <para>
/// The scan is SYNTACTIC and reads string literals only: the prose in these very docs
/// mentions ⛔/⏸ dozens of times, and a comment must stay free to explain a glyph. A
/// literal is what a second definition would be.
/// </para>
/// </summary>
public class VerdictMarkerGuardTests
{
    /// <summary>⛔ the FORMAT cannot express the shape (terminal).</summary>
    private const string ImpossibleGlyph = "\u26D4";

    /// <summary>⏸ the format CAN express the shape; this generator has not (our debt).</summary>
    private const string DeferredGlyph = "\u23F8";

    /// <summary>⚠ the MEMBER is unreadable in every format (not a capability verdict).</summary>
    private const string MemberErrorGlyph = "\u26A0";

    private const string ImpossibleName = "ImpossibleMarker";
    private const string DeferredName = "DeferredMarker";
    private const string MemberErrorName = "MemberErrorMarker";

    /// <summary>The capability model: the ONE place the two CAPABILITY glyphs are spelled.</summary>
    private const string ModelFile = "FormatCapabilities.cs";

    /// <summary>The implementation file: allowed to spell ⚠, never ⛔/⏸.</summary>
    private const string InfrastructureFile = "GenInfrastructure.cs";

    /// <summary>
    /// How many <c>messageFormat</c> templates carry each capability glyph today:
    /// PICOSERDE001 (⛔/⏸/⚠), PICOSERDE003 (⛔/⏸), PICOSERDE004 (⛔/⏸) and, since R2 Task 8,
    /// PICOSERDE005 (⛔/⏸) — four verdicts of each capability kind, one ⚠. Pinned on
    /// purpose: a NEW template has to be a visible edit here, and a template that silently
    /// loses its marker (the drift this file exists for) has to fail.
    /// <para>
    /// PICOSERDE005 is why D-10 existed: the anonymous-type drop was the fourth surface and
    /// the only one with NO marker, so the counts here (3) were the reason it slipped — this
    /// guard could only see templates that already carried a glyph. 3 → 4 is the D-10 fix
    /// becoming visible to the guard.
    /// <para>
    /// 4 → 5 is N-1: PICOSERDE001 needed a SECOND ⛔ template, for the drop that is terminal for
    /// the TYPE instead of for the format (abstract / static / no accessible parameterless
    /// constructor — <c>DropVerdict.TypeUnconstructible</c>). Same id, same glyph, different
    /// sentence: one of them must not borrow the other's blame. The count moving here is the
    /// visibility the guard was built to give a new template.
    /// </para>
    /// <para>
    /// 5 → 6 is the attribute pipeline's own TypeUnconstructible drop (PICOSERDE007,
    /// <c>GenInfrastructure.UnconstructibleTypeWarning</c>): a type discovered by an attribute with
    /// no primary constructor and no public parameterless one is reported and skipped instead of
    /// being emitted as <c>new T()</c> + init-only assignments (CS7036 / CS8852). Same ⛔, because
    /// the verdict is the same and no other format helps either; a new id, because the SUBJECT is
    /// the type, not a member.
    /// </para>
    /// <para>
    /// 6 → 7 is PICOSERDE008 (an INI key carrying a structural character): the key is a
    /// compile-time constant that cannot be written and read back, so the member is dropped at
    /// generation time and the build says so instead of emitting a document whose key nobody
    /// declared.
    /// </para>
    /// </summary>
    private const int ImpossibleTemplateCount = 7;

    /// <summary>
    /// 4 → 5 is PICOSERDE006 (the member-kind gap): a member whose TypeKind has no arm in the
    /// member emit switches is OUR missing emitter, not a limit of the format — the same ⏸ the
    /// other four templates carry, with the <c>reopen:</c> tail AGENTS.md asks for. The count moves
    /// here because that is what this guard is for: a new template must be a visible edit, never a
    /// silent fifth.
    /// </summary>
    private const int DeferredTemplateCount = 5;

    private const int MemberErrorTemplateCount = 1;

    // ── 1. no second copy of the glyph ─────────────────────────────────────────────

    /// <summary>
    /// ⛔ and ⏸ may be spelled as string literals in <c>FormatCapabilities.cs</c> and
    /// NOWHERE ELSE under <c>shared/Gen</c> — every other file references the constants.
    /// This is the test that goes red when the local copies come back.
    /// </summary>
    [Test]
    public async Task Capability_glyphs_are_spelled_only_in_the_capability_model()
    {
        var offenders = new List<string>();
        var definitions = new List<string>();

        foreach (var file in GenFiles())
        {
            var name = Path.GetFileName(file);
            foreach (var (line, glyph) in GlyphLiterals(file, ImpossibleGlyph, DeferredGlyph))
            {
                var site = $"{name}:{line}: literal {glyph}";
                if (name == ModelFile)
                    definitions.Add(site);
                else
                    offenders.Add(site);
            }
        }

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                "a ⛔/⏸ string literal outside FormatCapabilities.cs is a SECOND definition of "
                    + "a capability verdict. Reference FormatCapabilities.ImpossibleMarker / "
                    + "FormatCapabilities.DeferredMarker instead: two copies compile, emit the "
                    + "same bytes and fail no behavioural test, so the drift would be silent. "
                    + "Offenders: "
                    + string.Join(" | ", offenders)
            );

        // The carve-out is not a hole: the model must actually hold both glyphs, otherwise
        // "no literal outside the model" would be satisfied by spelling them nowhere.
        await Assert
            .That(definitions.Count)
            .IsEqualTo(2)
            .Because(
                "FormatCapabilities.cs must spell ⛔ and ⏸ exactly once each (the two "
                    + "constants); found: "
                    + string.Join(" | ", definitions)
            );
    }

    // ── 2. one declaration each, in the right file ─────────────────────────────────

    /// <summary>
    /// Each marker NAME is declared exactly once under <c>shared/Gen</c>, in the file its
    /// verdict belongs to. Renaming the class or the file does not dodge this: a second
    /// <c>const string ImpossibleMarker</c> in another class is still a second declaration.
    /// </summary>
    [Test]
    public async Task Each_verdict_marker_is_declared_once_in_the_file_its_verdict_belongs_to()
    {
        var expected = new (string Name, string File)[]
        {
            (ImpossibleName, ModelFile),
            (DeferredName, ModelFile),
            (MemberErrorName, InfrastructureFile),
        };

        var declarations = GenFiles().SelectMany(ConstStringDeclarations).ToArray();

        foreach (var (name, file) in expected)
        {
            var sites = declarations.Where(d => d.Name == name).ToArray();

            await Assert
                .That(sites.Length)
                .IsEqualTo(1)
                .Because(
                    $"{name} must have exactly ONE declaration in shared/Gen; found: "
                        + string.Join(" | ", sites.Select(s => $"{s.File}:{s.Line}"))
                );

            await Assert
                .That(sites[0].File)
                .IsEqualTo(file)
                .Because(
                    $"{name} declares a verdict, so its home is decided by WHAT the verdict "
                        + "says: ⛔/⏸ are capability facts and live in the model, ⚠ is not and "
                        + "lives in the implementation file"
                );
        }

        // Pin the glyph itself, so "one declaration" cannot be satisfied by a copy whose
        // value quietly changed.
        await Assert.That(FormatCapabilities.ImpossibleMarker).IsEqualTo(ImpossibleGlyph);
        await Assert.That(FormatCapabilities.DeferredMarker).IsEqualTo(DeferredGlyph);
    }

    // ── 3. the templates actually use the constants ───────────────────────────────

    /// <summary>
    /// Every <c>messageFormat</c> that carries a verdict references the constant, and there
    /// are as many references as there are templates. A template that spells its glyph
    /// inline would be caught by guard 1; a template that lost its marker entirely, or a
    /// constant that stopped being used, is caught here.
    /// </summary>
    [Test]
    public async Task Every_verdict_template_references_the_constant_instead_of_a_literal()
    {
        var references = MarkerReferences(
            Path.Combine(GenDir(), InfrastructureFile),
            ImpossibleName,
            DeferredName,
            MemberErrorName
        );

        await Assert
            .That(references.GetValueOrDefault(ImpossibleName))
            .IsEqualTo(ImpossibleTemplateCount)
            .Because(
                "the six ⛔ templates (PICOSERDE001 x2 /003/004/005/006) must each reference "
                    + "FormatCapabilities.ImpossibleMarker; a template that builds its glyph "
                    + "another way is the drift this guard exists for"
            );
        await Assert
            .That(references.GetValueOrDefault(DeferredName))
            .IsEqualTo(DeferredTemplateCount)
            .Because("the five ⏸ templates must each reference FormatCapabilities.DeferredMarker");
        await Assert
            .That(references.GetValueOrDefault(MemberErrorName))
            .IsEqualTo(MemberErrorTemplateCount)
            .Because("the one ⚠ template keeps its OWN constant: ⚠ is not a capability verdict");
    }

    // ── 4. every ⏸ template carries its reopen pointer ─────────────────────────────

    /// <summary>
    /// AGENTS.md rule 5: when the format CAN express a shape and this framework cannot, the gap is
    /// <b>our debt</b>, and the debt must be <b>registered</b> — a <c>reopen:</c> pointer in the
    /// message, so the reader is told where the condition is written down instead of being left with
    /// "somebody has not implemented it yet".
    /// <para>
    /// <b>Why this needs its own guard.</b> Ablation on <c>0ae36ca</c>: deleting the <c>reopen:</c>
    /// tail from PICOSERDE004 turns a test red, but deleting it from <b>PICOSERDE001, PICOSERDE003
    /// and PICOSERDE006</b> left the whole suite green — four of the five ⏸ templates were never
    /// fenced, and the one that was (004) was fenced by a behavioural test that happens to read its
    /// message, not by the rule. A rule only one of five instances is held to is not a rule: the next
    /// ⏸ template would be written without a pointer and nothing would say so.
    /// </para>
    /// <para>
    /// The scan is syntactic and mirrors guard 3: it walks every <c>messageFormat:</c> argument of
    /// every diagnostic creation under <c>shared/Gen</c>, keeps the templates whose expression
    /// <b>starts</b> with <see cref="FormatCapabilities.DeferredMarker"/> (the left-most operand of
    /// the concatenation — the glyph itself), and requires the template's prose to contain the
    /// literal <c>reopen:</c>. Reading the string literals rather than the whole expression text is
    /// deliberate: a <c>reopen:</c> surviving only in a doc comment is prose, not a pointer.
    /// </para>
    /// <para>
    /// The family is pinned by count AND by id, so this guard cannot pass by seeing nothing: a
    /// template that lost its ⏸ reference fails guard 3's count, a sixth ⏸ template has to be a
    /// visible edit here, and a ⏸ whose prose was trimmed fails the per-template check below.
    /// </para>
    /// </summary>
    [Test]
    public async Task Every_deferred_template_carries_a_reopen_pointer()
    {
        var deferred = GenFiles()
            .SelectMany(MessageFormats)
            .Where(t => StartsWithDeferredMarker(t.Message))
            .ToArray();

        await Assert
            .That(deferred.Length)
            .IsEqualTo(DeferredTemplateCount)
            .Because(
                "the scan must see the WHOLE ⏸ family (every messageFormat whose expression starts "
                    + "with FormatCapabilities.DeferredMarker), otherwise \"no template lacks a "
                    + "reopen: pointer\" is satisfied by finding none. Found: "
                    + string.Join(" | ", deferred.Select(t => $"{t.Id} ({t.File}:{t.Line})"))
            );

        // Pinned by id, because the id is the public contract: PICOSERDE001's member-drop sibling,
        // 003 (recursion), 004 (a shape dropped in the ⏸ wording), 005 (anonymous collections) and
        // 006 (the member-kind emitter gap). A sixth would be a visible edit, never a silent one.
        await Assert
            .That(
                string.Join(
                    ",",
                    deferred
                        .Select(t => t.Id)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                )
            )
            .IsEqualTo("PICOSERDE001,PICOSERDE003,PICOSERDE004,PICOSERDE005,PICOSERDE006")
            .Because("the ⏸ family, by id");

        var missing = deferred
            .Where(t => !MessageText(t.Message).Contains("reopen:", StringComparison.Ordinal))
            .Select(t => $"{t.Id} ({t.File}:{t.Line})")
            .ToArray();

        await Assert
            .That(missing.Length)
            .IsEqualTo(0)
            .Because(
                "every ⏸ message must carry a `reopen:` pointer (AGENTS.md rule 5: a debt has to be "
                    + "REGISTERED, not merely admitted). A ⏸ that says \"not implemented yet\" and "
                    + "stops there tells the reader the gap is reopenable but not where its "
                    + "condition is written down — the dead end the pointer exists to prevent. "
                    + "Templates without one: "
                    + string.Join(" | ", missing)
            );
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private static string GenDir() => Path.Combine(TestRepo.Root(), "shared", "Gen");

    /// <summary>Every <c>.cs</c> file under <c>shared/Gen</c>, model and tables included.</summary>
    private static IEnumerable<string> GenFiles() =>
        Directory
            .EnumerateFiles(GenDir(), "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// String literals that contain one of <paramref name="glyphs"/>, read from the SYNTAX so
    /// a glyph in a comment is prose (not a definition) and <c>"\u26D4"</c> in source is the
    /// same glyph as a pasted ⛔ (<see cref="SyntaxToken.ValueText"/> resolves both).
    /// </summary>
    private static IEnumerable<(int Line, string Glyph)> GlyphLiterals(
        string file,
        params string[] glyphs
    )
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var text = literal.Token.ValueText;
            foreach (var glyph in glyphs)
                if (text.Contains(glyph, StringComparison.Ordinal))
                    yield return (LineOf(literal), glyph);
        }
    }

    /// <summary>Every <c>const string</c> declared in a file, as (file, line, variable name).</summary>
    private static IEnumerable<(string File, int Line, string Name)> ConstStringDeclarations(
        string file
    )
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;
            if (field.Declaration.Type is not PredefinedTypeSyntax predefined)
                continue;
            if (!predefined.Keyword.IsKind(SyntaxKind.StringKeyword))
                continue;

            foreach (var variable in field.Declaration.Variables)
                yield return (Path.GetFileName(file), LineOf(variable), variable.Identifier.Text);
        }
    }

    /// <summary>
    /// How often each marker name is READ in a file. Reads are <c>IdentifierNameSyntax</c>
    /// nodes, which covers both spellings the templates use: the qualified
    /// <c>FormatCapabilities.ImpossibleMarker</c> (a member access whose NAME is the
    /// identifier) and the local <c>MemberErrorMarker</c>. The declaration's own name is a
    /// declarator TOKEN, not an identifier node, so a file that declares and never uses
    /// cannot pass this guard - and counting identifiers never double-counts a qualified
    /// read, which iterating the member accesses would.
    /// </summary>
    private static Dictionary<string, int> MarkerReferences(string file, params string[] names)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var referenced = identifier.Identifier.Text;
            if (!names.Contains(referenced))
                continue;

            counts[referenced] = counts.GetValueOrDefault(referenced) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Every <c>messageFormat:</c> argument under <c>shared/Gen</c>, as (id, file, line, expression).
    /// The <c>id:</c> argument is read too — it is the public contract, and naming a template by it
    /// makes a failure say WHICH verdict lost its pointer. A creation with no literal <c>id:</c>
    /// still yields a row (<c>&lt;no id&gt;</c>): the ⏸ rule is about the message, not about the id.
    /// </summary>
    private static IEnumerable<(
        string Id,
        string File,
        int Line,
        ExpressionSyntax Message
    )> MessageFormats(string file)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        var name = Path.GetFileName(file);

        // BaseObjectCreationExpressionSyntax covers BOTH spellings the descriptors use: the explicit
        // `new DiagnosticDescriptor(...)` and the target-typed `new(...)` (an
        // ImplicitObjectCreationExpressionSyntax, which an OfType<ObjectCreationExpressionSyntax>
        // scan silently misses — the count assertion below is what caught that).
        foreach (
            var creation in root
                .DescendantNodes()
                .OfType<BaseObjectCreationExpressionSyntax>()
        )
        {
            if (creation.ArgumentList is not { } list)
                continue;

            var message = list.Arguments.FirstOrDefault(a =>
                a.NameColon?.Name.Identifier.Text == "messageFormat"
            );
            if (message is null)
                continue;

            var id = list.Arguments
                .FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "id")
                ?.Expression;
            yield return (
                id is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression)
                    ? literal.Token.ValueText
                    : "<no id>",
                name,
                LineOf(message),
                message.Expression
            );
        }
    }

    /// <summary>
    /// True when the template expression's LEFT-MOST operand reads the ⏸ constant — the glyph is the
    /// first thing the reader sees, which is what "the template carries the ⏸ verdict" means. The
    /// concatenation is walked down its left spine, so a wrapping parenthesis does not dodge the scan.
    /// </summary>
    private static bool StartsWithDeferredMarker(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                    expression = binary.Left;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case MemberAccessExpressionSyntax access:
                    return access.Name.Identifier.Text == DeferredName;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The template's PROSE: every string literal inside the expression, concatenated in source
    /// order. Not <c>ToString()</c>, because that carries trivia (comments included) — a
    /// <c>reopen:</c> in a doc comment would then read as a pointer in the message.
    /// </summary>
    private static string MessageText(ExpressionSyntax expression) =>
        string.Concat(
            expression
                .DescendantNodesAndSelf()
                .OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(literal => literal.Token.ValueText)
        );

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
