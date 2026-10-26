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
    /// PICOSERDE001 (⛔/⏸/⚠), PICOSERDE003 (⛔/⏸) and PICOSERDE004 (⛔/⏸) — three verdicts
    /// of each capability kind, one ⚠. Pinned on purpose: a NEW template has to be a
    /// visible edit here, and a template that silently loses its marker (the drift this
    /// file exists for) has to fail.
    /// </summary>
    private const int ImpossibleTemplateCount = 3;

    private const int DeferredTemplateCount = 3;

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
                "the three ⛔ templates (PICOSERDE001/003/004) must each reference "
                    + "FormatCapabilities.ImpossibleMarker; a template that builds its glyph "
                    + "another way is the drift this guard exists for"
            );
        await Assert
            .That(references.GetValueOrDefault(DeferredName))
            .IsEqualTo(DeferredTemplateCount)
            .Because("the three ⏸ templates must each reference FormatCapabilities.DeferredMarker");
        await Assert
            .That(references.GetValueOrDefault(MemberErrorName))
            .IsEqualTo(MemberErrorTemplateCount)
            .Because("the one ⚠ template keeps its OWN constant: ⚠ is not a capability verdict");
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

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
